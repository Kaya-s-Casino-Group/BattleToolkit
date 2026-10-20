using System;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleAdventureAdminTests
    {
        private static BattleAdventureSession Session()
        {
            var content = BattleContentCompiler.CompileXml(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle-2026-10-21.xml")).Content;
            var xml = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/StandardAdventure.xml"));
            xml.Root.SetAttributeValue("startNode", "shop");
            xml.Root.Element("Nodes").ReplaceNodes(XElement.Parse("<Nodes>" +
                "<Node id='shop' kind='Shop' chapter='1' next='fight'/>" +
                "<Node id='fight' kind='Battle' chapter='1' encounter='standard-single-battle' gold='25' next='rest'/>" +
                "<Node id='rest' kind='Rest' chapter='1' next='boss'/>" +
                "<Node id='boss' kind='Boss' chapter='1' encounter='standard-gluttony-boss' gold='50'/>" + "</Nodes>").Elements());
            return new BattleAdventureSession(BattleAdventureXml.Load(xml.ToString(), content), content, "admin-adventure", 55);
        }
        private static void Command(BattleAdventureSession session, AdventureCommandKind kind, string id = null)
        { var result = session.Execute(session.Revision, new AdventureCommand(kind, id)); Assert.IsTrue(result.Accepted, result.Error); }
        private static void EnterFight(BattleAdventureSession s)
        { Command(s, AdventureCommandKind.EnterNode, "shop"); Command(s, AdventureCommandKind.Continue); Command(s, AdventureCommandKind.EnterNode, "fight"); }
        private static void AssertReplay(BattleAdventureSession s)
        {
            var saved = s.Export(); var restored = BattleAdventureSession.Restore(s.Definition, s.Content, saved);
            Assert.AreEqual(saved, restored.Export(), "Administrator patches must replay deterministically, including generated IDs and card faces.");
        }
        [Test] public void PersistentEditsPreviewAtomicallyAndRestoreWithoutSpendingOrUsingRng()
        {
            var s = Session(); string before = s.Export(); var player = s.PlayerId;
            var patch = new BattleAdminPatch(new[] { new BattleAdminVitals(player, 4000000000L, 5000000000L) },
                relics: new[] { new BattleAdminRelics(player, new[] { new BattleAdminRelic("11004", charges: 2) }) },
                gold: new[] { new BattleAdminGold(player, 9007199254740993L) });
            var preview = s.ApplyAdmin(s.Revision, patch, preview: true);
            Assert.IsTrue(preview.Accepted, preview.Error); Assert.AreEqual(before, s.Export());
            Assert.AreEqual(9007199254740993L, preview.Session.Gold);
            var applied = s.ApplyAdmin(s.Revision, patch); Assert.IsTrue(applied.Accepted, applied.Error);
            Assert.AreEqual(preview.Session.Export(), s.Export()); AssertReplay(s);
            EnterFight(s);
            Assert.AreEqual(s.Gold, s.Battle.State.GoldFor(player)); Assert.AreEqual(5000000000L, s.Battle.State.Duel.Combat.Get(player).MaxHealth);
            Assert.AreEqual(2, s.Battle.State.Duel.Player(player).Relics.Relics.Single().RemainingCharges);
        }
        [Test] public void BattleAdminChangesAreSavedButCardsNeverBecomePermanent()
        {
            var s = Session(); EnterFight(s); var player = s.PlayerId; var originalDeck = s.Deck.Select(c => c.Id).ToArray();
            var state = s.Battle.State; var random = state.Random.StateBytes.ToArray(); int energy = state.Duel.Player(player).Energy.Energy;
            var patch = new BattleAdminPatch(new[] { new BattleAdminVitals(player, 321, 1200, 77) },
                new[] { new BattleAdminZone(player, CardZoneKinds.Hand, new[] { new BattleAdminCard(definitionId: "standard:1:14", effects: CardEffects.Unplayable) }) },
                new[] { new BattleAdminRelics(player, new[] { new BattleAdminRelic("10002") }) }, new[] { new BattleAdminGold(player, 777) });
            var result = s.ApplyAdmin(s.Revision, patch); Assert.IsTrue(result.Accepted, result.Error);
            Assert.AreEqual(321, s.Health); Assert.AreEqual(1200, s.MaxHealth); Assert.AreEqual(777, s.Gold);
            Assert.AreEqual(energy, s.Battle.State.Duel.Player(player).Energy.Energy);
            CollectionAssert.AreEqual(random, s.Battle.State.Random.StateBytes);
            Assert.AreEqual(1, s.Battle.State.Zone(CardZoneKinds.Hand).Cards.Count); CollectionAssert.AreEqual(originalDeck, s.Deck.Select(c => c.Id));
            AssertReplay(s);
            var enemyKills = s.Battle.State.Duel.Enemies.Select(e => new BattleAdminVitals(e.Id, health: 0)).ToArray();
            Assert.IsTrue(s.ApplyAdmin(s.Revision, new BattleAdminPatch(enemyKills)).Accepted);
            Assert.AreEqual(AdventurePhase.Reward, s.Phase); Assert.AreEqual(777, s.Gold); Assert.AreEqual(0, s.LastRewardGold);
            Command(s, AdventureCommandKind.Continue); Command(s, AdventureCommandKind.EnterNode, "rest");
            Command(s, AdventureCommandKind.ChooseRest, "rest-gold"); Command(s, AdventureCommandKind.Continue);
            Command(s, AdventureCommandKind.EnterNode, "boss");
            Assert.AreEqual(8, s.Battle.State.Zone(CardZoneKinds.Hand).Cards.Count);
            Assert.IsFalse(s.Battle.State.Table.Cards.Any(c => (c.Effects & CardEffects.Unplayable) != 0)); AssertReplay(s);
        }
        [Test] public void IllegalMixedPatchDoesNotPartiallyChangeGoldOrHealth()
        {
            var s = Session(); var before = s.Export();
            var patch = new BattleAdminPatch(new[] { new BattleAdminVitals(s.PlayerId, 50) },
                new[] { new BattleAdminZone(s.PlayerId, CardZoneKinds.Hand, Array.Empty<BattleAdminCard>()) },
                gold: new[] { new BattleAdminGold(s.PlayerId, 50) });
            Assert.IsFalse(s.ApplyAdmin(s.Revision, patch).Accepted); Assert.AreEqual(before, s.Export());
            EnterFight(s); before = s.Export();
            patch = new BattleAdminPatch(new[] { new BattleAdminVitals(s.PlayerId, 50) },
                relics: new[] { new BattleAdminRelics(s.PlayerId, new[] { new BattleAdminRelic("not-configured") }) },
                gold: new[] { new BattleAdminGold(s.PlayerId, 500) });
            Assert.IsFalse(s.ApplyAdmin(s.Revision, patch).Accepted); Assert.AreEqual(before, s.Export());
        }
        [Test] public void DirectZeroHealthDoesNotConsumeReviveAndTerminalRejectsFurtherEdits()
        {
            var s = Session(); EnterFight(s); var player = s.PlayerId;
            var setup = new BattleAdminPatch(relics: new[] { new BattleAdminRelics(player, new[] { new BattleAdminRelic("13004") }) });
            Assert.IsTrue(s.ApplyAdmin(s.Revision, setup).Accepted);
            var result = s.ApplyAdmin(s.Revision, new BattleAdminPatch(new[] { new BattleAdminVitals(player, 0) }));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(AdventurePhase.Failed, s.Phase); Assert.AreEqual(0, s.Health);
            Assert.AreEqual(1, s.Relics.Single().Charges); AssertReplay(s);
            Assert.IsFalse(s.ApplyAdmin(s.Revision, new BattleAdminPatch(new[] { new BattleAdminVitals(player, 10) })).Accepted);
        }
        [Test] public void AdminEndingBattleClearsActiveRouletteBeforeNextBattle()
        {
            var s = Session(); EnterFight(s); var player = s.PlayerId;
            Assert.IsTrue(s.ApplyAdmin(s.Revision, new BattleAdminPatch(relics: new[] { new BattleAdminRelics(player,
                new[] { new BattleAdminRelic("15001"), new BattleAdminRelic("13004") }) })).Accepted);
            var roulette = s.Relics.Single(r => r.DefinitionId == "15001");
            Assert.IsTrue(s.UseBattleItem(s.Revision, roulette.Id).Accepted);
            // This fixed seed samples a later explosion, so it verifies actual active-state cleanup.
            Assert.AreEqual(1, s.Relics.Single(r => r.Id == roulette.Id).Memory.CounterValue(BattleItemRuntime.RouletteActiveKey));
            Assert.IsTrue(s.ApplyAdmin(s.Revision, new BattleAdminPatch(s.Battle.State.Duel.Enemies.Select(e => new BattleAdminVitals(e.Id, 0)))).Accepted);
            Assert.AreEqual(0, s.Relics.Single(r => r.Id == roulette.Id).Memory.CounterValue(BattleItemRuntime.RouletteActiveKey));
            AssertReplay(s);
        }
        [Test] public void ExistingPersistentRelicKeepsInstanceAndChargesWhenEditingGold()
        {
            var s = Session(); var player = s.PlayerId;
            Assert.IsTrue(s.ApplyAdmin(s.Revision, new BattleAdminPatch(relics: new[] { new BattleAdminRelics(player, new[] { new BattleAdminRelic("11004", charges: 1) }) })).Accepted);
            var relic = s.Relics.Single();
            var patch = new BattleAdminPatch(relics: new[] { new BattleAdminRelics(player, new[] { new BattleAdminRelic(relic.DefinitionId, relic.Id) }) }, gold: new[] { new BattleAdminGold(player, 55) });
            Assert.IsFalse(s.ApplyAdmin(s.Revision - 1, patch).Accepted); Assert.IsTrue(s.ApplyAdmin(s.Revision, patch).Accepted);
            Assert.AreEqual(relic.Id, s.Relics.Single().Id); Assert.AreEqual(1, s.Relics.Single().Charges); AssertReplay(s);
        }
    }
}
