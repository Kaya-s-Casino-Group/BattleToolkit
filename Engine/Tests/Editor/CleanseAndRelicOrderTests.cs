using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CleanseAndRelicOrderTests
    {
        private static readonly ParticipantId Player = new ParticipantId("cleanser"), Target = new ParticipantId("target");
        private static StatusInstance Status(string id, string definition, ParticipantId target) => new StatusInstance(
            new StatusInstanceId(id), new StatusDefinitionId(definition), EntityRef.Participant(Player), EntityRef.Participant(target), 2,
            StatusDuration.UntilRemoved());
        private static CombatStatusCatalog Catalog() => new CombatStatusCatalog("cleanse.1", new[] {
            new CombatStatusDefinition(new StatusDefinitionId("debuff"), StatusTags.Negative, Array.Empty<StatusBehaviorSpec>(),
                StatusLifetimeSpec.UntilRemoved(), StatusReapplyPolicy.IndependentInstances),
            new CombatStatusDefinition(new StatusDefinitionId("stop"), StatusTags.Control, new StatusBehaviorSpec[] { new BlockActionWindowSpec() }, StatusLifetimeSpec.UntilRemoved()),
            new CombatStatusDefinition(new StatusDefinitionId("buff"), StatusTags.None, Array.Empty<StatusBehaviorSpec>(), StatusLifetimeSpec.UntilRemoved())
        });
        private static CombatState State(params StatusInstance[] statuses) => new CombatState("cleanse", 0, new[] {
            new CombatantDefinition("hero", 100).Create(Player, "allies"), new CombatantDefinition("friend", 100).Create(Target, "allies") }, statuses);

        [Test] public void CleanseRemovesAllNegativeAndControlInstancesOnlyFromItsTarget()
        {
            var bad = Status("bad", "debuff", Target); var second = Status("bad-2", "debuff", Target);
            var control = Status("control", "stop", Target); var buff = Status("good", "buff", Target); var other = Status("other", "debuff", Player);
            var state = State(bad, buff, control, second, other);
            var result = CombatEngine.Resolve(state, 0, new[] { CombatEffect.Cleanse(Player, Target) }, statusCatalog: Catalog());
            Assert.IsTrue(result.Accepted); Assert.IsEmpty(result.Events);
            CollectionAssert.AreEqual(new[] { buff, other }, result.State.Statuses);
            var changes = result.RuleEvents.Cast<CombatStatusChangedEvent>().ToArray();
            CollectionAssert.AreEqual(new[] { bad, control, second }, changes.Select(x => x.StatusBefore));
            Assert.IsTrue(changes.All(x => x.Reason == CombatStatusChangeReason.Cleansed && x.StatusAfter == null && x.Effect.Kind == CombatEffectKind.Cleanse));
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, changes.Select(x => x.SequenceIndex));
            Assert.IsEmpty(CombatStatusLifecycle.Blockers(result.State, Target, Catalog()));
            Assert.AreEqual(5, state.Statuses.Count); Assert.AreEqual(100, result.State.Get(Target).Health);
        }

        [Test] public void CleanseUsesTheCurrentBatchSnapshotAndDoesNotPreventLaterReapplication()
        {
            var first = Status("first", "debuff", Target); var later = Status("later", "debuff", Target);
            var result = CombatEngine.Resolve(State(), 0, new[] {
                CombatEffect.ApplyStatus(first), CombatEffect.Cleanse(Player, Target), CombatEffect.ApplyStatus(later)
            }, statusCatalog: Catalog());
            Assert.IsTrue(result.Accepted); Assert.AreSame(later, result.State.Statuses.Single());
            Assert.AreEqual(CombatStatusChangeReason.Cleansed, ((CombatStatusChangedEvent)result.RuleEvents[1]).Reason);
        }

        [Test] public void LaterInvalidEffectRollsBackCleansingAndItsFacts()
        {
            var state = State(Status("bad", "debuff", Target));
            var result = CombatEngine.Resolve(state, 0, new[] { CombatEffect.Cleanse(Player, Target),
                CombatEffect.ApplyStatus(Status("invalid", "missing", Target)) }, statusCatalog: Catalog());
            Assert.AreEqual(CombatError.InvalidEffect, result.Error); Assert.AreSame(state, result.State); Assert.IsEmpty(result.RuleEvents);
            Assert.AreEqual(CombatError.StaleRevision, CombatEngine.Resolve(state, 1, new[] { CombatEffect.Cleanse(Player, Target) }, statusCatalog: Catalog()).Error);
        }

        [Test] public void EmptyAndDeadTargetsDoNotInventRemovalFactsAndCatalogIsRequired()
        {
            var state = State();
            var empty = CombatEngine.Resolve(state, 0, new[] { CombatEffect.Cleanse(Player, Target) }, statusCatalog: Catalog());
            Assert.IsTrue(empty.Accepted); Assert.IsEmpty(empty.RuleEvents);
            Assert.AreEqual(CombatError.InvalidEffect, CombatEngine.Resolve(state, 0, new[] { CombatEffect.Cleanse(Player, Target) }).Error);
            var dead = CombatEngine.Resolve(State(Status("bad", "debuff", Target)), 0, new[] {
                CombatEffect.Eliminate(Player, Target), CombatEffect.Cleanse(Player, Target) }, statusCatalog: Catalog());
            Assert.IsTrue(dead.Accepted); Assert.AreEqual(1, dead.State.Statuses.Count); Assert.AreEqual(1, dead.RuleEvents.Count);
            Assert.Throws<ArgumentException>(() => new CombatEffect(CombatEffectKind.Cleanse, Player, Target, 1));
        }

        private static BattleRelicSetup Gear(string id, int window, BattleEffectSpec effect, int priority)
        {
            var responses = new[] { RelicTriggerTestData.Response(effect) };
            var rules = new BattleRelicRules("order.1",
                triggers: window == 0 ? new[] { RelicTriggerTestData.Trigger(effects: responses, priority: priority) } : null,
                boundaryTriggers: window == 1 ? new[] { RelicBoundaryTestData.Boundary(effects: responses, priority: priority) } : null,
                actionTriggers: window == 2 ? new[] { RelicActionTestData.Action(effects: responses, priority: priority) } : null);
            return new BattleRelicSetup(new RelicDefinition("def/" + id, Array.Empty<ScoreEffect>()), new RunRelic(id, "def/" + id), rules);
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void AcquisitionOrderOverridesCrossRelicPriorityAndChangesActualSettlement(int window)
        {
            // Lexical IDs and priority deliberately oppose acquisition order.
            var shield = Gear("z-first", window, BattleEffectSpec.GainBlock(10), 100);
            var harm = Gear("a-second", window, BattleEffectSpec.Damage(7), -100);
            var forward = new RelicTriggerTestData.Fixture(new[] { shield, harm }, start: window != 1);
            var reverse = new RelicTriggerTestData.Fixture(new[] { harm, shield }, start: window != 1);
            var left = forward.Act(window == 1 ? CardDuelAction.Start : CardDuelAction.Play, window == 1 ? 0 : 1);
            var right = reverse.Act(window == 1 ? CardDuelAction.Start : CardDuelAction.Play, window == 1 ? 0 : 1);
            CollectionAssert.AreEqual(new[] { "z-first", "a-second" }, left.RelicActivations.Select(x => x.SourceInstanceId));
            CollectionAssert.AreEqual(new[] { "a-second", "z-first" }, right.RelicActivations.Select(x => x.SourceInstanceId));
            Assert.AreEqual(600, forward.State.Duel.Combat.Get(RelicTriggerTestData.Owner).Health);
            Assert.AreEqual(3, forward.State.Duel.Combat.Get(RelicTriggerTestData.Owner).Block);
            Assert.AreEqual(593, reverse.State.Duel.Combat.Get(RelicTriggerTestData.Owner).Health);
            Assert.AreEqual(10, reverse.State.Duel.Combat.Get(RelicTriggerTestData.Owner).Block);
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void RelicCleanseRemovesAnEarlierRelicsStatusInsideTheSameWindow(int window)
        {
            var apply = Gear("z-control", window, RelicTriggerTestData.Status(RelicTriggerTestData.Stun), 100);
            var cleanse = Gear("a-cleanse", window, BattleEffectSpec.Cleanse(), -100);
            var f = new RelicTriggerTestData.Fixture(new[] { apply, cleanse }, catalog: RelicTriggerTestData.Catalog(), start: window != 1);
            var result = f.Act(window == 1 ? CardDuelAction.Start : CardDuelAction.Play, window == 1 ? 0 : 1);
            Assert.IsEmpty(f.State.Duel.Combat.Statuses);
            var removed = result.RuleEvents.OfType<CombatStatusChangedEvent>().Single(x => x.Reason == CombatStatusChangeReason.Cleansed);
            Assert.AreEqual(RelicTriggerTestData.Stun, removed.StatusBefore.DefinitionId);
            Assert.AreEqual(CombatEffectOrigin.Triggered, removed.Provenance.Origin);
        }

        [Test] public void OrderedNumericChangesFeedTheNextStepAndSurviveComponentRestore()
        {
            var flat = RelicTriggerTestData.Gear("z-flat", triggers: Array.Empty<RelicTriggerSpec>(), modifiers: new[] {
                new RelicNumericModifierSpec("add", BattleNumericStat.DamageDealt, RelicNumericOperation.AddFlat, 20, new[] { CombatEffectOrigin.Primary }) });
            var rate = RelicTriggerTestData.Gear("a-rate", triggers: Array.Empty<RelicTriggerSpec>(), modifiers: new[] {
                new RelicNumericModifierSpec("multiply", BattleNumericStat.DamageDealt, RelicNumericOperation.AddRate, 0.5m, new[] { CombatEffectOrigin.Primary }) });
            var first = new BattleRelicLoadout(Player, new[] { flat, rate }); var reverse = new BattleRelicLoadout(Player, new[] { rate, flat });
            var restored = BattleRelicSnapshotCodec.Deserialize(BattleRelicSnapshotCodec.Serialize(first), first);
            var resolver = new BattleNumericResolver();
            Assert.AreEqual(180, resolver.Resolve(new BattleNumericQuery(restored, BattleNumericStat.DamageDealt, 100, CombatEffectOrigin.Primary)).OutputAmount);
            Assert.AreEqual(170, resolver.Resolve(new BattleNumericQuery(reverse, BattleNumericStat.DamageDealt, 100, CombatEffectOrigin.Primary)).OutputAmount);
            Assert.Throws<SnapshotException>(() => BattleRelicSnapshotCodec.Deserialize(BattleRelicSnapshotCodec.Serialize(first), reverse));
        }
    }
}
