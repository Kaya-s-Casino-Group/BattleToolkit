using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    public sealed class CombatStatusBehaviorTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), Enemy = new ParticipantId("enemy");
        private const string Round1 = "status-battle/round/0000000001", Round2 = "status-battle/round/0000000002";
        private static CombatState Create(IEnumerable<StatusInstance> statuses = null, long revision = 0) =>
            new CombatState("status-battle", revision, new[] {
                new CombatantDefinition("hero", 20).Create(Player, "player"), new CombatantDefinition("enemy", 20).Create(Enemy, "enemy") }, statuses);
        private static StatusInstance Status(string id, string definition, StatusDuration duration, ParticipantId? target = null,
            ParticipantId? source = null, int stacks = 1) => new StatusInstance(new StatusInstanceId(id), new StatusDefinitionId(definition),
                EntityRef.Participant(source ?? Player), EntityRef.Participant(target ?? Enemy), stacks, duration);
        private static CombatStatusDefinition Block(string id = "custom-stop", StatusReapplyPolicy reapply = StatusReapplyPolicy.KeepLonger) =>
            new CombatStatusDefinition(new StatusDefinitionId(id), StatusTags.Control, new StatusBehaviorSpec[] { new BlockActionWindowSpec() },
                StatusLifetimeSpec.ForTriggers(1), reapply);
        private static CombatStatusDefinition GuardDefinition(string id = "custom-guard", StatusReapplyPolicy reapply = StatusReapplyPolicy.KeepLonger) =>
            new CombatStatusDefinition(new StatusDefinitionId(id), StatusTags.None,
                new StatusBehaviorSpec[] { new PreventStatusApplicationSpec(StatusTags.Control) }, StatusLifetimeSpec.ThroughNextPhase("enemy"), reapply);
        private static CombatStatusCatalog Catalog(params CombatStatusDefinition[] definitions) =>
            new CombatStatusCatalog("statuses.test.1", definitions.Length == 0 ? new[] { Block(), GuardDefinition() } : definitions);
        private static CombatEffect Damage(long amount = 1) => new CombatEffect(CombatEffectKind.Damage, Player, Enemy, amount);
        private sealed class UnknownBehavior : StatusBehaviorSpec { }

        [Test] public void DefinitionsRejectUnknownDuplicateAndAmbiguousBehaviorsAndDefensivelyCopy()
        {
            var list = new List<StatusBehaviorSpec> { new BlockActionWindowSpec() };
            var definition = new CombatStatusDefinition(new StatusDefinitionId("arbitrary"), StatusTags.Control, list, StatusLifetimeSpec.ForTriggers(2));
            list.Clear(); Assert.AreEqual(1, definition.Behaviors.Count);
            Assert.AreEqual(StatusReapplyPolicy.KeepLonger, definition.ReapplyPolicy);
            Assert.Throws<NotSupportedException>(() => ((IList<StatusBehaviorSpec>)definition.Behaviors).Clear());
            Assert.Throws<ArgumentException>(() => new PreventStatusApplicationSpec(StatusTags.None));
            Assert.Throws<ArgumentException>(() => new PreventStatusApplicationSpec((StatusTags)2));
            Assert.Throws<ArgumentException>(() => new CombatStatusDefinition(definition.Id, (StatusTags)2, list, StatusLifetimeSpec.UntilRemoved()));
            Assert.Throws<ArgumentException>(() => new CombatStatusDefinition(definition.Id, StatusTags.None,
                new StatusBehaviorSpec[] { new UnknownBehavior() }, StatusLifetimeSpec.UntilRemoved()));
            Assert.Throws<ArgumentException>(() => new CombatStatusDefinition(definition.Id, StatusTags.None,
                new StatusBehaviorSpec[] { new BlockActionWindowSpec(), new BlockActionWindowSpec() }, StatusLifetimeSpec.UntilRemoved()));
            Assert.Throws<ArgumentException>(() => new CombatStatusDefinition(definition.Id, StatusTags.None,
                new StatusBehaviorSpec[] { new BlockActionWindowSpec(), new PreventStatusApplicationSpec(StatusTags.Control) }, StatusLifetimeSpec.ForTriggers(1)));
            Assert.Throws<ArgumentException>(() => new CombatStatusDefinition(definition.Id, StatusTags.None, list,
                StatusLifetimeSpec.UntilRemoved(), (StatusReapplyPolicy)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => StatusLifetimeSpec.ForTriggers(0));
            Assert.Throws<ArgumentException>(() => StatusLifetimeSpec.ThroughNextPhase(" "));
            Assert.Throws<ArgumentException>(() => Catalog(definition, definition));
            var definitions = new List<CombatStatusDefinition> { definition }; var catalog = new CombatStatusCatalog("copy.1", definitions);
            definitions.Clear(); Assert.AreSame(definition, catalog.Get(definition.Id));
            Assert.IsNull(catalog.Find(new StatusDefinitionId("missing")));
            Assert.Throws<ArgumentException>(() => catalog.Get(new StatusDefinitionId("missing")));
            Assert.Throws<NotSupportedException>(() => ((IDictionary<StatusDefinitionId, CombatStatusDefinition>)catalog.Definitions).Clear());
        }

        [Test] public void SameBatchImmunityBlocksControlConsumesOnlyOnInterceptAndThenAllowsNextControl()
        {
            var state = Create(); var catalog = Catalog();
            var result = CombatEngine.Resolve(state, 0, new[] {
                CombatEffect.ApplyStatus(Status("guard", "custom-guard", StatusDuration.ForTriggers(1))), Damage(),
                CombatEffect.ApplyStatus(Status("blocked", "custom-stop", StatusDuration.ForTriggers(1))),
                CombatEffect.ApplyStatus(Status("allowed", "custom-stop", StatusDuration.ForTriggers(1))), Damage()
            }, statusCatalog: catalog);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(18, result.State.Get(Enemy).Health);
            Assert.AreEqual("allowed", result.State.Statuses.Single().Id.Value); Assert.IsEmpty(state.Statuses);
            var blocked = result.RuleEvents.OfType<CombatStatusBlockedEvent>().Single();
            Assert.AreEqual("blocked", blocked.Status.Id.Value); Assert.AreEqual("guard", blocked.BlockingStatusId.Value);
            var consumed = result.RuleEvents.OfType<CombatStatusChangedEvent>().Single();
            Assert.AreEqual(CombatStatusChangeReason.Consumed, consumed.Reason); Assert.IsNull(consumed.StatusAfter);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 2, 3, 4 }, result.RuleEvents.Select(item => item.Index));
            CollectionAssert.AreEqual(Enumerable.Range(0, 6), result.RuleEvents.Select(item => item.SequenceIndex));
            Assert.AreEqual(2, result.Events.Count); Assert.AreEqual(1, result.State.Revision);
        }

        [Test] public void PhaseImmunityBlocksEveryMatchingAttemptWithoutBeingConsumedOrCleansingExistingControl()
        {
            var existing = Status("old-stop", "custom-stop", StatusDuration.ForTriggers(1));
            var state = Create(new[] { existing });
            var result = CombatEngine.Resolve(state, 0, new[] {
                CombatEffect.ApplyStatus(Status("guard", "custom-guard", StatusDuration.UntilPhaseEnd(Round1, "enemy"))),
                CombatEffect.ApplyStatus(Status("stop1", "custom-stop", StatusDuration.ForTriggers(1))),
                CombatEffect.ApplyStatus(Status("stop2", "custom-stop", StatusDuration.ForTriggers(1)))
            }, statusCatalog: Catalog());
            Assert.IsTrue(result.Accepted); Assert.AreEqual(2, result.State.Statuses.Count);
            Assert.AreSame(existing, result.State.Statuses[0]); Assert.AreEqual(2, result.RuleEvents.OfType<CombatStatusBlockedEvent>().Count());
            Assert.IsFalse(result.RuleEvents.OfType<CombatStatusChangedEvent>().Any());
            Assert.AreEqual(1, CombatStatusLifecycle.Blockers(result.State, Enemy, Catalog()).Count);
        }

        [Test] public void MultipleImmunitiesUseOneStableInterceptorAndDoNotSpendUnrelatedTargetsCharges()
        {
            var first = Status("first", "custom-guard", StatusDuration.ForTriggers(2));
            var second = Status("second", "custom-guard", StatusDuration.ForTriggers(2));
            var own = Status("own", "custom-guard", StatusDuration.ForTriggers(2), Player);
            var state = Create(new[] { first, second, own }); var catalog = Catalog(Block(), GuardDefinition(reapply: StatusReapplyPolicy.IndependentInstances));
            var result = CombatEngine.Resolve(state, 0, new[] { CombatEffect.ApplyStatus(Status("blocked", "custom-stop", StatusDuration.ForTriggers(1))) }, statusCatalog: catalog);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(first.Id, result.RuleEvents.OfType<CombatStatusBlockedEvent>().Single().BlockingStatusId);
            Assert.AreEqual(1, result.State.Statuses[0].Duration.RemainingTriggers); Assert.AreSame(second, result.State.Statuses[1]); Assert.AreSame(own, result.State.Statuses[2]);
        }

        [Test] public void UnknownDefinitionsAndAmbiguousRuntimeTriggerOverridesRollbackTheBatch()
        {
            var state = Create();
            var unknown = CombatEngine.Resolve(state, 0, new[] { Damage(), CombatEffect.ApplyStatus(Status("x", "unknown", StatusDuration.UntilRemoved())) }, statusCatalog: Catalog());
            Assert.AreEqual(CombatError.InvalidEffect, unknown.Error); Assert.AreSame(state, unknown.State); Assert.IsEmpty(unknown.RuleEvents);
            var unknownState = Create(new[] { Status("x", "unknown", StatusDuration.UntilRemoved()) });
            Assert.AreEqual(CombatError.InvalidState, CombatEngine.Resolve(unknownState, 0, new[] { Damage() }, statusCatalog: Catalog()).Error);
            var definition = new CombatStatusDefinition(new StatusDefinitionId("both"), StatusTags.None,
                new StatusBehaviorSpec[] { new BlockActionWindowSpec(), new PreventStatusApplicationSpec(StatusTags.Control) }, StatusLifetimeSpec.UntilRemoved());
            var result = CombatEngine.Resolve(state, 0, new[] { Damage(), CombatEffect.ApplyStatus(Status("x", "both", StatusDuration.ForTriggers(1))) }, statusCatalog: Catalog(definition));
            Assert.AreEqual(CombatError.InvalidEffect, result.Error); Assert.AreSame(state, result.State); Assert.IsEmpty(result.RuleEvents);
        }

        [Test] public void DeadTargetsSkipStatusChecksAndDoNotSpendTheirImmunity()
        {
            var guard = Status("guard", "custom-guard", StatusDuration.ForTriggers(1)); var state = Create(new[] { guard });
            var result = CombatEngine.Resolve(state, 0, new[] { CombatEffect.Eliminate(Player, Enemy),
                CombatEffect.ApplyStatus(Status("stop", "custom-stop", StatusDuration.ForTriggers(1))),
                CombatEffect.ApplyStatus(Status("unknown", "unknown", StatusDuration.UntilRemoved())) }, statusCatalog: Catalog());
            Assert.IsTrue(result.Accepted); Assert.AreSame(guard, result.State.Statuses.Single()); Assert.AreEqual(1, result.RuleEvents.Count);
            Assert.IsEmpty(CombatStatusLifecycle.Blockers(result.State, Enemy, Catalog()));
        }

        [Test] public void SkippingConsumesEveryCountedBlockerOnceButKeepsPhaseBlockers()
        {
            var state = Create(new[] {
                Status("one", "custom-stop", StatusDuration.ForTriggers(1)),
                Status("two", "custom-stop", StatusDuration.ForTriggers(2)),
                Status("phase", "custom-stop", StatusDuration.UntilPhaseEnd(Round1, "enemy")) });
            var catalog = Catalog(Block(reapply: StatusReapplyPolicy.IndependentInstances));
            var blockers = CombatStatusLifecycle.Blockers(state, Enemy, catalog); Assert.AreEqual(3, blockers.Count);
            Assert.Throws<NotSupportedException>(() => ((IList<StatusInstance>)blockers).Clear());
            var result = CombatStatusLifecycle.SkipActionWindow(state, 0, Enemy, catalog);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(1, result.State.Revision); Assert.AreEqual(2, result.State.Statuses.Count);
            Assert.AreEqual(1, result.State.Statuses[0].Duration.RemainingTriggers); Assert.AreEqual("phase", result.State.Statuses[1].Id.Value);
            var skipped = result.RuleEvents.OfType<CombatActionWindowSkippedEvent>().Single(); Assert.AreEqual(Enemy, skipped.Actor); Assert.AreEqual(3, skipped.StatusIds.Count);
            Assert.AreEqual(2, result.RuleEvents.OfType<CombatStatusChangedEvent>().Count());
            Assert.IsTrue(result.RuleEvents.All(item => item.Index == -1 && item.Effect == null));
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, result.RuleEvents.Select(item => item.SequenceIndex));
            Assert.IsEmpty(result.Events); Assert.AreEqual(3, state.Statuses.Count);
        }

        [Test] public void SkipRequiresAnActualLivingActionAndDoesNotMutateDuringQueriesOrRejections()
        {
            var state = Create(new[] { Status("one", "custom-stop", StatusDuration.ForTriggers(1)) }); var catalog = Catalog();
            CombatStatusLifecycle.Blockers(state, Enemy, catalog); CombatStatusLifecycle.Blockers(state, Enemy, catalog);
            Assert.AreEqual(1, state.Statuses.Single().Duration.RemainingTriggers);
            Assert.AreEqual(CombatError.StaleRevision, CombatStatusLifecycle.SkipActionWindow(state, 1, Enemy, catalog).Error);
            Assert.AreEqual(CombatError.InvalidEffect, CombatStatusLifecycle.SkipActionWindow(state, 0, Player, catalog).Error);
            Assert.AreEqual(CombatError.InvalidEffect, CombatStatusLifecycle.SkipActionWindow(state, 0, new ParticipantId("missing"), catalog).Error);
            Assert.AreEqual(CombatError.InvalidEffect, CombatStatusLifecycle.SkipActionWindow(state, 0, Enemy, null).Error);
            Assert.IsEmpty(CombatStatusLifecycle.Blockers(state, Enemy, null));
            var overflow = Create(state.Statuses, long.MaxValue);
            var rejected = CombatStatusLifecycle.SkipActionWindow(overflow, long.MaxValue, Enemy, catalog);
            Assert.AreEqual(CombatError.Overflow, rejected.Error); Assert.AreSame(overflow, rejected.State); Assert.IsEmpty(rejected.RuleEvents);
        }

        [Test] public void ReapplyKeepsIdentityUsesTheLargerBudgetAndRefreshesAttributionWithoutAddingCounts()
        {
            var original = Status("original", "custom-stop", StatusDuration.ForTriggers(3), stacks: 2); var state = Create(new[] { original });
            var result = CombatEngine.Resolve(state, 0, new[] {
                CombatEffect.ApplyStatus(Status("incoming", "custom-stop", StatusDuration.ForTriggers(2), source: Enemy, stacks: 1))
            }, statusCatalog: Catalog());
            Assert.IsTrue(result.Accepted); var refreshed = result.State.Statuses.Single();
            Assert.AreEqual(original.Id, refreshed.Id); Assert.AreEqual(3, refreshed.Duration.RemainingTriggers);
            Assert.AreEqual(2, refreshed.Stacks); Assert.AreEqual(Enemy.Value, refreshed.Source.Id);
            var changed = result.RuleEvents.OfType<CombatStatusChangedEvent>().Single();
            Assert.AreEqual(CombatStatusChangeReason.Refreshed, changed.Reason); Assert.AreSame(original, changed.StatusBefore); Assert.AreSame(refreshed, changed.StatusAfter);
            var longer = CombatEngine.Resolve(result.State, result.State.Revision,
                new[] { CombatEffect.ApplyStatus(Status("later", "custom-stop", StatusDuration.ForTriggers(5))) }, statusCatalog: Catalog());
            Assert.AreEqual(5, longer.State.Statuses.Single().Duration.RemainingTriggers); Assert.AreEqual(original.Id, longer.State.Statuses.Single().Id);
        }

        [Test] public void IndependentPolicyPreservesInstancesAndDefaultPolicyRejectsInvalidDuplicateState()
        {
            var a = Status("a", "custom-stop", StatusDuration.ForTriggers(1)); var b = Status("b", "custom-stop", StatusDuration.ForTriggers(2));
            var state = Create(); var result = CombatEngine.Resolve(state, 0, new[] { CombatEffect.ApplyStatus(a), CombatEffect.ApplyStatus(b) },
                statusCatalog: Catalog(Block(reapply: StatusReapplyPolicy.IndependentInstances)));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(2, result.State.Statuses.Count); Assert.AreEqual(2, result.RuleEvents.OfType<CombatStatusEvent>().Count());
            Assert.AreEqual(CombatError.InvalidState, CombatEngine.Resolve(result.State, result.State.Revision, new[] { Damage() }, statusCatalog: Catalog()).Error);
        }

        [Test] public void ReapplyUsesLaterCanonicalPhaseAndRejectsIncompatibleKindsOrPhaseNames()
        {
            var state = Create(new[] { Status("guard", "custom-guard", StatusDuration.UntilPhaseEnd(Round1, "enemy")) }); var catalog = Catalog();
            var later = CombatEngine.Resolve(state, 0, new[] { CombatEffect.ApplyStatus(Status("later", "custom-guard", StatusDuration.UntilPhaseEnd(Round2, "enemy"))) }, statusCatalog: catalog);
            Assert.IsTrue(later.Accepted); Assert.AreEqual(Round2, later.State.Statuses.Single().Duration.ScopeId);
            var earlier = CombatEngine.Resolve(later.State, 1, new[] { CombatEffect.ApplyStatus(Status("early", "custom-guard", StatusDuration.UntilPhaseEnd(Round1, "enemy"))) }, statusCatalog: catalog);
            Assert.IsTrue(earlier.Accepted); Assert.AreEqual(Round2, earlier.State.Statuses.Single().Duration.ScopeId);
            foreach (var incompatible in new[] { StatusDuration.ForTriggers(1), StatusDuration.UntilPhaseEnd(Round2, "player"), StatusDuration.UntilPhaseEnd("other/round/0000000002", "enemy") })
            {
                var rejected = CombatEngine.Resolve(state, 0, new[] { Damage(), CombatEffect.ApplyStatus(Status("bad", "custom-guard", incompatible)) }, statusCatalog: catalog);
                Assert.AreEqual(CombatError.InvalidEffect, rejected.Error); Assert.AreSame(state, rejected.State); Assert.IsEmpty(rejected.RuleEvents);
            }
        }

        [Test] public void RefreshAndInterceptionAtCapacityDoNotRequireAnotherStatusSlot()
        {
            var passive = new CombatStatusDefinition(new StatusDefinitionId("passive"), StatusTags.None, Array.Empty<StatusBehaviorSpec>(),
                StatusLifetimeSpec.UntilRemoved(), StatusReapplyPolicy.IndependentInstances);
            var statuses = Enumerable.Range(0, 255).Select(i => Status("passive/" + i, "passive", StatusDuration.UntilRemoved()))
                .Concat(new[] { Status("guard", "custom-guard", StatusDuration.ForTriggers(2)) });
            var state = Create(statuses); var catalog = Catalog(passive, Block(), GuardDefinition());
            var result = CombatEngine.Resolve(state, 0, new[] {
                CombatEffect.ApplyStatus(Status("refresh", "custom-guard", StatusDuration.ForTriggers(3))),
                CombatEffect.ApplyStatus(Status("blocked", "custom-stop", StatusDuration.ForTriggers(1))) }, statusCatalog: catalog);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(256, result.State.Statuses.Count);
            Assert.AreEqual(2, result.State.Statuses.Last().Duration.RemainingTriggers); Assert.AreEqual(3, result.RuleEvents.Count);
        }

        [Test] public void LaterFailureRollsBackImmunityConsumptionAndEarlierDamage()
        {
            var state = Create(new[] { Status("guard", "custom-guard", StatusDuration.ForTriggers(1)) });
            var result = CombatEngine.Resolve(state, 0, new[] { Damage(),
                CombatEffect.ApplyStatus(Status("blocked", "custom-stop", StatusDuration.ForTriggers(1))),
                new CombatEffect(CombatEffectKind.GainBlock, Player, Player, long.MaxValue) }, statusCatalog: Catalog());
            Assert.AreEqual(CombatError.Overflow, result.Error); Assert.AreSame(state, result.State); Assert.IsEmpty(result.RuleEvents);
            Assert.AreEqual(1, state.Statuses.Single().Duration.RemainingTriggers); Assert.AreEqual(20, state.Get(Enemy).Health);
        }

        [Test] public void PhaseAndRoundExpiryMatchExactBoundariesAndWorkAfterParticipantDeath()
        {
            var initial = Create(new[] {
                Status("phase", "custom-guard", StatusDuration.UntilPhaseEnd(Round1, "enemy")),
                Status("other-phase", "custom-guard", StatusDuration.UntilPhaseEnd(Round1, "player")),
                Status("future-phase", "custom-guard", StatusDuration.UntilPhaseEnd(Round2, "enemy")),
                Status("round", "custom-guard", StatusDuration.UntilRoundEnd(Round1)),
                Status("future-round", "custom-guard", StatusDuration.UntilRoundEnd(Round2)),
                Status("count", "custom-guard", StatusDuration.ForTriggers(1)), Status("permanent", "custom-guard", StatusDuration.UntilRemoved()) });
            var dead = CombatEngine.Resolve(initial, 0, new[] { CombatEffect.Eliminate(Player, Enemy) }).State;
            var phase = CombatStatusLifecycle.EndPhase(dead, dead.Revision, Round1, "enemy");
            Assert.IsTrue(phase.Accepted); Assert.AreEqual(6, phase.State.Statuses.Count); Assert.AreEqual(0, phase.State.Get(Enemy).Health);
            var expired = phase.RuleEvents.OfType<CombatStatusChangedEvent>().Single();
            Assert.AreEqual("phase", expired.StatusBefore.Id.Value); Assert.IsNull(expired.StatusAfter);
            Assert.AreEqual(CombatStatusChangeReason.Expired, expired.Reason); Assert.AreEqual(Round1, expired.ScopeId); Assert.AreEqual("enemy", expired.PhaseKey);
            Assert.AreEqual(-1, expired.Index); Assert.AreEqual(0, expired.SequenceIndex); Assert.IsNull(expired.Effect); Assert.IsEmpty(phase.Events);
            var round = CombatStatusLifecycle.EndRound(phase.State, phase.State.Revision, Round1);
            Assert.IsTrue(round.Accepted); Assert.AreEqual(5, round.State.Statuses.Count);
            CollectionAssert.AreEquivalent(new[] { "other-phase", "future-phase", "future-round", "count", "permanent" }, round.State.Statuses.Select(item => item.Id.Value));
        }

        [Test] public void EmptyExpiryPreservesRevisionAndInvalidOrStaleBoundariesDoNotChangeState()
        {
            var state = Create(new[] { Status("guard", "custom-guard", StatusDuration.UntilPhaseEnd(Round2, "enemy")) }, long.MaxValue);
            var unchanged = CombatStatusLifecycle.EndPhase(state, long.MaxValue, Round1, "enemy");
            Assert.IsTrue(unchanged.Accepted); Assert.AreSame(state, unchanged.State); Assert.IsEmpty(unchanged.RuleEvents);
            Assert.AreSame(state, CombatStatusLifecycle.EndRound(state, long.MaxValue, Round1).State);
            Assert.AreEqual(CombatError.StaleRevision, CombatStatusLifecycle.EndPhase(state, 0, Round2, "enemy").Error);
            Assert.AreEqual(CombatError.InvalidEffect, CombatStatusLifecycle.EndPhase(state, long.MaxValue, Round2, " ").Error);
            var overflow = CombatStatusLifecycle.EndPhase(state, long.MaxValue, Round2, "enemy");
            Assert.AreEqual(CombatError.Overflow, overflow.Error); Assert.AreSame(state, overflow.State); Assert.IsEmpty(overflow.RuleEvents);
        }
    }
}
