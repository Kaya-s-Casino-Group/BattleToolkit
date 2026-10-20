using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>One highest scoring pattern and the distinct compatible shape mechanisms in the submitted cards.</summary>
    public sealed class PokerHandCompatibility
    {
        public PokerHandClassification Highest { get; }
        public IReadOnlyList<HandCategory> Categories { get; }
        private PokerHandCompatibility(PokerHandClassification highest, IEnumerable<HandCategory> categories)
        { Highest = highest; Categories = Array.AsReadOnly(categories.OrderByDescending(c => c).ToArray()); }

        public static PokerHandCompatibility Classify(IEnumerable<EffectiveCard> source, int minFlushCards = 5,
            int minStraightCards = 5, bool ignoreFlush = false, bool flushAsAttribute = false)
        {
            var cards = source?.ToArray() ?? throw new ArgumentNullException(nameof(source));
            if (cards.Length < 1 || cards.Length > 512 || cards.Any(c => c == null) || cards.Select(c => c.Id).Distinct().Count() != cards.Length ||
                minFlushCards < 3 || minFlushCards > 5 || minStraightCards < 3 || minStraightCards > 5)
                throw new ArgumentException("Compatible patterns require one to 512 distinct cards and valid shape limits.");
            var categories = new HashSet<HandCategory>(); PokerHandClassification best = null;
            // At most 637 subsets for ten cards; evaluate each shape once and keep one authoritative score subset.
            // Ignoring suits on the same subset reveals a rank mechanism hidden by a higher flush.
            var subsets = cards.Length > 10 ? PokerExpandedCandidates.Enumerate(cards,minFlushCards,minStraightCards) :
                Enumerable.Range(1,(1 << cards.Length)-1).Select(mask=>cards.Where((c,i)=>(mask & 1 << i)!=0).ToArray()).Where(c=>c.Length<=5);
            foreach(var subset in subsets)
            {
                var normal = PokerHandClassifier.Classify(subset, minFlushCards, minStraightCards, ignoreFlush);
                if (flushAsAttribute && normal.Category == HandCategory.Flush)
                    normal = PokerHandClassifier.Classify(subset, minFlushCards, minStraightCards, true);
                categories.Add(normal.Category);
                if (!ignoreFlush && subset.Length >= minFlushCards && subset.All(c => c.Suit == subset[0].Suit))
                    categories.Add(HandCategory.Flush);
                if (!ignoreFlush) categories.Add(PokerHandClassifier.Classify(subset, minFlushCards, minStraightCards, true).Category);
                if (best == null || PokerHandClassifier.Compare(normal.Category, normal.TieBreakRanks, best.Category, best.TieBreakRanks) > 0)
                    best = normal;
            }
            if (categories.Count > 1) categories.Remove(HandCategory.HighCard);
            var highest = new PokerHandClassification(best.Category, best.TieBreakRanks, cards.Select(c => c.Id), best.ScoringCards);
            // The existing royal-flush elimination branch remains exclusive.
            if (highest.Category == HandCategory.StraightFlush && highest.TieBreakRanks[0] == (int)Rank.Ace)
                categories = new HashSet<HandCategory> { HandCategory.StraightFlush };
            return new PokerHandCompatibility(highest, categories);
        }
    }
}
