using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class RelicActionTransactionTests
    {
        private static readonly ParticipantId Owner = RelicTriggerTestData.Owner;
        private sealed class InvalidDraw : ICardDuelDealPolicy
        {
            private readonly CardDuelDealTrigger timing;
            internal InvalidDraw(CardDuelDealTrigger timing) { this.timing = timing; }
            public int GetDrawCount(CardDuelDealContext context) => context.Trigger == timing ? context.MissingCards + 1 : new CardDuelDealPolicy().GetDrawCount(context);
        }
        private static void Rejected(CardDuelState before, CardDuelTransition result, CardDuelError error)
        {
            Assert.AreEqual(error, result.Error); Assert.AreSame(before, result.State);
            Assert.IsEmpty(result.RuleEvents); Assert.IsEmpty(result.RelicActivations); Assert.IsEmpty(result.Draws); Assert.IsNull(result.ActionCompletion);
        }

        [TestCase(CardDuelAction.Play)] [TestCase(CardDuelAction.Discard)]
        public void RefillFailureRollsBackCompletionChargesCooldownEnergyCardsAndRandom(CardDuelAction action)
        {
            var timing = action == CardDuelAction.Play ? RelicActionTiming.PlayCompleted : RelicActionTiming.DiscardCompleted;
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] { RelicActionTestData.Action(timing: timing) },
                new RelicTriggerBudgetSpec(cooldownRounds: 1)) }, new BattlePlayerRules(dealPolicy: new InvalidDraw(
                    action == CardDuelAction.Play ? CardDuelDealTrigger.AfterPlay : CardDuelDealTrigger.AfterDiscard)));
            var before = f.State; var json = BattleRelicSnapshotCodec.Serialize(before.Duel.Player(Owner).Relics);
            for (int i = 0; i < 2; i++) Rejected(before, f.Engine.Execute(before, before.Revision, Owner, action, f.Cards()), CardDuelError.InvalidDealPlan);
            Assert.AreEqual(json, BattleRelicSnapshotCodec.Serialize(before.Duel.Player(Owner).Relics));
            Assert.AreEqual(3, before.Duel.PlayerTurn.Energy); Assert.AreEqual(3, before.DiscardsRemaining);
        }

        [Test] public void SessionLaterFailureRollsBackTheCompletedActionAndAutomaticSteps()
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear() }, new BattlePlayerRules(initialEnergy: 1,
                dealPolicy: new InvalidDraw(CardDuelDealTrigger.PlayerTurnStart)));
            var before = f.State; var session = new BattleSession(f.Engine, before);
            var result = session.Execute(before.Revision, Owner, CardDuelAction.Play, f.Cards());
            Assert.AreEqual(CardDuelError.InvalidDealPlan, result.Error); Assert.AreSame(before, result.State); Assert.AreSame(before, session.State);
            Assert.IsEmpty(result.Steps); Assert.AreEqual(20, f.Relic().RemainingCharges);
        }

        [Test] public void PreviewAndExecutionProduceTheSameCompletionAndBudgetWithoutMutatingLiveState()
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(budget: new RelicTriggerBudgetSpec(cooldownRounds: 1)) });
            var before = f.State; var json = BattleRelicSnapshotCodec.Serialize(before.Duel.Player(Owner).Relics);
            var preview = f.Engine.PreviewPlay(before, before.Revision, Owner, f.Cards());
            Assert.IsTrue(preview.Accepted); Assert.AreEqual(json, BattleRelicSnapshotCodec.Serialize(before.Duel.Player(Owner).Relics));
            var executed = f.Act(CardDuelAction.Play, 1);
            Assert.AreEqual(preview.ActionCompletion.Context.ActionId, executed.ActionCompletion.Context.ActionId);
            CollectionAssert.AreEqual(preview.RelicActivations.Select(x => x.ActivationId), executed.RelicActivations.Select(x => x.ActivationId));
            Assert.AreEqual(BattleRelicSnapshotCodec.Serialize(preview.State.Duel.Player(Owner).Relics), BattleRelicSnapshotCodec.Serialize(executed.State.Duel.Player(Owner).Relics));
        }

        [TestCase(false)] [TestCase(true)]
        public void InvalidCompletionBatchRollsBackEarlierDamageAndActionPayment(bool overflow)
        {
            var effects = overflow ? new[] { RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(long.MaxValue)) } : new[] {
                RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(1)), RelicTriggerTestData.Response(BattleEffectSpec.Heal(1)) };
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] { RelicActionTestData.Action(effects: effects) }) },
                limits: new CombatReactionLimits(1));
            Rejected(f.State, f.Engine.Execute(f.State, f.State.Revision, Owner, CardDuelAction.Play, f.Cards()),
                overflow ? CardDuelError.Overflow : CardDuelError.ReactionLimitExceeded);
        }

        [Test] public void BleedAndCompletionChildrenShareTheSameQueueLimit()
        {
            var bleed = new StatusDefinitionId("limited-bleed");
            var catalog = new CombatStatusCatalog("bleed.1", new[] { new CombatStatusDefinition(bleed, StatusTags.None,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.ActionCompleted, 1) }, StatusLifetimeSpec.ForTriggers(1)) });
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(boundaries: new[] { RelicBoundaryTestData.Boundary(effects: new[] {
                RelicTriggerTestData.Response(RelicTriggerTestData.Status(bleed)) }) }) }, catalog: catalog, limits: new CombatReactionLimits(1));
            Rejected(f.State, f.Engine.Execute(f.State, f.State.Revision, Owner, CardDuelAction.Play, f.Cards()), CardDuelError.ReactionLimitExceeded);
            Assert.AreEqual(1, f.State.Duel.Combat.Statuses.Count); Assert.AreEqual(19, f.Relic().RemainingCharges);
        }

        [Test] public void RejectedSelectionsCostsAndDiscardBudgetsNeverCompleteOrSpend()
        {
            var gear = new[] { RelicActionTestData.Gear(new[] { RelicActionTestData.Action(), RelicActionTestData.Action("discard", RelicActionTiming.DiscardCompleted) }) };
            var f = new RelicTriggerTestData.Fixture(gear);
            Rejected(f.State, f.Engine.Execute(f.State, f.State.Revision, Owner, CardDuelAction.Play), CardDuelError.InvalidSelection);
            Rejected(f.State, f.Engine.Execute(f.State, f.State.Revision + 1, Owner, CardDuelAction.Play, f.Cards()), CardDuelError.StaleRevision);
            f = new RelicTriggerTestData.Fixture(gear, new BattlePlayerRules(initialEnergy: 0, discardEnergyCost: 0));
            Rejected(f.State, f.Engine.Execute(f.State, f.State.Revision, Owner, CardDuelAction.Play, f.Cards()), CardDuelError.InsufficientEnergy);
            for (int i = 0; i < 3; i++) f.Act(CardDuelAction.Discard, 1);
            Rejected(f.State, f.Engine.Execute(f.State, f.State.Revision, Owner, CardDuelAction.Discard, f.Cards()), CardDuelError.NoDiscards);
        }

        [Test] public void ActionOnlyRuntimeRoundTripsWithExistingSnapshotSchema()
        {
            var gear = new[] { RelicActionTestData.Gear(budget: new RelicTriggerBudgetSpec(cooldownRounds: 1)) };
            var f = new RelicTriggerTestData.Fixture(gear); f.Act(CardDuelAction.Play, 1);
            var json = BattleRelicSnapshotCodec.Serialize(f.State.Duel.Player(Owner).Relics);
            var restored = BattleRelicSnapshotCodec.Deserialize(json, new BattleRelicLoadout(Owner, gear));
            Assert.AreEqual(json, BattleRelicSnapshotCodec.Serialize(restored)); Assert.AreEqual(19, restored.Relics.Single().RemainingCharges);
            Assert.AreEqual(1, restored.Relics.Single().UsesThisRound); Assert.AreEqual(2, restored.Relics.Single().CooldownRounds);
        }

        [TestCase("timing")] [TestCase("relation")] [TestCase("effect")] [TestCase("target")] [TestCase("condition")]
        [TestCase("priority")] [TestCase("source")] [TestCase("chain")] [TestCase("budget")]
        public void ConfigurationChangesInvalidateSavedBinding(string change)
        {
            var original = new BattleRelicLoadout(Owner, new[] { RelicActionTestData.Gear() });
            var spec = RelicActionTestData.Action(timing: change == "timing" ? RelicActionTiming.DiscardCompleted : RelicActionTiming.PlayCompleted,
                relation: change == "relation" ? RelicActionRelation.AllyIsActor : RelicActionRelation.OwnerIsActor,
                effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(change == "effect" ? 4 : 3),
                    change == "target" ? CombatTargetSpec.EventActor() : CombatTargetSpec.Self()) },
                conditions: change == "condition" ? new[] { RelicTriggerConditionSpec.OwnerHealthAtMost(.5m) } : null,
                priority: change == "priority" ? 1 : 0, chain: change == "chain",
                source: change == "source" ? CombatReactionSourcePolicy.AllowDefeated : CombatReactionSourcePolicy.RequireLiving);
            var changed = new BattleRelicLoadout(Owner, new[] { RelicActionTestData.Gear(new[] { spec },
                change == "budget" ? new RelicTriggerBudgetSpec(cooldownRounds: 1) : null) });
            Assert.AreNotEqual(original.Fingerprint, changed.Fingerprint);
            Assert.Throws<SnapshotException>(() => BattleRelicSnapshotCodec.Deserialize(BattleRelicSnapshotCodec.Serialize(original), changed));
        }

        [TestCase("event-source")] [TestCase("event-target")] [TestCase("fact-condition")] [TestCase("status-condition")]
        [TestCase("discard-hand")] [TestCase("play-discard")] [TestCase("duplicate")] [TestCase("boundary-actor")]
        [TestCase("fact-actor")] [TestCase("periodic-actor")] [TestCase("status-actor")]
        public void UnavailableFactsAndTargetsAreRejectedAtConfiguration(string kind)
        {
            Assert.Catch<ArgumentException>(() => {
                var actor = RelicTriggerTestData.Response(BattleEffectSpec.Heal(1), CombatTargetSpec.EventActor());
                switch (kind)
                {
                    case "event-source": RelicActionTestData.Action(effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Heal(1), CombatTargetSpec.EventSource()) }); break;
                    case "event-target": RelicActionTestData.Action(effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Heal(1), CombatTargetSpec.EventTarget()) }); break;
                    case "fact-condition": RelicActionTestData.Action(conditions: new[] { RelicTriggerConditionSpec.FactAtLeast(RelicFactAmount.HealthRestored, 1) }); break;
                    case "status-condition": RelicActionTestData.Action(conditions: new[] { RelicTriggerConditionSpec.EventStatus(RelicTriggerTestData.Stun) }); break;
                    case "discard-hand": RelicActionTestData.Action(timing: RelicActionTiming.DiscardCompleted, conditions: new[] { RelicTriggerConditionSpec.HandIs(HandCategory.OnePair) }); break;
                    case "play-discard": RelicActionTestData.Action(conditions: new[] { RelicTriggerConditionSpec.DiscardedCardsAtLeast(1) }); break;
                    case "duplicate": RelicActionTestData.Gear(facts: new[] { RelicTriggerTestData.Trigger("completed") }); break;
                    case "boundary-actor": RelicBoundaryTestData.Boundary(effects: new[] { actor }); break;
                    case "fact-actor": RelicTriggerTestData.Trigger(effects: new[] { actor }); break;
                    case "periodic-actor": new PeriodicDamageSpec(CombatTimingKind.ActionCompleted, 1, CombatTargetSpec.EventActor()); break;
                    case "status-actor": new LifeStealSpec(1, CombatTargetSpec.EventActor()); break;
                }
            });
        }

        [TestCase(false)] [TestCase(true)]
        public void StatusDependenciesAreValidatedBeforeBattleStart(bool condition)
        {
            var spec = condition ? RelicActionTestData.Action(conditions: new[] { RelicTriggerConditionSpec.OwnerHasStatus(RelicTriggerTestData.Stun) }) :
                RelicActionTestData.Action(effects: new[] { RelicTriggerTestData.Response(RelicTriggerTestData.Status(RelicTriggerTestData.Stun)) });
            Assert.Catch<ArgumentException>(() => new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] { spec }) }, start: false));
        }

        [Test] public void ExplicitEmptyExtensionPreservesExistingFingerprintActionsAndClockEmission()
        {
            var old = RelicTriggerTestData.Gear();
            var copy = new BattleRelicSetup(old.Definition, old.Instance, new BattleRelicRules(old.BattleRules.Version,
                triggers: old.BattleRules.Triggers, triggerBudget: old.BattleRules.TriggerBudget, actionTriggers: Array.Empty<RelicActionTriggerSpec>()));
            Assert.AreEqual(new BattleRelicLoadout(Owner, new[] { old }).Fingerprint, new BattleRelicLoadout(Owner, new[] { copy }).Fingerprint);
            var left = new RelicTriggerTestData.Fixture(new[] { old }); var right = new RelicTriggerTestData.Fixture(new[] { copy });
            var a = left.Act(CardDuelAction.Play, 1); var b = right.Act(CardDuelAction.Play, 1);
            Assert.IsNull(b.ActionCompletion); Assert.IsFalse(b.RuleEvents.OfType<CombatTimingEvent>().Any());
            Assert.AreEqual(a.State.Duel.Combat.Revision, b.State.Duel.Combat.Revision);
            CollectionAssert.AreEqual(a.Events.Select(x => x.After.Block), b.Events.Select(x => x.After.Block));
            right.Act(CardDuelAction.CollectPlayed); Assert.IsEmpty(right.Act(CardDuelAction.Discard, 1).RuleEvents);
        }
    }
}
