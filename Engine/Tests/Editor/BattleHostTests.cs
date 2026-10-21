using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleHostTests
    {
        private static BattleSession Create(int health = 100)
        {
            var engine = new CardDuelEngine(new BattleRules("host.test", 20));
            var deck = StandardDeck.Cards().Take(10).Select((card, i) => new DeckEntry("card/" + i, card.Id));
            return new BattleSession(engine, engine.Create("host", HmacDeckShuffler.FromSeed(new byte[32]),
                new[] { new BattlePlayerSetup(new ParticipantId("player"), new CombatantDefinition("hero", health), deck) },
                new[] { new BattleEnemySetup(new ParticipantId("enemy"), new CombatantDefinition("enemy", 100000), 7) }));
        }

        private sealed class Presentation : IBattleSessionPresentation
        {
            public bool IsPresenting { get; private set; }
            public CardDuelState State;
            public int Initializations, Synchronizations;
            public readonly List<BattleSessionResult> Batches = new List<BattleSessionResult>();
            public Action Initializing, Synchronizing;
            public bool LastAnimate;
            public Exception Failure;
            public void Initialize(CardDuelState initialState)
            {
                State = initialState; IsPresenting = false; Initializations++; Initializing?.Invoke();
            }
            public void Present(BattleSessionResult batch, bool animate)
            {
                Batches.Add(batch); LastAnimate = animate; IsPresenting = animate;
                if (Failure != null) throw Failure;
                State = animate ? batch.Before : batch.State;
            }
            public void Synchronize(CardDuelState state)
            {
                Synchronizing?.Invoke(); State = state; IsPresenting = false; Synchronizations++;
            }
        }

        private sealed class OuterCommands : IBattleSessionCommands
        {
            private readonly BattleSession authority;
            public BattleSession Session => new BattleSession(authority.Engine, authority.State);
            public long Revision { get; private set; } = 100;
            public int Executions, ItemCalls, AdminCalls;
            public long LastExpectedRevision;
            public OuterCommands(BattleSession session) { authority = session; }
            public BattleSessionResult Execute(long expectedRevision, ParticipantId actor, CardDuelAction action,
                IEnumerable<CardInstanceId> selected = null, IEnumerable<ParticipantId> targets = null,
                BattleTargetBindings targetBindings = null, int drawCount = 0)
            {
                LastExpectedRevision = expectedRevision; Executions++;
                if (expectedRevision != Revision) return BattleSessionResult.Rejected(authority.State, CardDuelError.StaleRevision);
                var result = authority.Execute(authority.State.Revision, actor, action, selected, targets, targetBindings, drawCount);
                if (result.Accepted) Revision++;
                return result;
            }
            public BattleSessionResult UseItem(long expectedRevision, ParticipantId actor, string instanceId, string targetInstanceId = null)
            { ItemCalls++; LastExpectedRevision = expectedRevision; return BattleSessionResult.Rejected(authority.State, CardDuelError.InvalidPhase); }
            public BattleSessionResult ApplyAdmin(long expectedRevision, BattleAdminPatch patch)
            { AdminCalls++; LastExpectedRevision = expectedRevision; return BattleSessionResult.Rejected(authority.State, CardDuelError.InvalidPhase); }
        }

        [Test]
        public void OuterAuthorityOpeningIsPresentedOnceAndSubsequentCommandsUseItsOwnRevision()
        {
            var session = Create(); var player = session.State.Duel.PlayerId;
            var opening = session.Execute(session.State.Revision, player, CardDuelAction.Start);
            var commands = new OuterCommands(session); var view = new Presentation();
            var host = new BattleHost(() => throw new InvalidOperationException("Standalone factory must not run"));
            host.AttachCommands(commands, view, opening, animate: false);
            Assert.AreEqual(0, commands.Executions); Assert.AreEqual(1, view.Batches.Count);
            Assert.AreSame(opening, view.Batches[0]); Assert.AreSame(session.State, view.State);
            Assert.AreNotSame(commands.Session, commands.Session, "The port deliberately exposes isolated wrappers.");
            var before = session.State;
            var cards = before.Zone(CardZoneKinds.Hand, player).Cards.Take(1).ToArray();
            var preview = host.Playback.PreviewPlay(player, cards);
            Assert.IsTrue(preview.Accepted); Assert.AreSame(before, session.State); Assert.AreEqual(0, commands.Executions);
            var result = host.Playback.Submit(player, CardDuelAction.Play, cards, animate: false);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(100, commands.LastExpectedRevision);
            Assert.AreEqual(101, host.Playback.Revision); Assert.AreSame(session.State, host.AuthorityState);
            Assert.AreSame(session.State, view.State); Assert.AreNotSame(before, host.AuthorityState);
            host.Playback.UseItem(host.Playback.Revision, player, "missing");
            Assert.AreEqual(1, commands.ItemCalls); Assert.AreEqual(101, commands.LastExpectedRevision);
            host.Playback.ApplyAdmin(host.Playback.Revision, null);
            Assert.AreEqual(1, commands.AdminCalls); Assert.AreEqual(101, commands.LastExpectedRevision);
        }

        [Test]
        public void OuterAuthorityCanRestoreStartedBattleWithoutReplayingOpening()
        {
            var session = Create(); session.Execute(session.State.Revision, session.State.Duel.PlayerId, CardDuelAction.Start);
            var commands = new OuterCommands(session); var view = new Presentation();
            var host = new BattleHost(() => throw new InvalidOperationException("Standalone factory must not run"));
            host.AttachCommands(commands, view);
            Assert.AreEqual(0, commands.Executions); Assert.IsEmpty(view.Batches); Assert.AreSame(session.State, view.State);
        }

        [TestCase(true)] [TestCase(false)]
        public void StartInstallsSessionBeforeInitializeAndCommandPortBeforeOpening(bool animate)
        {
            var session = Create(); int factories = 0; var order = new List<string>();
            var host = new BattleHost(() => { factories++; order.Add("create"); return session; });
            var view = new Presentation();
            view.Initializing = () =>
            {
                order.Add("initialize"); Assert.AreSame(session, host.Session);
                Assert.IsNull(host.Playback); Assert.IsFalse(host.AuthorityState.HasStarted);
            };
            host.SessionAttached += () =>
            {
                order.Add("attached"); Assert.AreSame(session, host.Playback.Session);
                Assert.IsFalse(session.State.HasStarted); Assert.IsEmpty(view.Batches);
            };
            var result = host.StartBattle(view, animate);
            Assert.IsTrue(result.Accepted); Assert.IsTrue(session.State.HasStarted);
            Assert.AreEqual(1, factories); Assert.AreEqual(1, view.Batches.Count);
            Assert.AreEqual(animate, view.LastAnimate);
            CollectionAssert.AreEqual(new[] { "create", "initialize", "attached" }, order);
        }

        [TestCase(true)] [TestCase(false)]
        public void AttachPreservesExistingStateWithoutCreatingOrStarting(bool started)
        {
            var session = Create(); var view = new Presentation();
            if (started) new BattleSessionPlayback(session, new Presentation()).Start(false);
            var state = session.State;
            var host = new BattleHost(() => throw new Exception("Factory must not run"));
            host.AttachSession(session, view);
            Assert.AreSame(state, host.AuthorityState); Assert.AreSame(state, view.State);
            Assert.IsEmpty(view.Batches); Assert.AreEqual(1, view.Initializations);
        }

        [Test]
        public void RestartReadsCurrentConfigurationAndResetsTheSamePresentation()
        {
            int health = 100, calls = 0;
            var host = new BattleHost(() => { calls++; return Create(health); });
            var view = new Presentation(); host.StartBattle(view);
            var previous = host.Session; var previousState = previous.State;
            health = 200; host.RestartBattle(view, false);
            Assert.AreNotSame(previous, host.Session); Assert.AreSame(previousState, previous.State);
            Assert.AreEqual(200, host.AuthorityState.Duel.Combat.Get(new ParticipantId("player")).Health);
            Assert.AreEqual(2, calls); Assert.AreEqual(2, view.Initializations);
            Assert.AreEqual(0, view.Synchronizations); Assert.IsFalse(view.IsPresenting);
        }

        [Test]
        public void SwitchingFrontendSynchronizesOldPresentationBeforeInstallingNewSession()
        {
            var host = new BattleHost(() => Create()); var oldView = new Presentation();
            host.StartBattle(oldView); var oldSession = host.Session;
            oldView.Synchronizing = () => Assert.AreSame(oldSession, host.Session);
            var next = Create(); var nextView = new Presentation();
            host.AttachSession(next, nextView);
            Assert.IsFalse(oldView.IsPresenting); Assert.AreSame(oldSession.State, oldView.State);
            Assert.AreEqual(1, oldView.Synchronizations); Assert.AreSame(next.State, nextView.State);
            Assert.IsEmpty(nextView.Batches);
        }

        [Test]
        public void SynchronizeCancelsPendingDisplayWithoutAdvancingRules()
        {
            var host = new BattleHost(() => Create()); var view = new Presentation();
            host.Synchronize(); host.StartBattle(view); var committed = host.AuthorityState;
            Assert.IsTrue(view.IsPresenting); Assert.AreNotSame(committed, view.State);
            host.Synchronize(); host.Synchronize();
            Assert.AreSame(committed, host.AuthorityState); Assert.AreSame(committed, view.State);
            Assert.IsFalse(view.IsPresenting); Assert.AreEqual(1, view.Batches.Count);
        }

        [TestCase("null")] [TestCase("throw")] [TestCase("started")]
        public void InvalidFactoryResultLeavesPreviousSessionAndPresentationIntact(string failure)
        {
            bool fail = false;
            var host = new BattleHost(() =>
            {
                if (!fail) return Create();
                if (failure == "null") return null;
                if (failure == "throw") throw new InvalidOperationException("factory");
                var started = Create(); new BattleSessionPlayback(started, new Presentation()).Start(false); return started;
            });
            var view = new Presentation(); host.StartBattle(view, false);
            var session = host.Session; var playback = host.Playback; fail = true;
            Assert.Throws<InvalidOperationException>(() => host.RestartBattle(view));
            Assert.AreSame(session, host.Session); Assert.AreSame(playback, host.Playback);
            Assert.AreEqual(1, view.Initializations);
            fail = false; Assert.DoesNotThrow(() => host.RestartBattle(view, false));
        }

        [Test]
        public void InitializationFailureDisablesCommandsUntilExplicitReattachment()
        {
            var host = new BattleHost(() => Create()); var view = new Presentation();
            host.StartBattle(view, false); var next = Create(); int attached = 0;
            host.SessionAttached += () => attached++;
            view.Initializing = () => throw new InvalidOperationException("initialize");
            Assert.Throws<InvalidOperationException>(() => host.AttachSession(next, view));
            Assert.AreSame(next, host.Session); Assert.IsNull(host.Playback); Assert.AreEqual(0, attached);
            Assert.IsFalse(next.State.HasStarted);
            Assert.Throws<InvalidOperationException>(() => host.Synchronize());
            view.Initializing = null; host.AttachSession(next, view);
            Assert.AreSame(next, host.Playback.Session); Assert.AreEqual(1, attached);
            Assert.IsFalse(next.State.HasStarted);
        }

        [Test]
        public void LifecycleCallbacksCannotReenterAndTheGuardReleasesAfterFailure()
        {
            var host = new BattleHost(() => Create()); var view = new Presentation();
            view.Initializing = () => host.RestartBattle(view);
            Assert.Throws<InvalidOperationException>(() => host.StartBattle(view));
            Assert.IsFalse(host.Session.State.HasStarted); Assert.IsNull(host.Playback);
            view.Initializing = null;
            host.SessionAttached += () =>
            {
                Assert.Throws<InvalidOperationException>(() => host.AttachSession(Create(), view));
                Assert.Throws<InvalidOperationException>(() => host.Synchronize());
            };
            Assert.DoesNotThrow(() => host.StartBattle(view, false));
            host.Synchronize(); Assert.IsTrue(host.Session.State.HasStarted);
        }

        [Test]
        public void FailedOpeningPresentationRetainsCommittedRulesAndSynchronizes()
        {
            var host = new BattleHost(() => Create());
            var view = new Presentation { Failure = new InvalidOperationException("present") };
            Assert.Throws<InvalidOperationException>(() => host.StartBattle(view));
            Assert.IsTrue(host.Session.State.HasStarted); Assert.AreSame(host.AuthorityState, view.State);
            Assert.AreEqual(1, view.Synchronizations); Assert.IsFalse(view.IsPresenting);
            view.Failure = null; Assert.DoesNotThrow(() => host.RestartBattle(view, false));
        }

        [Test]
        public void MissingArgumentsAreRejectedBeforeMutatingTheCurrentSession()
        {
            Assert.Throws<ArgumentNullException>(() => new BattleHost(null));
            var host = new BattleHost(() => Create()); var view = new Presentation(); host.StartBattle(view, false);
            var state = host.AuthorityState;
            Assert.Throws<ArgumentNullException>(() => host.StartBattle(null));
            Assert.Throws<ArgumentNullException>(() => host.AttachSession(null, view));
            Assert.Throws<ArgumentNullException>(() => host.AttachSession(Create(), null));
            Assert.AreSame(state, host.AuthorityState); Assert.AreEqual(1, view.Initializations);
        }
    }
}
