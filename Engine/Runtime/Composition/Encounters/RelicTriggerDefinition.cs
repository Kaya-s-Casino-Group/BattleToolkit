using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PokerEngine
{
    public enum RelicTriggerTiming { DamageSettled = 1, HealingSettled, BlockGained, UnitDied, StatusApplied, StatusBlocked }
    public enum RelicEventRelation { OwnerIsSource = 1, OwnerIsTarget, AllyIsSource, AllyIsTarget, OpponentIsSource, OpponentIsTarget, Any }
    public enum RelicTriggerOccurrence { PerEvent = 1, PerAction, PerBoundary }
    public enum RelicFactAmount { DamageToHealth = 1, BlockAbsorbed, HealthRestored, Overheal, BlockGained }
    public enum RelicTriggerConditionKind { FactAtLeast = 1, OwnerHealthAtMost, OwnerHasStatus, EventStatus, HandCategory, SubmittedCardsAtLeast, DiscardedCardsAtLeast }

    /// <summary>AND-composed predicates. Unavailable facts never satisfy a condition.</summary>
    public sealed class RelicTriggerConditionSpec
    {
        public RelicTriggerConditionKind Kind { get; }
        public RelicFactAmount? Fact { get; }
        public long Threshold { get; }
        public decimal HealthRatio { get; }
        public StatusDefinitionId? StatusId { get; }
        public HandCategory? Category { get; }
        private RelicTriggerConditionSpec(RelicTriggerConditionKind kind, RelicFactAmount? fact = null, long threshold = 0,
            decimal healthRatio = 0, StatusDefinitionId? status = null, HandCategory? category = null)
        { Kind = kind; Fact = fact; Threshold = threshold; HealthRatio = healthRatio; StatusId = status; Category = category; }
        public static RelicTriggerConditionSpec FactAtLeast(RelicFactAmount fact, long amount)
        {
            if (!Enum.IsDefined(typeof(RelicFactAmount), fact) || amount < 0) throw new ArgumentException("Invalid fact threshold.");
            return new RelicTriggerConditionSpec(RelicTriggerConditionKind.FactAtLeast, fact, amount);
        }
        public static RelicTriggerConditionSpec OwnerHealthAtMost(decimal ratio)
        {
            if (ratio < 0 || ratio > 1) throw new ArgumentOutOfRangeException(nameof(ratio));
            return new RelicTriggerConditionSpec(RelicTriggerConditionKind.OwnerHealthAtMost, healthRatio: ratio);
        }
        public static RelicTriggerConditionSpec OwnerHasStatus(StatusDefinitionId id) => Status(RelicTriggerConditionKind.OwnerHasStatus, id);
        public static RelicTriggerConditionSpec EventStatus(StatusDefinitionId id) => Status(RelicTriggerConditionKind.EventStatus, id);
        private static RelicTriggerConditionSpec Status(RelicTriggerConditionKind kind, StatusDefinitionId id)
        {
            if (!id.IsValid) throw new ArgumentException("A status condition needs a valid definition.");
            return new RelicTriggerConditionSpec(kind, status: id);
        }
        public static RelicTriggerConditionSpec HandIs(HandCategory category)
        {
            if (!Enum.IsDefined(typeof(HandCategory), category)) throw new ArgumentException("Unknown hand category.");
            return new RelicTriggerConditionSpec(RelicTriggerConditionKind.HandCategory, category: category);
        }
        public static RelicTriggerConditionSpec SubmittedCardsAtLeast(int count) => CardCount(RelicTriggerConditionKind.SubmittedCardsAtLeast, count, 5);
        public static RelicTriggerConditionSpec DiscardedCardsAtLeast(int count) => CardCount(RelicTriggerConditionKind.DiscardedCardsAtLeast, count, 52);
        private static RelicTriggerConditionSpec CardCount(RelicTriggerConditionKind kind, int count, int max)
        {
            if (count < 1 || count > max) throw new ArgumentOutOfRangeException(nameof(count));
            return new RelicTriggerConditionSpec(kind, threshold: count);
        }
        internal void Write(BinaryWriter writer)
        {
            writer.Write((int)Kind); writer.Write(Fact.HasValue); if (Fact.HasValue) writer.Write((int)Fact.Value);
            writer.Write(Threshold); writer.Write(HealthRatio);
            writer.Write(StatusId.HasValue); if (StatusId.HasValue) writer.Write(StatusId.Value.Value);
            writer.Write(Category.HasValue); if (Category.HasValue) writer.Write((int)Category.Value);
        }
    }

    /// <summary>Shared by every trigger of one relic instance. Null limits are unbounded; charges are not refilled implicitly.</summary>
    public sealed class RelicTriggerBudgetSpec
    {
        public int? MaxUsesPerAction { get; }
        public int? MaxUsesPerRound { get; }
        public int ChargeCost { get; }
        public int CooldownRounds { get; }
        public int? MaxUsesPerBoundary { get; }
        public RelicTriggerBudgetSpec(int? maxUsesPerAction = null, int? maxUsesPerRound = null, int chargeCost = 1, int cooldownRounds = 0,
            int? maxUsesPerBoundary = null)
        {
            if (maxUsesPerAction < 1 || maxUsesPerRound < 1 || maxUsesPerBoundary < 1 || chargeCost < 0 || cooldownRounds < 0 || cooldownRounds == int.MaxValue)
                throw new ArgumentException("Invalid shared relic trigger budget.");
            MaxUsesPerAction = maxUsesPerAction; MaxUsesPerRound = maxUsesPerRound; ChargeCost = chargeCost; CooldownRounds = cooldownRounds;
            MaxUsesPerBoundary = maxUsesPerBoundary;
        }
        internal void Write(BinaryWriter writer)
        {
            writer.Write(MaxUsesPerAction.HasValue); if (MaxUsesPerAction.HasValue) writer.Write(MaxUsesPerAction.Value);
            writer.Write(MaxUsesPerRound.HasValue); if (MaxUsesPerRound.HasValue) writer.Write(MaxUsesPerRound.Value);
            writer.Write(ChargeCost); writer.Write(CooldownRounds);
        }
    }

    public sealed class RelicTriggeredEffectSpec
    {
        public BattleEffectSpec Effect { get; }
        public CombatTargetSpec Target { get; }
        public RelicTriggeredEffectSpec(BattleEffectSpec effect, CombatTargetSpec target)
        {
            Effect = effect ?? throw new ArgumentNullException(nameof(effect)); Target = target ?? throw new ArgumentNullException(nameof(target));
            var status = effect.Status;
            if (status?.Duration != null && status.Duration.Kind != DurationKind.UntilRemoved && status.Duration.Kind != DurationKind.TriggerCount ||
                status?.Lifetime?.Kind == StatusLifetimeKind.ThroughNextPhase &&
                status.Lifetime.PhaseKey != BattleStatusTiming.PlayerPhase && status.Lifetime.PhaseKey != BattleStatusTiming.EnemyPhase)
                throw new ArgumentException("Relic status effects require a relative lifetime.");
        }
        internal void Write(BinaryWriter writer)
        { RelicHandEffectModifierSpec.WriteEffect(writer, Effect); writer.Write((int)Target.Group); writer.Write((int)Target.Selection); }
    }

    public sealed class RelicTriggerSpec
    {
        public string Id { get; }
        public RelicTriggerTiming Timing { get; }
        public RelicEventRelation Relation { get; }
        public RelicTriggerOccurrence Occurrence { get; }
        public IReadOnlyList<RelicTriggerConditionSpec> Conditions { get; }
        public IReadOnlyList<RelicTriggeredEffectSpec> Effects { get; }
        public IReadOnlyList<CombatEffectOrigin> Origins { get; }
        public int Priority { get; }
        public bool CanTriggerReactions { get; }
        public CombatReactionSourcePolicy SourcePolicy { get; }
        public RelicTriggerSpec(string id, RelicTriggerTiming timing, RelicEventRelation relation, IEnumerable<RelicTriggeredEffectSpec> effects,
            IEnumerable<RelicTriggerConditionSpec> conditions = null, RelicTriggerOccurrence occurrence = RelicTriggerOccurrence.PerEvent,
            IEnumerable<CombatEffectOrigin> origins = null, int priority = 0, bool canTriggerReactions = false,
            CombatReactionSourcePolicy sourcePolicy = CombatReactionSourcePolicy.RequireLiving)
        {
            Id = Guard.Text(id, nameof(id));
            var steps = Guard.Copy(effects, nameof(effects)); var predicates = Guard.Copy(conditions ?? Array.Empty<RelicTriggerConditionSpec>(), nameof(conditions));
            var allowed = Guard.Copy(origins ?? new[] { CombatEffectOrigin.Primary }, nameof(origins));
            if (!Enum.IsDefined(typeof(RelicTriggerTiming), timing) || !Enum.IsDefined(typeof(RelicEventRelation), relation) ||
                !Enum.IsDefined(typeof(RelicTriggerOccurrence), occurrence) || !Enum.IsDefined(typeof(CombatReactionSourcePolicy), sourcePolicy) ||
                steps.Length < 1 || steps.Length > 64 || predicates.Length > 16 || allowed.Length == 0 ||
                steps.Any(step => step.Target.Group == CombatTargetGroup.EventActor) ||
                allowed.Distinct().Count() != allowed.Length || allowed.Any(origin => !Enum.IsDefined(typeof(CombatEffectOrigin), origin)))
                throw new ArgumentException("Invalid relic fact trigger.");
            Timing = timing; Relation = relation; Occurrence = occurrence; Effects = Guard.View(steps); Conditions = Guard.View(predicates);
            Origins = Guard.View(allowed.OrderBy(origin => (int)origin).ToArray()); Priority = priority;
            CanTriggerReactions = canTriggerReactions; SourcePolicy = sourcePolicy;
        }
        internal void Write(BinaryWriter writer)
        {
            writer.Write(Id); writer.Write((int)Timing); writer.Write((int)Relation); writer.Write((int)Occurrence); writer.Write(Priority);
            writer.Write(CanTriggerReactions); writer.Write((int)SourcePolicy);
            writer.Write(Origins.Count); foreach (var origin in Origins) writer.Write((int)origin);
            writer.Write(Conditions.Count); foreach (var condition in Conditions) condition.Write(writer);
            writer.Write(Effects.Count); foreach (var effect in Effects) effect.Write(writer);
        }
    }
}
