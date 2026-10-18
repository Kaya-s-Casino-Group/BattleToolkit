using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PokerEngine
{
    public enum RelicActionTiming { PlayCompleted = 1, DiscardCompleted }
    public enum RelicActionRelation { OwnerIsActor = 1, AllyIsActor, OpponentIsActor, Any }

    /// <summary>One subscription per completed operation, independent of card, hit and target counts.</summary>
    public sealed class RelicActionTriggerSpec
    {
        public string Id { get; }
        public RelicActionTiming Timing { get; }
        public RelicActionRelation Relation { get; }
        public IReadOnlyList<RelicTriggeredEffectSpec> Effects { get; }
        public IReadOnlyList<RelicTriggerConditionSpec> Conditions { get; }
        public int Priority { get; }
        public bool CanTriggerReactions { get; }
        public CombatReactionSourcePolicy SourcePolicy { get; }
        public RelicActionTriggerSpec(string id, RelicActionTiming timing, IEnumerable<RelicTriggeredEffectSpec> effects,
            RelicActionRelation relation = RelicActionRelation.OwnerIsActor, IEnumerable<RelicTriggerConditionSpec> conditions = null,
            int priority = 0, bool canTriggerReactions = false, CombatReactionSourcePolicy sourcePolicy = CombatReactionSourcePolicy.RequireLiving)
        {
            Id = Guard.Text(id, nameof(id));
            var steps = Guard.Copy(effects, nameof(effects));
            var predicates = Guard.Copy(conditions ?? Array.Empty<RelicTriggerConditionSpec>(), nameof(conditions));
            if (!Enum.IsDefined(typeof(RelicActionTiming), timing) || !Enum.IsDefined(typeof(RelicActionRelation), relation) ||
                !Enum.IsDefined(typeof(CombatReactionSourcePolicy), sourcePolicy) || steps.Length < 1 || steps.Length > 64 || predicates.Length > 16 ||
                steps.Any(step => step.Target.Group == CombatTargetGroup.EventSource || step.Target.Group == CombatTargetGroup.EventTarget) ||
                predicates.Any(item => !Supports(timing, item.Kind)))
                throw new ArgumentException("Invalid relic action trigger or unavailable completion facts.");
            Timing = timing; Relation = relation; Effects = Guard.View(steps); Conditions = Guard.View(predicates);
            Priority = priority; CanTriggerReactions = canTriggerReactions; SourcePolicy = sourcePolicy;
        }
        private static bool Supports(RelicActionTiming timing, RelicTriggerConditionKind kind) =>
            kind == RelicTriggerConditionKind.OwnerHealthAtMost || kind == RelicTriggerConditionKind.OwnerHasStatus ||
            (timing == RelicActionTiming.PlayCompleted ? kind == RelicTriggerConditionKind.HandCategory || kind == RelicTriggerConditionKind.SubmittedCardsAtLeast :
                kind == RelicTriggerConditionKind.DiscardedCardsAtLeast);

        internal bool Matches(CombatState combat, ParticipantId owner, ParticipantId actor, ScoredDuelActionKind action)
        {
            if (action != (Timing == RelicActionTiming.PlayCompleted ? ScoredDuelActionKind.ScoredHand : ScoredDuelActionKind.Discard)) return false;
            switch (Relation)
            {
                case RelicActionRelation.OwnerIsActor: return owner == actor;
                case RelicActionRelation.AllyIsActor: return combat.Get(owner).TeamId == combat.Get(actor).TeamId;
                case RelicActionRelation.OpponentIsActor: return combat.Get(owner).TeamId != combat.Get(actor).TeamId;
                default: return true;
            }
        }
        internal bool MatchesConditions(CombatState combat, ParticipantId owner, BattleActionCompletionContext context) =>
            Conditions.All(condition => condition.Kind == RelicTriggerConditionKind.HandCategory ? context.Hand?.Pattern.Category == condition.Category :
                condition.Kind == RelicTriggerConditionKind.SubmittedCardsAtLeast ? context.Hand != null && context.Hand.SubmittedCards.Count >= condition.Threshold :
                condition.Kind == RelicTriggerConditionKind.DiscardedCardsAtLeast ? context.DiscardedCards != null && context.DiscardedCards.Count >= condition.Threshold :
                BattleRelicTriggerResolver.OwnerCondition(condition, combat, owner));

        internal void Write(BinaryWriter writer)
        {
            writer.Write(Id); writer.Write((int)Timing); writer.Write((int)Relation); writer.Write(Priority);
            writer.Write(CanTriggerReactions); writer.Write((int)SourcePolicy);
            writer.Write(Conditions.Count); foreach (var condition in Conditions) condition.Write(writer);
            writer.Write(Effects.Count); foreach (var effect in Effects) effect.Write(writer);
        }
    }

    /// <summary>Authority-owned action snapshot. Null card facts mean unavailable, never a fabricated empty hand.</summary>
    public sealed class BattleActionCompletionContext
    {
        public CombatTimingSignal Signal { get; }
        public string ActionId => Signal.BoundaryId;
        public RelicActionTiming Timing { get; }
        public ParticipantId Actor { get; }
        public string RoundScope { get; }
        public ScoredDuelPhase Phase { get; }
        public HandAttackInfo Hand { get; }
        public IReadOnlyList<DiscardCardSnapshot> DiscardedCards { get; }
        public int? SelectedCardCount => Timing == RelicActionTiming.PlayCompleted ? Hand?.SubmittedCards.Count : DiscardedCards?.Count;
        public BattleActionCostResolution ActionCost { get; }
        public int EnergyBefore { get; }
        public int EnergyAfter { get; }
        internal BattleActionCompletionContext(TeamBattleState start, ParticipantId actor, ScoredDuelActionKind action,
            HandAttackInfo hand, IEnumerable<DiscardCardSnapshot> discardedCards, BattleActionCostResolution cost, int energyAfter)
        {
            if (action != ScoredDuelActionKind.ScoredHand && action != ScoredDuelActionKind.Discard)
                throw new ArgumentException("Only play and discard operations have completion facts.");
            Signal = BattleStatusTiming.ActionCompleted(start, actor, action); Actor = actor;
            Timing = action == ScoredDuelActionKind.ScoredHand ? RelicActionTiming.PlayCompleted : RelicActionTiming.DiscardCompleted;
            RoundScope = BattleStatusTiming.RoundScope(start); Phase = start.Phase;
            Hand = Timing == RelicActionTiming.PlayCompleted ? hand : null;
            DiscardedCards = Timing == RelicActionTiming.DiscardCompleted && discardedCards != null ?
                Guard.View(Guard.Copy(discardedCards, nameof(discardedCards))) : null;
            ActionCost = cost; EnergyBefore = start.Player(actor).Energy.Energy; EnergyAfter = energyAfter;
        }
    }

    /// <summary>The post-periodic completion dispatch, before phase maintenance and automatic dealing.</summary>
    public sealed class BattleActionCompletion
    {
        public BattleActionCompletionContext Context { get; }
        public CombatTimingEvent Clock { get; }
        internal BattleActionCompletion(BattleActionCompletionContext context, CombatTimingEvent clock) { Context = context; Clock = clock; }
    }
}
