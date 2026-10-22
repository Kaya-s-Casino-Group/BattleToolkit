using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    public sealed class HandGuideTests
    {
        static readonly ParticipantId Player = new ParticipantId("player"), Enemy = new ParticipantId("enemy");
        static BattleAdminCard Card(int rank, int suit = 1, CardEffects effects = CardEffects.None) =>
            new BattleAdminCard(definitionId: "standard:" + suit + ":" + rank, effects: effects);
        static BattleSession Setup(BattleAdminCard[] hand, BattleAdminCard[] deck = null, BattleAdminCard[] discard = null, string relic = null, bool fullHealth = false)
        {
            var content = BattleContentCompiler.CompileXml(ToolkitTestFiles.Read("Engine/Content/SingleChapterBattle.xml"));
            var session = BattleContentFactory.Create(content.Content, new BattleSetupRequest("single-chapter-battle-1", "guide-test",
                HmacDeckShuffler.FromSeed(new byte[32]))).Session;
            Assert.IsTrue(session.Execute(0, Player, CardDuelAction.Start).Accepted);
            var patch = session.ApplyAdmin(session.State.Revision, new BattleAdminPatch(
                vitals: new[] { new BattleAdminVitals(Enemy, 100000, 100000, 0), new BattleAdminVitals(Player, fullHealth ? 800 : 600, 800, 0) },
                zones: new[] { new BattleAdminZone(Player, CardZoneKinds.Hand, hand),
                    new BattleAdminZone(Player, CardZoneKinds.Deck, deck ?? Array.Empty<BattleAdminCard>()),
                    new BattleAdminZone(Player, CardZoneKinds.Discard, discard ?? Array.Empty<BattleAdminCard>()) },
                relics: new[] { new BattleAdminRelics(Player, relic == null ? Array.Empty<BattleAdminRelic>() : new[] { new BattleAdminRelic(relic) }) }));
            Assert.IsTrue(patch.Accepted, patch.Error.ToString()); return session;
        }
        [Test] public void ExistingPairsAreMinimalDistinctAndDoNotIncludeTripleAsPair()
        {
            var session = Setup(new[] { Card(2), Card(2,2), Card(2,3), Card(7), Card(7,2) });
            var before = session.State;
            var result = session.Engine.QueryHandGuide(before, Player, new HandGuideRequest(HandGuideType.OnePair));
            Assert.AreEqual(HandGuideAvailability.Complete, result.Availability);
            Assert.AreEqual(4, result.Candidates.Count);
            Assert.IsTrue(result.Candidates.All(c => c.Cards.Count == 2 && c.MissingCount == 0));
            Assert.AreEqual(4, result.Candidates.Select(c => c.Id).Distinct().Count());
            Assert.AreSame(before, session.State);
        }
        [Test] public void DiscardCompletesStraightWithoutDrawingAndPreservesSpecialCard()
        {
            var session = Setup(new[] { Card(2), Card(3,2), Card(4,3), Card(5,4) }, discard: new[] { Card(6,1,CardEffects.Enhanced) });
            var before = session.State;
            var result = session.Engine.QueryHandGuide(before, Player, new HandGuideRequest(HandGuideType.Straight));
            Assert.AreEqual(HandGuideAvailability.Completable, result.Availability);
            var candidate = result.Candidates.Single(); Assert.AreEqual(1, candidate.MissingCount);
            var missing = candidate.Cards.Single(c => c.IsMissing);
            Assert.AreEqual(CardZoneKinds.Discard, missing.ZoneKind); Assert.AreEqual(CardEffects.Enhanced, missing.Card.Effects);
            var preview = session.Engine.PreviewHandGuide(before, candidate, true, new[] { Enemy });
            Assert.AreEqual(CardDuelError.None, preview.Error); Assert.AreEqual(CardDuelError.None, preview.SettlementError);
            Assert.AreEqual(5, preview.BaseEffects.Steps.Count(s => s.Effect.Kind == CombatEffectKind.Damage));
            Assert.AreSame(before, session.State); Assert.AreEqual(4, before.Zone(CardZoneKinds.Hand).Cards.Count);
            Assert.AreEqual(1, before.Zone(CardZoneKinds.Discard).Cards.Count);
        }
        [Test] public void MultiplicityAndUnplayableCardsCannotInventACompletion()
        {
            var session = Setup(new[] { Card(8) }, new[] { Card(8,2,CardEffects.Unplayable), Card(9) });
            Assert.AreEqual(HandGuideAvailability.Impossible, session.Engine.QueryHandGuide(session.State, Player,
                new HandGuideRequest(HandGuideType.OnePair)).Availability);
            Assert.AreEqual(HandGuideAvailability.Impossible, session.Engine.QueryHandGuide(session.State, Player,
                new HandGuideRequest(HandGuideType.FiveOfAKind)).Availability);
        }
        [Test] public void CompleteHandWinsOverHigherValueMissingCards()
        {
            var session = Setup(new[] { Card(2), Card(2,2), Card(13) }, new[] { Card(13,2) });
            var result = session.Engine.QueryHandGuide(session.State, Player, new HandGuideRequest(HandGuideType.OnePair));
            Assert.AreEqual(1, result.Candidates.Count); Assert.AreEqual(0, result.Candidates[0].MissingCount);
            Assert.IsTrue(result.Candidates[0].Cards.All(c => c.Face.Rank == Rank.Two));
        }
        [Test] public void FlushIsIndependentAndPreviewMatchesAuthoritativeSettlement()
        {
            var session = Setup(new[] { Card(2), Card(3), Card(4), Card(5), Card(6) }, relic: "11002");
            var before = session.State;
            var unfiltered = session.Engine.QueryHandGuide(before, Player, new HandGuideRequest(HandGuideType.Straight));
            var filtered = session.Engine.QueryHandGuide(before, Player, new HandGuideRequest(HandGuideType.Straight, true));
            Assert.AreEqual(unfiltered.Candidates.Single().Id, filtered.Candidates.Single().Id);
            var preview = session.Engine.PreviewHandGuide(before, filtered.Candidates.Single(), true, new[] { Enemy });
            Assert.IsTrue(preview.Hand.IsFlush); Assert.AreEqual(80, preview.Score.Total);
            Assert.AreEqual(10m, preview.RuleEffects.Steps.Single(s => s.Effect.Kind == CombatEffectKind.Heal).Effect.UnroundedAmount);
            Assert.AreEqual(16, preview.BaseEffects.Steps.Single(s => s.Effect.Kind == CombatEffectKind.Heal).Effect.Amount);
            Assert.AreEqual(16, preview.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).HealthRestored);
            var actual = session.Execute(before.Revision, Player, CardDuelAction.Play, before.Zone(CardZoneKinds.Hand).Cards, new[] { Enemy });
            Assert.IsTrue(actual.Accepted, actual.Error.ToString());
            CollectionAssert.AreEqual(actual.Steps[0].Transition.Events.Select(e => e.SettledAmount), preview.Events.Select(e => e.SettledAmount));
            Assert.AreEqual(CardDuelError.StaleRevision, session.Engine.PreviewHandGuide(session.State, filtered.Candidates.Single()).Error);
        }
        [Test] public void PureFlushKeepsScoringCardsSeparateFromHealingCardsEvenAtFullHealth()
        {
            var session = Setup(new[] { Card(2), Card(5), Card(7), Card(9), Card(13) }, fullHealth: true);
            var candidate = session.Engine.QueryHandGuide(session.State, Player, new HandGuideRequest(HandGuideType.HighCard, true)).Candidates.Single();
            var preview = session.Engine.PreviewHandGuide(session.State, candidate, true, new[] { Enemy });
            Assert.AreEqual(1, preview.Hand.ScoringCards.Count); Assert.AreEqual(5, preview.Hand.FlushCards.Count);
            Assert.AreEqual(26, preview.Score.Total); Assert.AreEqual(36, preview.Hand.FlushBaseTotal);
            var heal = preview.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal);
            Assert.AreEqual(18, heal.SettledAmount); Assert.AreEqual(0, heal.HealthRestored); Assert.AreEqual(18, heal.Overheal);
        }
        [Test] public void FullHouseRetainsBothDamageComponentsAndUtilityEffects()
        {
            var session = Setup(new[] { Card(3), Card(3,2), Card(3,3), Card(2), Card(2,2) });
            var candidate = session.Engine.QueryHandGuide(session.State, Player, new HandGuideRequest(HandGuideType.FullHouse)).Candidates.Single();
            var preview = session.Engine.PreviewHandGuide(session.State, candidate, true, new[] { Enemy });
            Assert.AreEqual(CardDuelError.None, preview.SettlementError);
            CollectionAssert.AreEqual(new long[] { 36, 16 }, preview.BaseEffects.Steps.Where(s => s.Effect.Kind == CombatEffectKind.Damage).Select(s => s.Effect.Amount));
            Assert.IsTrue(preview.BaseEffects.Steps.Any(s => s.Effect.Kind == CombatEffectKind.Cleanse));
        }
        [Test] public void RoyalFlushIsSeparateFromOrdinaryStraightFlush()
        {
            var session = Setup(new[] { Card(10), Card(11), Card(12), Card(13), Card(14) });
            var royal = session.Engine.QueryHandGuide(session.State, Player, new HandGuideRequest(HandGuideType.RoyalFlush));
            Assert.AreEqual(HandGuideAvailability.Complete, royal.Availability);
            Assert.AreEqual(HandGuideAvailability.Impossible, session.Engine.QueryHandGuide(session.State, Player, new HandGuideRequest(HandGuideType.Straight, true)).Availability);
            var preview = session.Engine.PreviewHandGuide(session.State, royal.Candidates.Single());
            Assert.IsTrue(preview.BaseEffects.Steps.Any(s => s.Effect.Kind == CombatEffectKind.Eliminate));
        }
        [Test] public void BudgetExhaustionIsUnknownNeverImpossible()
        {
            var session = Setup(new[] { Card(2), Card(3) }, new[] { Card(4), Card(5), Card(6) });
            var result = session.Engine.QueryHandGuide(session.State, Player, new HandGuideRequest(HandGuideType.Straight, nodeBudget: 1));
            Assert.IsTrue(result.Truncated); Assert.AreEqual(HandGuideAvailability.SearchLimited, result.Availability);
        }
        [Test] public void SampleOrderDoesNotRevealDrawOrder()
        {
            var session = Setup(new[] { Card(13) }, new[] { Card(13,2), Card(13,3), Card(9), Card(7) });
            var before = session.State;
            var first = session.Engine.QueryHandGuide(before, Player, new HandGuideRequest(HandGuideType.OnePair));
            var reordered = session.ApplyAdmin(before.Revision, new BattleAdminPatch(zones: new[] { new BattleAdminZone(Player, CardZoneKinds.Deck,
                before.Zone(CardZoneKinds.Deck).Cards.Reverse().Select(id => new BattleAdminCard(instanceId: id.Value))) }));
            Assert.IsTrue(reordered.Accepted);
            var second = session.Engine.QueryHandGuide(session.State, Player, new HandGuideRequest(HandGuideType.OnePair));
            CollectionAssert.AreEqual(first.Candidates.Select(c => c.Id), second.Candidates.Select(c => c.Id));
        }
        [Test] public void StandardDeckAnswersEveryFilterWithinDefaultBudget()
        {
            var deck = Enumerable.Range(1,4).SelectMany(suit => Enumerable.Range(2,13).Select(rank => Card(rank,suit))).ToArray();
            var session = Setup(Array.Empty<BattleAdminCard>(), deck);
            foreach (HandGuideType type in Enum.GetValues(typeof(HandGuideType))) foreach (bool flush in new[] { false, true })
            {
                var result = session.Engine.QueryHandGuide(session.State, Player, new HandGuideRequest(type, flush));
                bool possible = type != HandGuideType.FiveOfAKind && (!flush || type == HandGuideType.Any || type == HandGuideType.HighCard ||
                    type == HandGuideType.Straight || type == HandGuideType.RoyalFlush);
                Assert.AreEqual(possible ? HandGuideAvailability.Completable : HandGuideAvailability.Impossible,
                    result.Availability, type + " flush=" + flush + " nodes=" + result.VisitedNodes);
            }
        }
    }
}
