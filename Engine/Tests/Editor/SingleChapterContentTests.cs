using System;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class SingleChapterContentTests
    {
        private const string BattlePath = "Engine/Content/SingleChapterBattle.xml";
        private const string AdventurePath = "Engine/Content/SingleChapterAdventure.xml";

        private static CompiledBattleContent Content(bool fastBattles = false)
        {
            var xml = XDocument.Parse(ToolkitTestFiles.Read(BattlePath));
            if (fastBattles)
            {
                // Keep the shipped route and economic content intact. Only replace combat opposition
                // for this route test; encounter balance and enemy skills belong to battle tests.
                foreach (var enemy in xml.Root.Element("Enemies").Elements())
                {
                    enemy.RemoveNodes();
                    enemy.SetAttributeValue("maxHealth", 1);
                    enemy.SetAttributeValue("damage", 0);
                }
                xml.Root.Element("ScoringProfiles").Element("Scoring").Elements("Hand")
                    .Single(h => (string)h.Attribute("category") == "HighCard").SetAttributeValue("multiplier", 10);
            }
            var compiled = BattleContentCompiler.CompileXml(xml.ToString());
            Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics.Select(d => d.Message)));
            return compiled.Content;
        }

        private static BattleAdventureSession Session(bool fastBattles = false)
        {
            var content = Content(fastBattles);
            return new BattleAdventureSession(BattleAdventureXml.Load(ToolkitTestFiles.Read(AdventurePath), content),
                content, "single-chapter-content", 312);
        }

        private static void Command(BattleAdventureSession session, AdventureCommandKind kind, string id = null)
        {
            var result = session.Execute(session.Revision, new AdventureCommand(kind, id));
            Assert.IsTrue(result.Accepted, kind + ": " + result.Error);
        }

        private static BattleAdventureSession Restored(BattleAdventureSession session)
        {
            var saved = session.Export();
            var restored = BattleAdventureSession.Restore(session.Definition, session.Content, saved);
            Assert.AreEqual(saved, restored.Export(), "Every checkpoint must restore without rerolling outcomes or granting rewards again.");
            return restored;
        }

        [Test]
        public void ShippedContentCompilesWithOneChapterSevenRowsAndExplicitItemQualities()
        {
            var s = Session();
            Assert.AreEqual(1, s.Definition.TotalChapters);
            Assert.AreEqual(2, s.Definition.EconomyVersion);
            Assert.AreEqual(4, s.Definition.ShopOfferCount);
            Assert.AreEqual(20, s.Definition.RestHeal);
            Assert.AreEqual(9, s.Definition.Nodes.Count, "Two supply rows each have two alternatives.");
            CollectionAssert.AreEqual(Enumerable.Range(0, 7), s.Definition.Nodes.Select(n => n.Row).Distinct().OrderBy(r => r));
            CollectionAssert.AreEquivalent(new[] { "shop-1", "rest-1" }, s.Definition.Node("battle-2").NextIds);
            CollectionAssert.AreEquivalent(new[] { "shop-2", "rest-2" }, s.Definition.Node("elite").NextIds);
            Assert.AreEqual(50, s.Definition.Node("battle-1").Gold);
            Assert.AreEqual(100, s.Definition.Node("chest").Gold);
            Assert.AreEqual(60, s.Definition.Node("battle-2").Gold);
            Assert.AreEqual(800, s.Definition.Node("elite").Gold);
            Assert.IsEmpty(s.Definition.Node("boss").NextIds);
            Assert.AreEqual(12, s.Content.Relics.Count);
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 6), s.Content.Relics.Values.Select(r => r.Definition.Quality).Distinct());
            foreach (var node in s.Definition.Nodes.Where(n => n.EncounterId != null))
            {
                var rules = s.Content.BattleRules[s.Content.Encounters[node.EncounterId].RulesId].Rules;
                Assert.AreEqual(node.Kind == AdventureNodeKind.Battle ? 10 : 20, rules.RoundLimit, node.Id);
                Assert.AreEqual(BattleRoundLimitBehavior.Lose, rules.RoundLimitBehavior, node.Id);
            }
            var first = s.Content.Enemies["rotator"].Behavior.Stages.Single().Actions;
            var second = s.Content.Enemies["rotator-strong"].Behavior.Stages.Single().Actions;
            Assert.AreEqual(2, first.Count);
            Assert.AreEqual(1.25m, s.Definition.Node("battle-2").EnemyHealthMultiplier);
            for (int i = 0; i < first.Count; i++)
            {
                Assert.AreEqual(first[i].Id, second[i].Id);
                Assert.AreEqual(CombatEffectKind.Damage, first[i].Effects.Steps.Single().Effect.Kind);
                Assert.AreEqual((long)decimal.Floor(first[i].Effects.Steps.Single().Effect.Amount * 1.25m),
                    second[i].Effects.Steps.Single().Effect.Amount);
            }
        }

        [TestCase("shop-1", "shop-2")]
        [TestCase("shop-1", "rest-2")]
        [TestCase("rest-1", "shop-2")]
        [TestCase("rest-1", "rest-2")]
        public void AllSupplyCombinationsCompleteSevenStopsAndReplayEveryCheckpoint(string firstSupply, string secondSupply)
        {
            var s = Session(fastBattles: true);
            string[] route = { "battle-1", "chest", "battle-2", firstSupply, "elite", secondSupply, "boss" };
            foreach (string id in route)
            {
                Assert.AreEqual(AdventurePhase.Map, s.Phase, id);
                Assert.IsTrue(s.AvailableNodes.Any(n => n.Id == id), id);
                if (id.StartsWith("rest", StringComparison.Ordinal))
                    Assert.IsTrue(s.ApplyAdmin(s.Revision, new BattleAdminPatch(
                        new[] { new BattleAdminVitals(s.PlayerId, health: 100) })).Accepted);
                long beforeGold = s.Gold;
                var enter = new AdventureCommand(AdventureCommandKind.EnterNode, id);
                string before = s.Export();
                var preview = s.Execute(s.Revision, enter, preview: true);
                Assert.IsTrue(preview.Accepted, preview.Error);
                Assert.AreEqual(before, s.Export());
                Command(s, AdventureCommandKind.EnterNode, id);
                Assert.AreEqual(preview.Session.Export(), s.Export());
                s = Restored(s);

                if (s.Phase == AdventurePhase.Battle)
                {
                    long combatGold = s.Gold;
                    var win = s.ExecuteBattle(s.Revision, CardDuelAction.Play,
                        s.Battle.State.Zone(CardZoneKinds.Hand).Cards.Take(1),
                        new[] { new ParticipantId("enemy") });
                    Assert.IsTrue(win.Accepted, win.Error);
                    Assert.AreEqual(AdventurePhase.Reward, s.Phase, id);
                    Assert.AreEqual(combatGold + s.CurrentNode.Gold, s.Gold);
                    Assert.AreEqual(s.CurrentNode.Gold, s.LastRewardGold);
                }
                else if (s.Phase == AdventurePhase.Chest)
                {
                    Assert.AreEqual(beforeGold + 100, s.Gold);
                    Assert.AreEqual(1, s.LastLocationReward.GrantedRelicIds.Count);
                    Assert.AreEqual(1, s.Relics.Count);
                }
                else if (s.Phase == AdventurePhase.Shop)
                {
                    Assert.AreEqual(4, s.ShopItems.Count);
                    Assert.AreEqual(4, s.ShopItems.Select(o => o.Id).Distinct().Count());
                    var stock = s.ShopItems.Select(o => o.Id + ":" + o.RelicId + ":" + o.Price).ToArray();
                    var item = s.ShopItems.OrderBy(o => o.Price).First();
                    if (s.Relics.Count < s.Definition.RelicCapacity && s.Gold >= item.Price)
                    {
                        Command(s, AdventureCommandKind.BuyRelic, item.Id);
                        Assert.IsTrue(s.PurchasedItemIds.Contains(item.Id));
                        Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.BuyRelic, item.Id)).Accepted);
                    }
                    CollectionAssert.AreEqual(stock, s.ShopItems.Select(o => o.Id + ":" + o.RelicId + ":" + o.Price));
                    if (id == "shop-1")
                    {
                        int count = s.Deck.Count;
                        Command(s, AdventureCommandKind.DuplicateCard, s.Deck[0].Id);
                        Assert.AreEqual(count + 1, s.Deck.Count);
                    }
                }
                else if (s.Phase == AdventurePhase.Rest)
                {
                    Assert.AreEqual(20, s.LastLocationReward.HealthDelta);
                    Assert.IsTrue(s.CanContinue, "The player may leave after the base heal without drawing.");
                    if (id == "rest-1")
                    {
                        Command(s, AdventureCommandKind.DrawRestReward);
                        Assert.IsTrue(s.RestLotteryUsed);
                        Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.DrawRestReward)).Accepted);
                    }
                }
                else Assert.Fail("Unexpected location phase: " + s.Phase);

                while (s.PendingRelicRewards.Count > 0) Command(s, AdventureCommandKind.SkipRelicReward);
                s = Restored(s);
                int deckSize = s.Deck.Count;
                Command(s, AdventureCommandKind.Continue);
                Assert.AreEqual(deckSize, s.Deck.Count, "Permanent cards survive leaving a location.");
                s = Restored(s);
            }
            Assert.AreEqual(AdventurePhase.Completed, s.Phase);
            CollectionAssert.AreEqual(route, s.VisitedNodeIds);
            Assert.AreEqual(1, s.CompletedChapters);
            Assert.AreEqual(1, s.Progression.Settlements.Count);
            Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.Continue)).Accepted);
        }
    }
}
