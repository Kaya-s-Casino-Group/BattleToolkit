using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CombatEliteBleedingTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), Enemy = new ParticipantId("enemy"),
            Other = new ParticipantId("other"), Ally = new ParticipantId("ally");
        private static StatusBehaviorSpec Incoming(decimal rate = 0.5m) => new IncomingAttackDamagePerStackSpec(rate);
        private static StatusBehaviorSpec Decay(CombatTimingKind timing = CombatTimingKind.ActionCompleted,
            PeriodicTimingSubject subject = PeriodicTimingSubject.Applier, int stacks = 1) => new StackDecaySpec(timing, subject, stacks);
        private static CombatStatusDefinition Bleeding(decimal rate = 0.5m) => new CombatStatusDefinition(
            new StatusDefinitionId("elite-bleeding"), StatusTags.Negative, new[] { Incoming(rate), Decay() },
            StatusLifetimeSpec.UntilRemoved(), StatusReapplyPolicy.AddStacks);
        private static CombatStatusCatalog Catalog(params CombatStatusDefinition[] definitions) =>
            new CombatStatusCatalog("elite-bleeding.test.1", definitions.Length == 0 ? new[] { Bleeding() } : definitions);
        private static CombatState State(IEnumerable<StatusInstance> statuses = null, long block = 0, long enemyHealth = 1000) =>
            new CombatState("elite-bleeding", 0, new[] {
                new CombatantState(Player, "hero", "players", 1000, 1000, block),
                new CombatantState(Enemy, "elite", "enemies", 1000, enemyHealth),
                new CombatantState(Other, "other", "enemies", 1000, 1000),
                new CombatantState(Ally, "ally", "players", 1000, 1000)
            }, statuses);
        private static StatusInstance Status(string id, int stacks, ParticipantId? source = null, ParticipantId? target = null,
            string definition = "elite-bleeding") => new StatusInstance(new StatusInstanceId(id), new StatusDefinitionId(definition),
                EntityRef.Participant(source ?? Enemy), EntityRef.Participant(target ?? Player), stacks, StatusDuration.UntilRemoved());
        private static CombatEffect Attack(long amount = 90, ParticipantId? source = null, ParticipantId? target = null,
            AttackOrigin origin = AttackOrigin.EnemyAttack) => new CombatEffect(CombatEffectKind.Damage, source ?? Enemy, target ?? Player,
                amount, attack: new AttackContext(source ?? Enemy, origin, amount));
        private static CombatTimingSignal Action(ParticipantId actor, string id = "action/1") =>
            CombatTimingSignal.ActionCompleted(id, actor, "attack");

        [TestCase(0, 90)] [TestCase(1, 135)] [TestCase(2, 180)] [TestCase(3, 225)] [TestCase(5, 315)]
        public void PrimaryEnemyAttackAddsEachLayerLinearly(int stacks, int expected)
        {
            var state = State(stacks == 0 ? null : new[] { Status("bleeding", stacks) });
            var result = CombatEngine.Resolve(state, 0, new[] { Attack() }, statusCatalog: Catalog());
            Assert.IsTrue(result.Accepted, result.Error.ToString()); var hit = result.Events.Single();
            Assert.AreEqual(expected, hit.SettledDamage); Assert.AreEqual(expected, hit.DamageToHealth);
            Assert.AreEqual(1000 - expected, result.State.Get(Player).Health); Assert.AreEqual(1000, state.Get(Player).Health);
            Assert.AreEqual(stacks, result.State.Statuses.Sum(item => item.Stacks));
        }

        [Test] public void MultipleAppliersAddLayersRatherThanMultiplyingSeparateBonuses()
        {
            var result = CombatEngine.Resolve(State(new[] { Status("a", 1), Status("b", 2, Other) }), 0,
                new[] { Attack() }, statusCatalog: Catalog());
            Assert.IsTrue(result.Accepted, result.Error.ToString()); Assert.AreEqual(225, result.Events.Single().SettledDamage);
        }

        [Test] public void OneCombinedHitIsAmplifiedBeforeBlockAndAuditsOnlyTheBleedingContribution()
        {
            var result = CombatEngine.Resolve(State(new[] { Status("bleeding", 2) }, block: 130), 0,
                new[] { Attack() }, new ReduceIncoming(), Catalog());
            Assert.IsTrue(result.Accepted, result.Error.ToString()); var hit = result.Events.Single();
            Assert.AreEqual(160, hit.SettledDamage); Assert.AreEqual(130, hit.BlockAbsorbed); Assert.AreEqual(30, hit.DamageToHealth);
            Assert.AreEqual("incoming.attack-per-stack.1", hit.IncomingModification.RuleVersion);
            Assert.AreEqual(90, hit.IncomingModification.InputAmount); Assert.AreEqual(160, hit.IncomingModification.OutputAmount);
            CollectionAssert.AreEqual(new[] { "test.reduce", "incoming.attack-per-stack" }, hit.IncomingModification.Triggers.Select(t => t.SourceId));
            StringAssert.Contains("80→160", hit.IncomingModification.Triggers.Last().Reason);
            StringAssert.Contains("+80", hit.IncomingModification.Triggers.Last().Reason);
            Assert.AreEqual(1, result.RuleEvents.OfType<CombatEvent>().Count());
        }

        [TestCase(AttackOrigin.Other, false, false)]
        [TestCase(AttackOrigin.EnemyAttack, true, false)]
        [TestCase(AttackOrigin.EnemyAttack, false, true)]
        public void OtherDamageSelfDamageAndFriendlyFireAreNotAmplified(AttackOrigin origin, bool self, bool friendly)
        {
            var source = self ? Player : Enemy; var target = friendly ? Other : Player;
            var result = CombatEngine.Resolve(State(new[] { Status("bleeding", 3, target: target) }), 0,
                new[] { Attack(source: source, target: target, origin: origin) }, statusCatalog: Catalog());
            Assert.IsTrue(result.Accepted, result.Error.ToString()); Assert.AreEqual(90, result.Events.Single().SettledDamage);
        }

        [TestCase(CombatEffectOrigin.Reflection)] [TestCase(CombatEffectOrigin.Periodic)] [TestCase(CombatEffectOrigin.Triggered)]
        public void DerivedEnemyAttackFactsAreNotAmplified(CombatEffectOrigin origin)
        {
            var result = CombatEngine.Resolve(State(new[] { Status("bleeding", 3) }), 0,
                new[] { new CombatEffect(CombatEffectKind.GainBlock, Enemy, Enemy, 1) }, statusCatalog: Catalog(),
                reactionProvider: new DerivedAttack(origin));
            Assert.IsTrue(result.Accepted, result.Error.ToString()); var hit = result.Events.Single(item => item.Effect.Kind == CombatEffectKind.Damage);
            Assert.AreEqual(90, hit.SettledDamage); Assert.AreEqual(origin, hit.Provenance.Origin);
        }

        [Test] public void DamageThenApplyUsesOldStacksAndOnlyCompletedApplierActionDecaysOne()
        {
            var catalog = Catalog(); var state = State();
            var cast = CombatEngine.Resolve(state, 0, new[] { Attack(45), CombatEffect.ApplyStatus(Status("cast/1", 3)) }, statusCatalog: catalog);
            Assert.IsTrue(cast.Accepted); Assert.AreEqual(45, cast.Events.Single().DamageToHealth);
            var complete = CombatEngine.ResolveTiming(cast.State, cast.State.Revision, Action(Enemy), statusCatalog: catalog);
            Assert.IsTrue(complete.Accepted); Assert.AreEqual(2, complete.State.Statuses.Single().Stacks); Assert.IsEmpty(complete.Events);
            var normal = CombatEngine.Resolve(complete.State, complete.State.Revision, new[] { Attack() }, statusCatalog: catalog);
            Assert.AreEqual(180, normal.Events.Single().DamageToHealth);
            var next = CombatEngine.ResolveTiming(normal.State, normal.State.Revision, Action(Enemy, "action/2"), statusCatalog: catalog);
            Assert.AreEqual(1, next.State.Statuses.Single().Stacks);
            var secondCast = CombatEngine.Resolve(next.State, next.State.Revision,
                new[] { Attack(45), CombatEffect.ApplyStatus(Status("cast/2", 3)) }, statusCatalog: catalog);
            Assert.AreEqual(67, secondCast.Events.Single().DamageToHealth);
            var last = CombatEngine.ResolveTiming(secondCast.State, secondCast.State.Revision, Action(Enemy, "action/3"), statusCatalog: catalog);
            Assert.AreEqual(3, last.State.Statuses.Single().Stacks);
        }

        [Test] public void DecayChangesOnlyItsAppliersInstanceWithoutDamageOrReactionFacts()
        {
            var catalog = Catalog(); var state = State(new[] { Status("a", 3), Status("b", 4, Other) });
            var result = CombatEngine.ResolveTiming(state, 0, Action(Enemy), statusCatalog: catalog, reactionProvider: new FailIfReacted());
            Assert.IsTrue(result.Accepted, result.Error.ToString()); Assert.IsEmpty(result.Events);
            Assert.AreEqual(2, result.State.Statuses.Single(s => s.Id.Value == "a").Stacks);
            Assert.AreEqual(4, result.State.Statuses.Single(s => s.Id.Value == "b").Stacks);
            var change = result.RuleEvents.OfType<CombatStatusChangedEvent>().Single();
            Assert.AreEqual(CombatStatusChangeReason.Consumed, change.Reason); Assert.IsNull(change.Effect);
            Assert.AreEqual(2, result.RuleEvents.Count); Assert.AreEqual(3, state.Statuses.First().Stacks);
            var playerAction = CombatEngine.ResolveTiming(result.State, result.State.Revision, Action(Player, "action/2"), statusCatalog: catalog);
            Assert.IsTrue(playerAction.Accepted); Assert.IsEmpty(playerAction.RuleEvents.OfType<CombatStatusChangedEvent>());
        }

        [Test] public void LastLayerDisappearsAndCleanseAndReviveStillRemoveBleeding()
        {
            var catalog = Catalog(); var state = State(new[] { Status("a", 1) });
            var expired = CombatEngine.ResolveTiming(state, 0, Action(Enemy), statusCatalog: catalog);
            Assert.IsTrue(expired.Accepted); Assert.IsEmpty(expired.State.Statuses); Assert.IsEmpty(expired.Events);
            Assert.IsNull(expired.RuleEvents.OfType<CombatStatusChangedEvent>().Single().StatusAfter);
            var cleansed = CombatEngine.Resolve(state, 0, new[] { CombatEffect.Cleanse(Player, Player) }, statusCatalog: catalog);
            Assert.IsTrue(cleansed.Accepted); Assert.IsEmpty(cleansed.State.Statuses);
            Assert.AreEqual(CombatStatusChangeReason.Cleansed, cleansed.RuleEvents.OfType<CombatStatusChangedEvent>().Single().Reason);
            var dead = CombatEngine.Resolve(state, 0, new[] { Attack(1000) }, statusCatalog: catalog);
            var revived = CombatEngine.Resolve(dead.State, dead.State.Revision,
                new[] { new CombatEffect(CombatEffectKind.Revive, Ally, Player, 100) }, statusCatalog: catalog);
            Assert.IsTrue(revived.Accepted); Assert.IsEmpty(revived.State.Statuses); Assert.AreEqual(100, revived.State.Get(Player).Health);
        }

        [Test] public void ReapplicationMergesOnlyTheSameApplier()
        {
            var result = CombatEngine.Resolve(State(new[] { Status("a", 2) }), 0,
                new[] { CombatEffect.ApplyStatus(Status("b", 3, Other)), CombatEffect.ApplyStatus(Status("c", 3)) }, statusCatalog: Catalog());
            Assert.IsTrue(result.Accepted, result.Error.ToString()); Assert.AreEqual(2, result.State.Statuses.Count);
            Assert.AreEqual(5, result.State.Statuses.Single(s => s.Source.Id == Enemy.Value).Stacks);
            Assert.AreEqual(3, result.State.Statuses.Single(s => s.Source.Id == Other.Value).Stacks);
        }

        [Test] public void ExistingPeriodicDamageKeepsItsOriginalDamageAndDecay()
        {
            var poison = new CombatStatusDefinition(new StatusDefinitionId("poison"), StatusTags.Negative,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.TurnStarted, 1, stackDecay: 1) }, StatusLifetimeSpec.UntilRemoved());
            var state = State(new[] { Status("poison", 3, definition: "poison"), Status("bleeding", 2) });
            var result = CombatEngine.ResolveTiming(state, 0, CombatTimingSignal.TurnStarted("turn/1", new[] { Player }),
                statusCatalog: Catalog(Bleeding(), poison));
            Assert.IsTrue(result.Accepted, result.Error.ToString()); Assert.AreEqual(3, result.Events.Single().DamageToHealth);
            Assert.AreEqual(2, result.State.Statuses.Single(s => s.Id.Value == "poison").Stacks);
            Assert.AreEqual(2, result.State.Statuses.Single(s => s.Id.Value == "bleeding").Stacks);
        }

        [Test] public void DefaultSpecsAreExplicitAndInvalidParametersAreRejected()
        {
            Assert.AreEqual(0.5m, new IncomingAttackDamagePerStackSpec().Rate);
            var decay = new StackDecaySpec(); Assert.AreEqual(CombatTimingKind.ActionCompleted, decay.Timing);
            Assert.AreEqual(PeriodicTimingSubject.Applier, decay.TimingSubject); Assert.AreEqual(1, decay.Stacks);
            Assert.Throws<ArgumentOutOfRangeException>(() => new IncomingAttackDamagePerStackSpec(-0.1m));
            Assert.Throws<ArgumentException>(() => new StackDecaySpec(CombatTimingKind.BattleStarted));
            Assert.Throws<ArgumentException>(() => new StackDecaySpec(timingSubject: (PeriodicTimingSubject)0));
            Assert.Throws<ArgumentException>(() => new StackDecaySpec(stacks: 0));
            Assert.Throws<ArgumentException>(() => new CombatStatusDefinition(new StatusDefinitionId("ambiguous"), StatusTags.None,
                new[] { Decay() }, StatusLifetimeSpec.ForTriggers(2)));
        }

        [Test] public void HolderClockAndConfiguredDecayAreIndependentFromTheApplier()
        {
            var definition = new CombatStatusDefinition(new StatusDefinitionId("elite-bleeding"), StatusTags.Negative,
                new[] { Decay(CombatTimingKind.TurnStarted, PeriodicTimingSubject.Holder, 2) }, StatusLifetimeSpec.UntilRemoved());
            var result = CombatEngine.ResolveTiming(State(new[] { Status("a", 1) }), 0,
                CombatTimingSignal.TurnStarted("turn/1", new[] { Player }), statusCatalog: Catalog(definition));
            Assert.IsTrue(result.Accepted); Assert.IsEmpty(result.Events); Assert.IsEmpty(result.State.Statuses);
            Assert.AreEqual(1, result.RuleEvents.OfType<CombatStatusChangedEvent>().Count());
        }

        [Test] public void ZeroRateLeavesExistingIncomingReportUnchanged()
        {
            var result = CombatEngine.Resolve(State(new[] { Status("a", 3) }), 0, new[] { Attack() },
                new ReduceIncoming(), Catalog(Bleeding(0m)));
            Assert.IsTrue(result.Accepted); var hit = result.Events.Single(); Assert.AreEqual(80, hit.SettledDamage);
            Assert.AreEqual("test.reduce.1", hit.IncomingModification.RuleVersion);
            Assert.AreEqual(1, hit.IncomingModification.Triggers.Count);
        }

        [Test] public void OverflowRejectsTheWholeAttackWithoutChangingInputState()
        {
            var state = State(new[] { Status("a", 3) });
            var result = CombatEngine.Resolve(state, 0, new[] { Attack(long.MaxValue) }, statusCatalog: Catalog());
            Assert.AreEqual(CombatError.Overflow, result.Error); Assert.AreSame(state, result.State);
            Assert.IsEmpty(result.Events); Assert.IsEmpty(result.RuleEvents); Assert.AreEqual(1000, state.Get(Player).Health);
        }

        [Test] public void LifeStealUsesActualAmplifiedHealthDamageAfterTheSingleShieldSettlement()
        {
            var leech = new CombatStatusDefinition(new StatusDefinitionId("leech"), StatusTags.None,
                new StatusBehaviorSpec[] { new LifeStealSpec(0.2m) }, StatusLifetimeSpec.UntilRemoved());
            var result = CombatEngine.Resolve(State(new[] { Status("a", 2), Status("leech", 1, target: Enemy, definition: "leech") },
                block: 130, enemyHealth: 500), 0, new[] { Attack() }, statusCatalog: Catalog(Bleeding(), leech));
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            var hit = result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Damage);
            Assert.AreEqual(180, hit.SettledDamage); Assert.AreEqual(130, hit.BlockAbsorbed); Assert.AreEqual(50, hit.DamageToHealth);
            var heal = result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal);
            Assert.AreEqual(10, heal.HealthRestored); Assert.AreEqual(510, result.State.Get(Enemy).Health);
            Assert.AreEqual(CombatEffectOrigin.Lifesteal, heal.Provenance.Origin); Assert.AreEqual(2, result.Events.Count);
        }

        private sealed class ReduceIncoming : IIncomingDamageModifier
        {
            public DamageModification Modify(DamageModifierContext context) => new DamageModification("test.reduce.1", "reduce", context.Amount,
                Math.Max(0, context.Amount - 10), new[] { new DamageTriggerReason("test.reduce", "-10") });
        }
        private sealed class DerivedAttack : ICombatReactionProvider
        {
            private readonly CombatEffectOrigin origin;
            public DerivedAttack(CombatEffectOrigin origin) { this.origin = origin; }
            public IEnumerable<CombatReaction> React(CombatReactionContext context) =>
                new[] { new CombatReaction(Attack(), "test.derived", Enemy, origin) };
        }
        private sealed class FailIfReacted : ICombatReactionProvider
        {
            public IEnumerable<CombatReaction> React(CombatReactionContext context)
            { Assert.Fail("Stack decay must not synthesize a settled effect."); return Array.Empty<CombatReaction>(); }
        }
    }
}
