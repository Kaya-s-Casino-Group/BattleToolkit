using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public sealed class HoldemEncounterSettings
    {
        public string Version { get; }
        public long PlayerChips { get; }
        public long OpponentChips { get; }
        public long SmallBlind { get; }
        public long BigBlind { get; }
        public bool WinnersOnly { get; }
        public long FoldWinPoints { get; }
        public HoldemEncounterSettings(string version = "holdem-run.2", long playerChips = 1000, long opponentChips = 1000,
            long smallBlind = 10, long bigBlind = 20, bool winnersOnly = true, long foldWinPoints = 20)
        {
            Version = Guard.Text(version, nameof(version));
            if (playerChips <= 0 || opponentChips <= 0 || smallBlind < 0 || bigBlind <= 0 || smallBlind > bigBlind || foldWinPoints < 0)
                throw new ArgumentException("Invalid Holdem encounter policy.");
            PlayerChips = playerChips; OpponentChips = opponentChips; SmallBlind = smallBlind; BigBlind = bigBlind;
            WinnersOnly = winnersOnly; FoldWinPoints = foldWinPoints;
        }
    }
    /// <summary>Freeze the scored checkpoint before the run changes growth or shops edit the deck.</summary>
    [DataContract]
    public sealed class HoldemEncounterState
    {
        [DataMember(Order = 1, IsRequired = true)] public MatchState Match { get; private set; }
        [DataMember(Order = 2)] public ScoreResult LastScore { get; private set; }
        [DataMember(Name = "GrowJokers", Order = 3, IsRequired = true)] public bool GrowRelics { get; private set; }
        public HoldemEncounterState(MatchState match, ScoreResult lastScore = null, bool growRelics = false)
        { Match = match ?? throw new ArgumentNullException(nameof(match)); LastScore = lastScore; GrowRelics = growRelics; }
    }
    public sealed class HoldemRunAdapter : IRunEncounter<HoldemEncounterState, MatchCommand, MatchEvent>
    {
        public static readonly ParticipantId OpponentId = new ParticipantId("run-opponent");
        private readonly AbilityRuntime runtime;
        private readonly HoldemEncounterSettings settings;
        public string Kind => "holdem";
        public string RulesVersion => settings.Version + "/" + AbilityHoldem.RulesetVersion;
        public int SchemaVersion => MatchSnapshotCodec.SchemaVersion;
        public int MinimumDeckSize => 12;
        public HoldemRunAdapter(AbilityRuntime runtime, HoldemEncounterSettings settings = null)
        { this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime)); this.settings = settings ?? new HoldemEncounterSettings(); }
        private MatchDefinition Definition(RunEncounterContext c) => AbilityHoldem.Definition(c.Content.CardsAndAbilities.Version, 2, settings.SmallBlind, settings.BigBlind);
        /// <summary>Shared encounter description plus the concrete table rules; controllers remain host-owned.</summary>
        public EncounterSetup<MatchDefinition> Describe(RunEncounterContext c)
        {
            if (c.PlayerId == OpponentId) throw new ArgumentException("Player identity conflicts with opponent.");
            var definition = new EncounterDefinition(c.Node.Id, c.Content.Definition.Version, Kind, RulesVersion,
                new EncounterObjectiveDefinition(EncounterObjectiveKind.ReachScore, c.Stage.TargetScore), c.Stage.RoundLimit, new[] {
                    new EncounterParticipantDefinition("player", "player", new ResourceState(new Chips(settings.PlayerChips), c.Stage.Energy)),
                    new EncounterParticipantDefinition("opponent", "opponent", new ResourceState(new Chips(settings.OpponentChips), c.Stage.Energy))
                });
            return new EncounterSetup<MatchDefinition>(new EncounterContext(c.EncounterId, definition, new[] {
                    new EncounterParticipantBinding("player", c.PlayerId), new EncounterParticipantBinding("opponent", OpponentId)
                }, new EncounterRunBinding(c.RunId, c.Floor.Id, c.Node.Id)), Definition(c));
        }
        private HoldemMatchEngine Engine(RunEncounterContext c) => new HoldemMatchEngine(c.Content.CardsAndAbilities, abilityRuntime: runtime,
            deckEntries: c.Deck.Select(card => new DeckEntry(card.Id, card.DefinitionId)));
        public RunEncounterTransition<HoldemEncounterState, MatchEvent> Create(RunEncounterContext c)
        {
            if (c.PlayerId == OpponentId) throw new ArgumentException("Player identity conflicts with opponent.");
            var engine = Engine(c);
            var state = engine.Create(new MatchId(c.EncounterId), Definition(c), new TableState(new[] { new SeatId("player"), new SeatId("opponent") }), new[] {
                new ParticipantState(c.PlayerId, new SeatId("player"), ParticipantStatus.Active, new ResourceState(new Chips(settings.PlayerChips), c.Stage.Energy)),
                new ParticipantState(OpponentId, new SeatId("opponent"), ParticipantStatus.Active, new ResourceState(new Chips(settings.OpponentChips), c.Stage.Energy))
            }, c.Random, RunAbilityLoadout.Install(c));
            var result = engine.ExecuteSystem(state, new StartHandCommand(c.EncounterId + "/start", state.Revision));
            return new RunEncounterTransition<HoldemEncounterState, MatchEvent>(new HoldemEncounterState(result.State), result.Events, result.Accepted ? null : result.Error.ToString());
        }
        public RunEncounterTransition<HoldemEncounterState, MatchEvent> Execute(RunEncounterContext c, HoldemEncounterState state, MatchCommand command, ParticipantId actor, bool isSystem)
        {
            var engine = Engine(c); TransitionResult result;
            if (isSystem && command is SystemMatchCommand) result = engine.ExecuteSystem(state.Match, (SystemMatchCommand)command);
            else if (!isSystem && command is BettingCommand) result = engine.Execute(state.Match, (BettingCommand)command, actor);
            else if (!isSystem && command is AbilityCommand) result = engine.Execute(state.Match, (AbilityCommand)command, actor);
            else return new RunEncounterTransition<HoldemEncounterState, MatchEvent>(state, error: "InvalidCommandRoute");
            if (!result.Accepted) return new RunEncounterTransition<HoldemEncounterState, MatchEvent>(state, error: result.Error.ToString());
            var next = new HoldemEncounterState(result.State, state.LastScore, state.GrowRelics);
            if (Checkpoint(next.Match) && next.LastScore?.Id != next.Match.CurrentHand.Settlement.SettlementId) next = Score(c, next.Match);
            return new RunEncounterTransition<HoldemEncounterState, MatchEvent>(next, result.Events);
        }
        private static bool Checkpoint(MatchState m) => m.CurrentHand?.Stage == HandStage.Settled && m.AbilityWindow == null && m.ActiveDecision == null;
        private HoldemEncounterState Score(RunEncounterContext c, MatchState match)
        {
            var settlement = match.CurrentHand.Settlement;
            bool won = settlement.Awards.Any(a => a.ParticipantId == c.PlayerId);
            var evaluation = settlement.Evaluations.FirstOrDefault(e => e.ParticipantId == c.PlayerId);
            bool scored = evaluation != null && (!settings.WinnersOnly || won);
            ScoreResult score;
            if (scored)
            {
                var cards = evaluation.SelectedCards.Select(id => {
                    var card = match.CurrentHand.Cards.First(x => x.Id == id); var owned = c.Deck.First(x => x.Id == card.OriginId);
                    return new ScoringCard(owned.Id, evaluation.Faces.First(f => f.CardId == id).Face,
                        owned.BonusPoints, owned.BonusMultiplier, retriggers: owned.Retriggers, effects: card.Effects);
                });
                score = ScoreEngine.Evaluate(settlement.SettlementId, c.Content.Definition.Scoring, evaluation.Category,
                    c.HandLevels.First(l => l.Category == evaluation.Category).Level, cards,
                    c.Relics.Select(j => new ScoringRelic(j.Id, j.Level, j.Growth, c.Content.Relic(j.DefinitionId).ScoreEffects)));
            }
            else score = ScoreEngine.Fixed(settlement.SettlementId, won && evaluation == null ? settings.FoldWinPoints : 0);
            return new HoldemEncounterState(match, score, scored);
        }
        public RunEncounterProgress ReadProgress(RunEncounterContext c, HoldemEncounterState state)
        {
            var m = state.Match; bool checkpoint = Checkpoint(m);
            return new RunEncounterProgress(m.Random, checkpoint,
                exhausted: checkpoint && (m.Status == MatchStatus.Completed || m.Participants.Single(p => p.Id == c.PlayerId).Resources.Chips == Chips.Zero),
                score: checkpoint ? state.LastScore : null, growRelics: checkpoint && state.GrowRelics,
                relics: checkpoint ? RunAbilityLoadout.Export(m.Abilities) : null);
        }
        public HoldemEncounterState SyncBuild(RunEncounterContext c, HoldemEncounterState state)
        {
            var m = state.Match;
            return new HoldemEncounterState(new MatchState(m.Id, m.Revision, m.Definition, m.Status, m.Table, m.Participants,
                RunAbilityLoadout.Sync(c, m.Abilities), m.Statuses, m.CurrentHand, m.ActiveDecision, m.Resolution, m.Random, m.AbilityWindow, m.Knowledge),
                state.LastScore, state.GrowRelics);
        }
        public LegalBettingActions GetLegalActions(RunEncounterContext context, HoldemEncounterState state, ParticipantId player) => Engine(context).GetLegalActions(state.Match, player);
        public IReadOnlyList<MatchEvent> FilterEvents(HoldemEncounterState state, IEnumerable<MatchEvent> events, ParticipantId viewer)
        {
            if (!state.Match.Participants.Any(p => p.Id == viewer)) throw new ArgumentException("Unknown viewer.");
            return Guard.View(events.Where(e => e.Audience == EventAudience.Public || e.Recipient == viewer).ToArray());
        }
        public void Validate(RunEncounterContext c, HoldemEncounterState state)
        {
            var m = state.Match; StateValidator.Validate(m, c.Content.CardsAndAbilities);
            var d = Definition(c);
            if (m.Id.Value != c.EncounterId || m.Definition.RulesetId != d.RulesetId || m.Definition.RulesetVersion != d.RulesetVersion ||
                m.Definition.Mode != MatchMode.Solo || m.Definition.MaxSeats != 2 || m.Definition.SmallBlind != d.SmallBlind || m.Definition.BigBlind != d.BigBlind ||
                m.Participants.Count != 2 || !m.Participants.Any(p => p.Id == c.PlayerId) || !m.Participants.Any(p => p.Id == OpponentId))
                throw new StateValidationException("InvalidHoldemEncounter", "Encounter.Definition");
            if (state.GrowRelics && state.LastScore == null || Checkpoint(m) && state.LastScore?.Id != m.CurrentHand.Settlement.SettlementId)
                throw new StateValidationException("InvalidHoldemCheckpoint", "Encounter.LastScore");
            if (state.LastScore != null && !state.LastScore.Id.StartsWith(c.EncounterId + "/", StringComparison.Ordinal))
                throw new StateValidationException("InvalidHoldemCheckpoint", "Encounter.LastScore");
            if (c.Phase == RunPhase.Encounter)
            {
                if (m.CurrentHand != null && (m.CurrentHand.Cards.Count != c.Deck.Count || m.CurrentHand.Cards.Any(card => !c.Deck.Any(entry => entry.Id == card.OriginId && entry.DefinitionId == card.DefinitionId))))
                    throw new StateValidationException("InvalidHoldemDeck", "Encounter.Deck");
                RunAbilityLoadout.Validate(c, m.Abilities);
            }
        }
    }
}
