using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PokerEngine
{
    [Flags] public enum StatusTags { None = 0, Control = 1 }
    public enum StatusLifetimeKind { UntilRemoved = 1, TriggerCount, ThroughNextPhase, ThroughCurrentRound }
    public enum StatusReapplyPolicy { KeepLonger = 1, IndependentInstances = 2 }

    /// <summary>Relative lifetime declaration. The mode binds phase and round lifetimes to StatusDuration.</summary>
    public sealed class StatusLifetimeSpec
    {
        public StatusLifetimeKind Kind { get; }
        public string PhaseKey { get; }
        public int? Count { get; }
        private StatusLifetimeSpec(StatusLifetimeKind kind, string phaseKey = null, int? count = null)
        { Kind = kind; PhaseKey = phaseKey; Count = count; }
        public static StatusLifetimeSpec UntilRemoved() => new StatusLifetimeSpec(StatusLifetimeKind.UntilRemoved);
        public static StatusLifetimeSpec ForTriggers(int count)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            return new StatusLifetimeSpec(StatusLifetimeKind.TriggerCount, count: count);
        }
        public static StatusLifetimeSpec ThroughNextPhase(string phaseKey) =>
            new StatusLifetimeSpec(StatusLifetimeKind.ThroughNextPhase, Guard.Text(phaseKey, nameof(phaseKey)));
        public static StatusLifetimeSpec ThroughCurrentRound() => new StatusLifetimeSpec(StatusLifetimeKind.ThroughCurrentRound);
    }

    public abstract class StatusBehaviorSpec { protected StatusBehaviorSpec() { } }
    public sealed class BlockActionWindowSpec : StatusBehaviorSpec { }
    public sealed class PreventStatusApplicationSpec : StatusBehaviorSpec
    {
        public StatusTags BlockedTags { get; }
        public PreventStatusApplicationSpec(StatusTags blockedTags)
        {
            if (blockedTags == StatusTags.None || (blockedTags & ~StatusTags.Control) != 0)
                throw new ArgumentException("Specify supported nonempty status tags.", nameof(blockedTags));
            BlockedTags = blockedTags;
        }
    }

    /// <summary>Immutable configurable behavior; no built-in meaning is assigned to any definition ID.</summary>
    public sealed class CombatStatusDefinition
    {
        public StatusDefinitionId Id { get; }
        public StatusTags Tags { get; }
        public IReadOnlyList<StatusBehaviorSpec> Behaviors { get; }
        public StatusLifetimeSpec DefaultLifetime { get; }
        public StatusReapplyPolicy ReapplyPolicy { get; }
        public CombatStatusDefinition(StatusDefinitionId id, StatusTags tags, IEnumerable<StatusBehaviorSpec> behaviors,
            StatusLifetimeSpec defaultLifetime, StatusReapplyPolicy reapplyPolicy = StatusReapplyPolicy.KeepLonger)
        {
            if (!id.IsValid || (tags & ~StatusTags.Control) != 0 || !Enum.IsDefined(typeof(StatusReapplyPolicy), reapplyPolicy))
                throw new ArgumentException("Invalid status definition.");
            var copy = Guard.Copy(behaviors, nameof(behaviors));
            if (copy.Any(item => item == null || !(item is BlockActionWindowSpec || item is PreventStatusApplicationSpec ||
                item is ReflectBlockedDamageSpec || item is LifeStealSpec || item is OverhealDamageSpec || item is PeriodicDamageSpec)) ||
                copy.Select(item => item.GetType()).Distinct().Count() != copy.Length)
                throw new ArgumentException("Status behaviors must have distinct supported types.", nameof(behaviors));
            Id = id; Tags = tags; Behaviors = Guard.View(copy);
            DefaultLifetime = defaultLifetime ?? throw new ArgumentNullException(nameof(defaultLifetime)); ReapplyPolicy = reapplyPolicy;
            ValidateTriggerBudget(DefaultLifetime.Kind == StatusLifetimeKind.TriggerCount);
        }
        internal void ValidateTriggerBudget(bool hasTriggerBudget)
        {
            if (hasTriggerBudget && Behaviors.Count > 1)
                throw new ArgumentException("A trigger budget cannot be shared by different consuming behaviors.");
        }
    }

    public sealed class CombatStatusCatalog
    {
        private readonly IReadOnlyDictionary<StatusDefinitionId, CombatStatusDefinition> definitions;
        public string Version { get; }
        public IReadOnlyDictionary<StatusDefinitionId, CombatStatusDefinition> Definitions => definitions;
        public CombatStatusCatalog(string version, IEnumerable<CombatStatusDefinition> definitions)
        {
            Version = Guard.Text(version, nameof(version));
            var copy = Guard.Copy(definitions, nameof(definitions));
            if (copy.Any(item => item == null) || copy.Select(item => item.Id).Distinct().Count() != copy.Length)
                throw new ArgumentException("Status definitions must be nonnull and unique.", nameof(definitions));
            this.definitions = new ReadOnlyDictionary<StatusDefinitionId, CombatStatusDefinition>(copy.ToDictionary(item => item.Id));
        }
        public CombatStatusDefinition Find(StatusDefinitionId id)
        {
            CombatStatusDefinition found; return definitions.TryGetValue(id, out found) ? found : null;
        }
        public CombatStatusDefinition Get(StatusDefinitionId id) => Find(id) ?? throw new ArgumentException("Unknown status definition.", nameof(id));
    }
}
