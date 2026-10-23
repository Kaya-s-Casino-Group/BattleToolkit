using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    /// <summary>Pattern recognition result. The mode decides whether only pattern cards or all submitted cards score.</summary>
    [DataContract]
    public sealed class PokerHandClassification
    {
        [DataMember(Order = 1, IsRequired = true)] public HandCategory Category { get; private set; }
        [DataMember(Name = "TieBreakRanks", Order = 2, IsRequired = true)] private int[] tieBreakRanks;
        [DataMember(Name = "SubmittedCards", Order = 3, IsRequired = true)] private CardInstanceId[] submittedCards;
        [DataMember(Name = "ScoringCards", Order = 4, IsRequired = true)] private CardInstanceId[] scoringCards;
        private IReadOnlyList<int> tieBreakView;
        private IReadOnlyList<CardInstanceId> submittedView, scoringView;
        public IReadOnlyList<int> TieBreakRanks => tieBreakView ?? (tieBreakView = Guard.View(tieBreakRanks));
        public IReadOnlyList<CardInstanceId> SubmittedCards => submittedView ?? (submittedView = Guard.View(submittedCards));
        public IReadOnlyList<CardInstanceId> ScoringCards => scoringView ?? (scoringView = Guard.View(scoringCards));
        internal PokerHandClassification(HandCategory category, IEnumerable<int> tieBreakRanks,
            IEnumerable<CardInstanceId> submittedCards, IEnumerable<CardInstanceId> scoringCards)
        {
            Category = category; this.tieBreakRanks = Guard.Copy(tieBreakRanks, nameof(tieBreakRanks));
            this.submittedCards = Guard.Copy(submittedCards, nameof(submittedCards)); this.scoringCards = Guard.Copy(scoringCards, nameof(scoringCards));
        }
    }

    /// <summary>Shared high-poker pattern classifier for one to five cards. No betting, run, or scoring rules.</summary>
    public static class PokerHandClassifier
    {
        public static PokerHandClassification ClassifyBest(IEnumerable<EffectiveCard> source, int minFlushCards = 5, int minStraightCards = 5, bool ignoreFlush = false)
        {
            var cards = Guard.Copy(source, nameof(source));
            if (cards.Length <= 5) return Classify(cards, minFlushCards, minStraightCards, ignoreFlush);
            if (cards.Length > 512 || cards.Select(c => c.Id).Distinct().Count() != cards.Length)
                throw new ArgumentException("Expanded plays support up to 512 distinct cards.");
            PokerHandClassification best = null;
            foreach(var subset in PokerExpandedCandidates.Enumerate(cards,minFlushCards,minStraightCards))
            {
                var next = Classify(subset,minFlushCards,minStraightCards,ignoreFlush);
                if (best == null || Compare(next.Category, next.TieBreakRanks, best.Category, best.TieBreakRanks) > 0) best = next;
            }
            return new PokerHandClassification(best.Category, best.TieBreakRanks, cards.Select(c => c.Id), best.ScoringCards);
        }
        public static PokerHandClassification Classify(IEnumerable<EffectiveCard> source, int minFlushCards = 5, int minStraightCards = 5, bool ignoreFlush = false)
        {
            var cards = Guard.Copy(source, nameof(source));
            if (minFlushCards < 3 || minFlushCards > 5 || minStraightCards < 3 || minStraightCards > 5 || cards.Length < 1 || cards.Length > 5 || cards.Select(c => c.Id).Distinct().Count() != cards.Length)
                throw new ArgumentException("Classification requires one to five distinct card instances.");
            return ClassifyValidated(cards, minFlushCards, minStraightCards, ignoreFlush);
        }
        // Compatibility enumerates subsets of an already validated immutable input. Do not copy and
        // revalidate membership for every subset; the classification result still owns its arrays.
        internal static PokerHandClassification ClassifyValidated(EffectiveCard[] cards, int minFlushCards, int minStraightCards, bool ignoreFlush)
        {
            var ranks = cards.Select(c => (int)c.Rank).OrderByDescending(r => r).ToArray();
            var groups = ranks.GroupBy(r => r).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).ToArray();
            bool flush = !ignoreFlush && cards.Length >= minFlushCards && cards.All(c => c.Suit == cards[0].Suit);
            int straight = cards.Length >= minStraightCards && ranks.Distinct().Count() == cards.Length && ranks[0] - ranks[cards.Length - 1] == cards.Length - 1 ? ranks[0] : 0;
            if (cards.Length >= minStraightCards && ranks[0] == 14 && ranks.Skip(1).SequenceEqual(Enumerable.Range(2, cards.Length - 1).Reverse())) straight = cards.Length;
            HandCategory category; int[] tie; Func<EffectiveCard, bool> scores;
            if (groups[0].Count() == 5) { category = HandCategory.FiveOfAKind; tie = new[] { groups[0].Key }; scores = c => true; }
            else if (flush && straight > 0 && cards.Length == 5) { category = HandCategory.StraightFlush; tie = new[] { straight }; scores = c => true; }
            else if (groups[0].Count() == 4) { category = HandCategory.FourOfAKind; tie = groups.Select(g => g.Key).ToArray(); scores = c => (int)c.Rank == groups[0].Key; }
            else if (groups[0].Count() == 3 && groups.Length > 1 && groups[1].Count() == 2) { category = HandCategory.FullHouse; tie = groups.Select(g => g.Key).ToArray(); scores = c => true; }
            else if (flush) { category = HandCategory.Flush; tie = ranks; scores = c => true; }
            else if (straight > 0) { category = HandCategory.Straight; tie = new[] { straight }; scores = c => true; }
            else if (groups[0].Count() == 3) { category = HandCategory.ThreeOfAKind; tie = groups.Select(g => g.Key).ToArray(); scores = c => (int)c.Rank == groups[0].Key; }
            else if (groups[0].Count() == 2 && groups.Length > 1 && groups[1].Count() == 2) { category = HandCategory.TwoPair; tie = groups.Select(g => g.Key).ToArray(); scores = c => (int)c.Rank == groups[0].Key || (int)c.Rank == groups[1].Key; }
            else if (groups[0].Count() == 2) { category = HandCategory.OnePair; tie = groups.Select(g => g.Key).ToArray(); scores = c => (int)c.Rank == groups[0].Key; }
            else { category = HandCategory.HighCard; tie = ranks; var high = cards.First(c => (int)c.Rank == ranks[0]).Id; scores = c => c.Id == high; }
            return new PokerHandClassification(category, tie, cards.Select(c => c.Id), cards.Where(scores).Select(c => c.Id));
        }

        public static int Compare(HandCategory leftCategory, IReadOnlyList<int> leftRanks, HandCategory rightCategory, IReadOnlyList<int> rightRanks)
        {
            if (leftRanks == null || rightRanks == null) throw new ArgumentNullException("Tie-break ranks are required.");
            int result = leftCategory.CompareTo(rightCategory);
            if (result != 0) return result;
            for (int i = 0; i < Math.Min(leftRanks.Count, rightRanks.Count); i++)
                if (leftRanks[i] != rightRanks[i]) return leftRanks[i].CompareTo(rightRanks[i]);
            return leftRanks.Count.CompareTo(rightRanks.Count);
        }
    }
}
