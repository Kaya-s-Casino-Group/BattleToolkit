using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    public sealed class HandGuideFourOfAKindTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player");
        private static BattleAdminCard Card(int rank, int suit = 1) => new BattleAdminCard(definitionId: "standard:" + suit + ":" + rank);
        private static BattleSession Setup(int min, int max, BattleAdminCard[] hand,
            BattleAdminCard[] deck = null, BattleAdminCard[] discard = null)
        {
            var xml = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/SingleChapterBattle.xml"));
            var player = xml.Root.Element("Players").Elements("Player").Single(item => (string)item.Attribute("id") == "hero");
            player.SetAttributeValue("minPlayCards", min); player.SetAttributeValue("maxPlayCards", max);
            var content = BattleContentCompiler.CompileXml(xml.ToString());
            Assert.IsTrue(content.Success, string.Join(";", content.Diagnostics.Select(d => d.Message)));
            var session = BattleContentFactory.Create(content.Content, new BattleSetupRequest("single-chapter-battle-1", "four-prefix",
                HmacDeckShuffler.FromSeed(new byte[32]))).Session;
            Assert.IsTrue(session.Execute(0, Player, CardDuelAction.Start).Accepted);
            var changed = session.ApplyAdmin(session.State.Revision, new BattleAdminPatch(zones: new[] {
                new BattleAdminZone(Player, CardZoneKinds.Hand, hand),
                new BattleAdminZone(Player, CardZoneKinds.Deck, deck ?? Array.Empty<BattleAdminCard>()),
                new BattleAdminZone(Player, CardZoneKinds.Discard, discard ?? Array.Empty<BattleAdminCard>()) }));
            Assert.IsTrue(changed.Accepted, changed.Error.ToString());
            return session;
        }
        private static HandGuideResult Query(BattleSession session, int budget = 200000) =>
            session.Engine.QueryHandGuide(session.State, Player, new HandGuideRequest(HandGuideType.FourOfAKind, nodeBudget: budget));
        private static bool Prefix(IEnumerable<HandGuideCard> cards, int size) => (bool)typeof(CardDuelEngine)
            .GetMethod("GuidePrefixPossible", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { cards.ToList(), new HandGuideRequest(HandGuideType.FourOfAKind), size, 5, 5 });

        [TestCase(true)] [TestCase(false)]
        public void FiveCardMinimumPreservesQuadAndKickerAtEitherEnd(bool kickerFirst)
        {
            var quad = new[] { Card(7), Card(7,2), Card(7,3), Card(7,4) };
            var held = kickerFirst ? new[] { Card(14) }.Concat(quad).ToArray() : quad.Concat(new[] { Card(14) }).ToArray();
            var session = Setup(5, 5, held); var before = session.State;
            var result = Query(session); var candidate = result.Candidates.Single();
            Assert.AreEqual(HandGuideAvailability.Complete, result.Availability); Assert.IsFalse(result.Truncated);
            Assert.AreEqual(5, candidate.Cards.Count); Assert.AreEqual(0, candidate.MissingCount);
            Assert.AreEqual(Rank.Ace, (kickerFirst ? candidate.Cards.First() : candidate.Cards.Last()).Face.Rank,
                "The kicker must actually appear at the tested end of the stable instance-ID search order.");
            for (int count = 1; count <= candidate.Cards.Count; count++) Assert.IsTrue(Prefix(candidate.Cards.Take(count), 5));
            var preview = session.Engine.PreviewHandGuide(before, candidate);
            Assert.AreEqual(CardDuelError.None, preview.Error); Assert.AreEqual(HandCategory.FourOfAKind, preview.Hand.Pattern.Category);
            Assert.AreSame(before, session.State);
        }
        [Test] public void FourCardWitnessAndEmptyFiveCardRootRemainReachable()
        {
            Assert.IsTrue(Prefix(Array.Empty<HandGuideCard>(), 5));
            var session = Setup(1, 5, new[] { Card(7), Card(7,2), Card(7,3), Card(7,4), Card(14) });
            var result = Query(session);
            Assert.AreEqual(HandGuideAvailability.Complete, result.Availability);
            Assert.AreEqual(4, result.Candidates.Single().Cards.Count, "Do not require a kicker when four cards are legal.");
        }
        [Test] public void TwoMissingCardsAcrossDeckAndDiscardCompleteAFiveCardQuad()
        {
            var session = Setup(5, 5, new[] { Card(7), Card(7,2), Card(14) }, new[] { Card(7,3) }, new[] { Card(7,4) });
            var result = Query(session); var candidate = result.Candidates.Single();
            Assert.AreEqual(HandGuideAvailability.Completable, result.Availability); Assert.IsFalse(result.Truncated);
            Assert.AreEqual(5, candidate.Cards.Count); Assert.AreEqual(2, candidate.MissingCount);
            Assert.AreEqual(4, candidate.Cards.Count(c => c.Face.Rank == Rank.Seven));
            CollectionAssert.AreEquivalent(new[] { CardZoneKinds.Deck, CardZoneKinds.Discard }, candidate.Cards.Where(c => c.IsMissing).Select(c => c.ZoneKind));
        }
        [Test] public void FiveCardPrefixesRejectInsufficientRemainingRankSlots()
        {
            var session = Setup(5, 5, new[] { Card(2), Card(2,2), Card(2,3), Card(2,4), Card(3), Card(4) });
            var cards = session.Engine.QueryHandGuide(session.State, Player, new HandGuideRequest(HandGuideType.Any)).Candidates
                .SelectMany(c => c.Cards).GroupBy(c => c.Card.Id).Select(g => g.First()).ToArray();
            var twos = cards.Where(c => c.Face.Rank == Rank.Two).ToArray();
            var three = cards.Single(c => c.Face.Rank == Rank.Three); var four = cards.Single(c => c.Face.Rank == Rank.Four);
            Assert.IsFalse(Prefix(new[] { twos[0], three, four }, 5));
            Assert.IsFalse(Prefix(new[] { twos[0], twos[1], three, four }, 5));
            Assert.IsTrue(Prefix(twos.Take(3).Concat(new[] { three }), 5));
            Assert.IsTrue(Prefix(twos, 5));
        }
        [Test] public void ExhaustedBudgetRemainsUnknownAndIncrementalSearchKeepsTheSameCandidates()
        {
            var session = Setup(5, 5, new[] { Card(7), Card(7,2), Card(14) }, new[] { Card(7,3) }, new[] { Card(7,4) });
            var limited = Query(session, 1);
            Assert.IsTrue(limited.Truncated); Assert.AreEqual(HandGuideAvailability.SearchLimited, limited.Availability);
            Assert.AreEqual(1, limited.VisitedNodes); Assert.IsEmpty(limited.Candidates);
            var request = new HandGuideRequest(HandGuideType.FourOfAKind);
            var incremental = session.Engine.QueryHandGuideIncremental(session.State, Player, request, nodesPerStep: 1).Last(result => result != null);
            var complete = Query(session);
            CollectionAssert.AreEqual(complete.Candidates.Select(c => c.Id), incremental.Candidates.Select(c => c.Id));
            Assert.AreEqual(complete.Availability, incremental.Availability); Assert.AreEqual(complete.VisitedNodes, incremental.VisitedNodes);
        }
        [Test] public void SixCardSelectionsRetainTheConservativeExpandedBoundary()
        {
            var session = Setup(6, 6, new[] { Card(7), Card(7,2), Card(7,3), Card(7,4), Card(14), Card(14,2) });
            var result = Query(session); var candidate = result.Candidates.Single();
            Assert.AreEqual(HandGuideAvailability.Complete, result.Availability); Assert.AreEqual(6, candidate.Cards.Count);
            Assert.IsTrue(Prefix(candidate.Cards, 6), "The pair kicker must not activate <=5-card duplicate pruning.");
        }
    }
}
