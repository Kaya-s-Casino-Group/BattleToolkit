using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CombatActionCompletionRuntimeTests
    {
        private static readonly ParticipantId P = new ParticipantId("p"), E = new ParticipantId("e");
        private sealed class Runtime : ICombatActionCompletionReactionRuntime
        {
            private readonly Func<CombatActionCompletionReactionContext, CombatReactionEvaluation> action;
            private readonly Func<CombatReactionContext, CombatReactionEvaluation> fact;
            internal Runtime(Func<CombatActionCompletionReactionContext, CombatReactionEvaluation> action,
                Func<CombatReactionContext, CombatReactionEvaluation> fact = null) { this.action = action; this.fact = fact; }
            public CombatReactionEvaluation ReactActionCompleted(CombatActionCompletionReactionContext context) => action(context);
            public CombatReactionEvaluation React(CombatReactionContext context) => fact == null ? new CombatReactionEvaluation(this) : fact(context);
        }
        private static CombatState State(StatusInstance status = null) => new CombatState("action", 0, new[] {
            new CombatantState(P, "p", "players", 20, 20), new CombatantState(E, "e", "enemies", 20, 20) }, status == null ? null : new[] { status });
        private static CombatReactionBatch Batch(string id = "batch", bool chain = false, ParticipantId? target = null) =>
            new CombatReactionBatch(id, new[] { new CombatReaction(new CombatEffect(CombatEffectKind.Damage, P, target ?? E, 3),
                "action", P, canTriggerReactions: chain, sourceInstanceId: "gear") });

        [Test] public void CompletionReadsPostBleedStateAndSpentStatusBeforeDispatchingChildren()
        {
            var bleed = new StatusDefinitionId("bleed");
            var catalog = new CombatStatusCatalog("test", new[] { new CombatStatusDefinition(bleed, StatusTags.None,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.ActionCompleted, 5) }, StatusLifetimeSpec.ForTriggers(1)) });
            var state = State(new StatusInstance(new StatusInstanceId("bleed"), bleed, EntityRef.Participant(E), EntityRef.Participant(P), 1, StatusDuration.ForTriggers(1)));
            int calls = 0; Runtime runtime = null;
            runtime = new Runtime(context => {
                calls++; Assert.AreEqual(15, context.State.Get(P).Health); Assert.IsEmpty(context.State.Statuses);
                Assert.IsNull(context.Event.Effect); CollectionAssert.AreEqual(new[] { P }, CombatTargetResolver.Resolve(context.State, E, CombatTargetSpec.EventActor(), context.Event));
                return new CombatReactionEvaluation(runtime, new[] { Batch() });
            });
            var result = CombatEngine.ResolveTiming(state, 0, CombatTimingSignal.ActionCompleted("action/1", P, "play"), statusCatalog: catalog, reactionRuntime: runtime);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(1, calls); Assert.AreEqual(17, result.State.Get(E).Health);
            Assert.AreEqual(CombatEffectOrigin.Periodic, result.Events.First().Provenance.Origin);
            Assert.AreEqual(0, result.Events.Last().Provenance.ParentSequenceIndex); Assert.AreEqual("batch", result.Events.Last().Provenance.ActivationId);
        }

        [Test] public void ActionOnlyHookNeverReceivesPhaseClocksAndEventActorRejectsOtherEventKinds()
        {
            var runtime = new Runtime(context => { Assert.Fail("A boundary entered the action hook."); return null; });
            var state = State(); var result = CombatEngine.ResolveTiming(state, 0, CombatTimingSignal.PhaseEnding("end", new[] { P }, "player"), reactionRuntime: runtime);
            Assert.IsTrue(result.Accepted); Assert.AreSame(runtime, result.ReactionRuntime);
            Assert.Throws<ArgumentException>(() => CombatTargetResolver.Resolve(state, E, CombatTargetSpec.EventActor(), result.RuleEvents.Single()));
            var hit = CombatEngine.Resolve(state, 0, new[] { new CombatEffect(CombatEffectKind.Damage, P, E, 1) });
            Assert.Throws<ArgumentException>(() => CombatTargetResolver.Resolve(state, E, CombatTargetSpec.EventActor(), hit.RuleEvents.Single()));
            Assert.Throws<ArgumentException>(() => CombatTargetResolver.Resolve(state, E, CombatTargetSpec.EventActor()));
        }

        [Test] public void EventActorDoesNotRetargetWhenTheActorDies()
        {
            var clock = CombatEngine.ResolveTiming(State(), 0, CombatTimingSignal.ActionCompleted("action/1", P, "play")).RuleEvents.Single();
            var dead = CombatEngine.Resolve(State(), 0, new[] { CombatEffect.Eliminate(E, P) }).State;
            Assert.IsEmpty(CombatTargetResolver.Resolve(dead, E, CombatTargetSpec.EventActor(), clock));
        }

        [TestCase("null", CombatError.InvalidReaction)] [TestCase("unknown", CombatError.InvalidReaction)]
        [TestCase("limit", CombatError.ReactionLimitExceeded)] [TestCase("duplicate", CombatError.InvalidReaction)]
        [TestCase("depth", CombatError.ReactionLimitExceeded)]
        public void InvalidCompletionResponsesRollbackTheClockAndRuntime(string kind, CombatError error)
        {
            Runtime runtime = null;
            runtime = new Runtime(context => kind == "null" ? null : new CombatReactionEvaluation(runtime,
                kind == "limit" ? new[] { Batch("a"), Batch("b") } : new[] { Batch(chain: kind == "duplicate" || kind == "depth",
                    target: kind == "unknown" ? (ParticipantId?)new ParticipantId("missing") : null) }),
                context => new CombatReactionEvaluation(runtime, new[] { Batch(kind == "depth" ? "child" : "batch") }));
            var state = State(); var result = CombatEngine.ResolveTiming(state, 0, CombatTimingSignal.ActionCompleted("action/1", P, "play"),
                reactionRuntime: runtime, reactionLimits: new CombatReactionLimits(kind == "limit" ? 1 : 100, kind == "depth" ? 1 : 10));
            Assert.AreEqual(error, result.Error); Assert.AreSame(state, result.State); Assert.IsNull(result.ReactionRuntime); Assert.IsEmpty(result.RuleEvents);
        }
    }
}
