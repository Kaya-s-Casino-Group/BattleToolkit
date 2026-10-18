using System;
using System.Collections.Generic;
using System.Linq;
namespace PokerEngine
{
    public sealed class DrawEncounterSettings
    {
        public string Version { get; }
        public int HandSize { get; }
        public int Discards { get; }
        public int MaxSelection { get; }
        public DrawEncounterSettings(string version = "draw-run.1", int handSize = 8, int discards = 3, int maxSelection = 5)
        {
            Version = Guard.Text(version, nameof(version));
            if (handSize < 1 || handSize > 64 || discards < 0 || discards > 1000 || maxSelection < 1 || maxSelection > 5 || maxSelection > handSize)
                throw new ArgumentException("Invalid draw encounter policy.");
            HandSize = handSize; Discards = discards; MaxSelection = maxSelection;
        }
    }
    public sealed class DrawRunAdapter : IRunEncounter<DrawState, DrawCommand, DrawEvent>
    {
        private readonly AbilityRuntime runtime;
        private readonly DrawEncounterSettings settings;
        public string Kind => "draw-score";
        public string RulesVersion => settings.Version;
        public int SchemaVersion => DrawSnapshotCodec.SchemaVersion;
        public int MinimumDeckSize => settings.HandSize;
        public DrawRunAdapter(AbilityRuntime runtime, DrawEncounterSettings settings = null)
        { this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime)); this.settings = settings ?? new DrawEncounterSettings(); }
        private DrawDefinition Definition(RunEncounterContext c) =>
            new DrawDefinition(RulesVersion, c.Stage.TargetScore, settings.HandSize, c.Stage.RoundLimit, settings.Discards, settings.MaxSelection,
                c.Content.Definition.Scoring, c.Relics.Select(j => new DrawRelic(new ScoringRelic(j.Id, j.Level, j.Growth, c.Content.Relic(j.DefinitionId).ScoreEffects))),
                c.HandLevels.Select(l => new DrawHandLevel(l.Category, l.Level)));
        public DrawEngine Engine(RunEncounterContext c) => new DrawEngine(c.Content.CardsAndAbilities, Definition(c), runtime);
        /// <summary>Shared encounter description plus the concrete rules used by this adapter.</summary>
        public EncounterSetup<DrawDefinition> Describe(RunEncounterContext c)
        {
            var definition = new EncounterDefinition(c.Node.Id, c.Content.Definition.Version, Kind, RulesVersion,
                new EncounterObjectiveDefinition(EncounterObjectiveKind.ReachScore, c.Stage.TargetScore), c.Stage.RoundLimit,
                new[] { new EncounterParticipantDefinition("player", "player", new ResourceState(Chips.Zero, c.Stage.Energy)) });
            return new EncounterSetup<DrawDefinition>(new EncounterContext(c.EncounterId, definition,
                new[] { new EncounterParticipantBinding("player", c.PlayerId) },
                new EncounterRunBinding(c.RunId, c.Floor.Id, c.Node.Id)), Definition(c));
        }
        public RunEncounterTransition<DrawState, DrawEvent> Create(RunEncounterContext c)
        {
            var cards = c.Deck.Select((card, i) => new DrawCard(new CardInstance(new CardInstanceId(c.EncounterId + "/card/" + i), card.DefinitionId, card.Id),
                card.BonusPoints, card.BonusMultiplier, retriggers: card.Retriggers));
            var state = Engine(c).Create(c.EncounterId, c.PlayerId, cards, c.Random, RunAbilityLoadout.Install(c), new ResourceState(Chips.Zero, c.Stage.Energy));
            return new RunEncounterTransition<DrawState, DrawEvent>(state);
        }
        public RunEncounterTransition<DrawState, DrawEvent> Execute(RunEncounterContext c, DrawState state, DrawCommand command, ParticipantId actor, bool isSystem)
        {
            DrawTransitionResult result; var engine = Engine(c);
            if (isSystem && command.Action == DrawAction.Timeout) result = engine.ResolveTimeout(state, command.ExpectedRevision, command.DecisionId);
            else if (isSystem && command.Action == DrawAction.Continue) result = engine.Execute(state, command, c.PlayerId);
            else if (!isSystem && command.Action != DrawAction.Timeout) result = engine.Execute(state, command, actor);
            else return new RunEncounterTransition<DrawState, DrawEvent>(state, error: "InvalidCommandRoute");
            return new RunEncounterTransition<DrawState, DrawEvent>(result.State, result.Events, result.Accepted ? null : result.Error.ToString());
        }
        public RunEncounterProgress ReadProgress(RunEncounterContext c, DrawState state)
        {
            bool checkpoint = state.Phase == DrawPhase.Scored || state.Phase == DrawPhase.Won || state.Phase == DrawPhase.Lost;
            var score = checkpoint ? state.Scores.LastOrDefault() : null;
            return new RunEncounterProgress(state.Random, checkpoint, checkpoint && state.Phase != DrawPhase.Scored,
                score, score != null, checkpoint ? RunAbilityLoadout.Export(state.World.Abilities) : null);
        }
        public DrawState SyncBuild(RunEncounterContext c, DrawState state) => Engine(c).SyncAbilities(state, RunAbilityLoadout.Sync(c, state.World.Abilities));
        public IReadOnlyList<DrawEvent> FilterEvents(DrawState state, IEnumerable<DrawEvent> events, ParticipantId viewer)
        {
            if (viewer != state.PlayerId) throw new ArgumentException("Unknown viewer.");
            return Guard.View(events.ToArray());
        }
        public void Validate(RunEncounterContext c, DrawState state)
        {
            Engine(c).Validate(state);
            if (state.Id != c.EncounterId || state.PlayerId != c.PlayerId) throw new StateValidationException("InvalidDrawEncounter", "Encounter.Identity");
            if (c.Phase == RunPhase.Encounter)
            {
                if (state.Cards.Count != c.Deck.Count || state.Cards.Any(card => !c.Deck.Any(entry => entry.Id == card.Card.OriginId && entry.DefinitionId == card.Card.DefinitionId &&
                    entry.BonusPoints == card.BonusPoints && entry.BonusMultiplier == card.BonusMultiplier && entry.Retriggers == card.Retriggers)))
                    throw new StateValidationException("InvalidDrawDeck", "Encounter.Deck");
                RunAbilityLoadout.Validate(c, state.World.Abilities);
            }
        }
    }
}
