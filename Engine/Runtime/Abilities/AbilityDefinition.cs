using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    [Flags] public enum AbilityModes { Solo = 1, Pvp = 2, All = Solo | Pvp }
    public enum MatchMode { Solo = 1, Pvp }
    public enum AbilityMechanism { Activated = 1, Triggered, RuleModifier, EffectReplacement, OutcomeModifier }
    public enum ConditionKind { MinimumEnergy = 1, UsesThisRoundBelow, CounterAtLeast, HasMark, OwnerIsEventActor, OwnerIsCurrentActor }

    /// <summary>Small AND-composed condition vocabulary; specialized conditions use IAbilityCondition.</summary>
    public sealed class AbilityCondition
    {
        public ConditionKind Kind { get; }
        public long Threshold { get; }
        public string MemoryKey { get; }
        public AbilityCondition(ConditionKind kind, long threshold = 0, string memoryKey = null)
        {
            if (!Enum.IsDefined(typeof(ConditionKind), kind) || threshold < 0) throw new ArgumentException("Invalid condition.");
            bool keyed = kind == ConditionKind.CounterAtLeast || kind == ConditionKind.HasMark;
            if (keyed != (memoryKey != null)) throw new ArgumentException("Invalid condition key.");
            if (keyed) Guard.Text(memoryKey, nameof(memoryKey));
            if ((kind == ConditionKind.HasMark || kind == ConditionKind.OwnerIsEventActor || kind == ConditionKind.OwnerIsCurrentActor) && threshold != 0)
                throw new ArgumentException("This condition does not take a threshold.");
            Kind = kind; Threshold = threshold; MemoryKey = memoryKey;
        }
    }

    public sealed class AbilityDefinition
    {
        private readonly string[] windows;
        private readonly AbilityCondition[] conditions;
        public AbilityDefinitionId Id { get; }
        public AbilityMechanism Mechanism { get; }
        public AbilityModes Modes { get; }
        public int Priority { get; }
        public string RequiredAction { get; }
        public IReadOnlyList<string> Windows => Guard.View(windows);
        public IReadOnlyList<AbilityCondition> Conditions => Guard.View(conditions);
        public AbilityDefinition(AbilityDefinitionId id, AbilityMechanism mechanism, AbilityModes modes,
            IEnumerable<string> windows, IEnumerable<AbilityCondition> conditions, int priority = 0, string requiredAction = null)
        {
            if (!id.IsValid || !Enum.IsDefined(typeof(AbilityMechanism), mechanism) || modes == 0 || (modes & ~AbilityModes.All) != 0)
                throw new ArgumentException("Invalid ability definition.");
            this.windows = Guard.Copy(windows, nameof(windows)); this.conditions = Guard.Copy(conditions, nameof(conditions));
            if (this.windows.Length == 0 || this.windows.Distinct().Count() != this.windows.Length || this.windows.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Unique valid ability windows are required.");
            if (requiredAction != null) Guard.Text(requiredAction, nameof(requiredAction));
            Id = id; Mechanism = mechanism; Modes = modes; Priority = priority; RequiredAction = requiredAction;
        }
    }

    /// <summary>Opened only by the authoritative match orchestrator, never by a client request.</summary>
    [DataContract]
    public sealed class AbilityWindowState
    {
        [DataMember(Order = 1, IsRequired = true)] public string Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public string Timing { get; private set; }
        [DataMember(Order = 3)] public string ScopeId { get; private set; }
        [DataMember(Order = 4)] public ParticipantId? EventActorId { get; private set; }
        [DataMember(Order = 5)] public string Action { get; private set; }
        public AbilityWindowState(string id, string timing, string scopeId = null, ParticipantId? eventActorId = null, string action = null)
        {
            Id = Guard.Text(id, nameof(id)); Timing = Guard.Text(timing, nameof(timing));
            ScopeId = scopeId == null ? null : Guard.Text(scopeId, nameof(scopeId)); EventActorId = eventActorId;
            Action = action == null ? null : Guard.Text(action, nameof(action));
            if (eventActorId.HasValue && !eventActorId.Value.IsValid) throw new ArgumentException("Invalid event actor.");
        }
    }
}
