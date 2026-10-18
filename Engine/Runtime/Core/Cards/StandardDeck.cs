using System;
using System.Collections.Generic;
namespace PokerEngine
{
    public static class StandardDeck
    {
        public static CardDefinitionId CardId(Suit suit, Rank rank) => new CardDefinitionId("standard:" + (int)suit + ":" + (int)rank);
        public static IReadOnlyList<CardDefinition> Cards()
        {
            var cards = new List<CardDefinition>();
            foreach (Suit suit in Enum.GetValues(typeof(Suit)))
            foreach (Rank rank in Enum.GetValues(typeof(Rank)))
                cards.Add(new CardDefinition(CardId(suit, rank), rank, suit, new string[0]));
            return cards.AsReadOnly();
        }
    }
}
