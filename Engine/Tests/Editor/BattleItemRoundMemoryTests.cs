using System;
using System.Linq;
using System.Xml;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleItemRoundMemoryTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), Enemy = new ParticipantId("enemy");

        private static BattleSession Battle(string[] items, bool lethalEnemy = false, bool diceOnlyGrenade = false)
        {
            var document = new XmlDocument();
            document.LoadXml(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle-2026-10-21.xml"));
            document.SelectSingleNode("//Loadout[@id='baseline']").InnerXml = string.Join("", items.Select((id, index) =>
                "<Relic ref='" + id + "' instanceId='memory-item-" + index + "'/>"));
            var enemy = (XmlElement)document.SelectSingleNode("//Enemy[@id='rotator']");
            enemy.InnerXml = ""; enemy.SetAttribute("maxHealth", "1000000"); enemy.SetAttribute("damage", lethalEnemy ? "1000" : "0");
            ((XmlElement)document.SelectSingleNode("//Encounter[@id='standard-single-battle']/Player")).SetAttribute("initialHealth", "500");
            if (diceOnlyGrenade)
            {
                foreach (XmlNode node in document.SelectNodes("/BattleContent/Relics/Relic").Cast<XmlNode>()
                    .Where(node => !new[] { "10001", "10002", "11004", "14001" }.Contains(node.Attributes["id"].Value)).ToArray())
                    node.ParentNode.RemoveChild(node);
                document.SelectSingleNode("//Loadout[@id='item-lab']").InnerXml = "";
            }
            var content = BattleContentCompiler.CompileXml(document.OuterXml);
            Assert.IsTrue(content.Success, string.Join(";", content.Diagnostics.Select(d => d.Message)));
            var created = BattleContentFactory.Create(content.Content, new BattleSetupRequest("standard-single-battle",
                "item-round-memory", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(created.Success, string.Join(";", created.Diagnostics.Select(d => d.Message)));
            Action(created.Session, CardDuelAction.Start);
            return created.Session;
        }

        private static BattleSessionResult Action(BattleSession battle, CardDuelAction action, CardInstanceId[] cards = null)
        {
            var result = battle.Execute(battle.State.Revision, Player, action, cards);
            Assert.IsTrue(result.Accepted, action + ": " + result.Error);
            return result;
        }
        private static BattleRelicState Relic(BattleSession battle, string id) => battle.State.Duel.Player(Player).Relics.Relics.Single(r => r.DefinitionId == id);
        private static void PlayOne(BattleSession battle) => Action(battle, CardDuelAction.Play, new[] { battle.State.Zone(CardZoneKinds.Hand, Player).Cards.First() });
        private static void HasRoundMemory(BattleSession battle) => Assert.IsTrue(Relic(battle, "10001").Memory.Counters.Any(counter => counter.Scope == MemoryScope.Round),
            "Playing a scored hand must leave the real program memory that previously broke item rebinding.");

        [Test]
        public void ConsumedPotionAfterScoredHandPreservesAnotherRelicsRoundMemory()
        {
            var battle = Battle(new[] { "10001", "10002" }); PlayOne(battle); HasRoundMemory(battle);
            var memory = Relic(battle, "10001").Memory.Counters.Select(c => c.Key + ":" + c.Value + ":" + c.Scope).ToArray();
            int actions = battle.State.FlowFor(Player).ActionsRemaining;
            var result = battle.UseItem(battle.State.Revision, Player, Relic(battle, "10002").InstanceId);
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            Assert.IsFalse(battle.State.Duel.Player(Player).Relics.Relics.Any(r => r.DefinitionId == "10002"));
            CollectionAssert.AreEqual(memory, Relic(battle, "10001").Memory.Counters.Select(c => c.Key + ":" + c.Value + ":" + c.Scope));
            Assert.AreEqual(actions, battle.State.FlowFor(Player).ActionsRemaining);
            Assert.AreEqual(510, battle.State.Duel.Combat.Get(Player).Health);
        }

        [Test]
        public void DiceReplacementAfterScoredHandPreservesAnotherRelicsRoundMemory()
        {
            var battle = Battle(new[] { "10001", "10002", "14001" }, diceOnlyGrenade: true); PlayOne(battle); HasRoundMemory(battle);
            var memory = Relic(battle, "10001").Memory.Counters.Select(c => c.Key + ":" + c.Value + ":" + c.Scope).ToArray();
            string original = Relic(battle, "10002").InstanceId;
            var result = battle.UseItem(battle.State.Revision, Player, Relic(battle, "14001").InstanceId, original);
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            Assert.IsFalse(battle.State.Duel.Player(Player).Relics.Relics.Any(r => r.InstanceId == original));
            Assert.AreEqual(3, Relic(battle, "11004").RemainingCharges);
            CollectionAssert.AreEqual(memory, Relic(battle, "10001").Memory.Counters.Select(c => c.Key + ":" + c.Value + ":" + c.Scope));
        }

        [Test]
        public void LethalEnemyPreviewAndEndTurnAfterScoredHandCanConsumeReviver()
        {
            var battle = Battle(new[] { "10001", "13004" }, lethalEnemy: true); PlayOne(battle); HasRoundMemory(battle);
            var before = battle.State;
            var preview = battle.Engine.Duel.PreviewEnemyAction(before.Duel, Enemy);
            Assert.IsNotNull(preview); Assert.IsTrue(preview.Accepted, preview.Error.ToString());
            Assert.AreSame(before, battle.State);
            Assert.AreEqual(1, Relic(battle, "13004").RemainingCharges);
            var result = Action(battle, CardDuelAction.EndTurn);
            Assert.AreEqual(480, battle.State.Duel.Combat.Get(Player).Health);
            Assert.IsFalse(battle.State.Duel.Player(Player).Relics.Relics.Any(r => r.DefinitionId == "13004"));
            Assert.AreEqual(1, result.Steps.SelectMany(s => s.Transition.Events).Count(e => e.Effect.Kind == CombatEffectKind.Revive));
            Assert.AreEqual(ScoredDuelPhase.AwaitingPlayerHand, battle.State.Duel.Phase);
            Assert.AreEqual(3, battle.State.FlowFor(Player).ActionsRemaining);
        }

        [Test]
        public void FinalDiscardDoesNotRollBackWhenAutomaticEnemyPhaseConsumesReviver()
        {
            var battle = Battle(new[] { "10001", "13004" }, lethalEnemy: true);
            PlayOne(battle); PlayOne(battle); HasRoundMemory(battle);
            var card = battle.State.Zone(CardZoneKinds.Hand, Player).Cards.First();
            int scores = battle.State.Duel.Scores.Count;
            Assert.AreEqual(1, battle.State.FlowFor(Player).ActionsRemaining);
            var result = Action(battle, CardDuelAction.Discard, new[] { card });
            Assert.AreEqual(scores, battle.State.Duel.Scores.Count);
            Assert.AreEqual(480, battle.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(1, battle.State.Duel.CompletedRounds);
            Assert.AreEqual(3, battle.State.FlowFor(Player).ActionsRemaining);
            Assert.IsFalse(battle.State.Duel.Player(Player).Relics.Relics.Any(r => r.DefinitionId == "13004"));
            Assert.AreEqual(1, result.Steps.SelectMany(s => s.Transition.Events).Count(e => e.Effect.Kind == CombatEffectKind.Revive));
        }

        [Test]
        public void FinalScoredActionDoesNotRollBackWhenAutomaticEnemyPhaseConsumesReviver()
        {
            var battle = Battle(new[] { "10001", "13004" }, lethalEnemy: true);
            PlayOne(battle); PlayOne(battle); HasRoundMemory(battle);
            int scores = battle.State.Duel.Scores.Count;
            Assert.AreEqual(1, battle.State.FlowFor(Player).ActionsRemaining);
            PlayOne(battle);
            Assert.AreEqual(scores + 1, battle.State.Duel.Scores.Count);
            Assert.AreEqual(480, battle.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(1, battle.State.Duel.CompletedRounds);
            Assert.AreEqual(3, battle.State.FlowFor(Player).ActionsRemaining);
            Assert.IsFalse(battle.State.Duel.Player(Player).Relics.Relics.Any(r => r.DefinitionId == "13004"));
        }
    }
}
