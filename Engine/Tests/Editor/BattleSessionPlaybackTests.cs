using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleSessionPlaybackTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player");
        private static readonly ParticipantId Enemy = new ParticipantId("enemy");

        private static BattleSession Create(int energy = 2)
        {
            var engine = new CardDuelEngine(new BattleRules("playback.test", 20));
            var rules = new BattlePlayerRules(maxEnergy: energy, initialEnergy: energy, unlimitedDiscards: true,
                dealPolicy: new CardDuelDealPolicy(CardDuelRefillTiming.AfterAction, CardDuelRefillTiming.AfterAction),
                damagePolicy: new ScoreDamagePolicy(5));
            var deck = StandardDeck.Cards().Take(10).Select((card, index) => new DeckEntry("card/" + index, card.Id)).ToArray();
            return new BattleSession(engine, engine.Create("playback", HmacDeckShuffler.FromSeed(new byte[32]),
                new[] { new BattlePlayerSetup(Player, new CombatantDefinition("hero", 1000), deck, rules: rules) },
                new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("enemy", 1000000), 7) }));
        }

        private static BattleSession CreateConfigured(BattlePlayerRules rules, int playerCount = 1, bool disabled = false, int enemyHealth = 1000000)
        {
            var engine = new CardDuelEngine(new BattleRules("playback.configured", 20));
            var deck = StandardDeck.Cards().Take(10)
                .Select((card, index) => new DeckEntry("card/" + index, card.Id, isDisabled: disabled)).ToArray();
            var players = Enumerable.Range(0, playerCount).Select(index => new BattlePlayerSetup(
                index == 0 ? Player : new ParticipantId("player/" + index), new CombatantDefinition("hero/" + index, 1000), deck, rules: rules));
            return new BattleSession(engine, engine.Create("playback.configured", HmacDeckShuffler.FromSeed(new byte[32]),
                players, new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("enemy", enemyHealth), 7) }));
        }

        private sealed class ImmediatePresentation : IBattleSessionPresentation
        {
            public bool IsPresenting => false;
            public CardDuelState State;
            public int Initializations, Synchronizations;
            public readonly List<BattleSessionResult> Batches = new List<BattleSessionResult>();
            public bool LastAnimate;
            public Exception Failure;
            public void Initialize(CardDuelState initialState) { State = initialState; Initializations++; }
            public void Present(BattleSessionResult batch, bool animate)
            {
                Batches.Add(batch); LastAnimate = animate;
                if (Failure != null) throw Failure;
                State = batch.State;
            }
            public void Synchronize(CardDuelState state) { State = state; Synchronizations++; }
        }

        private sealed class ManualPresentation : IBattleSessionPresentation
        {
            public bool IsPresenting => pending != null;
            public CardDuelState State;
            public readonly List<BattleSessionResult> Batches = new List<BattleSessionResult>();
            public bool LastAnimate;
            private BattleSessionResult pending;
            private int index;
            public void Initialize(CardDuelState initialState) { State = initialState; pending = null; }
            public void Present(BattleSessionResult batch, bool animate)
            {
                Batches.Add(batch); LastAnimate = animate;
                if (!animate) { Synchronize(batch.State); return; }
                State = batch.Before; pending = batch; index = 0;
            }
            public bool Advance()
            {
                if (pending == null) return false;
                State = pending.Steps[index++].Transition.State;
                if (index == pending.Steps.Count) pending = null;
                return true;
            }
            public void Synchronize(CardDuelState state) { State = state; pending = null; }
        }

        private static void SameState(CardDuelState expected, CardDuelState actual)
        {
            Assert.AreEqual(expected.Revision, actual.Revision);
            Assert.AreEqual(expected.HasStarted, actual.HasStarted);
            Assert.AreEqual(expected.Duel.Phase, actual.Duel.Phase);
            Assert.AreEqual(expected.Duel.CompletedRounds, actual.Duel.CompletedRounds);
            Assert.AreEqual(expected.Duel.Player(Player).Energy.Energy, actual.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(expected.DiscardsFor(Player), actual.DiscardsFor(Player));
            CollectionAssert.AreEqual(expected.Random.StateBytes, actual.Random.StateBytes);
            CollectionAssert.AreEqual(expected.Duel.Actions.Select(action => action.Actor + "/" + action.Kind),
                actual.Duel.Actions.Select(action => action.Actor + "/" + action.Kind));
            CollectionAssert.AreEqual(expected.Duel.Scores.Select(score => score.Total), actual.Duel.Scores.Select(score => score.Total));
            foreach (var unit in expected.Duel.Combat.Combatants)
            {
                Assert.AreEqual(unit.Health, actual.Duel.Combat.Get(unit.Id).Health);
                Assert.AreEqual(unit.Block, actual.Duel.Combat.Get(unit.Id).Block);
            }
            foreach (var zone in expected.Table.Zones)
                CollectionAssert.AreEqual(zone.Cards, actual.Table.GetZone(zone.Id).Cards);
        }

        [Test] public void ConstructorInitializesTheFrontendFromTheSessionAndRejectsMissingDependencies()
        {
            var session = Create(); var presentation = new ImmediatePresentation();
            var playback = new BattleSessionPlayback(session, presentation);
            Assert.AreSame(session, playback.Session); Assert.AreSame(presentation, playback.Presentation);
            Assert.AreSame(session.State, presentation.State); Assert.AreEqual(1, presentation.Initializations);
            Assert.IsEmpty(presentation.Batches);
            Assert.Throws<ArgumentNullException>(() => new BattleSessionPlayback(null, presentation));
            Assert.Throws<ArgumentNullException>(() => new BattleSessionPlayback(session, null));
            Assert.IsFalse(typeof(IBattleSessionPresentation).Assembly.GetReferencedAssemblies()
                .Any(reference => reference.Name.StartsWith("UnityEngine") || reference.Name.StartsWith("UnityEditor")));
        }

        [Test] public void ImmediateAndManualFrontendsProduceTheSameRulesWithIndependentDisplayTiming()
        {
            var immediate = new ImmediatePresentation(); var manual = new ManualPresentation();
            var first = new BattleSessionPlayback(Create(), immediate); var second = new BattleSessionPlayback(Create(), manual);
            for (int command = 0; command < 19; command++)
            {
                var action = command == 0 ? CardDuelAction.Start : command % 3 == 0 ? CardDuelAction.EndTurn :
                    command % 2 == 0 ? CardDuelAction.Discard : CardDuelAction.Play;
                var cards = action == CardDuelAction.Play || action == CardDuelAction.Discard
                    ? first.Session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray() : null;
                var a = first.Execute(first.Session.State.Revision, Player, action, cards);
                var b = second.Execute(second.Session.State.Revision, Player, action, cards);
                Assert.IsTrue(a.Accepted); Assert.IsTrue(b.Accepted);
                SameState(a.State, b.State);
                CollectionAssert.AreEqual(a.Steps.Select(step => step.Action), b.Steps.Select(step => step.Action));
                Assert.AreSame(a.State, immediate.State); Assert.AreSame(b.Before, manual.State);
                Assert.IsTrue(manual.IsPresenting); var authority = second.Session.State;
                foreach (var step in b.Steps)
                {
                    Assert.IsTrue(manual.Advance()); Assert.AreSame(step.Transition.State, manual.State);
                    Assert.AreSame(authority, second.Session.State);
                }
                Assert.IsFalse(manual.Advance()); Assert.IsFalse(manual.IsPresenting);
                Assert.AreSame(authority, manual.State);
            }
        }

        [Test] public void PlaybackBusyGateDoesNotBecomeARuleSessionConstraint()
        {
            var session = Create(); var presentation = new ManualPresentation();
            var playback = new BattleSessionPlayback(session, presentation);
            Assert.IsTrue(playback.Execute(session.State.Revision, Player, CardDuelAction.Start).Accepted);
            var before = session.State;
            var rejected = playback.Execute(before.Revision, Player, CardDuelAction.EndTurn);
            Assert.AreEqual(CardDuelError.InvalidPhase, rejected.Error); Assert.IsEmpty(rejected.Steps);
            Assert.AreSame(before, rejected.Before); Assert.AreSame(before, rejected.State); Assert.AreSame(before, session.State);
            Assert.AreEqual(1, presentation.Batches.Count);
            var direct = session.Execute(before.Revision, Player, CardDuelAction.EndTurn);
            Assert.IsTrue(direct.Accepted); Assert.AreEqual(1, direct.State.Duel.CompletedRounds);
            Assert.IsTrue(presentation.IsPresenting);
            playback.Synchronize();
            Assert.AreSame(direct.State, session.State); Assert.AreSame(direct.State, presentation.State);
            Assert.IsFalse(presentation.IsPresenting);
        }

        [Test] public void SynchronizingPartiallyPlayedBatchCancelsDisplayWithoutAdvancingRules()
        {
            var session = Create(energy: 1); var presentation = new ManualPresentation();
            var playback = new BattleSessionPlayback(session, presentation);
            playback.Execute(session.State.Revision, Player, CardDuelAction.Start, animate: false);
            var batch = playback.Execute(session.State.Revision, Player, CardDuelAction.Play,
                session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1));
            Assert.IsTrue(batch.Accepted); Assert.AreEqual(1, session.State.Duel.CompletedRounds);
            Assert.IsTrue(presentation.Advance()); Assert.IsTrue(presentation.IsPresenting);
            Assert.AreEqual(0, presentation.State.Duel.CompletedRounds);
            var authority = session.State;
            playback.Synchronize(); playback.Synchronize();
            Assert.AreSame(authority, session.State); Assert.AreSame(authority, presentation.State);
            Assert.IsFalse(presentation.IsPresenting); Assert.IsFalse(presentation.Advance());
            Assert.AreEqual(2, presentation.Batches.Count);
        }

        [Test] public void RejectedRuleCommandsDoNotReachThePresentation()
        {
            var session = Create(); var presentation = new ImmediatePresentation();
            var playback = new BattleSessionPlayback(session, presentation);
            playback.Execute(session.State.Revision, Player, CardDuelAction.Start);
            var before = session.State;
            var stale = playback.Execute(before.Revision - 1, Player, CardDuelAction.EndTurn);
            var empty = playback.Execute(before.Revision, Player, CardDuelAction.Play);
            Assert.AreEqual(CardDuelError.StaleRevision, stale.Error); Assert.AreEqual(CardDuelError.InvalidSelection, empty.Error);
            Assert.AreSame(before, session.State); Assert.AreSame(before, presentation.State);
            Assert.AreEqual(1, presentation.Batches.Count); Assert.AreEqual(0, presentation.Synchronizations);
        }

        [Test] public void PresentationFailureKeepsTheCommittedRulesAndSynchronizesBeforeRethrowing()
        {
            var session = Create(); var presentation = new ImmediatePresentation();
            var playback = new BattleSessionPlayback(session, presentation);
            playback.Execute(session.State.Revision, Player, CardDuelAction.Start);
            var before = session.State; var failure = new InvalidOperationException("Presentation failed"); presentation.Failure = failure;
            var thrown = Assert.Throws<InvalidOperationException>(() => playback.Execute(before.Revision, Player, CardDuelAction.EndTurn));
            Assert.AreSame(failure, thrown); Assert.AreNotSame(before, session.State);
            Assert.AreEqual(1, session.State.Duel.CompletedRounds); Assert.AreEqual(993, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreSame(session.State, presentation.State); Assert.AreEqual(1, presentation.Synchronizations);
            var authority = session.State; playback.Synchronize(); Assert.AreSame(authority, session.State);
        }

        [TestCase(false)] [TestCase(true)]
        public void AnimateIsForwardedToTheFrontendWithoutChangingRuleResults(bool animate)
        {
            var session = Create(); var presentation = new ManualPresentation();
            var playback = new BattleSessionPlayback(session, presentation);
            var result = playback.Execute(session.State.Revision, Player, CardDuelAction.Start, animate: animate);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(animate, presentation.LastAnimate);
            Assert.AreEqual(animate, presentation.IsPresenting);
            Assert.AreEqual(8, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Count);
            var authority = session.State; playback.Synchronize();
            Assert.AreSame(authority, session.State); Assert.AreSame(authority, presentation.State);
        }

        [Test] public void StartAndSubmitUseTheFirstPlayerAndCurrentRevisionWithoutChangingTheCommandResult()
        {
            var session = Create(); var expected = Create(); var presentation = new ImmediatePresentation();
            var playback = new BattleSessionPlayback(session, presentation);
            var opening = playback.Start(false);
            Assert.IsTrue(opening.Accepted); Assert.AreEqual(Player, opening.Steps[0].Actor);
            Assert.AreEqual(CardDuelAction.Start, opening.Steps[0].Action); Assert.IsFalse(presentation.LastAnimate);
            expected.Execute(expected.State.Revision, Player, CardDuelAction.Start);
            SameState(expected.State, session.State);

            var cards = session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            var actual = playback.Submit(Player, CardDuelAction.Play, cards, new[] { Enemy }, animate: false);
            var baseline = expected.Execute(expected.State.Revision, Player, CardDuelAction.Play, cards, new[] { Enemy });
            Assert.IsTrue(actual.Accepted); SameState(baseline.State, actual.State);
            Assert.AreSame(session.State, presentation.State); Assert.IsFalse(presentation.LastAnimate);
            var before = session.State; int batches = presentation.Batches.Count;
            Assert.AreEqual("Opening turn rejected: InvalidPhase", Assert.Throws<InvalidOperationException>(() => playback.Start()).Message);
            Assert.AreEqual(CardDuelError.InvalidPhase, playback.Submit(Enemy, CardDuelAction.EnemyAction).Error);
            Assert.AreSame(before, session.State); Assert.AreEqual(batches, presentation.Batches.Count);
        }

        [Test] public void SharedQueriesAndSubmitRespectOpeningAndPresentationBusyGates()
        {
            var session = Create(); var presentation = new ManualPresentation();
            var playback = new BattleSessionPlayback(session, presentation);
            Assert.IsFalse(playback.CanAct(Player)); Assert.IsNull(playback.PreviewPlay(Player, null));
            Assert.IsNull(playback.PreviewDiscard(Player, null));
            playback.Start();
            var cards = session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            var before = session.State;
            Assert.IsFalse(playback.CanAct(Player)); Assert.IsFalse(playback.CanPlay(Player, cards));
            Assert.IsFalse(playback.CanDiscard(Player, cards)); Assert.IsNull(playback.PreviewPlay(Player, cards));
            Assert.IsNull(playback.PreviewDiscard(Player, cards));
            Assert.AreEqual(CardDuelError.InvalidPhase, playback.Submit(Player, CardDuelAction.EndTurn).Error);
            Assert.AreSame(before, session.State); Assert.AreEqual(1, presentation.Batches.Count);
            playback.Synchronize();
            Assert.IsTrue(playback.CanAct(Player)); Assert.IsTrue(playback.CanPlay(Player, cards)); Assert.IsTrue(playback.CanDiscard(Player, cards));
            foreach (var actor in new[] { default(ParticipantId), Enemy, new ParticipantId("unknown") })
            {
                Assert.IsFalse(playback.CanAct(actor)); Assert.IsFalse(playback.CanPlay(actor, cards));
                Assert.IsFalse(playback.CanDiscard(actor, cards)); Assert.IsNull(playback.PreviewPlay(actor, cards));
                Assert.IsNull(playback.PreviewDiscard(actor, cards));
            }
            Assert.AreSame(before, session.State);
        }

        [TestCase(false)] [TestCase(true)]
        public void LightweightQueriesRespectIndependentCostsLimitsAndFiniteOrUnlimitedDiscardBudgets(bool unlimited)
        {
            var rules = new BattlePlayerRules(maxEnergy: 3, initialEnergy: 7, playEnergyCost: 3, discardEnergyCost: 2,
                discards: 1, unlimitedDiscards: unlimited, maxPlayCards: 2, maxDiscardCards: 4);
            var session = CreateConfigured(rules);
            var playback = new BattleSessionPlayback(session, new ImmediatePresentation()); playback.Start(false);
            var hand = session.State.Zone(CardZoneKinds.Hand, Player).Cards;
            Assert.AreEqual(7, session.State.Duel.Player(Player).Energy.Energy);
            Assert.IsTrue(playback.CanPlay(Player, hand.Take(2))); Assert.IsFalse(playback.CanPlay(Player, hand.Take(3)));
            Assert.IsTrue(playback.CanDiscard(Player, hand.Take(4))); Assert.IsFalse(playback.CanDiscard(Player, hand.Take(5)));
            Assert.IsTrue(playback.Submit(Player, CardDuelAction.Discard, hand.Take(1), animate: false).Accepted);
            hand = session.State.Zone(CardZoneKinds.Hand, Player).Cards;
            Assert.AreEqual(5, session.State.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(unlimited, playback.CanDiscard(Player, hand.Take(1)));
            Assert.IsTrue(playback.CanPlay(Player, hand.Take(1)));
            Assert.IsTrue(playback.Submit(Player, CardDuelAction.Play, hand.Take(1), animate: false).Accepted);
            hand = session.State.Zone(CardZoneKinds.Hand, Player).Cards;
            Assert.AreEqual(2, session.State.Duel.Player(Player).Energy.Energy);
            Assert.IsFalse(playback.CanPlay(Player, hand.Take(1)));
            Assert.AreEqual(unlimited, playback.CanDiscard(Player, hand.Take(1)));
            Assert.IsTrue(playback.CanAct(Player));
            Assert.IsTrue(playback.Submit(Player, CardDuelAction.EndTurn, animate: false).Accepted);
        }

        [TestCase(false)] [TestCase(true)]
        public void ZeroDiscardCounterAndInsufficientEnergyDoNotPreventEndingTheTurn(bool unlimited)
        {
            var session = CreateConfigured(new BattlePlayerRules(initialEnergy: 1, playEnergyCost: 2, discardEnergyCost: 1,
                discards: 0, unlimitedDiscards: unlimited));
            var playback = new BattleSessionPlayback(session, new ImmediatePresentation()); playback.Start(false);
            var cards = session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1);
            Assert.IsFalse(playback.CanPlay(Player, cards)); Assert.AreEqual(unlimited, playback.CanDiscard(Player, cards));
            Assert.IsTrue(playback.CanAct(Player)); Assert.IsTrue(playback.Submit(Player, CardDuelAction.EndTurn, animate: false).Accepted);
        }

        [Test] public void QueriesRejectEmptyDuplicateInvalidForeignAndDisabledCardsWithoutChangingState()
        {
            var session = CreateConfigured(new BattlePlayerRules(), playerCount: 2);
            var presentation = new ImmediatePresentation(); var playback = new BattleSessionPlayback(session, presentation); playback.Start(false);
            var before = session.State; var first = before.Zone(CardZoneKinds.Hand, Player).Cards[0];
            var other = new ParticipantId("player/1");
            var invalid = new IEnumerable<CardInstanceId>[] {
                null, Array.Empty<CardInstanceId>(), new[] { first, first }, new[] { default(CardInstanceId) },
                new[] { new CardInstanceId("foreign") }, new[] { before.Zone(CardZoneKinds.Deck, Player).Cards[0] },
                new[] { before.Zone(CardZoneKinds.Hand, other).Cards[0] }
            };
            foreach (var cards in invalid)
            { Assert.IsFalse(playback.CanPlay(Player, cards)); Assert.IsFalse(playback.CanDiscard(Player, cards)); }
            Assert.IsTrue(playback.CanPlay(Player, new[] { first }));
            Assert.AreSame(before, session.State); Assert.AreSame(before, presentation.State); Assert.AreEqual(1, presentation.Batches.Count);

            var disabled = CreateConfigured(new BattlePlayerRules(), disabled: true);
            var disabledPlayback = new BattleSessionPlayback(disabled, new ImmediatePresentation()); disabledPlayback.Start(false);
            var disabledCards = disabled.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1);
            Assert.IsTrue(disabledPlayback.CanAct(Player));
            Assert.IsFalse(disabledPlayback.CanPlay(Player, disabledCards)); Assert.IsFalse(disabledPlayback.CanDiscard(Player, disabledCards));
        }

        [Test] public void QueriesTrackEachPlayerAndRejectUncollectedOrTerminalStates()
        {
            var session = CreateConfigured(new BattlePlayerRules(), playerCount: 2);
            var playback = new BattleSessionPlayback(session, new ImmediatePresentation()); playback.Start(false);
            var other = new ParticipantId("player/1"); var before = session.State;
            Assert.IsTrue(playback.CanAct(Player)); Assert.IsTrue(playback.CanAct(other));
            var checkpoint = session.Engine.PreviewPlay(before, before.Revision, Player, before.Zone(CardZoneKinds.Hand, Player).Cards.Take(1));
            Assert.IsTrue(checkpoint.Accepted); Assert.IsNotEmpty(checkpoint.State.Zone(CardZoneKinds.Played, Player).Cards);
            var checkpointPlayback = new BattleSessionPlayback(new BattleSession(session.Engine, checkpoint.State), new ImmediatePresentation());
            Assert.IsFalse(checkpointPlayback.CanAct(Player)); Assert.IsNull(checkpointPlayback.PreviewPlay(Player, null));
            Assert.IsTrue(checkpointPlayback.CanAct(other)); Assert.AreSame(before, session.State);

            Assert.IsTrue(playback.Submit(Player, CardDuelAction.EndTurn, animate: false).Accepted);
            Assert.IsFalse(playback.CanAct(Player)); Assert.IsTrue(playback.CanAct(other));
            Assert.IsTrue(playback.CanPlay(other, session.State.Zone(CardZoneKinds.Hand, other).Cards.Take(1)));
            Assert.IsTrue(playback.Submit(other, CardDuelAction.EndTurn, animate: false).Accepted);
            Assert.AreEqual(1, session.State.Duel.CompletedRounds); Assert.IsTrue(playback.CanAct(Player)); Assert.IsTrue(playback.CanAct(other));

            var terminal = CreateConfigured(new BattlePlayerRules(), enemyHealth: 1);
            var terminalPlayback = new BattleSessionPlayback(terminal, new ImmediatePresentation()); terminalPlayback.Start(false);
            Assert.IsTrue(terminalPlayback.Submit(Player, CardDuelAction.Play, terminal.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1), animate: false).Accepted);
            Assert.IsTrue(terminal.State.Duel.IsFinished); Assert.IsFalse(terminalPlayback.CanAct(Player));
            Assert.IsNull(terminalPlayback.PreviewPlay(Player, null)); Assert.IsNull(terminalPlayback.PreviewDiscard(Player, null));
        }

        [TestCase(false)] [TestCase(true)]
        public void SharedPreviewsAreReadOnlyAndMatchTheSubmittedPrimaryStep(bool discard)
        {
            var session = Create(); var presentation = new ImmediatePresentation();
            var playback = new BattleSessionPlayback(session, presentation); playback.Start(false);
            var before = session.State; var random = before.Random.StateBytes.ToArray();
            var cards = before.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            var targets = new[] { discard ? Player : Enemy };
            var preview = discard ? playback.PreviewDiscard(Player, cards, targets) : playback.PreviewPlay(Player, cards, targets);
            Assert.IsTrue(preview.Accepted); Assert.AreSame(before, session.State); Assert.AreSame(before, presentation.State);
            CollectionAssert.AreEqual(random, session.State.Random.StateBytes);
            Assert.AreEqual(1, presentation.Batches.Count); Assert.AreEqual(0, presentation.Synchronizations);
            var invalid = discard ? playback.PreviewDiscard(Player, null) : playback.PreviewPlay(Player, null);
            Assert.AreEqual(CardDuelError.InvalidSelection, invalid.Error); Assert.AreSame(before, session.State);
            var actual = playback.Submit(Player, discard ? CardDuelAction.Discard : CardDuelAction.Play, cards, targets, false);
            Assert.IsTrue(actual.Accepted); SameState(preview.State, actual.Steps[0].Transition.State);
            CollectionAssert.AreEqual(preview.Events.Select(effect => effect.HealthLost), actual.Steps[0].Transition.Events.Select(effect => effect.HealthLost));
            Assert.AreEqual(2, presentation.Batches.Count);
        }

        private sealed class RejectingDamageRule : IScoredHandDamageRule
        {
            public string Version => "reject.damage";
            public DamagePlan Resolve(ScoredHandDamageContext context) => throw new ArgumentException("Invalid custom damage.");
        }
        private sealed class RejectingRewardRule : IDiscardRewardRule
        {
            public string Version => "reject.reward";
            public DiscardRewardPlan Resolve(DiscardRewardContext context) => throw new ArgumentException("Invalid custom reward.");
        }

        [Test] public void AvailabilityDoesNotEvaluateCustomRulesWhilePreviewAndSubmissionStillValidateThem()
        {
            var session = CreateConfigured(new BattlePlayerRules(damagePolicy: new RejectingDamageRule(), discardReward: new RejectingRewardRule()));
            var presentation = new ImmediatePresentation(); var playback = new BattleSessionPlayback(session, presentation); playback.Start(false);
            var before = session.State; var cards = before.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            Assert.IsTrue(playback.CanPlay(Player, cards)); Assert.IsTrue(playback.CanDiscard(Player, cards));
            Assert.AreEqual(CardDuelError.InvalidDamagePlan, playback.PreviewPlay(Player, cards).Error);
            Assert.AreEqual(CardDuelError.InvalidDiscardReward, playback.PreviewDiscard(Player, cards).Error);
            Assert.AreEqual(CardDuelError.InvalidDamagePlan, playback.Submit(Player, CardDuelAction.Play, cards).Error);
            Assert.AreEqual(CardDuelError.InvalidDiscardReward, playback.Submit(Player, CardDuelAction.Discard, cards).Error);
            Assert.AreSame(before, session.State); Assert.AreSame(before, presentation.State); Assert.AreEqual(1, presentation.Batches.Count);
        }
    }
}
