using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    internal static class RelicActionTestData
    {
        internal static RelicActionTriggerSpec Action(string id = "completed", RelicActionTiming timing = RelicActionTiming.PlayCompleted,
            RelicTriggeredEffectSpec[] effects = null, RelicActionRelation relation = RelicActionRelation.OwnerIsActor,
            RelicTriggerConditionSpec[] conditions = null, bool chain = false, int priority = 0,
            CombatReactionSourcePolicy source = CombatReactionSourcePolicy.RequireLiving) =>
            new RelicActionTriggerSpec(id, timing, effects ?? new[] { RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(3)) },
                relation, conditions, priority, chain, source);
        internal static BattleRelicSetup Gear(RelicActionTriggerSpec[] actions = null, RelicTriggerBudgetSpec budget = null,
            RelicTriggerSpec[] facts = null, RelicBoundaryTriggerSpec[] boundaries = null, int? charges = 20, string id = "gear") =>
            new BattleRelicSetup(new RelicDefinition("def/" + id, Array.Empty<ScoreEffect>(), charges: charges),
                new RunRelic(id, "def/" + id, charges: charges),
                new BattleRelicRules("actions.1", triggers: facts, triggerBudget: budget, boundaryTriggers: boundaries,
                    actionTriggers: actions ?? new[] { Action() }));
    }

    [TestFixture]
    public sealed class RelicActionBattleTests
    {
        private static readonly ParticipantId Owner = RelicTriggerTestData.Owner, Ally = RelicTriggerTestData.Ally;

        [Test] public void MultiHitAoeAndMultipleCardsEmitOneCompletionWithAuthorityFactsAndPaidEnergy()
        {
            var rules = new BattlePlayerRules(effectRule: new RelicTriggerTestData.Sequence(RelicTriggerTestData.Hit(), RelicTriggerTestData.Hit()));
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear() }, rules, otherEnemy: true);
            var before = f.State; var ids = f.Cards(3); var result = f.Act(CardDuelAction.Play, 3);
            var completion = result.ActionCompletion; var context = completion.Context;
            Assert.AreEqual(RelicActionTiming.PlayCompleted, context.Timing); Assert.AreEqual(Owner, context.Actor);
            Assert.AreEqual(3, context.SelectedCardCount); CollectionAssert.AreEquivalent(ids, context.Hand.SubmittedCards.Select(x => x.Id));
            Assert.IsNull(context.DiscardedCards); Assert.AreSame(result.Score, result.State.Duel.Scores.Last());
            Assert.AreEqual(BattleStatusTiming.RoundScope(before.Duel), context.RoundScope); Assert.AreEqual(before.Duel.Phase, context.Phase);
            Assert.AreEqual(3, context.EnergyBefore); Assert.AreEqual(2, context.EnergyAfter); Assert.AreEqual(1, context.ActionCost.Cost);
            Assert.AreEqual(BattleStatusTiming.ActionCompleted(before.Duel, Owner, ScoredDuelActionKind.ScoredHand).BoundaryId, context.ActionId);
            Assert.AreEqual(1, result.RelicActivations.Count); Assert.AreEqual(4, result.Events.Count(x => x.Effect.Kind == CombatEffectKind.Damage));
            var activation = result.RelicActivations.Single(); var child = result.Events.Last();
            Assert.AreEqual(RelicActionTiming.PlayCompleted, activation.ActionTiming); Assert.IsNull(activation.Timing); Assert.IsNull(activation.BoundaryTiming);
            Assert.AreEqual(context.ActionId, activation.ActionId); Assert.IsNull(activation.BoundaryId);
            Assert.AreEqual(completion.Clock.Revision, activation.ParentRevision); Assert.AreEqual(completion.Clock.SequenceIndex, activation.ParentSequenceIndex);
            Assert.AreEqual(activation.ActivationId, child.Provenance.ActivationId); Assert.AreEqual(CombatEffectOrigin.Triggered, child.Provenance.Origin);
            Assert.AreEqual(-1, child.Provenance.RootEffectIndex); Assert.AreEqual(1, child.Provenance.Depth);
            Assert.AreEqual(19, f.Relic().RemainingCharges); Assert.AreEqual(20, before.Duel.Player(Owner).Relics.Relics.Single().RemainingCharges);
        }

        [Test] public void DefaultEmptyDiscardStillCompletesOnceAndKeepsOriginalEnergyBudgetAndRefill()
        {
            var spec = RelicActionTestData.Action(timing: RelicActionTiming.DiscardCompleted,
                conditions: new[] { RelicTriggerConditionSpec.DiscardedCardsAtLeast(2) },
                effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Heal(4), CombatTargetSpec.EventActor()) });
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] { spec }) });
            var cards = f.Cards(2); var result = f.Act(CardDuelAction.Discard, 2);
            Assert.IsEmpty(result.EffectPlan.Effects); Assert.IsNull(result.ActionCompletion.Context.Hand);
            CollectionAssert.AreEquivalent(cards, result.ActionCompletion.Context.DiscardedCards.Select(x => x.Id));
            Assert.AreEqual(2, result.ActionCompletion.Context.SelectedCardCount); Assert.AreEqual(604, f.State.Duel.Combat.Get(Owner).Health);
            Assert.AreEqual(2, f.State.Duel.PlayerTurn.Energy); Assert.AreEqual(2, f.State.DiscardsRemaining);
            Assert.AreEqual(8, f.State.Zone(CardZoneKinds.Hand).Cards.Count); Assert.AreEqual(1, result.RelicActivations.Count);
            Assert.IsNotEmpty(result.Draws);
        }

        [TestCase(RelicActionRelation.OwnerIsActor, 0)] [TestCase(RelicActionRelation.AllyIsActor, 1)]
        [TestCase(RelicActionRelation.OpponentIsActor, 0)] [TestCase(RelicActionRelation.Any, 1)]
        public void EventActorIsTheActingAllyWhileSelfAndEffectSourceRemainTheRelicOwner(RelicActionRelation relation, int expected)
        {
            var action = RelicActionTestData.Action(relation: relation, effects: new[] {
                RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(4), CombatTargetSpec.EventActor()),
                RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(2)) });
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] { action }) }, allyGear: Array.Empty<BattleRelicSetup>());
            var result = f.Act(CardDuelAction.Play, 1, Ally);
            Assert.AreEqual(expected, result.RelicActivations.Count); Assert.AreEqual(4 * expected, f.State.Duel.Combat.Get(Ally).Block);
            Assert.AreEqual(2 * expected, f.State.Duel.Combat.Get(Owner).Block);
            if (expected == 1)
            {
                Assert.AreEqual(Ally, result.ActionCompletion.Context.Actor);
                Assert.IsTrue(result.RelicActivations.Single().Batch.Reactions.All(x => x.Effect.Source == Owner));
            }
        }

        [TestCase(1, 0)] [TestCase(2, 1)]
        public void PrimaryFactsReserveSharedActionBudgetBeforeCompletionRegardlessOfCompletionPriority(int limit, int completed)
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] { RelicActionTestData.Action(priority: -100) },
                new RelicTriggerBudgetSpec(maxUsesPerAction: limit), new[] { RelicTriggerTestData.Trigger() }) });
            var result = f.Act(CardDuelAction.Play, 1);
            Assert.AreEqual(1 + completed, result.RelicActivations.Count); Assert.AreEqual("hit", result.RelicActivations[0].TriggerId);
            Assert.AreEqual(19 - completed, f.Relic().RemainingCharges);
        }

        [TestCase(false, 1)] [TestCase(true, 2)]
        public void CompletionDescendantFactsShareActionOccurrenceAndBudgetWithoutBorrowingHandFacts(bool chain, int expected)
        {
            var root = RelicActionTestData.Action(chain: chain, effects: new[] {
                RelicTriggerTestData.Response(BattleEffectSpec.Damage(2), CombatTargetSpec.Opponents(CombatTargetSelection.AllLiving)) });
            var facts = new[] {
                RelicTriggerTestData.Trigger("once", occurrence: RelicTriggerOccurrence.PerAction, origins: new[] { CombatEffectOrigin.Primary, CombatEffectOrigin.Triggered }),
                RelicTriggerTestData.Trigger("child", origins: new[] { CombatEffectOrigin.Triggered }, occurrence: RelicTriggerOccurrence.PerAction),
                RelicTriggerTestData.Trigger("boundary", origins: new[] { CombatEffectOrigin.Triggered }, occurrence: RelicTriggerOccurrence.PerBoundary),
                RelicTriggerTestData.Trigger("hand", origins: new[] { CombatEffectOrigin.Triggered }, conditions: new[] { RelicTriggerConditionSpec.SubmittedCardsAtLeast(1) }) };
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] { root }, new RelicTriggerBudgetSpec(maxUsesPerAction: 3), facts) }, otherEnemy: true);
            var result = f.Act(CardDuelAction.Play, 1);
            CollectionAssert.AreEqual(chain ? new[] { "once", "completed", "child" } : new[] { "once", "completed" }, result.RelicActivations.Select(x => x.TriggerId));
            Assert.AreEqual(expected + 1, result.RelicActivations.Count);
            Assert.IsTrue(result.RelicActivations.Skip(1).All(x => x.ActionId == result.ActionCompletion.Context.ActionId && x.BoundaryId == null));
        }

        [Test] public void CompletionAndPhaseEndHaveIndependentWindowsButOnePersistentBudget()
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(budget: new RelicTriggerBudgetSpec(maxUsesPerAction: 1, maxUsesPerBoundary: 1),
                boundaries: new[] { RelicBoundaryTestData.Boundary(timing: RelicBoundaryTiming.PhaseEnding) }) }, new BattlePlayerRules(initialEnergy: 1));
            var result = f.Act(CardDuelAction.Play, 1);
            CollectionAssert.AreEqual(new[] { "completed", "boundary" }, result.RelicActivations.Select(x => x.TriggerId));
            Assert.AreEqual(18, f.Relic().RemainingCharges); Assert.AreEqual(2, f.Relic().UsesThisRound);
        }

        [Test] public void FreePlayAndDiscardShareCooldownWhichOnlyAdvancesAtNextPlayerRound()
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] {
                RelicActionTestData.Action(), RelicActionTestData.Action("discard", RelicActionTiming.DiscardCompleted) },
                new RelicTriggerBudgetSpec(cooldownRounds: 1)) }, new BattlePlayerRules(initialEnergy: 0, playEnergyCost: 0, discardEnergyCost: 0));
            var first = f.Act(CardDuelAction.Play, 1); Assert.AreEqual(0, first.ActionCompletion.Context.ActionCost.Cost);
            f.Act(CardDuelAction.CollectPlayed); Assert.IsEmpty(f.Act(CardDuelAction.Discard, 1).RelicActivations);
            Assert.AreEqual(2, f.Relic().CooldownRounds); Assert.AreEqual(ScoredDuelPhase.AwaitingPlayerHand, f.State.Duel.Phase);
            f.NextRound(); Assert.AreEqual(1, f.Relic().CooldownRounds); Assert.AreEqual(0, f.Relic().UsesThisRound);
            Assert.IsEmpty(f.Act(CardDuelAction.Discard, 1).RelicActivations);
            f.NextRound(); Assert.AreEqual(0, f.Relic().CooldownRounds); Assert.AreEqual(1, f.Act(CardDuelAction.Discard, 1).RelicActivations.Count);
        }

        [Test] public void SharedRoundBudgetResetsOnceAndCompletionPriorityIsStable()
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] {
                RelicActionTestData.Action("z"), RelicActionTestData.Action("a"), RelicActionTestData.Action("first", priority: -1) },
                new RelicTriggerBudgetSpec(maxUsesPerRound: 2)) });
            CollectionAssert.AreEqual(new[] { "first", "a" }, f.Act(CardDuelAction.Play, 1).RelicActivations.Select(x => x.TriggerId));
            f.Act(CardDuelAction.CollectPlayed); Assert.IsEmpty(f.Act(CardDuelAction.Play, 1).RelicActivations); f.Act(CardDuelAction.CollectPlayed);
            f.NextRound(); Assert.AreEqual(0, f.Relic().UsesThisRound); Assert.AreEqual(2, f.Act(CardDuelAction.Play, 1).RelicActivations.Count);
        }

        [TestCase("player", 1)] [TestCase("enemy", 0)]
        public void CompletionStatusUsesActionScopeAndExpiresAtTheUpcomingCompletePhase(string phase, int ahead)
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] {
                RelicActionTestData.Action(effects: new[] { RelicTriggerTestData.Response(RelicBoundaryTestData.Protection(phase)) }) }, charges: 1) },
                new BattlePlayerRules(initialEnergy: 1), catalog: RelicTriggerTestData.Catalog());
            string scope = BattleStatusTiming.RoundScope(f.State.Duel, ahead);
            f.Act(CardDuelAction.Play, 1); var status = f.State.Duel.Combat.Statuses.Single();
            Assert.AreEqual(scope, status.Duration.ScopeId); Assert.AreEqual(phase, status.Duration.PhaseKey);
            f.Act(CardDuelAction.EnemyAction);
            if (ahead == 0) Assert.IsEmpty(f.State.Duel.Combat.Statuses);
            else { Assert.AreEqual(1, f.State.Duel.Combat.Statuses.Count); f.Act(CardDuelAction.EndTurn); Assert.IsEmpty(f.State.Duel.Combat.Statuses); }
        }

        [Test] public void BleedRunsAndConsumesBeforeCompletionAndNewBleedWaitsForNextAction()
        {
            var bleed = new StatusDefinitionId("completion-bleed");
            var catalog = new CombatStatusCatalog("bleed.1", new[] { new CombatStatusDefinition(bleed, StatusTags.None,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.ActionCompleted, 10) }, StatusLifetimeSpec.ForTriggers(1)) });
            var apply = RelicTriggerTestData.Response(RelicTriggerTestData.Status(bleed));
            var action = RelicActionTestData.Action(timing: RelicActionTiming.DiscardCompleted, effects: new[] {
                RelicTriggerTestData.Response(BattleEffectSpec.Heal(4)), apply }, conditions: new[] { RelicTriggerConditionSpec.OwnerHealthAtMost(.5m) });
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] { action }, boundaries: new[] {
                RelicBoundaryTestData.Boundary(effects: new[] { apply }) }) }, ownerHealth: 505, catalog: catalog);
            var result = f.Act(CardDuelAction.Discard, 1);
            Assert.AreEqual(499, f.State.Duel.Combat.Get(Owner).Health); Assert.AreEqual(1, f.State.Duel.Combat.Statuses.Count);
            Assert.AreEqual(1, result.Events.Count(x => x.Provenance.Origin == CombatEffectOrigin.Periodic));
            Assert.AreEqual(CombatEffectOrigin.Periodic, result.Events.First().Provenance.Origin);
            Assert.AreEqual(CombatEffectKind.Heal, result.Events.Last().Effect.Kind);
            Assert.AreEqual(1, result.RuleEvents.OfType<CombatTimingEvent>().Count());
            f.Act(CardDuelAction.Discard, 1); Assert.AreEqual(493, f.State.Duel.Combat.Get(Owner).Health);
        }

        [TestCase(false)] [TestCase(true)]
        public void TerminalPrimaryOrBleedDoesNotStartCompletionOrSpendItsCharges(bool bleedDeath)
        {
            var bleed = new StatusDefinitionId("lethal-bleed");
            var catalog = new CombatStatusCatalog("bleed.1", new[] { new CombatStatusDefinition(bleed, StatusTags.None,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.ActionCompleted, 10) }, StatusLifetimeSpec.ForTriggers(1)) });
            var gear = RelicActionTestData.Gear(boundaries: bleedDeath ? new[] { RelicBoundaryTestData.Boundary(effects: new[] {
                RelicTriggerTestData.Response(RelicTriggerTestData.Status(bleed)) }) } : null);
            var f = new RelicTriggerTestData.Fixture(new[] { gear }, ownerHealth: bleedDeath ? 5 : 600, enemyHealth: bleedDeath ? 10000 : 1, catalog: catalog);
            var result = f.Act(CardDuelAction.Play, 1);
            Assert.IsTrue(f.State.Duel.IsFinished); Assert.IsEmpty(result.RelicActivations); Assert.IsNull(result.ActionCompletion);
            Assert.AreEqual(bleedDeath ? 19 : 20, f.Relic().RemainingCharges);
        }

        [Test] public void CompletionKillFinishesAlreadyQueuedEffectsAndSkipsLaterBoundariesAndDiscardRefill()
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] {
                RelicActionTestData.Action(timing: RelicActionTiming.DiscardCompleted, effects: new[] {
                    RelicTriggerTestData.Response(BattleEffectSpec.Eliminate(), CombatTargetSpec.Opponents()),
                    RelicTriggerTestData.Response(BattleEffectSpec.Heal(4)) }) }, boundaries: new[] {
                        RelicBoundaryTestData.Boundary(timing: RelicBoundaryTiming.PhaseEnding) }) }, new BattlePlayerRules(initialEnergy: 1));
            var result = f.Act(CardDuelAction.Discard, 1);
            Assert.AreEqual(ScoredDuelPhase.Won, f.State.Duel.Phase); Assert.AreEqual(604, f.State.Duel.Combat.Get(Owner).Health);
            Assert.AreEqual(1, result.RelicActivations.Count); Assert.IsEmpty(result.Draws); Assert.AreEqual(2, f.State.DiscardsRemaining);
        }

        [Test] public void SkipEndCollectAndEnemyActionNeverCountAsPlayerCompletion()
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] {
                RelicActionTestData.Action(), RelicActionTestData.Action("discard", RelicActionTiming.DiscardCompleted) }) });
            Assert.IsEmpty(f.Act(CardDuelAction.EndTurn).RelicActivations); Assert.IsEmpty(f.Act(CardDuelAction.EnemyAction).RelicActivations);
            f.Act(CardDuelAction.Play, 1); Assert.IsEmpty(f.Act(CardDuelAction.CollectPlayed).RelicActivations);
            var stunned = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(boundaries: new[] {
                RelicBoundaryTestData.Boundary(effects: new[] { RelicTriggerTestData.Response(RelicTriggerTestData.Status(RelicTriggerTestData.Stun)) }) }) },
                catalog: RelicTriggerTestData.Catalog());
            Assert.IsEmpty(stunned.Act(CardDuelAction.SkipPlayerTurn).RelicActivations);
        }

        [Test] public void CardConditionsUseClassifiedAuthorityHandWhileLegacyTeamCallsKeepCardFactsUnknown()
        {
            var root = RelicActionTestData.Action(conditions: new[] {
                RelicTriggerConditionSpec.HandIs(HandCategory.HighCard), RelicTriggerConditionSpec.SubmittedCardsAtLeast(1) });
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] { root }) });
            var before = f.State; var score = f.Engine.PreviewHand(before, Owner, f.Cards()).Score;
            var legacy = f.Engine.Duel.Execute(before.Duel, before.Duel.Revision, Owner, ScoredDuelActionKind.ScoredHand, score);
            Assert.IsTrue(legacy.Accepted); Assert.IsEmpty(legacy.RelicActivations);
            Assert.IsNull(legacy.ActionCompletion.Context.Hand); Assert.IsNull(legacy.ActionCompletion.Context.SelectedCardCount);
            Assert.AreEqual(1, f.Act(CardDuelAction.Play, 1).RelicActivations.Count);
        }

        [TestCase(0)] [TestCase(2)]
        public void ZeroEffectsOrFailedCardConditionsDoNotConsumeCharges(int threshold)
        {
            var root = RelicActionTestData.Action(effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(threshold == 0 ? 0 : 3)) },
                conditions: threshold == 0 ? null : new[] { RelicTriggerConditionSpec.SubmittedCardsAtLeast(threshold) });
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] { root }) });
            Assert.IsEmpty(f.Act(CardDuelAction.Play, 1).RelicActivations); Assert.AreEqual(20, f.Relic().RemainingCharges);
        }

        [TestCase(false)] [TestCase(true)]
        public void DeadEventActorIsSkippedAndDefeatedRelicOwnerRequiresExplicitSourcePolicy(bool allowed)
        {
            var bleed = new StatusDefinitionId("actor-bleed");
            var catalog = new CombatStatusCatalog("bleed.1", new[] { new CombatStatusDefinition(bleed, StatusTags.None,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.ActionCompleted, 10) }, StatusLifetimeSpec.ForTriggers(1)) });
            var roots = new[] {
                RelicActionTestData.Action("dead-actor", effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.Heal(3), CombatTargetSpec.EventActor()) },
                    source: CombatReactionSourcePolicy.AllowDefeated),
                RelicActionTestData.Action("survivor", effects: new[] { RelicTriggerTestData.Response(BattleEffectSpec.GainBlock(3), CombatTargetSpec.Allies()) },
                    source: allowed ? CombatReactionSourcePolicy.AllowDefeated : CombatReactionSourcePolicy.RequireLiving) };
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(roots, boundaries: new[] { RelicBoundaryTestData.Boundary(effects: new[] {
                RelicTriggerTestData.Response(RelicTriggerTestData.Status(bleed)) }) }) }, ownerHealth: 5, allyGear: Array.Empty<BattleRelicSetup>(), catalog: catalog);
            var result = f.Act(CardDuelAction.Play, 1);
            Assert.IsFalse(f.State.Duel.Combat.Get(Owner).IsAlive); Assert.IsFalse(f.State.Duel.IsFinished);
            Assert.AreEqual(allowed ? 1 : 0, result.RelicActivations.Count); Assert.AreEqual(allowed ? 3 : 0, f.State.Duel.Combat.Get(Ally).Block);
            Assert.AreEqual(allowed ? 18 : 19, f.Relic().RemainingCharges);
        }

        [Test] public void DescendantStatusSharesActionRoundScopeAndExpiresAtRoundEnd()
        {
            var status = BattleEffectSpec.ApplyStatus(StatusApplicationSpec.WithLifetime(RelicTriggerTestData.Immunity, StatusLifetimeSpec.ThroughCurrentRound()));
            var fact = RelicTriggerTestData.Trigger(timing: RelicTriggerTiming.BlockGained, origins: new[] { CombatEffectOrigin.Triggered },
                effects: new[] { RelicTriggerTestData.Response(status) });
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] { RelicActionTestData.Action(chain: true) }, facts: new[] { fact }) },
                catalog: RelicTriggerTestData.Catalog());
            string scope = BattleStatusTiming.RoundScope(f.State.Duel); var result = f.Act(CardDuelAction.Play, 1);
            Assert.AreEqual(2, result.RelicActivations.Count); Assert.AreEqual(scope, f.State.Duel.Combat.Statuses.Single().Duration.ScopeId);
            f.Act(CardDuelAction.CollectPlayed); f.NextRound(); Assert.IsEmpty(f.State.Duel.Combat.Statuses);
        }

        [Test] public void CompletionCannotObserveABleedStatusWhoseLastUseWasConsumed()
        {
            var bleed = new StatusDefinitionId("consumed-bleed");
            var catalog = new CombatStatusCatalog("bleed.1", new[] { new CombatStatusDefinition(bleed, StatusTags.None,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.ActionCompleted, 1) }, StatusLifetimeSpec.ForTriggers(1)) });
            var f = new RelicTriggerTestData.Fixture(new[] { RelicActionTestData.Gear(new[] {
                RelicActionTestData.Action(conditions: new[] { RelicTriggerConditionSpec.OwnerHasStatus(bleed) }) }, boundaries: new[] {
                    RelicBoundaryTestData.Boundary(effects: new[] { RelicTriggerTestData.Response(RelicTriggerTestData.Status(bleed)) }) }) }, catalog: catalog);
            Assert.IsEmpty(f.Act(CardDuelAction.Play, 1).RelicActivations); Assert.IsEmpty(f.State.Duel.Combat.Statuses);
            Assert.AreEqual(19, f.Relic().RemainingCharges);
        }
    }
}
