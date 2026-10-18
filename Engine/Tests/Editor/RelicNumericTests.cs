using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class RelicNumericTests
    {
        private static readonly ParticipantId Owner = new ParticipantId("numeric-owner");
        private static RelicNumericModifierSpec Spec(string id, BattleNumericStat stat, RelicNumericOperation operation, decimal value,
            CombatEffectOrigin origin = CombatEffectOrigin.Primary) => new RelicNumericModifierSpec(id, stat, operation, value, new[] { origin });
        private static BattleRelicSetup Relic(string id, params RelicNumericModifierSpec[] specs) => new BattleRelicSetup(
            new RelicDefinition("def/" + id, Array.Empty<ScoreEffect>()), new RunRelic(id, "def/" + id), new BattleRelicRules("numeric.1", specs));
        private static CombatAmountModification Query(BattleRelicLoadout loadout, BattleNumericStat stat, long amount,
            CombatEffectOrigin origin = CombatEffectOrigin.Primary) => new BattleNumericResolver().Resolve(new BattleNumericQuery(loadout, stat, amount, origin));

        [TestCase(BattleNumericStat.DamageDealt)] [TestCase(BattleNumericStat.DamageReceived)]
        [TestCase(BattleNumericStat.HealingDone)] [TestCase(BattleNumericStat.HealingReceived)]
        [TestCase(BattleNumericStat.BlockGranted)] [TestCase(BattleNumericStat.BlockReceived)]
        public void EveryStatCombinesFlatAndAdditiveRatesWithoutEquipmentOrderDependence(BattleNumericStat stat)
        {
            var equipment = new[] {
                Relic("a", Spec("rate", stat, RelicNumericOperation.AddRate, 0.2m)),
                Relic("b", Spec("rate", stat, RelicNumericOperation.AddRate, 0.3m)),
                Relic("c", Spec("flat", stat, RelicNumericOperation.AddFlat, 10))
            };
            var normal = Query(new BattleRelicLoadout(Owner, equipment), stat, 100);
            var reversed = Query(new BattleRelicLoadout(Owner, equipment.Reverse()), stat, 100);
            Assert.AreEqual(160, normal.OutputAmount); Assert.AreEqual(normal.OutputAmount, reversed.OutputAmount);
            Assert.AreEqual(0.5m, normal.RateAdjustment); Assert.AreEqual(10, normal.FlatAdjustment);
            CollectionAssert.AreEqual(normal.Contributions.Select(item => item.SourceInstanceId), reversed.Contributions.Select(item => item.SourceInstanceId));
            Assert.AreEqual(3, normal.Contributions.Count);
        }

        [Test] public void RatesRoundOnlyAfterCombiningAndNegativeResultsClampWithoutCreatingNegativeEffects()
        {
            var stat = BattleNumericStat.DamageDealt;
            var rules = new[] { Spec("a", stat, RelicNumericOperation.AddRate, 0.5m), Spec("b", stat, RelicNumericOperation.AddRate, 0.5m) };
            Assert.AreEqual(2, Query(new BattleRelicLoadout(Owner, new[] { Relic("r", rules) }), stat, 1).OutputAmount);
            var reduced = new BattleRelicLoadout(Owner, new[] { Relic("r", Spec("a", stat, RelicNumericOperation.AddRate, -2),
                Spec("b", stat, RelicNumericOperation.AddFlat, 5)) });
            Assert.AreEqual(5, Query(reduced, stat, 100).OutputAmount);
            Assert.AreEqual(0, Query(new BattleRelicLoadout(Owner, new[] { Relic("r", Spec("a", stat, RelicNumericOperation.AddFlat, -200)) }), stat, 100).OutputAmount);
            Assert.AreEqual(5, Query(reduced, stat, 0).OutputAmount);
        }

        [Test] public void OriginAndStatFiltersDoNotImplicitlyEnhanceDerivedDamageOrOtherResources()
        {
            var equipment = new BattleRelicLoadout(Owner, new[] { Relic("r",
                Spec("primary", BattleNumericStat.DamageDealt, RelicNumericOperation.AddFlat, 10),
                Spec("periodic", BattleNumericStat.DamageDealt, RelicNumericOperation.AddFlat, 2, CombatEffectOrigin.Periodic)) });
            Assert.AreEqual(20, Query(equipment, BattleNumericStat.DamageDealt, 10).OutputAmount);
            Assert.AreEqual(12, Query(equipment, BattleNumericStat.DamageDealt, 10, CombatEffectOrigin.Periodic).OutputAmount);
            Assert.AreEqual(10, Query(equipment, BattleNumericStat.DamageDealt, 10, CombatEffectOrigin.Reflection).OutputAmount);
            Assert.AreEqual(10, Query(equipment, BattleNumericStat.HealingDone, 10).OutputAmount);
        }

        [Test] public void BindingAndSnapshotIncludeNumericConfigurationAndRetainItAcrossRuntimeUpdates()
        {
            var spec = Spec("bonus", BattleNumericStat.DamageDealt, RelicNumericOperation.AddFlat, 7);
            var binding = new BattleRelicLoadout(Owner, new[] { Relic("r", spec) });
            var state = binding.Relics[0]; var runtime = binding.WithRuntime(new[] {
                state.WithRuntime(null, 2, 3, AbilityMemory.Empty.WithCounter("uses", 9, MemoryScope.Encounter)) });
            string json = BattleRelicSnapshotCodec.Serialize(runtime);
            var restored = BattleRelicSnapshotCodec.Deserialize(json, binding);
            Assert.AreSame(binding.RulesFor("r"), restored.RulesFor("r")); Assert.AreEqual(binding.Fingerprint, runtime.Fingerprint);
            Assert.AreEqual(17, Query(restored, BattleNumericStat.DamageDealt, 10).OutputAmount);
            Assert.AreEqual(2, restored.Relics[0].UsesThisRound); Assert.AreEqual(3, restored.Relics[0].CooldownRounds);
            Assert.AreEqual(json, BattleRelicSnapshotCodec.Serialize(restored));
            foreach (var other in new[] {
                Spec("bonus", BattleNumericStat.DamageDealt, RelicNumericOperation.AddFlat, 8),
                Spec("bonus", BattleNumericStat.DamageReceived, RelicNumericOperation.AddFlat, 7),
                Spec("bonus", BattleNumericStat.DamageDealt, RelicNumericOperation.AddRate, 7),
                Spec("bonus", BattleNumericStat.DamageDealt, RelicNumericOperation.AddFlat, 7, CombatEffectOrigin.Periodic)
            }) Assert.Throws<SnapshotException>(() => BattleRelicSnapshotCodec.Deserialize(json, new BattleRelicLoadout(Owner, new[] { Relic("r", other) })));
            Assert.Throws<SnapshotException>(() => BattleRelicSnapshotCodec.Deserialize(json, new BattleRelicLoadout(Owner, new[] { Relic("r") })));
            var setup = Relic("r", spec);
            var versionChanged = new BattleRelicLoadout(Owner, new[] { new BattleRelicSetup(setup.Definition, setup.Instance, new BattleRelicRules("numeric.2", new[] { spec })) });
            Assert.Throws<SnapshotException>(() => BattleRelicSnapshotCodec.Deserialize(json, versionChanged));
        }

        [Test] public void EmptyNumericConfigurationPreservesTheLegacyBindingFingerprint()
        {
            var definition = new RelicDefinition("legacy", Array.Empty<ScoreEffect>());
            var instance = new RunRelic("legacy/item", "legacy");
            var owner = new ParticipantId("legacy-owner");
            var old = new BattleRelicLoadout(owner, new[] { new BattleRelicSetup(definition, instance) });
            Assert.AreEqual("5f4ad621744ff16a58171d92b45630c009c0466ec8735a097f6b1f8d57acf6c2", old.Fingerprint);
            var explicitEmpty = new BattleRelicLoadout(owner, new[] { new BattleRelicSetup(definition, instance, new BattleRelicRules("empty-config.1")) });
            Assert.AreEqual(old.Fingerprint, explicitEmpty.Fingerprint);
            Assert.DoesNotThrow(() => BattleRelicSnapshotCodec.Deserialize(BattleRelicSnapshotCodec.Serialize(old), explicitEmpty));
        }

        [Test] public void ConfigurationCopiesCollectionsAndRejectsInvalidOrAmbiguousEntries()
        {
            var origins = new[] { CombatEffectOrigin.Primary };
            var spec = new RelicNumericModifierSpec("a", BattleNumericStat.DamageDealt, RelicNumericOperation.AddFlat, 5, origins);
            origins[0] = CombatEffectOrigin.Periodic;
            Assert.AreEqual(CombatEffectOrigin.Primary, spec.Origins[0]);
            var items = new[] { spec }; var rules = new BattleRelicRules("cfg.1", items); items[0] = null;
            Assert.AreSame(spec, rules.Modifiers[0]);
            Assert.Catch<NotSupportedException>(() => ((IList<RelicNumericModifierSpec>)rules.Modifiers)[0] = null);
            Assert.Throws<ArgumentException>(() => new BattleRelicRules("cfg.1", new[] { spec, spec }));
            Assert.Catch<ArgumentException>(() => new BattleRelicRules("cfg.1", new RelicNumericModifierSpec[] { null }));
            Assert.Throws<ArgumentException>(() => new RelicNumericModifierSpec("a", (BattleNumericStat)99, RelicNumericOperation.AddFlat, 1, origins));
            Assert.Throws<ArgumentException>(() => new RelicNumericModifierSpec("a", BattleNumericStat.DamageDealt, RelicNumericOperation.AddFlat, 0.5m, origins));
            Assert.Throws<ArgumentException>(() => new RelicNumericModifierSpec("a", BattleNumericStat.DamageDealt, RelicNumericOperation.AddRate, 1, Array.Empty<CombatEffectOrigin>()));
            Assert.Throws<ArgumentException>(() => new RelicNumericModifierSpec("a", BattleNumericStat.DamageDealt, RelicNumericOperation.AddRate, 1, origins, HandCategory.OnePair));
            var definition = new RelicDefinition("copies", Array.Empty<ScoreEffect>(), maxCopies: 2);
            Assert.Throws<ArgumentException>(() => new BattleRelicLoadout(Owner, new[] {
                new BattleRelicSetup(definition, new RunRelic("a", "copies"), rules),
                new BattleRelicSetup(definition, new RunRelic("b", "copies"))
            }));
        }
    }
}
