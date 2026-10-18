using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    [DataContract]
    public sealed class AbilityParticipant
    {
        [DataMember(Order = 1, IsRequired = true)] public ParticipantId Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public bool Active { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public ResourceState Resources { get; private set; }
        public AbilityParticipant(ParticipantId id, bool active, ResourceState resources)
        { if (!id.IsValid) throw new ArgumentException("Invalid participant."); Id = id; Active = active; Resources = resources ?? throw new ArgumentNullException(nameof(resources)); }
    }

    /// <summary>Authoritative mode-neutral projection. Participant order is the mode's explicit execution order.</summary>
    [DataContract]
    public sealed class AbilityWorld
    {
        [DataMember(Order = 1, IsRequired = true)] public string Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public long Revision { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public bool IsRunning { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public MatchMode Mode { get; private set; }
        [DataMember(Order = 5)] public string ScopeId { get; private set; }
        [DataMember(Order = 6)] public string PhaseKey { get; private set; }
        [DataMember(Order = 7)] public ParticipantId? CurrentActorId { get; private set; }
        [DataMember(Name = "Participants", Order = 8, IsRequired = true)] private AbilityParticipant[] participants;
        [DataMember(Name = "Cards", Order = 9, IsRequired = true)] private CardInstance[] cards;
        [DataMember(Name = "Zones", Order = 10, IsRequired = true)] private CardZone[] zones;
        [DataMember(Name = "Abilities", Order = 11, IsRequired = true)] private AbilityInstance[] abilities;
        [DataMember(Name = "Statuses", Order = 12, IsRequired = true)] private StatusInstance[] statuses;
        [DataMember(Order = 13)] public DecisionState ActiveDecision { get; private set; }
        [DataMember(Order = 14, IsRequired = true)] public ResolutionState Resolution { get; private set; }
        [DataMember(Order = 15)] public AbilityWindowState AbilityWindow { get; private set; }
        [DataMember(Name = "Knowledge", Order = 16, IsRequired = true)] private KnowledgeGrant[] knowledge;
        [DataMember(Order = 17, IsRequired = true)] public bool AllowChipChanges { get; private set; }
        [DataMember(Order = 18, IsRequired = true)] public bool AllowCardChanges { get; private set; }
        [DataMember(Order = 19, IsRequired = true)] public bool AllowKnowledgeProjection { get; private set; }
        public IReadOnlyList<AbilityParticipant> Participants => Guard.View(participants);
        public IReadOnlyList<CardInstance> Cards => Guard.View(cards);
        public IReadOnlyList<CardZone> Zones => Guard.View(zones);
        public IReadOnlyList<AbilityInstance> Abilities => Guard.View(abilities);
        public IReadOnlyList<StatusInstance> Statuses => Guard.View(statuses);
        public IReadOnlyList<KnowledgeGrant> Knowledge => Guard.View(knowledge);
        public AbilityWorld(string id, long revision, bool isRunning, MatchMode mode, string scopeId, string phaseKey,
            ParticipantId? currentActorId, IEnumerable<AbilityParticipant> participants, IEnumerable<CardInstance> cards,
            IEnumerable<CardZone> zones, IEnumerable<AbilityInstance> abilities, IEnumerable<StatusInstance> statuses,
            DecisionState activeDecision, ResolutionState resolution, AbilityWindowState abilityWindow,
            IEnumerable<KnowledgeGrant> knowledge, bool allowChipChanges = true, bool allowCardChanges = true, bool allowKnowledgeProjection = true)
        {
            Id = Guard.Text(id, nameof(id)); Revision = revision; IsRunning = isRunning; Mode = mode;
            ScopeId = scopeId; PhaseKey = phaseKey; CurrentActorId = currentActorId;
            this.participants = Guard.Copy(participants, nameof(participants)); this.cards = Guard.Copy(cards, nameof(cards));
            this.zones = Guard.Copy(zones, nameof(zones)); this.abilities = Guard.Copy(abilities, nameof(abilities));
            this.statuses = Guard.Copy(statuses, nameof(statuses)); this.knowledge = Guard.Copy(knowledge, nameof(knowledge));
            ActiveDecision = activeDecision; Resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
            AbilityWindow = abilityWindow; AllowChipChanges = allowChipChanges; AllowCardChanges = allowCardChanges; AllowKnowledgeProjection = allowKnowledgeProjection;
        }
    }

    internal sealed class AbilityWorldEdit
    {
        private readonly AbilityWorld source;
        public List<AbilityParticipant> Players;
        public List<AbilityInstance> Abilities;
        public List<StatusInstance> Statuses;
        public List<KnowledgeGrant> Knowledge;
        public List<CardZone> Zones;
        public DecisionState Decision;
        public ResolutionState Resolution;
        public AbilityWorldEdit(AbilityWorld source)
        {
            this.source = source; Players = source.Participants.ToList(); Abilities = source.Abilities.ToList();
            Statuses = source.Statuses.ToList(); Knowledge = source.Knowledge.ToList(); Zones = source.Zones.ToList();
            Decision = source.ActiveDecision; Resolution = source.Resolution;
        }
        public AbilityWorld Build() => new AbilityWorld(source.Id, source.Revision, source.IsRunning, source.Mode, source.ScopeId,
            source.PhaseKey, source.CurrentActorId, Players, source.Cards, Zones, Abilities, Statuses, Decision, Resolution,
            source.AbilityWindow, Knowledge, source.AllowChipChanges, source.AllowCardChanges, source.AllowKnowledgeProjection);
        public void Resource(ParticipantId target, ResourceKind resource, long delta)
        {
            int index = Players.FindIndex(candidate => candidate.Id == target);
            if (index < 0) throw new ArgumentException("Unknown resource target.");
            var player = Players[index]; var resources = player.Resources;
            if (resource == ResourceKind.Chips && !source.AllowChipChanges) throw new ArgumentException("This mode window locks chips.");
            var chips = resource == ResourceKind.Chips ? new Chips(checked(resources.Chips.Value + delta)) : resources.Chips;
            int energy = resource == ResourceKind.Energy ? checked(resources.Energy + checked((int)delta)) : resources.Energy;
            int? health = resources.Health;
            if (resource == ResourceKind.Health)
            { if (!health.HasValue) throw new ArgumentException("Target has no health resource."); health = checked(health.Value + checked((int)delta)); }
            Players[index] = new AbilityParticipant(player.Id, player.Active, new ResourceState(chips, energy, health));
        }
    }
}
