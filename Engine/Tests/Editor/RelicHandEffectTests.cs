using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    internal static class RelicHandEffectTestData
    {
        internal static readonly ParticipantId Owner = new ParticipantId("structure-owner"), Ally = new ParticipantId("structure-ally"),
            Enemy = new ParticipantId("structure-enemy"), Other = new ParticipantId("structure-other");
        internal static PokerHandEffectProfile Profile(int hits = 3) => new PokerHandEffectProfile("structure-profile.1",
            Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>().Where(category => category != HandCategory.FullHouse)
                .Select(category => new KeyValuePair<HandCategory, PokerHandDamageSettings>(category,
                    new PokerHandDamageSettings(1, category == HandCategory.Straight ? hits : 1))), 1);
        internal static BattleRelicSetup Gear(string id, params RelicHandEffectModifierSpec[] specs) => new BattleRelicSetup(
            new RelicDefinition("def/" + id, Array.Empty<ScoreEffect>()), new RunRelic(id, "def/" + id),
            new BattleRelicRules("structure.1", handEffects: specs));
        internal static ScoredHandEffectContext Context(HandCategory category)
        {
            var score = ScoreEngine.Evaluate("structure/score", new ScoreProfile("structure-score.1",
                Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>().Select(item => new HandScoreRule(item, 10, 1))),
                category, 1, Array.Empty<ScoringCard>(), Array.Empty<ScoringRelic>());
            return new ScoredHandEffectContext(new CombatState("structure", 0, new[] {
                new CombatantDefinition("hero", 100).Create(Owner, "player"), new CombatantDefinition("foe", 100).Create(Enemy, "enemy")
            }), Owner, score);
        }
    }

    [TestFixture]
    public sealed class RelicHandEffectTests
    {
        private static BattleEffectStep Hit(string id = "hit", long amount = 10) =>
            new BattleEffectStep(id, BattleEffectSpec.Damage(amount), EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving));
        private static BattleEffectSequence Sequence(params BattleEffectStep[] steps) => new BattleEffectSequence("base.1", "base effects", steps);
        private static HandEffectModification Modify(BattleEffectSequence sequence, params BattleRelicSetup[] gear) =>
            new RelicHandEffectModifier().Modify(new HandEffectModifierContext(new BattleRelicLoadout(RelicHandEffectTestData.Owner, gear),
                RelicHandEffectTestData.Context(HandCategory.Straight), sequence));

        [Test] public void BuiltInStepNamesSurviveHitCountAndSequencePositionChanges()
        {
            var rule = new PokerHandEffectRule(RelicHandEffectTestData.Profile());
            var straight = rule.Resolve(RelicHandEffectTestData.Context(HandCategory.Straight));
            var longer = new PokerHandEffectRule(RelicHandEffectTestData.Profile(5)).Resolve(RelicHandEffectTestData.Context(HandCategory.Straight));
            CollectionAssert.AreEqual(new[] { "damage/1", "damage/2", "damage/3" }, straight.Steps.Select(step => step.Id));
            CollectionAssert.AreEqual(straight.Steps.Select(step => step.Id), longer.Steps.Take(3).Select(step => step.Id));
            CollectionAssert.AreEqual(new[] { "damage/1", "heal" }, rule.Resolve(RelicHandEffectTestData.Context(HandCategory.Flush)).Steps.Select(step => step.Id));
            CollectionAssert.AreEqual(new[] { "stun", "control-immunity" }, rule.Resolve(RelicHandEffectTestData.Context(HandCategory.FullHouse)).Steps.Select(step => step.Id));
        }

        [Test] public void NamedStepsAreUniqueWhileLegacyUnnamedStepsRemainValid()
        {
            Assert.Throws<ArgumentException>(() => Sequence(Hit(), Hit()));
            Assert.Catch<ArgumentException>(() => new BattleEffectStep(" ", BattleEffectSpec.Heal(1), EffectTargetSpec.Self()));
            var anonymous = new BattleEffectStep(BattleEffectSpec.Damage(1), EffectTargetSpec.Self());
            var original = Sequence(anonymous, anonymous);
            var modified = Modify(original, RelicHandEffectTestData.Gear("r", RelicHandEffectModifierSpec.AddHits("extra", HandCategory.Straight, "hit", 1)));
            Assert.IsTrue(modified.IsIdentity); Assert.AreSame(original, modified.Output);
        }

        [Test] public void AllFourOperationsComposeOnceAndDoNotDependOnEquipmentOrSpecOrder()
        {
            var original = Sequence(Hit(), new BattleEffectStep("heal", BattleEffectSpec.Heal(3), EffectTargetSpec.Self()), Hit("last", 5));
            var specs = new[] {
                RelicHandEffectModifierSpec.AddHits("repeat", HandCategory.Straight, "hit", 2),
                RelicHandEffectModifierSpec.Replace("replace", HandCategory.Straight, "hit", BattleEffectSpec.Damage(20)),
                RelicHandEffectModifierSpec.Retarget("aoe", HandCategory.Straight, "hit", EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving)),
                RelicHandEffectModifierSpec.Append("block", HandCategory.Straight, BattleEffectSpec.GainBlock(7), EffectTargetSpec.Self())
            };
            var a = RelicHandEffectTestData.Gear("a", specs);
            var b = RelicHandEffectTestData.Gear("b", RelicHandEffectModifierSpec.AddHits("repeat", HandCategory.Straight, "hit", 1));
            var result = Modify(original, a, b);
            var reversed = Modify(original, b, RelicHandEffectTestData.Gear("a", specs.Reverse().ToArray()));
            Assert.AreEqual(7, result.Output.Steps.Count); Assert.AreEqual(5, result.Changes.Count);
            Assert.IsTrue(result.Output.Steps.Take(4).All(step => step.Effect.Amount == 20 && step.Target.Selection == EffectTargetSelection.AllLiving));
            Assert.AreSame(original.Steps[1], result.Output.Steps[4]); Assert.AreSame(original.Steps[2], result.Output.Steps[5]);
            Assert.AreEqual(CombatEffectKind.GainBlock, result.Output.Steps.Last().Effect.Kind);
            Assert.AreEqual(7, result.Output.Steps.Select(step => step.Id).Distinct().Count());
            CollectionAssert.AreEqual(result.Output.Steps.Select(step => step.Id), reversed.Output.Steps.Select(step => step.Id));
            CollectionAssert.AreEqual(result.Changes.Select(change => change.SourceInstanceId + "/" + change.RuleId),
                reversed.Changes.Select(change => change.SourceInstanceId + "/" + change.RuleId));
            Assert.AreEqual(10, original.Steps[0].Effect.Amount); Assert.AreEqual(3, original.Steps.Count);
            Assert.AreEqual(original.RuleVersion, result.Output.RuleVersion);
        }

        [Test] public void WrongCategoryAndAbsentStepLeaveAlternateHandShapesUnchanged()
        {
            var sequence = Sequence(Hit());
            var result = Modify(sequence, RelicHandEffectTestData.Gear("r",
                RelicHandEffectModifierSpec.Replace("other-category", HandCategory.Flush, "hit", BattleEffectSpec.Heal(3)),
                RelicHandEffectModifierSpec.AddHits("missing-step", HandCategory.Straight, "eliminate", 1)));
            Assert.IsTrue(result.IsIdentity); Assert.AreSame(sequence, result.Output); Assert.IsEmpty(result.Changes);
        }

        [Test] public void GeneratedStepsAreNotRematchedByOtherRelics()
        {
            var original = Sequence(Hit());
            var append = RelicHandEffectTestData.Gear("a", RelicHandEffectModifierSpec.Append("extra", HandCategory.Straight,
                BattleEffectSpec.Damage(7), EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving)));
            var generated = Modify(original, append).Output.Steps.Last().Id;
            var result = Modify(original, append, RelicHandEffectTestData.Gear("b",
                RelicHandEffectModifierSpec.AddHits("repeat-added", HandCategory.Straight, generated, 63)));
            Assert.AreEqual(2, result.Output.Steps.Count); Assert.AreEqual(1, result.Changes.Count);
        }

        [Test] public void EquivalentReplacementsAndTargetsMergeAndRetainBothAttributions()
        {
            Func<string, BattleRelicSetup> gear = id => RelicHandEffectTestData.Gear(id,
                RelicHandEffectModifierSpec.Replace("replace", HandCategory.Straight, "hit", BattleEffectSpec.Damage(20)),
                RelicHandEffectModifierSpec.Retarget("target", HandCategory.Straight, "hit", EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving)));
            var result = Modify(Sequence(Hit()), gear("a"), gear("b"));
            Assert.AreEqual(1, result.Output.Steps.Count); Assert.AreEqual(20, result.Output.Steps[0].Effect.Amount);
            Assert.AreEqual(4, result.Changes.Count);
        }

        [TestCase(false)] [TestCase(true)]
        public void ConflictingExclusiveEditsAreRejectedAtBindingInEitherOrder(bool target)
        {
            var a = RelicHandEffectTestData.Gear("a", target ? RelicHandEffectModifierSpec.Retarget("x", HandCategory.Straight, "hit", EffectTargetSpec.Self()) :
                RelicHandEffectModifierSpec.Replace("x", HandCategory.Straight, "hit", BattleEffectSpec.Damage(1)));
            var b = RelicHandEffectTestData.Gear("b", target ? RelicHandEffectModifierSpec.Retarget("x", HandCategory.Straight, "hit", EffectTargetSpec.Allies(EffectTargetSelection.AllLiving)) :
                RelicHandEffectModifierSpec.Replace("x", HandCategory.Straight, "hit", BattleEffectSpec.Damage(2)));
            Assert.Throws<ArgumentException>(() => new BattleRelicLoadout(RelicHandEffectTestData.Owner, new[] { a, b }));
            Assert.Throws<ArgumentException>(() => new BattleRelicLoadout(RelicHandEffectTestData.Owner, new[] { b, a }));
        }

        [Test] public void RepeatRejectsNonDamageTemplatesAndNonDamageReplacements()
        {
            var extra = RelicHandEffectTestData.Gear("r", RelicHandEffectModifierSpec.AddHits("extra", HandCategory.Straight, "hit", 1));
            Assert.Throws<ArgumentException>(() => Modify(Sequence(new BattleEffectStep("hit", BattleEffectSpec.Heal(1), EffectTargetSpec.Self())), extra));
            var replace = RelicHandEffectTestData.Gear("s", RelicHandEffectModifierSpec.Replace("replace", HandCategory.Straight, "hit", BattleEffectSpec.Heal(1)));
            Assert.Throws<ArgumentException>(() => new BattleRelicLoadout(RelicHandEffectTestData.Owner, new[] { extra, replace }));
        }

        [Test] public void ConfigurationIsImmutableAndRejectsInvalidIdsCountsAndFixedBattleScopes()
        {
            var spec = RelicHandEffectModifierSpec.AddHits("extra", HandCategory.Straight, "hit", 1);
            var items = new[] { spec }; var rules = new BattleRelicRules("cfg.1", handEffects: items); items[0] = null;
            Assert.AreSame(spec, rules.HandEffects[0]);
            Assert.Catch<NotSupportedException>(() => ((IList<RelicHandEffectModifierSpec>)rules.HandEffects)[0] = null);
            Assert.Catch<ArgumentException>(() => new BattleRelicRules("cfg.1", handEffects: new RelicHandEffectModifierSpec[] { null }));
            Assert.Throws<ArgumentException>(() => new BattleRelicRules("cfg.1", handEffects: new[] { spec, spec }));
            Assert.Catch<ArgumentException>(() => RelicHandEffectModifierSpec.AddHits("extra", HandCategory.Straight, "", 1));
            Assert.Catch<ArgumentException>(() => RelicHandEffectModifierSpec.AddHits("extra", HandCategory.Straight, "hit", 0));
            Assert.Catch<ArgumentException>(() => RelicHandEffectModifierSpec.AddHits("extra", HandCategory.Straight, "hit", 64));
            Assert.Throws<ArgumentException>(() => RelicHandEffectModifierSpec.AddHits("extra", (HandCategory)999, "hit", 1));
            Assert.Throws<ArgumentException>(() => RelicHandEffectModifierSpec.Append("status", HandCategory.Straight,
                BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(new StatusDefinitionId("s"), 1, StatusDuration.UntilRoundEnd("stale-round"))), EffectTargetSpec.Self()));
            Assert.Throws<ArgumentException>(() => new BattleRelicRules("cfg.1", new[] {
                new RelicNumericModifierSpec("extra", BattleNumericStat.DamageDealt, RelicNumericOperation.AddFlat, 1, new[] { CombatEffectOrigin.Primary })
            }, new[] { spec }));
        }

        [Test] public void StructuralBindingsSurviveRuntimeRestoreAndRejectChangedConfiguration()
        {
            var original = RelicHandEffectModifierSpec.AddHits("extra", HandCategory.Straight, "hit", 1);
            var binding = new BattleRelicLoadout(RelicHandEffectTestData.Owner, new[] { RelicHandEffectTestData.Gear("r", original) });
            var runtime = binding.WithRuntime(new[] { binding.Relics[0].WithRuntime(null, 2, 1, AbilityMemory.Empty.WithCounter("n", 3, MemoryScope.Encounter)) });
            var saved = BattleRelicSnapshotCodec.Serialize(runtime); var restored = BattleRelicSnapshotCodec.Deserialize(saved, binding);
            Assert.AreSame(binding.RulesFor("r"), restored.RulesFor("r")); Assert.AreEqual(saved, BattleRelicSnapshotCodec.Serialize(restored));
            Assert.AreEqual(2, new RelicHandEffectModifier().Modify(new HandEffectModifierContext(restored,
                RelicHandEffectTestData.Context(HandCategory.Straight), Sequence(Hit()))).Output.Steps.Count);
            foreach (var changed in new[] {
                RelicHandEffectModifierSpec.AddHits("extra", HandCategory.Straight, "hit", 2),
                RelicHandEffectModifierSpec.AddHits("extra", HandCategory.Flush, "hit", 1),
                RelicHandEffectModifierSpec.AddHits("extra", HandCategory.Straight, "other", 1),
                RelicHandEffectModifierSpec.AddHits("other-rule", HandCategory.Straight, "hit", 1),
                RelicHandEffectModifierSpec.Replace("extra", HandCategory.Straight, "hit", BattleEffectSpec.Heal(3))
            }) Assert.Throws<SnapshotException>(() => BattleRelicSnapshotCodec.Deserialize(saved,
                new BattleRelicLoadout(RelicHandEffectTestData.Owner, new[] { RelicHandEffectTestData.Gear("r", changed) })));
            Assert.Throws<SnapshotException>(() => BattleRelicSnapshotCodec.Deserialize(saved,
                new BattleRelicLoadout(RelicHandEffectTestData.Owner, new[] { RelicHandEffectTestData.Gear("r") })));
        }

        [Test] public void NumericOnlyFingerprintMatchesThePreviouslyCommittedImplementation()
        {
            // Captured by executing fc3bb5d in an isolated temporary checkout, before structural configuration existed.
            var binding = new BattleRelicLoadout(RelicHandEffectTestData.Owner, new[] {
                new BattleRelicSetup(new RelicDefinition("def/r", Array.Empty<ScoreEffect>()), new RunRelic("r", "def/r"),
                    new BattleRelicRules("numeric.1", new[] { new RelicNumericModifierSpec("bonus", BattleNumericStat.DamageDealt,
                        RelicNumericOperation.AddFlat, 7, new[] { CombatEffectOrigin.Primary }) }))
            });
            Assert.AreEqual("5261de8f03eb53d3b510ccab3bd4610b91ac0e634bea5aae8fde4d02db6eacfe", binding.Fingerprint);
            Assert.IsEmpty(binding.RulesFor("r").HandEffects);
        }

        [Test] public void FingerprintIncludesEffectTargetStatusAndLifetimePayloads()
        {
            var status = new StatusDefinitionId("status");
            var variants = new[] {
                RelicHandEffectModifierSpec.Append("x", HandCategory.Flush, BattleEffectSpec.Heal(1), EffectTargetSpec.Self()),
                RelicHandEffectModifierSpec.Append("x", HandCategory.Flush, BattleEffectSpec.Heal(2), EffectTargetSpec.Self()),
                RelicHandEffectModifierSpec.Append("x", HandCategory.Flush, BattleEffectSpec.GainBlock(1), EffectTargetSpec.Self()),
                RelicHandEffectModifierSpec.Append("x", HandCategory.Flush, BattleEffectSpec.Heal(1), EffectTargetSpec.Allies(EffectTargetSelection.Requested, "a")),
                RelicHandEffectModifierSpec.Append("x", HandCategory.Flush, BattleEffectSpec.Heal(1), EffectTargetSpec.Allies(EffectTargetSelection.Requested, "b")),
                RelicHandEffectModifierSpec.Append("x", HandCategory.Flush, BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(status, 1)), EffectTargetSpec.Self()),
                RelicHandEffectModifierSpec.Append("x", HandCategory.Flush, BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(status, 2)), EffectTargetSpec.Self()),
                RelicHandEffectModifierSpec.Append("x", HandCategory.Flush, BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(status, 1, StatusDuration.ForTriggers(2))), EffectTargetSpec.Self()),
                RelicHandEffectModifierSpec.Append("x", HandCategory.Flush, BattleEffectSpec.ApplyStatus(StatusApplicationSpec.WithLifetime(status, StatusLifetimeSpec.ThroughNextPhase("enemy"))), EffectTargetSpec.Self()),
                RelicHandEffectModifierSpec.Append("x", HandCategory.Flush, BattleEffectSpec.ApplyStatus(StatusApplicationSpec.WithLifetime(status, StatusLifetimeSpec.ThroughNextPhase("player"))), EffectTargetSpec.Self())
            };
            var bindings = variants.Select(spec => new BattleRelicLoadout(RelicHandEffectTestData.Owner, new[] { RelicHandEffectTestData.Gear("r", spec) })).ToArray();
            Assert.AreEqual(bindings.Length, bindings.Select(binding => binding.Fingerprint).Distinct().Count());
            var saved = BattleRelicSnapshotCodec.Serialize(bindings[0]);
            foreach (var binding in bindings.Skip(1)) Assert.Throws<SnapshotException>(() => BattleRelicSnapshotCodec.Deserialize(saved, binding));
            var definition = new RelicDefinition("copies", Array.Empty<ScoreEffect>(), maxCopies: 2);
            Assert.Throws<ArgumentException>(() => new BattleRelicLoadout(RelicHandEffectTestData.Owner, new[] {
                new BattleRelicSetup(definition, new RunRelic("a", "copies"), new BattleRelicRules("v1", handEffects: new[] { variants[0] })),
                new BattleRelicSetup(definition, new RunRelic("b", "copies"), new BattleRelicRules("v1", handEffects: new[] { variants[1] }))
            }));
        }
    }
}
