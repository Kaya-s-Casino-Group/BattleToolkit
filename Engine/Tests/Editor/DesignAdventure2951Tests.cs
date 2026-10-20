using System;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class DesignAdventure2951Tests
    {
        private const string TwoFights =
            "<Node id='f1' kind='Battle' chapter='1' encounter='standard-single-battle' next='f2'/>" +
            "<Node id='f2' kind='Battle' chapter='1' encounter='standard-single-battle'/>";

        private static BattleAdventureSession Session(string[] items, string route = TwoFights,
            long initialHealth = 100, long maxHealth = 800, long enemyHealth = 1, Action<XDocument> configure = null)
        {
            var xml = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle-2026-10-21.xml"));
            var player = xml.Root.Element("Players").Elements().Single(p => (string)p.Attribute("id") == "hero");
            player.SetAttributeValue("maxHealth", maxHealth);
            var enemy = xml.Root.Element("Enemies").Elements().Single(e => (string)e.Attribute("id") == "rotator");
            enemy.RemoveNodes(); enemy.SetAttributeValue("maxHealth", enemyHealth); enemy.SetAttributeValue("damage", 0);
            var loadout = xml.Root.Element("Loadouts").Elements().Single(l => (string)l.Attribute("id") == "baseline");
            loadout.ReplaceNodes(items.Select((id, i) => new XElement("Relic", new XAttribute("ref", id), new XAttribute("instanceId", "test-item-" + i))));
            xml.Root.Element("Encounters").Elements().Single(e => (string)e.Attribute("id") == "standard-single-battle")
                .Element("Player").SetAttributeValue("initialHealth", initialHealth);
            configure?.Invoke(xml);
            var compiled = BattleContentCompiler.CompileXml(xml.ToString());
            Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics.Select(d => d.Message)));

            var adventure = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/StandardAdventure.xml"));
            adventure.Root.Element("Nodes").ReplaceNodes(XElement.Parse("<Nodes>" + route + "</Nodes>").Elements());
            adventure.Root.Element("Nodes").Elements().Last().SetAttributeValue("kind", "Boss");
            adventure.Root.SetAttributeValue("startNode", (string)adventure.Root.Element("Nodes").Elements().First().Attribute("id"));
            adventure.Root.SetAttributeValue("relicCapacity", 32);
            adventure.Root.Element("Tarots").Elements().Single(t => (string)t.Attribute("id") == "fool").SetAttributeValue("maxHealthMultiplier", 1);
            var definition = BattleAdventureXml.Load(adventure.ToString(), compiled.Content);
            return new BattleAdventureSession(definition, compiled.Content, "adventure2951", 23);
        }
        private static void Command(BattleAdventureSession s, AdventureCommandKind kind, string id = null)
        {
            var result = s.Execute(s.Revision, new AdventureCommand(kind, id));
            Assert.IsTrue(result.Accepted, kind + ": " + result.Error);
        }
        private static string Item(BattleAdventureSession s, string id) => s.Relics.First(r => r.DefinitionId == id).Id;
        private static AdventureResult Use(BattleAdventureSession s, string id, string target = null, bool preview = false)
        {
            var result = s.UseBattleItem(s.Revision, id, target, preview);
            Assert.IsTrue(result.Accepted, "UseBattleItem: " + result.Error);
            return result;
        }
        private static void Win(BattleAdventureSession s)
        {
            var result = s.ExecuteBattle(s.Revision, CardDuelAction.Play, s.Battle.State.Zone(CardZoneKinds.Hand).Cards.Take(1));
            Assert.IsTrue(result.Accepted, result.Error);
            Assert.AreEqual(AdventurePhase.Reward, s.Phase);
        }
        private static BattleAdventureSession Restore(BattleAdventureSession s)
        {
            var restored = BattleAdventureSession.Restore(s.Definition, s.Content, s.Export());
            Assert.AreEqual(s.Export(), restored.Export());
            return restored;
        }

        [Test] public void ItemPreviewCommitAndRestoredContinuationAreIdenticalWithoutDoubleConsumption()
        {
            var s = Session(new[] { "10002", "10002" });
            Command(s, AdventureCommandKind.EnterNode, "f1");
            string before = s.Export(), potion = Item(s, "10002");
            var preview = Use(s, potion, preview: true);
            Assert.AreEqual(before, s.Export());
            Assert.AreEqual(110, preview.Session.Health);
            Assert.AreEqual(1, preview.Session.Relics.Count(r => r.DefinitionId == "10002"));
            var committed = Use(s, potion);
            Assert.AreEqual(preview.Session.Export(), committed.Session.Export());
            Assert.AreEqual(3, s.Battle.State.FlowFor(s.PlayerId).ActionsRemaining);
            var restored = Restore(s);
            var other = Item(s, "10002");
            Use(s, other); Use(restored, other);
            Assert.AreEqual(s.Export(), restored.Export());
            Assert.AreEqual(120, s.Health);
            Assert.IsEmpty(s.Relics);
            before = s.Export();
            Assert.IsFalse(s.UseBattleItem(s.Revision, potion).Accepted);
            Assert.AreEqual(before, s.Export());
            Win(s); Command(s, AdventureCommandKind.Continue); Command(s, AdventureCommandKind.EnterNode, "f2");
            Assert.IsEmpty(s.Relics);
            Assert.IsEmpty(s.Battle.State.Duel.Player(s.PlayerId).Relics.Relics);
        }

        [Test] public void EachGrenadeKeepsItsOwnRemainingChargesAcrossBattlesAndIsRemovedOnThirdUse()
        {
            string route = string.Concat(Enumerable.Range(1, 3).Select(i =>
                "<Node id='f" + i + "' kind='Battle' chapter='1' encounter='standard-single-battle'" +
                (i < 3 ? " next='f" + (i + 1) + "'" : "") + "/>"));
            var s = Session(new[] { "11004", "11004" }, route);
            var first = s.Relics[0].Id; var untouched = s.Relics[1].Id;
            for (int fight = 1; fight <= 3; fight++)
            {
                Command(s, AdventureCommandKind.EnterNode, "f" + fight);
                Assert.AreEqual(4 - fight, s.Battle.State.Duel.Player(s.PlayerId).Relics.Relics.Single(r => r.InstanceId == first).RemainingCharges);
                Use(s, first);
                Assert.AreEqual(AdventurePhase.Reward, s.Phase);
                Assert.AreEqual(3, s.Relics.Single(r => r.Id == untouched).Charges);
                if (fight < 3) Assert.AreEqual(3 - fight, s.Relics.Single(r => r.Id == first).Charges);
                else Assert.IsFalse(s.Relics.Any(r => r.Id == first));
                s = Restore(s);
                if (fight < 3) Command(s, AdventureCommandKind.Continue);
            }
        }

        [Test] public void DiceCooldownCountsCurrentCompletedBattleButNotShopAndRest()
        {
            string route =
                "<Node id='f1' kind='Battle' chapter='1' encounter='standard-single-battle' next='shop'/>" +
                "<Node id='shop' kind='Shop' chapter='1' next='rest'/>" +
                "<Node id='rest' kind='Rest' chapter='1' next='f2'/>" +
                string.Concat(Enumerable.Range(2, 6).Select(i =>
                    "<Node id='f" + i + "' kind='Battle' chapter='1' encounter='standard-single-battle'" +
                    (i < 7 ? " next='f" + (i + 1) + "'" : "") + "/>"));
            var s = Session(new[] { "14001", "10002" }, route);
            var dice = Item(s, "14001"); var potion = Item(s, "10002");
            Command(s, AdventureCommandKind.EnterNode, "f1");
            string before = s.Export();
            var preview = Use(s, dice, potion, preview: true);
            Assert.AreEqual(before, s.Export());
            Use(s, dice, potion);
            Assert.AreEqual(preview.Session.Export(), s.Export());
            Assert.IsFalse(s.Relics.Any(r => r.Id == potion));
            Assert.AreEqual(6, s.Relics.Single(r => r.Id == dice).Memory.CounterValue(BattleItemRuntime.CooldownBattlesKey));
            s = Restore(s);
            Win(s);
            Assert.AreEqual(5, s.Relics.Single(r => r.Id == dice).Memory.CounterValue(BattleItemRuntime.CooldownBattlesKey));
            Command(s, AdventureCommandKind.Continue); Command(s, AdventureCommandKind.EnterNode, "shop");
            Assert.AreEqual(5, s.Relics.Single(r => r.Id == dice).Memory.CounterValue(BattleItemRuntime.CooldownBattlesKey));
            Command(s, AdventureCommandKind.Continue); Command(s, AdventureCommandKind.EnterNode, "rest");
            Command(s, AdventureCommandKind.ChooseRest, "rest-gold"); Command(s, AdventureCommandKind.Continue);
            Assert.AreEqual(5, s.Relics.Single(r => r.Id == dice).Memory.CounterValue(BattleItemRuntime.CooldownBattlesKey));
            for (int fight = 2; fight <= 6; fight++)
            {
                Command(s, AdventureCommandKind.EnterNode, "f" + fight);
                Assert.IsFalse(BattleItemRuntime.CanUse(s.Battle.State, s.PlayerId, dice));
                Win(s);
                Assert.AreEqual(6 - fight, s.Relics.Single(r => r.Id == dice).Memory.CounterValue(BattleItemRuntime.CooldownBattlesKey));
                s = Restore(s); Command(s, AdventureCommandKind.Continue);
            }
            Command(s, AdventureCommandKind.EnterNode, "f7");
            Assert.IsTrue(BattleItemRuntime.CanUse(s.Battle.State, s.PlayerId, dice));
        }

        [Test] public void LocationItemsTriggerOnceOnBattleShopAndRestIncludingPreviewAndRestore()
        {
            const string route =
                "<Node id='f1' kind='Battle' chapter='1' encounter='standard-single-battle' next='shop'/>" +
                "<Node id='shop' kind='Shop' chapter='1' next='rest'/>" +
                "<Node id='rest' kind='Rest' chapter='1' next='f2'/>" +
                "<Node id='f2' kind='Battle' chapter='1' encounter='standard-single-battle'/>";
            var s = Session(new[] { "13001", "11003" }, route);
            var preview = s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.EnterNode, "f1"), preview: true);
            Assert.IsTrue(preview.Accepted, preview.Error);
            Assert.AreEqual(100, s.Health); Assert.AreEqual(103, preview.Session.Health);
            Command(s, AdventureCommandKind.EnterNode, "f1");
            Assert.AreEqual(preview.Session.Export(), s.Export());
            Assert.AreEqual(103, s.Battle.State.Duel.Combat.Get(s.PlayerId).Health);
            Win(s); Command(s, AdventureCommandKind.Continue);
            Assert.AreEqual(33, s.Gold);
            Command(s, AdventureCommandKind.EnterNode, "shop");
            Assert.AreEqual(106, s.Health);
            string saved = s.Export();
            Assert.IsFalse(s.Execute(s.Revision, new AdventureCommand(AdventureCommandKind.EnterNode, "shop")).Accepted);
            Assert.AreEqual(saved, s.Export());
            s = Restore(s);
            Command(s, AdventureCommandKind.Continue); Assert.AreEqual(36, s.Gold);
            Command(s, AdventureCommandKind.EnterNode, "rest"); Assert.AreEqual(109, s.Health);
            Command(s, AdventureCommandKind.ChooseRest, "rest-gold"); Assert.AreEqual(56, s.Gold);
            Command(s, AdventureCommandKind.Continue); Assert.AreEqual(61, s.Gold);
            Command(s, AdventureCommandKind.EnterNode, "f2"); Assert.AreEqual(112, s.Health);
            Assert.AreEqual(112, s.Battle.State.Duel.Combat.Get(s.PlayerId).Health);
        }

        [Test] public void LongHealthSurvivesItemUseBattleCompletionAndSavedContinuation()
        {
            var s = Session(new[] { "10002" }, initialHealth: 8000000000L, maxHealth: 9000000000L);
            Assert.AreEqual(8000000000L, s.Health); Assert.AreEqual(9000000000L, s.MaxHealth);
            Command(s, AdventureCommandKind.EnterNode, "f1");
            Use(s, Item(s, "10002"));
            Assert.AreEqual(8000000010L, s.Battle.State.Duel.Combat.Get(s.PlayerId).Health);
            s = Restore(s); Win(s); Command(s, AdventureCommandKind.Continue);
            s = Restore(s); Command(s, AdventureCommandKind.EnterNode, "f2");
            Assert.AreEqual(8000000010L, s.Health);
            Assert.AreEqual(9000000000L, s.Battle.State.Duel.Combat.Get(s.PlayerId).MaxHealth);
            Restore(s);
        }

        [Test] public void OpeningRevivalImmediatelySynchronizesRunItemsAndRestoredBattleBeforeFirstAction()
        {
            var s = Session(new[] { "10001", "13004" }, configure: xml =>
                xml.Root.Element("Relics").Elements().Single(r => (string)r.Attribute("id") == "10001")
                    .Add(new XElement("Program", new XAttribute("id", "opening-self-loss"),
                        new XAttribute("kind", "TurnHealthLoss"), new XAttribute("amount", 200))));
            string revive = Item(s, "13004");
            Command(s, AdventureCommandKind.EnterNode, "f1");

            Assert.AreEqual(AdventurePhase.Battle, s.Phase);
            Assert.AreEqual(480, s.Health);
            Assert.AreEqual(0, s.Battle.State.Duel.CompletedRounds);
            Assert.AreEqual(3, s.Battle.State.FlowFor(s.PlayerId).ActionsRemaining);
            Assert.IsFalse(s.Relics.Any(r => r.Id == revive));
            CollectionAssert.AreEqual(s.Relics.Select(r => r.Id),
                s.Battle.State.Duel.Player(s.PlayerId).Relics.Relics.Select(r => r.InstanceId));

            var restored = Restore(s);
            Assert.AreEqual(480, restored.Health);
            Assert.IsFalse(restored.Relics.Any(r => r.Id == revive));
            CollectionAssert.AreEqual(restored.Relics.Select(r => r.Id),
                restored.Battle.State.Duel.Player(restored.PlayerId).Relics.Relics.Select(r => r.InstanceId));
        }
    }
}
