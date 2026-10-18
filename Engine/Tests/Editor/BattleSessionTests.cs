using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleSessionTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), SecondPlayer = new ParticipantId("player-2");
        private static readonly ParticipantId Enemy = new ParticipantId("enemy"), SecondEnemy = new ParticipantId("enemy-2"), ThirdEnemy = new ParticipantId("enemy-3");

        private static BattleSession Create(int energy = 3, int enemyHealth = 10000, int playerHealth = 40,
            long attack = 7, int rounds = 8, int playerCount = 1, int enemyCount = 1, int deckSize = 52,
            ICardDuelDealPolicy dealPolicy = null, IBattleTargetPolicy secondEnemyTargets = null)
        {
            var engine = new CardDuelEngine(new BattleRules("session.test", rounds));
            var rules = new BattlePlayerRules(maxEnergy: energy, initialEnergy: energy, dealPolicy: dealPolicy,
                unlimitedDiscards: true, damagePolicy: new ScoreDamagePolicy(5));
            var deck = StandardDeck.Cards().Take(deckSize).Select((card, i) => new DeckEntry("card/" + i, card.Id)).ToArray();
            var players = new[] { Player, SecondPlayer }.Take(playerCount).Select(id =>
                new BattlePlayerSetup(id, new CombatantDefinition("hero", playerHealth), deck, rules: rules));
            var enemies = new[] { Enemy, SecondEnemy, ThirdEnemy }.Take(enemyCount).Select(id =>
                new BattleEnemySetup(id, new CombatantDefinition("monster", enemyHealth), attack,
                    targetRules: new BattleActorTargetRules(id == SecondEnemy ? secondEnemyTargets : null)));
            return new BattleSession(engine, engine.Create("session", HmacDeckShuffler.FromSeed(new byte[32]), players, enemies));
        }

        private static BattleSessionResult Do(BattleSession session, ParticipantId actor, CardDuelAction action,
            IEnumerable<CardInstanceId> cards = null, IEnumerable<ParticipantId> targets = null)
        {
            var result = session.Execute(session.State.Revision, actor, action, cards, targets);
            Assert.That(result.Accepted, Is.True, action + ": " + result.Error);
            Assert.That(session.State, Is.SameAs(result.State));
            session.State.Table.Validate();
            return result;
        }

        private static void Start(BattleSession session) => Do(session, Player, CardDuelAction.Start);

        private static void SameState(CardDuelState expected, CardDuelState actual)
        {
            Assert.That(actual.Revision, Is.EqualTo(expected.Revision));
            Assert.That(actual.HasStarted, Is.EqualTo(expected.HasStarted));
            Assert.That(actual.Table.MatchId, Is.EqualTo(expected.Table.MatchId));
            CollectionAssert.AreEqual(expected.Random.StateBytes, actual.Random.StateBytes);
            foreach (var zone in expected.Table.Zones)
                CollectionAssert.AreEqual(zone.Cards, actual.Table.GetZone(zone.Id).Cards, zone.Id.ToString());
            Assert.That(actual.Duel.Revision, Is.EqualTo(expected.Duel.Revision));
            Assert.That(actual.Duel.Phase, Is.EqualTo(expected.Duel.Phase));
            Assert.That(actual.Duel.CompletedRounds, Is.EqualTo(expected.Duel.CompletedRounds));
            CollectionAssert.AreEqual(expected.Duel.PendingEnemies, actual.Duel.PendingEnemies);
            CollectionAssert.AreEqual(expected.Duel.Scores.Select(score => score.Total), actual.Duel.Scores.Select(score => score.Total));
            CollectionAssert.AreEqual(expected.Duel.Actions.Select(action => action.Actor + "/" + action.Kind),
                actual.Duel.Actions.Select(action => action.Actor + "/" + action.Kind));
            foreach (var player in expected.Duel.Players)
            {
                var other = actual.Duel.Player(player.Id);
                Assert.That(other.Energy.Energy, Is.EqualTo(player.Energy.Energy));
                Assert.That(other.HasEnded, Is.EqualTo(player.HasEnded));
                Assert.That(actual.DiscardsFor(player.Id), Is.EqualTo(expected.DiscardsFor(player.Id)));
            }
            foreach (var unit in expected.Duel.Combat.Combatants)
            {
                var other = actual.Duel.Combat.Get(unit.Id);
                Assert.That(other.Health, Is.EqualTo(unit.Health));
                Assert.That(other.Block, Is.EqualTo(unit.Block));
            }
        }

        [Test] public void SessionLivesInPureCompositionAndRejectsForeignRules()
        {
            var session = Create();
            Assert.That(typeof(BattleSession).Assembly.GetName().Name, Is.EqualTo("PokerEngine.Composition"));
            Assert.That(typeof(BattleSession).Assembly.GetReferencedAssemblies()
                .Any(reference => reference.Name.StartsWith("UnityEngine") || reference.Name.StartsWith("UnityEditor")), Is.False);
            Assert.Throws<ArgumentNullException>(() => new BattleSession(null, session.State));
            Assert.Throws<ArgumentNullException>(() => new BattleSession(session.Engine, null));
            Assert.Throws<ArgumentException>(() => new BattleSession(Create().Engine, session.State));
        }

        [Test] public void StartDealsOnceAndPublishesAnImmutableSnapshotChain()
        {
            var session = Create(playerCount: 2); var initial = session.State;
            Assert.That(initial.HasStarted, Is.False);
            var opening = Do(session, Player, CardDuelAction.Start);
            Assert.That(opening.Before, Is.SameAs(initial));
            Assert.That(opening.Steps.Count, Is.EqualTo(1));
            Assert.That(opening.Steps[0].Action, Is.EqualTo(CardDuelAction.Start));
            Assert.That(opening.Steps[0].Actor, Is.EqualTo(Player));
            Assert.That(opening.Steps[0].Before, Is.SameAs(initial));
            Assert.That(opening.Steps[0].Transition.State, Is.SameAs(opening.State));
            Assert.That(opening.State.Duel.Actions, Is.Empty);
            Assert.That(opening.State.Duel.Players.All(player => player.Energy.Energy == 3), Is.True);
            Assert.That(opening.State.Zone(CardZoneKinds.Hand, Player).Cards.Count, Is.EqualTo(8));
            Assert.That(opening.State.Zone(CardZoneKinds.Hand, SecondPlayer).Cards.Count, Is.EqualTo(8));
            var mutable = opening.Steps as IList<BattleSessionStep>;
            Assert.That(mutable, Is.Not.Null);
            Assert.Throws<NotSupportedException>(() => mutable.Clear());
            Assert.Throws<NotSupportedException>(() => mutable[0] = null);
            Do(session, Player, CardDuelAction.EndTurn);
            Assert.That(opening.State.Duel.Player(Player).Energy.Energy, Is.EqualTo(3));
            Assert.That(initial.Zone(CardZoneKinds.Hand, Player).Cards, Is.Empty);
        }

        [Test] public void RejectedCommandsExposeNoStepsAndKeepTheExactState()
        {
            var session = Create(); Start(session); var before = session.State;
            var card = before.Zone(CardZoneKinds.Hand, Player).Cards[0];
            var results = new[] {
                session.Execute(before.Revision - 1, Player, CardDuelAction.Play, new[] { card }),
                session.Execute(before.Revision, Player, CardDuelAction.Start),
                session.Execute(before.Revision, Enemy, CardDuelAction.EndTurn),
                session.Execute(before.Revision, Player, CardDuelAction.Play, new[] { card, card }),
                session.Execute(before.Revision, Player, CardDuelAction.Play, new[] { card }, new[] { Player })
            };
            CollectionAssert.AreEqual(new[] { CardDuelError.StaleRevision, CardDuelError.InvalidPhase, CardDuelError.InvalidPhase,
                CardDuelError.InvalidSelection, CardDuelError.InvalidTarget }, results.Select(result => result.Error));
            foreach (var result in results)
            {
                Assert.That(result.Accepted, Is.False); Assert.That(result.Before, Is.SameAs(before));
                Assert.That(result.State, Is.SameAs(before)); Assert.That(result.Steps, Is.Empty);
            }
            Assert.That(session.State, Is.SameAs(before));
        }

        [TestCase(CardDuelAction.EnemyAction)] [TestCase(CardDuelAction.CollectPlayed)] [TestCase((CardDuelAction)99)]
        public void AutomaticAndUnknownActionsCannotBeSubmitted(CardDuelAction action)
        {
            var session = Create(); Start(session); var before = session.State;
            var result = session.Execute(before.Revision, Player, action);
            Assert.That(result.Error, Is.EqualTo(CardDuelError.InvalidPhase));
            Assert.That(result.State, Is.SameAs(before)); Assert.That(result.Steps, Is.Empty);
            Assert.That(session.State, Is.SameAs(before));
        }

        [Test] public void PlayCollectsBeforeReturningToTheNextPlayerDecision()
        {
            var session = Create(); Start(session); var before = session.State;
            var cards = before.Zone(CardZoneKinds.Hand, Player).Cards.Take(2).ToArray();
            var result = Do(session, Player, CardDuelAction.Play, cards);
            CollectionAssert.AreEqual(new[] { CardDuelAction.Play, CardDuelAction.CollectPlayed }, result.Steps.Select(step => step.Action));
            CollectionAssert.AreEqual(cards, result.Steps[0].Selected);
            Assert.That(result.Steps[1].Selected, Is.Empty);
            CollectionAssert.AreEqual(cards, result.Steps[0].Transition.State.Zone(CardZoneKinds.Played, Player).Cards);
            Assert.That(result.Steps[1].Before, Is.SameAs(result.Steps[0].Transition.State));
            Assert.That(result.State.Zone(CardZoneKinds.Played, Player).Cards, Is.Empty);
            CollectionAssert.AreEqual(cards, result.State.Zone(CardZoneKinds.Discard, Player).Cards);
            Assert.That(result.State.Duel.Player(Player).Energy.Energy, Is.EqualTo(2));
            Assert.That(result.State.Duel.CompletedRounds, Is.Zero);
            Assert.That(result.State.Duel.Combat.Get(Player).Health, Is.EqualTo(40));
            var first = cards[0]; cards[0] = cards[1];
            Assert.That(result.Steps[0].Selected[0], Is.EqualTo(first));
            Assert.Throws<NotSupportedException>(() => ((IList<CardInstanceId>)result.Steps[0].Selected).Clear());
        }

        [Test] public void AllPlayersMustEndBeforeOrderedEnemiesResolveWithoutAFrontend()
        {
            var session = Create(energy: 1, playerCount: 2, enemyCount: 3); Start(session);
            var first = Do(session, Player, CardDuelAction.Play, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1));
            CollectionAssert.AreEqual(new[] { CardDuelAction.Play, CardDuelAction.CollectPlayed }, first.Steps.Select(step => step.Action));
            Assert.That(first.State.Duel.CanAct(Player), Is.False); Assert.That(first.State.Duel.CanAct(SecondPlayer), Is.True);
            var result = Do(session, SecondPlayer, CardDuelAction.EndTurn);
            CollectionAssert.AreEqual(new[] { SecondPlayer, Enemy, SecondEnemy, ThirdEnemy }, result.Steps.Select(step => step.Actor));
            CollectionAssert.AreEqual(new[] { CardDuelAction.EndTurn, CardDuelAction.EnemyAction, CardDuelAction.EnemyAction, CardDuelAction.EnemyAction },
                result.Steps.Select(step => step.Action));
            for (int index = 1; index < result.Steps.Count; index++)
                Assert.That(result.Steps[index].Before, Is.SameAs(result.Steps[index - 1].Transition.State));
            Assert.That(result.Steps[1].Transition.State.Duel.Player(Player).Energy.Energy, Is.Zero);
            Assert.That(result.State.Duel.CompletedRounds, Is.EqualTo(1));
            Assert.That(result.State.Duel.Players.All(player => player.Energy.Energy == 1), Is.True);
            Assert.That(result.State.Duel.Combat.Get(Player).Health, Is.EqualTo(19));
            Assert.That(result.State.Zone(CardZoneKinds.Hand, Player).Cards.Count, Is.EqualTo(8));
        }

        [Test] public void KillingTheLastEnemyPreservesPlayedAndStopsRefillAndRetaliation()
        {
            var session = Create(energy: 1, enemyHealth: 1, dealPolicy: new CardDuelDealPolicy(CardDuelRefillTiming.AfterAction)); Start(session);
            var card = session.State.Zone(CardZoneKinds.Hand, Player).Cards[0];
            var result = Do(session, Player, CardDuelAction.Play, new[] { card });
            Assert.That(result.Steps.Count, Is.EqualTo(1)); Assert.That(result.State.Duel.Phase, Is.EqualTo(ScoredDuelPhase.Won));
            CollectionAssert.AreEqual(new[] { card }, result.State.Zone(CardZoneKinds.Played, Player).Cards);
            Assert.That(result.State.Zone(CardZoneKinds.Hand, Player).Cards.Count, Is.EqualTo(7));
            Assert.That(result.State.Duel.Combat.Get(Player).Health, Is.EqualTo(40));
            var retry = session.Execute(result.State.Revision, Player, CardDuelAction.EndTurn);
            Assert.That(retry.Error, Is.EqualTo(CardDuelError.InvalidPhase)); Assert.That(session.State, Is.SameAs(result.State));
        }

        [Test] public void EnemyQueueRetargetsAfterDeathsAndStopsWhenThePlayersAreEliminated()
        {
            var session = Create(playerHealth: 1, playerCount: 2, enemyCount: 3); Start(session);
            Do(session, Player, CardDuelAction.EndTurn);
            var result = Do(session, SecondPlayer, CardDuelAction.EndTurn);
            CollectionAssert.AreEqual(new[] { SecondPlayer, Enemy, SecondEnemy }, result.Steps.Select(step => step.Actor));
            Assert.That(result.Steps[1].Transition.Events.Single().Effect.Target, Is.EqualTo(Player));
            Assert.That(result.Steps[2].Transition.Events.Single().Effect.Target, Is.EqualTo(SecondPlayer));
            Assert.That(result.State.Duel.Phase, Is.EqualTo(ScoredDuelPhase.Lost)); Assert.That(result.State.Duel.PendingEnemies, Is.Empty);
        }

        [Test] public void RequestedTargetsArePassedThroughAndDeadEnemiesAreSkipped()
        {
            var session = Create(enemyHealth: 1, enemyCount: 3); Start(session);
            var hit = Do(session, Player, CardDuelAction.Play, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1), new[] { SecondEnemy });
            Assert.That(hit.Steps[0].Transition.Events.Single().Effect.Target, Is.EqualTo(SecondEnemy));
            var ended = Do(session, Player, CardDuelAction.EndTurn);
            CollectionAssert.AreEqual(new[] { Player, Enemy, ThirdEnemy }, ended.Steps.Select(step => step.Actor));
        }

        [Test] public void RoundLimitStopsOnlyAfterTheWholeEnemyPhaseWithoutRefill()
        {
            var session = Create(energy: 1, rounds: 1, enemyCount: 3, attack: 0); Start(session);
            var result = Do(session, Player, CardDuelAction.Play, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1));
            Assert.That(result.Steps.Count, Is.EqualTo(4)); Assert.That(result.State.Duel.CompletedRounds, Is.EqualTo(1));
            Assert.That(result.State.Duel.Phase, Is.EqualTo(ScoredDuelPhase.Lost));
            Assert.That(result.State.Zone(CardZoneKinds.Hand, Player).Cards.Count, Is.EqualTo(7));
            Assert.That(result.State.Zone(CardZoneKinds.Played, Player).Cards, Is.Empty);
            Assert.That(session.Engine.Duel.ReadProgress(result.State.Duel).Result.Reason, Is.EqualTo(EncounterEndReason.RoundLimit));
        }

        private sealed class InvalidEnemyTargets : IBattleTargetPolicy
        { public IReadOnlyList<ParticipantId> SelectTargets(BattleTargetContext context) => new[] { context.Source }; }

        [Test] public void FailureInALaterAutomaticStepRollsBackTheEntireBatch()
        {
            var session = Create(energy: 1, enemyCount: 3, secondEnemyTargets: new InvalidEnemyTargets()); Start(session);
            var before = session.State; var cards = before.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            var result = session.Execute(before.Revision, Player, CardDuelAction.Play, cards);
            Assert.That(result.Error, Is.EqualTo(CardDuelError.InvalidTarget)); Assert.That(result.Steps, Is.Empty);
            Assert.That(result.Before, Is.SameAs(before)); Assert.That(result.State, Is.SameAs(before)); Assert.That(session.State, Is.SameAs(before));
            Assert.That(session.State.Duel.Scores, Is.Empty); Assert.That(session.State.Duel.Player(Player).Energy.Energy, Is.EqualTo(1));
            Assert.That(session.State.Duel.Combat.Get(Player).Health, Is.EqualTo(40));
        }

        [Test] public void RefillCannotShuffleTheCurrentPlayButDiscardCanRecycleItself()
        {
            var session = Create(deckSize: 8, dealPolicy: new CardDuelDealPolicy(CardDuelRefillTiming.AfterAction)); Start(session);
            var first = session.State.Zone(CardZoneKinds.Hand, Player).Cards[0];
            Do(session, Player, CardDuelAction.Play, new[] { first });
            Assert.That(session.State.Zone(CardZoneKinds.Hand, Player).Cards.Count, Is.EqualTo(7));
            var second = session.State.Zone(CardZoneKinds.Hand, Player).Cards[0];
            var next = Do(session, Player, CardDuelAction.Play, new[] { second });
            Assert.That(next.State.Zone(CardZoneKinds.Hand, Player).Cards.Contains(first), Is.True);
            Assert.That(next.State.Zone(CardZoneKinds.Hand, Player).Cards.Contains(second), Is.False);
            Assert.That(next.State.Zone(CardZoneKinds.Hand, Player).Cards.Count, Is.EqualTo(7));
            var discard = Do(session, Player, CardDuelAction.Discard, new[] { first });
            var discarded = discard.Steps[0].Transition.State;
            CollectionAssert.AreEqual(new[] { first }, discard.Steps[0].Selected);
            Assert.That(discarded.Zone(CardZoneKinds.Hand, Player).Cards.Count, Is.EqualTo(8));
            Assert.That(discarded.Zone(CardZoneKinds.Hand, Player).Cards.Contains(first), Is.True);
            Assert.That(discarded.Zone(CardZoneKinds.Hand, Player).Cards.Contains(second), Is.True);
            Assert.That(discarded.Zone(CardZoneKinds.Discard, Player).Cards, Is.Empty);
        }

        [TestCase(CardDuelRefillTiming.AfterAction)] [TestCase(CardDuelRefillTiming.NextPlayerTurn)]
        public void SessionMatchesTheExplicitEngineFlowIncludingRepeatedShuffles(CardDuelRefillTiming timing)
        {
            var session = Create(rounds: 20, enemyHealth: 1000000, attack: 0, deckSize: 10,
                dealPolicy: new CardDuelDealPolicy(timing, timing));
            var explicitState = session.State;
            for (int command = 0; command < 31; command++)
            {
                var action = command == 0 ? CardDuelAction.Start : command % 3 == 0 ? CardDuelAction.EndTurn :
                    command % 2 == 0 ? CardDuelAction.Discard : CardDuelAction.Play;
                var cards = action == CardDuelAction.Play || action == CardDuelAction.Discard
                    ? explicitState.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray() : null;
                var expected = new List<CardDuelTransition>();
                var actions = new List<CardDuelAction>();
                var step = session.Engine.Execute(explicitState, explicitState.Revision, Player, action, cards);
                Assert.That(step.Accepted, Is.True); expected.Add(step); actions.Add(action); explicitState = step.State;
                while (explicitState.Duel.Phase == ScoredDuelPhase.AwaitingEnemyAction)
                {
                    step = session.Engine.Execute(explicitState, explicitState.Revision, explicitState.Duel.NextEnemy, CardDuelAction.EnemyAction);
                    Assert.That(step.Accepted, Is.True); expected.Add(step); actions.Add(CardDuelAction.EnemyAction); explicitState = step.State;
                }
                if (explicitState.Duel.Phase == ScoredDuelPhase.AwaitingPlayerHand && explicitState.Zone(CardZoneKinds.Played, Player).Cards.Count > 0)
                {
                    step = session.Engine.Execute(explicitState, explicitState.Revision, Player, CardDuelAction.CollectPlayed);
                    Assert.That(step.Accepted, Is.True); expected.Add(step); actions.Add(CardDuelAction.CollectPlayed); explicitState = step.State;
                }
                var actual = Do(session, Player, action, cards);
                CollectionAssert.AreEqual(actions, actual.Steps.Select(item => item.Action));
                for (int index = 0; index < expected.Count; index++) SameState(expected[index].State, actual.Steps[index].Transition.State);
                SameState(explicitState, session.State);
            }
            Assert.That(session.State.Duel.CompletedRounds, Is.EqualTo(10));
        }
    }
}
