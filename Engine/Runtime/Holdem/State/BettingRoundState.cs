using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public enum Street { Preflop = 1, Flop, Turn, River }

    [DataContract]
    public sealed class BettingPlayerState
    {
        [DataMember(Order = 1, IsRequired = true)] public ParticipantId ParticipantId { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public bool HasActed { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public Chips BetToAtLastAction { get; private set; }
        [DataMember(Order = 4)] public Chips? ReopenAtBetTo { get; private set; }
        public BettingPlayerState(ParticipantId participantId, bool hasActed, Chips betToAtLastAction, Chips? reopenAtBetTo)
        {
            if (!participantId.IsValid) throw new ArgumentException("Invalid participant.");
            ParticipantId = participantId; HasActed = hasActed; BetToAtLastAction = betToAtLastAction; ReopenAtBetTo = reopenAtBetTo;
        }
    }

    /// <summary>PendingActors is authoritative turn order; its first member acts next.</summary>
    [DataContract]
    public sealed class BettingRoundState
    {
        [DataMember(Order = 1, IsRequired = true)] public Street Street { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public Chips CurrentBetTo { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public Chips LastFullRaiseSize { get; private set; }
        [DataMember(Name = "PendingActors", Order = 4, IsRequired = true)] private ParticipantId[] pendingActors;
        [DataMember(Name = "Players", Order = 5, IsRequired = true)] private BettingPlayerState[] players;
        public IReadOnlyList<ParticipantId> PendingActors => Guard.View(pendingActors);
        public IReadOnlyList<BettingPlayerState> Players => Guard.View(players);
        public ParticipantId? CurrentActor => pendingActors.Length == 0 ? (ParticipantId?)null : pendingActors[0];
        public BettingRoundState(Street street, Chips currentBetTo, Chips lastFullRaiseSize,
            IEnumerable<ParticipantId> pendingActors, IEnumerable<BettingPlayerState> players)
        {
            if (!Enum.IsDefined(typeof(Street), street) || lastFullRaiseSize.Value <= 0) throw new ArgumentException("Invalid betting round.");
            Street = street; CurrentBetTo = currentBetTo; LastFullRaiseSize = lastFullRaiseSize;
            this.pendingActors = Guard.Copy(pendingActors, nameof(pendingActors)); this.players = Guard.Copy(players, nameof(players));
        }
    }
}
