using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    public sealed class PokerHandCompatibilityRegressionTests
    {
        // Deliberately keep the exhaustive, separately classified rank/suit oracle. It also checks
        // stable scoring-card identity when several equally ranked subsets exist.
        private static (PokerHandClassification best, HandCategory[] categories) Exhaustive(
            EffectiveCard[] cards, int flush, int straight, bool ignore, bool attribute, bool modern)
        {
            PokerHandClassification best = null;
            var categories = new HashSet<HandCategory>();
            foreach (var subset in Enumerable.Range(1, (1 << cards.Length) - 1)
                .Select(mask => cards.Where((card, index) => (mask & 1 << index) != 0).ToArray()).Where(part => part.Length <= 5))
            {
                var normal = PokerHandClassifier.Classify(subset, flush, straight, ignore);
                if (attribute && (normal.Category == HandCategory.Flush || modern &&
                    normal.Category == HandCategory.StraightFlush && normal.TieBreakRanks[0] != (int)Rank.Ace))
                    normal = PokerHandClassifier.Classify(subset, flush, straight, true);
                categories.Add(normal.Category);
                if (!ignore && subset.Length >= flush && subset.All(card => card.Suit == subset[0].Suit)) categories.Add(HandCategory.Flush);
                if (!ignore) categories.Add(PokerHandClassifier.Classify(subset, flush, straight, true).Category);
                if (best == null || PokerHandClassifier.Compare(normal.Category, normal.TieBreakRanks, best.Category, best.TieBreakRanks) > 0) best = normal;
            }
            if (categories.Count > 1 && !(modern && best.Category == HandCategory.HighCard)) categories.Remove(HandCategory.HighCard);
            if (best.Category == HandCategory.StraightFlush && best.TieBreakRanks[0] == (int)Rank.Ace)
                categories = modern ? new HashSet<HandCategory> { HandCategory.StraightFlush, HandCategory.Flush } : new HashSet<HandCategory> { HandCategory.StraightFlush };
            return (best, categories.OrderByDescending(category => category).ToArray());
        }
        private static EffectiveCard[] Cards(int[] ranks, bool sameSuit = false) => ranks.Select((rank, i) =>
            new EffectiveCard(new CardInstanceId("card/" + i), (Rank)rank, sameSuit ? Suit.Clubs : (Suit)(i % 4 + 1))).ToArray();
        private static void SamePattern(PokerHandClassification expected, PokerHandClassification actual)
        {
            Assert.AreEqual(expected.Category, actual.Category);
            CollectionAssert.AreEqual(expected.TieBreakRanks, actual.TieBreakRanks);
            CollectionAssert.AreEqual(expected.ScoringCards, actual.ScoringCards);
        }
        private static void Check(EffectiveCard[] cards)
        {
            for (int flush = 3; flush <= 5; flush++) for (int straight = 3; straight <= 5; straight++)
            foreach (bool ignore in new[] { false, true }) foreach (bool attribute in new[] { false, true }) foreach (bool modern in new[] { false, true })
            {
                var expected = Exhaustive(cards, flush, straight, ignore, attribute, modern);
                var actual = PokerHandCompatibility.Classify(cards, flush, straight, ignore, attribute, modern);
                SamePattern(expected.best, actual.Highest);
                CollectionAssert.AreEqual(expected.categories, actual.Categories);
                CollectionAssert.AreEqual(cards.Select(card => card.Id), actual.Highest.SubmittedCards);
                var rank = Exhaustive(cards, flush, straight, true, false, false);
                SamePattern(rank.best, actual.RankHighest);
                CollectionAssert.AreEqual(cards.Select(card => card.Id), actual.RankHighest.SubmittedCards);
            }
        }
        [Test] public void SpecialShapesMatchExhaustiveClassificationForEveryShapeThresholdAndCompositionMode()
        {
            foreach (var ranks in new[] {
                new[] { 14, 2, 3, 4, 5 }, new[] { 10, 11, 12, 13, 14 }, new[] { 2, 2, 2, 2, 2 },
                new[] { 2, 2, 2, 3, 3 }, new[] { 2, 3, 4, 8, 8 }, new[] { 2, 4, 7, 10, 13 } })
            { Check(Cards(ranks)); Check(Cards(ranks, true)); }
        }
        [TestCase(1)] [TestCase(3)] [TestCase(5)] [TestCase(8)] [TestCase(10)]
        public void StableRandomHandsPreserveCategoriesKickersAndScoringIdentity(int size)
        {
            var random = new Random(312 + size);
            for (int sample = 0; sample < 3; sample++)
                Check(Enumerable.Range(0, size).Select(i => new EffectiveCard(new CardInstanceId("sample/" + i),
                    (Rank)random.Next(2, 15), (Suit)random.Next(1, 5))).ToArray());
        }
        [Test] public void FiveCardClassificationAllocatesLessThanExhaustiveRankAndSuitPasses()
        {
            var cards = Cards(new[] { 2, 3, 4, 5, 6 }, true);
            for (int i = 0; i < 10; i++) { PokerHandCompatibility.Classify(cards, modernScoring: true, flushAsAttribute: true); Exhaustive(cards, 5, 5, false, true, true); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++) PokerHandCompatibility.Classify(cards, modernScoring: true, flushAsAttribute: true);
            long optimized = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++) Exhaustive(cards, 5, 5, false, true, true);
            long reference = GC.GetAllocatedBytesForCurrentThread() - before;
            if (reference == 0) Assert.Ignore("This runtime does not expose per-thread allocation counts.");
            Assert.Less(optimized, reference * .75, "Classification should not restore duplicated per-subset rank/suit work.");
        }
    }
}
