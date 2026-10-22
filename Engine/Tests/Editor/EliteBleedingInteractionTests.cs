using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class EliteBleedingInteractionTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), Enemy = new ParticipantId("enemy");
        private const string Encounter = "standard-armored-summoner";
        private static string Read(string relativePath, [CallerFilePath] string source = "") =>
            File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "../../..", relativePath)));
        private static XElement Find(XDocument xml, string section, string id) => xml.Root.Element(section).Elements().Single(e => (string)e.Attribute("id") == id);
        private static CompiledBattleContent Content(string[] items = null, Action<XDocument> edit = null)
        {
            var xml = XDocument.Parse(Read("Engine/Content/StandardSingleBattle-2026-10-21.xml"));
            Find(xml, "Loadouts", "baseline").ReplaceNodes((items ?? Array.Empty<string>()).Select((id, i) =>
                new XElement("Relic", new XAttribute("ref", id), new XAttribute("instanceId", "interaction-item-" + i))));
            edit?.Invoke(xml);
            var result = BattleContentCompiler.CompileXml(xml.ToString());
            Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message))); return result.Content;
        }
        private static BattleSession Session(string[] items = null, Action<XDocument> edit = null)
        {
            var built = BattleContentFactory.Create(Content(items, edit),
                new BattleSetupRequest(Encounter, "elite-bleeding-interactions", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(built.Success, string.Join("\n", built.Diagnostics.Select(d => d.Message)));
            Act(built.Session, CardDuelAction.Start); return built.Session;
        }
        private static BattleSessionResult Act(BattleSession s, CardDuelAction action, CardInstanceId[] cards = null, bool target = false)
        {
            var result = s.Execute(s.State.Revision, Player, action, cards, target ? new[] { Enemy } : null);
            Assert.IsTrue(result.Accepted, action + ": " + result.Error); return result;
        }
        private static CombatEvent[] Hits(BattleSessionResult r) => r.Steps.SelectMany(s => s.Transition.Events).Where(e => e.Effect.Kind == CombatEffectKind.Damage).ToArray();
        private static int Layers(CombatState state) => state.Statuses.Where(s => s.DefinitionId.Value == "elite-bleeding" && s.Target.Id == Player.Value).Sum(s => s.Stacks);
        private static void Deck(XDocument xml, int[] ranks, bool sameSuit = false) => Find(xml, "Decks", "standard-deck")
            .ReplaceNodes(ranks.Select((rank, i) => new XElement("Card", new XAttribute("id", "interaction-card-" + i),
                new XAttribute("face", "standard:" + (sameSuit ? 1 : i % 4 + 1) + ":" + rank))));
        private static void AdventureCommand(BattleAdventureSession s, AdventureCommandKind kind, string id = null)
        {
            var result = s.Execute(s.Revision, new AdventureCommand(kind, id)); Assert.IsTrue(result.Accepted, result.Error);
        }

        [Test] public void FullHouseCleansesBleedingAndItsImmunityBlocksControlButAllowsNegativeBleeding()
        {
            var s = Session(edit: xml => Deck(xml, new[] { 3, 3, 3, 2, 2 }));
            DesignRevision1726Tests.Round(s); Assert.AreEqual(2, Layers(s.State.Duel.Combat));
            var played = Act(s, CardDuelAction.Play, s.State.Zone(CardZoneKinds.Hand, Player).Cards.ToArray(), true);
            Assert.AreEqual(HandCategory.FullHouse, played.Steps.First().Transition.Score.Category);
            Assert.AreEqual(0, Layers(s.State.Duel.Combat)); Assert.IsTrue(s.State.Duel.IsActionBlocked(Enemy));
            Assert.IsTrue(s.State.Duel.Combat.Statuses.Any(st => st.DefinitionId.Value == "control-immunity" && st.Target.Id == Player.Value));
            Assert.AreEqual(1, played.Steps.SelectMany(step => step.Transition.RuleEvents).OfType<CombatStatusChangedEvent>()
                .Count(change => change.Reason == CombatStatusChangeReason.Cleansed && change.StatusBefore.DefinitionId.Value == "elite-bleeding"));
            // Use the actual post-hand immunity and catalog to distinguish Control from Negative tags.
            var combat = s.State.Duel.Combat;
            var result = CombatEngine.Resolve(combat, combat.Revision, new[] {
                CombatEffect.ApplyStatus(new StatusInstance(new StatusInstanceId("incoming-stun"), new StatusDefinitionId("stun"),
                    EntityRef.Participant(Enemy), EntityRef.Participant(Player), 1, StatusDuration.ForTriggers(1))),
                CombatEffect.ApplyStatus(new StatusInstance(new StatusInstanceId("incoming-bleeding"), new StatusDefinitionId("elite-bleeding"),
                    EntityRef.Participant(Enemy), EntityRef.Participant(Player), 3, StatusDuration.UntilRemoved()))
            }, statusCatalog: s.Engine.Duel.Definition.StatusCatalog);
            Assert.IsTrue(result.Accepted, result.Error.ToString()); Assert.AreEqual(3, Layers(result.State));
            Assert.AreEqual(1, result.RuleEvents.OfType<CombatStatusBlockedEvent>().Count());
            Assert.IsFalse(result.State.Statuses.Any(st => st.DefinitionId.Value == "stun" && st.Target.Id == Player.Value));
        }

        [Test] public void StunnedEliteDoesNotDecayExistingBleedingOrAdvanceIntentButStillGrowsAtRoundEnd()
        {
            var s = Session(new[] { "test-stun" }, xml => xml.Root.Element("Relics").Add(XElement.Parse(
                "<Relic id='test-stun' version='test'><ActionTrigger id='stun-on-play' timing='PlayCompleted'>" +
                "<Effect kind='ApplyStatus' statusRef='stun' target='Opponents'/></ActionTrigger></Relic>")));
            DesignRevision1726Tests.Round(s); Assert.AreEqual(2, Layers(s.State.Duel.Combat));
            Act(s, CardDuelAction.Play, s.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray());
            Assert.IsTrue(s.State.Duel.IsActionBlocked(Enemy));
            var before = s.State.Duel.Combat.Get(Player).Health;
            var skipped = Act(s, CardDuelAction.EndTurn);
            Assert.AreEqual(before, s.State.Duel.Combat.Get(Player).Health); Assert.AreEqual(2, Layers(s.State.Duel.Combat));
            Assert.IsEmpty(Hits(skipped)); Assert.AreEqual(2, s.State.Duel.CompletedRounds);
            Assert.AreEqual("strike", s.State.Duel.Enemies.Single().BehaviorState.Intent.Id);
            var preview = s.Engine.Duel.PreviewEnemyAction(s.State.Duel, Enemy);
            Assert.IsTrue(preview.Accepted); Assert.AreEqual(28, preview.Hits.Single().DamageToHealth);
            var cast = Act(s, CardDuelAction.EndTurn);
            Assert.AreEqual(28, Hits(cast).Single().DamageToHealth); Assert.AreEqual(1, Layers(s.State.Duel.Combat));
        }

        [Test] public void LethalSpellConsumesReviverClearsOldBleedingAndCancelsItsFollowingApplication()
        {
            var s = Session(new[] { "13004" }, xml =>
                Find(xml, "Encounters", Encounter).Element("Player").SetAttributeValue("initialHealth", 34));
            DesignRevision1726Tests.Round(s); DesignRevision1726Tests.Round(s);
            Assert.AreEqual(1, Layers(s.State.Duel.Combat)); Assert.AreEqual(10, s.State.Duel.Combat.Get(Player).Health);
            var result = Act(s, CardDuelAction.EndTurn);
            Assert.AreEqual(11, Hits(result).Single().SettledDamage); Assert.AreEqual(480, s.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(0, Layers(s.State.Duel.Combat)); Assert.IsEmpty(s.State.Duel.Combat.Statuses);
            Assert.IsFalse(s.State.Duel.Player(Player).Relics.Relics.Any(r => r.DefinitionId == "13004"));
            Assert.AreEqual(1, result.Steps.SelectMany(step => step.Transition.Events).Count(e => e.Effect.Kind == CombatEffectKind.Revive));
            var revivalFrame = result.Timeline.Single(entry => entry.RuleEvent is CombatEvent fact &&
                fact.Effect.Kind == CombatEffectKind.Revive && fact.Effect.Target == Player);
            Assert.AreEqual(480, revivalFrame.State.Duel.Combat.Get(Player).Health, "The exact revive timeline frame must restore HP.");
            Assert.IsFalse(revivalFrame.State.Duel.Combat.Statuses.Any(status => status.Target.Kind == EntityKind.Participant &&
                status.Target.Id == Player.Value), "The exact revive timeline frame must already clear all target statuses.");
            Assert.IsFalse(result.Steps.SelectMany(step => step.Transition.RuleEvents).OfType<CombatStatusEvent>()
                .Any(e => e.Status.DefinitionId.Value == "elite-bleeding"));
            Assert.AreEqual(ScoredDuelPhase.AwaitingPlayerHand, s.State.Duel.Phase);
        }

        [Test] public void ReflectArmorKillsTheCasterBeforeItsFollowingBleedingApplication()
        {
            var s = Session(new[] { "12001" }, xml => {
                var enemy = Find(xml, "Enemies", "summoner");
                enemy.SetAttributeValue("maxHealth", 3);
                // Reflect armor rounds down 20% of absorbed damage; guarantee a lethal reflected hit.
                enemy.Element("Behavior").SetAttributeValue("attack", 40);
                Find(xml, "Players", "hero").SetAttributeValue("initialBlock", 100);
            });
            var result = Act(s, CardDuelAction.EndTurn); var hits = Hits(result);
            CollectionAssert.AreEqual(new long[] { 20, 4 }, hits.Select(hit => hit.SettledDamage));
            Assert.AreEqual(20, hits[0].BlockAbsorbed); Assert.AreEqual(CombatEffectOrigin.Reflection, hits[1].Provenance.Origin);
            Assert.AreEqual(ScoredDuelPhase.Won, s.State.Duel.Phase); Assert.AreEqual(0, Layers(s.State.Duel.Combat));
            Assert.IsFalse(result.Steps.SelectMany(step => step.Transition.RuleEvents).OfType<CombatStatusEvent>()
                .Any(e => e.Status.DefinitionId.Value == "elite-bleeding"));
        }

        [Test] public void RealRouletteItemSelfDamageIsNotAmplifiedByExistingBleeding()
        {
            var s = Session(new[] { "15001" }, xml => {
                var item = Find(xml, "Relics", "15001").Element("Item");
                item.SetAttributeValue("rouletteTurns", 1); item.SetAttributeValue("explosionDamage", 10);
            });
            DesignRevision1726Tests.Round(s); Assert.AreEqual(2, Layers(s.State.Duel.Combat));
            long before = s.State.Duel.Combat.Get(Player).Health;
            var itemId = s.State.Duel.Player(Player).Relics.Relics.Single().InstanceId;
            var result = s.UseItem(s.State.Revision, Player, itemId);
            Assert.IsTrue(result.Accepted, result.Error.ToString()); Assert.AreEqual(10, Hits(result).Single().SettledDamage);
            Assert.AreEqual(Player, Hits(result).Single().Effect.Source); Assert.AreEqual(before - 10, s.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(2, Layers(s.State.Duel.Combat));
        }

        [Test] public void AdventureCarriesHealthLossButStartsTheNextBattleWithoutBleeding()
        {
            // Five kings deal 65 x 12 without a flush healing away the elite's 4 damage.
            var content = Content(edit: xml => Deck(xml, new[] { 13, 13, 13, 13, 13 }));
            var xml = XDocument.Parse(Read("Engine/Content/StandardAdventure.xml"));
            xml.Root.SetAttributeValue("startNode", "elite");
            xml.Root.Element("Tarots").Elements().Single(t => (string)t.Attribute("id") == "fool").SetAttributeValue("maxHealthMultiplier", 1);
            xml.Root.Element("Nodes").ReplaceNodes(XElement.Parse("<Nodes>" +
                "<Node id='elite' kind='Elite' chapter='1' encounter='standard-armored-summoner' next='next'/>" +
                "<Node id='next' kind='Boss' chapter='1' encounter='standard-single-battle'/></Nodes>").Elements());
            var definition = BattleAdventureXml.Load(xml.ToString(), content);
            var s = new BattleAdventureSession(definition, content, "elite-bleeding-adventure", 23);
            AdventureCommand(s, AdventureCommandKind.EnterNode, "elite");
            var round = s.ExecuteBattle(s.Revision, CardDuelAction.EndTurn); Assert.IsTrue(round.Accepted, round.Error);
            Assert.AreEqual(2, Layers(s.Battle.State.Duel.Combat));
            var win = s.ExecuteBattle(s.Revision, CardDuelAction.Play, s.Battle.State.Zone(CardZoneKinds.Hand, Player).Cards);
            Assert.IsTrue(win.Accepted, win.Error); Assert.AreEqual(AdventurePhase.Reward, s.Phase);
            long health = s.Health; Assert.AreEqual(796, health); Assert.Less(health, s.MaxHealth);
            AdventureCommand(s, AdventureCommandKind.Continue); AdventureCommand(s, AdventureCommandKind.EnterNode, "next");
            Assert.AreEqual(health, s.Health); Assert.AreEqual(health, s.Battle.State.Duel.Combat.Get(Player).Health);
            Assert.IsEmpty(s.Battle.State.Duel.Combat.Statuses);
        }
    }
}
