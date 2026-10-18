using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    public sealed class ScoreProfileTests
    {
        private static RankPointRule[] Ranks(long king = 10) => Enum.GetValues(typeof(Rank)).Cast<Rank>()
            .Select(rank => new RankPointRule(rank, rank == Rank.King ? king : CardScoreRules.GetBasePoints(rank))).ToArray();
        private static ScoreProfile Profile(CardPointProfile points) => new ScoreProfile("custom", Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>()
            .Select(category => ScoreProfile.Default.Rule(category)), points);
        private static ScoringCard Card(string id, Rank rank, Suit suit, CardEffects effects = CardEffects.None) => new ScoringCard(id, new CardFace(rank, suit), effects: effects);
        private static ScoreResult Score(ScoreProfile profile, int level, params ScoringCard[] cards) => ScoreEngine.EvaluatePattern("test", profile,
            PokerHandClassifier.Classify(cards.Select(card => new EffectiveCard(new CardInstanceId(card.Id), card.Face.Rank, card.Face.Suit))), level, cards, new ScoringRelic[0]);

        [TestCase(HandCategory.HighCard, 5, 1, 10, 1)]
        [TestCase(HandCategory.OnePair, 10, 2, 15, 1)]
        [TestCase(HandCategory.TwoPair, 20, 2, 20, 1)]
        [TestCase(HandCategory.ThreeOfAKind, 30, 3, 20, 2)]
        [TestCase(HandCategory.Straight, 30, 4, 30, 3)]
        [TestCase(HandCategory.Flush, 35, 4, 15, 2)]
        [TestCase(HandCategory.FullHouse, 40, 4, 25, 2)]
        [TestCase(HandCategory.FourOfAKind, 60, 7, 30, 3)]
        [TestCase(HandCategory.StraightFlush, 100, 8, 40, 4)]
        [TestCase(HandCategory.FiveOfAKind, 120, 12, 35, 3)]
        public void DefaultBaseAndGrowthMatchReference(HandCategory category, long chips, int mult, long growthChips, int growthMult)
        {
            var score = ScoreEngine.Evaluate("level3", ScoreProfile.Default, category, 3, new ScoringCard[0], new ScoringRelic[0]);
            Assert.AreEqual(chips + 2 * growthChips, score.BasePoints);
            Assert.AreEqual(mult + 2 * growthMult, score.BaseMultiplier); ScoreEngine.Validate(score);
        }
        [Test] public void PairUsesOnlyPatternCardsAndKeepsAceAsUnscoredKicker()
        {
            var score = Score(ScoreProfile.Default, 1, Card("k1", Rank.King, Suit.Spades), Card("k2", Rank.King, Suit.Hearts), Card("a", Rank.Ace, Suit.Clubs));
            Assert.AreEqual(HandCategory.OnePair, score.Category); Assert.AreEqual(30, score.Points); Assert.AreEqual(2, score.Multiplier); Assert.AreEqual(60, score.Total);
            CollectionAssert.AreEqual(new[] { "k1", "k2" }, score.Steps.Select(step => step.CardId));
        }
        [Test] public void FaceOverrideAndRankDefaultsStackWithInstanceBonusAndRetriggers()
        {
            var table = new CardPointProfile(Ranks(20), new[] { new FacePointRule(new CardFace(Rank.King, Suit.Spades), 50) });
            var special = new ScoringCard("special", new CardFace(Rank.King, Suit.Spades), bonusPoints: 3, retriggers: 1);
            var ordinary = Card("ordinary", Rank.King, Suit.Hearts);
            var score = Score(Profile(table), 1, special, ordinary);
            Assert.AreEqual(53, special.GetPointsPerTrigger(table)); Assert.AreEqual(20, ordinary.GetBasePoints(table));
            Assert.AreEqual((10 + 53 * 2 + 20) * 2, score.Total);
            Assert.AreEqual(10, ordinary.BasePoints); // Default-only query is intentionally explicit.
            ScoreEngine.Validate(score);
        }
        [Test] public void IneffectiveStillFormsPairButSkipsConfiguredChips()
        {
            var table = new CardPointProfile(Ranks(long.MaxValue));
            var cards = new[] { Card("a", Rank.King, Suit.Spades, CardEffects.Ineffective), Card("b", Rank.King, Suit.Hearts, CardEffects.Ineffective) };
            var result = Score(Profile(table), 1, cards);
            Assert.AreEqual(HandCategory.OnePair, result.Category); Assert.AreEqual(20, result.Total); Assert.IsEmpty(result.Steps);
            Assert.Throws<OverflowException>(() => Score(Profile(table), 1, Card("c", Rank.King, Suit.Hearts)));
        }
        [Test] public void ChipConfigurationCannotChangeClassificationOrHandComparison()
        {
            var cards = new[] { Card("a", Rank.Ace, Suit.Spades), Card("k", Rank.King, Suit.Hearts) };
            var result = Score(Profile(new CardPointProfile(Ranks(1000))), 1, cards);
            Assert.AreEqual(HandCategory.HighCard, result.Category); Assert.AreEqual(16, result.Total);
            Assert.AreEqual("a", result.Steps.Single().CardId);
        }
        [Test] public void ProfilesCopyInputsAndRejectMissingDuplicateOrNegativeRules()
        {
            var ranks = Ranks(); var table = new CardPointProfile(ranks); ranks[0] = new RankPointRule(Rank.Two, 999);
            Assert.AreEqual(2, table.GetBasePoints(new CardFace(Rank.Two, Suit.Spades)));
            Assert.Throws<ArgumentException>(() => new CardPointProfile(ranks.Skip(1)));
            Assert.Throws<ArgumentException>(() => new CardPointProfile(ranks.Concat(ranks.Take(1))));
            var face = new FacePointRule(new CardFace(Rank.Two, Suit.Spades), 9);
            Assert.Throws<ArgumentException>(() => new CardPointProfile(ranks, new[] { face, face }));
            Assert.Throws<ArgumentException>(() => new RankPointRule(Rank.Two, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FacePointRule(face.Face, -1));
        }
    }
}
