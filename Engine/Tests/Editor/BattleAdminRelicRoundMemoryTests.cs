using System;
using System.Linq;
using System.Reflection;
using System.Xml;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleAdminRelicRoundMemoryTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player");

        private static BattleSession Battle()
        {
            var document = new XmlDocument();
            document.LoadXml(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle-2026-10-21.xml"));
            document.SelectSingleNode("//Loadout[@id='baseline']").InnerXml =
                "<Relic ref='10001' instanceId='program'/><Relic ref='10002' instanceId='potion'/><Relic ref='11004' instanceId='grenade'/>";
            var enemy = (XmlElement)document.SelectSingleNode("//Enemy[@id='rotator']");
            enemy.InnerXml = "";
            enemy.SetAttribute("maxHealth", "1000000");
            enemy.SetAttribute("damage", "0");
            var content = BattleContentCompiler.CompileXml(document.OuterXml);
            Assert.IsTrue(content.Success, string.Join(";", content.Diagnostics.Select(d => d.Message)));
            var created = BattleContentFactory.Create(content.Content, new BattleSetupRequest("standard-single-battle",
                "admin-round-memory", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(created.Success, string.Join(";", created.Diagnostics.Select(d => d.Message)));
            Assert.IsTrue(created.Session.Execute(created.Session.State.Revision, Player, CardDuelAction.Start).Accepted);
            return created.Session;
        }

        private static BattleRelicState Relic(BattleRelicLoadout loadout, string definition) =>
            loadout.Relics.Single(r => r.DefinitionId == definition);

        private static string[] Memory(BattleRelicState relic) => relic.Memory.Counters
            .Select(c => c.Key + ":" + c.Value + ":" + c.Scope).ToArray();

        private static void PlayOne(BattleSession battle)
        {
            var result = battle.Execute(battle.State.Revision, Player, CardDuelAction.Play,
                new[] { battle.State.Zone(CardZoneKinds.Hand, Player).Cards.First() });
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            var program = Relic(battle.State.Duel.Player(Player).Relics, "10001");
            Assert.IsTrue(program.Memory.Counters.Any(c => c.Scope == MemoryScope.Round));
        }

        [Test]
        public void RelicEditAfterRealScoredHandPreservesFullMemoryAndOmittedRuntimeFields()
        {
            var battle = Battle();
            var loadout = battle.State.Duel.Player(Player).Relics;
            var configured = battle.ApplyAdmin(battle.State.Revision, new BattleAdminPatch(relics: new[] {
                new BattleAdminRelics(Player, loadout.Relics.Select(r => r.DefinitionId == "11004"
                    ? new BattleAdminRelic(r.DefinitionId, r.InstanceId, charges: 3, growth: 9, cooldownRounds: 2, usesThisRound: 1)
                    : new BattleAdminRelic(r.DefinitionId, r.InstanceId))) }));
            Assert.IsTrue(configured.Accepted, configured.Error.ToString());
            var used = battle.UseItem(battle.State.Revision, Player, Relic(battle.State.Duel.Player(Player).Relics, "11004").InstanceId);
            Assert.IsTrue(used.Accepted, used.Error.ToString());
            PlayOne(battle);
            var before = battle.State;
            loadout = before.Duel.Player(Player).Relics;
            var program = Relic(loadout, "10001");
            var grenade = Relic(loadout, "11004");
            Assert.IsTrue(grenade.Memory.Counters.Any(c => c.Scope == MemoryScope.Encounter));
            var patch = new BattleAdminPatch(relics: new[] { new BattleAdminRelics(Player, new[] {
                new BattleAdminRelic(grenade.DefinitionId, grenade.InstanceId),
                new BattleAdminRelic(program.DefinitionId, program.InstanceId),
                new BattleAdminRelic("13004") }) });

            var preview = battle.PreviewAdmin(before.Revision, patch);
            Assert.IsTrue(preview.Accepted, preview.Error.ToString());
            Assert.AreSame(before, battle.State, "Preview must not commit the edit.");
            var applied = battle.ApplyAdmin(before.Revision, patch);
            Assert.IsTrue(applied.Accepted, applied.Error.ToString());
            var after = battle.State.Duel.Player(Player).Relics;
            CollectionAssert.AreEqual(Memory(program), Memory(Relic(after, "10001")));
            CollectionAssert.AreEqual(Memory(grenade), Memory(Relic(after, "11004")));
            Assert.AreEqual(grenade.InstanceId, after.Relics[0].InstanceId);
            Assert.AreEqual(2, after.Relics[0].RemainingCharges);
            Assert.AreEqual(9, after.Relics[0].Growth);
            Assert.AreEqual(2, after.Relics[0].CooldownRounds);
            Assert.AreEqual(1, after.Relics[0].UsesThisRound);
            Assert.IsFalse(after.Relics.Any(r => r.DefinitionId == "10002"));
            Assert.AreEqual(1, Relic(after, "13004").RemainingCharges);
            Assert.AreSame(before.Random, battle.State.Random);
            Assert.AreEqual(before.FlowFor(Player).ActionsRemaining, battle.State.FlowFor(Player).ActionsRemaining);
            Assert.AreEqual(before.Duel.Scores.Count, battle.State.Duel.Scores.Count);
            Assert.IsTrue(battle.Engine.PreviewHand(battle.State, Player,
                new[] { battle.State.Zone(CardZoneKinds.Hand, Player).Cards.First() }).Accepted);
        }

        [TestCase("RemoveItem")]
        [TestCase("ReplaceItem")]
        public void ItemRebindingPreservesSurvivingNonzeroRuntimeFieldsAndBothMemoryScopes(string operation)
        {
            var battle = Battle();
            PlayOne(battle);
            var original = battle.State.Duel.Player(Player).Relics;
            var grenade = Relic(original, "11004");
            var memory = grenade.Memory.WithCounter("regression/encounter", 5, MemoryScope.Encounter)
                .WithCounter("regression/round", 7, MemoryScope.Round);
            var loadout = original.WithRuntime(original.Relics.Select(r => r.InstanceId == grenade.InstanceId
                ? r.WithRuntime(2, 3, 4, memory) : r));
            var potion = Relic(loadout, "10002");
            object[] arguments = operation == "RemoveItem" ? new object[] { potion.InstanceId } : new object[] {
                potion.InstanceId, new BattleRelicSetup(loadout.Definition(potion.InstanceId),
                    new RunRelic("replacement-potion", "10002", charges: 1), loadout.RulesFor(potion.InstanceId)) };
            var method = typeof(BattleRelicLoadout).GetMethod(operation, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            var rebound = (BattleRelicLoadout)method.Invoke(loadout, arguments);
            var survivor = Relic(rebound, "11004");
            Assert.AreEqual(grenade.InstanceId, survivor.InstanceId);
            Assert.AreEqual(2, survivor.RemainingCharges);
            Assert.AreEqual(3, survivor.UsesThisRound);
            Assert.AreEqual(4, survivor.CooldownRounds);
            Assert.AreEqual(5, survivor.Memory.CounterValue("regression/encounter"));
            Assert.AreEqual(7, survivor.Memory.CounterValue("regression/round"));
            CollectionAssert.AreEqual(Memory(Relic(loadout, "10001")), Memory(Relic(rebound, "10001")));
            var saved = rebound.ExportRunRelics().Single(r => r.DefinitionId == "11004");
            Assert.AreEqual(5, saved.Memory.CounterValue("regression/encounter"));
            Assert.AreEqual(0, saved.Memory.CounterValue("regression/round"));
            Assert.AreEqual(2, saved.Charges);
            Assert.AreEqual(4, saved.CooldownRounds);
        }
    }
}
