using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Ordered observations of real rule execution. These are facts, never executable instructions.</summary>
    public enum BattleTimelineKind
    {
        BattleStarted, PhaseStarted, ActionStarted, ActionSkipped, HandEvaluated,
        ActionCostSettled, ResourcesChanged, EffectSettled, StatusChanged, RelicActivated,
        RuleTiming, CardsMoved, DeckRecycled, CardsDealt, ActionResolved,
        PhaseEnding, PhaseEnded, PhaseClosed, BattleEnded, EnemySummoned, CardsCreated, AdminEdited
    }

    /// <summary>
    /// Immutable checkpoint after one fact. State is the intermediate rule snapshot at this observation,
    /// not the enclosing command's final result. Its revisions retain their original authority meaning;
    /// use Sequence, not Revision, to order checkpoints. Do not submit intermediate snapshots as commands.
    /// </summary>
    public sealed class BattleTimelineEntry
    {
        public long Sequence { get; }
        public int StepIndex { get; }
        public BattleTimelineKind Kind { get; }
        public CardDuelAction Action { get; }
        public ParticipantId Actor { get; }
        public string ActionId { get; }
        /// <summary>The accepted action window was actually skipped by control. Known for every entry in this action.</summary>
        public bool IsActionSkipped { get; }
        public CardDuelState State { get; }
        public string PhaseKey { get; }
        public string Reason { get; }
        public IReadOnlyList<CardInstanceId> Selected { get; }
        public IReadOnlyList<ParticipantId> Targets { get; }
        public IReadOnlyList<CardInstanceId> Cards { get; }
        public string FromZone { get; }
        public string ToZone { get; }
        public ParticipantId CardOwner { get; }
        public CardDuelDealTrigger? DealTrigger { get; }
        public CardInsertionPosition? InsertionPosition { get; }
        public CombatRuleEvent RuleEvent { get; }
        public RelicTriggerActivation RelicActivation { get; }
        public BattleActionCostResolution ActionCost { get; }
        public ScoreResult Score { get; }
        public PokerHandClassification Pattern { get; }
        internal BattleTimelineEntry(long sequence, int stepIndex, BattleTimelineKind kind, CardDuelAction action,
            ParticipantId actor, string actionId, CardDuelState state, IEnumerable<CardInstanceId> selected,
            IEnumerable<ParticipantId> targets, string phaseKey = null, string reason = null,
            CombatRuleEvent ruleEvent = null, RelicTriggerActivation activation = null,
            BattleActionCostResolution cost = null, ScoreResult score = null, PokerHandClassification pattern = null,
            IEnumerable<CardInstanceId> cards = null, string from = null, string to = null,
            ParticipantId cardOwner = default(ParticipantId), CardDuelDealTrigger? dealTrigger = null, bool isActionSkipped = false,
            CardInsertionPosition? insertionPosition = null)
        {
            Sequence = sequence; StepIndex = stepIndex; Kind = kind; Action = action; Actor = actor; ActionId = actionId;
            State = state; PhaseKey = phaseKey; Reason = reason; RuleEvent = ruleEvent; RelicActivation = activation;
            IsActionSkipped = isActionSkipped;
            ActionCost = cost; Score = score; Pattern = pattern;
            Selected = Guard.View(Guard.Copy(selected ?? Array.Empty<CardInstanceId>(), nameof(selected)));
            Targets = Guard.View(Guard.Copy(targets ?? Array.Empty<ParticipantId>(), nameof(targets)));
            Cards = Guard.View(Guard.Copy(cards ?? Array.Empty<CardInstanceId>(), nameof(cards)));
            FromZone = from; ToZone = to; CardOwner = cardOwner; DealTrigger = dealTrigger;
            InsertionPosition = insertionPosition;
        }
        internal BattleTimelineEntry InBatch(long sequence, int stepIndex) => new BattleTimelineEntry(sequence, stepIndex,
            Kind, Action, Actor, ActionId, State, Selected, Targets, PhaseKey, Reason, RuleEvent, RelicActivation,
            ActionCost, Score, Pattern, Cards, FromZone, ToZone, CardOwner, DealTrigger, IsActionSkipped, InsertionPosition);
        internal BattleTimelineEntry WithState(CardDuelState state) => new BattleTimelineEntry(Sequence, StepIndex,
            Kind, Action, Actor, ActionId, state, Selected, Targets, PhaseKey, Reason, RuleEvent, RelicActivation,
            ActionCost, Score, Pattern, Cards, FromZone, ToZone, CardOwner, DealTrigger, IsActionSkipped, InsertionPosition);
        internal BattleTimelineEntry WithSkippedAction() => new BattleTimelineEntry(Sequence, StepIndex,
            Kind, Action, Actor, ActionId, State, Selected, Targets, PhaseKey, Reason, RuleEvent, RelicActivation,
            ActionCost, Score, Pattern, Cards, FromZone, ToZone, CardOwner, DealTrigger, true, InsertionPosition);
    }

    /// <summary>Per-execution observer. It has no engine callbacks, RNG, clocks, or mutation of authority state.</summary>
    internal sealed class BattleTimelineRecorder
    {
        private readonly List<BattleTimelineEntry> entries = new List<BattleTimelineEntry>();
        private readonly HashSet<string> recordedActivations = new HashSet<string>(StringComparer.Ordinal);
        private readonly CardDuelAction action;
        private readonly ParticipantId actor;
        private readonly CardInstanceId[] selected;
        private readonly ParticipantId[] targets;
        private readonly string actionId;
        private bool skipped;
        internal CardDuelState State { get; private set; }
        internal IReadOnlyList<BattleTimelineEntry> Entries => entries.AsReadOnly();
        internal BattleTimelineRecorder(CardDuelState state, CardDuelAction action, ParticipantId actor,
            IEnumerable<CardInstanceId> selected, IEnumerable<ParticipantId> targets)
        {
            State = state; this.action = action; this.actor = actor;
            this.selected = (selected ?? Array.Empty<CardInstanceId>()).ToArray();
            this.targets = (targets ?? Array.Empty<ParticipantId>()).ToArray();
            actionId = state.Table.MatchId + "/command/" + state.Revision + "/" + action;
        }
        internal void SetState(CardDuelState state) { State = state; }
        internal void SetTeam(TeamBattleState team) => State = new CardDuelState(State.Table, team, State.Random,
            State.Duel.Players.ToDictionary(p => p.Id, p => State.DiscardsFor(p.Id)), State.HasStarted, State.CopyFlows(), State.CopyGold());
        internal void SetCards(CardTableState table, RandomState random, IDictionary<ParticipantId, int> discards = null,
            bool? started = null) => State = new CardDuelState(table, State.Duel, random,
                discards ?? State.Duel.Players.ToDictionary(p => p.Id, p => State.DiscardsFor(p.Id)), started ?? State.HasStarted, State.CopyFlows(), State.CopyGold());
        internal void Add(BattleTimelineKind kind, string phase = null, string reason = null,
            CombatRuleEvent ruleEvent = null, RelicTriggerActivation activation = null,
            BattleActionCostResolution cost = null, ScoreResult score = null, PokerHandClassification pattern = null,
            IEnumerable<CardInstanceId> cards = null, string from = null, string to = null,
            ParticipantId cardOwner = default(ParticipantId), CardDuelDealTrigger? trigger = null, CardInsertionPosition? insertionPosition = null)
        {
            entries.Add(new BattleTimelineEntry(entries.Count, 0, kind, action, actor, actionId, State,
                selected, targets, phase, reason, ruleEvent, activation, cost, score, pattern,
                cards, from, to, cardOwner, trigger, skipped, insertionPosition));
        }
        internal static string Phase(TeamBattleState state) =>
            state.Phase == ScoredDuelPhase.AwaitingEnemyAction ? BattleStatusTiming.EnemyPhase : BattleStatusTiming.PlayerPhase;

        /// <summary>Replay explicit immutable deltas in their returned order; never infer lifecycle markers from snapshots.</summary>
        internal void ObserveCombat(IEnumerable<CombatRuleEvent> facts, IEnumerable<RelicTriggerActivation> activations = null)
        {
            var reports = (activations ?? Array.Empty<RelicTriggerActivation>()).ToArray();
            foreach (var fact in facts)
            {
                if (fact is CombatActionWindowSkippedEvent)
                {
                    skipped = true;
                    for (int i = 0; i < entries.Count; i++) entries[i] = entries[i].WithSkippedAction();
                }
                var source = State.Duel; var combat = source.Combat;
                var units = combat.Combatants.ToArray(); var statuses = combat.Statuses.ToList();
                if (fact is CombatEvent vitals)
                {
                    units = units.Select(unit => unit.Id == vitals.After.Id ? vitals.After : unit).ToArray();
                    // Revive clears the target's statuses atomically in CombatEngine, without separate removal facts.
                    if (vitals.Effect.Kind == CombatEffectKind.Revive)
                        statuses.RemoveAll(status => status.Target.Kind == EntityKind.Participant &&
                            status.Target.Id == vitals.Effect.Target.Value);
                }
                else if (fact is CombatStatusEvent applied) statuses.Add(applied.Status);
                else if (fact is CombatStatusChangedEvent changed)
                {
                    int index = statuses.FindIndex(status => status.Id == changed.StatusBefore.Id);
                    if (changed.StatusAfter == null) statuses.RemoveAt(index);
                    else statuses[index] = changed.StatusAfter;
                }
                combat = new CombatState(combat.Id, fact.Revision, units, statuses, combat.RulesVersion);
                SetTeam(new TeamBattleState(source.Rules, combat, source.Players, source.Enemies, source.Phase,
                    source.PendingEnemies, source.Revision, source.CompletedRounds, source.Scores, source.Actions));
                var kind = fact is CombatEvent ? BattleTimelineKind.EffectSettled :
                    fact is CombatActionWindowSkippedEvent ? BattleTimelineKind.ActionSkipped :
                    fact is CombatTimingEvent ? BattleTimelineKind.RuleTiming : BattleTimelineKind.StatusChanged;
                Add(kind, ruleEvent: fact);
                foreach (var report in reports.Where(report => report.ParentRevision == fact.Revision &&
                    report.ParentSequenceIndex == fact.SequenceIndex && recordedActivations.Add(report.ActivationId)))
                {
                    var team = State.Duel;
                    var players = team.Players.Select(player => player.Id != report.Owner ? player :
                        new BattlePlayerTurn(player.Id, player.Energy, player.HasEnded, player.Rules, player.DamageRules,
                            player.TargetRules, player.Relics.WithRuntime(player.Relics.Relics.Select(relic =>
                                relic.InstanceId == report.SourceInstanceId ? report.After : relic)), player.Limits)).ToArray();
                    SetTeam(new TeamBattleState(team.Rules, team.Combat, players, team.Enemies, team.Phase,
                        team.PendingEnemies, team.Revision, team.CompletedRounds, team.Scores, team.Actions));
                    Add(BattleTimelineKind.RelicActivated, activation: report);
                }
            }
        }
    }
}
