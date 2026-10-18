using System;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class PlayerTurnTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), Enemy = new ParticipantId("enemy");
        private static ScoredDuelEngine Engine(int energy = 3, int cost = 1, int rounds = 3, int health = 1000, long attack = 2) =>
            new ScoredDuelEngine(new ScoredDuelDefinition("energy.test.1", new CombatantDefinition("hero", 20),
                new CombatantDefinition("enemy", health), new ScoreDamagePolicy(10), attack, rounds, energy, cost));
        private static ScoredDuelState Start(ScoredDuelEngine engine) => engine.Create("energy", Player, Enemy);
        private static ScoredDuelState Hit(ScoredDuelEngine engine, ScoredDuelState state, long points = 10) =>
            engine.ResolveScoredHand(state, state.Revision, ScoreEngine.Fixed(engine.NextScoreId(state), points)).State;
        [Test] public void SeveralHandsSpendEnergyWithinOneRoundAndOnlyOneEnemyActionFollows()
        {
            var engine = Engine(); var state = Start(engine);
            for (int i = 0; i < 3; i++)
            {
                state = Hit(engine, state);
                Assert.That(state.PlayerTurn.Energy, Is.EqualTo(2 - i)); Assert.That(state.CompletedRounds, Is.Zero);
                Assert.That(state.Combat.Get(Player).Health, Is.EqualTo(20));
                Assert.That(state.Phase, Is.EqualTo(i == 2 ? ScoredDuelPhase.AwaitingEnemyAction : ScoredDuelPhase.AwaitingPlayerHand));
            }
            Assert.That(state.Scores.Count, Is.EqualTo(3));
            Assert.That(engine.ResolveScoredHand(state, state.Revision, ScoreEngine.Fixed(engine.NextScoreId(state), 10)).Accepted, Is.False);
            var response = engine.ResolveEnemyAction(state, state.Revision);
            Assert.That(response.Accepted, Is.True); Assert.That(response.State.CompletedRounds, Is.EqualTo(1));
            Assert.That(response.State.PlayerTurn.Energy, Is.EqualTo(3)); Assert.That(response.State.Combat.Get(Player).Health, Is.EqualTo(18));
            Assert.That(engine.ResolveEnemyAction(response.State, response.State.Revision).Accepted, Is.False);
        }
        [Test] public void ManualEndCanSkipAllPlaysAndDiscardsUnusedEnergy()
        {
            var engine = Engine(); var before = Start(engine); var ended = engine.EndPlayerTurn(before, 0);
            Assert.That(ended.Accepted, Is.True); Assert.That(ended.Events, Is.Empty);
            Assert.That(ended.State.Combat, Is.SameAs(before.Combat)); Assert.That(ended.State.Revision, Is.EqualTo(1));
            Assert.That(ended.State.Combat.Revision, Is.Zero); Assert.That(ended.State.PlayerTurn.Energy, Is.Zero);
            Assert.That(ended.State.Scores, Is.Empty); Assert.That(ended.State.CompletedRounds, Is.Zero);
            Assert.That(engine.EndPlayerTurn(ended.State, ended.State.Revision).Accepted, Is.False);
            Assert.That(engine.EndPlayerTurn(before, 1).Error, Is.EqualTo(ScoredDuelError.StaleRevision));
            var next = engine.ResolveEnemyAction(ended.State, ended.State.Revision).State;
            Assert.That(next.PlayerTurn.Energy, Is.EqualTo(3)); Assert.That(next.CompletedRounds, Is.EqualTo(1)); Assert.That(next.Scores, Is.Empty);
            engine.Validate(next);
        }
        [Test] public void TurnLimitCountsEnemyRoundsInsteadOfNumberOfScoredHands()
        {
            var engine = Engine(rounds: 1, attack: 0); var state = Start(engine);
            state = Hit(engine, Hit(engine, Hit(engine, state)));
            Assert.That(state.IsFinished, Is.False); Assert.That(state.Scores.Count, Is.EqualTo(3));
            state = engine.ResolveEnemyAction(state, state.Revision).State;
            Assert.That(state.Phase, Is.EqualTo(ScoredDuelPhase.Lost)); Assert.That(state.CompletedRounds, Is.EqualTo(1));
            Assert.That(engine.ReadProgress(state).Result.Reason, Is.EqualTo(EncounterEndReason.RoundLimit));
            Assert.That(engine.EndPlayerTurn(state, state.Revision).Accepted, Is.False);
        }
        [Test] public void KillingBeforeEnergyRunsOutStillEndsBattleWithoutRetaliation()
        {
            var engine = Engine(health: 1, attack: 100); var state = Hit(engine, Start(engine));
            Assert.That(state.Phase, Is.EqualTo(ScoredDuelPhase.Won)); Assert.That(state.CompletedRounds, Is.EqualTo(1));
            Assert.That(state.PlayerTurn.Energy, Is.EqualTo(2)); Assert.That(state.Combat.Get(Player).Health, Is.EqualTo(20));
            Assert.That(engine.EndPlayerTurn(state, state.Revision).Accepted, Is.False);
            Assert.That(engine.ResolveEnemyAction(state, state.Revision).Accepted, Is.False); engine.Validate(state);
        }
        [Test] public void UnaffordablePlayRemainderCanFundADiscardThenResetNextRound()
        {
            var engine = Engine(energy: 5, cost: 2); var state = Hit(engine, Start(engine));
            Assert.That(state.PlayerTurn.Energy, Is.EqualTo(3)); state = Hit(engine, state);
            Assert.That(state.PlayerTurn.Energy, Is.EqualTo(1)); Assert.That(state.Phase, Is.EqualTo(ScoredDuelPhase.AwaitingPlayerHand));
            Assert.That(engine.ResolveScoredHand(state, state.Revision, ScoreEngine.Fixed(engine.NextScoreId(state), 10)).Error,
                Is.EqualTo(ScoredDuelError.InsufficientEnergy));
            state = engine.ResolveDiscard(state, state.Revision).State;
            Assert.That(state.PlayerTurn.Energy, Is.Zero); Assert.That(state.Phase, Is.EqualTo(ScoredDuelPhase.AwaitingEnemyAction));
            state = engine.ResolveEnemyAction(state, state.Revision).State; Assert.That(state.PlayerTurn.Energy, Is.EqualTo(5));
            Assert.Throws<ArgumentException>(() => Engine(energy: 0)); Assert.Throws<ArgumentException>(() => Engine(cost: 0));
            var expensive = Engine(energy: 2, cost: 3);
            Assert.That(expensive.ResolveScoredHand(Start(expensive), 0, ScoreEngine.Fixed("energy/score/0", 10)).Error, Is.EqualTo(ScoredDuelError.InsufficientEnergy));
        }
        [Test] public void SnapshotsRestorePartialTurnsAndZeroPlayTurnEndsWithoutExtraEnemyAttacks()
        {
            var engine = Engine(); var partial = Hit(engine, Start(engine));
            var restored = ScoredDuelSnapshotCodec.Deserialize(ScoredDuelSnapshotCodec.Serialize(partial, engine), engine);
            Assert.That(restored.PlayerTurn.Energy, Is.EqualTo(2)); Assert.That(restored.CompletedRounds, Is.Zero);
            var ended = engine.EndPlayerTurn(restored, restored.Revision).State;
            var resumed = ScoredDuelSnapshotCodec.Deserialize(ScoredDuelSnapshotCodec.Serialize(ended, engine), engine);
            var response = engine.ResolveEnemyAction(resumed, resumed.Revision).State;
            Assert.That(response.CompletedRounds, Is.EqualTo(1)); Assert.That(response.PlayerTurn.Energy, Is.EqualTo(3));
            var skipped = engine.EndPlayerTurn(response, response.Revision).State;
            resumed = ScoredDuelSnapshotCodec.Deserialize(ScoredDuelSnapshotCodec.Serialize(skipped, engine), engine);
            response = engine.ResolveEnemyAction(resumed, resumed.Revision).State;
            Assert.That(response.CompletedRounds, Is.EqualTo(2)); Assert.That(response.Scores.Count, Is.EqualTo(1));
            Assert.That(response.Combat.Get(Player).Health, Is.EqualTo(16));
        }
        [Test] public void ForgedEnergyRoundCountAndActionLedgerAreRejected()
        {
            var engine = Engine(); var state = Hit(engine, Start(engine)); var json = ScoredDuelSnapshotCodec.Serialize(state, engine);
            Assert.That(json, Does.Contain("\"Energy\":2"));
            Assert.Throws<SnapshotException>(() => ScoredDuelSnapshotCodec.Deserialize(json.Replace("\"Energy\":2", "\"Energy\":3"), engine));
            Assert.Throws<SnapshotException>(() => ScoredDuelSnapshotCodec.Deserialize(json.Replace("\"CompletedRounds\":0", "\"CompletedRounds\":1"), engine));
            Assert.Throws<SnapshotException>(() => ScoredDuelSnapshotCodec.Deserialize(json.Replace("\"ScoreIndex\":0", "\"ScoreIndex\":1"), engine));
            Assert.Throws<SnapshotException>(() => ScoredDuelSnapshotCodec.Deserialize(json.Replace("\"Kind\":1", "\"Kind\":3"), engine));
            Assert.Throws<SnapshotException>(() => ScoredDuelSnapshotCodec.Deserialize(json, Engine(energy: 4)));
        }
        [Test] public void InitialEnergyCanExceedCapacityAndBothActionCostsReplayWithoutClamping()
        {
            var engine = new ScoredDuelEngine(new ScoredDuelDefinition("independent-energy", new CombatantDefinition("hero", 20),
                new CombatantDefinition("enemy", 1000), new ScoreDamagePolicy(10), 0, 3,
                maxEnergy: 3, playEnergyCost: 3, initialEnergy: 7, discardEnergyCost: 2));
            var state = Start(engine); Assert.That(state.PlayerTurn.MaxEnergy, Is.EqualTo(3)); Assert.That(state.PlayerTurn.Energy, Is.EqualTo(7));
            state = engine.ResolveDiscard(state, state.Revision).State; Assert.That(state.PlayerTurn.Energy, Is.EqualTo(5));
            state = Hit(engine, state); Assert.That(state.PlayerTurn.Energy, Is.EqualTo(2));
            Assert.That(state.Phase, Is.EqualTo(ScoredDuelPhase.AwaitingPlayerHand));
            var json = ScoredDuelSnapshotCodec.Serialize(state, engine);
            var restored = ScoredDuelSnapshotCodec.Deserialize(json, engine); Assert.That(restored.PlayerTurn.Energy, Is.EqualTo(2));
            Assert.Throws<SnapshotException>(() => ScoredDuelSnapshotCodec.Deserialize(json.Replace("\"Energy\":2", "\"Energy\":5"), engine));
            state = engine.ResolveDiscard(restored, restored.Revision).State;
            Assert.That(state.Phase, Is.EqualTo(ScoredDuelPhase.AwaitingEnemyAction)); Assert.That(state.PlayerTurn.Energy, Is.Zero);
            state = engine.ResolveEnemyAction(state, state.Revision).State;
            Assert.That(state.PlayerTurn.Energy, Is.EqualTo(3)); Assert.That(state.CompletedRounds, Is.EqualTo(1)); engine.Validate(state);
            var full = new PlayerTurnState(3, int.MaxValue); Assert.That(full.Energy, Is.EqualTo(int.MaxValue));
        }
        [Test] public void UnaffordableDiscardLeavesEnergyAndLedgerUntouched()
        {
            var engine = new ScoredDuelEngine(new ScoredDuelDefinition("expensive-discard", new CombatantDefinition("hero", 20),
                new CombatantDefinition("enemy", 1000), new ScoreDamagePolicy(10), 0, 2,
                maxEnergy: 3, initialEnergy: 1, discardEnergyCost: 2));
            var state = Start(engine); var result = engine.ResolveDiscard(state, 0);
            Assert.That(result.Error, Is.EqualTo(ScoredDuelError.InsufficientEnergy)); Assert.That(result.State, Is.SameAs(state));
            Assert.That(state.PlayerTurn.Energy, Is.EqualTo(1)); Assert.That(state.Actions, Is.Empty);
            Assert.That(engine.EndPlayerTurn(state, 0).Accepted, Is.True);
        }
    }
}
