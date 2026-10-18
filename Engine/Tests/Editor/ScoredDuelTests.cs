using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class ScoredDuelTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player");
        private static readonly ParticipantId Enemy = new ParticipantId("enemy");
        private static ScoredDuelEngine Engine(long attack = 3, int rounds = 3, int enemyHealth = 10, long points = 10) =>
            new ScoredDuelEngine(new ScoredDuelDefinition("test.1", new CombatantDefinition("hero", 20, 2),
                new CombatantDefinition("slime", enemyHealth, 1), new ScoreDamagePolicy(points), attack, rounds, maxEnergy: 1));
        private static ScoredDuelState Start(ScoredDuelEngine engine) => engine.Create("battle", Player, Enemy);
        private static ScoredDuelTransition Hit(ScoredDuelEngine engine, ScoredDuelState state, long score) =>
            engine.ResolveScoredHand(state, state.Revision, ScoreEngine.Fixed(engine.NextScoreId(state), score));
        private static void Change(object target, string name, object value) => target.GetType().GetProperty(name,
            BindingFlags.Public | BindingFlags.Instance).GetSetMethod(true).Invoke(target, new[] { value });

        [Test] public void ActualPokerPatternUsesExistingScoringBeforeDamage()
        {
            var engine = Engine(enemyHealth: 100); var state = Start(engine);
            var faces = new[] { new CardFace(Rank.Ace, Suit.Spades), new CardFace(Rank.Ace, Suit.Hearts) };
            var cards = faces.Select((face, i) => new EffectiveCard(new CardInstanceId("card" + i), face.Rank, face.Suit)).ToArray();
            var pattern = PokerHandClassifier.Classify(cards);
            var score = ScoreEngine.EvaluatePattern(engine.NextScoreId(state), ScoreProfile.Default, pattern, 1,
                faces.Select((face, i) => new ScoringCard("card" + i, face)), new ScoringRelic[0]);
            var result = engine.ResolveScoredHand(state, 0, score);
            Assert.That(result.Accepted, Is.True); Assert.That(result.Events.Single().Effect.Amount, Is.EqualTo(score.Total / 10));
            Assert.That(result.State.Scores.Single(), Is.SameAs(score)); Assert.That(result.State.Phase, Is.EqualTo(ScoredDuelPhase.AwaitingEnemyAction));
            Assert.That(engine.ReadProgress(result.State).CurrentActor, Is.EqualTo(Enemy));
        }
        [Test] public void LivingEnemyRetaliatesOnceAndUsesSameBlockResolver()
        {
            var engine = Engine(); var hit = Hit(engine, Start(engine), 50).State;
            Assert.That(hit.Combat.Get(Enemy).Health, Is.EqualTo(6)); Assert.That(hit.CompletedRounds, Is.Zero);
            Assert.That(Hit(engine, hit, 50).Error, Is.EqualTo(ScoredDuelError.InvalidPhase));
            var response = engine.ResolveEnemyAction(hit, hit.Revision);
            Assert.That(response.State.Combat.Get(Player).Health, Is.EqualTo(19)); Assert.That(response.Events[0].BlockAbsorbed, Is.EqualTo(2));
            Assert.That(response.State.CompletedRounds, Is.EqualTo(1)); Assert.That(response.State.Phase, Is.EqualTo(ScoredDuelPhase.AwaitingPlayerHand));
            Assert.That(engine.ResolveEnemyAction(response.State, response.State.Revision).Error, Is.EqualTo(ScoredDuelError.InvalidPhase));
        }
        [Test] public void KillingEnemyCompletesRoundWithoutRetaliationAndProjectsBothOutcomes()
        {
            var engine = Engine(attack: long.MaxValue); var won = Hit(engine, Start(engine), 110).State;
            Assert.That(won.Phase, Is.EqualTo(ScoredDuelPhase.Won)); Assert.That(won.Combat.Get(Player).Health, Is.EqualTo(20));
            Assert.That(engine.ResolveEnemyAction(won, won.Revision).Accepted, Is.False);
            var result = engine.ReadProgress(won).Result;
            Assert.That(result.CompletedRounds, Is.EqualTo(1)); Assert.That(result.Reason, Is.EqualTo(EncounterEndReason.ObjectiveReached));
            Assert.That(result.For(Player).Outcome, Is.EqualTo(EncounterOutcome.Won)); Assert.That(result.For(Enemy).Outcome, Is.EqualTo(EncounterOutcome.Lost));
            result.Validate(engine.Describe(won));
        }
        [Test] public void PlayerDeathAndRoundLimitHaveDistinctReasons()
        {
            var lethal = Engine(attack: 100); var pending = Hit(lethal, Start(lethal), 0).State;
            var dead = lethal.ResolveEnemyAction(pending, pending.Revision).State;
            Assert.That(lethal.ReadProgress(dead).Result.Reason, Is.EqualTo(EncounterEndReason.ParticipantEliminated));
            var limited = Engine(attack: 0, rounds: 1); pending = Hit(limited, Start(limited), 0).State;
            var timedOut = limited.ResolveEnemyAction(pending, pending.Revision).State;
            Assert.That(timedOut.Combat.Get(Player).IsAlive, Is.True);
            Assert.That(limited.ReadProgress(timedOut).Result.Reason, Is.EqualTo(EncounterEndReason.RoundLimit));
            Assert.That(Hit(limited, timedOut, 1000).Accepted, Is.False);
        }
        [Test] public void StaleForeignAndRepeatedScoresAreRejectedWithoutEvents()
        {
            var engine = Engine(); var state = Start(engine); var score = ScoreEngine.Fixed(engine.NextScoreId(state), 20);
            Assert.That(engine.ResolveScoredHand(state, 1, score).Error, Is.EqualTo(ScoredDuelError.StaleRevision));
            Assert.That(engine.ResolveScoredHand(state, 0, ScoreEngine.Fixed("other/score/0", 20)).Error, Is.EqualTo(ScoredDuelError.InvalidScore));
            var pending = engine.ResolveScoredHand(state, 0, score).State; var next = engine.ResolveEnemyAction(pending, pending.Revision).State;
            var repeat = engine.ResolveScoredHand(next, next.Revision, score);
            Assert.That(repeat.Error, Is.EqualTo(ScoredDuelError.InvalidScore)); Assert.That(repeat.State, Is.SameAs(next)); Assert.That(repeat.Events, Is.Empty);
        }
        [Test] public void ScoreArithmeticAndAccumulatedOverflowAreChecked()
        {
            var engine = Engine(attack: 0, points: long.MaxValue); var state = Start(engine);
            var forged = ScoreEngine.Fixed(engine.NextScoreId(state), 20); Change(forged, "Total", 999L);
            Assert.That(engine.ResolveScoredHand(state, 0, forged).Error, Is.EqualTo(ScoredDuelError.InvalidScore));
            var pending = Hit(engine, state, long.MaxValue).State; var next = engine.ResolveEnemyAction(pending, pending.Revision).State;
            var overflow = Hit(engine, next, 1);
            Assert.That(overflow.Error, Is.EqualTo(ScoredDuelError.Overflow)); Assert.That(overflow.State, Is.SameAs(next));
            Assert.That(new ScoreDamagePolicy(10).Calculate(ScoreEngine.Fixed("floor", 9)), Is.Zero);
            Assert.Throws<ArgumentOutOfRangeException>(() => new ScoreDamagePolicy(0));
        }
        [Test] public void SnapshotRestoresPendingEnemyActionAndContinuesDeterministically()
        {
            var engine = Engine(); var pending = Hit(engine, Start(engine), 50).State;
            var json = ScoredDuelSnapshotCodec.Serialize(pending, engine);
            var restored = ScoredDuelSnapshotCodec.Deserialize(json, engine);
            var expected = engine.ResolveEnemyAction(pending, pending.Revision).State;
            var actual = engine.ResolveEnemyAction(restored, restored.Revision).State;
            Assert.That(ScoredDuelSnapshotCodec.Serialize(actual, engine), Is.EqualTo(ScoredDuelSnapshotCodec.Serialize(expected, engine)));
            var won = Hit(engine, actual, 60).State;
            var terminal = ScoredDuelSnapshotCodec.Deserialize(ScoredDuelSnapshotCodec.Serialize(won, engine), engine);
            Assert.That(engine.ReadProgress(terminal).Result.For(Player).Outcome, Is.EqualTo(EncounterOutcome.Won));
        }
        [Test] public void SnapshotRejectsChangedRulesAndUnreachableVitalsOrPhase()
        {
            var engine = Engine(); var pending = Hit(engine, Start(engine), 50).State;
            var json = ScoredDuelSnapshotCodec.Serialize(pending, engine);
            Assert.Throws<SnapshotException>(() => ScoredDuelSnapshotCodec.Deserialize(json, Engine(attack: 4)));
            Assert.Throws<SnapshotException>(() => ScoredDuelSnapshotCodec.Deserialize(json.Replace("\"Health\":6", "\"Health\":7"), engine));
            Assert.Throws<SnapshotException>(() => ScoredDuelSnapshotCodec.Deserialize(json.Replace("\"Phase\":2", "\"Phase\":3"), engine));
            Assert.Throws<SnapshotException>(() => ScoredDuelSnapshotCodec.Deserialize(json.Replace("\"Revision\":1", "\"Revision\":2"), engine));
            Assert.Throws<SnapshotException>(() => ScoredDuelSnapshotCodec.Deserialize(json.Replace("\"SchemaVersion\":4", "\"SchemaVersion\":3"), engine));
            Assert.Throws<SnapshotException>(() => ScoredDuelSnapshotCodec.Deserialize("{}", engine));
        }
        [Test] public void DamagedPlayerStartsFromExplicitRunInputWithoutSharingItsMutableState()
        {
            var engine = Engine(); var state = engine.Create("battle", Player, Enemy, 7);
            Assert.That(state.Combat.Get(Player).Health, Is.EqualTo(7));
            Assert.That(engine.Describe(state).Definition.Participants.All(p => p.InitialResources == null), Is.True);
            Assert.That(ScoredDuelSnapshotCodec.Deserialize(ScoredDuelSnapshotCodec.Serialize(state, engine), engine).InitialPlayerHealth, Is.EqualTo(7));
            Assert.Throws<ArgumentException>(() => engine.Create("battle", Player, Enemy, 0));
            Assert.Throws<ArgumentException>(() => engine.Create("battle", Player, Player));
        }
        [Test] public void CombatSnapshotKeepsPlayerEnergyOutsideUnits()
        {
            var engine = Engine(); var state = engine.Create("battle", Player, Enemy, 7);
            var json = ScoredDuelSnapshotCodec.Serialize(state, engine);
            Assert.That(json, Does.Not.Contain("\"Resources\""));
            Assert.That(json, Does.Not.Contain("\"Chips\""));
            Assert.That(typeof(CombatantState).GetProperty("Energy"), Is.Null);
            Assert.That(json, Does.Contain("\"PlayerTurn\""));
            var restored = ScoredDuelSnapshotCodec.Deserialize(json, engine);
            Assert.That(restored.Combat.Get(Player).Health, Is.EqualTo(7));
            Assert.That(restored.Combat.Get(Enemy).Health, Is.EqualTo(10));
            var pending = Hit(engine, restored, 50).State;
            var response = engine.ResolveEnemyAction(pending, pending.Revision).State;
            Assert.That(response.Combat.Get(Player).Health, Is.EqualTo(6));
            Assert.That(response.Combat.Get(Enemy).Health, Is.EqualTo(6));
        }
        [Test] public void SnapshotRequiresExplicitBoundedHealthInsteadOfDefaultingMissingData()
        {
            var engine = Engine(); var json = ScoredDuelSnapshotCodec.Serialize(Start(engine), engine);
            Assert.That(json, Does.Contain("\"Health\":20"));
            Assert.Throws<SnapshotException>(() => ScoredDuelSnapshotCodec.Deserialize(json.Replace("\"Health\":20", "\"Health\":null"), engine));
            Assert.Throws<SnapshotException>(() => ScoredDuelSnapshotCodec.Deserialize(json.Replace("\"Health\":20", "\"Health\":21"), engine));
            // A v1 nested resource payload must not silently become zero HP even if its envelope is mislabeled v3.
            var legacyShape = json.Replace("\"Health\":20", "\"Resources\":{\"Health\":20,\"Energy\":0}");
            Assert.Throws<SnapshotException>(() => ScoredDuelSnapshotCodec.Deserialize(legacyShape, engine));
        }
        [Test] public void MalformedHealthAndUnimplementedStatusesCannotEnterDuelFlow()
        {
            var engine = Engine(); var state = Start(engine);
            Change(state.Combat.Get(Player), "Health", -1);
            Assert.That(Hit(engine, state, 10).Error, Is.EqualTo(ScoredDuelError.InvalidState));
            state = Start(engine);
            var status = new StatusInstance(new StatusInstanceId("mark"), new StatusDefinitionId("mark"),
                EntityRef.Participant(Player), EntityRef.Participant(Enemy), 1, StatusDuration.UntilRemoved());
            Change(state, "Combat", new CombatState(state.Combat.Id, state.Revision, state.Combat.Combatants, new[] { status }));
            Assert.Throws<ArgumentException>(() => engine.Validate(state));
        }
    }
}
