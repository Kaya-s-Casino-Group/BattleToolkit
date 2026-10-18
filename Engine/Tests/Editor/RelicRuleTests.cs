using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    internal static class RelicRuleTestData
    {
        internal static readonly ParticipantId Owner = new ParticipantId("rules-owner"), Ally = new ParticipantId("rules-ally"),
            Enemy = new ParticipantId("rules-enemy");
        internal static RelicRuleValueSpec Value(string id, BattleRuleValueKey key, int delta) => new RelicRuleValueSpec(id, key, delta);
        internal static BattleRelicSetup Gear(string id, RelicRuleValueSpec[] values = null, RelicRefillRuleSpec[] refills = null) =>
            new BattleRelicSetup(new RelicDefinition("def/" + id, Array.Empty<ScoreEffect>(),
                abilityId: new AbilityDefinitionId("uninstalled"), charges: 4),
                new RunRelic(id, "def/" + id, charges: 3, cooldownRounds: 2,
                    memory: AbilityMemory.Empty.WithCounter("persistent", 7, MemoryScope.Encounter)),
                new BattleRelicRules("rules.1", ruleValues: values, refillRules: refills));

        internal sealed class Fixture
        {
            internal readonly CardDuelEngine Engine;
            internal CardDuelState State;
            internal Fixture(BattlePlayerRules rules = null, BattleRelicSetup[] gear = null, int deckSize = 52,
                BattlePlayerSetup ally = null, CombatStatusCatalog catalog = null, bool start = true)
            {
                Engine = new CardDuelEngine(new BattleRules("rule-tests.1", 20, statusCatalog: catalog));
                var deck = StandardDeck.Cards().Take(deckSize).Select((card, i) => new DeckEntry("card/" + i, card.Id));
                var owner = new BattlePlayerSetup(Owner, new CombatantDefinition("hero", 1000), deck, rules: rules, relics: gear);
                State = Engine.Create("rule-tests", HmacDeckShuffler.FromSeed(new byte[32]), ally == null ? new[] { owner } : new[] { owner, ally },
                    new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("foe", 100000), 7) });
                if (start) Act(CardDuelAction.Start);
            }
            internal CardDuelTransition Act(CardDuelAction action, int count = 0, ParticipantId? actor = null)
            {
                var source = actor ?? (action == CardDuelAction.EnemyAction ? Enemy : Owner);
                var selected = count == 0 ? null : State.Zone(CardZoneKinds.Hand, source).Cards.Take(count).ToArray();
                var result = Engine.Execute(State, State.Revision, source, action, selected);
                Assert.IsTrue(result.Accepted, action + ": " + result.Error); State = result.State; return result;
            }
        }
    }

    [TestFixture]
    public sealed class RelicRuleTests
    {
        [Test] public void IntegerAdjustmentsAggregateBeforeClampingAndAreIndependentOfEquipmentOrder()
        {
            var a = RelicRuleTestData.Gear("a", new[] {
                RelicRuleTestData.Value("capacity", BattleRuleValueKey.MaxEnergy, int.MaxValue),
                RelicRuleTestData.Value("cost", BattleRuleValueKey.PlayEnergyCost, -5) });
            var b = RelicRuleTestData.Gear("b", new[] {
                RelicRuleTestData.Value("capacity", BattleRuleValueKey.MaxEnergy, -int.MaxValue),
                RelicRuleTestData.Value("cost", BattleRuleValueKey.PlayEnergyCost, 6) });
            var first = new RelicRuleTestData.Fixture(gear: new[] { a, b });
            var second = new RelicRuleTestData.Fixture(gear: new[] { b, a });
            var query = first.Engine.Duel.QueryActionCost(first.State.Duel, RelicRuleTestData.Owner, ScoredDuelActionKind.ScoredHand);
            var other = second.Engine.Duel.QueryActionCost(second.State.Duel, RelicRuleTestData.Owner, ScoredDuelActionKind.ScoredHand);
            Assert.AreEqual(3, first.State.Duel.PlayerTurn.MaxEnergy); Assert.AreEqual(2, query.Cost);
            Assert.AreEqual(1, query.Amount.BaseValue); Assert.AreEqual(1, query.Amount.Delta);
            Assert.AreEqual(query.Cost, other.Cost);
            CollectionAssert.AreEqual(new[] { "a", "b" }, query.Amount.Contributions.Select(item => item.SourceInstanceId));
            CollectionAssert.AreEqual(query.Amount.Contributions.Select(item => item.SourceInstanceId), other.Amount.Contributions.Select(item => item.SourceInstanceId));
            Assert.AreEqual(1, first.State.Duel.Player(RelicRuleTestData.Owner).Rules.PlayEnergyCost);
        }

        [TestCase(BattleRuleValueKey.MaxEnergy, -3)]
        [TestCase(BattleRuleValueKey.InitialEnergy, -4)]
        [TestCase(BattleRuleValueKey.HandLimit, -8)]
        [TestCase(BattleRuleValueKey.HandLimit, 45)]
        public void InvalidEffectiveCapacityFailsAtBattleCreation(BattleRuleValueKey key, int delta)
        {
            Assert.Throws<ArgumentException>(() => new RelicRuleTestData.Fixture(gear: new[] {
                RelicRuleTestData.Gear("bad", new[] { RelicRuleTestData.Value("value", key, delta) }) }));
        }

        [TestCase(BattleRuleValueKey.MaxEnergy)] [TestCase(BattleRuleValueKey.InitialEnergy)]
        [TestCase(BattleRuleValueKey.PlayEnergyCost)] [TestCase(BattleRuleValueKey.DiscardEnergyCost)]
        public void EffectiveIntegerOverflowIsRejectedAtBinding(BattleRuleValueKey key)
        {
            Assert.Throws<OverflowException>(() => new RelicRuleTestData.Fixture(gear: new[] {
                RelicRuleTestData.Gear("bad", new[] { RelicRuleTestData.Value("value", key, int.MaxValue) }) }));
        }

        [Test] public void NegativeCostsClampToZeroAndInitialEnergyMayExceedTheMaximum()
        {
            var fixture = new RelicRuleTestData.Fixture(gear: new[] { RelicRuleTestData.Gear("r", new[] {
                RelicRuleTestData.Value("play", BattleRuleValueKey.PlayEnergyCost, int.MinValue),
                RelicRuleTestData.Value("discard", BattleRuleValueKey.DiscardEnergyCost, -1),
                RelicRuleTestData.Value("start", BattleRuleValueKey.InitialEnergy, 2) }) });
            var query = fixture.Engine.Duel.QueryActionCost(fixture.State.Duel, RelicRuleTestData.Owner, ScoredDuelActionKind.ScoredHand);
            Assert.AreEqual(0, query.Cost); Assert.AreEqual(1L + int.MinValue, query.Amount.UnboundedValue);
            Assert.AreEqual(5, fixture.State.Duel.PlayerTurn.Energy); Assert.AreEqual(3, fixture.State.Duel.PlayerTurn.MaxEnergy);
            Assert.AreEqual(0, fixture.Act(CardDuelAction.Discard, 1).ActionCost.Cost);
        }

        [Test] public void ConfigurationCopiesItsInputAndSharesTheExistingModifierIdentityBudget()
        {
            var values = new[] { RelicRuleTestData.Value("v", BattleRuleValueKey.HandLimit, 1) };
            var refills = new[] { new RelicRefillRuleSpec("f", CardDuelDealTrigger.AfterPlay, countDelta: 1) };
            var rules = new BattleRelicRules("rules.1", ruleValues: values, refillRules: refills);
            values[0] = null; refills[0] = null;
            Assert.AreEqual(1, rules.RuleValues[0].Delta); Assert.AreEqual(1, rules.RefillRules[0].CountDelta);
            Assert.Throws<NotSupportedException>(() => ((IList<RelicRuleValueSpec>)rules.RuleValues)[0] = null);
            Assert.Throws<NotSupportedException>(() => ((IList<RelicRefillRuleSpec>)rules.RefillRules)[0] = null);
            Assert.Catch<ArgumentException>(() => new BattleRelicRules("r", ruleValues: values));
            Assert.Catch<ArgumentException>(() => new BattleRelicRules("r", refillRules: refills));
            Assert.Throws<ArgumentException>(() => new BattleRelicRules("r", ruleValues: rules.RuleValues,
                refillRules: new[] { new RelicRefillRuleSpec("v", CardDuelDealTrigger.AfterPlay) }));
            Assert.Throws<ArgumentException>(() => new BattleRelicRules("r", ruleValues: rules.RuleValues, handEffects: new[] {
                RelicHandEffectModifierSpec.AddHits("v", HandCategory.OnePair, "damage/1", 1) }));
            Assert.Throws<ArgumentException>(() => new BattleRelicRules("r", ruleValues: Enumerable.Range(0, 32)
                .Select(i => RelicRuleTestData.Value("v" + i, BattleRuleValueKey.HandLimit, 0)), refillRules: rules.RefillRules));
            Assert.Throws<ArgumentException>(() => RelicRuleTestData.Value("v", (BattleRuleValueKey)999, 1));
        }

        [Test] public void RefillConfigurationRejectsInvalidEnumsAndAmbiguousFixedCounts()
        {
            Assert.Throws<ArgumentException>(() => new RelicRefillRuleSpec("f", (CardDuelDealTrigger)999));
            Assert.Throws<ArgumentException>(() => new RelicRefillRuleSpec("f", CardDuelDealTrigger.AfterPlay, (BattleRefillMode)999));
            Assert.Throws<ArgumentException>(() => new RelicRefillRuleSpec("f", CardDuelDealTrigger.AfterPlay, BattleRefillMode.FixedCount));
            Assert.Throws<ArgumentException>(() => new RelicRefillRuleSpec("f", CardDuelDealTrigger.AfterPlay, BattleRefillMode.FixedCount, -1));
            Assert.Throws<ArgumentException>(() => new RelicRefillRuleSpec("f", CardDuelDealTrigger.AfterPlay, fixedCount: 1));
            Assert.Throws<ArgumentException>(() => new RelicRefillRuleSpec("f", CardDuelDealTrigger.AfterPlay, BattleRefillMode.FillToLimit, 1));
        }

        [TestCase(false)] [TestCase(true)]
        public void ConflictingRefillModesOrFixedCountsFailInEitherEquipmentOrder(bool sameMode)
        {
            var first = new RelicRefillRuleSpec("a", CardDuelDealTrigger.AfterPlay, BattleRefillMode.FixedCount, 1);
            var second = new RelicRefillRuleSpec("b", CardDuelDealTrigger.AfterPlay,
                sameMode ? BattleRefillMode.FixedCount : BattleRefillMode.Skip, sameMode ? (int?)2 : null);
            Assert.Throws<ArgumentException>(() => new BattleRelicRules("r", refillRules: new[] { first, second }));
            var a = RelicRuleTestData.Gear("a", refills: new[] { first });
            var b = RelicRuleTestData.Gear("b", refills: new[] { second });
            Assert.Throws<ArgumentException>(() => new BattleRelicLoadout(RelicRuleTestData.Owner, new[] { a, b }));
            Assert.Throws<ArgumentException>(() => new BattleRelicLoadout(RelicRuleTestData.Owner, new[] { b, a }));
        }

        [Test] public void NewRuleFieldsBindSnapshotsWhileRuntimeUpdatesRetainEffectiveLimits()
        {
            Func<int, CardDuelDealTrigger, int, BattleRelicSetup> gear = (delta, trigger, count) => RelicRuleTestData.Gear("r",
                new[] { RelicRuleTestData.Value("max", BattleRuleValueKey.MaxEnergy, delta) },
                new[] { new RelicRefillRuleSpec("refill", trigger, BattleRefillMode.FixedCount, count) });
            var fixture = new RelicRuleTestData.Fixture(gear: new[] { gear(1, CardDuelDealTrigger.AfterPlay, 2) });
            var owner = fixture.State.Duel.Player(RelicRuleTestData.Owner); var binding = owner.Relics;
            var json = BattleRelicSnapshotCodec.Serialize(binding);
            Assert.AreEqual(json, BattleRelicSnapshotCodec.Serialize(BattleRelicSnapshotCodec.Deserialize(json, binding)));
            foreach (var changed in new[] { gear(2, CardDuelDealTrigger.AfterPlay, 2), gear(1, CardDuelDealTrigger.AfterDiscard, 2), gear(1, CardDuelDealTrigger.AfterPlay, 3) })
                Assert.Throws<SnapshotException>(() => BattleRelicSnapshotCodec.Deserialize(json,
                    new BattleRelicLoadout(RelicRuleTestData.Owner, new[] { changed })));
            var relic = binding.Relics[0];
            var committed = fixture.Engine.UpdateRelicRuntime(fixture.State, fixture.State.Revision, RelicRuleTestData.Owner,
                new[] { relic.WithRuntime(1, 2, 0, relic.Memory) });
            Assert.IsTrue(committed.Accepted); Assert.AreSame(owner.Limits, committed.State.Duel.Player(owner.Id).Limits);
            Assert.AreEqual(binding.Fingerprint, committed.State.Duel.Player(owner.Id).Relics.Fingerprint);
            Assert.AreSame(binding.RulesFor("r"), committed.State.Duel.Player(owner.Id).Relics.RulesFor("r"));
        }

        [Test] public void CopiesOfOneDefinitionCannotDisagreeOnRuleConfiguration()
        {
            var definition = new RelicDefinition("same", Array.Empty<ScoreEffect>(), maxCopies: 2);
            Func<string, int, BattleRelicSetup> setup = (id, delta) => new BattleRelicSetup(definition, new RunRelic(id, "same"),
                new BattleRelicRules("same.1", ruleValues: new[] { RelicRuleTestData.Value("v", BattleRuleValueKey.HandLimit, delta) }));
            Assert.Throws<ArgumentException>(() => new BattleRelicLoadout(RelicRuleTestData.Owner, new[] { setup("a", 1), setup("b", 2) }));
            Assert.DoesNotThrow(() => new BattleRelicLoadout(RelicRuleTestData.Owner, new[] { setup("a", 1), setup("b", 1) }));
        }
    }
}
