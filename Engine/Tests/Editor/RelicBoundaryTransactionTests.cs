using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class RelicBoundaryTransactionTests
    {
        private static readonly ParticipantId Owner = RelicTriggerTestData.Owner, Enemy = RelicTriggerTestData.Enemy;
        private sealed class InvalidDraw : ICardDuelDealPolicy
        {
            private readonly CardDuelDealTrigger timing;
            internal InvalidDraw(CardDuelDealTrigger timing) { this.timing = timing; }
            public int GetDrawCount(CardDuelDealContext context) => context.Trigger == timing ? context.MissingCards + 1 : new CardDuelDealPolicy().GetDrawCount(context);
        }
        private static void Rejected(CardDuelState before, CardDuelTransition result, CardDuelError error)
        {
            Assert.AreEqual(error, result.Error); Assert.AreSame(before, result.State);
            Assert.IsEmpty(result.RuleEvents); Assert.IsEmpty(result.RelicActivations); Assert.IsEmpty(result.Draws);
        }

        [Test] public void OpeningDealFailureRollsBackBothBoundariesAndAllowsAnUnspentRetry()
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary("open"), RelicBoundaryTestData.Boundary("phase", RelicBoundaryTiming.PhaseStarted) }) },
                new BattlePlayerRules(dealPolicy: new InvalidDraw(CardDuelDealTrigger.OpeningTurn)), start: false);
            var before = f.State; var snapshot = BattleRelicSnapshotCodec.Serialize(before.Duel.Player(Owner).Relics);
            for (int i = 0; i < 2; i++) Rejected(before, f.Engine.Execute(before, before.Revision, CardDuelAction.Start), CardDuelError.InvalidDealPlan);
            Assert.IsFalse(before.HasStarted); Assert.AreEqual(0, before.Duel.Combat.Get(Owner).Block);
            Assert.AreEqual(snapshot, BattleRelicSnapshotCodec.Serialize(before.Duel.Player(Owner).Relics));
        }

        [Test] public void NextRoundDealFailureRollsBackPhaseEndResetCooldownAndPhaseStartTogether()
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary("end", RelicBoundaryTiming.PhaseEnding, RelicPhaseRelation.OpponentTeam),
                RelicBoundaryTestData.Boundary("start", RelicBoundaryTiming.PhaseStarted) }, new RelicTriggerBudgetSpec(maxUsesPerRound: 1)) },
                new BattlePlayerRules(dealPolicy: new InvalidDraw(CardDuelDealTrigger.PlayerTurnStart)));
            f.Act(CardDuelAction.EndTurn); var before = f.State;
            var snapshot = BattleRelicSnapshotCodec.Serialize(before.Duel.Player(Owner).Relics);
            Rejected(before, f.Engine.Execute(before, before.Revision, CardDuelAction.EnemyAction), CardDuelError.InvalidDealPlan);
            Assert.AreEqual(snapshot, BattleRelicSnapshotCodec.Serialize(before.Duel.Player(Owner).Relics));
            Assert.AreEqual(0, before.Duel.CompletedRounds); Assert.AreEqual(1, f.Relic().UsesThisRound);
        }

        [Test] public void SessionLaterFailureRollsBackAnEarlierCommittedBoundaryDraft()
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary(timing: RelicBoundaryTiming.PhaseEnding) }) },
                new BattlePlayerRules(dealPolicy: new InvalidDraw(CardDuelDealTrigger.PlayerTurnStart)));
            var before = f.State; var session = new BattleSession(f.Engine, before);
            var result = session.Execute(before.Revision, Owner, CardDuelAction.EndTurn);
            Assert.AreEqual(CardDuelError.InvalidDealPlan, result.Error); Assert.AreSame(before, result.State); Assert.AreSame(before, session.State);
            Assert.IsEmpty(result.Steps); Assert.AreEqual(20, f.Relic().RemainingCharges);
        }

        [TestCase(false)] [TestCase(true)]
        public void PreviewPlayAndEnemyActionIncludeBoundariesWithoutSpendingLiveState(bool enemy)
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary(timing: RelicBoundaryTiming.PhaseEnding, phase: RelicPhaseRelation.Any) }) },
                new BattlePlayerRules(initialEnergy: 1));
            if (enemy) f.Act(CardDuelAction.EndTurn);
            var before = f.State; var original = BattleRelicSnapshotCodec.Serialize(before.Duel.Player(Owner).Relics);
            if (enemy)
            {
                var preview = f.Engine.Duel.PreviewEnemyAction(before.Duel, Enemy);
                Assert.IsNotNull(preview);
                Assert.AreEqual(original, BattleRelicSnapshotCodec.Serialize(before.Duel.Player(Owner).Relics));
                var result = f.Act(CardDuelAction.EnemyAction);
                Assert.IsTrue(preview.Accepted);
                CollectionAssert.AreEqual(preview.RuleEvents.Select(x => new { x.Revision, x.SequenceIndex, Kind = x.Effect?.Kind }),
                    f.Engine.Duel.Execute(before.Duel, before.Duel.Revision, Enemy, ScoredDuelActionKind.EnemyAction).RuleEvents
                        .Select(x => new { x.Revision, x.SequenceIndex, Kind = x.Effect?.Kind }));
                Assert.AreEqual(preview.Hits.Sum(x => x.SettledDamage), result.Events.Where(x => x.Effect.Kind == CombatEffectKind.Damage).Sum(x => x.SettledDamage));
                Assert.AreEqual(1, result.RelicActivations.Count);
            }
            else
            {
                var preview = f.Engine.PreviewPlay(before, before.Revision, Owner, f.Cards());
                Assert.IsTrue(preview.Accepted); Assert.AreEqual(original, BattleRelicSnapshotCodec.Serialize(before.Duel.Player(Owner).Relics));
                var result = f.Act(CardDuelAction.Play, 1);
                CollectionAssert.AreEqual(preview.RelicActivations.Select(x => x.ActivationId), result.RelicActivations.Select(x => x.ActivationId));
                Assert.AreEqual(BattleRelicSnapshotCodec.Serialize(preview.State.Duel.Player(Owner).Relics), BattleRelicSnapshotCodec.Serialize(result.State.Duel.Player(Owner).Relics));
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void InvalidBoundaryBatchRollsBackTheEntireStart(bool overflow)
        {
            var effects = overflow ? new[] { RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(long.MaxValue)) } : new[] {
                RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(1)), RelicTriggerTestData.Response(BattleEffectSpec.Heal(1)) };
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] { RelicBoundaryTestData.Boundary(effects: effects) }) },
                limits: new CombatReactionLimits(1), start: false);
            Rejected(f.State, f.Engine.Execute(f.State, f.State.Revision, CardDuelAction.Start),
                overflow ? CardDuelError.Overflow : CardDuelError.ReactionLimitExceeded);
            Assert.AreEqual(20, f.Relic().RemainingCharges);
        }

        [Test] public void PoisonAndBoundaryChildrenShareOneQueueLimitAndTerminalPoisonSkipsNewActivations()
        {
            var poison = new StatusDefinitionId("poison");
            var catalog = new CombatStatusCatalog("poison.1", new[] { new CombatStatusDefinition(poison, StatusTags.None,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.PhaseEnding, 10) }, StatusLifetimeSpec.ForTriggers(1)) });
            var gear = RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary("open", effects: new[] { RelicTriggerTestData.Response(RelicTriggerTestData.Status(poison)) }),
                RelicBoundaryTestData.Boundary("end", RelicBoundaryTiming.PhaseEnding) });
            var f = new RelicTriggerTestData.Fixture(new[] { gear }, catalog: catalog, limits: new CombatReactionLimits(1));
            Rejected(f.State, f.Engine.Execute(f.State, f.State.Revision, CardDuelAction.EndTurn), CardDuelError.ReactionLimitExceeded);
            var lethal = new RelicTriggerTestData.Fixture(new[] { gear }, catalog: catalog, limits: new CombatReactionLimits(1), ownerHealth: 5);
            var end = lethal.Act(CardDuelAction.EndTurn);
            Assert.IsTrue(end.State.Duel.IsFinished); Assert.IsEmpty(end.RelicActivations); Assert.AreEqual(19, lethal.Relic().RemainingCharges);
        }

        [Test] public void BoundaryRuntimeRoundTripsWithoutAddingPersistedWindowCounters()
        {
            var gear = new[] { RelicBoundaryTestData.Gear(budget: new RelicTriggerBudgetSpec(cooldownRounds: 1)) };
            var f = new RelicTriggerTestData.Fixture(gear);
            var json = BattleRelicSnapshotCodec.Serialize(f.State.Duel.Player(Owner).Relics);
            var restored = BattleRelicSnapshotCodec.Deserialize(json, new BattleRelicLoadout(Owner, gear));
            Assert.AreEqual(json, BattleRelicSnapshotCodec.Serialize(restored));
            Assert.AreEqual(19, restored.Relics.Single().RemainingCharges); Assert.AreEqual(1, restored.Relics.Single().UsesThisRound);
            Assert.AreEqual(2, restored.Relics.Single().CooldownRounds);
        }

        [TestCase("phase")] [TestCase("timing")] [TestCase("effect")] [TestCase("target")] [TestCase("condition")]
        [TestCase("priority")] [TestCase("source")] [TestCase("chain")] [TestCase("window-budget")] [TestCase("budget")]
        public void BoundaryConfigurationChangesInvalidateSavedBinding(string change)
        {
            var original = new BattleRelicLoadout(Owner, new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary(timing: RelicBoundaryTiming.PhaseStarted) }) });
            var changed = new BattleRelicLoadout(Owner, new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary(timing: change == "timing" ? RelicBoundaryTiming.PhaseEnding : RelicBoundaryTiming.PhaseStarted,
                    phase: change == "phase" ? RelicPhaseRelation.Any : RelicPhaseRelation.OwnerTeam,
                    effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(change == "effect" ? 4 : 3),
                        change == "target" ? CombatTargetSpec.Allies() : CombatTargetSpec.Self()) },
                    conditions: change == "condition" ? new[] { RelicTriggerConditionSpec.OwnerHealthAtMost(.5m) } : null,
                    priority: change == "priority" ? 1 : 0, chain: change == "chain",
                    source: change == "source" ? CombatReactionSourcePolicy.AllowDefeated : CombatReactionSourcePolicy.RequireLiving) },
                change == "window-budget" ? new RelicTriggerBudgetSpec(maxUsesPerBoundary: 1) : change == "budget" ? new RelicTriggerBudgetSpec(cooldownRounds: 1) : null) });
            Assert.AreNotEqual(original.Fingerprint, changed.Fingerprint);
            Assert.Throws<SnapshotException>(() => BattleRelicSnapshotCodec.Deserialize(BattleRelicSnapshotCodec.Serialize(original), changed));
        }

        [TestCase("event-target")] [TestCase("event-source")] [TestCase("fact-condition")] [TestCase("phase-missing")]
        [TestCase("opening-phase")] [TestCase("duplicate")] [TestCase("budget")] [TestCase("periodic-start")]
        public void InvalidBoundaryContractsAreRejectedAtConfiguration(string kind)
        {
            Assert.Catch<ArgumentException>(() => {
                var effect = RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(1));
                switch (kind)
                {
                    case "event-target": RelicBoundaryTestData.Boundary(effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Heal(1), CombatTargetSpec.EventTarget()) }); break;
                    case "event-source": RelicBoundaryTestData.Boundary(effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Heal(1), CombatTargetSpec.EventSource()) }); break;
                    case "fact-condition": RelicBoundaryTestData.Boundary(conditions: new[] { RelicTriggerConditionSpec.HandIs(HandCategory.Flush) }); break;
                    case "phase-missing": new RelicBoundaryTriggerSpec("x", RelicBoundaryTiming.PhaseStarted, new[] { effect }); break;
                    case "opening-phase": new RelicBoundaryTriggerSpec("x", RelicBoundaryTiming.BattleStarted, new[] { effect }, RelicPhaseRelation.Any); break;
                    case "duplicate": RelicBoundaryTestData.Gear(new[] { RelicBoundaryTestData.Boundary("hit") }, facts: new[] { RelicTriggerTestData.Trigger() }); break;
                    case "budget": new RelicTriggerBudgetSpec(maxUsesPerBoundary: 0); break;
                    case "periodic-start": new PeriodicDamageSpec(CombatTimingKind.PhaseStarted, 3); break;
                }
            });
        }

        [TestCase(false)] [TestCase(true)]
        public void BoundaryStatusDependenciesAreValidatedBeforeStart(bool condition)
        {
            var spec = condition ? RelicBoundaryTestData.Boundary(conditions: new[] { RelicTriggerConditionSpec.OwnerHasStatus(RelicTriggerTestData.Stun) }) :
                RelicBoundaryTestData.Boundary(effects: new[] { RelicTriggerTestData.Response(RelicTriggerTestData.Status(RelicTriggerTestData.Stun)) });
            Assert.Catch<ArgumentException>(() => new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] { spec }) }, start: false));
        }

        [Test] public void ExplicitEmptyBoundaryExtensionPreservesFactBindingAndDefaultActions()
        {
            var old = RelicTriggerTestData.Gear();
            var copy = new BattleRelicSetup(old.Definition, old.Instance, new BattleRelicRules(old.BattleRules.Version,
                triggers: old.BattleRules.Triggers, triggerBudget: old.BattleRules.TriggerBudget, boundaryTriggers: Array.Empty<RelicBoundaryTriggerSpec>()));
            Assert.AreEqual(new BattleRelicLoadout(Owner, new[] { old }).Fingerprint, new BattleRelicLoadout(Owner, new[] { copy }).Fingerprint);
            var f = new RelicTriggerTestData.Fixture(new[] { copy }, start: false);
            Assert.IsEmpty(f.Act(CardDuelAction.Start).RuleEvents); Assert.AreEqual(0, f.State.Duel.Revision);
            Assert.AreEqual(1, f.Act(CardDuelAction.Play, 1).RelicActivations.Count); f.Act(CardDuelAction.CollectPlayed);
            Assert.IsEmpty(f.Act(CardDuelAction.Discard, 1).RuleEvents);
        }
    }
}
