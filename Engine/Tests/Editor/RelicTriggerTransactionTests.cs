using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class RelicTriggerTransactionTests
    {
        private static readonly ParticipantId Owner = RelicTriggerTestData.Owner, Enemy = RelicTriggerTestData.Enemy;
        private sealed class InvalidDraw : ICardDuelDealPolicy
        {
            private readonly CardDuelDealTrigger timing;
            internal InvalidDraw(CardDuelDealTrigger timing) { this.timing = timing; }
            public int GetDrawCount(CardDuelDealContext context) => context.Trigger == timing ? context.MissingCards + 1 : new CardDuelDealPolicy().GetDrawCount(context);
        }
        private sealed class Provider : ICombatReactionProvider
        {
            private readonly Func<CombatReactionContext, IEnumerable<CombatReaction>> react;
            internal Provider(Func<CombatReactionContext, IEnumerable<CombatReaction>> react) { this.react = react; }
            public IEnumerable<CombatReaction> React(CombatReactionContext context) => react(context);
        }
        private static void Rejected(CardDuelState before, CardDuelTransition result, CardDuelError error)
        {
            Assert.AreEqual(error, result.Error); Assert.AreSame(before, result.State);
            Assert.IsEmpty(result.RuleEvents); Assert.IsEmpty(result.RelicActivations); Assert.IsEmpty(result.Draws); Assert.IsNull(result.ActionCost);
            Assert.AreEqual(5, before.Duel.Player(Owner).Relics.Relics.Single().RemainingCharges);
        }

        [TestCase(CardDuelAction.Play)] [TestCase(CardDuelAction.Discard)]
        public void PreviewReturnsIdenticalDraftButDoesNotSpendLiveRuntimeOrCards(CardDuelAction action)
        {
            var trigger = RelicTriggerTestData.Trigger(timing: action == CardDuelAction.Play ? RelicTriggerTiming.DamageSettled : RelicTriggerTiming.BlockGained);
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { trigger }) },
                new BattlePlayerRules(discardReward: new PerCardBlockRewardRule(3)));
            var before = fixture.State; var json = BattleRelicSnapshotCodec.Serialize(before.Duel.Player(Owner).Relics);
            var preview = action == CardDuelAction.Play ? fixture.Engine.PreviewPlay(before, before.Revision, Owner, fixture.Cards()) :
                fixture.Engine.PreviewDiscard(before, before.Revision, Owner, fixture.Cards());
            Assert.IsTrue(preview.Accepted); Assert.AreSame(before, fixture.State);
            Assert.AreEqual(json, BattleRelicSnapshotCodec.Serialize(before.Duel.Player(Owner).Relics));
            var result = fixture.Act(action, 1);
            Assert.AreEqual(1, result.RelicActivations.Count);
            Assert.AreEqual(BattleRelicSnapshotCodec.Serialize(preview.State.Duel.Player(Owner).Relics), BattleRelicSnapshotCodec.Serialize(result.State.Duel.Player(Owner).Relics));
            CollectionAssert.AreEqual(preview.RelicActivations.Select(item => item.ActivationId), result.RelicActivations.Select(item => item.ActivationId));
            CollectionAssert.AreEqual(preview.State.Random.StateBytes, result.State.Random.StateBytes);
            CollectionAssert.AreEqual(preview.State.Zone(CardZoneKinds.Hand).Cards, result.State.Zone(CardZoneKinds.Hand).Cards);
            Rejected(before, fixture.Engine.Execute(before, before.Revision + 1, action, fixture.Cards()), CardDuelError.StaleRevision);
        }

        [TestCase(CardDuelAction.Play, CardDuelDealTrigger.AfterPlay)] [TestCase(CardDuelAction.Discard, CardDuelDealTrigger.AfterDiscard)]
        public void LaterRefillFailureRollsBackRelicReservationAndAllActionState(CardDuelAction action, CardDuelDealTrigger timing)
        {
            var trigger = RelicTriggerTestData.Trigger(timing: action == CardDuelAction.Play ? RelicTriggerTiming.DamageSettled : RelicTriggerTiming.BlockGained);
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { trigger }, budget: new RelicTriggerBudgetSpec(cooldownRounds: 1)) },
                new BattlePlayerRules(dealPolicy: new InvalidDraw(timing), discardReward: new PerCardBlockRewardRule(3)));
            var before = fixture.State;
            Rejected(before, fixture.Engine.Execute(before, before.Revision, action, fixture.Cards()), CardDuelError.InvalidDealPlan);
            Assert.AreEqual(0, fixture.Relic().CooldownRounds); Assert.AreEqual(0, fixture.Relic().UsesThisRound);
        }

        [Test] public void AutomaticEnemyStepFailureRollsBackEarlierSuccessfulPlayerActivation()
        {
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear() },
                new BattlePlayerRules(initialEnergy: 1, dealPolicy: new InvalidDraw(CardDuelDealTrigger.PlayerTurnStart)));
            var before = fixture.State; var session = new BattleSession(fixture.Engine, before);
            var result = session.Execute(before.Revision, Owner, CardDuelAction.Play, fixture.Cards());
            Assert.AreEqual(CardDuelError.InvalidDealPlan, result.Error); Assert.AreSame(before, result.State); Assert.AreSame(before, session.State);
            Assert.IsEmpty(result.Steps); Assert.AreEqual(5, fixture.Relic().RemainingCharges);
            Assert.AreEqual(0, fixture.Relic().CooldownRounds); Assert.AreEqual(0, fixture.Relic().UsesThisRound);
        }

        [Test] public void TriggerEffectOverflowRollsBackHealthCardsBudgetAndReports()
        {
            var trigger = RelicTriggerTestData.Trigger(effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(long.MaxValue)) });
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { trigger }) });
            Rejected(fixture.State, fixture.Engine.Execute(fixture.State, fixture.State.Revision, CardDuelAction.Play, fixture.Cards()), CardDuelError.Overflow);
        }

        [TestCase(false, false, 1)] [TestCase(true, false, 1)] [TestCase(false, true, 1)] [TestCase(true, true, 2)]
        public void ChainingRequiresBothEmittingPermissionAndReceivingOriginOptIn(bool emit, bool receive, int count)
        {
            var origins = receive ? new[] { CombatEffectOrigin.Primary, CombatEffectOrigin.Triggered } : null;
            var trigger = RelicTriggerTestData.Trigger(chain: emit, origins: origins,
                effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Damage(1), CombatTargetSpec.EventTarget()) });
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { trigger },
                budget: new RelicTriggerBudgetSpec(maxUsesPerAction: 2)) });
            var result = fixture.Act(CardDuelAction.Play, 1);
            Assert.AreEqual(count, result.RelicActivations.Count); Assert.AreEqual(9990 - count, fixture.State.Duel.Combat.Get(Enemy).Health);
        }

        [Test] public void ReactionLimitFailureAlsoRollsBackEverySharedCharge()
        {
            var trigger = RelicTriggerTestData.Trigger(chain: true, origins: new[] { CombatEffectOrigin.Primary, CombatEffectOrigin.Triggered },
                effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Damage(1), CombatTargetSpec.EventTarget()) });
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { trigger }) }, limits: new CombatReactionLimits(2));
            Rejected(fixture.State, fixture.Engine.Execute(fixture.State, fixture.State.Revision, CardDuelAction.Play, fixture.Cards()), CardDuelError.ReactionLimitExceeded);
        }

        [Test] public void TriggeredNumericModifiersApplyWithoutLeakingPrimaryHandModifiers()
        {
            var trigger = RelicTriggerTestData.Trigger(effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Damage(10), CombatTargetSpec.EventTarget()) });
            var modifiers = new[] {
                new RelicNumericModifierSpec("primary", BattleNumericStat.DamageDealt, RelicNumericOperation.AddFlat, 100, new[] { CombatEffectOrigin.Primary }, HandCategory.HighCard),
                new RelicNumericModifierSpec("trigger", BattleNumericStat.DamageDealt, RelicNumericOperation.AddFlat, 3, new[] { CombatEffectOrigin.Triggered }) };
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { trigger }, modifiers: modifiers) });
            var result = fixture.Act(CardDuelAction.Play, 1); var child = result.Events.Single(item => item.Provenance.ActivationId != null);
            Assert.AreEqual(13, child.SettledAmount); Assert.IsNull(child.Effect.Attack.Hand);
            Assert.AreEqual(AttackOrigin.Other, child.Effect.Attack.Origin);
            Assert.AreEqual(110, result.Events.First().SettledAmount);
        }

        [Test] public void HandConditionsUseAuthorityFactsAndNeverAttachToDerivedEffects()
        {
            var hand = new[] { RelicTriggerConditionSpec.HandIs(HandCategory.HighCard), RelicTriggerConditionSpec.SubmittedCardsAtLeast(1) };
            var root = RelicTriggerTestData.Trigger("root", effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Damage(1), CombatTargetSpec.EventTarget()) }, chain: true, conditions: hand);
            var wrong = RelicTriggerTestData.Trigger("wrong", origins: new[] { CombatEffectOrigin.Triggered }, conditions: hand);
            var child = RelicTriggerTestData.Trigger("child", origins: new[] { CombatEffectOrigin.Triggered });
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { root, wrong, child }) });
            var result = fixture.Act(CardDuelAction.Play, 1);
            CollectionAssert.AreEqual(new[] { "root", "child" }, result.RelicActivations.Select(item => item.TriggerId));
        }

        [Test] public void StatusAppliedRefreshedAndBlockedAreDistinctFactsWithoutConsumptionHooks()
        {
            var applied = RelicTriggerTestData.Trigger("applied", RelicTriggerTiming.StatusApplied, conditions: new[] {
                RelicTriggerConditionSpec.EventStatus(RelicTriggerTestData.Immunity), RelicTriggerConditionSpec.OwnerHasStatus(RelicTriggerTestData.Immunity) });
            var blocked = RelicTriggerTestData.Trigger("blocked", RelicTriggerTiming.StatusBlocked,
                conditions: new[] { RelicTriggerConditionSpec.EventStatus(RelicTriggerTestData.Stun) });
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { applied, blocked }) },
                new BattlePlayerRules(effectRule: new RelicTriggerTestData.Sequence(
                    RelicTriggerTestData.Self(RelicTriggerTestData.Status(RelicTriggerTestData.Immunity)),
                    RelicTriggerTestData.Self(RelicTriggerTestData.Status(RelicTriggerTestData.Immunity)),
                    RelicTriggerTestData.Self(RelicTriggerTestData.Status(RelicTriggerTestData.Stun)))), catalog: RelicTriggerTestData.Catalog());
            var result = fixture.Act(CardDuelAction.Play, 1);
            CollectionAssert.AreEqual(new[] { "applied", "applied", "blocked" }, result.RelicActivations.Select(item => item.TriggerId));
            Assert.AreEqual(9, fixture.State.Duel.Combat.Get(Owner).Block); Assert.IsEmpty(fixture.State.Duel.Combat.Statuses);
        }

        [Test] public void TriggeredStatusUsesExistingImmunityAndAnImmuneResultDoesNotRefund()
        {
            var trigger = RelicTriggerTestData.Trigger(effects: new[] { RelicTriggerTestData.Response(RelicTriggerTestData.Status(RelicTriggerTestData.Stun)) });
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { trigger }) },
                new BattlePlayerRules(effectRule: new RelicTriggerTestData.Sequence(
                    RelicTriggerTestData.Self(RelicTriggerTestData.Status(RelicTriggerTestData.Immunity)), RelicTriggerTestData.Hit())), catalog: RelicTriggerTestData.Catalog());
            var result = fixture.Act(CardDuelAction.Play, 1);
            Assert.AreEqual(1, result.RelicActivations.Count); Assert.AreEqual(4, fixture.Relic().RemainingCharges);
            Assert.AreEqual(1, result.RuleEvents.OfType<CombatStatusBlockedEvent>().Count()); Assert.IsEmpty(fixture.State.Duel.Combat.Statuses);
        }

        [Test] public void TriggeredStatusesBindRelativeLifetimesAndExpireThroughExistingLifecycle()
        {
            var status = BattleEffectSpec.ApplyStatus(StatusApplicationSpec.WithLifetime(RelicTriggerTestData.Immunity,
                StatusLifetimeSpec.ThroughNextPhase(BattleStatusTiming.EnemyPhase)));
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] {
                RelicTriggerTestData.Trigger(effects: new[] { RelicTriggerTestData.Response(status) }) }) }, catalog: RelicTriggerTestData.Catalog());
            var result = fixture.Act(CardDuelAction.Play, 1);
            Assert.AreEqual(1, fixture.State.Duel.Combat.Statuses.Count);
            Assert.IsTrue(fixture.State.Duel.Combat.Statuses.Single().Id.Value.StartsWith(result.RelicActivations.Single().ActivationId, StringComparison.Ordinal));
            fixture.Act(CardDuelAction.CollectPlayed); fixture.NextRound();
            Assert.IsEmpty(fixture.State.Duel.Combat.Statuses); Assert.AreEqual(4, fixture.Relic().RemainingCharges);
        }

        [Test] public void PeriodicLeavesAndPhaseMaintenanceDoNotBecomeNewRelicHooks()
        {
            var poison = new StatusDefinitionId("trigger-periodic");
            var catalog = new CombatStatusCatalog("periodic.1", new[] { new CombatStatusDefinition(poison, StatusTags.None,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.ActionCompleted, 2) }, StatusLifetimeSpec.ForTriggers(2)) });
            var trigger = RelicTriggerTestData.Trigger(relation: RelicEventRelation.Any, origins: new[] { CombatEffectOrigin.Periodic, CombatEffectOrigin.Triggered });
            var provider = new Provider(context => context.Event.Effect.Kind == CombatEffectKind.ClearBlock ? new[] {
                new CombatReaction(new CombatEffect(CombatEffectKind.Damage, Owner, Enemy, 1), "phase-clear", Owner, canTriggerReactions: true) } : Array.Empty<CombatReaction>());
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { trigger }) },
                new BattlePlayerRules(effectRule: new RelicTriggerTestData.Sequence(RelicTriggerTestData.Self(RelicTriggerTestData.Status(poison)))),
                ownerBlock: 100, catalog: catalog, provider: provider, expiry: PlayerBlockExpiry.AfterEnemyPhase);
            var result = fixture.Act(CardDuelAction.Play, 1);
            Assert.AreEqual(1, result.Events.Count(item => item.Provenance.Origin == CombatEffectOrigin.Periodic)); Assert.IsEmpty(result.RelicActivations);
            fixture.Act(CardDuelAction.CollectPlayed); fixture.Act(CardDuelAction.EndTurn); result = fixture.Act(CardDuelAction.EnemyAction);
            Assert.IsTrue(result.Events.Any(item => item.Provenance.RuleId == "phase-clear")); Assert.IsEmpty(result.RelicActivations);
            Assert.AreEqual(5, fixture.Relic().RemainingCharges);
        }

        [Test] public void ExternalAttributionCannotSpendAnUnrelatedRelicBudget()
        {
            var trigger = RelicTriggerTestData.Trigger(timing: RelicTriggerTiming.HealingSettled);
            var provider = new Provider(context => context.Event.Provenance.Origin == CombatEffectOrigin.Primary ? new[] {
                new CombatReaction(new CombatEffect(CombatEffectKind.GainBlock, Owner, Owner, 1), "external", Owner, sourceInstanceId: "gear") } : Array.Empty<CombatReaction>());
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { trigger }) }, provider: provider);
            var result = fixture.Act(CardDuelAction.Play, 1);
            Assert.AreEqual(1, fixture.State.Duel.Combat.Get(Owner).Block); Assert.IsEmpty(result.RelicActivations);
            Assert.AreEqual(5, fixture.Relic().RemainingCharges); Assert.IsTrue(result.Events.All(item => item.Provenance.ActivationId == null));
        }
    }
}
