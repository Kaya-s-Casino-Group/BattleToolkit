using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CombatBoundaryRuntimeTests
    {
        private static readonly ParticipantId P = new ParticipantId("p"), E = new ParticipantId("e");
        private sealed class FactOnly : ICombatReactionRuntime
        {
            public CombatReactionEvaluation React(CombatReactionContext context) => throw new InvalidOperationException("Clock reached fact-only runtime.");
        }
        private sealed class Runtime : ICombatBoundaryReactionRuntime
        {
            private readonly Func<CombatBoundaryReactionContext, CombatReactionEvaluation> boundary;
            private readonly Func<CombatReactionContext, CombatReactionEvaluation> fact;
            internal Runtime(Func<CombatBoundaryReactionContext, CombatReactionEvaluation> boundary,
                Func<CombatReactionContext, CombatReactionEvaluation> fact = null) { this.boundary = boundary; this.fact = fact; }
            public CombatReactionEvaluation ReactBoundary(CombatBoundaryReactionContext context) => boundary(context);
            public CombatReactionEvaluation React(CombatReactionContext context) => fact == null ? new CombatReactionEvaluation(this) : fact(context);
        }
        private sealed class Provider : ICombatReactionProvider
        {
            public IEnumerable<CombatReaction> React(CombatReactionContext context)
            {
                Assert.IsNotNull(context.Event.Effect);
                return new[] { new CombatReaction(new CombatEffect(CombatEffectKind.GainBlock, P, P, 2), "provider", P) };
            }
        }
        private static CombatState State(IEnumerable<StatusInstance> statuses = null) => new CombatState("boundary", 0,
            new[] { new CombatantState(P, "p", "players", 20, 20), new CombatantState(E, "e", "enemies", 20, 20) }, statuses);
        private static CombatReactionBatch Batch(string id = "batch", bool chain = false, ParticipantId? target = null) =>
            new CombatReactionBatch(id, new[] { new CombatReaction(new CombatEffect(CombatEffectKind.Damage, P, target ?? E, 3),
                "boundary", P, canTriggerReactions: chain, sourceInstanceId: "gear") });

        [Test] public void OptionalBoundaryHookLeavesFactOnlyRuntimesAndActionClocksUntouched()
        {
            var state = State(); var old = new FactOnly();
            var result = CombatEngine.ResolveTiming(state, 0, CombatTimingSignal.BattleStarted("start", new[] { P, E }), reactionRuntime: old);
            Assert.IsTrue(result.Accepted); Assert.AreSame(old, result.ReactionRuntime); Assert.AreEqual(1, result.RuleEvents.Count);
            var next = new Runtime(context => { Assert.Fail("Action clock entered boundary hook."); return null; });
            result = CombatEngine.ResolveTiming(state, 0, CombatTimingSignal.ActionCompleted("action", P, "play"), reactionRuntime: next);
            Assert.IsTrue(result.Accepted); Assert.AreSame(next, result.ReactionRuntime);
        }

        [Test] public void BoundaryHookReadsPostPeriodicStateAndRemainingStatusCharges()
        {
            var id = new StatusDefinitionId("poison");
            var catalog = new CombatStatusCatalog("test", new[] { new CombatStatusDefinition(id, StatusTags.None,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.PhaseEnding, 5) }, StatusLifetimeSpec.ForTriggers(1)) });
            var status = new StatusInstance(new StatusInstanceId("poison"), id, EntityRef.Participant(P), EntityRef.Participant(E), 1, StatusDuration.ForTriggers(1));
            var committed = new FactOnly();
            var runtime = new Runtime(context => {
                Assert.AreEqual(15, context.State.Get(E).Health); Assert.IsEmpty(context.State.Statuses); Assert.IsNull(context.Event.Effect);
                return new CombatReactionEvaluation(committed, new[] { Batch() });
            });
            var state = State(new[] { status });
            var result = CombatEngine.ResolveTiming(state, 0, CombatTimingSignal.PhaseEnding("end", new[] { E }, "enemy"),
                statusCatalog: catalog, reactionRuntime: runtime);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(12, result.State.Get(E).Health); Assert.AreSame(committed, result.ReactionRuntime);
            var child = result.Events.Last(); Assert.AreEqual(1, child.Provenance.Depth); Assert.AreEqual(-1, child.Provenance.RootEffectIndex);
            Assert.AreEqual(0, child.Provenance.ParentSequenceIndex); Assert.AreEqual("batch", child.Provenance.ActivationId);
        }

        [Test] public void BoundaryChildrenUseTheExistingProviderAndFactQueueOnlyWhenEnabled()
        {
            var old = new FactOnly();
            var runtime = new Runtime(context => new CombatReactionEvaluation(new Runtime(_ => null), new[] { Batch(chain: true) }));
            var result = CombatEngine.ResolveTiming(State(), 0, CombatTimingSignal.PhaseStarted("start", new[] { P }, "player"),
                reactionRuntime: runtime, reactionProvider: new Provider());
            Assert.IsTrue(result.Accepted); Assert.AreEqual(2, result.State.Get(P).Block); Assert.AreEqual(2, result.Events.Count);
            runtime = new Runtime(context => new CombatReactionEvaluation(old, new[] { Batch() }));
            result = CombatEngine.ResolveTiming(State(), 0, CombatTimingSignal.BattleStarted("start", new[] { P }),
                reactionRuntime: runtime, reactionProvider: new Provider());
            Assert.IsTrue(result.Accepted); Assert.AreEqual(0, result.State.Get(P).Block); Assert.AreEqual(1, result.Events.Count);
        }

        [TestCase("null", CombatError.InvalidReaction)] [TestCase("unknown", CombatError.InvalidReaction)]
        [TestCase("limit", CombatError.ReactionLimitExceeded)] [TestCase("duplicate", CombatError.InvalidReaction)]
        public void InvalidBoundaryRuntimeOutputNeverCommitsEventsOrNextRuntime(string kind, CombatError error)
        {
            Runtime runtime = null;
            runtime = new Runtime(context => kind == "null" ? null : new CombatReactionEvaluation(runtime,
                kind == "limit" ? new[] { Batch("a"), Batch("b") } : new[] { Batch(chain: kind == "duplicate", target:
                    kind == "unknown" ? (ParticipantId?)new ParticipantId("missing") : null) }),
                context => new CombatReactionEvaluation(runtime, new[] { Batch() }));
            var state = State();
            var result = CombatEngine.ResolveTiming(state, 0, CombatTimingSignal.BattleStarted("start", new[] { P }),
                reactionRuntime: runtime, reactionLimits: new CombatReactionLimits(kind == "limit" ? 1 : 100));
            Assert.AreEqual(error, result.Error); Assert.AreSame(state, result.State); Assert.IsNull(result.ReactionRuntime); Assert.IsEmpty(result.RuleEvents);
        }

        [Test] public void DescendantsRespectTheSameDepthLimitAsOrdinaryReactions()
        {
            Runtime runtime = null;
            runtime = new Runtime(context => new CombatReactionEvaluation(runtime, new[] { Batch("root", true) }),
                context => new CombatReactionEvaluation(runtime, new[] { Batch("child") }));
            var state = State();
            var result = CombatEngine.ResolveTiming(state, 0, CombatTimingSignal.BattleStarted("start", new[] { P }),
                reactionRuntime: runtime, reactionLimits: new CombatReactionLimits(10, 1));
            Assert.AreEqual(CombatError.ReactionLimitExceeded, result.Error); Assert.AreSame(state, result.State);
        }
    }
}
