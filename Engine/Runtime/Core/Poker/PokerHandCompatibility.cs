using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>One highest scoring pattern and the distinct compatible shape mechanisms in the submitted cards.</summary>
    public sealed class PokerHandCompatibility
    {
        public PokerHandClassification Highest { get; }
        /// <summary>Highest rank-only pattern from the same subset traversal, with suits ignored.</summary>
        public PokerHandClassification RankHighest { get; }
        public IReadOnlyList<HandCategory> Categories { get; }
        private PokerHandCompatibility(PokerHandClassification highest, PokerHandClassification rankHighest, IEnumerable<HandCategory> categories)
        { Highest = highest; RankHighest = rankHighest; Categories = Array.AsReadOnly(categories.OrderByDescending(c => c).ToArray()); }

        /// <summary>One same-suit subset, ranked lexicographically, independent of rank-hand kickers.</summary>
        public static IReadOnlyList<CardInstanceId> FlushCards(IEnumerable<EffectiveCard> source, int minFlushCards = 5)
        {
            EffectiveCard[] best = null;
            foreach (var group in source.GroupBy(card => card.Suit).Where(group => group.Count() >= minFlushCards))
            {
                var candidate = group.OrderByDescending(card => card.Rank).Take(5).ToArray();
                if (best == null || PokerHandClassifier.Compare(HandCategory.Flush, candidate.Select(card => (int)card.Rank).ToArray(),
                    HandCategory.Flush, best.Select(card => (int)card.Rank).ToArray()) > 0) best = candidate;
            }
            return Array.AsReadOnly((best ?? Array.Empty<EffectiveCard>()).Select(card => card.Id).ToArray());
        }

        public static PokerHandCompatibility Classify(IEnumerable<EffectiveCard> source, int minFlushCards = 5,
            int minStraightCards = 5, bool ignoreFlush = false, bool flushAsAttribute = false, bool modernScoring = false)
        {
            var cards = source?.ToArray() ?? throw new ArgumentNullException(nameof(source));
            if (cards.Length < 1 || cards.Length > 512 || cards.Any(c => c == null) || cards.Select(c => c.Id).Distinct().Count() != cards.Length ||
                minFlushCards < 3 || minFlushCards > 5 || minStraightCards < 3 || minStraightCards > 5)
                throw new ArgumentException("Compatible patterns require one to 512 distinct cards and valid shape limits.");
            var categories = new HashSet<HandCategory>(); PokerHandClassification best = null, rankBest = null;
            // At most 637 subsets for ten cards; evaluate each shape once and keep one authoritative score subset.
            // Ignoring suits on the same subset reveals a rank mechanism hidden by a higher flush.
            var subsets = cards.Length > 10 ? PokerExpandedCandidates.Enumerate(cards,minFlushCards,minStraightCards) : SmallSubsets(cards);
            foreach(var subset in subsets)
            {
                var normal = PokerHandClassifier.ClassifyValidated(subset, minFlushCards, minStraightCards, ignoreFlush);
                // Suits can only change Flush/StraightFlush. All other classifications are already
                // valid rank evidence, including full houses and duplicated-card five of a kind.
                var rank = !ignoreFlush && (normal.Category == HandCategory.Flush || normal.Category == HandCategory.StraightFlush)
                    ? PokerHandClassifier.ClassifyValidated(subset, minFlushCards, minStraightCards, true) : normal;
                if (flushAsAttribute && (normal.Category == HandCategory.Flush ||
                    modernScoring && normal.Category == HandCategory.StraightFlush && normal.TieBreakRanks[0] != (int)Rank.Ace))
                    normal = rank;
                categories.Add(normal.Category);
                if (!ignoreFlush && subset.Length >= minFlushCards && subset.All(c => c.Suit == subset[0].Suit))
                    categories.Add(HandCategory.Flush);
                if (!ignoreFlush) categories.Add(rank.Category);
                if (rankBest == null || PokerHandClassifier.Compare(rank.Category, rank.TieBreakRanks, rankBest.Category, rankBest.TieBreakRanks) > 0)
                    rankBest = rank;
                if (best == null || PokerHandClassifier.Compare(normal.Category, normal.TieBreakRanks, best.Category, best.TieBreakRanks) > 0)
                    best = normal;
            }
            if (categories.Count > 1 && !(modernScoring && best.Category == HandCategory.HighCard))
                categories.Remove(HandCategory.HighCard);
            var highest = new PokerHandClassification(best.Category, best.TieBreakRanks, cards.Select(c => c.Id), best.ScoringCards);
            // The existing royal-flush elimination branch remains exclusive.
            if (highest.Category == HandCategory.StraightFlush && highest.TieBreakRanks[0] == (int)Rank.Ace)
                categories = modernScoring ? new HashSet<HandCategory> { HandCategory.StraightFlush, HandCategory.Flush } :
                    new HashSet<HandCategory> { HandCategory.StraightFlush };
            var rankHighest = ReferenceEquals(best, rankBest) ? highest :
                new PokerHandClassification(rankBest.Category, rankBest.TieBreakRanks, cards.Select(c => c.Id), rankBest.ScoringCards);
            return new PokerHandCompatibility(highest, rankHighest, categories);
        }

        private static IEnumerable<EffectiveCard[]> SmallSubsets(EffectiveCard[] cards)
        {
            for (int mask = 1; mask < 1 << cards.Length; mask++)
            {
                int count = 0;
                for (int bits = mask; bits != 0; bits &= bits - 1) count++;
                if (count > 5) continue;
                var subset = new EffectiveCard[count];
                for (int i = 0, at = 0; i < cards.Length; i++)
                    if ((mask & 1 << i) != 0) subset[at++] = cards[i];
                yield return subset;
            }
        }
    }
}
