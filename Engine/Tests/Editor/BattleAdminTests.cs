using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Xml;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleAdminTests
    {
        private static readonly ParticipantId P = new ParticipantId("player"), E = new ParticipantId("enemy");
        private static BattleSession Session(bool started = true, params string[] relics)
        {
            var doc = new XmlDocument(); doc.LoadXml(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle-2026-10-21.xml"));
            doc.SelectSingleNode("//Loadout[@id='baseline']").InnerXml = string.Join("", relics.Select((id, index) =>
                "<Relic ref='" + id + "' instanceId='held-" + index + "'/>"));
            var enemy = (XmlElement)doc.SelectSingleNode("//Enemy[@id='rotator']");
            enemy.InnerXml = ""; enemy.SetAttribute("maxHealth", "1000000"); enemy.SetAttribute("damage", "0");
            var content = BattleContentCompiler.CompileXml(doc.OuterXml);
            Assert.IsTrue(content.Success, string.Join(";", content.Diagnostics.Select(d => d.Message)));
            var built = BattleContentFactory.Create(content.Content,
                new BattleSetupRequest("standard-single-battle", "admin-tests", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(built.Success);
            var session = new BattleSession(built.Session.Engine, built.Session.State.WithGold(new[] { new BattleAdminGold(P, 19) }));
            if (started) Assert.IsTrue(session.Execute(session.State.Revision, P, CardDuelAction.Start).Accepted);
            return session;
        }
        private static BattleAdminCard New(int rank, CardEffects effects = CardEffects.None) =>
            new BattleAdminCard(definitionId: "standard:1:" + rank, effects: effects);
        private static BattleSessionResult Edit(BattleSession session, BattleAdminPatch patch)
        {
            var result = session.ApplyAdmin(session.State.Revision, patch);
            Assert.IsTrue(result.Accepted, result.Error.ToString()); return result;
        }

        [Test] public void AtomicPreviewAndCommitPreserveBudgetsRandomAndHistoryWithLongValues()
        {
            var s = Session(); var before = s.State;
            long amount = (long)int.MaxValue + 1000;
            var patch = new BattleAdminPatch(
                vitals: new[] { new BattleAdminVitals(P, amount - 1, amount, amount + 1) },
                gold: new[] { new BattleAdminGold(P, long.MaxValue) }, reason: "高数值场景");
            var preview = s.PreviewAdmin(before.Revision, patch);
            Assert.IsTrue(preview.Accepted); Assert.AreSame(before, s.State);
            var applied = Edit(s, patch);
            Assert.AreEqual(before.Revision + 1, applied.State.Revision);
            Assert.AreEqual(amount - 1, s.State.Duel.Combat.Get(P).Health);
            Assert.AreEqual(amount, s.State.Duel.Combat.Get(P).MaxHealth);
            Assert.AreEqual(amount + 1, s.State.Duel.Combat.Get(P).Block);
            Assert.AreEqual(long.MaxValue, s.State.GoldFor(P));
            Assert.AreEqual(preview.State.GoldFor(P), s.State.GoldFor(P));
            Assert.AreSame(before.Random, s.State.Random);
            Assert.AreEqual(before.Duel.Player(P).Energy.Energy, s.State.Duel.Player(P).Energy.Energy);
            Assert.AreSame(before.FlowFor(P), s.State.FlowFor(P));
            CollectionAssert.AreEqual(before.Duel.Actions, s.State.Duel.Actions);
            CollectionAssert.AreEqual(before.Duel.Scores, s.State.Duel.Scores);
            Assert.AreEqual(BattleTimelineKind.AdminEdited, applied.Timeline.Single().Kind);
            Assert.IsEmpty(applied.Steps.Single().Transition.Events);
        }

        [Test] public void AnyInvalidPartRejectsEntireBatchWithoutCommittingOrSpendingRandom()
        {
            var s = Session(); var before = s.State;
            var id = before.Zone(CardZoneKinds.Hand, P).Cards[0].Value;
            var patch = new BattleAdminPatch(gold: new[] { new BattleAdminGold(P, 999) },
                zones: new[] {
                    new BattleAdminZone(P, CardZoneKinds.Hand, new[] { new BattleAdminCard(id) }),
                    new BattleAdminZone(P, CardZoneKinds.Discard, new[] { new BattleAdminCard(id) }) });
            var rejected = s.ApplyAdmin(before.Revision, patch);
            Assert.AreEqual(CardDuelError.InvalidAdminPatch, rejected.Error); Assert.AreSame(before, s.State);
            Assert.AreSame(before.Random, rejected.State.Random); Assert.IsEmpty(rejected.Timeline);
            Assert.AreEqual(CardDuelError.InvalidAdminPatch,
                s.ApplyAdmin(before.Revision, new BattleAdminPatch(vitals: new[] { new BattleAdminVitals(P, maxHealth: 1) })).Error);
            Assert.AreSame(before, s.State);
        }

        [Test] public void ZoneReplacementMovesExistingIdentityPreservesOrderAndCreatesDisabledCards()
        {
            var s = Session(); var before = s.State;
            var hand = before.Zone(CardZoneKinds.Hand, P).Cards;
            var old = before.Table.GetCard(hand[0]);
            var patch = new BattleAdminPatch(zones: new[] {
                new BattleAdminZone(P, CardZoneKinds.Deck, new[] { New(2), new BattleAdminCard(old.Id.Value), New(13) }),
                new BattleAdminZone(P, CardZoneKinds.Discard, new[] { New(4, CardEffects.Unplayable) })
            });
            var preview = s.PreviewAdmin(before.Revision, patch);
            Edit(s, patch);
            var deck = s.State.Zone(CardZoneKinds.Deck, P).Cards;
            Assert.AreEqual(3, deck.Count); Assert.AreEqual(old.Id, deck[1]);
            Assert.AreEqual(old.OriginId, s.State.Table.GetCard(old.Id).OriginId);
            Assert.IsFalse(s.State.Zone(CardZoneKinds.Hand, P).Cards.Contains(old.Id));
            Assert.AreEqual(7, s.State.Zone(CardZoneKinds.Hand, P).Cards.Count);
            Assert.IsFalse(s.State.Table.GetCard(s.State.Zone(CardZoneKinds.Discard, P).Cards[0]).CanPlay);
            CollectionAssert.AreEqual(preview.State.Zone(CardZoneKinds.Deck, P).Cards, deck);
            var discarded = s.State.Zone(CardZoneKinds.Hand, P).Cards[0];
            Assert.IsTrue(s.Execute(s.State.Revision, P, CardDuelAction.Discard, new[] { discarded }).Accepted);
            Assert.IsTrue(s.State.Zone(CardZoneKinds.Hand, P).Cards.Contains(deck[2]), "The supplied deck's last element is drawn first.");
            Assert.AreEqual(19, s.State.GoldFor(P));
        }

        [Test] public void EmptyZoneListsDeleteTheirCardsWithoutForcingDrawOrEndingTheTurn()
        {
            var s = Session();
            Edit(s, new BattleAdminPatch(zones: new[] {
                new BattleAdminZone(P, CardZoneKinds.Hand, Array.Empty<BattleAdminCard>()),
                new BattleAdminZone(P, CardZoneKinds.Deck, Array.Empty<BattleAdminCard>()),
                new BattleAdminZone(P, CardZoneKinds.Discard, Array.Empty<BattleAdminCard>()) }));
            Assert.IsEmpty(s.State.Table.Cards);
            Assert.AreEqual(3, s.State.FlowFor(P).ActionsRemaining);
            Assert.IsFalse(s.State.Duel.IsFinished);
            Assert.IsTrue(s.Execute(s.State.Revision, P, CardDuelAction.EndTurn).Accepted);
            Assert.AreEqual(19, s.State.GoldFor(P));
            Assert.IsEmpty(s.State.Zone(CardZoneKinds.Hand, P).Cards);
        }

        [Test] public void RelicsCanBeAddedReorderedUpdatedAndRemovedWithImmediatePassiveRebinding()
        {
            var s = Session(true, "11004");
            var held = s.State.Duel.Player(P).Relics.Relics.Single();
            var hand = s.State.Zone(CardZoneKinds.Hand, P).Cards.Take(1).ToArray();
            var beforeScore = s.Engine.PreviewHand(s.State, P, hand).Score;
            Edit(s, new BattleAdminPatch(relics: new[] { new BattleAdminRelics(P, new[] {
                new BattleAdminRelic("13003"),
                new BattleAdminRelic("11004", held.InstanceId, charges: 2, growth: 123, cooldownRounds: 2, usesThisRound: 1)
            }) }));
            var loadout = s.State.Duel.Player(P).Relics;
            Assert.AreEqual("13003", loadout.Relics[0].DefinitionId);
            Assert.AreEqual(held.InstanceId, loadout.Relics[1].InstanceId);
            Assert.AreEqual(2, loadout.Relics[1].RemainingCharges);
            Assert.AreEqual(123, loadout.Relics[1].Growth);
            Assert.AreEqual(2, loadout.Relics[1].CooldownRounds);
            Assert.AreEqual(1, loadout.Relics[1].UsesThisRound);
            Assert.Greater(s.Engine.PreviewHand(s.State, P, hand).Score.Total, beforeScore.Total);
            Edit(s, new BattleAdminPatch(relics: new[] { new BattleAdminRelics(P, Array.Empty<BattleAdminRelic>()) }));
            Assert.IsEmpty(s.State.Duel.Player(P).Relics.Relics);
            Assert.AreEqual(beforeScore.Total, s.Engine.PreviewHand(s.State, P, hand).Score.Total);
        }

        [Test] public void RelicDefinitionCopyAndChargeLimitsStillRejectAtomically()
        {
            var s = Session(); var before = s.State;
            foreach (var items in new[] {
                new[] { new BattleAdminRelic("missing") },
                new[] { new BattleAdminRelic("13003"), new BattleAdminRelic("13003") },
                new[] { new BattleAdminRelic("11004", charges: 4) }
            })
            {
                Assert.AreEqual(CardDuelError.InvalidAdminPatch, s.ApplyAdmin(before.Revision,
                    new BattleAdminPatch(relics: new[] { new BattleAdminRelics(P, items) }, gold: new[] { new BattleAdminGold(P, 1) })).Error);
                Assert.AreSame(before, s.State);
            }
        }

        [Test] public void DirectDeathEndsBattleWithoutRevivalItemConsumptionOrDeathEffects()
        {
            var s = Session(true, "13004");
            var result = Edit(s, new BattleAdminPatch(vitals: new[] { new BattleAdminVitals(P, health: 0) }));
            Assert.AreEqual(ScoredDuelPhase.Lost, s.State.Duel.Phase);
            Assert.AreEqual(1, s.State.Duel.Player(P).Relics.Relics.Single().RemainingCharges);
            Assert.IsEmpty(result.Steps.Single().Transition.RuleEvents);
            Assert.IsFalse(result.Timeline.Any(t => t.Kind == BattleTimelineKind.BattleEnded));
            Assert.AreEqual(CardDuelError.InvalidPhase,
                s.ApplyAdmin(s.State.Revision, new BattleAdminPatch(vitals: new[] { new BattleAdminVitals(P, health: 1) })).Error);
            var win = Session();
            Edit(win, new BattleAdminPatch(vitals: new[] { new BattleAdminVitals(E, health: 0) }));
            Assert.AreEqual(ScoredDuelPhase.Won, win.State.Duel.Phase);
        }

        [Test] public void AdministrativeVictoryUsesTheExistingConfiguredObjective()
        {
            var rules = new BattleRules("admin-objective", 20, victoryEnemyRef: "objective");
            var engine = new CardDuelEngine(rules);
            var guard = new ParticipantId("guard");
            var session = new BattleSession(engine, engine.Create("admin-victory", HmacDeckShuffler.FromSeed(new byte[32]),
                new[] { new BattlePlayerSetup(P, new CombatantDefinition("hero", 800)) },
                new[] { new BattleEnemySetup(E, new CombatantDefinition("objective", 1000), 0),
                    new BattleEnemySetup(guard, new CombatantDefinition("guard", 1000), 0) }));
            Assert.IsTrue(session.Execute(session.State.Revision, P, CardDuelAction.Start).Accepted);
            Edit(session, new BattleAdminPatch(vitals: new[] { new BattleAdminVitals(E, health: 0) }));
            Assert.AreEqual(ScoredDuelPhase.Won, session.State.Duel.Phase);
            Assert.AreEqual(1000, session.State.Duel.Combat.Get(guard).Health);
        }

        [Test] public void AdminRequiresStartedRevisionBoundaryAndSeparateApi()
        {
            var s = Session(false); var patch = new BattleAdminPatch(gold: new[] { new BattleAdminGold(P, 10) });
            Assert.AreEqual(CardDuelError.InvalidPhase, s.ApplyAdmin(s.State.Revision, patch).Error);
            Assert.IsTrue(s.Execute(s.State.Revision, P, CardDuelAction.Start).Accepted);
            Assert.AreEqual(CardDuelError.StaleRevision, s.ApplyAdmin(s.State.Revision - 1, patch).Error);
            Assert.AreEqual(CardDuelError.InvalidPhase, s.Execute(s.State.Revision, P, CardDuelAction.AdminEdit).Error);
            Assert.Throws<InvalidOperationException>(() => s.State.WithGold(new[] { new BattleAdminGold(P, 1) }));
        }

        [Test] public void RemovingLastDecisionInMultiplayerRejectsInsteadOfLockingBattle()
        {
            var template = Session(); var second = new ParticipantId("second-player");
            var engine = template.Engine; var rules = template.State.Duel.Player(P).Rules;
            var session = new BattleSession(engine, engine.Create("admin-multiplayer", HmacDeckShuffler.FromSeed(new byte[32]),
                new[] {
                    new BattlePlayerSetup(P, new CombatantDefinition("hero", 800), rules: rules),
                    new BattlePlayerSetup(second, new CombatantDefinition("hero", 800), rules: rules)
                }, new[] { new BattleEnemySetup(E, new CombatantDefinition("enemy", 1000000), 0) }));
            Assert.IsTrue(session.Execute(session.State.Revision, P, CardDuelAction.Start).Accepted);
            Assert.IsTrue(session.Execute(session.State.Revision, P, CardDuelAction.EndTurn).Accepted);
            Assert.IsTrue(session.State.Duel.Player(P).HasEnded);
            Assert.IsTrue(session.State.Duel.HasPlayerWindow(second));
            var before = session.State;
            var result = session.ApplyAdmin(before.Revision, new BattleAdminPatch(
                vitals: new[] { new BattleAdminVitals(second, health: 0) }, gold: new[] { new BattleAdminGold(P, 99) }));
            Assert.AreEqual(CardDuelError.InvalidAdminPatch, result.Error);
            Assert.AreSame(before, session.State);
            Assert.IsTrue(session.State.Duel.HasPlayerWindow(second));
            Assert.IsTrue(session.Execute(session.State.Revision, second, CardDuelAction.EndTurn).Accepted);
        }

        [Test] public void GoldSurvivesItemsPlayAndAutomaticEnemyRound()
        {
            var s = Session(true, "10002");
            Edit(s, new BattleAdminPatch(gold: new[] { new BattleAdminGold(P, 5000000000L) }));
            var used = s.UseItem(s.State.Revision, P, s.State.Duel.Player(P).Relics.Relics.Single().InstanceId);
            Assert.IsTrue(used.Accepted, used.Error.ToString());
            Assert.AreEqual(5000000000L, s.State.GoldFor(P));
            Assert.IsTrue(s.Execute(s.State.Revision, P, CardDuelAction.Play, s.State.Zone(CardZoneKinds.Hand, P).Cards.Take(1)).Accepted);
            Assert.AreEqual(5000000000L, s.State.GoldFor(P));
            Assert.IsTrue(s.Execute(s.State.Revision, P, CardDuelAction.EndTurn).Accepted);
            Assert.AreEqual(5000000000L, s.State.GoldFor(P));
        }

        [Test] public void PatchSerializationReplaysStableInstancesAndExactLongWallet()
        {
            var s = Session(); var before = s.State;
            var patch = new BattleAdminPatch(gold: new[] { new BattleAdminGold(P, long.MaxValue) },
                zones: new[] { new BattleAdminZone(P, CardZoneKinds.Hand, new[] { New(6) }) },
                relics: new[] { new BattleAdminRelics(P, new[] { new BattleAdminRelic("10002") }) });
            var serializer = new DataContractJsonSerializer(typeof(BattleAdminPatch));
            BattleAdminPatch restored;
            using (var stream = new MemoryStream()) { serializer.WriteObject(stream, patch); stream.Position = 0; restored = (BattleAdminPatch)serializer.ReadObject(stream); }
            restored.Validate();
            var preview = s.PreviewAdmin(before.Revision, patch); Edit(s, restored);
            Assert.AreEqual(long.MaxValue, s.State.GoldFor(P));
            CollectionAssert.AreEqual(preview.State.Zone(CardZoneKinds.Hand, P).Cards, s.State.Zone(CardZoneKinds.Hand, P).Cards);
            Assert.AreEqual(preview.State.Duel.Player(P).Relics.Relics[0].InstanceId, s.State.Duel.Player(P).Relics.Relics[0].InstanceId);
        }
    }
}
