using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class AdventureEconomyTests
    {
        private CompiledBattleContent content;
        private const string Boss = "<Node id='boss' kind='Boss' chapter='1' encounter='single-chapter-wrath'/>";
        [OneTimeSetUp] public void Compile()
        {
            var result = BattleContentCompiler.CompileXml(ToolkitTestFiles.Read("Engine/Content/SingleChapterBattle.xml"));
            Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
            content = result.Content;
        }
        private BattleAdventureDefinition Definition(string nodes, int capacity = 5, long gold = 10000, long heal = 20)
        {
            var doc = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/SingleChapterAdventure.xml"));
            doc.Root.Element("Nodes").ReplaceNodes(XElement.Parse("<Nodes>" + nodes + Boss + "</Nodes>").Elements());
            doc.Root.SetAttributeValue("startNode", (string)doc.Root.Element("Nodes").Elements().First().Attribute("id"));
            doc.Root.SetAttributeValue("relicCapacity", capacity); doc.Root.SetAttributeValue("startingGold", gold);
            doc.Root.SetAttributeValue("restHeal", heal);
            return BattleAdventureXml.Load(doc.ToString(), content);
        }
        private BattleAdventureSession Session(BattleAdventureDefinition definition, ulong seed = 1) =>
            new BattleAdventureSession(definition, content, "economy-test", seed);
        private static AdventureResult Command(BattleAdventureSession s, AdventureCommandKind kind, string id = null, Suit? suit = null)
        {
            var result = s.Execute(s.Revision, new AdventureCommand(kind, id, suit));
            Assert.IsTrue(result.Accepted, kind + ": " + result.Error); return result;
        }
        private static void Items(BattleAdventureSession s, params string[] ids)
        {
            var result = s.ApplyAdmin(s.Revision, new BattleAdminPatch(relics: new[] {
                new BattleAdminRelics(s.PlayerId, ids.Select(id => new BattleAdminRelic(id))) }));
            Assert.IsTrue(result.Accepted, result.Error);
        }
        private static void Health(BattleAdventureSession s, long health)
        {
            var result = s.ApplyAdmin(s.Revision, new BattleAdminPatch(vitals: new[] { new BattleAdminVitals(s.PlayerId, health) }));
            Assert.IsTrue(result.Accepted, result.Error);
        }
        private static BattleAdventureSession Restore(BattleAdventureSession s)
        {
            var restored = BattleAdventureSession.Restore(s.Definition, s.Content, s.Export());
            Assert.AreEqual(s.Export(), restored.Export()); return restored;
        }
        private static string[] Stock(BattleAdventureSession s) => s.ShopItems.Select(i => i.Id + ":" + i.RelicId + ":" + i.Price).ToArray();

        [Test] public void QualityIsExplicitAndIndependentOfInstanceLevel()
        {
            Assert.AreEqual(0, content.Relics["10002"].Definition.Quality);
            Assert.AreEqual(5, content.Relics["15001"].Definition.Quality);
            Assert.AreEqual(1, content.Relics["15001"].Definition.MaxLevel);
            var xml = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/SingleChapterBattle.xml"));
            xml.Root.Element("Relics").Elements().First().SetAttributeValue("quality", 6);
            Assert.IsFalse(BattleContentCompiler.CompileXml(xml.ToString()).Success);
        }
        [Test] public void ServicesMatchTwentyPurchaseCurveAndCountIndependentlyAcrossVisits()
        {
            var s = Session(Definition("<Node id='shop1' kind='Shop' chapter='1' next='shop2'/><Node id='shop2' kind='Shop' chapter='1' next='boss'/>", gold: 100000));
            Command(s, AdventureCommandKind.EnterNode, "shop1");
            long[] remove = {45,45,45,50,54,60,66,72,80,88,94,100,107,115,123,132,141,151,161,173};
            long[] duplicate = {20,20,20,22,24,27,29,32,35,39,42,45,48,51,55,58,63,67,72,77};
            long[] suit = {30,30,30,33,36,40,44,48,53,58,63,67,72,77,82,88,94,100,107,115};
            string changed = s.Deck[0].Id;
            for (int i = 0; i < 20; i++)
            {
                if (i == 10) { Command(s, AdventureCommandKind.Continue); Command(s, AdventureCommandKind.EnterNode, "shop2"); s = Restore(s); }
                Assert.AreEqual(remove[i], s.ServicePrice(AdventureCommandKind.DeleteCard), "delete " + (i + 1));
                Assert.AreEqual(duplicate[i], s.ServicePrice(AdventureCommandKind.DuplicateCard), "duplicate " + (i + 1));
                Assert.AreEqual(suit[i], s.ServicePrice(AdventureCommandKind.ChangeSuit), "suit " + (i + 1));
                long before = s.Gold;
                Command(s, AdventureCommandKind.DeleteCard, s.Deck.Last().Id);
                Assert.AreEqual(duplicate[i], s.ServicePrice(AdventureCommandKind.DuplicateCard));
                Command(s, AdventureCommandKind.DuplicateCard, changed);
                var face = StandardDeck.Cards().Single(c => c.Id == s.Deck.Single(d => d.Id == changed).DefinitionId);
                Command(s, AdventureCommandKind.ChangeSuit, changed, face.Suit == Suit.Clubs ? Suit.Hearts : Suit.Clubs);
                Assert.AreEqual(before - remove[i] - duplicate[i] - suit[i], s.Gold);
            }
            Assert.AreEqual(20, s.ServiceUses(AdventureCommandKind.DeleteCard));
            Restore(s);
        }
        [Test] public void ShopFiltersOwnedUniqueItemsAndNeverRefillsPurchasedSlots()
        {
            var definition = Definition("<Node id='shop' kind='Shop' chapter='1' next='boss'/>", capacity: 32);
            var s = Session(definition, 31);
            Items(s, content.Relics.Values.Where(r => r.Definition.MaxCopies == 1).Select(r => r.Definition.Id).ToArray());
            string before = s.Export();
            var preview = s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.EnterNode, "shop"), preview: true);
            Assert.IsTrue(preview.Accepted); Assert.AreEqual(before, s.Export());
            Command(s, AdventureCommandKind.EnterNode, "shop"); Assert.AreEqual(preview.Session.Export(), s.Export());
            Assert.AreEqual(4, s.ShopItems.Count); Assert.AreEqual(4, s.ShopItems.Select(i => i.Id).Distinct().Count());
            Assert.IsTrue(s.ShopItems.All(i => content.Relics[i.RelicId].Definition.MaxCopies > 1));
            var stock = Stock(s); var restored = Restore(s); CollectionAssert.AreEqual(stock, Stock(restored));
            string first = s.ShopItems[0].Id;
            Command(s, AdventureCommandKind.BuyRelic, first); CollectionAssert.AreEqual(stock, Stock(s));
            Assert.Contains(first, s.PurchasedItemIds.ToArray()); before = s.Export();
            Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.BuyRelic, first)).Accepted);
            Assert.AreEqual(before, s.Export()); Restore(s);
        }
        [Test] public void ShopPricesUseQualityAndRandomPriceIsStableThroughRestore()
        {
            var definition = Definition("<Node id='shop' kind='Shop' chapter='1' next='boss'/>");
            var observed = new HashSet<int>(); var variablePrices = new HashSet<long>();
            for (ulong seed = 0; seed < 60; seed++)
            {
                var s = Session(definition, seed); Command(s, AdventureCommandKind.EnterNode, "shop");
                foreach (var offer in s.ShopItems)
                {
                    int quality = content.Relics[offer.RelicId].Definition.Quality; observed.Add(quality);
                    if (quality == 5) { Assert.That(offer.Price, Is.InRange(50, 200)); variablePrices.Add(offer.Price); }
                    else Assert.AreEqual(new[] {10,20,30,50,100}[quality], offer.Price);
                }
                if (seed < 3) CollectionAssert.AreEqual(Stock(s), Stock(Restore(s)));
            }
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 6), observed); Assert.Greater(variablePrices.Count, 1);
        }
        [Test] public void ChestPaysOnceAndFullInventoryCanReplaceOrSkipWithoutReroll()
        {
            var definition = Definition("<Node id='chest' kind='Chest' chapter='1' gold='100' next='boss'/>", capacity: 1);
            var s = Session(definition); Items(s, "10001");
            string old = s.Relics.Single().Id; long gold = s.Gold;
            Command(s, AdventureCommandKind.EnterNode, "chest");
            Assert.AreEqual(AdventurePhase.Chest, s.Phase); Assert.AreEqual(gold + 100, s.Gold);
            Assert.AreEqual(1, s.PendingRelicRewards.Count); Assert.IsFalse(s.CanContinue);
            string granted = s.PendingRelicRewards.Single(); Assert.AreNotEqual("10001", granted);
            s = Restore(s); string before = s.Export();
            Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.EnterNode, "chest")).Accepted);
            Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.Continue)).Accepted);
            Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.ClaimRelicReward, "missing")).Accepted);
            Assert.AreEqual(before, s.Export());
            var skipped = s.Fork(); Command(skipped, AdventureCommandKind.SkipRelicReward);
            Assert.AreEqual("10001", skipped.Relics.Single().DefinitionId); Assert.IsTrue(skipped.CanContinue);
            Assert.IsEmpty(skipped.LastLocationReward.GrantedRelicIds); Assert.IsFalse(skipped.ObtainedRelicIds.Contains(granted)); Restore(skipped);
            Command(s, AdventureCommandKind.ClaimRelicReward, old);
            Assert.AreEqual(granted, s.Relics.Single().DefinitionId); Assert.IsEmpty(s.PendingRelicRewards);
            CollectionAssert.AreEquivalent(new[] {"10001", granted}, s.ObtainedRelicIds);
            Command(s, AdventureCommandKind.Continue); Assert.AreEqual(gold + 100, s.Gold); Restore(s);
        }
        [Test] public void DeclinedChestOfferIsNotAnAcquisitionAndCanBeOfferedAtALaterChest()
        {
            var s = Session(Definition("<Node id='chest1' kind='Chest' chapter='1' gold='100' next='chest2'/>" +
                "<Node id='chest2' kind='Chest' chapter='1' gold='100' next='boss'/>", capacity: 11));
            Items(s, content.Relics.Keys.Where(id => id != "15001").ToArray());
            Command(s, AdventureCommandKind.EnterNode, "chest1");
            CollectionAssert.AreEqual(new[] { "15001" }, s.PendingRelicRewards);
            Assert.IsFalse(s.ObtainedRelicIds.Contains("15001"));
            Command(s, AdventureCommandKind.SkipRelicReward);
            Assert.IsEmpty(s.LastLocationReward.GrantedRelicIds); Assert.IsFalse(s.ObtainedRelicIds.Contains("15001"));
            s = Restore(s); Command(s, AdventureCommandKind.Continue); Command(s, AdventureCommandKind.EnterNode, "chest2");
            CollectionAssert.AreEqual(new[] { "15001" }, s.PendingRelicRewards); Restore(s);
        }
        [Test] public void ChestExcludesPreviouslyLostItemsAndExhaustedPoolStillGrantsGold()
        {
            var s = Session(Definition("<Node id='chest' kind='Chest' chapter='1' gold='100' next='boss'/>", capacity: 32));
            var all = content.Relics.Keys.ToArray(); Items(s, all); Items(s);
            Assert.AreEqual(all.Length, s.ObtainedRelicIds.Count); Assert.IsEmpty(s.Relics);
            long gold = s.Gold; Command(s, AdventureCommandKind.EnterNode, "chest");
            Assert.AreEqual(gold + 100, s.Gold); Assert.IsEmpty(s.PendingRelicRewards);
            Assert.IsEmpty(s.LastLocationReward.GrantedRelicIds); Assert.IsTrue(s.CanContinue); Restore(s);
        }
        [Test] public void ZeroCapacityRewardCanBeDeclinedWithoutLosingTheOutcome()
        {
            var s = Session(Definition("<Node id='chest' kind='Chest' chapter='1' gold='100' next='boss'/>", capacity: 0));
            Command(s, AdventureCommandKind.EnterNode, "chest"); Assert.AreEqual(1, s.PendingRelicRewards.Count);
            string before = s.Export(); Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.ClaimRelicReward)).Accepted);
            Assert.AreEqual(before, s.Export()); Command(s, AdventureCommandKind.SkipRelicReward); Command(s, AdventureCommandKind.Continue); Restore(s);
        }
        [Test] public void RestHealsOnEntryOnceAndLotteryIsOptional()
        {
            var definition = Definition("<Node id='rest' kind='Rest' chapter='1' next='boss'/>");
            var s = Session(definition); Health(s, 100); Command(s, AdventureCommandKind.EnterNode, "rest");
            Assert.AreEqual(120, s.Health); Assert.AreEqual(20, s.LastLocationReward.HealthDelta); Assert.IsTrue(s.CanContinue);
            Assert.IsFalse(s.RestLotteryUsed); s = Restore(s); Assert.AreEqual(120, s.Health);
            Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.ChooseRest, "rest-gold")).Accepted);
            Command(s, AdventureCommandKind.Continue); Assert.AreEqual(120, s.Health);
            Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.DrawRestReward)).Accepted);
            s = Session(definition); Health(s, s.MaxHealth - 5); Command(s, AdventureCommandKind.EnterNode, "rest");
            Assert.AreEqual(s.MaxHealth, s.Health); Assert.AreEqual(5, s.LastLocationReward.HealthDelta);
        }
        [Test] public void LotteryHasAllOutcomesAndPreviewReplayCannotRerollOrRepeat()
        {
            var definition = Definition("<Node id='rest' kind='Rest' chapter='1' next='boss'/>", capacity: 5, gold: 10);
            var observed = new HashSet<AdventureLocationRewardKind>(); bool loss = false, gain = false;
            for (ulong seed = 0; seed < 100; seed++)
            {
                var s = Session(definition, seed); Command(s, AdventureCommandKind.EnterNode, "rest");
                string before = s.Export(); var preview = s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.DrawRestReward), preview: true);
                Assert.IsTrue(preview.Accepted); Assert.AreEqual(before, s.Export());
                Command(s, AdventureCommandKind.DrawRestReward); Assert.AreEqual(preview.Session.Export(), s.Export());
                Assert.IsTrue(s.RestLotteryUsed); observed.Add(s.LastLocationReward.Kind);
                foreach (var id in s.LastLocationReward.GrantedRelicIds)
                {
                    int quality = content.Relics[id].Definition.Quality;
                    if (s.LastLocationReward.Kind == AdventureLocationRewardKind.RestHighRelicGain) Assert.That(quality, Is.InRange(3, 4));
                    else Assert.That(quality, Is.InRange(0, 2));
                }
                Assert.GreaterOrEqual(s.Gold, 0);
                if (s.LastLocationReward.Kind == AdventureLocationRewardKind.RestGold)
                { loss |= s.LastLocationReward.GoldDelta < 0; gain |= s.LastLocationReward.GoldDelta > 0; }
                before = s.Export(); Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.DrawRestReward)).Accepted);
                Assert.AreEqual(before, s.Export()); if (seed < 10) Restore(s);
            }
            Assert.AreEqual(4, observed.Count); Assert.IsTrue(loss); Assert.IsTrue(gain);
        }
        [Test] public void HighRelicLossFallsBackToHighestOwnedQualityAndDoesNotForgetHistory()
        {
            var definition = Definition("<Node id='rest' kind='Rest' chapter='1' next='boss'/>");
            BattleAdventureSession chosen = null;
            for (ulong seed = 0; seed < 100 && chosen == null; seed++)
            {
                var s = Session(definition, seed); Items(s, "10001", "11002");
                Command(s, AdventureCommandKind.EnterNode, "rest"); Command(s, AdventureCommandKind.DrawRestReward);
                if (s.LastLocationReward.Kind == AdventureLocationRewardKind.RestHighRelicLoss) chosen = s;
            }
            Assert.IsNotNull(chosen); CollectionAssert.AreEqual(new[] {"11002"}, chosen.LastLocationReward.LostRelicIds);
            Assert.AreEqual("10001", chosen.Relics.Single().DefinitionId); Assert.Contains("11002", chosen.ObtainedRelicIds.ToArray()); Restore(chosen);
        }
        [Test] public void PendingUniqueRewardCannotStackAfterInventoryChanges()
        {
            var definition = Definition("<Node id='rest' kind='Rest' chapter='1' next='boss'/>", capacity: 2);
            BattleAdventureSession chosen = null;
            for (ulong seed = 0; seed < 100 && chosen == null; seed++)
            {
                var s = Session(definition, seed); Items(s, "10001", "11002");
                Command(s, AdventureCommandKind.EnterNode, "rest"); Command(s, AdventureCommandKind.DrawRestReward);
                if (s.LastLocationReward.Kind == AdventureLocationRewardKind.RestHighRelicGain && s.PendingRelicRewards.Count == 1 &&
                    content.Relics[s.PendingRelicRewards[0]].Definition.MaxCopies == 1) chosen = s;
            }
            Assert.IsNotNull(chosen); string pending = chosen.PendingRelicRewards.Single();
            Items(chosen, pending, "10001"); string before = chosen.Export();
            string other = chosen.Relics.Single(r => r.DefinitionId == "10001").Id;
            Assert.IsFalse(chosen.Execute(chosen.Revision, new AdventureCommand(AdventureCommandKind.ClaimRelicReward, other)).Accepted);
            Assert.AreEqual(before, chosen.Export());
            Command(chosen, AdventureCommandKind.ClaimRelicReward, chosen.Relics.Single(r => r.DefinitionId == pending).Id);
            Assert.AreEqual(1, chosen.Relics.Count(r => r.DefinitionId == pending)); Restore(chosen);
        }
        [Test] public void TwoLowRewardsAtZeroCapacityCanBothBeDeclinedAfterRestore()
        {
            var definition = Definition("<Node id='rest' kind='Rest' chapter='1' next='boss'/>", capacity: 0);
            BattleAdventureSession chosen = null;
            for (ulong seed = 0; seed < 100 && chosen == null; seed++)
            {
                var s = Session(definition, seed); Command(s, AdventureCommandKind.EnterNode, "rest"); Command(s, AdventureCommandKind.DrawRestReward);
                if (s.LastLocationReward.Kind == AdventureLocationRewardKind.RestLowRelics) chosen = s;
            }
            Assert.IsNotNull(chosen); Assert.AreEqual(2, chosen.PendingRelicRewards.Count);
            chosen = Restore(chosen); Command(chosen, AdventureCommandKind.SkipRelicReward); Assert.AreEqual(1, chosen.PendingRelicRewards.Count);
            Assert.IsFalse(chosen.CanContinue); chosen = Restore(chosen); Command(chosen, AdventureCommandKind.SkipRelicReward);
            Assert.IsTrue(chosen.CanContinue); Command(chosen, AdventureCommandKind.Continue); Restore(chosen);
        }
    }
}
