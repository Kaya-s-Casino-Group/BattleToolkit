using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class WrathAdventureReplayTests
    {
        private static BattleAdventureSession Create()
        {
            var compiled = BattleContentCompiler.CompileXml(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle-2026-10-21.xml"));
            Assert.IsTrue(compiled.Success, string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
            var xml = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/StandardAdventure.xml"));
            xml.Root.SetAttributeValue("startNode", "wrath-replay");
            xml.Root.Element("Nodes").ReplaceNodes(new XElement("Node", new XAttribute("id", "wrath-replay"),
                new XAttribute("kind", "Boss"), new XAttribute("chapter", 1), new XAttribute("encounter", "standard-wrath-thousand-hands")));
            var definition = BattleAdventureXml.Load(xml.ToString(), compiled.Content);
            var run = new BattleAdventureSession(definition, compiled.Content, "wrath-adventure-replay", 71);
            var entered = run.Execute(run.Revision, new AdventureCommand(AdventureCommandKind.EnterNode, "wrath-replay"));
            Assert.IsTrue(entered.Accepted, entered.Error);
            return run;
        }

        private static void Round(BattleAdventureSession run)
        {
            if (run.Battle.State.FlowFor(run.PlayerId).Window == BattlePlayerWindow.Draw)
                Assert.IsTrue(run.ExecuteBattle(run.Revision, CardDuelAction.SkipDraw).Accepted);
            var ended = run.ExecuteBattle(run.Revision, run.Battle.State.Duel.IsActionBlocked(run.PlayerId)
                ? CardDuelAction.SkipPlayerTurn : CardDuelAction.EndTurn);
            Assert.IsTrue(ended.Accepted, ended.Error);
            if (run.Battle.State.FlowFor(run.PlayerId).Window == BattlePlayerWindow.Ending)
            {
                var finished = run.ExecuteBattle(run.Revision, CardDuelAction.FinishTurn);
                Assert.IsTrue(finished.Accepted, finished.Error);
            }
        }

        private static void AssertSameBoss(BattleAdventureSession expected, BattleAdventureSession actual)
        {
            var a = WrathBossQuery.Read(expected.Battle.State.Duel); var b = WrathBossQuery.Read(actual.Battle.State.Duel);
            Assert.AreEqual(a.BodyId, b.BodyId); Assert.AreEqual(a.Attack, b.Attack); Assert.AreEqual(a.Charges, b.Charges);
            Assert.AreEqual(a.WarningActive, b.WarningActive); Assert.AreEqual(a.ReleaseRound, b.ReleaseRound);
            Assert.AreEqual(a.CooldownRemaining, b.CooldownRemaining);
            Assert.AreEqual(a.UltimateBaseDamage, b.UltimateBaseDamage); Assert.AreEqual(a.PendingUltimateBaseDamage, b.PendingUltimateBaseDamage);
            CollectionAssert.AreEqual(a.Hands.Select(h => h.Id), b.Hands.Select(h => h.Id));
            CollectionAssert.AreEqual(a.Hands.Select(h => h.Role), b.Hands.Select(h => h.Role));
            CollectionAssert.AreEqual(a.Hands.Select(h => h.IsAlive), b.Hands.Select(h => h.IsAlive));
            CollectionAssert.AreEqual(a.Hands.Select(h => h.ReviveRound), b.Hands.Select(h => h.ReviveRound));
            CollectionAssert.AreEqual(a.Hands.Select(h => h.ReviveRemaining), b.Hands.Select(h => h.ReviveRemaining));
            Assert.AreEqual(expected.Battle.State.Duel.CompletedRounds, actual.Battle.State.Duel.CompletedRounds);
            Assert.AreEqual(expected.Battle.State.Duel.Combat.Get(expected.PlayerId).Health, actual.Battle.State.Duel.Combat.Get(actual.PlayerId).Health);
            Assert.AreEqual(expected.Export(), actual.Export());
        }

        [Test]
        public void AdventureSnapshotReplaysChargeAndSeverDeadlinesThenContinuesThroughRegenerationAndUltimate()
        {
            var run = Create(); Round(run); Round(run);
            var equipped = run.ApplyAdmin(run.Revision, new BattleAdminPatch(relics: new[] {
                new BattleAdminRelics(run.PlayerId, new[] { new BattleAdminRelic("11004") }) }));
            Assert.IsTrue(equipped.Accepted, equipped.Error);
            var grenade = run.Relics.Single();
            var used = run.UseBattleItem(run.Revision, grenade.Id);
            Assert.IsTrue(used.Accepted, used.Error);
            var severed = WrathBossQuery.Read(run.Battle.State.Duel);
            Assert.AreEqual(8, severed.Attack); Assert.IsTrue(severed.Hands.All(h => h.ReviveRound == 11 && h.ReviveRemaining == 8));
            Round(run); Round(run);
            var before = WrathBossQuery.Read(run.Battle.State.Duel);
            Assert.AreEqual(1, before.Charges); Assert.AreEqual(18, before.Attack);
            Assert.IsTrue(before.Hands.All(h => !h.IsAlive && h.ReviveRound == 11 && h.ReviveRemaining == 6));

            var saved = run.Export(); var restored = BattleAdventureSession.Restore(run.Definition, run.Content, saved);
            AssertSameBoss(run, restored);
            for (int round = 5; round <= 11; round++)
            {
                Round(run); Round(restored); AssertSameBoss(run, restored);
            }
            var regenerated = WrathBossQuery.Read(restored.Battle.State.Duel);
            Assert.IsTrue(regenerated.Hands.All(h => h.IsAlive && h.ReviveRound == null));
            Assert.IsTrue(regenerated.WarningActive); Assert.AreEqual(12, regenerated.ReleaseRound);
            long health = restored.Battle.State.Duel.Combat.Get(restored.PlayerId).Health;
            Round(run); Round(restored); AssertSameBoss(run, restored);
            Assert.IsFalse(WrathBossQuery.Read(restored.Battle.State.Duel).WarningActive);
            Assert.AreEqual(189, health - restored.Battle.State.Duel.Combat.Get(restored.PlayerId).Health);
        }
    }
}
