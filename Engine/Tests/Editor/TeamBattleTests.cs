using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    public sealed class TeamBattleTests
    {
        private static readonly ParticipantId P1 = new ParticipantId("p1"), P2 = new ParticipantId("p2"),
            E1 = new ParticipantId("e1"), E2 = new ParticipantId("e2"), E3 = new ParticipantId("e3");
        private static CardDuelEngine Engine(int energy = 3, int rounds = 8) =>
            new CardDuelEngine(new ScoredDuelDefinition("team.test", new CombatantDefinition("hero", 40), new CombatantDefinition("enemy", 10000),
                new ScoreDamagePolicy(5), 7, rounds, maxEnergy: energy));
        private static CardDuelState Create(CardDuelEngine engine, int hp1 = 40, int hp2 = 40, int enemyHealth = 10000, IBattleTargetPolicy targeting = null, IBattleAllyTargetPolicy support = null)
        {
            var state = engine.Create("teams", HmacDeckShuffler.FromSeed(new byte[32]),
                new[] { new BattlePlayerSetup(P1, new CombatantDefinition("hero", 40), initialHealth: hp1, targetRules: new BattleActorTargetRules(targeting, support)), new BattlePlayerSetup(P2, new CombatantDefinition("hero", 40), initialHealth: hp2, targetRules: new BattleActorTargetRules(targeting, support)) },
                new[] { E1, E2, E3 }.Select(id => new BattleEnemySetup(id, new CombatantDefinition("enemy", enemyHealth), 7)));
            return Do(engine, state, P1, CardDuelAction.Start);
        }
        private static CardDuelState Do(CardDuelEngine engine, CardDuelState state, ParticipantId actor, CardDuelAction action,
            IEnumerable<CardInstanceId> cards = null, IEnumerable<ParticipantId> targets = null)
        {
            var result = engine.Execute(state, state.Revision, actor, action, cards, targets);
            Assert.That(result.Accepted, Is.True, action + ": " + result.Error); result.State.Table.Validate(); return result.State;
        }
        private static CardDuelState EndPlayers(CardDuelEngine engine, CardDuelState state)
        { return Do(engine, Do(engine, state, P1, CardDuelAction.EndTurn), P2, CardDuelAction.EndTurn); }
        private sealed class InvalidAllyPolicy : IBattleAllyTargetPolicy
        {
            public IReadOnlyList<ParticipantId> SelectTargets(BattleAllyTargetContext context) => new[] { E1 };
        }
        [Test] public void SupportTargetsDefaultToSelfAndRejectHostileOrInvalidRequests()
        {
            var engine = Engine(); var state = Create(engine);
            CollectionAssert.AreEqual(new[] { P1 }, engine.Duel.AllyTargets(state.Duel, P1));
            CollectionAssert.AreEqual(new[] { P2 }, engine.Duel.AllyTargets(state.Duel, P1, new[] { P2 }));
            Assert.Throws<ArgumentException>(() => engine.Duel.AllyTargets(state.Duel, P1, new[] { E1 }));
            Assert.Throws<ArgumentException>(() => engine.Duel.AllyTargets(state.Duel, P1, new[] { P2, P2 }));
            Assert.Throws<ArgumentException>(() => engine.Duel.AllyTargets(state.Duel, P1, new[] { new ParticipantId("absent") }));
            var invalid = Create(engine, support: new InvalidAllyPolicy());
            Assert.Throws<ArgumentException>(() => engine.Duel.AllyTargets(invalid.Duel, P1));
            var dying = EndPlayers(engine, Create(engine, hp2: 1));
            var dead = Do(engine, dying, E1, CardDuelAction.EnemyAction, targets: new[] { P2 });
            Assert.Throws<ArgumentException>(() => engine.Duel.AllyTargets(dead.Duel, P1, new[] { P2 }));
        }
        [Test] public void SeparateDecksAndBudgetsAllowInterleavedPlayerActions()
        {
            var engine = Engine(); var state = Create(engine);
            Assert.That(state.Table.Cards.Count, Is.EqualTo(104)); Assert.That(state.Table.Zones.Count, Is.EqualTo(8));
            var secondHand = state.Zone(CardZoneKinds.Hand, P2).Cards.ToArray();
            state = Do(engine, state, P1, CardDuelAction.Discard, state.Zone(CardZoneKinds.Hand, P1).Cards.Take(2));
            Assert.That(state.DiscardsFor(P1), Is.EqualTo(2)); Assert.That(state.DiscardsFor(P2), Is.EqualTo(3));
            CollectionAssert.AreEqual(secondHand, state.Zone(CardZoneKinds.Hand, P2).Cards);
            state = Do(engine, state, P2, CardDuelAction.Play, secondHand.Take(1));
            Assert.That(state.Duel.Player(P1).Energy.Energy, Is.EqualTo(2)); Assert.That(state.Duel.Player(P2).Energy.Energy, Is.EqualTo(2));
            Assert.That(state.Duel.PendingEnemies, Is.Empty); Assert.That(state.Duel.CompletedRounds, Is.Zero);
        }
        [Test] public void EveryoneMustEndAndEveryEnemyActsOnceBeforeAllLivingPlayersRefill()
        {
            var engine = Engine(); var state = Create(engine);
            state = Do(engine, state, P2, CardDuelAction.Discard, state.Zone(CardZoneKinds.Hand, P2).Cards.Take(1));
            state = Do(engine, state, P1, CardDuelAction.EndTurn);
            Assert.That(state.Duel.CanAct(P1), Is.False); Assert.That(state.Duel.CanAct(P2), Is.True);
            Assert.That(engine.Execute(state, state.Revision, E1, CardDuelAction.EnemyAction).Accepted, Is.False);
            state = Do(engine, state, P2, CardDuelAction.EndTurn);
            CollectionAssert.AreEqual(new[] { E1, E2, E3 }, state.Duel.PendingEnemies);
            Assert.That(engine.Execute(state, state.Revision, E2, CardDuelAction.EnemyAction).Accepted, Is.False);
            var old = state;
            state = Do(engine, state, E1, CardDuelAction.EnemyAction);
            Assert.That(state.Duel.CompletedRounds, Is.Zero); Assert.That(state.Duel.Player(P2).Energy.Energy, Is.Zero);
            Assert.That(engine.Execute(state, old.Revision, E2, CardDuelAction.EnemyAction).Error, Is.EqualTo(CardDuelError.StaleRevision));
            Assert.That(engine.Execute(state, state.Revision, E1, CardDuelAction.EnemyAction).Accepted, Is.False);
            state = Do(engine, state, E2, CardDuelAction.EnemyAction); state = Do(engine, state, E3, CardDuelAction.EnemyAction);
            Assert.That(state.Duel.CompletedRounds, Is.EqualTo(1)); Assert.That(state.Duel.Players.All(x => !x.HasEnded && x.Energy.Energy == 3), Is.True);
            Assert.That(state.Duel.Combat.Get(P1).Health, Is.EqualTo(19)); Assert.That(state.Duel.Combat.Get(P2).Health, Is.EqualTo(40));
        }
        [Test] public void AutoEndAffectsOnlyThePlayerWhoSpentTheirLastEnergy()
        {
            var engine = Engine(energy: 1); var state = Create(engine);
            state = Do(engine, state, P1, CardDuelAction.Play, state.Zone(CardZoneKinds.Hand, P1).Cards.Take(1));
            Assert.That(state.Duel.CanAct(P1), Is.False); Assert.That(state.Duel.CanAct(P2), Is.True);
            state = Do(engine, state, P1, CardDuelAction.CollectPlayed);
            Assert.That(state.Zone(CardZoneKinds.Played, P1).Cards, Is.Empty);
            Assert.That(engine.Execute(state, state.Revision, P1, CardDuelAction.EndTurn).Accepted, Is.False);
            state = Do(engine, state, P2, CardDuelAction.EndTurn); Assert.That(state.Duel.NextEnemy, Is.EqualTo(E1));
        }
        [Test] public void ForeignCardsAndInvalidTargetsRejectAtomically()
        {
            var engine = Engine(); var state = Create(engine);
            var stolen = engine.Execute(state, state.Revision, P1, CardDuelAction.Play, state.Zone(CardZoneKinds.Hand, P2).Cards.Take(1));
            Assert.That(stolen.Error, Is.EqualTo(CardDuelError.InvalidSelection)); Assert.That(stolen.State, Is.SameAs(state));
            var badTarget = engine.Execute(state, state.Revision, P1, CardDuelAction.Play, state.Zone(CardZoneKinds.Hand, P1).Cards.Take(1), new[] { P2 });
            Assert.That(badTarget.Error, Is.EqualTo(CardDuelError.InvalidTarget)); Assert.That(badTarget.State, Is.SameAs(state));
            Assert.That(state.Duel.Player(P1).Energy.Energy, Is.EqualTo(3));
        }
        [Test] public void DeathOfOnePlayerRetargetsLaterEnemiesWithoutEndingTheTeamBattle()
        {
            var engine = Engine(); var state = EndPlayers(engine, Create(engine, hp1: 1));
            state = Do(engine, state, E1, CardDuelAction.EnemyAction);
            Assert.That(state.Duel.Combat.Get(P1).IsAlive, Is.False); Assert.That(state.Duel.IsFinished, Is.False);
            state = Do(engine, state, E2, CardDuelAction.EnemyAction); state = Do(engine, state, E3, CardDuelAction.EnemyAction);
            Assert.That(state.Duel.Combat.Get(P2).Health, Is.EqualTo(26)); Assert.That(state.Duel.CanAct(P1), Is.False);
            state = Do(engine, state, P2, CardDuelAction.EndTurn); Assert.That(state.Duel.Phase, Is.EqualTo(ScoredDuelPhase.AwaitingEnemyAction));
        }
        [Test] public void AllPlayersDeadStopsRemainingEnemyQueue()
        {
            var engine = Engine(); var state = EndPlayers(engine, Create(engine, hp1: 1, hp2: 1));
            state = Do(engine, state, E1, CardDuelAction.EnemyAction); state = Do(engine, state, E2, CardDuelAction.EnemyAction);
            Assert.That(state.Duel.Phase, Is.EqualTo(ScoredDuelPhase.Lost)); Assert.That(state.Duel.PendingEnemies, Is.Empty);
            Assert.That(engine.Execute(state, state.Revision, E3, CardDuelAction.EnemyAction).Accepted, Is.False);
            Assert.That(engine.Duel.ReadProgress(state.Duel).Result.Reason, Is.EqualTo(EncounterEndReason.ParticipantEliminated));
        }
        [Test] public void DeadEnemyIsSkippedAndVictoryRequiresWholeEnemyRoster()
        {
            var engine = Engine(); var state = Create(engine, enemyHealth: 1);
            state = Do(engine, state, P2, CardDuelAction.Play, state.Zone(CardZoneKinds.Hand, P2).Cards.Take(1), new[] { E2 });
            Assert.That(state.Duel.Combat.Get(E2).IsAlive, Is.False); Assert.That(state.Duel.IsFinished, Is.False);
            state = Do(engine, state, P2, CardDuelAction.CollectPlayed); state = EndPlayers(engine, state);
            CollectionAssert.AreEqual(new[] { E1, E3 }, state.Duel.PendingEnemies);
            state = Do(engine, state, E1, CardDuelAction.EnemyAction); state = Do(engine, state, E3, CardDuelAction.EnemyAction);
            foreach (var target in new[] { E1, E3 })
            {
                state = Do(engine, state, P1, CardDuelAction.Play, state.Zone(CardZoneKinds.Hand, P1).Cards.Take(1), new[] { target });
                if (!state.Duel.IsFinished) state = Do(engine, state, P1, CardDuelAction.CollectPlayed);
            }
            Assert.That(state.Duel.Phase, Is.EqualTo(ScoredDuelPhase.Won));
        }
        private sealed class AllOpponents : IBattleTargetPolicy
        { public IReadOnlyList<ParticipantId> SelectTargets(BattleTargetContext context) => context.Opponents; }
        [Test] public void MultiTargetPolicyProducesOneCostAndOneEventPerTarget()
        {
            var engine = Engine(); var state = Create(engine, enemyHealth: 1, targeting: new AllOpponents());
            var result = engine.Execute(state, state.Revision, P2, CardDuelAction.Play, state.Zone(CardZoneKinds.Hand, P2).Cards.Take(1));
            Assert.That(result.Accepted, Is.True); Assert.That(result.Events.Count, Is.EqualTo(3)); Assert.That(result.Events.All(x => x.Died), Is.True);
            Assert.That(result.State.Duel.Player(P2).Energy.Energy, Is.EqualTo(2)); Assert.That(result.State.Duel.Phase, Is.EqualTo(ScoredDuelPhase.Won));
            Assert.That(result.State.Duel.Actions.Single().Actor, Is.EqualTo(P2));
            Assert.That(result.State.Duel.Actions.Single().ScoreIndex, Is.EqualTo(0));
        }
        [Test] public void EachActorUsesItsOwnHealthEnergyCostsHandAndDealPolicy()
        {
            var engine = new CardDuelEngine(new BattleRules("distinct", 10));
            var state = engine.Create("distinct", HmacDeckShuffler.FromSeed(new byte[32]), new[] {
                new BattlePlayerSetup(P1, new CombatantDefinition("hero-a", 60), rules: new BattlePlayerRules(3, 7, 3, 2, 5, 1,
                    new CardDuelDealPolicy(CardDuelRefillTiming.AfterAction, CardDuelRefillTiming.NextPlayerTurn), damagePolicy: new ScoreDamagePolicy(5))),
                new BattlePlayerSetup(P2, new CombatantDefinition("hero-b", 90), rules: new BattlePlayerRules(1, 2, 1, 2, 9, 4, damagePolicy: new ScoreDamagePolicy(5))) },
                new[] { new BattleEnemySetup(E1, new CombatantDefinition("small", 1000), 3), new BattleEnemySetup(E2, new CombatantDefinition("big", 2000), 11) });
            state = Do(engine, state, P1, CardDuelAction.Start);
            Assert.That(state.Zone(CardZoneKinds.Hand, P1).Cards.Count, Is.EqualTo(5)); Assert.That(state.Zone(CardZoneKinds.Hand, P2).Cards.Count, Is.EqualTo(9));
            state = Do(engine, state, P1, CardDuelAction.Play, state.Zone(CardZoneKinds.Hand, P1).Cards.Take(1));
            Assert.That(state.Duel.Player(P1).Energy.Energy, Is.EqualTo(4)); Assert.That(state.Zone(CardZoneKinds.Hand, P1).Cards.Count, Is.EqualTo(5));
            state = Do(engine, state, P1, CardDuelAction.CollectPlayed);
            state = Do(engine, state, P1, CardDuelAction.Discard, state.Zone(CardZoneKinds.Hand, P1).Cards.Take(1));
            Assert.That(state.Duel.Player(P1).Energy.Energy, Is.EqualTo(2)); Assert.That(state.Zone(CardZoneKinds.Hand, P1).Cards.Count, Is.EqualTo(4));
            Assert.That(state.DiscardsFor(P1), Is.Zero); Assert.That(state.DiscardsFor(P2), Is.EqualTo(4));
            state = Do(engine, state, P2, CardDuelAction.Discard, state.Zone(CardZoneKinds.Hand, P2).Cards.Take(1));
            Assert.That(state.Duel.Player(P2).HasEnded, Is.True); Assert.That(state.Duel.CanAct(P1), Is.True);
            state = Do(engine, state, P1, CardDuelAction.EndTurn);
            state = Do(engine, state, E1, CardDuelAction.EnemyAction); state = Do(engine, state, E2, CardDuelAction.EnemyAction);
            Assert.That(state.Duel.Combat.Get(P1).Health, Is.EqualTo(46)); Assert.That(state.Duel.Combat.Get(P2).Health, Is.EqualTo(90));
            Assert.That(state.Duel.Player(P1).Energy.Energy, Is.EqualTo(3)); Assert.That(state.Duel.Player(P2).Energy.Energy, Is.EqualTo(1));
            Assert.That(state.Zone(CardZoneKinds.Hand, P1).Cards.Count, Is.EqualTo(5)); Assert.That(state.Zone(CardZoneKinds.Hand, P2).Cards.Count, Is.EqualTo(9));
        }
        [Test] public void RoundLimitIsAppliedAfterWholeEnemyPhaseAndRosterIdsAreUnique()
        {
            var engine = Engine(rounds: 1); var state = EndPlayers(engine, Create(engine));
            state = Do(engine, state, E1, CardDuelAction.EnemyAction); state = Do(engine, state, E2, CardDuelAction.EnemyAction);
            Assert.That(state.Duel.IsFinished, Is.False); state = Do(engine, state, E3, CardDuelAction.EnemyAction);
            Assert.That(engine.Duel.ReadProgress(state.Duel).Result.Reason, Is.EqualTo(EncounterEndReason.RoundLimit));
            Assert.Throws<ArgumentException>(() => engine.Create("bad", HmacDeckShuffler.FromSeed(new byte[32]),
                new[] { new BattlePlayerSetup(P1, new CombatantDefinition("hero", 40)) }, new[] { new BattleEnemySetup(P1, new CombatantDefinition("enemy", 10000), 1) }));
        }
    }
}
