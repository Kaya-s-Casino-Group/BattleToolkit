using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public enum HandCategory { HighCard = 1, OnePair, TwoPair, ThreeOfAKind, Straight, Flush, FullHouse, FourOfAKind, StraightFlush, FiveOfAKind }

    [DataContract]
    public sealed class EvaluatedCardFace
    {
        [DataMember(Order = 1, IsRequired = true)] public CardInstanceId CardId { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public CardFace Face { get; private set; }
        public EvaluatedCardFace(CardInstanceId cardId, CardFace face) { CardId = cardId; Face = face ?? throw new ArgumentNullException(nameof(face)); }
    }

    [DataContract]
    public sealed class HandEvaluation
    {
        [DataMember(Order = 1, IsRequired = true)] public ParticipantId ParticipantId { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public HandCategory Category { get; private set; }
        [DataMember(Name = "TieBreakRanks", Order = 3, IsRequired = true)] private int[] tieBreakRanks;
        [DataMember(Name = "SelectedCards", Order = 4, IsRequired = true)] private CardInstanceId[] selectedCards;
        [DataMember(Name = "AppliedModifiers", Order = 5, IsRequired = true)] private StatusInstanceId[] appliedModifiers;
        [DataMember(Name = "Faces", Order = 6, IsRequired = true)] private EvaluatedCardFace[] faces;
        public IReadOnlyList<EvaluatedCardFace> Faces => Guard.View(faces);
        public IReadOnlyList<int> TieBreakRanks => Guard.View(tieBreakRanks);
        public IReadOnlyList<CardInstanceId> SelectedCards => Guard.View(selectedCards);
        public IReadOnlyList<StatusInstanceId> AppliedModifiers => Guard.View(appliedModifiers);
        public HandEvaluation(ParticipantId participantId, HandCategory category, IEnumerable<int> tieBreakRanks,
            IEnumerable<CardInstanceId> selectedCards, IEnumerable<StatusInstanceId> appliedModifiers, IEnumerable<EvaluatedCardFace> faces = null)
        {
            ParticipantId = participantId; Category = category;
            this.tieBreakRanks = Guard.Copy(tieBreakRanks, nameof(tieBreakRanks));
            this.selectedCards = Guard.Copy(selectedCards, nameof(selectedCards));
            this.appliedModifiers = Guard.Copy(appliedModifiers, nameof(appliedModifiers));
            this.faces = Guard.Copy(faces ?? new EvaluatedCardFace[0], nameof(faces));
        }
    }

}
