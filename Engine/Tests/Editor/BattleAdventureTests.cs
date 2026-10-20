using System;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleAdventureTests
    {
        private static string BattleXml(Action<XDocument> edit = null)
        {
            var doc = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle.xml"));
            edit?.Invoke(doc); return doc.ToString();
        }
        private static CompiledBattleContent Content(Action<XDocument> edit = null)
        {
            var result = BattleContentCompiler.CompileXml(BattleXml(edit));
            Assert.IsTrue(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
            return result.Content;
        }
        private static void Easy(XDocument doc)
        {
            foreach (var enemy in doc.Root.Element("Enemies").Elements())
            { enemy.SetAttributeValue("maxHealth", 1); enemy.SetAttributeValue("damage", 0); enemy.RemoveNodes(); }
        }
        private static void RoyalDeck(XDocument doc)
        {
            var deck = doc.Root.Element("Decks").Elements().Single(e => (string)e.Attribute("id") == "standard-deck");
            deck.ReplaceNodes(Enumerable.Range(10, 5).Select((rank, i) =>
                new XElement("Card", new XAttribute("id", "royal-" + i), new XAttribute("face", "standard:1:" + rank))));
        }
        private static string AdventureXml(string nodes = null, Action<XDocument> edit = null)
        {
            var doc = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/StandardAdventure.xml"));
            if (nodes != null)
            {
                doc.Root.Element("Nodes").ReplaceNodes(XElement.Parse("<Nodes>" + nodes + "</Nodes>").Elements());
                doc.Root.SetAttributeValue("startNode", (string)doc.Root.Element("Nodes").Elements().First().Attribute("id"));
            }
            edit?.Invoke(doc); return doc.ToString();
        }
        private const string ShortRoute =
            "<Node id='fight' kind='Battle' chapter='1' encounter='standard-single-battle' gold='20' next='shop'/>" +
            "<Node id='shop' kind='Shop' chapter='1' next='rest'/>" +
            "<Node id='rest' kind='Rest' chapter='1' next='boss'/>" +
            "<Node id='boss' kind='Boss' chapter='1' encounter='standard-gluttony-boss' gold='50'/>";
        private static BattleAdventureSession Session(CompiledBattleContent content = null, string route = ShortRoute,
            Action<XDocument> edit = null, string id = "adventure-test", ulong seed = 1, PlayerProgressionState progression = null, string tarot = null)
        {
            content = content ?? Content(Easy);
            var definition = BattleAdventureXml.Load(AdventureXml(route, edit), content);
            return new BattleAdventureSession(definition, content, id, seed, tarot, progression);
        }
        private static AdventureResult Command(BattleAdventureSession session, AdventureCommandKind kind, string id = null, Suit? suit = null)
        {
            var result = session.Execute(session.Revision, new AdventureCommand(kind, id, suit));
            Assert.IsTrue(result.Accepted, kind + ": " + result.Error); return result;
        }
        private static AdventureResult Battle(BattleAdventureSession session, CardDuelAction action, CardInstanceId[] cards = null)
        {
            var result = session.ExecuteBattle(session.Revision, action, cards);
            Assert.IsTrue(result.Accepted, action + ": " + result.Error); return result;
        }
        private static void Win(BattleAdventureSession s)
        {
            if (s.Battle.State.FlowFor(s.PlayerId).Window == BattlePlayerWindow.Draw) Battle(s, CardDuelAction.SkipDraw);
            Battle(s, CardDuelAction.Play, s.Battle.State.Zone(CardZoneKinds.Hand).Cards.Take(1).ToArray());
            Assert.AreEqual(AdventurePhase.Reward, s.Phase);
        }
        private static void EnterShop(BattleAdventureSession s)
        { Command(s, AdventureCommandKind.EnterNode, "fight"); Win(s); Command(s, AdventureCommandKind.Continue); Command(s, AdventureCommandKind.EnterNode, "shop"); }
        private static void Finish(BattleAdventureSession s)
        {
            while (s.Phase != AdventurePhase.Completed && s.Phase != AdventurePhase.Failed)
            {
                if (s.Phase == AdventurePhase.Map)
                {
                    Assert.AreEqual(s.AvailableNodes.First().Chapter - 1, s.ChapterIndex, "Map must show the next reachable chapter.");
                    Command(s, AdventureCommandKind.EnterNode, s.AvailableNodes.First().Id);
                }
                else if (s.Phase == AdventurePhase.Battle) Win(s);
                else if (s.Phase == AdventurePhase.Rest && s.RestChoiceId == null) Command(s, AdventureCommandKind.ChooseRest, "rest-gold");
                else Command(s, AdventureCommandKind.Continue);
            }
        }
        [Test] public void StandardDefinesThreeChaptersAndSevenNodesWithConfigurableGrowth()
        {
            var c = Content(); var d = BattleAdventureXml.Load(AdventureXml(), c);
            Assert.AreEqual(3, d.TotalChapters); Assert.AreEqual(21, d.Nodes.Count);
            foreach (var chapter in d.Nodes.GroupBy(n => n.Chapter))
            {
                Assert.AreEqual(7, chapter.Count()); Assert.AreEqual(3, chapter.Count(n => n.Kind == AdventureNodeKind.Battle));
                foreach (var kind in new[] { AdventureNodeKind.Elite, AdventureNodeKind.Boss, AdventureNodeKind.Shop, AdventureNodeKind.Rest })
                    Assert.AreEqual(1, chapter.Count(n => n.Kind == kind));
            }
            Assert.AreEqual(1.25m, d.Node("chapter-2-battle-1").EnemyHealthMultiplier);
            Assert.AreEqual(1.5m, d.Node("chapter-3-battle-1").EnemyHealthMultiplier);
            var s = new BattleAdventureSession(d, c, "standard", 1);
            Assert.AreEqual(960, s.MaxHealth); Assert.AreEqual(960, s.Health); Assert.AreEqual("fool", s.TarotId);
            Assert.AreEqual(1, s.UnlockedTarots.Count);
        }
        [Test] public void StandardBattleStartsWithRealRulesAndPreviewDoesNotCommit()
        {
            var s = Session(Content()); string before = s.Export();
            var result = s.Execute(0, new AdventureCommand(AdventureCommandKind.EnterNode, "fight"), true);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(before, s.Export()); Assert.AreEqual(0, s.Revision);
            Assert.AreEqual(AdventurePhase.Battle, result.Session.Phase); Assert.AreEqual(8, result.Session.Battle.State.Zone(CardZoneKinds.Hand).Cards.Count);
            Assert.AreEqual(3, result.Session.Battle.State.FlowFor(s.PlayerId).ActionsRemaining);
            Assert.IsNotNull(result.BattleResult);
            var restored = BattleAdventureSession.Restore(s.Definition, s.Content, result.Session.Export());
            Assert.AreEqual(result.Session.Export(), restored.Export());
        }
        [Test] public void IllegalNodesStaleRevisionsAndBattleCommandsDoNotMutate()
        {
            var s = Session(); var before = s.Export();
            Assert.IsFalse(s.Execute(0, new AdventureCommand(AdventureCommandKind.EnterNode, "boss")).Accepted);
            Assert.IsFalse(s.Execute(1, new AdventureCommand(AdventureCommandKind.EnterNode, "fight")).Accepted);
            Assert.IsFalse(s.ExecuteBattle(0, CardDuelAction.Play).Accepted);
            Assert.AreEqual(before, s.Export());
            Command(s, AdventureCommandKind.EnterNode, "fight"); before = s.Export();
            foreach (var action in new[] { CardDuelAction.Start, CardDuelAction.EnemyAction, CardDuelAction.CollectPlayed, CardDuelAction.SkipPlayerTurn })
                Assert.IsFalse(s.ExecuteBattle(s.Revision, action).Accepted, action.ToString());
            Assert.IsFalse(s.ExecuteBattle(s.Revision, CardDuelAction.Play, new[] { new CardInstanceId("forged") }).Accepted);
            Assert.AreEqual(before, s.Export());
        }
        [Test] public void BattlePreviewAndForkLeaveAuthorityAndRandomnessUnchanged()
        {
            var s = Session(); Command(s, AdventureCommandKind.EnterNode, "fight");
            Battle(s, CardDuelAction.SkipDraw); var before = s.Export();
            var cards = s.Battle.State.Zone(CardZoneKinds.Hand).Cards.Take(1).ToArray();
            var preview = s.ExecuteBattle(s.Revision, CardDuelAction.Play, cards, preview: true);
            Assert.IsTrue(preview.Accepted); Assert.AreEqual(AdventurePhase.Reward, preview.Session.Phase); Assert.AreEqual(before, s.Export());
            var fork = s.Fork(); Battle(fork, CardDuelAction.Play, cards); Assert.AreEqual(before, s.Export());
            Assert.AreEqual(preview.Session.Export(), fork.Export());
            // Public Battle is a read/preview wrapper, not a way to bypass the adventure command ledger.
            var exposed = s.Battle; exposed.Execute(exposed.State.Revision, s.PlayerId, CardDuelAction.Play, cards);
            Assert.AreEqual(before, s.Export());
        }
        [Test] public void VictoryPaysExactlyOnceAndWaitsOnRewardBeforeNextNode()
        {
            var s = Session(); Command(s, AdventureCommandKind.EnterNode, "fight"); Win(s);
            Assert.AreEqual(50, s.Gold); Assert.AreEqual(20, s.LastRewardGold); Assert.IsEmpty(s.AvailableNodes);
            var saved = s.Export(); Assert.IsFalse(s.ExecuteBattle(s.Revision, CardDuelAction.EndTurn).Accepted);
            Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.EnterNode, "shop")).Accepted); Assert.AreEqual(saved, s.Export());
            Command(s, AdventureCommandKind.Continue); Assert.AreEqual(AdventurePhase.Map, s.Phase);
            Assert.AreEqual("shop", s.AvailableNodes.Single().Id); Assert.AreEqual(50, s.Gold); Assert.AreEqual("fight", s.VisitedNodeIds.Single());
        }
        [Test] public void ShopCardServicesPreserveRankEffectsAndIncreaseAcrossShopNodes()
        {
            const string route = "<Node id='shop' kind='Shop' chapter='1' next='shop2'/><Node id='shop2' kind='Shop' chapter='1' next='boss'/>" +
                "<Node id='boss' kind='Boss' chapter='1' encounter='standard-gluttony-boss'/>";
            var s = Session(route: route, edit: d => d.Root.SetAttributeValue("startingGold", 1000));
            Command(s, AdventureCommandKind.EnterNode, "shop"); var original = s.Deck[0]; int count = s.Deck.Count;
            Command(s, AdventureCommandKind.DuplicateCard, original.Id); Assert.AreEqual(count + 1, s.Deck.Count);
            var duplicate = s.Deck.Last(); Assert.AreNotEqual(original.Id, duplicate.Id); Assert.AreEqual(original.DefinitionId, duplicate.DefinitionId);
            Assert.AreEqual(30, s.ServicePrice(AdventureCommandKind.DuplicateCard)); Assert.AreEqual(980, s.Gold);
            var source = StandardDeck.Cards().Single(c => c.Id == original.DefinitionId);
            var targetSuit = source.Suit == Suit.Clubs ? Suit.Hearts : Suit.Clubs;
            Command(s, AdventureCommandKind.ChangeSuit, original.Id, targetSuit);
            Assert.AreEqual(StandardDeck.CardId(targetSuit, source.Rank), s.Deck.Single(c => c.Id == original.Id).DefinitionId);
            Command(s, AdventureCommandKind.DeleteCard, duplicate.Id); Assert.AreEqual(count, s.Deck.Count);
            Command(s, AdventureCommandKind.Continue); Command(s, AdventureCommandKind.EnterNode, "shop2");
            Assert.AreEqual(30, s.ServicePrice(AdventureCommandKind.DuplicateCard)); Assert.AreEqual(15, s.ServicePrice(AdventureCommandKind.ChangeSuit));
            Command(s, AdventureCommandKind.DuplicateCard, original.Id); Assert.AreEqual(40, s.ServicePrice(AdventureCommandKind.DuplicateCard));
            var restored = BattleAdventureSession.Restore(s.Definition, s.Content, s.Export());
            Assert.AreEqual(s.Export(), restored.Export()); Assert.AreEqual(2, restored.ServiceUses(AdventureCommandKind.DuplicateCard));
        }
        [Test] public void InvalidShopOperationsAreAtomicIncludingInsufficientGold()
        {
            var s = Session(edit: d => d.Root.SetAttributeValue("startingGold", 0)); EnterShop(s);
            string before = s.Export();
            Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.BuyRelic, "shop-flush")).Accepted);
            Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.ChangeSuit, s.Deck[0].Id)).Accepted);
            Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.DeleteCard, "forged")).Accepted);
            Assert.AreEqual(before, s.Export());
            // Duplicate is affordable once; the next failure must not append a card or increment service use.
            Command(s, AdventureCommandKind.DuplicateCard, s.Deck[0].Id); before = s.Export();
            Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.DuplicateCard, s.Deck[0].Id)).Accepted);
            Assert.AreEqual(before, s.Export());
        }
        [Test] public void ShopRelicsUseAcquisitionOrderSingleStockAndSellValue()
        {
            var s = Session(edit: d => d.Root.SetAttributeValue("startingGold", 500)); EnterShop(s);
            Command(s, AdventureCommandKind.BuyRelic, "shop-flush"); Command(s, AdventureCommandKind.BuyRelic, "shop-straight");
            CollectionAssert.AreEqual(new[] { "flush-overheal", "straight-dot" }, s.Relics.Select(r => r.DefinitionId));
            Assert.AreEqual(2, s.BoughtShopItemIds.Count); long gold = s.Gold;
            Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.BuyRelic, "shop-flush")).Accepted);
            Command(s, AdventureCommandKind.SellRelic, s.Relics[0].Id); Assert.AreEqual(gold + 15, s.Gold);
            Assert.AreEqual("straight-dot", s.Relics.Single().DefinitionId);
            Command(s, AdventureCommandKind.Continue); Command(s, AdventureCommandKind.EnterNode, "rest");
            Command(s, AdventureCommandKind.ChooseRest, "rest-gold"); Command(s, AdventureCommandKind.Continue); Command(s, AdventureCommandKind.EnterNode, "boss");
            Assert.AreEqual(s.Relics.Single().Id, s.Battle.State.Duel.Player(s.PlayerId).Relics.Relics.Single().InstanceId);
        }
        [Test] public void RestRequiresExactlyOneChoiceAndContinueDoesNotGrantAgain()
        {
            var s = Session(); EnterShop(s); Command(s, AdventureCommandKind.Continue); Command(s, AdventureCommandKind.EnterNode, "rest");
            string before = s.Export(); Assert.IsFalse(s.CanContinue);
            Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.Continue)).Accepted); Assert.AreEqual(before, s.Export());
            long gold = s.Gold; Command(s, AdventureCommandKind.ChooseRest, "rest-gold");
            Assert.IsTrue(s.CanContinue); Assert.AreEqual(gold + 20, s.Gold);
            Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.ChooseRest, "rest-heal")).Accepted);
            Command(s, AdventureCommandKind.Continue); Assert.AreEqual(gold + 20, s.Gold);
        }
        [Test] public void HealthCarriesButBossJunkAndBattleStateDoNotBecomePermanentDeck()
        {
            var content = Content(d =>
            {
                RoyalDeck(d);
                // The second encounter's template block must not become cross-battle player shield.
                d.Root.Element("Players").Elements().First().SetAttributeValue("initialBlock", 99);
                d.Descendants("Action").Single(e => (string)e.Attribute("id") == "pollute").Element("Effects").Add(
                    new XElement("Effect", new XAttribute("id", "poison"), new XAttribute("kind", "ApplyStatus"),
                        new XAttribute("statusRef", "bleed"), new XAttribute("stacks", 3), new XAttribute("target", "Opponents")));
                d.Descendants("Hand").Single(e => (string)e.Attribute("variant") == "Royal").AddFirst(
                    new XElement("Effect", new XAttribute("id", "block"), new XAttribute("kind", "GainBlock"),
                        new XAttribute("amount", 50), new XAttribute("target", "Self")));
            });
            const string route = "<Node id='boss1' kind='Boss' chapter='1' encounter='standard-gluttony-boss' next='boss2'/>" +
                "<Node id='boss2' kind='Boss' chapter='2' encounter='standard-gluttony-boss'/>";
            var s = Session(content, route); Command(s, AdventureCommandKind.EnterNode, "boss1");
            Assert.AreEqual(0, s.Battle.State.Duel.Combat.Get(s.PlayerId).Block);
            Battle(s, CardDuelAction.SkipDraw); Battle(s, CardDuelAction.EndTurn);
            Assert.Less(s.Health, s.MaxHealth); Assert.AreEqual(5, s.Deck.Count); Assert.AreEqual(6, s.Battle.State.Table.Cards.Count);
            Battle(s, CardDuelAction.SkipDraw);
            Battle(s, CardDuelAction.Play, s.Battle.State.Zone(CardZoneKinds.Hand).Cards.ToArray());
            Assert.AreEqual(AdventurePhase.Reward, s.Phase); int health = s.Health;
            Assert.IsNotEmpty(s.Battle.State.Duel.Combat.Statuses); Assert.AreEqual(50, s.Battle.State.Duel.Combat.Get(s.PlayerId).Block);
            Command(s, AdventureCommandKind.Continue); Command(s, AdventureCommandKind.EnterNode, "boss2");
            Assert.AreEqual(health, s.Health); Assert.AreEqual(health, s.Battle.State.Duel.Combat.Get(s.PlayerId).Health);
            Assert.AreEqual(5, s.Battle.State.Table.Cards.Count); Assert.AreEqual(0, s.Battle.State.Duel.Combat.Get(s.PlayerId).Block);
            Assert.IsEmpty(s.Battle.State.Duel.Combat.Statuses);
        }
        [Test] public void ChapterHealthMultiplierAppliesToConfiguredEnemyAndSummonCatalog()
        {
            var s = Session(Content(), "<Node id='boss' kind='Boss' chapter='1' encounter='standard-armored-summoner' enemyHealthMultiplier='1.5'/>");
            Command(s, AdventureCommandKind.EnterNode, "boss");
            var enemy = s.Battle.State.Duel.Enemies.First();
            Assert.AreEqual((int)(s.Content.Enemies["summoner"].Definition.MaxHealth * 1.5m), enemy.Definition.MaxHealth);
            foreach (var pair in enemy.SummonCatalog) Assert.AreEqual((int)(s.Content.Enemies[pair.Key].Definition.MaxHealth * 1.5m), pair.Value.Definition.MaxHealth);
        }
        [Test] public void CompleteRunUnlocksConfiguredTarotsOnlyOnceAndNextRunSelectsOne()
        {
            var s = Session(); Finish(s); Assert.AreEqual(AdventurePhase.Completed, s.Phase);
            Assert.AreEqual(1, s.CompletedChapters); Assert.AreEqual(1, s.Progression.Statistics.Wins); Assert.AreEqual(3, s.UnlockedTarots.Count);
            var restored = BattleAdventureSession.Restore(s.Definition, s.Content, s.Export());
            Assert.AreEqual(1, restored.Progression.Statistics.Wins); Assert.AreEqual(s.Export(), restored.Export());
            var next = new BattleAdventureSession(s.Definition, s.Content, "next", 2, "empress", restored.Progression);
            Assert.AreEqual(800, next.MaxHealth); Assert.AreEqual(70, next.Gold);
            Assert.Throws<ArgumentException>(() => new BattleAdventureSession(s.Definition, s.Content, s.RunId, 2, progression: s.Progression));
            var magician = new BattleAdventureSession(s.Definition, s.Content, "next-mage", 2, "magician", s.Progression);
            Assert.AreEqual("straight-dot", magician.Relics.Single().DefinitionId); Assert.AreEqual(800, magician.MaxHealth);
        }
        [Test] public void DeathAndAbandonSettleLossWithoutUnlockingTarots()
        {
            var c = Content(d =>
            {
                foreach (var enemy in d.Root.Element("Enemies").Elements()) { enemy.RemoveNodes(); enemy.SetAttributeValue("damage", 10000); }
            });
            var s = Session(c); Command(s, AdventureCommandKind.EnterNode, "fight"); Battle(s, CardDuelAction.SkipDraw); Battle(s, CardDuelAction.EndTurn);
            Assert.AreEqual(AdventurePhase.Failed, s.Phase); Assert.AreEqual(0, s.Health); Assert.AreEqual(1, s.UnlockedTarots.Count);
            Assert.AreEqual(1, s.Progression.Statistics.Losses);
            Assert.AreEqual(s.Export(), BattleAdventureSession.Restore(s.Definition, s.Content, s.Export()).Export());
            var abandoned = Session(); Command(abandoned, AdventureCommandKind.Abandon);
            Assert.AreEqual(1, abandoned.Progression.Statistics.Abandoned); Assert.AreEqual(1, abandoned.UnlockedTarots.Count);
            Assert.IsFalse(abandoned.Execute(abandoned.Revision, new AdventureCommand(AdventureCommandKind.EnterNode, "fight")).Accepted);
        }
        [Test] public void SaveRoundtripResumesSameBattleAndNextCommandDeterministically()
        {
            var s = Session(Content()); Command(s, AdventureCommandKind.EnterNode, "fight"); Battle(s, CardDuelAction.SkipDraw);
            string saved = s.Export(); var restored = BattleAdventureSession.Restore(s.Definition, s.Content, saved);
            Assert.AreEqual(saved, restored.Export()); var cards = s.Battle.State.Zone(CardZoneKinds.Hand).Cards.Take(1).ToArray();
            Battle(s, CardDuelAction.Play, cards); Battle(restored, CardDuelAction.Play, cards);
            Assert.AreEqual(s.Export(), restored.Export());
        }
        [Test] public void SnapshotRejectsChangedContentRevisionDigestAndIllegalBattleAction()
        {
            var s = Session(); Command(s, AdventureCommandKind.EnterNode, "fight"); Battle(s, CardDuelAction.SkipDraw);
            string saved = s.Export();
            Assert.Throws<SnapshotException>(() => BattleAdventureSession.Restore(s.Definition, s.Content, saved.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":9")));
            Assert.Throws<SnapshotException>(() => BattleAdventureSession.Restore(s.Definition, s.Content, saved.Replace("\"Revision\":2", "\"Revision\":999")));
            Assert.Throws<SnapshotException>(() => BattleAdventureSession.Restore(s.Definition, s.Content, saved.Replace("\"Action\":9", "\"Action\":3")));
            Assert.Throws<SnapshotException>(() => BattleAdventureSession.Restore(s.Definition, s.Content, saved.Replace("\"ResultDigest\":\"", "\"ResultDigest\":\"x")));
            var changed = Content(d => Easy(d));
            var changedDefinition = BattleAdventureXml.Load(AdventureXml(ShortRoute, d => d.Root.SetAttributeValue("startingGold", 99)), changed);
            Assert.Throws<SnapshotException>(() => BattleAdventureSession.Restore(changedDefinition, changed, saved));
            Assert.Throws<SnapshotException>(() => BattleAdventureSession.Restore(s.Definition, s.Content, new string('x', BattleAdventureSession.MaxSnapshotBytes + 1)));
            Assert.Throws<SnapshotException>(() => BattleAdventureSession.Restore(s.Definition, s.Content, saved + "{}"));
        }
        [Test] public void FullThreeChapterRouteSettlesAllChaptersAndSameSeedDrawsSameCards()
        {
            var c = Content(Easy); var d = BattleAdventureXml.Load(AdventureXml(), c);
            var s = new BattleAdventureSession(d, c, "three-chapters", 9);
            var copy = new BattleAdventureSession(d, c, "other-identity", 9);
            Command(s, AdventureCommandKind.EnterNode, d.StartNodeId); Command(copy, AdventureCommandKind.EnterNode, d.StartNodeId);
            CollectionAssert.AreEqual(s.Battle.State.Zone(CardZoneKinds.Hand).Cards.Select(id => s.Battle.State.Table.GetCard(id).DefinitionId),
                copy.Battle.State.Zone(CardZoneKinds.Hand).Cards.Select(id => copy.Battle.State.Table.GetCard(id).DefinitionId));
            Finish(s); Assert.AreEqual(3, s.CompletedChapters); Assert.AreEqual(21, s.VisitedNodeIds.Count);
            Assert.AreEqual(3, s.Progression.Settlements.Single().CompletedFloors);
            Assert.AreEqual(s.Export(), BattleAdventureSession.Restore(d, c, s.Export()).Export());
        }
        [Test] public void RestHealingUsesConfiguredFractionAndCannotExceedHealthCap()
        {
            var c = Content(d => d.Root.Element("Encounters").Elements().First().Element("Player").SetAttributeValue("initialHealth", 200));
            const string route = "<Node id='rest' kind='Rest' chapter='1' next='boss'/><Node id='boss' kind='Boss' chapter='1' encounter='standard-gluttony-boss'/>";
            var s = Session(c, route); Assert.AreEqual(240, s.Health); Command(s, AdventureCommandKind.EnterNode, "rest");
            var preview = s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.ChooseRest, "rest-heal"), true);
            Assert.IsTrue(preview.Accepted); Assert.AreEqual(528, preview.Session.Health); Assert.AreEqual(240, s.Health);
            var full = Session(route: route); Command(full, AdventureCommandKind.EnterNode, "rest");
            Command(full, AdventureCommandKind.ChooseRest, "rest-heal"); Assert.AreEqual(full.MaxHealth, full.Health);
        }
        [Test] public void LockedTarotCannotStartAndConfiguredDeckAndRelicCapsAreAtomic()
        {
            var c = Content(); var d = BattleAdventureXml.Load(AdventureXml(), c);
            Assert.Throws<ArgumentException>(() => new BattleAdventureSession(d, c, "locked", 1, "magician"));
            const string route = "<Node id='shop' kind='Shop' chapter='1' next='boss'/><Node id='boss' kind='Boss' chapter='1' encounter='standard-gluttony-boss'/>";
            var s = Session(c, route, x =>
            {
                x.Root.SetAttributeValue("startingGold", 1000); x.Root.SetAttributeValue("relicCapacity", 0);
                x.Root.SetAttributeValue("minimumDeckSize", 52); x.Root.SetAttributeValue("maximumDeckSize", 52);
            });
            Command(s, AdventureCommandKind.EnterNode, "shop"); string before = s.Export();
            foreach (var command in new[] { new AdventureCommand(AdventureCommandKind.BuyRelic, "shop-flush"),
                new AdventureCommand(AdventureCommandKind.DuplicateCard, s.Deck[0].Id), new AdventureCommand(AdventureCommandKind.DeleteCard, s.Deck[0].Id) })
                Assert.IsFalse(s.Execute(s.Revision, command).Accepted);
            Assert.AreEqual(before, s.Export());
        }
        [Test] public void ConfiguredLargeDeckOverflowCanDiscardMoreThanFiftyTwoAndRestore()
        {
            var content = Content(d =>
            {
                d.Root.Element("Decks").Elements().First().ReplaceNodes(Enumerable.Range(0, 80).Select(i =>
                    new XElement("Card", new XAttribute("id", "large-" + i), new XAttribute("face", "standard:1:2"))));
                d.Root.Element("HandRules").Elements().First().Elements("Hand").Single(h => (string)h.Attribute("category") == "HighCard").Add(
                    new XElement("Effect", new XAttribute("id", "draw-many"), new XAttribute("kind", "DrawCards"),
                        new XAttribute("amount", 80), new XAttribute("target", "Self")));
            });
            var s = Session(content); Command(s, AdventureCommandKind.EnterNode, "fight"); Battle(s, CardDuelAction.SkipDraw);
            Battle(s, CardDuelAction.Play, s.Battle.State.Zone(CardZoneKinds.Hand).Cards.Take(1).ToArray()); Battle(s, CardDuelAction.EndTurn);
            int required = s.Battle.Engine.RequiredDiscards(s.Battle.State, s.PlayerId);
            Assert.Greater(required, 52); Assert.AreEqual(BattlePlayerWindow.ForcedDiscard, s.Battle.State.FlowFor(s.PlayerId).Window);
            Battle(s, CardDuelAction.ConfirmOverflow, s.Battle.State.Zone(CardZoneKinds.Hand).Cards.Take(required).ToArray());
            Assert.AreEqual(s.Export(), BattleAdventureSession.Restore(s.Definition, s.Content, s.Export()).Export());
        }
        [TestCase("<Node id='a' kind='Battle' chapter='1' encounter='standard-single-battle' next='a'/>")]
        [TestCase("<Node id='a' kind='Battle' chapter='1' encounter='missing'/>")]
        [TestCase("<Node id='a' kind='Shop' chapter='1'/>")]
        [TestCase("<Node id='a' kind='Boss' chapter='1' encounter='standard-single-battle'/><Node id='b' kind='Boss' chapter='1' encounter='standard-single-battle'/>")]
        public void XmlRejectsCyclesUnknownReferencesNonBossTerminalsAndUnreachableNodes(string nodes)
        { Assert.Throws<ArgumentException>(() => BattleAdventureXml.Load(AdventureXml(nodes), Content())); }
        [Test] public void XmlRejectsDtdAndUnknownConfiguration()
        {
            var c = Content();
            Assert.Throws<System.Xml.XmlException>(() => BattleAdventureXml.Load("<!DOCTYPE AdventureContent [<!ENTITY x SYSTEM 'file:///etc/passwd'>]>" + AdventureXml(), c));
            Assert.Throws<ArgumentException>(() => BattleAdventureXml.Load(AdventureXml(ShortRoute, d => d.Root.SetAttributeValue("ignoredRule", 1)), c));
        }
    }
}
