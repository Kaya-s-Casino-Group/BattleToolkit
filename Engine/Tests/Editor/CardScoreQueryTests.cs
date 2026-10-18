using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CardScoreQueryTests
    {
        [Test] public void StandardDeckBasePointsAreAvailableWithoutEvaluatingAHand()
        {
            var expected = new long[] { 2, 3, 4, 5, 6, 7, 8, 9, 10, 10, 10, 10, 11 };
            foreach (var definition in StandardDeck.Cards())
            {
                long points = expected[(int)definition.Rank - 2];
                var card = new ScoringCard(definition.Id.Value, new CardFace(definition.Rank, definition.Suit));
                Assert.AreEqual(points, CardScoreRules.GetBasePoints(definition.Rank));
                Assert.AreEqual(points, CardScoreRules.GetBasePoints(definition));
                Assert.AreEqual(points, card.BasePoints); Assert.AreEqual(points, card.PointsPerTrigger);
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => CardScoreRules.GetBasePoints((Rank)0));
            Assert.Throws<ArgumentOutOfRangeException>(() => CardScoreRules.GetBasePoints((Rank)15));
            Assert.Throws<ArgumentNullException>(() => CardScoreRules.GetBasePoints((CardDefinition)null));
        }

        [Test] public void EffectiveFaceAndEnhancementQueriesMatchEachActualScoringTrigger()
        {
            var definition = StandardDeck.Cards().Single(candidate => candidate.Rank == Rank.Two && candidate.Suit == Suit.Hearts);
            var instance = new CardInstance(new CardInstanceId("card"), definition.Id, effects: CardEffects.Enhanced);
            var owned = new DrawCard(instance, bonusPoints: 20, bonusMultiplier: 2, multiplierFactor: 1.5m, retriggers: 2);
            var card = owned.WithFace(new CardFace(Rank.Ace, Suit.Hearts));
            Assert.AreEqual(2, CardScoreRules.GetBasePoints(definition)); Assert.AreEqual(11, card.BasePoints);
            Assert.AreEqual(20, card.BonusPoints); Assert.AreEqual(31, card.PointsPerTrigger);
            Assert.AreEqual(2, card.BonusMultiplier); Assert.AreEqual(1.5m, card.MultiplierFactor); Assert.AreEqual(2, card.Retriggers);
            Assert.IsTrue(card.CanScore);
            var result = ScoreEngine.Evaluate("score", ScoreProfile.Default, HandCategory.HighCard, 1, new[] { card }, new ScoringRelic[0]);
            var additions = result.Steps.Where(step => step.CardId == card.Id && step.Operation == ScoreOperation.AddPoints).ToArray();
            Assert.AreEqual(3, additions.Length);
            Assert.IsTrue(additions.All(step => step.Amount == card.PointsPerTrigger));
            Assert.AreEqual(result.BasePoints + 31 * 3, result.Points); ScoreEngine.Validate(result);
            Assert.AreEqual(11, card.BasePoints); Assert.AreEqual(31, card.PointsPerTrigger);
            Assert.AreSame(instance, owned.Card); Assert.AreEqual(definition.Id, instance.DefinitionId);
        }

        [Test] public void IneffectiveCardsKeepInspectableValuesButProduceNoPerCardScoreSteps()
        {
            var face = new CardFace(Rank.Ace, Suit.Spades);
            var effects = CardEffects.Enhanced | CardEffects.Laminated | CardEffects.Ineffective;
            var card = new ScoringCard("inactive", face, 20, 3, 2, 2, effects);
            Assert.AreEqual(11, card.BasePoints); Assert.AreEqual(20, card.BonusPoints); Assert.AreEqual(31, card.PointsPerTrigger);
            Assert.IsFalse(card.CanScore);
            var relic = new ScoringRelic("per-card", 1, 0, new[] { new ScoreEffect(ScoreTiming.PerCard, ScoreOperation.AddPoints, 100) });
            var result = ScoreEngine.Evaluate("score", ScoreProfile.Default, HandCategory.HighCard, 1, new[] { card }, new[] { relic });
            Assert.IsEmpty(result.Steps); Assert.AreEqual(result.BasePoints, result.Points); Assert.AreEqual(result.BaseMultiplier, result.Multiplier);
            Assert.AreEqual(effects, card.Effects); Assert.AreEqual(31, card.PointsPerTrigger);
            // Markers alone do not invent enhancement values.
            var marked = new ScoringCard("marked", face, effects: CardEffects.Enhanced | CardEffects.Laminated);
            Assert.AreEqual(11, marked.PointsPerTrigger); Assert.AreEqual(0, marked.BonusPoints);
        }

        [Test] public void InspectablePointsDoNotClaimThatAnUnscoredKickerContributes()
        {
            var cards = new[] {
                new ScoringCard("q1", new CardFace(Rank.Queen, Suit.Hearts)),
                new ScoringCard("q2", new CardFace(Rank.Queen, Suit.Spades)),
                new ScoringCard("ace", new CardFace(Rank.Ace, Suit.Clubs), bonusPoints: 20)
            };
            var pattern = PokerHandClassifier.Classify(cards.Select(card => new EffectiveCard(new CardInstanceId(card.Id), card.Face.Rank, card.Face.Suit)));
            var result = ScoreEngine.EvaluatePattern("score", ScoreProfile.Default, pattern, 1, cards, new ScoringRelic[0]);
            Assert.IsTrue(cards[2].CanScore); Assert.AreEqual(31, cards[2].PointsPerTrigger);
            CollectionAssert.AreEquivalent(new[] { "q1", "q2" }, result.Steps.Select(step => step.CardId));
            Assert.AreEqual(result.BasePoints + 20, result.Points);
        }

        [Test] public void CombinedPointsAreCheckedWithoutDestroyingIndividualValuesOrChangingIneffectiveSkip()
        {
            var card = new ScoringCard("large", new CardFace(Rank.Ace, Suit.Spades), bonusPoints: long.MaxValue);
            Assert.AreEqual(11, card.BasePoints); Assert.AreEqual(long.MaxValue, card.BonusPoints);
            Assert.Throws<OverflowException>(() => { var unused = card.PointsPerTrigger; });
            Assert.Throws<OverflowException>(() => ScoreEngine.Evaluate("score", ScoreProfile.Default, HandCategory.HighCard, 1,
                new[] { card }, new ScoringRelic[0]));
            var inactive = new ScoringCard(card.Id, card.Face, card.BonusPoints, effects: CardEffects.Ineffective);
            var result = ScoreEngine.Evaluate("skip", ScoreProfile.Default, HandCategory.HighCard, 1, new[] { inactive }, new ScoringRelic[0]);
            Assert.IsEmpty(result.Steps); Assert.AreEqual(result.BasePoints, result.Points);
        }
    }
}
