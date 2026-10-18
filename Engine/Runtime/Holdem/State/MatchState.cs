using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public enum MatchStatus { Ready = 1, Running, Completed, Aborted }
    public enum HandStage { Preparing = 1, Betting, Showdown, Settled }

    [DataContract]
    public sealed class HandState
    {
        [DataMember(Order = 1, IsRequired = true)] public HandId Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public HandStage Stage { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public SeatId DealerSeatId { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public ParticipantId SmallBlindId { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public ParticipantId BigBlindId { get; private set; }
        [DataMember(Name = "Participants", Order = 6, IsRequired = true)] private HandParticipantState[] participants;
        [DataMember(Name = "Cards", Order = 7, IsRequired = true)] private CardInstance[] cards;
        [DataMember(Name = "Zones", Order = 8, IsRequired = true)] private CardZone[] zones;
        [DataMember(Order = 9)] public BettingRoundState Betting { get; private set; }
        [DataMember(Order = 10)] public PotSettlement Settlement { get; private set; }
        public IReadOnlyList<HandParticipantState> Participants => Guard.View(participants);
        public IReadOnlyList<CardInstance> Cards => Guard.View(cards);
        public IReadOnlyList<CardZone> Zones => Guard.View(zones);
        public HandState(HandId id, HandStage stage, SeatId dealerSeatId, ParticipantId smallBlindId, ParticipantId bigBlindId,
            IEnumerable<HandParticipantState> participants, IEnumerable<CardInstance> cards, IEnumerable<CardZone> zones,
            BettingRoundState betting = null, PotSettlement settlement = null)
        {
            Id = id; Stage = stage; DealerSeatId = dealerSeatId; SmallBlindId = smallBlindId; BigBlindId = bigBlindId;
            this.participants = Guard.Copy(participants, nameof(participants)); this.cards = Guard.Copy(cards, nameof(cards));
            this.zones = Guard.Copy(zones, nameof(zones)); Betting = betting; Settlement = settlement;
        }
    }

    /// <summary>Immutable authoritative data. Never send this object to a PvP client.</summary>
    [DataContract]
    public sealed class MatchState
    {
        [DataMember(Order = 1, IsRequired = true)] public MatchId Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public long Revision { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public MatchDefinition Definition { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public MatchStatus Status { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public TableState Table { get; private set; }
        [DataMember(Name = "Participants", Order = 6, IsRequired = true)] private ParticipantState[] participants;
        [DataMember(Name = "Abilities", Order = 7, IsRequired = true)] private AbilityInstance[] abilities;
        [DataMember(Name = "Statuses", Order = 8, IsRequired = true)] private StatusInstance[] statuses;
        [DataMember(Order = 9)] public HandState CurrentHand { get; private set; }
        [DataMember(Order = 10)] public DecisionState ActiveDecision { get; private set; }
        [DataMember(Order = 11, IsRequired = true)] public ResolutionState Resolution { get; private set; }
        [DataMember(Order = 12, IsRequired = true)] public RandomState Random { get; private set; }
        [DataMember(Order = 13)] public AbilityWindowState AbilityWindow { get; private set; }
        [DataMember(Name = "Knowledge", Order = 14, IsRequired = true)] private KnowledgeGrant[] knowledge;
        public IReadOnlyList<ParticipantState> Participants => Guard.View(participants);
        public IReadOnlyList<AbilityInstance> Abilities => Guard.View(abilities);
        public IReadOnlyList<StatusInstance> Statuses => Guard.View(statuses);
        public IReadOnlyList<KnowledgeGrant> Knowledge => Guard.View(knowledge);
        public MatchState(MatchId id, long revision, MatchDefinition definition, MatchStatus status, TableState table,
            IEnumerable<ParticipantState> participants, IEnumerable<AbilityInstance> abilities, IEnumerable<StatusInstance> statuses,
            HandState currentHand, DecisionState activeDecision, ResolutionState resolution, RandomState random,
            AbilityWindowState abilityWindow = null, IEnumerable<KnowledgeGrant> knowledge = null)
        {
            if (!id.IsValid || revision < 0 || !Enum.IsDefined(typeof(MatchStatus), status)) throw new ArgumentException("Invalid match header.");
            Id = id; Revision = revision; Definition = definition ?? throw new ArgumentNullException(nameof(definition)); Status = status;
            Table = table ?? throw new ArgumentNullException(nameof(table)); this.participants = Guard.Copy(participants, nameof(participants));
            this.abilities = Guard.Copy(abilities, nameof(abilities)); this.statuses = Guard.Copy(statuses, nameof(statuses));
            CurrentHand = currentHand; ActiveDecision = activeDecision;
            Resolution = resolution ?? throw new ArgumentNullException(nameof(resolution)); Random = random ?? throw new ArgumentNullException(nameof(random));
            AbilityWindow = abilityWindow; this.knowledge = Guard.Copy(knowledge ?? new KnowledgeGrant[0], nameof(knowledge));
        }
    }
}
