using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class RelicTriggerConfigurationTests
    {
        private static readonly ParticipantId Owner = RelicTriggerTestData.Owner;

        [TestCase("duplicate")] [TestCase("cross-kind")] [TestCase("empty")] [TestCase("orphan-budget")]
        [TestCase("negative-cost")] [TestCase("zero-limit")] [TestCase("cooldown-overflow")]
        public void InvalidTriggerDefinitionsFailBeforeBattleCreation(string kind)
        {
            Assert.Catch<ArgumentException>(() => {
                var trigger = RelicTriggerTestData.Trigger();
                switch (kind)
                {
                    case "duplicate": new BattleRelicRules("bad", triggers: new[] { trigger, trigger }); break;
                    case "cross-kind": new BattleRelicRules("bad", triggers: new[] { trigger }, ruleValues: new[] {
                        new RelicRuleValueSpec("hit", BattleRuleValueKey.PlayEnergyCost, -1) }); break;
                    case "empty": RelicTriggerTestData.Trigger(effects: Array.Empty<RelicTriggeredEffectSpec>()); break;
                    case "orphan-budget": new BattleRelicRules("bad", triggerBudget: new RelicTriggerBudgetSpec()); break;
                    case "negative-cost": new RelicTriggerBudgetSpec(chargeCost: -1); break;
                    case "zero-limit": new RelicTriggerBudgetSpec(maxUsesPerAction: 0); break;
                    case "cooldown-overflow": new RelicTriggerBudgetSpec(cooldownRounds: int.MaxValue); break;
                }
            });
        }

        [TestCase(false)] [TestCase(true)]
        public void StatusEffectsAndConditionsRequireTheirAuthoritativeCatalog(bool condition)
        {
            var trigger = condition ? RelicTriggerTestData.Trigger(conditions: new[] { RelicTriggerConditionSpec.OwnerHasStatus(RelicTriggerTestData.Stun) }) :
                RelicTriggerTestData.Trigger(effects: new[] { RelicTriggerTestData.Response(RelicTriggerTestData.Status(RelicTriggerTestData.Stun)) });
            Assert.Catch<ArgumentException>(() => new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { trigger }) }));
            Assert.DoesNotThrow(() => new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear(triggers: new[] { trigger }) }, catalog: RelicTriggerTestData.Catalog()));
        }

        [TestCase("budget")] [TestCase("effect")] [TestCase("target")] [TestCase("condition")]
        [TestCase("timing")] [TestCase("relation")] [TestCase("priority")] [TestCase("occurrence")]
        [TestCase("origin")] [TestCase("chain")] [TestCase("source")]
        public void EveryTriggerContractIsBoundIntoTheRuntimeSnapshotFingerprint(string kind)
        {
            var original = new BattleRelicLoadout(Owner, new[] { RelicTriggerTestData.Gear() });
            RelicTriggerSpec trigger;
            switch (kind)
            {
                case "effect": trigger = RelicTriggerTestData.Trigger(effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(4)) }); break;
                case "target": trigger = RelicTriggerTestData.Trigger(effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(3), CombatTargetSpec.EventTarget()) }); break;
                case "condition": trigger = RelicTriggerTestData.Trigger(conditions: new[] { RelicTriggerConditionSpec.OwnerHealthAtMost(.5m) }); break;
                case "timing": trigger = RelicTriggerTestData.Trigger(timing: RelicTriggerTiming.UnitDied); break;
                case "relation": trigger = RelicTriggerTestData.Trigger(relation: RelicEventRelation.OwnerIsTarget); break;
                case "priority": trigger = RelicTriggerTestData.Trigger(priority: 1); break;
                case "occurrence": trigger = RelicTriggerTestData.Trigger(occurrence: RelicTriggerOccurrence.PerAction); break;
                case "origin": trigger = RelicTriggerTestData.Trigger(origins: new[] { CombatEffectOrigin.Triggered }); break;
                case "chain": trigger = RelicTriggerTestData.Trigger(chain: true); break;
                case "source": trigger = RelicTriggerTestData.Trigger(source: CombatReactionSourcePolicy.AllowDefeated); break;
                default: trigger = RelicTriggerTestData.Trigger(); break;
            }
            var changed = new BattleRelicLoadout(Owner, new[] { RelicTriggerTestData.Gear(triggers: new[] { trigger },
                budget: kind == "budget" ? new RelicTriggerBudgetSpec(maxUsesPerRound: 1, chargeCost: 2, cooldownRounds: 1) : null) });
            Assert.AreNotEqual(original.Fingerprint, changed.Fingerprint);
            Assert.Throws<SnapshotException>(() => BattleRelicSnapshotCodec.Deserialize(BattleRelicSnapshotCodec.Serialize(original), changed));
        }

        [Test] public void SnapshotRestorePreservesBudgetAndCooldownAndMaintenanceResetsOnlyRoundMemory()
        {
            var gear = new[] { RelicTriggerTestData.Gear(budget: new RelicTriggerBudgetSpec(cooldownRounds: 1)) };
            var fixture = new RelicTriggerTestData.Fixture(gear);
            var memory = AbilityMemory.Empty.WithCounter("round", 4, MemoryScope.Round).WithCounter("encounter", 7, MemoryScope.Encounter);
            var seeded = fixture.Engine.UpdateRelicRuntime(fixture.State, fixture.State.Revision, Owner,
                new[] { fixture.Relic().WithRuntime(5, 0, 0, memory) });
            Assert.IsTrue(seeded.Accepted); fixture.State = seeded.State;
            fixture.Act(CardDuelAction.Play, 1); fixture.Act(CardDuelAction.CollectPlayed);
            var binding = fixture.State.Duel.Player(Owner).Relics; var json = BattleRelicSnapshotCodec.Serialize(binding);
            var restored = BattleRelicSnapshotCodec.Deserialize(json, new BattleRelicLoadout(Owner, gear));
            Assert.AreEqual(json, BattleRelicSnapshotCodec.Serialize(restored));
            Assert.AreEqual(4, restored.Relics.Single().RemainingCharges); Assert.AreEqual(1, restored.Relics.Single().UsesThisRound);
            Assert.AreEqual(2, restored.Relics.Single().CooldownRounds); Assert.AreEqual(4, restored.Relics.Single().Memory.CounterValue("round"));
            var committed = fixture.Engine.UpdateRelicRuntime(fixture.State, fixture.State.Revision, Owner, restored.Relics);
            Assert.IsTrue(committed.Accepted); fixture.State = committed.State; fixture.NextRound();
            Assert.AreEqual(4, fixture.Relic().RemainingCharges); Assert.AreEqual(0, fixture.Relic().UsesThisRound); Assert.AreEqual(1, fixture.Relic().CooldownRounds);
            Assert.AreEqual(0, fixture.Relic().Memory.CounterValue("round")); Assert.AreEqual(7, fixture.Relic().Memory.CounterValue("encounter"));
            var exported = fixture.State.Duel.Player(Owner).Relics.ExportRunRelics().Single();
            Assert.AreEqual(4, exported.Charges); Assert.AreEqual(1, exported.CooldownRounds);
            Assert.IsNull(binding.Definition("gear").AbilityId, "Finite trigger charges do not install a legacy ability.");
        }

        [Test] public void ExplicitEmptyTriggersPreserveLegacyBindingAndDefaultBattleBehavior()
        {
            var original = RelicRuleTestData.Gear("legacy", new[] { new RelicRuleValueSpec("free", BattleRuleValueKey.PlayEnergyCost, -1) });
            var explicitEmpty = new BattleRelicSetup(original.Definition, original.Instance,
                new BattleRelicRules(original.BattleRules.Version, original.BattleRules.Modifiers, original.BattleRules.HandEffects,
                    original.BattleRules.RuleValues, original.BattleRules.RefillRules, Array.Empty<RelicTriggerSpec>()));
            Assert.AreEqual(new BattleRelicLoadout(Owner, new[] { original }).Fingerprint, new BattleRelicLoadout(Owner, new[] { explicitEmpty }).Fingerprint);
            var fixture = new RelicTriggerTestData.Fixture(new[] { explicitEmpty });
            var before = BattleRelicSnapshotCodec.Serialize(fixture.State.Duel.Player(Owner).Relics);
            var result = fixture.Act(CardDuelAction.Play, 1);
            Assert.IsEmpty(result.RelicActivations); Assert.AreEqual(1, result.Events.Count); Assert.AreEqual(9990, fixture.State.Duel.Combat.Get(RelicTriggerTestData.Enemy).Health);
            fixture.Act(CardDuelAction.CollectPlayed); fixture.Act(CardDuelAction.Discard, 1); fixture.NextRound();
            Assert.AreEqual(before, BattleRelicSnapshotCodec.Serialize(fixture.State.Duel.Player(Owner).Relics));
        }
    }
}
