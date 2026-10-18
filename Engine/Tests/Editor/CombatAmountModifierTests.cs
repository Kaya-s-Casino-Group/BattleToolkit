using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CombatAmountModifierTests
    {
        private static readonly ParticipantId A = new ParticipantId("amount-a"), B = new ParticipantId("amount-b");
        private static CombatState State(int sourceHealth = 100) => new CombatState("amount-combat", 0, new[] {
            new CombatantState(A, "hero", "players", 100, sourceHealth), new CombatantState(B, "foe", "enemies", 100, 100)
        });
        private sealed class Modifier : ICombatAmountModifier
        {
            private readonly Func<CombatAmountContext, CombatAmountModification> apply;
            public readonly List<CombatAmountContext> Calls = new List<CombatAmountContext>();
            public Modifier(Func<CombatAmountContext, CombatAmountModification> apply = null) { this.apply = apply; }
            public CombatAmountModification Modify(CombatAmountContext context)
            { Calls.Add(context); return apply == null ? new CombatAmountModification(context.Amount) : apply(context); }
        }

        [TestCase(true)] [TestCase(false)]
        public void InvalidModifierReportRejectsTheWholeBatch(bool missingReport)
        {
            var before = State();
            var modifier = new Modifier(context => context.Effect.Kind == CombatEffectKind.Heal && context.Stage == CombatAmountStage.Incoming
                ? missingReport ? null : new CombatAmountModification(context.Amount + 1) : new CombatAmountModification(context.Amount));
            var result = CombatEngine.Resolve(before, before.Revision, new[] {
                new CombatEffect(CombatEffectKind.Damage, A, B, 10), new CombatEffect(CombatEffectKind.Heal, A, A, 10)
            }, amountModifier: modifier);
            Assert.AreEqual(CombatError.InvalidAmountModifier, result.Error); Assert.AreSame(before, result.State);
            Assert.IsEmpty(result.RuleEvents); Assert.AreEqual(100, before.Get(B).Health);
        }

        [Test] public void PrimaryOutgoingUsesBatchStartAndIncomingUsesLatestStateExactlyOnce()
        {
            var before = State(); var modifier = new Modifier();
            var result = CombatEngine.Resolve(before, before.Revision, new[] {
                new CombatEffect(CombatEffectKind.Damage, A, B, 10), new CombatEffect(CombatEffectKind.Damage, A, B, 10)
            }, amountModifier: modifier);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(4, modifier.Calls.Count);
            CollectionAssert.AreEqual(new[] { 100, 100 }, modifier.Calls.Where(item => item.Stage == CombatAmountStage.Outgoing).Select(item => item.Combat.Get(B).Health));
            CollectionAssert.AreEqual(new[] { 100, 90 }, modifier.Calls.Where(item => item.Stage == CombatAmountStage.Incoming).Select(item => item.Combat.Get(B).Health));
            Assert.IsTrue(result.Events.All(item => item.OutgoingAmountModification == null && item.IncomingAmountModification == null));
        }

        [Test] public void EliminationsStatusApplicationsAndEffectsSkippedAfterDeathDoNotCallNumericModifiers()
        {
            var before = State(); var modifier = new Modifier();
            var id = new StatusDefinitionId("test-status");
            var catalog = new CombatStatusCatalog("amount-status.1", new[] {
                new CombatStatusDefinition(id, StatusTags.Control, new StatusBehaviorSpec[] { new BlockActionWindowSpec() }, StatusLifetimeSpec.ForTriggers(1))
            });
            var status = new StatusInstance(new StatusInstanceId("s"), id, EntityRef.Participant(A), EntityRef.Participant(B), 1, StatusDuration.ForTriggers(1));
            var result = CombatEngine.Resolve(before, before.Revision, new[] {
                CombatEffect.ApplyStatus(status), new CombatEffect(CombatEffectKind.ClearBlock, A, B), CombatEffect.Eliminate(A, B),
                new CombatEffect(CombatEffectKind.Heal, A, B, 10), new CombatEffect(CombatEffectKind.GainBlock, A, B, 10)
            }, statusCatalog: catalog, amountModifier: modifier);
            Assert.IsTrue(result.Accepted); Assert.IsEmpty(modifier.Calls); Assert.IsFalse(result.State.Get(B).IsAlive);
        }

        [Test] public void PeriodicEffectsWithDefeatedAppliersStillReachBothNumericStages()
        {
            var dead = State(0); var definition = new StatusDefinitionId("dot");
            var status = new StatusInstance(new StatusInstanceId("dot/1"), definition, EntityRef.Participant(A), EntityRef.Participant(B), 1, StatusDuration.ForTriggers(1));
            var before = new CombatState(dead.Id, dead.Revision, dead.Combatants, new[] { status });
            var catalog = new CombatStatusCatalog("amount-dot.1", new[] { new CombatStatusDefinition(definition, StatusTags.None,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.ActionCompleted, 10) }, StatusLifetimeSpec.ForTriggers(1)) });
            var modifier = new Modifier(context => new CombatAmountModification(context.Amount, new[] {
                new CombatAmountContribution("test", "amount", flatAdjustment: context.Stage == CombatAmountStage.Outgoing ? 4 : -2)
            }));
            var result = CombatEngine.ResolveTiming(before, before.Revision, CombatTimingSignal.ActionCompleted("tick", B, "act"),
                statusCatalog: catalog, amountModifier: modifier);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(2, modifier.Calls.Count); Assert.AreEqual(12, result.Events.Single().SettledDamage);
            Assert.IsTrue(modifier.Calls.All(item => item.Provenance.Origin == CombatEffectOrigin.Periodic && !item.Combat.Get(A).IsAlive));
            Assert.IsEmpty(result.State.Statuses);
        }

        [Test] public void BlockOverflowAfterModificationIsAnAtomicFailure()
        {
            var before = State(); var modifier = new Modifier(context => new CombatAmountModification(context.Amount,
                context.Effect.Kind == CombatEffectKind.GainBlock && context.Stage == CombatAmountStage.Incoming
                    ? new[] { new CombatAmountContribution("r", "huge-shield", int.MaxValue) } : null));
            var result = CombatEngine.Resolve(before, before.Revision, new[] {
                new CombatEffect(CombatEffectKind.Damage, A, B, 10), new CombatEffect(CombatEffectKind.GainBlock, A, A, 1)
            }, amountModifier: modifier);
            Assert.AreEqual(CombatError.Overflow, result.Error); Assert.AreSame(before, result.State); Assert.IsEmpty(result.Events);
        }
    }
}
