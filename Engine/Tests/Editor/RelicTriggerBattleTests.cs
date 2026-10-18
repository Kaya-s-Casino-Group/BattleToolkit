using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    internal static class RelicTriggerTestData
    {
        internal static readonly ParticipantId Owner = new ParticipantId("trigger-owner"), Ally = new ParticipantId("trigger-ally"),
            Enemy = new ParticipantId("trigger-enemy"), Other = new ParticipantId("trigger-other");
        internal static readonly StatusDefinitionId Stun = new StatusDefinitionId("trigger-stun"), Immunity = new StatusDefinitionId("trigger-immunity");
        internal static CombatStatusCatalog Catalog() => new CombatStatusCatalog("trigger-statuses.1", new[] {
            new CombatStatusDefinition(Stun, StatusTags.Control, new StatusBehaviorSpec[] { new BlockActionWindowSpec() }, StatusLifetimeSpec.ForTriggers(1)),
            new CombatStatusDefinition(Immunity, StatusTags.None, new StatusBehaviorSpec[] { new PreventStatusApplicationSpec(StatusTags.Control) }, StatusLifetimeSpec.ForTriggers(1)) });
        internal static BattleEffectStep Hit(long amount = 10) => new BattleEffectStep(BattleEffectSpec.Damage(amount), EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving));
        internal static BattleEffectStep Self(BattleEffectSpec effect) => new BattleEffectStep(effect, EffectTargetSpec.Self());
        internal static BattleEffectSpec Status(StatusDefinitionId id) => BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(id));
        internal sealed class Sequence : IScoredHandEffectRule
        {
            private readonly BattleEffectStep[] steps;
            public string Version => "trigger-sequence.1";
            internal Sequence(params BattleEffectStep[] steps) { this.steps = steps; }
            public BattleEffectSequence Resolve(ScoredHandEffectContext context) => new BattleEffectSequence(Version, "trigger test", steps);
        }
        internal static RelicTriggeredEffectSpec Response(BattleEffectSpec effect, CombatTargetSpec target = null) => new RelicTriggeredEffectSpec(effect, target ?? CombatTargetSpec.Self());
        internal static RelicTriggerSpec Trigger(string id = "hit", RelicTriggerTiming timing = RelicTriggerTiming.DamageSettled,
            RelicEventRelation relation = RelicEventRelation.OwnerIsSource, RelicTriggeredEffectSpec[] effects = null,
            RelicTriggerConditionSpec[] conditions = null, RelicTriggerOccurrence occurrence = RelicTriggerOccurrence.PerEvent,
            CombatEffectOrigin[] origins = null, bool chain = false, int priority = 0,
            CombatReactionSourcePolicy source = CombatReactionSourcePolicy.RequireLiving) =>
            new RelicTriggerSpec(id, timing, relation, effects ?? new[] { Response(BattleEffectSpec.GainBlock(3)) }, conditions,
                occurrence, origins, priority, chain, source);
        internal static BattleRelicSetup Gear(string id = "gear", RelicTriggerSpec[] triggers = null, RelicTriggerBudgetSpec budget = null,
            int? charges = 5, int cooldown = 0, RelicNumericModifierSpec[] modifiers = null, RelicRuleValueSpec[] values = null) =>
            new BattleRelicSetup(new RelicDefinition("def/" + id, Array.Empty<ScoreEffect>(), charges: charges),
                new RunRelic(id, "def/" + id, charges: charges, cooldownRounds: cooldown),
                new BattleRelicRules("triggers.1", modifiers, ruleValues: values, triggers: triggers ?? new[] { Trigger() }, triggerBudget: budget));

        internal sealed class Fixture
        {
            internal readonly CardDuelEngine Engine;
            internal CardDuelState State;
            internal Fixture(BattleRelicSetup[] gear = null, BattlePlayerRules rules = null, bool otherEnemy = false,
                BattleRelicSetup[] allyGear = null, int ownerHealth = 600, int enemyHealth = 10000, int ownerBlock = 0,
                CombatStatusCatalog catalog = null, ICombatReactionProvider provider = null, CombatReactionLimits limits = null,
                PlayerBlockExpiry expiry = PlayerBlockExpiry.Retain, bool start = true)
            {
                Engine = new CardDuelEngine(new BattleRules("trigger-battle.1", 20, expiry, catalog, provider, limits));
                var players = new List<BattlePlayerSetup> { new BattlePlayerSetup(Owner, new CombatantDefinition("hero", 1000, ownerBlock),
                    initialHealth: ownerHealth, rules: rules ?? new BattlePlayerRules(effectRule: new Sequence(Hit())), relics: gear) };
                if (allyGear != null) players.Add(new BattlePlayerSetup(Ally, new CombatantDefinition("ally", 1000), relics: allyGear));
                var enemies = new List<BattleEnemySetup> { new BattleEnemySetup(Enemy, new CombatantDefinition("foe", enemyHealth), 7) };
                if (otherEnemy) enemies.Add(new BattleEnemySetup(Other, new CombatantDefinition("other", 10000), 7));
                State = Engine.Create("trigger-test", HmacDeckShuffler.FromSeed(new byte[32]), players, enemies);
                if (start) Act(CardDuelAction.Start);
            }
            internal BattleRelicState Relic(string id = "gear", ParticipantId? owner = null) => State.Duel.Player(owner ?? Owner).Relics.Relics.Single(item => item.InstanceId == id);
            internal CardInstanceId[] Cards(int count = 1) => State.Zone(CardZoneKinds.Hand, Owner).Cards.Take(count).ToArray();
            internal CardDuelTransition Act(CardDuelAction action, int count = 0, ParticipantId? actor = null)
            {
                var source = actor ?? (action == CardDuelAction.EnemyAction ? State.Duel.NextEnemy : Owner);
                var result = Engine.Execute(State, State.Revision, source, action, count == 0 ? null : State.Zone(CardZoneKinds.Hand, source).Cards.Take(count));
                Assert.IsTrue(result.Accepted, action + ": " + result.Error); State = result.State; return result;
            }
            internal void NextRound()
            {
                foreach (var player in State.Duel.Players.Where(player => State.Duel.HasPlayerWindow(player.Id)).ToArray())
                    Act(CardDuelAction.EndTurn, actor: player.Id);
                while (State.Duel.Phase == ScoredDuelPhase.AwaitingEnemyAction) Act(CardDuelAction.EnemyAction);
            }
        }
    }

    [TestFixture]
    public sealed class RelicTriggerBattleTests
    {
        private static readonly ParticipantId Owner = RelicTriggerTestData.Owner, Ally = RelicTriggerTestData.Ally, Enemy = RelicTriggerTestData.Enemy;
        private static BattlePlayerRules Multi(int hits = 3) => new BattlePlayerRules(effectRule: new RelicTriggerTestData.Sequence(
            Enumerable.Range(0, hits).Select(_ => RelicTriggerTestData.Hit()).ToArray()));

        [TestCase(RelicTriggerOccurrence.PerEvent, 4)] [TestCase(RelicTriggerOccurrence.PerAction, 1)]
        public void MultiHitAndAoeFactsRespectOccurrenceAndEachActivationPaysOnce(RelicTriggerOccurrence occurrence, int expected)
        {
            var spec = RelicTriggerTestData.Trigger(occurrence: occurrence, effects: new[] {
                RelicTriggerTestData.Response(BattleEffectSpec.Damage(2), CombatTargetSpec.Opponents(CombatTargetSelection.AllLiving)),
                RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(3)) });
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { spec }) }, Multi(2), otherEnemy: true);
            var before = fixture.State; var result = fixture.Act(CardDuelAction.Play, 1);
            Assert.AreEqual(expected, result.RelicActivations.Count); Assert.AreEqual(5 - expected, fixture.Relic().RemainingCharges);
            Assert.AreEqual(expected, fixture.Relic().UsesThisRound); Assert.AreEqual(expected * 3, fixture.State.Duel.Combat.Get(Owner).Block);
            Assert.AreEqual(9980 - expected * 2, fixture.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(5, before.Duel.Player(Owner).Relics.Relics.Single().RemainingCharges);
            foreach (var activation in result.RelicActivations)
            {
                Assert.AreEqual(3, activation.Batch.Reactions.Count);
                var children = result.RuleEvents.Where(item => item.Provenance?.ActivationId == activation.ActivationId).ToArray();
                Assert.AreEqual(3, children.Length);
                Assert.IsTrue(children.All(item => item.Provenance.SourceInstanceId == "gear" && item.Provenance.Origin == CombatEffectOrigin.Triggered &&
                    item.Provenance.ParentSequenceIndex == activation.ParentSequenceIndex && !item.Provenance.CanTriggerReactions));
                Assert.AreEqual(activation.Before.RemainingCharges - 1, activation.After.RemainingCharges);
            }
        }

        [Test] public void DifferentTriggersShareActionRoundAndChargeBudgetsInStablePriorityOrder()
        {
            var a = RelicTriggerTestData.Trigger("a", priority: 1); var b = RelicTriggerTestData.Trigger("b", priority: -1);
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { a, b },
                budget: new RelicTriggerBudgetSpec(maxUsesPerAction: 2, maxUsesPerRound: 3)) }, Multi());
            var first = fixture.Act(CardDuelAction.Play, 1);
            CollectionAssert.AreEqual(new[] { "b", "a" }, first.RelicActivations.Select(item => item.TriggerId));
            fixture.Act(CardDuelAction.CollectPlayed);
            var second = fixture.Act(CardDuelAction.Play, 1);
            CollectionAssert.AreEqual(new[] { "b" }, second.RelicActivations.Select(item => item.TriggerId));
            Assert.AreEqual(2, fixture.Relic().RemainingCharges); Assert.AreEqual(3, fixture.Relic().UsesThisRound);
            fixture.Act(CardDuelAction.CollectPlayed); Assert.IsEmpty(fixture.Act(CardDuelAction.Play, 1).RelicActivations);
        }

        [Test] public void EquipmentOrderDoesNotChangeDispatchAndEachOwnerHasAnIndependentBudget()
        {
            var any = new[] { RelicTriggerTestData.Trigger(relation: RelicEventRelation.AllyIsSource) };
            var a = RelicTriggerTestData.Gear("a", any, charges: 1); var z = RelicTriggerTestData.Gear("z", any, charges: 1);
            var ally = new[] { RelicTriggerTestData.Gear("b", any, charges: 1) };
            var left = new RelicTriggerTestData.Fixture(new[] { z, a }, Multi(), allyGear: ally);
            var right = new RelicTriggerTestData.Fixture(new[] { a, z }, Multi(), allyGear: ally);
            var l = left.Act(CardDuelAction.Play, 1); var r = right.Act(CardDuelAction.Play, 1);
            CollectionAssert.AreEqual(new[] { "a", "b", "z" }, l.RelicActivations.Select(item => item.SourceInstanceId));
            CollectionAssert.AreEqual(l.RelicActivations.Select(item => item.ActivationId), r.RelicActivations.Select(item => item.ActivationId));
            Assert.AreEqual(6, left.State.Duel.Combat.Get(Owner).Block); Assert.AreEqual(3, left.State.Duel.Combat.Get(Ally).Block);
            Assert.AreEqual(0, left.Relic("b", Ally).RemainingCharges);
        }

        [TestCase(0, 1, 0)] [TestCase(1, 2, 0)] [TestCase(0, 0, 3)] [TestCase(null, 1, 3)]
        public void FiniteChargesCostsAndUnlimitedChargesAreDistinct(int? charges, int cost, int uses)
        {
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(charges: charges,
                budget: new RelicTriggerBudgetSpec(chargeCost: cost)) }, Multi());
            var result = fixture.Act(CardDuelAction.Play, 1);
            Assert.AreEqual(uses, result.RelicActivations.Count); Assert.AreEqual(charges - uses * cost, fixture.Relic().RemainingCharges);
        }

        [Test] public void CooldownSkipsWholeFollowingRoundsAndFreeActionsDoNotAdvanceIt()
        {
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(budget: new RelicTriggerBudgetSpec(cooldownRounds: 1)) },
                new BattlePlayerRules(playEnergyCost: 0, initialEnergy: 0, effectRule: new RelicTriggerTestData.Sequence(RelicTriggerTestData.Hit())));
            fixture.Act(CardDuelAction.Play, 1); Assert.AreEqual(2, fixture.Relic().CooldownRounds);
            fixture.Act(CardDuelAction.CollectPlayed); Assert.IsEmpty(fixture.Act(CardDuelAction.Play, 1).RelicActivations);
            Assert.AreEqual(2, fixture.Relic().CooldownRounds); fixture.Act(CardDuelAction.CollectPlayed);
            fixture.NextRound(); Assert.AreEqual(1, fixture.Relic().CooldownRounds); Assert.AreEqual(0, fixture.Relic().UsesThisRound);
            Assert.IsEmpty(fixture.Act(CardDuelAction.Play, 1).RelicActivations); fixture.Act(CardDuelAction.CollectPlayed);
            fixture.NextRound(); Assert.AreEqual(0, fixture.Relic().CooldownRounds);
            Assert.AreEqual(1, fixture.Act(CardDuelAction.Play, 1).RelicActivations.Count);
        }

        [Test] public void SharedRoundLimitSurvivesFreeActionsAndResetsOnlyAtNextPlayerRound()
        {
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(budget: new RelicTriggerBudgetSpec(maxUsesPerRound: 1)) },
                new BattlePlayerRules(playEnergyCost: 0, initialEnergy: 0, effectRule: new RelicTriggerTestData.Sequence(RelicTriggerTestData.Hit())));
            fixture.Act(CardDuelAction.Play, 1); fixture.Act(CardDuelAction.CollectPlayed);
            Assert.IsEmpty(fixture.Act(CardDuelAction.Play, 1).RelicActivations); fixture.Act(CardDuelAction.CollectPlayed);
            fixture.Act(CardDuelAction.EndTurn); Assert.AreEqual(1, fixture.Relic().UsesThisRound);
            fixture.Act(CardDuelAction.EnemyAction); Assert.AreEqual(0, fixture.Relic().UsesThisRound);
            Assert.AreEqual(1, fixture.Act(CardDuelAction.Play, 1).RelicActivations.Count);
        }

        [Test] public void ImportedCooldownAndInactiveRelicsArePreservedUntilTheirOwnMaintenance()
        {
            var inactive = RelicTriggerTestData.Gear("inactive", Array.Empty<RelicTriggerSpec>(), cooldown: 3);
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(cooldown: 2), inactive }, start: false);
            Assert.AreEqual(2, fixture.Relic().CooldownRounds); Assert.IsEmpty(fixture.Act(CardDuelAction.Start).RelicActivations);
            Assert.AreEqual(2, fixture.Relic().CooldownRounds);
            Assert.IsEmpty(fixture.Act(CardDuelAction.Play, 1).RelicActivations); fixture.Act(CardDuelAction.CollectPlayed);
            var original = fixture.Relic("inactive"); fixture.NextRound();
            Assert.AreEqual(1, fixture.Relic().CooldownRounds); Assert.AreEqual(original.CooldownRounds, fixture.Relic("inactive").CooldownRounds);
        }

        [Test] public void DefaultDiscardEmitsNoSyntheticFactButConfiguredBlockRewardCanTrigger()
        {
            var spec = RelicTriggerTestData.Trigger(timing: RelicTriggerTiming.BlockGained, conditions: new[] {
                RelicTriggerConditionSpec.DiscardedCardsAtLeast(2), RelicTriggerConditionSpec.FactAtLeast(RelicFactAmount.BlockGained, 6) });
            var gear = new[] { RelicTriggerTestData.Gear(triggers: new[] { spec }) };
            var empty = new RelicTriggerTestData.Fixture(gear);
            var result = empty.Act(CardDuelAction.Discard, 2);
            Assert.IsEmpty(result.RuleEvents); Assert.IsEmpty(result.RelicActivations); Assert.AreEqual(5, empty.Relic().RemainingCharges);
            var rewarding = new RelicTriggerTestData.Fixture(gear, new BattlePlayerRules(discardReward: new PerCardBlockRewardRule(3)));
            result = rewarding.Act(CardDuelAction.Discard, 2);
            Assert.AreEqual(1, result.RelicActivations.Count); Assert.AreEqual(9, rewarding.State.Duel.Combat.Get(Owner).Block);
            Assert.AreEqual(2, rewarding.State.Duel.PlayerTurn.Energy); Assert.AreEqual(2, rewarding.State.DiscardsRemaining);
        }

        [Test] public void FactConditionsUseActualDamageAbsorptionHealingAndOverheal()
        {
            var triggers = new[] {
                RelicTriggerTestData.Trigger("damage", conditions: new[] { RelicTriggerConditionSpec.FactAtLeast(RelicFactAmount.DamageToHealth, 5), RelicTriggerConditionSpec.OwnerHealthAtMost(.5m) }),
                RelicTriggerTestData.Trigger("absorbed", conditions: new[] { RelicTriggerConditionSpec.FactAtLeast(RelicFactAmount.BlockAbsorbed, 7) }),
                RelicTriggerTestData.Trigger("healed", RelicTriggerTiming.HealingSettled, conditions: new[] { RelicTriggerConditionSpec.FactAtLeast(RelicFactAmount.HealthRestored, 505) }),
                RelicTriggerTestData.Trigger("overheal", RelicTriggerTiming.HealingSettled, conditions: new[] { RelicTriggerConditionSpec.FactAtLeast(RelicFactAmount.Overheal, 95) }),
                RelicTriggerTestData.Trigger("wrong-fact", RelicTriggerTiming.HealingSettled, conditions: new[] { RelicTriggerConditionSpec.FactAtLeast(RelicFactAmount.DamageToHealth, 0) }) };
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: triggers) },
                new BattlePlayerRules(effectRule: new RelicTriggerTestData.Sequence(RelicTriggerTestData.Self(BattleEffectSpec.Damage(12)),
                    RelicTriggerTestData.Self(BattleEffectSpec.Heal(600)))), ownerHealth: 500, ownerBlock: 7);
            var result = fixture.Act(CardDuelAction.Play, 1);
            CollectionAssert.AreEquivalent(new[] { "absorbed", "damage", "healed", "overheal" }, result.RelicActivations.Select(item => item.TriggerId));
            Assert.AreEqual(4, fixture.Relic().UsesThisRound); Assert.AreEqual(1000, fixture.State.Duel.Combat.Get(Owner).Health);
        }

        [TestCase(false)] [TestCase(true)]
        public void DamageDeathAndEliminateCanTriggerButDeadEventTargetsNeverRetarget(bool eliminate)
        {
            var spec = RelicTriggerTestData.Trigger(timing: RelicTriggerTiming.UnitDied, effects: new[] {
                RelicTriggerTestData.Response(BattleEffectSpec.Damage(2), CombatTargetSpec.EventTarget()),
                RelicTriggerTestData.Response(BattleEffectSpec.Heal(4)) });
            var primary = new BattleEffectStep(eliminate ? BattleEffectSpec.Eliminate() : BattleEffectSpec.Damage(20), EffectTargetSpec.Opponents());
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { spec }) },
                new BattlePlayerRules(effectRule: new RelicTriggerTestData.Sequence(primary)), enemyHealth: 10, otherEnemy: true);
            var result = fixture.Act(CardDuelAction.Play, 1);
            Assert.AreEqual(1, result.RelicActivations.Count); Assert.AreEqual(1, result.RelicActivations.Single().Batch.Reactions.Count);
            Assert.AreEqual(604, fixture.State.Duel.Combat.Get(Owner).Health);
            Assert.AreEqual(10000, fixture.State.Duel.Combat.Get(RelicTriggerTestData.Other).Health);
        }

        [Test] public void NoLivingTargetsOrOnlyZeroEffectsDoNotConsumeBudget()
        {
            var specs = new[] {
                RelicTriggerTestData.Trigger("dead", RelicTriggerTiming.UnitDied, effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Damage(2), CombatTargetSpec.EventTarget()) }),
                RelicTriggerTestData.Trigger("zero", effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(0)) }) };
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: specs) }, enemyHealth: 10);
            var result = fixture.Act(CardDuelAction.Play, 1);
            Assert.IsEmpty(result.RelicActivations); Assert.AreEqual(5, fixture.Relic().RemainingCharges); Assert.IsTrue(result.State.Duel.IsFinished);
        }

        [Test] public void EnqueuedEffectsSkippedAfterDeathDoNotRefundTheActivation()
        {
            var kill = RelicTriggerTestData.Trigger("kill", effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Eliminate(), CombatTargetSpec.EventTarget()) }, priority: -1);
            var hit = RelicTriggerTestData.Trigger("hit", effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Damage(20), CombatTargetSpec.EventTarget()) });
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { hit, kill }) });
            var result = fixture.Act(CardDuelAction.Play, 1);
            Assert.AreEqual(2, result.RelicActivations.Count); Assert.AreEqual(3, fixture.Relic().RemainingCharges);
            Assert.AreEqual(2, result.Events.Count); Assert.IsTrue(result.State.Duel.IsFinished);
        }

        [TestCase(CombatReactionSourcePolicy.RequireLiving, 10000)] [TestCase(CombatReactionSourcePolicy.AllowDefeated, 9996)]
        public void DeathReactionsRequireExplicitDefeatedSourcePolicy(CombatReactionSourcePolicy policy, int health)
        {
            var spec = RelicTriggerTestData.Trigger(timing: RelicTriggerTiming.UnitDied, relation: RelicEventRelation.OwnerIsTarget,
                effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Damage(4), CombatTargetSpec.EventSource()) }, source: policy);
            var fixture = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { spec }) }, ownerHealth: 5);
            fixture.Act(CardDuelAction.EndTurn); var result = fixture.Act(CardDuelAction.EnemyAction);
            Assert.AreEqual(health, result.State.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(0, result.State.Duel.Combat.Get(Owner).Health);
            Assert.AreEqual(policy == CombatReactionSourcePolicy.AllowDefeated ? 1 : 0, result.RelicActivations.Count);
        }
    }
}
