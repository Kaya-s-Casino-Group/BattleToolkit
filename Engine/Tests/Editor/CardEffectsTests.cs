using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CardEffectsTests
    {
        [Test] public void EffectsComposeWithoutChangingIdentityAndDisablingIsIndependent()
        {
            var original = new CardInstance(new CardInstanceId("instance"), new CardDefinitionId("definition"), "origin");
            var enhanced = original.AddEffects(CardEffects.Enhanced | CardEffects.Laminated);
            var ineffective = enhanced.AddEffects(CardEffects.Ineffective).AddEffects(CardEffects.Enhanced);
            Assert.AreEqual(CardEffects.None, original.Effects);
            Assert.AreEqual(7, (int)ineffective.Effects); Assert.IsFalse(ineffective.CanScore);
            Assert.IsTrue(ineffective.CanPlay); Assert.IsTrue(ineffective.CanDiscard);
            var disabled = ineffective.WithDisabled(true);
            Assert.IsFalse(disabled.CanPlay); Assert.IsFalse(disabled.CanDiscard); Assert.IsFalse(disabled.CanScore);
            var restored = disabled.RemoveEffects(CardEffects.Ineffective);
            Assert.AreEqual(enhanced.Effects, restored.Effects); Assert.IsTrue(restored.CanScore); Assert.IsTrue(restored.IsDisabled);
            Assert.IsTrue(restored.WithDisabled(false).CanPlay);
            Assert.AreEqual(original.Id, restored.Id); Assert.AreEqual(original.DefinitionId, restored.DefinitionId);
            Assert.AreEqual(original.OriginId, restored.OriginId);
        }

        [Test] public void UnknownBitsAreRejectedRatherThanSilentlyChangingRules()
        {
            foreach (var flags in new[] { (CardEffects)8, (CardEffects)(-1) })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new CardInstance(new CardInstanceId("a"), new CardDefinitionId("d"), effects: flags));
                Assert.Throws<ArgumentOutOfRangeException>(() => new ScoringCard("a", new CardFace(Rank.Ace, Suit.Clubs), effects: flags));
                var card = new CardInstance(new CardInstanceId("a"), new CardDefinitionId("d"));
                Assert.Throws<ArgumentOutOfRangeException>(() => card.RemoveEffects(flags));
            }
        }

        [Test] public void CardContractRoundTripsEffectsAndDisableWithoutClientState()
        {
            var serializer = new DataContractJsonSerializer(typeof(CardInstance));
            var card = new CardInstance(new CardInstanceId("a"), new CardDefinitionId("d"), "origin",
                CardEffects.Enhanced | CardEffects.Laminated | CardEffects.Ineffective, true);
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, card);
                var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
                Assert.IsFalse(json.Contains("Hovered")); Assert.IsFalse(json.Contains("Selected")); Assert.IsFalse(json.Contains("Visible"));
                stream.Position = 0; var restored = (CardInstance)serializer.ReadObject(stream);
                Assert.AreEqual(card.Effects, restored.Effects); Assert.IsTrue(restored.IsDisabled);
                Assert.AreEqual(card.DefinitionId, restored.DefinitionId); Assert.AreEqual(card.OriginId, restored.OriginId);
            }
        }

        [Test] public void IneffectiveCardStillFormsPairButSkipsAllPerCardScoreContributions()
        {
            var submitted = new[] {
                new EffectiveCard(new CardInstanceId("a"), Rank.Ace, Suit.Clubs),
                new EffectiveCard(new CardInstanceId("b"), Rank.Ace, Suit.Hearts) };
            var pattern = PokerHandClassifier.Classify(submitted);
            Assert.AreEqual(HandCategory.OnePair, pattern.Category); Assert.AreEqual(2, pattern.ScoringCards.Count);
            var cards = new[] {
                new ScoringCard("a", new CardFace(Rank.Ace, Suit.Clubs), long.MaxValue, 100, 100, 8,
                    CardEffects.Enhanced | CardEffects.Laminated | CardEffects.Ineffective),
                new ScoringCard("b", new CardFace(Rank.Ace, Suit.Hearts), effects: CardEffects.Enhanced | CardEffects.Laminated) };
            var relic = new ScoringRelic("joker", 1, 0, new[] { new ScoreEffect(ScoreTiming.PerCard, ScoreOperation.AddPoints, 5) });
            // This fixture isolates card-effect filtering from balance changes to the default profile.
            // Default scoring values are covered by the scoring and shared-content tests.
            var baseline = ScoreProfile.Default;
            var profile = new ScoreProfile("card-effects-fixture", Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>()
                .Select(category => category == HandCategory.OnePair ? new HandScoreRule(category, 20, 2) : baseline.Rule(category)));
            var result = ScoreEngine.EvaluatePattern("score", profile, pattern, 1, cards, new[] { relic });
            Assert.AreEqual(72, result.Total); // (pair base 20 + live ace 11 + relic 5) * 2
            Assert.AreEqual(2, result.Steps.Count); Assert.IsTrue(result.Steps.All(s => s.CardId == "b"));
            ScoreEngine.Validate(result);
            var allIneffective = cards.Select(c => new ScoringCard(c.Id, c.Face, effects: CardEffects.Ineffective));
            var baseOnly = ScoreEngine.EvaluatePattern("base", profile, pattern, 1, allIneffective, new ScoringRelic[0]);
            Assert.AreEqual(40, baseOnly.Total); Assert.AreEqual(HandCategory.OnePair, baseOnly.Category); Assert.AreEqual(0, baseOnly.Steps.Count);
        }
    }
}
