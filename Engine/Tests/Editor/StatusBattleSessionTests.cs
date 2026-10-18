using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    public sealed class StatusBattleSessionTests
    {
        private static readonly ParticipantId Player = new ParticipantId("status-player"), Enemy = new ParticipantId("status-enemy");
        private static readonly StatusDefinitionId Stun = new StatusDefinitionId("entry-stun");

        private sealed class SelfStunRule : IScoredHandEffectRule
        {
            public string Version => "entry-self-stun.1";
            public BattleEffectSequence Resolve(ScoredHandEffectContext context) => new BattleEffectSequence(Version,
                "block the source's remaining player window", new[] {
                    new BattleEffectStep(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(Stun, 1,
                        StatusDuration.ForTriggers(1))), EffectTargetSpec.Self())
                });
        }

        private static BattleSession Ready(bool blocked)
        {
            var catalog = new CombatStatusCatalog("status-entry.1", new[] {
                new CombatStatusDefinition(Stun, StatusTags.Control, new StatusBehaviorSpec[] { new BlockActionWindowSpec() },
                    StatusLifetimeSpec.ForTriggers(1))
            });
            var engine = new CardDuelEngine(new BattleRules("status-entry.1", 8, statusCatalog: catalog));
            var rules = new BattlePlayerRules(maxEnergy: 3, initialEnergy: 3, discards: 3, effectRule: new SelfStunRule(),
                dealPolicy: new CardDuelDealPolicy(CardDuelRefillTiming.AfterAction, CardDuelRefillTiming.AfterAction));
            var state = engine.Create("status-entry", HmacDeckShuffler.FromSeed(new byte[32]),
                new[] { new BattlePlayerSetup(Player, new CombatantDefinition("hero", 100), rules: rules) },
                new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("monster", 1000), 7) });
            var start = engine.Execute(state, state.Revision, Player, CardDuelAction.Start);
            Assert.IsTrue(start.Accepted, start.Error.ToString()); state = start.State;
            if (blocked)
            {
                var played = engine.Execute(state, state.Revision, Player, CardDuelAction.Play,
                    new[] { state.Zone(CardZoneKinds.Hand, Player).Cards[0] });
                Assert.IsTrue(played.Accepted, played.Error.ToString()); state = played.State;
                // Use direct rule steps to inspect the blocked snapshot before Session's automatic skip.
                var collected = engine.Execute(state, state.Revision, Player, CardDuelAction.CollectPlayed);
                Assert.IsTrue(collected.Accepted, collected.Error.ToString()); state = collected.State;
                Assert.IsTrue(state.Duel.IsActionBlocked(Player)); Assert.IsFalse(state.Duel.CanAct(Player));
                Assert.AreEqual(Player, state.Duel.NextBlockedPlayer);
            }
            return new BattleSession(engine, state);
        }

        [TestCase(CardDuelAction.Play)] [TestCase(CardDuelAction.Discard)] [TestCase(CardDuelAction.EndTurn)]
        public void DirectPlayerDecisionsRejectAControlledActorWithoutSpendingResources(CardDuelAction action)
        {
            var session = Ready(true); var before = session.State;
            var cards = action == CardDuelAction.EndTurn ? null : new[] { before.Zone(CardZoneKinds.Hand, Player).Cards[0] };
            var result = session.Engine.Execute(before, before.Revision, Player, action, cards);
            Assert.AreEqual(CardDuelError.ActionBlocked, result.Error); Assert.AreSame(before, result.State);
            Assert.IsEmpty(result.RuleEvents); Assert.IsEmpty(result.Events);
            Assert.AreEqual(2, result.State.Duel.Player(Player).Energy.Energy); Assert.IsFalse(result.State.Duel.Player(Player).HasEnded);
            Assert.AreEqual(3, result.State.DiscardsFor(Player)); Assert.AreEqual(1, result.State.Duel.Combat.Statuses.Count);
            Assert.AreEqual(1, result.State.Duel.Combat.Statuses.Single().Duration.RemainingTriggers);
            foreach (var zone in before.Table.Zones)
                CollectionAssert.AreEqual(zone.Cards, result.State.Table.GetZone(zone.Id).Cards);
            CollectionAssert.AreEqual(before.Random.StateBytes, result.State.Random.StateBytes);
            if (action == CardDuelAction.Play || action == CardDuelAction.Discard)
            {
                var preview = action == CardDuelAction.Play
                    ? session.Engine.PreviewPlay(before, before.Revision, Player, cards)
                    : session.Engine.PreviewDiscard(before, before.Revision, Player, cards);
                Assert.AreEqual(CardDuelError.ActionBlocked, preview.Error); Assert.AreSame(before, preview.State);
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void SessionDoesNotAcceptTheInternalSkipCommandEvenForABlockedActor(bool blocked)
        {
            var session = Ready(blocked); var before = session.State;
            var result = session.Execute(before.Revision, Player, CardDuelAction.SkipPlayerTurn);
            Assert.AreEqual(CardDuelError.InvalidPhase, result.Error); Assert.AreSame(before, result.Before);
            Assert.AreSame(before, result.State); Assert.AreSame(before, session.State); Assert.IsEmpty(result.Steps);
            if (blocked) Assert.AreEqual(1, session.State.Duel.Combat.Statuses.Single().Duration.RemainingTriggers);
        }

        [Test] public void InternalSkipRequiresAnActualActionBlocker()
        {
            var session = Ready(false); var before = session.State;
            var result = session.Engine.Execute(before, before.Revision, Player, CardDuelAction.SkipPlayerTurn);
            Assert.AreEqual(CardDuelError.InvalidPhase, result.Error); Assert.AreSame(before, result.State);
            Assert.IsEmpty(result.RuleEvents); Assert.IsEmpty(result.Events);
            Assert.AreEqual(3, result.State.Duel.Player(Player).Energy.Energy); Assert.IsFalse(result.State.Duel.Player(Player).HasEnded);
        }

        [Test] public void LegacyDuelRecordsRejectPlayerSkipInsteadOfReplayingItAsAnOrdinaryTurn()
        {
            Assert.Throws<ArgumentException>(() => new ScoredDuelActionRecord(ScoredDuelActionKind.SkipPlayerTurn));
            Assert.Throws<ArgumentException>(() => new ScoredDuelActionRecord(ScoredDuelActionKind.SkipPlayerTurn, 0));
        }
    }
}
