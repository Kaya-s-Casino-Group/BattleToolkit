using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CombatReactionRuntimeTests
    {
        private static readonly ParticipantId Owner = new ParticipantId("runtime-owner"), Enemy = new ParticipantId("runtime-enemy");
        private static CombatState State() => new CombatState("runtime", 0, new[] {
            new CombatantState(Owner, "hero", "player", 100, 100), new CombatantState(Enemy, "foe", "enemy", 100, 100) });
        private static CombatEffect Hit() => new CombatEffect(CombatEffectKind.Damage, Owner, Enemy, 1);
        private sealed class Runtime : ICombatReactionRuntime
        {
            internal readonly int Remaining;
            private readonly string error;
            internal Runtime(int remaining = 1, string error = null) { Remaining = remaining; this.error = error; }
            public CombatReactionEvaluation React(CombatReactionContext context)
            {
                if (error == "null") return null;
                if (Remaining == 0) return new CombatReactionEvaluation(this);
                string id = error == "duplicate" ? "same" : context.Event.Revision + "/" + context.Event.SequenceIndex;
                var target = error == "unknown" ? new ParticipantId("unknown") : Owner;
                var child = new CombatReaction(new CombatEffect(CombatEffectKind.GainBlock, Owner, target, 2), "runtime.rule", Owner, sourceInstanceId: "runtime.instance");
                return new CombatReactionEvaluation(new Runtime(Remaining - 1, error), new[] { new CombatReactionBatch(id, new[] { child, child }) });
            }
        }

        [Test] public void SharedImmutableRuntimeIsVisibleToLaterFactsAndReturnedOnlyOnSuccess()
        {
            var input = new Runtime(); var before = State();
            var result = CombatEngine.Resolve(before, 0, new[] { Hit(), Hit() }, reactionRuntime: input);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(1, input.Remaining); Assert.AreEqual(0, ((Runtime)result.ReactionRuntime).Remaining);
            Assert.AreEqual(4, result.State.Get(Owner).Block); Assert.AreEqual(98, result.State.Get(Enemy).Health);
            var children = result.Events.Where(item => item.Provenance.ActivationId != null).ToArray();
            Assert.AreEqual(2, children.Length); Assert.AreEqual(children[0].Provenance.ActivationId, children[1].Provenance.ActivationId);
            Assert.AreEqual(0, before.Get(Owner).Block);
        }

        [TestCase("null", CombatError.InvalidReaction)] [TestCase("unknown", CombatError.InvalidReaction)]
        [TestCase("duplicate", CombatError.InvalidReaction)] [TestCase("limit", CombatError.ReactionLimitExceeded)]
        public void InvalidRuntimeResponsesAndLimitsDiscardBothCombatAndRuntimeDrafts(string error, CombatError expected)
        {
            var input = new Runtime(2, error); var before = State();
            var result = CombatEngine.Resolve(before, 0, new[] { Hit(), Hit() }, reactionRuntime: input,
                reactionLimits: error == "limit" ? new CombatReactionLimits(1) : null);
            Assert.AreEqual(expected, result.Error); Assert.AreSame(before, result.State); Assert.IsNull(result.ReactionRuntime);
            Assert.AreEqual(2, input.Remaining); Assert.IsEmpty(result.RuleEvents);
        }
    }
}
