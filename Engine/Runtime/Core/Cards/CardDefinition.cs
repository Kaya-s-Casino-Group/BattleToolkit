using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public enum Suit { Clubs = 1, Diamonds = 2, Hearts = 3, Spades = 4 }
    public enum Rank { Two = 2, Three, Four, Five, Six, Seven, Eight, Nine, Ten, Jack, Queen, King, Ace }

    public sealed class CardDefinition
    {
        private readonly string[] tags;
        public CardDefinitionId Id { get; }
        public Rank Rank { get; }
        public Suit Suit { get; }
        public IReadOnlyList<string> Tags => Guard.View(tags);
        public CardDefinition(CardDefinitionId id, Rank rank, Suit suit, IEnumerable<string> tags)
        {
            if (!id.IsValid || !Enum.IsDefined(typeof(Rank), rank) || !Enum.IsDefined(typeof(Suit), suit))
                throw new ArgumentException("A valid standard card definition is required.");
            Id = id; Rank = rank; Suit = suit; this.tags = Guard.Copy(tags, nameof(tags));
        }
    }

}
