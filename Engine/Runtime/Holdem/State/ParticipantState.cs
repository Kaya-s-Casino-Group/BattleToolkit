using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public enum ParticipantStatus { Active = 1, Eliminated, Left }

    [DataContract]
    public sealed class ParticipantState
    {
        [DataMember(Order = 1, IsRequired = true)] public ParticipantId Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public SeatId SeatId { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public ParticipantStatus Status { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public ResourceState Resources { get; private set; }
        public ParticipantState(ParticipantId id, SeatId seatId, ParticipantStatus status, ResourceState resources)
        {
            if (!id.IsValid || !seatId.IsValid || !Enum.IsDefined(typeof(ParticipantStatus), status)) throw new ArgumentException("Invalid participant.");
            Id = id; SeatId = seatId; Status = status; Resources = resources ?? throw new ArgumentNullException(nameof(resources));
        }
    }

    /// <summary>Clockwise seat order. Occupancy is stored exclusively by ParticipantState.SeatId.</summary>
    [DataContract]
    public sealed class TableState
    {
        [DataMember(Name = "Seats", Order = 1, IsRequired = true)] private SeatId[] seats;
        public IReadOnlyList<SeatId> Seats => Guard.View(seats);
        public TableState(IEnumerable<SeatId> seats) { this.seats = Guard.Copy(seats, nameof(seats)); }
    }

    public enum Participation { InHand = 1, Folded }

    [DataContract]
    public sealed class HandParticipantState
    {
        [DataMember(Order = 1, IsRequired = true)] public ParticipantId ParticipantId { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public Participation Participation { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public Chips HandCommitted { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public Chips StreetCommitted { get; private set; }
        public HandParticipantState(ParticipantId participantId, Participation participation, Chips handCommitted, Chips streetCommitted)
        {
            if (!participantId.IsValid || !Enum.IsDefined(typeof(Participation), participation) || streetCommitted > handCommitted)
                throw new ArgumentException("Invalid hand contribution.");
            ParticipantId = participantId; Participation = participation; HandCommitted = handCommitted; StreetCommitted = streetCommitted;
        }
    }
}
