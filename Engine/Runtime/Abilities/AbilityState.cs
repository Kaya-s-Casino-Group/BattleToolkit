using System;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public enum EntityKind { Match = 1, Participant, Card, Ability }

    /// <summary>Tagged reference. Factories prevent mismatching identity and entity kind.</summary>
    [DataContract]
    public sealed class EntityRef
    {
        [DataMember(Order = 1, IsRequired = true)] public EntityKind Kind { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public string Id { get; private set; }
        private EntityRef(EntityKind kind, string id) { Kind = kind; Id = Guard.Text(id, nameof(id)); }
        public static EntityRef Match(MatchId id) => new EntityRef(EntityKind.Match, id.Value);
        public static EntityRef Participant(ParticipantId id) => new EntityRef(EntityKind.Participant, id.Value);
        public static EntityRef Card(CardInstanceId id) => new EntityRef(EntityKind.Card, id.Value);
        public static EntityRef Ability(AbilityInstanceId id) => new EntityRef(EntityKind.Ability, id.Value);
    }

    [DataContract]
    public sealed class AbilityInstance
    {
        [DataMember(Order = 1, IsRequired = true)] public AbilityInstanceId Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public AbilityDefinitionId DefinitionId { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public ParticipantId OwnerId { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public EntityRef Source { get; private set; }
        [DataMember(Order = 5)] public int? RemainingCharges { get; private set; }
        [DataMember(Order = 6, IsRequired = true)] public int UsesThisRound { get; private set; }
        [DataMember(Order = 7, IsRequired = true)] public int CooldownRounds { get; private set; }
        [DataMember(Order = 8, IsRequired = true)] public AbilityMemory Memory { get; private set; }
        [DataMember(Order = 9, IsRequired = true)] public int Order { get; private set; }
        public AbilityInstance(AbilityInstanceId id, AbilityDefinitionId definitionId, ParticipantId ownerId,
            EntityRef source, int? remainingCharges = null, int usesThisRound = 0, int cooldownRounds = 0, AbilityMemory memory = null, int order = 0)
        {
            if (!id.IsValid || !definitionId.IsValid || !ownerId.IsValid || remainingCharges < 0 || usesThisRound < 0 || cooldownRounds < 0)
                throw new ArgumentException("Invalid ability instance.");
            Id = id; DefinitionId = definitionId; OwnerId = ownerId; Source = source ?? throw new ArgumentNullException(nameof(source));
            RemainingCharges = remainingCharges; UsesThisRound = usesThisRound; CooldownRounds = cooldownRounds;
            Memory = memory ?? AbilityMemory.Empty;
            Order = order;
        }
    }

    public enum DurationKind { UntilRemoved = 1, UntilRoundEnd, UntilPhaseEnd, TriggerCount }

    /// <summary>Mode-neutral expiry. The mode closes rounds/phases using its own stable keys.</summary>
    [DataContract]
    public sealed class StatusDuration
    {
        [DataMember(Order = 1, IsRequired = true)] public DurationKind Kind { get; private set; }
        [DataMember(Order = 2)] public string ScopeId { get; private set; }
        [DataMember(Order = 3)] public string PhaseKey { get; private set; }
        [DataMember(Order = 4)] public int? RemainingTriggers { get; private set; }
        private StatusDuration(DurationKind kind, string scope, string phase, int? triggers)
        { Kind = kind; ScopeId = scope; PhaseKey = phase; RemainingTriggers = triggers; }
        public static StatusDuration UntilRemoved() => new StatusDuration(DurationKind.UntilRemoved, null, null, null);
        public static StatusDuration UntilRoundEnd(string scopeId) =>
            new StatusDuration(DurationKind.UntilRoundEnd, Guard.Text(scopeId, nameof(scopeId)), null, null);
        public static StatusDuration UntilPhaseEnd(string scopeId, string phaseKey) =>
            new StatusDuration(DurationKind.UntilPhaseEnd, Guard.Text(scopeId, nameof(scopeId)), Guard.Text(phaseKey, nameof(phaseKey)), null);
        public static StatusDuration ForTriggers(int count)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            return new StatusDuration(DurationKind.TriggerCount, null, null, count);
        }
    }

    [DataContract]
    public sealed class StatusInstance
    {
        [DataMember(Order = 1, IsRequired = true)] public StatusInstanceId Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public StatusDefinitionId DefinitionId { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public EntityRef Source { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public EntityRef Target { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public int Stacks { get; private set; }
        [DataMember(Order = 6, IsRequired = true)] public StatusDuration Duration { get; private set; }
        public StatusInstance(StatusInstanceId id, StatusDefinitionId definitionId, EntityRef source, EntityRef target, int stacks, StatusDuration duration)
        {
            if (!id.IsValid || !definitionId.IsValid || stacks <= 0) throw new ArgumentException("Invalid status.");
            Id = id; DefinitionId = definitionId; Source = source ?? throw new ArgumentNullException(nameof(source));
            Target = target ?? throw new ArgumentNullException(nameof(target)); Stacks = stacks;
            Duration = duration ?? throw new ArgumentNullException(nameof(duration));
        }
    }
}
