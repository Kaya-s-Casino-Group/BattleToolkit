using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CombatStatusReactionTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), Enemy = new ParticipantId("enemy"),
            Other = new ParticipantId("other"), Ally = new ParticipantId("ally");
        private static CombatState Create(IEnumerable<StatusInstance> statuses = null, int playerHealth = 20,
            int enemyHealth = 30, int enemyBlock = 0, int otherHealth = 30, int allyHealth = 30) => new CombatState("status-reactions", 0, new[] {
                new CombatantState(Player, "hero", "players", 30, playerHealth),
                new CombatantState(Enemy, "enemy", "enemies", 30, enemyHealth, enemyBlock),
                new CombatantState(Other, "other", "enemies", 30, otherHealth),
                new CombatantState(Ally, "ally", "players", 30, allyHealth)
            }, statuses);
        private static CombatStatusDefinition Definition(string id, StatusBehaviorSpec behavior) =>
            new CombatStatusDefinition(new StatusDefinitionId(id), StatusTags.None, new[] { behavior }, StatusLifetimeSpec.UntilRemoved());
        private static CombatStatusCatalog Catalog(params CombatStatusDefinition[] definitions) => new CombatStatusCatalog("status-reactions.1", definitions);
        private static StatusInstance Status(string id, string definition, ParticipantId owner, int stacks = 1, int? count = null) =>
            new StatusInstance(new StatusInstanceId(id), new StatusDefinitionId(definition), EntityRef.Participant(Ally), EntityRef.Participant(owner),
                stacks, count.HasValue ? StatusDuration.ForTriggers(count.Value) : StatusDuration.UntilRemoved());
        private static CombatEffect Damage(long amount) => new CombatEffect(CombatEffectKind.Damage, Player, Enemy, amount);
        private static CombatEffect Heal(long amount) => new CombatEffect(CombatEffectKind.Heal, Player, Player, amount);
        private static CombatEvent[] Derived(CombatTransition result, CombatEffectOrigin origin) =>
            result.Events.Where(item => item.Provenance.Origin == origin).ToArray();
        private static CombatStatusChangedEvent[] Consumed(CombatTransition result) =>
            result.RuleEvents.OfType<CombatStatusChangedEvent>().Where(item => item.Reason == CombatStatusChangeReason.Consumed).ToArray();
        private sealed class Provider : ICombatReactionProvider
        {
            private readonly Func<CombatReactionContext, IEnumerable<CombatReaction>> react;
            public readonly List<CombatReactionContext> Calls = new List<CombatReactionContext>();
            public Provider(Func<CombatReactionContext, IEnumerable<CombatReaction>> react) { this.react = react; }
            public IEnumerable<CombatReaction> React(CombatReactionContext context) { Calls.Add(context); return react(context); }
        }
        private static void Rejected(CombatState state, CombatTransition result, CombatError error)
        {
            Assert.AreEqual(error, result.Error); Assert.AreSame(state, result.State); Assert.AreEqual(0, state.Revision);
            Assert.IsEmpty(result.RuleEvents); Assert.IsEmpty(result.Events);
        }

        [Test] public void ReflectUsesActualBlockAbsorbedAndFloorsOnlyAfterStackScaling()
        {
            var status = Status("reflect-instance", "reflect", Enemy, stacks: 3);
            var state = Create(new[] { status }, enemyBlock: 5);
            var result = CombatEngine.Resolve(state, 0, new[] { Damage(8) },
                statusCatalog: Catalog(Definition("reflect", new ReflectBlockedDamageSpec(0.5m))));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(5, result.Events[0].BlockAbsorbed); Assert.AreEqual(3, result.Events[0].DamageToHealth);
            var reflected = Derived(result, CombatEffectOrigin.Reflection).Single();
            Assert.AreEqual(7, reflected.Effect.Amount); Assert.AreEqual(Enemy, reflected.Effect.Source); Assert.AreEqual(Player, reflected.Effect.Target);
            Assert.AreEqual(13, result.State.Get(Player).Health); Assert.AreEqual(27, result.State.Get(Enemy).Health);
            Assert.AreEqual(status.Id, reflected.Provenance.TriggerStatusId); Assert.AreEqual(status.Id.Value, reflected.Provenance.SourceInstanceId);
            Assert.AreEqual("status-reactions.1/reflect/ReflectBlockedDamageSpec", reflected.Provenance.RuleId);
            Assert.IsFalse(reflected.Provenance.CanTriggerReactions); Assert.IsEmpty(Consumed(result));
            Assert.AreEqual(5, state.Get(Enemy).Block);
        }

        [Test] public void LifeStealUsesHealthDamageExcludesOverkillAndBelongsToTheAttackerNotTheGrantor()
        {
            var status = Status("leech-instance", "leech", Player, stacks: 3);
            var state = Create(new[] { status }, playerHealth: 10, enemyHealth: 3, enemyBlock: 4, allyHealth: 0);
            var result = CombatEngine.Resolve(state, 0, new[] { Damage(100) },
                statusCatalog: Catalog(Definition("leech", new LifeStealSpec(0.5m))));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(3, result.Events[0].DamageToHealth); Assert.AreEqual(93, result.Events[0].Overkill);
            var healed = Derived(result, CombatEffectOrigin.Lifesteal).Single();
            Assert.AreEqual(4, healed.Effect.Amount); Assert.AreEqual(4, healed.HealthRestored); Assert.AreEqual(14, result.State.Get(Player).Health);
            Assert.AreEqual(Player, healed.Effect.Source); Assert.AreEqual(Player, healed.Effect.Target); Assert.AreEqual(Player, healed.Provenance.Owner);
            Assert.IsTrue(healed.Provenance.CanTriggerReactions); Assert.AreEqual(0, result.State.Get(Ally).Health);
            Assert.AreEqual(Ally.Value, result.State.Statuses.Single().Source.Id);
        }

        [Test] public void FullyBlockedDamageDoesNotHealOrConsumeLifeSteal()
        {
            var status = Status("leech-charge", "leech", Player, count: 1);
            var state = Create(new[] { status }, playerHealth: 10, enemyBlock: 10);
            var result = CombatEngine.Resolve(state, 0, new[] { Damage(5) }, statusCatalog: Catalog(Definition("leech", new LifeStealSpec(1m))));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(10, result.State.Get(Player).Health); Assert.AreEqual(1, result.Events.Count);
            Assert.AreEqual(0, result.Events[0].DamageToHealth); Assert.AreSame(status, result.State.Statuses.Single()); Assert.IsEmpty(Consumed(result));
        }

        [Test] public void OverhealUsesOnlyUnrestoredHealingAndIsOwnedByTheRecipient()
        {
            var status = Status("overflow-instance", "overflow", Player, stacks: 3);
            var state = Create(new[] { status }, playerHealth: 28);
            var result = CombatEngine.Resolve(state, 0, new[] { new CombatEffect(CombatEffectKind.Heal, Ally, Player, 5) },
                statusCatalog: Catalog(Definition("overflow", new OverhealDamageSpec(0.5m))));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(2, result.Events[0].HealthRestored); Assert.AreEqual(3, result.Events[0].Overheal);
            var overflow = Derived(result, CombatEffectOrigin.Overheal).Single();
            Assert.AreEqual(4, overflow.Effect.Amount); Assert.AreEqual(Player, overflow.Effect.Source); Assert.AreEqual(Enemy, overflow.Effect.Target);
            Assert.AreEqual(Player, overflow.Provenance.Owner); Assert.IsFalse(overflow.Provenance.CanTriggerReactions);
            Assert.AreEqual(26, result.State.Get(Enemy).Health); Assert.AreEqual(30, result.State.Get(Player).Health);
        }

        [Test] public void OneStatusCanCombineLifeStealAndOverhealWhenItHasNoSharedTriggerBudget()
        {
            var definition = new CombatStatusDefinition(new StatusDefinitionId("combination"), StatusTags.None,
                new StatusBehaviorSpec[] { new LifeStealSpec(1m), new OverhealDamageSpec(2m) }, StatusLifetimeSpec.UntilRemoved());
            var status = Status("combination-instance", "combination", Player);
            var result = CombatEngine.Resolve(Create(new[] { status }, playerHealth: 29), 0, new[] { Damage(6) }, statusCatalog: Catalog(definition));
            Assert.IsTrue(result.Accepted);
            CollectionAssert.AreEqual(new[] { CombatEffectOrigin.Primary, CombatEffectOrigin.Lifesteal, CombatEffectOrigin.Overheal },
                result.Events.Select(item => item.Provenance.Origin));
            Assert.AreEqual(6, result.Events[1].Effect.Amount); Assert.AreEqual(1, result.Events[1].HealthRestored); Assert.AreEqual(5, result.Events[1].Overheal);
            Assert.AreEqual(10, result.Events[2].Effect.Amount); Assert.AreEqual(14, result.State.Get(Enemy).Health);
            Assert.AreEqual(30, result.State.Get(Player).Health); Assert.AreEqual(1, result.Events[2].Provenance.ParentSequenceIndex);
            Assert.AreEqual(status.Id, result.Events[1].Provenance.TriggerStatusId); Assert.AreEqual(status.Id, result.Events[2].Provenance.TriggerStatusId);
            Assert.IsEmpty(Consumed(result));
        }

        [Test] public void ReflectionRunsBeforeLifeStealAndAQueuedHealDoesNotReviveItsDefeatedOwner()
        {
            var leech = Status("leech-first-in-storage", "leech", Player, count: 1);
            var reflect = Status("reflect-second-in-storage", "reflect", Enemy, count: 1);
            var state = Create(new[] { leech, reflect }, playerHealth: 2, enemyBlock: 5);
            var result = CombatEngine.Resolve(state, 0, new[] { Damage(8), Damage(100) }, statusCatalog: Catalog(
                Definition("leech", new LifeStealSpec(1m)), Definition("reflect", new ReflectBlockedDamageSpec(1m))));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(0, result.State.Get(Player).Health); Assert.AreEqual(27, result.State.Get(Enemy).Health);
            CollectionAssert.AreEqual(new[] { CombatEffectOrigin.Primary, CombatEffectOrigin.Reflection }, result.Events.Select(item => item.Provenance.Origin));
            Assert.IsEmpty(Derived(result, CombatEffectOrigin.Lifesteal)); Assert.IsEmpty(result.State.Statuses);
            Assert.AreEqual(2, Consumed(result).Length); Assert.AreEqual(reflect.Id, Consumed(result)[0].StatusBefore.Id);
            Assert.AreEqual(leech.Id, Consumed(result)[1].StatusBefore.Id); Assert.AreEqual(2, state.Statuses.Count);
        }

        [TestCase(false)] [TestCase(true)]
        public void LethalHitsReflectOnlyWhenTheBehaviorExplicitlyAllowsDefeatedSources(bool allowDefeated)
        {
            var status = Status("last-reflect", "reflect", Enemy, count: 1);
            var policy = allowDefeated ? CombatReactionSourcePolicy.AllowDefeated : CombatReactionSourcePolicy.RequireLiving;
            var result = CombatEngine.Resolve(Create(new[] { status }, enemyHealth: 2, enemyBlock: 3), 0, new[] { Damage(6) },
                statusCatalog: Catalog(Definition("reflect", new ReflectBlockedDamageSpec(1m, sourcePolicy: policy))));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(0, result.State.Get(Enemy).Health);
            Assert.AreEqual(allowDefeated ? 17 : 20, result.State.Get(Player).Health);
            Assert.AreEqual(allowDefeated ? 1 : 0, Consumed(result).Length); Assert.AreEqual(allowDefeated ? 0 : 1, result.State.Statuses.Count);
            if (allowDefeated)
            {
                var reflected = Derived(result, CombatEffectOrigin.Reflection).Single();
                Assert.AreEqual(Enemy, reflected.Effect.Source); Assert.AreEqual(Enemy, reflected.Provenance.Owner);
                Assert.AreEqual(CombatReactionSourcePolicy.AllowDefeated, reflected.Provenance.SourcePolicy);
            }
        }

        [Test] public void AreaActivationConsumesOnceAndAllQueuedTargetsRemainAfterTheStatusIsRemoved()
        {
            var status = Status("one-area-charge", "overflow", Player, stacks: 3, count: 1);
            var catalog = Catalog(Definition("overflow", new OverhealDamageSpec(0.5m, CombatTargetSpec.Opponents(CombatTargetSelection.AllLiving))));
            var state = Create(new[] { status }, playerHealth: 30);
            var result = CombatEngine.Resolve(state, 0, new[] { Heal(4) }, statusCatalog: catalog);
            Assert.IsTrue(result.Accepted); Assert.IsEmpty(result.State.Statuses); Assert.AreEqual(1, Consumed(result).Length);
            var hits = Derived(result, CombatEffectOrigin.Overheal);
            CollectionAssert.AreEqual(new[] { Enemy, Other }, hits.Select(item => item.Effect.Target));
            Assert.IsTrue(hits.All(item => item.Effect.Amount == 6 && item.Provenance.TriggerStatusId == status.Id));
            Assert.AreEqual(24, result.State.Get(Enemy).Health); Assert.AreEqual(24, result.State.Get(Other).Health);
            Assert.IsNull(Consumed(result)[0].StatusAfter); Assert.AreEqual(0, Consumed(result)[0].Provenance.ParentSequenceIndex);
            Assert.AreSame(status, state.Statuses.Single());
        }

        [Test] public void TriggerCountIsPerActivationAndStopsLaterPrimaryEventsAtZero()
        {
            var status = Status("two-area-charges", "overflow", Player, stacks: 3, count: 2);
            var catalog = Catalog(Definition("overflow", new OverhealDamageSpec(0.5m, CombatTargetSpec.Opponents(CombatTargetSelection.AllLiving))));
            var result = CombatEngine.Resolve(Create(new[] { status }, playerHealth: 30), 0, new[] { Heal(4), Heal(2), Heal(10) }, statusCatalog: catalog);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(4, Derived(result, CombatEffectOrigin.Overheal).Length);
            Assert.AreEqual(21, result.State.Get(Enemy).Health); Assert.AreEqual(21, result.State.Get(Other).Health); Assert.IsEmpty(result.State.Statuses);
            var consumed = Consumed(result); Assert.AreEqual(2, consumed.Length);
            Assert.AreEqual(1, consumed[0].StatusAfter.Duration.RemainingTriggers); Assert.AreEqual(3, consumed[0].StatusAfter.Stacks);
            Assert.IsNull(consumed[1].StatusAfter);
        }

        [TestCase(0)] [TestCase(1)]
        public void ZeroRatioOrRoundedZeroDoesNotSpendACharge(int variant)
        {
            var status = Status("unused", "reflect", Enemy, count: 1);
            var result = CombatEngine.Resolve(Create(new[] { status }, enemyBlock: 5), 0, new[] { Damage(5) },
                statusCatalog: Catalog(Definition("reflect", new ReflectBlockedDamageSpec(variant == 0 ? 0m : 0.1m))));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(1, result.Events.Count); Assert.AreSame(status, result.State.Statuses.Single());
            Assert.IsEmpty(Consumed(result)); Assert.AreEqual(20, result.State.Get(Player).Health);
        }

        [Test] public void NoOverhealOrNoLivingTargetsDoesNotSpendAnOverflowCharge()
        {
            var status = Status("unused", "overflow", Player, count: 1);
            var catalog = Catalog(Definition("overflow", new OverhealDamageSpec(1m)));
            var ordinary = CombatEngine.Resolve(Create(new[] { status }), 0, new[] { Heal(2) }, statusCatalog: catalog);
            Assert.IsTrue(ordinary.Accepted); Assert.AreEqual(1, ordinary.Events.Count); Assert.IsEmpty(Consumed(ordinary));
            Assert.AreSame(status, ordinary.State.Statuses.Single());
            var noTargets = CombatEngine.Resolve(Create(new[] { status }, playerHealth: 30, enemyHealth: 0, otherHealth: 0), 0,
                new[] { Heal(2) }, statusCatalog: catalog);
            Assert.IsTrue(noTargets.Accepted); Assert.AreEqual(1, noTargets.Events.Count); Assert.IsEmpty(Consumed(noTargets));
            Assert.AreSame(status, noTargets.State.Statuses.Single());
        }

        [TestCase(CombatEffectOrigin.Triggered)] [TestCase(CombatEffectOrigin.Periodic)]
        [TestCase(CombatEffectOrigin.Reflection)] [TestCase(CombatEffectOrigin.Overheal)]
        public void NonPrimaryDamageDoesNotTriggerTheBuiltInDamageBehaviorsEvenWhenChainingIsEnabled(CombatEffectOrigin origin)
        {
            var reflect = Status("reflect", "reflect", Enemy, count: 1); var leech = Status("leech", "leech", Player, count: 1);
            var external = new Provider(context => context.Event.Provenance.Origin == CombatEffectOrigin.Primary
                ? new[] { new CombatReaction(Damage(8), "synthetic", Player, origin, canTriggerReactions: true) } : Array.Empty<CombatReaction>());
            var result = CombatEngine.Resolve(Create(new[] { reflect, leech }, playerHealth: 10, enemyBlock: 5), 0,
                new[] { new CombatEffect(CombatEffectKind.GainBlock, Player, Player, 0) }, statusCatalog: Catalog(
                    Definition("reflect", new ReflectBlockedDamageSpec(1m)), Definition("leech", new LifeStealSpec(1m))), reactionProvider: external);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(2, result.Events.Count); Assert.AreEqual(27, result.State.Get(Enemy).Health);
            Assert.AreEqual(10, result.State.Get(Player).Health); Assert.AreEqual(2, result.State.Statuses.Count); Assert.IsEmpty(Consumed(result));
        }

        [Test] public void OtherTriggeredHealingDoesNotEnterTheLifeStealOverhealWhitelist()
        {
            var status = Status("overflow", "overflow", Player, count: 1);
            var external = new Provider(context => context.Event.Provenance.Origin == CombatEffectOrigin.Primary
                ? new[] { new CombatReaction(Heal(5), "synthetic-heal", Player, canTriggerReactions: true) } : Array.Empty<CombatReaction>());
            var result = CombatEngine.Resolve(Create(new[] { status }, playerHealth: 29), 0,
                new[] { new CombatEffect(CombatEffectKind.GainBlock, Player, Player, 0) },
                statusCatalog: Catalog(Definition("overflow", new OverhealDamageSpec(1m))), reactionProvider: external);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(2, result.Events.Count); Assert.AreEqual(4, result.Events[1].Overheal);
            Assert.IsEmpty(Derived(result, CombatEffectOrigin.Overheal)); Assert.AreSame(status, result.State.Statuses.Single());
        }

        [Test] public void EliminationAndBlockMaintenanceDoNotTriggerDamageBehaviors()
        {
            var reflect = Status("reflect", "reflect", Enemy, count: 1); var leech = Status("leech", "leech", Player, count: 1);
            var result = CombatEngine.Resolve(Create(new[] { reflect, leech }, enemyBlock: 5), 0, new[] {
                new CombatEffect(CombatEffectKind.ClearBlock, Player, Enemy), CombatEffect.Eliminate(Player, Enemy)
            }, statusCatalog: Catalog(Definition("reflect", new ReflectBlockedDamageSpec(1m)), Definition("leech", new LifeStealSpec(1m))));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(2, result.Events.Count); Assert.AreEqual(20, result.State.Get(Player).Health);
            Assert.IsEmpty(Consumed(result)); Assert.AreEqual(2, result.State.Statuses.Count);
        }

        [Test] public void StatusStorageOrderControlsSameGroupReactionsIndependentlyOfCatalogOrder()
        {
            var first = Status("first-instance", "first", Player); var second = Status("second-instance", "second", Player);
            var catalog = Catalog(Definition("first", new LifeStealSpec(1m)), Definition("second", new LifeStealSpec(2m)));
            var result = CombatEngine.Resolve(Create(new[] { second, first }, playerHealth: 10), 0, new[] { Damage(2) }, statusCatalog: catalog);
            Assert.IsTrue(result.Accepted);
            var heals = Derived(result, CombatEffectOrigin.Lifesteal);
            CollectionAssert.AreEqual(new[] { second.Id.Value, first.Id.Value }, heals.Select(item => item.Provenance.SourceInstanceId));
            CollectionAssert.AreEqual(new long[] { 4, 2 }, heals.Select(item => item.Effect.Amount));
            Assert.AreEqual(16, result.State.Get(Player).Health);
        }

        [Test] public void BuiltInActivationConsumptionDoesNotCallTheExternalProviderForConsumptionFacts()
        {
            var status = Status("charge", "leech", Player, count: 1);
            var external = new Provider(context => Array.Empty<CombatReaction>());
            var result = CombatEngine.Resolve(Create(new[] { status }), 0, new[] { Damage(2) },
                statusCatalog: Catalog(Definition("leech", new LifeStealSpec(1m))), reactionProvider: external);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(1, Consumed(result).Length); Assert.AreEqual(2, external.Calls.Count);
            Assert.IsTrue(external.Calls.All(call => call.Event is CombatEvent));
            Assert.IsEmpty(result.State.Statuses); Assert.AreEqual(22, result.State.Get(Player).Health);
        }

        [Test] public void ExternalSourceInstanceTextCannotConsumeAStatus()
        {
            var status = Status("charge", "leech", Player, count: 1);
            var external = new Provider(context => new[] { new CombatReaction(
                new CombatEffect(CombatEffectKind.GainBlock, Player, Player, 2), "external", Player, sourceInstanceId: status.Id.Value) });
            var result = CombatEngine.Resolve(Create(new[] { status }), 0,
                new[] { new CombatEffect(CombatEffectKind.GainBlock, Player, Player, 1) },
                statusCatalog: Catalog(Definition("leech", new LifeStealSpec(1m))), reactionProvider: external);
            Assert.IsTrue(result.Accepted); Assert.AreSame(status, result.State.Statuses.Single()); Assert.IsEmpty(Consumed(result));
            Assert.AreEqual(3, result.State.Get(Player).Block); Assert.IsNull(result.Events[1].Provenance.TriggerStatusId);
        }

        [Test] public void NoCatalogLeavesTheSameStatusesPassive()
        {
            var status = Status("passive", "reflect", Enemy, count: 1);
            var result = CombatEngine.Resolve(Create(new[] { status }, enemyBlock: 5), 0, new[] { Damage(8) });
            Assert.IsTrue(result.Accepted); Assert.AreEqual(1, result.Events.Count); Assert.AreEqual(20, result.State.Get(Player).Health);
            Assert.AreSame(status, result.State.Statuses.Single()); Assert.IsEmpty(Consumed(result));
        }

        [Test] public void TriggerBudgetAndNumericFailuresRollbackDamageAndStatusConsumption()
        {
            var area = Status("area", "overflow", Player, count: 1);
            var state = Create(new[] { area }, playerHealth: 30);
            var result = CombatEngine.Resolve(state, 0, new[] { Heal(2) }, statusCatalog: Catalog(Definition("overflow",
                new OverhealDamageSpec(1m, CombatTargetSpec.Opponents(CombatTargetSelection.AllLiving)))), reactionLimits: new CombatReactionLimits(maxEffects: 1));
            Rejected(state, result, CombatError.ReactionLimitExceeded); Assert.AreSame(area, state.Statuses.Single());
            var reflect = Status("overflowing-number", "reflect", Enemy, count: 1);
            state = Create(new[] { reflect }, enemyBlock: 3);
            result = CombatEngine.Resolve(state, 0, new[] { Damage(5) }, statusCatalog: Catalog(Definition("reflect", new ReflectBlockedDamageSpec(decimal.MaxValue))));
            Rejected(state, result, CombatError.Overflow); Assert.AreSame(reflect, state.Statuses.Single()); Assert.AreEqual(3, state.Get(Enemy).Block);
        }

        private sealed class UnknownReactionBehavior : StatusReactionBehaviorSpec
        {
            public UnknownReactionBehavior() : base(1m, CombatTargetSpec.Self(), CombatReactionSourcePolicy.RequireLiving) { }
        }
        [Test] public void DefinitionsRejectUnsupportedBehaviorsNegativeRatiosAndSharedTriggerBudgets()
        {
            Assert.Catch<ArgumentException>(() => new ReflectBlockedDamageSpec(-1m));
            Assert.Catch<ArgumentException>(() => new LifeStealSpec(-1m));
            Assert.Catch<ArgumentException>(() => new OverhealDamageSpec(-1m));
            Assert.Catch<ArgumentException>(() => new ReflectBlockedDamageSpec(1m, sourcePolicy: (CombatReactionSourcePolicy)999));
            Assert.Catch<ArgumentException>(() => Definition("unsupported", new UnknownReactionBehavior()));
            Assert.Catch<ArgumentException>(() => new CombatStatusDefinition(new StatusDefinitionId("duplicate"), StatusTags.None,
                new StatusBehaviorSpec[] { new LifeStealSpec(1m), new LifeStealSpec(2m) }, StatusLifetimeSpec.UntilRemoved()));
            Assert.Catch<ArgumentException>(() => new CombatStatusDefinition(new StatusDefinitionId("shared"), StatusTags.None,
                new StatusBehaviorSpec[] { new LifeStealSpec(1m), new OverhealDamageSpec(1m) }, StatusLifetimeSpec.ForTriggers(1)));
            Assert.DoesNotThrow(() => Definition("zero", new ReflectBlockedDamageSpec(0m)));
        }

        [Test] public void TriggerCountPayloadCannotBypassTheMultipleBehaviorDefinitionRestriction()
        {
            var definition = new CombatStatusDefinition(new StatusDefinitionId("combined"), StatusTags.None,
                new StatusBehaviorSpec[] { new LifeStealSpec(1m), new OverhealDamageSpec(1m) }, StatusLifetimeSpec.UntilRemoved());
            var status = Status("invalid-count", "combined", Player, count: 1); var state = Create();
            var result = CombatEngine.Resolve(state, 0, new[] { Damage(2), CombatEffect.ApplyStatus(status) }, statusCatalog: Catalog(definition));
            Rejected(state, result, CombatError.InvalidEffect); Assert.IsEmpty(state.Statuses); Assert.AreEqual(30, state.Get(Enemy).Health);
        }

        [Test] public void TargetChoiceIsIndependentOfTheStatusBehaviorAndDoesNotSplitTheAmount()
        {
            var status = Status("shared-heal", "leech", Player);
            var result = CombatEngine.Resolve(Create(new[] { status }, playerHealth: 10, allyHealth: 12), 0, new[] { Damage(4) },
                statusCatalog: Catalog(Definition("leech", new LifeStealSpec(0.5m, CombatTargetSpec.Allies(CombatTargetSelection.AllLiving)))));
            Assert.IsTrue(result.Accepted);
            var heals = Derived(result, CombatEffectOrigin.Lifesteal);
            CollectionAssert.AreEqual(new[] { Player, Ally }, heals.Select(item => item.Effect.Target));
            Assert.IsTrue(heals.All(item => item.Effect.Amount == 2));
            Assert.AreEqual(12, result.State.Get(Player).Health); Assert.AreEqual(14, result.State.Get(Ally).Health);
        }
    }
}
