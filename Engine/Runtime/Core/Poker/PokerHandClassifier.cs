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
        public IReadOnlyList<int> TieBreakRanks => Guard.View(tieBreakRanks);
        public IReadOnlyList<CardInstanceId> SubmittedCards => Guard.View(submittedCards);
        public IReadOnlyList<CardInstanceId> ScoringCards => Guard.View(scoringCards);
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
        public static PokerHandClassification Classify(IEnumerable<EffectiveCard> source)
        {
            var cards = Guard.Copy(source, nameof(source));
            if (cards.Length < 1 || cards.Length > 5 || cards.Select(c => c.Id).Distinct().Count() != cards.Length)
                throw new ArgumentException("Classification requires one to five distinct card instances.");
            var ranks = cards.Select(c => (int)c.Rank).OrderByDescending(r => r).ToArray();
            var groups = ranks.GroupBy(r => r).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).ToArray();
            bool flush = cards.Length == 5 && cards.All(c => c.Suit == cards[0].Suit);
            int straight = cards.Length == 5 && ranks.Distinct().Count() == 5 && ranks[0] - ranks[4] == 4 ? ranks[0] : 0;
            if (ranks.SequenceEqual(new[] { 14, 5, 4, 3, 2 })) straight = 5;
            HandCategory category; int[] tie; Func<EffectiveCard, bool> scores;
            if (groups[0].Count() == 5) { category = HandCategory.FiveOfAKind; tie = new[] { groups[0].Key }; scores = c => true; }
            else if (flush && straight > 0) { category = HandCategory.StraightFlush; tie = new[] { straight }; scores = c => true; }
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
