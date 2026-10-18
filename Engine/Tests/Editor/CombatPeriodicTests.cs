using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CombatPeriodicTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), Enemy = new ParticipantId("enemy"),
            Other = new ParticipantId("other"), Ally = new ParticipantId("ally");
        private const string Round = "periodic/round/0000000001";
        private static CombatState Create(IEnumerable<StatusInstance> statuses = null, int playerHealth = 20,
            int enemyHealth = 30, int enemyBlock = 0, int otherHealth = 30, int allyHealth = 30) => new CombatState("periodic", 0,
                new[] {
                    new CombatantState(Player, "hero", "players", 30, playerHealth),
                    new CombatantState(Enemy, "enemy", "enemies", 30, enemyHealth, enemyBlock),
                    new CombatantState(Other, "other", "enemies", 30, otherHealth),
                    new CombatantState(Ally, "ally", "players", 30, allyHealth)
                }, statuses);
        private static CombatStatusDefinition Definition(string id, CombatTimingKind timing = CombatTimingKind.ActionCompleted,
            long amount = 3, CombatTargetSpec target = null, StatusReapplyPolicy policy = StatusReapplyPolicy.KeepLonger) =>
            new CombatStatusDefinition(new StatusDefinitionId(id), StatusTags.None,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(timing, amount, target) }, StatusLifetimeSpec.ForTriggers(2), policy);
        private static CombatStatusCatalog Catalog(params CombatStatusDefinition[] definitions) => new CombatStatusCatalog("periodic.1", definitions);
        private static StatusInstance Status(string id, string definition, ParticipantId owner, int stacks = 1, int count = 2,
            ParticipantId? source = null, StatusDuration duration = null) => new StatusInstance(new StatusInstanceId(id),
                new StatusDefinitionId(definition), EntityRef.Participant(source ?? Player), EntityRef.Participant(owner), stacks,
                duration ?? StatusDuration.ForTriggers(count));
        private static CombatTimingSignal Action(ParticipantId actor, string boundary = "action/1") =>
            CombatTimingSignal.ActionCompleted(boundary, actor, "play");
        private static CombatTimingSignal Phase(params ParticipantId[] actors) =>
            CombatTimingSignal.PhaseEnding("phase/1", actors, "enemy");
        private static CombatStatusChangedEvent[] Consumed(CombatTransition transition) => transition.RuleEvents
            .OfType<CombatStatusChangedEvent>().Where(item => item.Reason == CombatStatusChangeReason.Consumed).ToArray();
        private static void Rejected(CombatState state, CombatTransition result, CombatError error)
        {
            Assert.AreEqual(error, result.Error); Assert.AreSame(state, result.State);
            Assert.IsEmpty(result.Events); Assert.IsEmpty(result.RuleEvents); Assert.AreEqual(0, state.Revision);
        }
        private sealed class ReduceIncoming : IIncomingDamageModifier
        {
            public readonly List<DamageModifierContext> Calls = new List<DamageModifierContext>();
            private readonly long reduction;
            public ReduceIncoming(long reduction) { this.reduction = reduction; }
            public DamageModification Modify(DamageModifierContext context)
            {
                Calls.Add(context);
                return new DamageModification("periodic.incoming.1", "reduce periodic incoming damage", context.Amount,
                    Math.Max(0, context.Amount - reduction));
            }
        }
        private sealed class InvalidIncoming : IIncomingDamageModifier
        {
            public DamageModification Modify(DamageModifierContext context) => null;
        }

        [Test] public void ClockWithoutEffectsIsARealFactAndAdvancesTheRevision()
        {
            var state = Create(); var signal = Action(Player);
            var result = CombatEngine.ResolveTiming(state, 0, signal);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(1, result.State.Revision); Assert.AreEqual(1, result.RuleEvents.Count);
            var clock = result.RuleEvents.OfType<CombatTimingEvent>().Single();
            Assert.AreSame(signal, clock.Signal); Assert.IsNull(clock.Effect); Assert.IsNull(clock.Provenance);
            Assert.AreEqual(0, clock.SequenceIndex); Assert.IsEmpty(result.Events); Assert.AreEqual(0, state.Revision);
        }

        [Test] public void BleedingTriggersOnlyOnItsHoldersActionAndNotOnPhaseEnding()
        {
            var status = Status("bleeding", "arbitrary", Enemy);
            var state = Create(new[] { status }); var catalog = Catalog(Definition("arbitrary"));
            var otherAction = CombatEngine.ResolveTiming(state, 0, Action(Player), statusCatalog: catalog);
            var phase = CombatEngine.ResolveTiming(state, 0, Phase(Enemy), statusCatalog: catalog);
            foreach (var result in new[] { otherAction, phase })
            { Assert.IsTrue(result.Accepted); Assert.IsEmpty(result.Events); Assert.AreSame(status, result.State.Statuses.Single()); }
            var own = CombatEngine.ResolveTiming(state, 0, Action(Enemy), statusCatalog: catalog);
            Assert.IsTrue(own.Accepted); Assert.AreEqual(27, own.State.Get(Enemy).Health);
            Assert.AreEqual(1, own.State.Statuses.Single().Duration.RemainingTriggers);
        }

        [Test] public void PoisonTriggersOnlyForThePhaseParticipantsAndDoesNotTickOnActions()
        {
            var a = Status("enemy-poison", "poison", Enemy); var b = Status("player-poison", "poison", Player, source: Enemy);
            var state = Create(new[] { a, b }); var catalog = Catalog(Definition("poison", CombatTimingKind.PhaseEnding));
            var action = CombatEngine.ResolveTiming(state, 0, Action(Enemy), statusCatalog: catalog);
            Assert.IsTrue(action.Accepted); Assert.IsEmpty(action.Events); Assert.IsEmpty(Consumed(action));
            var phase = CombatEngine.ResolveTiming(state, 0, Phase(Enemy, Other), statusCatalog: catalog);
            Assert.IsTrue(phase.Accepted); Assert.AreEqual(27, phase.State.Get(Enemy).Health); Assert.AreEqual(20, phase.State.Get(Player).Health);
            Assert.AreEqual(1, Consumed(phase).Length); Assert.AreSame(b, phase.State.Statuses.Single(item => item.Id == b.Id));
        }

        [Test] public void PeriodicDamageUsesStacksIncomingModificationBlockAndActualDamageFacts()
        {
            var status = Status("bleeding", "bleed", Enemy, stacks: 3); var modifier = new ReduceIncoming(2);
            var state = Create(new[] { status }, enemyBlock: 4);
            var result = CombatEngine.ResolveTiming(state, 0, Action(Enemy), modifier, Catalog(Definition("bleed")));
            Assert.IsTrue(result.Accepted); var hit = result.Events.Single();
            Assert.AreEqual(9, hit.Effect.Amount); Assert.AreEqual(7, hit.SettledDamage); Assert.AreEqual(4, hit.BlockAbsorbed);
            Assert.AreEqual(3, hit.DamageToHealth); Assert.AreEqual(27, result.State.Get(Enemy).Health); Assert.AreEqual(0, result.State.Get(Enemy).Block);
            Assert.AreEqual(1, modifier.Calls.Count); Assert.IsNull(hit.Effect.AttackModification);
            Assert.AreEqual(CombatEffectOrigin.Periodic, hit.Provenance.Origin); Assert.IsFalse(hit.Provenance.CanTriggerReactions);
            Assert.AreEqual(status.Id, hit.Provenance.TriggerStatusId); Assert.AreEqual(status.Id.Value, hit.Provenance.SourceInstanceId);
            Assert.AreEqual(0, hit.Provenance.ParentSequenceIndex); Assert.AreEqual(1, hit.Provenance.ParentRevision);
            Assert.AreEqual(1, hit.Provenance.Depth); Assert.AreEqual(4, state.Get(Enemy).Block);
        }

        [Test] public void DefeatedOriginalSourceStillDealsPeriodicDamageWithItsAttribution()
        {
            var status = Status("persistent", "poison", Enemy, source: Ally); var modifier = new ReduceIncoming(1);
            var result = CombatEngine.ResolveTiming(Create(new[] { status }, allyHealth: 0), 0, Phase(Enemy), modifier,
                Catalog(Definition("poison", CombatTimingKind.PhaseEnding, amount: 5)));
            Assert.IsTrue(result.Accepted); var hit = result.Events.Single();
            Assert.AreEqual(Ally, hit.Effect.Source); Assert.AreEqual(Enemy, hit.Effect.Target); Assert.AreEqual(26, result.State.Get(Enemy).Health);
            Assert.AreEqual(CombatReactionSourcePolicy.AllowDefeated, hit.Provenance.SourcePolicy);
            Assert.AreEqual(Ally, modifier.Calls.Single().Source); Assert.AreEqual(0, result.State.Get(Ally).Health);
        }

        [Test] public void TargetSelectionUsesTheHolderRatherThanTheOriginalSource()
        {
            var status = Status("aura", "aura", Enemy, source: Player);
            var result = CombatEngine.ResolveTiming(Create(new[] { status }), 0, Action(Enemy), statusCatalog: Catalog(
                Definition("aura", target: CombatTargetSpec.Opponents(CombatTargetSelection.AllLiving))));
            Assert.IsTrue(result.Accepted);
            CollectionAssert.AreEqual(new[] { Player, Ally }, result.Events.Select(item => item.Effect.Target));
            Assert.IsTrue(result.Events.All(item => item.Effect.Source == Player));
            Assert.AreEqual(17, result.State.Get(Player).Health); Assert.AreEqual(27, result.State.Get(Ally).Health);
            Assert.AreEqual(30, result.State.Get(Enemy).Health); Assert.AreEqual(1, Consumed(result).Length);
        }

        [Test] public void LastTickDealsDamageBeforeRemovingTheStatusAndNeverDecaysStacks()
        {
            var status = Status("two-ticks", "bleed", Enemy, stacks: 3, count: 2); var catalog = Catalog(Definition("bleed", amount: 2));
            var first = CombatEngine.ResolveTiming(Create(new[] { status }), 0, Action(Enemy), statusCatalog: catalog);
            Assert.IsTrue(first.Accepted); Assert.AreEqual(24, first.State.Get(Enemy).Health);
            Assert.AreEqual(3, first.State.Statuses.Single().Stacks); Assert.AreEqual(1, first.State.Statuses.Single().Duration.RemainingTriggers);
            var second = CombatEngine.ResolveTiming(first.State, first.State.Revision, Action(Enemy, "action/2"), statusCatalog: catalog);
            Assert.IsTrue(second.Accepted); Assert.AreEqual(18, second.State.Get(Enemy).Health); Assert.IsEmpty(second.State.Statuses);
            Assert.IsNull(Consumed(second).Single().StatusAfter);
            Assert.Less(second.Events.Single().SequenceIndex, Consumed(second).Single().SequenceIndex);
            var third = CombatEngine.ResolveTiming(second.State, second.State.Revision, Action(Enemy, "action/3"), statusCatalog: catalog);
            Assert.IsTrue(third.Accepted); Assert.IsEmpty(third.Events); Assert.AreEqual(18, third.State.Get(Enemy).Health);
            Assert.AreEqual(2, status.Duration.RemainingTriggers); Assert.AreEqual(3, status.Stacks);
        }

        [TestCase(0, 0, 0)] [TestCase(4, 10, 0)] [TestCase(4, 0, 10)]
        public void ValidTicksSpendOneChargeEvenWithNoHealthDamage(int damage, int block, int reduction)
        {
            var status = Status("zero-health-loss", "bleed", Enemy, count: 1);
            var result = CombatEngine.ResolveTiming(Create(new[] { status }, enemyBlock: block), 0, Action(Enemy),
                new ReduceIncoming(reduction), Catalog(Definition("bleed", amount: damage)));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(30, result.State.Get(Enemy).Health);
            Assert.IsEmpty(result.State.Statuses); Assert.AreEqual(1, Consumed(result).Length);
        }

        [Test] public void AreaTickDealsFullDamageToEachTargetThenSpendsOnlyOneLastCharge()
        {
            var status = Status("one-area-tick", "aura", Player, stacks: 2, count: 1);
            var result = CombatEngine.ResolveTiming(Create(new[] { status }), 0, Action(Player), statusCatalog: Catalog(
                Definition("aura", target: CombatTargetSpec.Opponents(CombatTargetSelection.AllLiving))));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(2, result.Events.Count); Assert.IsTrue(result.Events.All(item => item.Effect.Amount == 6));
            Assert.AreEqual(24, result.State.Get(Enemy).Health); Assert.AreEqual(24, result.State.Get(Other).Health);
            Assert.AreEqual(1, Consumed(result).Length); Assert.IsEmpty(result.State.Statuses);
            Assert.Less(result.Events.Last().SequenceIndex, Consumed(result).Single().SequenceIndex);
        }

        [TestCase(false)] [TestCase(true)]
        public void DeadHolderOrNoTargetsDoesNotSpendACharge(bool deadHolder)
        {
            var status = Status("unused", "aura", Player, count: 1);
            var state = Create(new[] { status }, playerHealth: deadHolder ? 0 : 20, enemyHealth: 0, otherHealth: 0);
            var result = CombatEngine.ResolveTiming(state, 0, Action(Player), statusCatalog: Catalog(Definition("aura",
                target: deadHolder ? CombatTargetSpec.Allies(CombatTargetSelection.AllLiving) : CombatTargetSpec.Opponents())));
            Assert.IsTrue(result.Accepted); Assert.IsEmpty(result.Events); Assert.IsEmpty(Consumed(result));
            Assert.AreSame(status, result.State.Statuses.Single()); Assert.AreEqual(30, result.State.Get(Ally).Health);
        }

        [Test] public void AreaTickFinishesAndConsumesOnceWhenItsFirstHitKillsTheHolderAndSource()
        {
            var status = Status("self-harming-area", "aura", Player, count: 1, source: Player);
            var result = CombatEngine.ResolveTiming(Create(new[] { status }, playerHealth: 2), 0, Action(Player), statusCatalog: Catalog(
                Definition("aura", target: CombatTargetSpec.Allies(CombatTargetSelection.AllLiving))));
            Assert.IsTrue(result.Accepted); CollectionAssert.AreEqual(new[] { Player, Ally }, result.Events.Select(item => item.Effect.Target));
            Assert.IsTrue(result.Events[0].Died); Assert.AreEqual(0, result.State.Get(Player).Health);
            Assert.AreEqual(27, result.State.Get(Ally).Health); Assert.IsEmpty(result.State.Statuses);
            Assert.AreEqual(1, Consumed(result).Length); Assert.Less(result.Events.Last().SequenceIndex, Consumed(result).Single().SequenceIndex);
        }

        [Test] public void EarlierPeriodicDeathSkipsALaterStatusHeldByTheVictim()
        {
            var first = Status("first", "first", Player, count: 1);
            var later = Status("later", "later", Enemy, count: 1, source: Other);
            var result = CombatEngine.ResolveTiming(Create(new[] { first, later }, enemyHealth: 2), 0, Phase(Enemy, Player),
                statusCatalog: Catalog(Definition("later", CombatTimingKind.PhaseEnding, target: CombatTargetSpec.Opponents()),
                    Definition("first", CombatTimingKind.PhaseEnding, target: CombatTargetSpec.Opponents())));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(1, result.Events.Count); Assert.IsTrue(result.Events[0].Died);
            Assert.AreEqual(CombatDeathCause.Damage, result.Events[0].DeathCause); Assert.AreEqual(0, result.State.Get(Enemy).Health);
            Assert.AreEqual(20, result.State.Get(Player).Health); Assert.AreEqual(1, Consumed(result).Length);
            Assert.AreSame(later, result.State.Statuses.Single());
        }

        [Test] public void LaterStatusSelectsItsTargetFromTheCurrentPostDamageState()
        {
            var a = Status("first", "first", Player, count: 1); var b = Status("second", "second", Player, count: 1);
            var result = CombatEngine.ResolveTiming(Create(new[] { a, b }, enemyHealth: 2), 0, Action(Player), statusCatalog: Catalog(
                Definition("second", amount: 4, target: CombatTargetSpec.Opponents()),
                Definition("first", amount: 3, target: CombatTargetSpec.Opponents())));
            Assert.IsTrue(result.Accepted); CollectionAssert.AreEqual(new[] { Enemy, Other }, result.Events.Select(item => item.Effect.Target));
            Assert.AreEqual(0, result.State.Get(Enemy).Health); Assert.AreEqual(26, result.State.Get(Other).Health);
            CollectionAssert.AreEqual(new[] { a.Id, b.Id }, result.Events.Select(item => item.Provenance.TriggerStatusId.Value));
            Assert.IsEmpty(result.State.Statuses);
        }

        [Test] public void PeriodicDamageNeverTriggersReflectionOrLifeSteal()
        {
            var statuses = new[] { Status("poison", "poison", Enemy, count: 1), Status("reflect", "reflect", Enemy, count: 1),
                Status("leech", "leech", Player, count: 1) };
            var catalog = Catalog(Definition("poison", CombatTimingKind.PhaseEnding, amount: 8),
                new CombatStatusDefinition(new StatusDefinitionId("reflect"), StatusTags.None,
                    new StatusBehaviorSpec[] { new ReflectBlockedDamageSpec(1m) }, StatusLifetimeSpec.ForTriggers(1)),
                new CombatStatusDefinition(new StatusDefinitionId("leech"), StatusTags.None,
                    new StatusBehaviorSpec[] { new LifeStealSpec(1m) }, StatusLifetimeSpec.ForTriggers(1)));
            var result = CombatEngine.ResolveTiming(Create(statuses, enemyBlock: 5), 0, Phase(Enemy), statusCatalog: catalog);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(1, result.Events.Count); Assert.AreEqual(27, result.State.Get(Enemy).Health);
            Assert.AreEqual(20, result.State.Get(Player).Health); Assert.AreEqual(1, Consumed(result).Length); Assert.AreEqual(2, result.State.Statuses.Count);
        }

        [Test] public void OrdinaryDamageDoesNotPretendToCompleteAnAction()
        {
            var status = Status("bleeding", "bleed", Enemy, count: 1);
            var result = CombatEngine.Resolve(Create(new[] { status }), 0,
                new[] { new CombatEffect(CombatEffectKind.Damage, Player, Enemy, 2) }, statusCatalog: Catalog(Definition("bleed")));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(28, result.State.Get(Enemy).Health); Assert.AreEqual(1, result.Events.Count);
            Assert.AreSame(status, result.State.Statuses.Single()); Assert.IsEmpty(Consumed(result));
        }

        [TestCase(false)] [TestCase(true)]
        public void PhaseAndRoundLifetimesKeepTheirLastTickBeforeExplicitExpiry(bool roundLifetime)
        {
            var duration = roundLifetime ? StatusDuration.UntilRoundEnd(Round) : StatusDuration.UntilPhaseEnd(Round, "enemy");
            var status = Status("expiring", "poison", Enemy, duration: duration);
            var result = CombatEngine.ResolveTiming(Create(new[] { status }), 0, Phase(Enemy),
                statusCatalog: Catalog(Definition("poison", CombatTimingKind.PhaseEnding)));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(27, result.State.Get(Enemy).Health);
            Assert.AreSame(status, result.State.Statuses.Single()); Assert.IsEmpty(Consumed(result));
            var expired = roundLifetime ? CombatStatusLifecycle.EndRound(result.State, result.State.Revision, Round)
                : CombatStatusLifecycle.EndPhase(result.State, result.State.Revision, Round, "enemy");
            Assert.IsTrue(expired.Accepted); Assert.IsEmpty(expired.State.Statuses); Assert.AreEqual(27, expired.State.Get(Enemy).Health);
            Assert.AreEqual(CombatStatusChangeReason.Expired, expired.RuleEvents.OfType<CombatStatusChangedEvent>().Single().Reason);
        }

        [Test] public void KeepLongerRefreshesWithoutAddingOrDecayingStacks()
        {
            var first = Status("original", "poison", Enemy, stacks: 3, count: 1);
            var reapplied = Status("refresh", "poison", Enemy, stacks: 2, count: 3, source: Ally);
            var catalog = Catalog(Definition("poison", CombatTimingKind.PhaseEnding, amount: 2));
            var applied = CombatEngine.Resolve(Create(new[] { first }), 0, new[] { CombatEffect.ApplyStatus(reapplied) }, statusCatalog: catalog);
            Assert.IsTrue(applied.Accepted); Assert.AreEqual(first.Id, applied.State.Statuses.Single().Id);
            Assert.AreEqual(3, applied.State.Statuses.Single().Stacks); Assert.AreEqual(3, applied.State.Statuses.Single().Duration.RemainingTriggers);
            var tick = CombatEngine.ResolveTiming(applied.State, applied.State.Revision, Phase(Enemy), statusCatalog: catalog);
            Assert.IsTrue(tick.Accepted); Assert.AreEqual(24, tick.State.Get(Enemy).Health);
            Assert.AreEqual(3, tick.State.Statuses.Single().Stacks); Assert.AreEqual(2, tick.State.Statuses.Single().Duration.RemainingTriggers);
            Assert.AreEqual(Ally, tick.Events.Single().Effect.Source);
        }

        [Test] public void IndependentInstancesTickAndExpireSeparatelyInStoredOrder()
        {
            var a = Status("one-tick", "poison", Enemy, stacks: 2, count: 1);
            var b = Status("two-ticks", "poison", Enemy, stacks: 3, count: 2);
            var catalog = Catalog(Definition("poison", CombatTimingKind.PhaseEnding, amount: 2, policy: StatusReapplyPolicy.IndependentInstances));
            var state = CombatEngine.Resolve(Create(), 0, new[] { CombatEffect.ApplyStatus(a), CombatEffect.ApplyStatus(b) }, statusCatalog: catalog).State;
            var result = CombatEngine.ResolveTiming(state, state.Revision, Phase(Enemy), statusCatalog: catalog);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(20, result.State.Get(Enemy).Health);
            CollectionAssert.AreEqual(new long[] { 4, 6 }, result.Events.Select(item => item.Effect.Amount));
            Assert.AreEqual(b.Id, result.State.Statuses.Single().Id); Assert.AreEqual(3, result.State.Statuses.Single().Stacks);
            Assert.AreEqual(1, result.State.Statuses.Single().Duration.RemainingTriggers);
        }

        [Test] public void NumericOverflowRollsBackEarlierDamageAndConsumedCharges()
        {
            var a = Status("first", "first", Enemy, count: 1); var b = Status("overflow", "overflow", Enemy, stacks: 2);
            var state = Create(new[] { a, b }, enemyBlock: 2);
            var result = CombatEngine.ResolveTiming(state, 0, Action(Enemy), statusCatalog: Catalog(
                Definition("first"), Definition("overflow", amount: long.MaxValue)));
            Rejected(state, result, CombatError.Overflow); Assert.AreEqual(30, state.Get(Enemy).Health); Assert.AreEqual(2, state.Get(Enemy).Block);
            Assert.AreEqual(2, state.Statuses.Count); Assert.AreEqual(1, a.Duration.RemainingTriggers);
        }

        [Test] public void EffectBudgetRejectsTheWholeAreaTickWithoutSpendingCharges()
        {
            var status = Status("too-many-targets", "aura", Player, count: 1); var state = Create(new[] { status });
            var result = CombatEngine.ResolveTiming(state, 0, Action(Player), statusCatalog: Catalog(
                Definition("aura", target: CombatTargetSpec.Opponents(CombatTargetSelection.AllLiving))),
                reactionLimits: new CombatReactionLimits(maxEffects: 1));
            Rejected(state, result, CombatError.ReactionLimitExceeded); Assert.AreSame(status, state.Statuses.Single());
            Assert.AreEqual(30, state.Get(Enemy).Health); Assert.AreEqual(30, state.Get(Other).Health);
        }

        [Test] public void InvalidIncomingModificationRollsBackTheClockAndCharge()
        {
            var state = Create(new[] { Status("bad-modifier", "bleed", Enemy, count: 1) });
            Rejected(state, CombatEngine.ResolveTiming(state, 0, Action(Enemy), new InvalidIncoming(), Catalog(Definition("bleed"))),
                CombatError.InvalidDamageModifier);
        }

        [Test] public void StaleRevisionPreventsResubmittingTheSameClock()
        {
            var state = Create(new[] { Status("once", "bleed", Enemy, count: 1) }); var signal = Action(Enemy);
            var catalog = Catalog(Definition("bleed")); var first = CombatEngine.ResolveTiming(state, 0, signal, statusCatalog: catalog);
            Assert.IsTrue(first.Accepted);
            var replay = CombatEngine.ResolveTiming(first.State, 0, signal, statusCatalog: catalog);
            Assert.AreEqual(CombatError.StaleRevision, replay.Error); Assert.AreSame(first.State, replay.State);
            Assert.IsEmpty(replay.RuleEvents); Assert.AreEqual(27, replay.State.Get(Enemy).Health);
        }

        [Test] public void UnknownParticipantsAndCatalogEntriesFailWithoutPublishingClockFacts()
        {
            var state = Create(); var unknown = new ParticipantId("unknown");
            Rejected(state, CombatEngine.ResolveTiming(state, 0, Action(unknown)), CombatError.InvalidEffect);
            Rejected(state, CombatEngine.ResolveTiming(state, 0, Phase(Enemy, unknown)), CombatError.InvalidEffect);
            state = Create(new[] { Status("unknown", "unknown", Enemy) });
            Rejected(state, CombatEngine.ResolveTiming(state, 0, Action(Enemy), statusCatalog: Catalog()), CombatError.InvalidState);
        }

        [Test] public void NoCatalogKeepsExistingStatusesPassiveWhilePublishingTheClock()
        {
            var status = Status("passive", "bleed", Enemy); var state = Create(new[] { status });
            var result = CombatEngine.ResolveTiming(state, 0, Action(Enemy));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(1, result.RuleEvents.Count); Assert.IsEmpty(result.Events);
            Assert.AreSame(status, result.State.Statuses.Single()); Assert.AreEqual(30, result.State.Get(Enemy).Health);
        }

        [Test] public void SignalAndBehaviorConfigurationRejectMalformedOrEventRelativeInputs()
        {
            Assert.Catch<ArgumentException>(() => CombatTimingSignal.ActionCompleted(" ", Enemy, "play"));
            Assert.Catch<ArgumentException>(() => CombatTimingSignal.ActionCompleted("a", default(ParticipantId), "play"));
            Assert.Catch<ArgumentException>(() => CombatTimingSignal.ActionCompleted("a", Enemy, " "));
            Assert.Catch<ArgumentException>(() => CombatTimingSignal.PhaseEnding(" ", new[] { Enemy }, "enemy"));
            Assert.Catch<ArgumentException>(() => CombatTimingSignal.PhaseEnding("p", new[] { Enemy }, " "));
            Assert.Catch<ArgumentException>(() => CombatTimingSignal.PhaseEnding("p", new[] { Enemy, Enemy }, "enemy"));
            Assert.Catch<ArgumentException>(() => CombatTimingSignal.PhaseEnding("p", new[] { default(ParticipantId) }, "enemy"));
            Assert.Catch<ArgumentException>(() => CombatTimingSignal.PhaseEnding("p", Array.Empty<ParticipantId>(), "enemy"));
            Assert.Catch<ArgumentException>(() => CombatTimingSignal.PhaseEnding("p", null, "enemy"));
            Assert.Catch<ArgumentException>(() => CombatTimingSignal.PhaseEnding("p",
                Enumerable.Range(0, 65).Select(index => new ParticipantId("unit/" + index)), "enemy"));
            Assert.Catch<ArgumentException>(() => new PeriodicDamageSpec((CombatTimingKind)999, 3));
            Assert.Catch<ArgumentException>(() => new PeriodicDamageSpec(CombatTimingKind.ActionCompleted, -1));
            Assert.Catch<ArgumentException>(() => new PeriodicDamageSpec(CombatTimingKind.ActionCompleted, 3, CombatTargetSpec.EventSource()));
            Assert.Catch<ArgumentException>(() => new PeriodicDamageSpec(CombatTimingKind.PhaseEnding, 3, CombatTargetSpec.EventTarget()));
            Assert.DoesNotThrow(() => Definition("zero-is-valid", amount: 0));
        }

        [Test] public void PhaseSignalsDefensivelyCopyTheirParticipantRoster()
        {
            var participants = new[] { Enemy, Other }; var signal = Phase(participants);
            participants[0] = Player;
            CollectionAssert.AreEqual(new[] { Enemy, Other }, signal.Participants);
            Assert.Catch<NotSupportedException>(() => ((IList<ParticipantId>)signal.Participants)[0] = Player);
            Assert.AreEqual(CombatTimingKind.PhaseEnding, signal.Kind); Assert.AreEqual("enemy", signal.PhaseKey);
            Assert.AreEqual("phase/1", signal.BoundaryId); Assert.IsNull(signal.ActionKey);
            var action = Action(Player); Assert.AreEqual(CombatTimingKind.ActionCompleted, action.Kind);
            Assert.AreEqual("play", action.ActionKey); Assert.IsNull(action.PhaseKey);
        }
    }
}
