using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    internal static class RelicBoundaryTestData
    {
        internal static RelicBoundaryTriggerSpec Boundary(string id = "boundary", RelicBoundaryTiming timing = RelicBoundaryTiming.BattleStarted,
            RelicPhaseRelation phase = RelicPhaseRelation.OwnerTeam, RelicTriggeredEffectSpec[] effects = null,
            RelicTriggerConditionSpec[] conditions = null, bool chain = false, int priority = 0,
            CombatReactionSourcePolicy source = CombatReactionSourcePolicy.RequireLiving) =>
            new RelicBoundaryTriggerSpec(id, timing, effects ?? new[] { RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(3)) },
                timing == RelicBoundaryTiming.BattleStarted ? (RelicPhaseRelation?)null : phase, conditions, priority, chain, source);
        internal static BattleRelicSetup Gear(RelicBoundaryTriggerSpec[] boundaries = null, RelicTriggerBudgetSpec budget = null,
            RelicTriggerSpec[] facts = null, int? charges = 20, RelicNumericModifierSpec[] modifiers = null) =>
            new BattleRelicSetup(new RelicDefinition("def/gear", Array.Empty<ScoreEffect>(), charges: charges),
                new RunRelic("gear", "def/gear", charges: charges),
                new BattleRelicRules("boundary.1", modifiers, triggers: facts, triggerBudget: budget, boundaryTriggers: boundaries ?? new[] { Boundary() }));
        internal static BattleEffectSpec Protection(string phase) => BattleEffectSpec.ApplyStatus(StatusApplicationSpec.WithLifetime(
            RelicTriggerTestData.Immunity, StatusLifetimeSpec.ThroughNextPhase(phase)));
    }

    [TestFixture]
    public sealed class RelicBoundaryBattleTests
    {
        private static readonly ParticipantId Owner = RelicTriggerTestData.Owner, Enemy = RelicTriggerTestData.Enemy, Ally = RelicTriggerTestData.Ally;

        [Test] public void OpeningRunsOnceBeforeFirstPhaseAndDealWithoutResettingInitialEnergy()
        {
            var gear = RelicBoundaryTestData.Gear(new[] { RelicBoundaryTestData.Boundary("open"),
                RelicBoundaryTestData.Boundary("phase", RelicBoundaryTiming.PhaseStarted) }, new RelicTriggerBudgetSpec(maxUsesPerBoundary: 1));
            var f = new RelicTriggerTestData.Fixture(new[] { gear }, new BattlePlayerRules(initialEnergy: 1), start: false);
            Assert.AreEqual(0, f.State.Duel.Combat.Revision); Assert.IsEmpty(f.State.Zone(CardZoneKinds.Hand).Cards);
            var before = f.State; var result = f.Act(CardDuelAction.Start);
            CollectionAssert.AreEqual(new[] { "open", "phase" }, result.RelicActivations.Select(x => x.TriggerId));
            CollectionAssert.AreEqual(new[] { CombatTimingKind.BattleStarted, CombatTimingKind.PhaseStarted },
                result.RuleEvents.OfType<CombatTimingEvent>().Select(x => x.Signal.Kind));
            Assert.AreEqual(2, result.RelicActivations.Select(x => x.BoundaryId).Distinct().Count());
            Assert.IsTrue(result.RelicActivations.All(x => x.Timing == null && x.BoundaryTiming.HasValue));
            Assert.AreEqual(1, f.State.Duel.PlayerTurn.Energy); Assert.AreEqual(2, f.Relic().UsesThisRound);
            Assert.AreEqual(18, f.Relic().RemainingCharges); Assert.AreEqual(6, f.State.Duel.Combat.Get(Owner).Block);
            Assert.IsNotEmpty(result.Draws); Assert.IsEmpty(f.State.Duel.Actions);
            Assert.AreEqual(CardDuelError.InvalidPhase, f.Engine.Execute(f.State, f.State.Revision, CardDuelAction.Start).Error);
            Assert.AreEqual(CardDuelError.StaleRevision, f.Engine.Execute(f.State, before.Revision, CardDuelAction.Start).Error);
            Assert.AreEqual(20, before.Duel.Player(Owner).Relics.Relics.Single().RemainingCharges);
        }

        [Test] public void OpeningAndFirstPhaseShareRoundBudgetWithoutASecondReset()
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary("open"), RelicBoundaryTestData.Boundary("phase", RelicBoundaryTiming.PhaseStarted) },
                new RelicTriggerBudgetSpec(maxUsesPerRound: 1)) }, start: false);
            CollectionAssert.AreEqual(new[] { "open" }, f.Act(CardDuelAction.Start).RelicActivations.Select(x => x.TriggerId));
            f.NextRound(); Assert.AreEqual(18, f.Relic().RemainingCharges); Assert.AreEqual(1, f.Relic().UsesThisRound);
        }

        [Test] public void LastEnemyActionEndAndNextStartHaveIndependentWindowsButSharedCharges()
        {
            var facts = new[] { RelicTriggerTestData.Trigger(relation: RelicEventRelation.OwnerIsTarget, occurrence: RelicTriggerOccurrence.PerAction) };
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary("end", RelicBoundaryTiming.PhaseEnding, RelicPhaseRelation.OpponentTeam),
                RelicBoundaryTestData.Boundary("start", RelicBoundaryTiming.PhaseStarted) },
                new RelicTriggerBudgetSpec(maxUsesPerAction: 1, maxUsesPerBoundary: 1), facts) });
            f.Act(CardDuelAction.EndTurn); var result = f.Act(CardDuelAction.EnemyAction);
            CollectionAssert.AreEqual(new[] { "hit", "end", "start" }, result.RelicActivations.Select(x => x.TriggerId));
            Assert.IsNull(result.RelicActivations[0].BoundaryId);
            Assert.AreNotEqual(result.RelicActivations[1].BoundaryId, result.RelicActivations[2].BoundaryId);
            Assert.AreEqual(16, f.Relic().RemainingCharges); Assert.AreEqual(1, f.Relic().UsesThisRound);
        }

        [TestCase(RelicPhaseRelation.OwnerTeam, 1)] [TestCase(RelicPhaseRelation.OpponentTeam, 1)] [TestCase(RelicPhaseRelation.Any, 2)]
        public void PhaseFiltersFireOncePerTeamEvenWithMultiplePlayersAndEnemies(RelicPhaseRelation phase, int count)
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary(timing: RelicBoundaryTiming.PhaseEnding, phase: phase) }) },
                allyGear: Array.Empty<BattleRelicSetup>(), otherEnemy: true);
            Assert.IsEmpty(f.Act(CardDuelAction.EndTurn).RelicActivations);
            var endPlayers = f.Act(CardDuelAction.EndTurn, actor: Ally);
            Assert.IsEmpty(f.Act(CardDuelAction.EnemyAction).RelicActivations);
            var endEnemies = f.Act(CardDuelAction.EnemyAction);
            Assert.AreEqual(count, endPlayers.RelicActivations.Count + endEnemies.RelicActivations.Count);
            Assert.AreEqual(20 - count, f.Relic().RemainingCharges);
        }

        [Test] public void CooldownAndRoundMemoryAdvanceOnlyBeforeNextPlayerStart()
        {
            var gear = RelicBoundaryTestData.Gear(new[] { RelicBoundaryTestData.Boundary("open"),
                RelicBoundaryTestData.Boundary("start", RelicBoundaryTiming.PhaseStarted, RelicPhaseRelation.Any) },
                new RelicTriggerBudgetSpec(cooldownRounds: 1));
            var f = new RelicTriggerTestData.Fixture(new[] { gear });
            Assert.AreEqual(2, f.Relic().CooldownRounds); Assert.AreEqual(19, f.Relic().RemainingCharges);
            var memory = AbilityMemory.Empty.WithCounter("round", 4, MemoryScope.Round).WithCounter("encounter", 7, MemoryScope.Encounter);
            f.State = f.Engine.UpdateRelicRuntime(f.State, f.State.Revision, Owner, new[] { f.Relic().WithRuntime(19, 1, 2, memory) }).State;
            f.NextRound(); Assert.AreEqual(1, f.Relic().CooldownRounds); Assert.AreEqual(19, f.Relic().RemainingCharges);
            Assert.AreEqual(0, f.Relic().UsesThisRound); Assert.AreEqual(0, f.Relic().Memory.CounterValue("round"));
            Assert.AreEqual(7, f.Relic().Memory.CounterValue("encounter"));
            f.NextRound(); Assert.AreEqual(2, f.Relic().CooldownRounds); Assert.AreEqual(18, f.Relic().RemainingCharges);
            Assert.AreEqual(1, f.Relic().UsesThisRound);
        }

        [Test] public void FreeActionsNeverCreatePhaseBoundariesOrConsumeTheirBudget()
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary(timing: RelicBoundaryTiming.PhaseEnding) }) },
                new BattlePlayerRules(initialEnergy: 0, playEnergyCost: 0, discardEnergyCost: 0));
            Assert.IsEmpty(f.Act(CardDuelAction.Play, 1).RelicActivations); f.Act(CardDuelAction.CollectPlayed);
            Assert.IsEmpty(f.Act(CardDuelAction.Discard, 1).RelicActivations); Assert.AreEqual(20, f.Relic().RemainingCharges);
            Assert.AreEqual(1, f.Act(CardDuelAction.EndTurn).RelicActivations.Count);
        }

        [TestCase(false, 0)] [TestCase(true, 1)]
        public void ChainedBoundaryFactsShareBoundaryLimitAndDoNotUsePerActionOccurrence(bool chain, int children)
        {
            var root = RelicBoundaryTestData.Boundary(effects: new[] {
                RelicTriggerTestData.Response(BattleEffectSpec.Damage(1), CombatTargetSpec.Opponents(CombatTargetSelection.AllLiving)) }, chain: chain);
            var facts = new[] {
                RelicTriggerTestData.Trigger("action-only", occurrence: RelicTriggerOccurrence.PerAction, origins: new[] { CombatEffectOrigin.Triggered }),
                RelicTriggerTestData.Trigger("boundary-only", occurrence: RelicTriggerOccurrence.PerBoundary, origins: new[] { CombatEffectOrigin.Triggered }) };
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] { root },
                new RelicTriggerBudgetSpec(maxUsesPerAction: 1, maxUsesPerBoundary: 2), facts) }, otherEnemy: true, start: false);
            var result = f.Act(CardDuelAction.Start);
            Assert.AreEqual(1 + children, result.RelicActivations.Count);
            Assert.IsTrue(result.RelicActivations.All(x => x.BoundaryId == result.RelicActivations[0].BoundaryId));
            Assert.IsFalse(result.RelicActivations.Any(x => x.TriggerId == "action-only"));
            Assert.AreEqual(10000 - 1, f.State.Duel.Combat.Get(Enemy).Health);
            Assert.IsEmpty(f.Act(CardDuelAction.Play, 1).RelicActivations);
        }

        [Test] public void DirectAndFactActivationsReserveOneSharedBoundaryBudgetInPriorityOrder()
        {
            var roots = new[] { RelicBoundaryTestData.Boundary("late", priority: 2), RelicBoundaryTestData.Boundary("early", priority: -1, chain: true) };
            var facts = new[] { RelicTriggerTestData.Trigger("fact", RelicTriggerTiming.BlockGained, origins: new[] { CombatEffectOrigin.Triggered }) };
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(roots,
                new RelicTriggerBudgetSpec(maxUsesPerBoundary: 1), facts) }, start: false);
            CollectionAssert.AreEqual(new[] { "early" }, f.Act(CardDuelAction.Start).RelicActivations.Select(x => x.TriggerId));
            Assert.AreEqual(19, f.Relic().RemainingCharges);
        }

        [TestCase(RelicBoundaryTiming.BattleStarted, "player", 0)]
        [TestCase(RelicBoundaryTiming.BattleStarted, "enemy", 0)]
        [TestCase(RelicBoundaryTiming.PhaseStarted, "player", 0)]
        [TestCase(RelicBoundaryTiming.PhaseEnding, "player", 1)]
        [TestCase(RelicBoundaryTiming.PhaseEnding, "enemy", 0)]
        public void RelativeStatusLifetimeBindsToTheCompleteUpcomingPhase(RelicBoundaryTiming timing, string phase, int ahead)
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary(timing: timing, effects: new[] { RelicTriggerTestData.Response(RelicBoundaryTestData.Protection(phase)) }) }, charges: 1) },
                catalog: RelicTriggerTestData.Catalog());
            string expected = BattleStatusTiming.RoundScope(f.State.Duel, ahead);
            if (timing == RelicBoundaryTiming.PhaseEnding) f.Act(CardDuelAction.EndTurn);
            var status = f.State.Duel.Combat.Statuses.Single();
            Assert.AreEqual(expected, status.Duration.ScopeId); Assert.AreEqual(phase, status.Duration.PhaseKey);
            if (f.State.Duel.Phase == ScoredDuelPhase.AwaitingPlayerHand) f.Act(CardDuelAction.EndTurn);
            if (phase == "player" && ahead == 0) Assert.IsEmpty(f.State.Duel.Combat.Statuses);
            f.Act(CardDuelAction.EnemyAction);
            if (ahead == 0) Assert.IsEmpty(f.State.Duel.Combat.Statuses);
            else { Assert.AreEqual(1, f.State.Duel.Combat.Statuses.Count); f.Act(CardDuelAction.EndTurn); Assert.IsEmpty(f.State.Duel.Combat.Statuses); }
        }

        [Test] public void NextRoundBoundaryDescendantsBindToNewRoundAndExpireThere()
        {
            var status = BattleEffectSpec.ApplyStatus(StatusApplicationSpec.WithLifetime(RelicTriggerTestData.Immunity, StatusLifetimeSpec.ThroughCurrentRound()));
            var root = RelicBoundaryTestData.Boundary(timing: RelicBoundaryTiming.PhaseStarted, chain: true);
            var fact = RelicTriggerTestData.Trigger(timing: RelicTriggerTiming.BlockGained, origins: new[] { CombatEffectOrigin.Triggered },
                effects: new[] { RelicTriggerTestData.Response(status) });
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] { root }, facts: new[] { fact }, charges: 4) }, catalog: RelicTriggerTestData.Catalog());
            f.NextRound(); Assert.AreEqual(BattleStatusTiming.RoundScope(f.State.Duel), f.State.Duel.Combat.Statuses.Single().Duration.ScopeId);
            Assert.AreEqual(0, f.Relic().RemainingCharges); f.NextRound(); Assert.IsEmpty(f.State.Duel.Combat.Statuses);
        }

        [Test] public void OpeningStunStillAllowsPassiveEndTriggerAndExpiresAtFirstPlayerEnd()
        {
            var stun = BattleEffectSpec.ApplyStatus(StatusApplicationSpec.WithLifetime(RelicTriggerTestData.Stun,
                StatusLifetimeSpec.ThroughNextPhase(BattleStatusTiming.PlayerPhase)));
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary("stun", effects: new[] { RelicTriggerTestData.Response(stun) }),
                RelicBoundaryTestData.Boundary("end", RelicBoundaryTiming.PhaseEnding) }) }, catalog: RelicTriggerTestData.Catalog());
            Assert.IsTrue(f.State.Duel.IsActionBlocked(Owner));
            var result = f.Act(CardDuelAction.SkipPlayerTurn);
            CollectionAssert.AreEqual(new[] { "end" }, result.RelicActivations.Select(x => x.TriggerId));
            Assert.IsEmpty(f.State.Duel.Combat.Statuses); Assert.AreEqual(ScoredDuelPhase.AwaitingEnemyAction, f.State.Duel.Phase);
        }

        [Test] public void EndBoundaryReadsPostPoisonHealthBeforeExpiryAndNewPoisonWaitsUntilNextEnd()
        {
            var poison = new StatusDefinitionId("boundary-poison");
            var catalog = new CombatStatusCatalog("boundary-poison.1", new[] { new CombatStatusDefinition(poison, StatusTags.None,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.PhaseEnding, 10) }, StatusLifetimeSpec.ThroughCurrentRound()) });
            var apply = RelicTriggerTestData.Response(RelicTriggerTestData.Status(poison));
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary("open", effects: new[] { apply }),
                RelicBoundaryTestData.Boundary("heal", RelicBoundaryTiming.PhaseEnding, effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Heal(4)), apply },
                    conditions: new[] { RelicTriggerConditionSpec.OwnerHealthAtMost(.5m), RelicTriggerConditionSpec.OwnerHasStatus(poison) }) }) },
                ownerHealth: 505, catalog: catalog);
            var end = f.Act(CardDuelAction.EndTurn);
            Assert.AreEqual(499, f.State.Duel.Combat.Get(Owner).Health);
            Assert.AreEqual(1, end.Events.Count(x => x.Provenance.Origin == CombatEffectOrigin.Periodic));
            Assert.AreEqual(1, end.RelicActivations.Count);
            Assert.Less(end.Events.First().SequenceIndex, end.Events.Last().SequenceIndex);
            f.Act(CardDuelAction.EnemyAction); Assert.IsEmpty(f.State.Duel.Combat.Statuses);
        }

        [Test] public void EndShieldIsClearedBeforeNextPlayerStartShield()
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary("end", RelicBoundaryTiming.PhaseEnding, RelicPhaseRelation.OpponentTeam),
                RelicBoundaryTestData.Boundary("start", RelicBoundaryTiming.PhaseStarted) }) }, expiry: PlayerBlockExpiry.AfterEnemyPhase);
            f.Act(CardDuelAction.EndTurn); var result = f.Act(CardDuelAction.EnemyAction);
            CollectionAssert.AreEqual(new[] { CombatEffectKind.Damage, CombatEffectKind.GainBlock, CombatEffectKind.ClearBlock, CombatEffectKind.GainBlock }, result.Events.Select(x => x.Effect.Kind));
            Assert.AreEqual(3, f.State.Duel.Combat.Get(Owner).Block);
        }

        [Test] public void LethalOpeningFinishesItsBatchAndSkipsFirstPhaseAndDeal()
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary("kill", effects: new[] {
                    RelicTriggerTestData.Response(BattleEffectSpec.Eliminate(), CombatTargetSpec.Opponents()),
                    RelicTriggerTestData.Response(BattleEffectSpec.Heal(4)) }),
                RelicBoundaryTestData.Boundary("phase", RelicBoundaryTiming.PhaseStarted) }) }, start: false);
            var result = f.Act(CardDuelAction.Start);
            Assert.AreEqual(ScoredDuelPhase.Won, f.State.Duel.Phase); Assert.AreEqual(604, f.State.Duel.Combat.Get(Owner).Health);
            Assert.IsEmpty(result.Draws); Assert.AreEqual(1, result.RelicActivations.Count);
            Assert.AreEqual(1, result.RuleEvents.OfType<CombatTimingEvent>().Count());
        }

        [Test] public void EnemyStartDeathRebuildsPendingQueueFromSurvivors()
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary(timing: RelicBoundaryTiming.PhaseStarted, phase: RelicPhaseRelation.OpponentTeam,
                    effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Eliminate(), CombatTargetSpec.Opponents()) }) }, charges: 1) }, otherEnemy: true);
            f.Act(CardDuelAction.EndTurn);
            Assert.IsFalse(f.State.Duel.Combat.Get(Enemy).IsAlive); Assert.AreEqual(RelicTriggerTestData.Other, f.State.Duel.NextEnemy);
            f.Act(CardDuelAction.EnemyAction); Assert.AreEqual(1, f.State.Duel.CompletedRounds);
        }

        [TestCase(false, 0)] [TestCase(true, 1)]
        public void DefeatedOwnerPolicyIsExplicitAndNeverRevivesDeadTargets(bool allowed, int expected)
        {
            var root = RelicBoundaryTestData.Boundary(timing: RelicBoundaryTiming.PhaseEnding, phase: RelicPhaseRelation.OpponentTeam,
                source: allowed ? CombatReactionSourcePolicy.AllowDefeated : CombatReactionSourcePolicy.RequireLiving,
                effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Heal(10), CombatTargetSpec.Allies(CombatTargetSelection.AllLiving)) });
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] { root }) }, ownerHealth: 1, allyGear: Array.Empty<BattleRelicSetup>());
            f.Act(CardDuelAction.EndTurn); f.Act(CardDuelAction.EndTurn, actor: Ally);
            var result = f.Act(CardDuelAction.EnemyAction);
            Assert.AreEqual(expected, result.RelicActivations.Count); Assert.AreEqual(0, f.State.Duel.Combat.Get(Owner).Health);
        }

        [Test] public void ZeroBoundaryEffectsDoNotPayAndLaterDeadTargetsSkipWithoutRefundOrRetarget()
        {
            var roots = new[] {
                RelicBoundaryTestData.Boundary("zero", priority: -2, effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(0)) }),
                RelicBoundaryTestData.Boundary("kill", priority: -1, effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Eliminate(), CombatTargetSpec.Opponents()) }),
                RelicBoundaryTestData.Boundary("later", effects: new[] {
                    RelicTriggerTestData.Response(BattleEffectSpec.Damage(5), CombatTargetSpec.Opponents()), RelicTriggerTestData.Response(BattleEffectSpec.Heal(4)) }) };
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(roots) }, otherEnemy: true, start: false);
            var result = f.Act(CardDuelAction.Start);
            CollectionAssert.AreEqual(new[] { "kill", "later" }, result.RelicActivations.Select(x => x.TriggerId));
            Assert.AreEqual(18, f.Relic().RemainingCharges); Assert.AreEqual(604, f.State.Duel.Combat.Get(Owner).Health);
            Assert.AreEqual(10000, f.State.Duel.Combat.Get(RelicTriggerTestData.Other).Health);
            Assert.IsFalse(result.Events.Any(x => x.Effect.Kind == CombatEffectKind.Damage));
        }

        [Test] public void BoundaryAmountsUseTriggeredOutgoingAndIncomingModifiersWithoutHandFacts()
        {
            var modifiers = new[] {
                new RelicNumericModifierSpec("out", BattleNumericStat.HealingDone, RelicNumericOperation.AddFlat, 3, new[] { CombatEffectOrigin.Triggered }),
                new RelicNumericModifierSpec("in", BattleNumericStat.HealingReceived, RelicNumericOperation.AddRate, .5m, new[] { CombatEffectOrigin.Triggered }),
                new RelicNumericModifierSpec("hand", BattleNumericStat.HealingDone, RelicNumericOperation.AddFlat, 100, new[] { CombatEffectOrigin.Primary }, HandCategory.HighCard) };
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] { RelicBoundaryTestData.Boundary(effects: new[] {
                RelicTriggerTestData.Response(BattleEffectSpec.Heal(7)) }) }, modifiers: modifiers) }, start: false);
            var result = f.Act(CardDuelAction.Start); Assert.AreEqual(615, f.State.Duel.Combat.Get(Owner).Health);
            Assert.AreEqual(15, result.Events.Single().SettledAmount); Assert.AreEqual(CombatEffectOrigin.Triggered, result.Events.Single().Provenance.Origin);
        }

        [TestCase("player", 1)] [TestCase("enemy", 0)]
        public void EnemyPhaseEntryUsesCurrentEnemyOrNextPlayerLifetime(string phase, int ahead)
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary(timing: RelicBoundaryTiming.PhaseStarted, phase: RelicPhaseRelation.OpponentTeam,
                    effects: new[] { RelicTriggerTestData.Response(RelicBoundaryTestData.Protection(phase)) }) }, charges: 1) }, catalog: RelicTriggerTestData.Catalog());
            string scope = BattleStatusTiming.RoundScope(f.State.Duel, ahead); f.Act(CardDuelAction.EndTurn);
            Assert.AreEqual(scope, f.State.Duel.Combat.Statuses.Single().Duration.ScopeId);
            f.Act(CardDuelAction.EnemyAction);
            if (ahead == 0) Assert.IsEmpty(f.State.Duel.Combat.Statuses);
            else { Assert.AreEqual(1, f.State.Duel.Combat.Statuses.Count); f.Act(CardDuelAction.EndTurn); Assert.IsEmpty(f.State.Duel.Combat.Statuses); }
        }

        [TestCase(CardDuelAction.Play)] [TestCase(CardDuelAction.Discard)] [TestCase(CardDuelAction.EndTurn)]
        public void TerminalPhaseEndingSkipsEnemyStartAndSubsequentDeal(CardDuelAction action)
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary("kill", RelicBoundaryTiming.PhaseEnding, effects: new[] {
                    RelicTriggerTestData.Response(BattleEffectSpec.Eliminate(), CombatTargetSpec.Opponents()) }),
                RelicBoundaryTestData.Boundary("start", RelicBoundaryTiming.PhaseStarted, RelicPhaseRelation.OpponentTeam) }) }, new BattlePlayerRules(initialEnergy: 1));
            var result = f.Act(action, action == CardDuelAction.EndTurn ? 0 : 1);
            Assert.AreEqual(ScoredDuelPhase.Won, f.State.Duel.Phase); Assert.IsEmpty(result.Draws);
            CollectionAssert.AreEqual(new[] { "kill" }, result.RelicActivations.Select(x => x.TriggerId));
            Assert.IsEmpty(f.State.Duel.PendingEnemies);
        }

        [Test] public void NextPlayerStartCanEndBattleBeforeRefillAfterRoundMaintenance()
        {
            var spec = RelicBoundaryTestData.Boundary(timing: RelicBoundaryTiming.PhaseStarted, conditions: new[] { RelicTriggerConditionSpec.OwnerHealthAtMost(.599m) },
                effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Eliminate(), CombatTargetSpec.Opponents()) });
            var f = new RelicTriggerTestData.Fixture(new[] { RelicBoundaryTestData.Gear(new[] { spec }) });
            f.Act(CardDuelAction.EndTurn); var result = f.Act(CardDuelAction.EnemyAction);
            Assert.AreEqual(ScoredDuelPhase.Won, f.State.Duel.Phase); Assert.IsEmpty(result.Draws);
            Assert.AreEqual(1, f.Relic().UsesThisRound); Assert.AreEqual(19, f.Relic().RemainingCharges);
        }

        [Test] public void RoundLimitStillRunsEndBoundaryButNeverStartsAnotherPhase()
        {
            var gear = RelicBoundaryTestData.Gear(new[] {
                RelicBoundaryTestData.Boundary("end", RelicBoundaryTiming.PhaseEnding, RelicPhaseRelation.OpponentTeam),
                RelicBoundaryTestData.Boundary("start", RelicBoundaryTiming.PhaseStarted) });
            var engine = new CardDuelEngine(new BattleRules("limit", 1));
            var state = engine.Create("limit", HmacDeckShuffler.FromSeed(new byte[32]),
                new[] { new BattlePlayerSetup(Owner, new CombatantDefinition("hero", 100), relics: new[] { gear }) },
                new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("enemy", 100), 1) });
            state = engine.Execute(state, state.Revision, CardDuelAction.Start).State;
            state = engine.Execute(state, state.Revision, CardDuelAction.EndTurn).State;
            var result = engine.Execute(state, state.Revision, CardDuelAction.EnemyAction);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(ScoredDuelPhase.Lost, result.State.Duel.Phase);
            CollectionAssert.AreEqual(new[] { "end" }, result.RelicActivations.Select(x => x.TriggerId));
            Assert.IsEmpty(result.Draws); Assert.AreEqual(2, result.State.Duel.Player(Owner).Relics.Relics.Single().UsesThisRound);
        }
    }
}
