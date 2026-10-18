using System;
using System.Runtime.Serialization;

namespace PokerEngine
{
    [DataContract]
    public sealed class CardFace
    {
        [DataMember(Order = 1, IsRequired = true)] public Rank Rank { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public Suit Suit { get; private set; }
        public CardFace(Rank rank, Suit suit)
        {
            if (!Enum.IsDefined(typeof(Rank), rank) || !Enum.IsDefined(typeof(Suit), suit)) throw new ArgumentException("Invalid face.");
            Rank = rank; Suit = suit;
        }
    }

}
