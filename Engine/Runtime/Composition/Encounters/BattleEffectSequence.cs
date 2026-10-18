using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Unbound status data. The authority creates a separate instance and attribution for each resolved target.</summary>
    public sealed class StatusApplicationSpec
    {
        public StatusDefinitionId DefinitionId { get; }
        public int Stacks { get; }
        public StatusDuration Duration { get; }
        /// <summary>Null Duration/Lifetime uses the registered definition's default lifetime.</summary>
        public StatusLifetimeSpec Lifetime { get; }
        public StatusApplicationSpec(StatusDefinitionId definitionId, int stacks = 1)
        {
            if (!definitionId.IsValid || stacks <= 0) throw new ArgumentException("Invalid status application specification.");
            DefinitionId = definitionId; Stacks = stacks;
        }
        public StatusApplicationSpec(StatusDefinitionId definitionId, int stacks, StatusDuration duration)
            : this(definitionId, stacks)
        {
            Duration = duration ?? throw new ArgumentNullException(nameof(duration));
        }
        private StatusApplicationSpec(StatusDefinitionId definitionId, StatusLifetimeSpec lifetime, int stacks) : this(definitionId, stacks)
        { Lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime)); }
        public static StatusApplicationSpec WithLifetime(StatusDefinitionId definitionId, StatusLifetimeSpec lifetime, int stacks = 1) =>
            new StatusApplicationSpec(definitionId, lifetime, stacks);
    }

    /// <summary>What an effect does, independent of source, target count, targeting and attack reports.</summary>
    public sealed class BattleEffectSpec
    {
        public CombatEffectKind Kind { get; }
        public long Amount { get; }
        public StatusApplicationSpec Status { get; }
        private BattleEffectSpec(CombatEffectKind kind, long amount = 0, StatusApplicationSpec status = null)
        {
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
            Kind = kind; Amount = amount; Status = status;
        }
        public static BattleEffectSpec Damage(long amount) => new BattleEffectSpec(CombatEffectKind.Damage, amount);
        public static BattleEffectSpec Heal(long amount) => new BattleEffectSpec(CombatEffectKind.Heal, amount);
        public static BattleEffectSpec GainBlock(long amount) => new BattleEffectSpec(CombatEffectKind.GainBlock, amount);
        public static BattleEffectSpec ApplyStatus(StatusApplicationSpec status) =>
            new BattleEffectSpec(CombatEffectKind.ApplyStatus, status: status ?? throw new ArgumentNullException(nameof(status)));
        public static BattleEffectSpec Eliminate() => new BattleEffectSpec(CombatEffectKind.Eliminate);
    }

    public sealed class BattleEffectStep
    {
        /// <summary>Stable semantic identity within the base sequence. Null only for legacy unnamed steps.</summary>
        public string Id { get; }
        public BattleEffectSpec Effect { get; }
        public EffectTargetSpec Target { get; }
        public BattleEffectStep(BattleEffectSpec effect, EffectTargetSpec target)
        { Effect = effect ?? throw new ArgumentNullException(nameof(effect)); Target = target ?? throw new ArgumentNullException(nameof(target)); }
        public BattleEffectStep(string id, BattleEffectSpec effect, EffectTargetSpec target) : this(effect, target)
        { Id = Guard.Text(id, nameof(id)); }
    }

    /// <summary>Ordered declarations before targeting. All steps bind against the same action-start snapshot.</summary>
    public sealed class BattleEffectSequence
    {
        public string RuleVersion { get; }
        public string Explanation { get; }
        public IReadOnlyList<BattleEffectStep> Steps { get; }
        public BattleEffectSequence(string ruleVersion, string explanation, IEnumerable<BattleEffectStep> steps)
        {
            RuleVersion = Guard.Text(ruleVersion, nameof(ruleVersion)); Explanation = Guard.Text(explanation, nameof(explanation));
            var copy = Guard.Copy(steps, nameof(steps));
            if (copy.Length == 0 || copy.Length > 64 || copy.Any(step => step == null)) throw new ArgumentException("Invalid effect sequence.");
            var ids = copy.Where(step => step.Id != null).Select(step => step.Id).ToArray();
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new ArgumentException("Effect step IDs must be unique within the sequence.");
            Steps = Guard.View(copy);
        }
    }
}
