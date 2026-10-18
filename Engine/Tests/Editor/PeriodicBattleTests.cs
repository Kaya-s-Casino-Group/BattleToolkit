using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class PeriodicBattleTests
    {
        private static readonly ParticipantId Player = new ParticipantId("periodic-player"), Ally = new ParticipantId("periodic-ally"),
            Enemy = new ParticipantId("periodic-enemy"), Other = new ParticipantId("periodic-other");
        private static readonly StatusDefinitionId Bleed = new StatusDefinitionId("periodic-bleed"), Poison = new StatusDefinitionId("periodic-poison"),
            Stun = new StatusDefinitionId("periodic-stun");
        private sealed class PlayerRule : IScoredHandEffectRule
        {
            private readonly BattleEffectStep[] steps;
            public string Version => "periodic-player.1";
            public PlayerRule(params BattleEffectStep[] steps) { this.steps = steps; }
            public BattleEffectSequence Resolve(ScoredHandEffectContext context) => new BattleEffectSequence(Version, "periodic test plan", steps);
        }
        private sealed class EnemyRule : IEnemyBattleEffectRule
        {
            private readonly BattleEffectStep[] steps;
            public string Version => "periodic-enemy.1";
            public EnemyRule(params BattleEffectStep[] steps) { this.steps = steps; }
            public BattleEffectSequence Resolve(EnemyBattleEffectContext context) => new BattleEffectSequence(Version, "periodic enemy plan", steps);
        }
        private static BattleEffectStep Self(BattleEffectSpec effect) => new BattleEffectStep(effect, EffectTargetSpec.Self());
        private static BattleEffectStep Hit(long damage) => new BattleEffectStep(BattleEffectSpec.Damage(damage), EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving));
        private static BattleEffectStep Apply(StatusDefinitionId id, EffectTargetSpec target = null, int stacks = 1) =>
            new BattleEffectStep(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(id, stacks)), target ?? EffectTargetSpec.Self());
        private static CombatStatusDefinition Periodic(StatusDefinitionId id, CombatTimingKind timing, long damage, int count = 3,
            CombatTargetSpec target = null, StatusLifetimeSpec lifetime = null) => new CombatStatusDefinition(id, StatusTags.None,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(timing, damage, target) }, lifetime ?? StatusLifetimeSpec.ForTriggers(count));
        private static CombatStatusDefinition Control() => new CombatStatusDefinition(Stun, StatusTags.Control,
            new StatusBehaviorSpec[] { new BlockActionWindowSpec() }, StatusLifetimeSpec.ForTriggers(1));
        private static CombatStatusCatalog Catalog(params CombatStatusDefinition[] definitions) => new CombatStatusCatalog("periodic-catalog.1", definitions);
        private static BattleSession Session(CombatStatusCatalog catalog, IScoredHandEffectRule rule = null, int energy = 3,
            int playerHealth = 100, int playerBlock = 0, int enemyHealth = 100, long enemyDamage = 0,
            IEnemyBattleEffectRule enemyRule = null, bool ally = false, bool otherEnemy = false,
            BattleActorDamageRules otherDamageRules = null, IDiscardRewardRule discardReward = null,
            PlayerBlockExpiry blockExpiry = PlayerBlockExpiry.Retain)
        {
            var engine = new CardDuelEngine(new BattleRules("periodic-battle.1", 20, blockExpiry, catalog));
            var rules = new BattlePlayerRules(maxEnergy: energy, initialEnergy: energy, effectRule: rule,
                discardReward: discardReward, discards: 5,
                dealPolicy: new CardDuelDealPolicy(CardDuelRefillTiming.AfterAction, CardDuelRefillTiming.AfterAction));
            var players = new List<BattlePlayerSetup> { new BattlePlayerSetup(Player, new CombatantDefinition("hero", 100, playerBlock),
                initialHealth: playerHealth, rules: rules) };
            if (ally) players.Add(new BattlePlayerSetup(Ally, new CombatantDefinition("ally", 100), rules: new BattlePlayerRules()));
            var enemies = new List<BattleEnemySetup> { new BattleEnemySetup(Enemy, new CombatantDefinition("foe", enemyHealth), enemyDamage, effectRule: enemyRule) };
            if (otherEnemy) enemies.Add(new BattleEnemySetup(Other, new CombatantDefinition("other", 100), 0, damageRules: otherDamageRules));
            var session = new BattleSession(engine, engine.Create("periodic-battle", HmacDeckShuffler.FromSeed(new byte[32]), players, enemies));
            Do(session, CardDuelAction.Start); return session;
        }
        private static CardInstanceId[] Card(CardDuelState state, ParticipantId actor) => state.Zone(CardZoneKinds.Hand, actor).Cards.Take(1).ToArray();
        private static BattleSessionResult Do(BattleSession session, CardDuelAction action, bool select = false)
        {
            var result = session.Execute(session.State.Revision, Player, action, select ? Card(session.State, Player) : null);
            Assert.IsTrue(result.Accepted, action + ": " + result.Error); result.State.Table.Validate(); return result;
        }
        private static CardDuelTransition Step(CardDuelEngine engine, ref CardDuelState state, ParticipantId actor, CardDuelAction action, bool select = false)
        {
            var result = engine.Execute(state, state.Revision, actor, action, select ? Card(state, actor) : null);
            Assert.IsTrue(result.Accepted, action + ": " + result.Error); state = result.State; state.Table.Validate(); return result;
        }
        private static IEnumerable<CombatEvent> Ticks(CardDuelTransition result) => result.Events.Where(item => item.Provenance.Origin == CombatEffectOrigin.Periodic);
        private static StatusInstance Status(CardDuelState state, StatusDefinitionId id, ParticipantId target) =>
            state.Duel.Combat.Statuses.Single(item => item.DefinitionId == id && item.Target.Id == target.Value);
        private static void SameState(CardDuelState expected, CardDuelState actual)
        {
            Assert.AreEqual(expected.Revision, actual.Revision); Assert.AreEqual(expected.Duel.Revision, actual.Duel.Revision);
            Assert.AreEqual(expected.Duel.Combat.Revision, actual.Duel.Combat.Revision); Assert.AreEqual(expected.Duel.Phase, actual.Duel.Phase);
            Assert.AreEqual(expected.Duel.CompletedRounds, actual.Duel.CompletedRounds);
            CollectionAssert.AreEqual(expected.Random.StateBytes, actual.Random.StateBytes);
            CollectionAssert.AreEqual(expected.Duel.Scores.Select(item => item.Total), actual.Duel.Scores.Select(item => item.Total));
            CollectionAssert.AreEqual(expected.Duel.PendingEnemies, actual.Duel.PendingEnemies);
            CollectionAssert.AreEqual(expected.Duel.Combat.Statuses.Select(item => item.Id), actual.Duel.Combat.Statuses.Select(item => item.Id));
            foreach (var status in expected.Duel.Combat.Statuses)
            {
                var other = actual.Duel.Combat.Statuses.Single(item => item.Id == status.Id);
                Assert.AreEqual(status.Stacks, other.Stacks); Assert.AreEqual(status.Duration.RemainingTriggers, other.Duration.RemainingTriggers);
            }
            foreach (var unit in expected.Duel.Combat.Combatants)
            {
                Assert.AreEqual(unit.Health, actual.Duel.Combat.Get(unit.Id).Health); Assert.AreEqual(unit.Block, actual.Duel.Combat.Get(unit.Id).Block);
            }
            foreach (var player in expected.Duel.Players)
            {
                Assert.AreEqual(player.Energy.Energy, actual.Duel.Player(player.Id).Energy.Energy);
                Assert.AreEqual(player.HasEnded, actual.Duel.Player(player.Id).HasEnded);
                Assert.AreEqual(expected.DiscardsFor(player.Id), actual.DiscardsFor(player.Id));
            }
            foreach (var zone in expected.Table.Zones) CollectionAssert.AreEqual(zone.Cards, actual.Table.GetZone(zone.Id).Cards);
        }

        [Test] public void BleedingTicksOncePerPlayOrDiscardRegardlessOfDamageSegmentsAndAoe()
        {
            var session = Session(Catalog(Periodic(Bleed, CombatTimingKind.ActionCompleted, 3)),
                new PlayerRule(Apply(Bleed), Hit(2), Hit(4)), otherEnemy: true);
            var play = Do(session, CardDuelAction.Play, true).Steps[0].Transition;
            Assert.AreEqual(4, play.Events.Count(item => item.Provenance.Origin == CombatEffectOrigin.Primary));
            Assert.AreEqual(1, Ticks(play).Count()); Assert.AreEqual(97, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(2, Status(session.State, Bleed, Player).Duration.RemainingTriggers);
            var discard = Do(session, CardDuelAction.Discard, true).Steps[0].Transition;
            Assert.AreEqual(1, Ticks(discard).Count()); Assert.IsEmpty(discard.DiscardReward.Plan.Effects);
            Assert.AreEqual(94, session.State.Duel.Combat.Get(Player).Health); Assert.AreEqual(1, Status(session.State, Bleed, Player).Duration.RemainingTriggers);
            Assert.AreEqual(1, session.State.Duel.Player(Player).Energy.Energy); Assert.AreEqual(4, session.State.DiscardsFor(Player));
            var ended = Do(session, CardDuelAction.EndTurn);
            Assert.IsEmpty(ended.Steps.SelectMany(item => Ticks(item.Transition)));
            var last = Do(session, CardDuelAction.Discard, true).Steps[0].Transition;
            Assert.AreEqual(1, Ticks(last).Count()); Assert.IsEmpty(session.State.Duel.Combat.Statuses);
            Assert.AreEqual(91, session.State.Duel.Combat.Get(Player).Health);
        }

        [Test] public void InvalidCardSelectionDoesNotEmitAClockOrSpendBleedingDuration()
        {
            var session = Session(Catalog(Periodic(Bleed, CombatTimingKind.ActionCompleted, 3)), new PlayerRule(Apply(Bleed)));
            Do(session, CardDuelAction.Play, true); var before = session.State; var card = Card(before, Player).Single();
            var failed = session.Execute(before.Revision, Player, CardDuelAction.Discard, new[] { card, card });
            Assert.IsFalse(failed.Accepted); Assert.AreSame(before, session.State); Assert.IsEmpty(failed.Steps);
            Assert.AreEqual(2, Status(session.State, Bleed, Player).Duration.RemainingTriggers);
        }

        [Test] public void PoisonTicksOnceForTheWholePlayerTeamWhenTheLastPlayerEnds()
        {
            var session = Session(Catalog(Periodic(Poison, CombatTimingKind.PhaseEnding, 4, 1)),
                new PlayerRule(Apply(Poison, EffectTargetSpec.Allies(EffectTargetSelection.AllLiving))), energy: 1, ally: true);
            var state = session.State;
            var play = Step(session.Engine, ref state, Player, CardDuelAction.Play, true);
            Assert.IsEmpty(Ticks(play)); Assert.AreEqual(ScoredDuelPhase.AwaitingPlayerHand, state.Duel.Phase);
            Assert.AreEqual(100, state.Duel.Combat.Get(Player).Health); Assert.AreEqual(100, state.Duel.Combat.Get(Ally).Health);
            var end = Step(session.Engine, ref state, Ally, CardDuelAction.EndTurn);
            CollectionAssert.AreEqual(new[] { Player, Ally }, Ticks(end).Select(item => item.Effect.Target));
            Assert.AreEqual(96, state.Duel.Combat.Get(Player).Health); Assert.AreEqual(96, state.Duel.Combat.Get(Ally).Health);
            Assert.IsEmpty(state.Duel.Combat.Statuses); Assert.AreEqual(ScoredDuelPhase.AwaitingEnemyAction, state.Duel.Phase);
        }

        [Test] public void StunnedEnemyDoesNotBleedButStillTakesPoisonWhenItsTeamPhaseEnds()
        {
            var session = Session(Catalog(Periodic(Bleed, CombatTimingKind.ActionCompleted, 5), Periodic(Poison, CombatTimingKind.PhaseEnding, 3, 1), Control()),
                new PlayerRule(Apply(Bleed, EffectTargetSpec.Opponents()), Apply(Poison, EffectTargetSpec.Opponents()), Apply(Stun, EffectTargetSpec.Opponents())), energy: 1);
            var result = Do(session, CardDuelAction.Play, true);
            var enemy = result.Steps.Single(item => item.Action == CardDuelAction.EnemyAction).Transition;
            Assert.AreEqual(1, enemy.RuleEvents.OfType<CombatActionWindowSkippedEvent>().Count());
            Assert.AreEqual(1, Ticks(enemy).Count()); Assert.AreEqual(3, Ticks(enemy).Single().Effect.Amount);
            Assert.AreEqual(97, session.State.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(3, Status(session.State, Bleed, Enemy).Duration.RemainingTriggers);
            Assert.AreEqual(1, session.State.Duel.Combat.Statuses.Count);
        }

        [Test] public void StunnedPlayerSkippedWindowStillClosesItsPoisonPhase()
        {
            var session = Session(Catalog(Periodic(Bleed, CombatTimingKind.ActionCompleted, 5), Periodic(Poison, CombatTimingKind.PhaseEnding, 3, 1), Control()),
                enemyRule: new EnemyRule(Apply(Bleed, EffectTargetSpec.Opponents()), Apply(Poison, EffectTargetSpec.Opponents()), Apply(Stun, EffectTargetSpec.Opponents())));
            var state = session.State;
            Step(session.Engine, ref state, Player, CardDuelAction.EndTurn);
            Step(session.Engine, ref state, Enemy, CardDuelAction.EnemyAction);
            Assert.IsTrue(state.Duel.IsActionBlocked(Player));
            var skipped = Step(session.Engine, ref state, Player, CardDuelAction.SkipPlayerTurn);
            Assert.AreEqual(1, skipped.RuleEvents.OfType<CombatActionWindowSkippedEvent>().Count());
            Assert.AreEqual(3, Ticks(skipped).Single().Effect.Amount); Assert.AreEqual(97, state.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(3, Status(state, Bleed, Player).Duration.RemainingTriggers); Assert.AreEqual(ScoredDuelPhase.AwaitingEnemyAction, state.Duel.Phase);
        }

        [Test] public void EnemyBleedsAfterItsDamageAndItsLethalTickStartsNoPoisonClock()
        {
            var session = Session(Catalog(Periodic(Bleed, CombatTimingKind.ActionCompleted, 3), Periodic(Poison, CombatTimingKind.PhaseEnding, 100, target: CombatTargetSpec.Opponents())),
                new PlayerRule(Apply(Bleed, EffectTargetSpec.Opponents()), Apply(Poison, EffectTargetSpec.Opponents())), energy: 1, enemyHealth: 3, enemyDamage: 7);
            var result = Do(session, CardDuelAction.Play, true);
            Assert.AreEqual(ScoredDuelPhase.Won, session.State.Duel.Phase); Assert.AreEqual(93, session.State.Duel.Combat.Get(Player).Health);
            var enemy = result.Steps.Single(item => item.Action == CardDuelAction.EnemyAction).Transition;
            Assert.AreEqual(1, Ticks(enemy).Count()); Assert.AreEqual(Enemy, Ticks(enemy).Single().Effect.Target);
            Assert.AreEqual(3, Status(session.State, Poison, Enemy).Duration.RemainingTriggers);
        }

        [Test] public void PrimaryVictoryDoesNotStartLethalBleedingAfterTheBattle()
        {
            var session = Session(Catalog(Periodic(Bleed, CombatTimingKind.ActionCompleted, 100)), new PlayerRule(Apply(Bleed), Hit(100)));
            var play = Do(session, CardDuelAction.Play, true).Steps[0].Transition;
            Assert.AreEqual(ScoredDuelPhase.Won, session.State.Duel.Phase); Assert.IsEmpty(Ticks(play));
            Assert.AreEqual(100, session.State.Duel.Combat.Get(Player).Health); Assert.AreEqual(3, Status(session.State, Bleed, Player).Duration.RemainingTriggers);
        }

        [Test] public void PoisonKillingTheLastPlayerEndsBeforeEnemiesCanAct()
        {
            var session = Session(Catalog(Periodic(Poison, CombatTimingKind.PhaseEnding, 7)), new PlayerRule(Apply(Poison)),
                energy: 1, playerHealth: 7, enemyDamage: 20);
            var result = Do(session, CardDuelAction.Play, true);
            Assert.AreEqual(1, result.Steps.Count); Assert.AreEqual(ScoredDuelPhase.Lost, session.State.Duel.Phase);
            Assert.IsEmpty(session.State.Duel.PendingEnemies); Assert.AreEqual(0, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(7, Ticks(result.Steps[0].Transition).Single().DamageToHealth);
        }

        [Test] public void PlayerPhasePoisonThatKillsAnEnemyRemovesItFromTheUpcomingQueue()
        {
            var session = Session(Catalog(Periodic(Poison, CombatTimingKind.PhaseEnding, 100, target: CombatTargetSpec.Opponents())),
                new PlayerRule(Apply(Poison)), energy: 1, otherEnemy: true);
            var state = session.State;
            var play = Step(session.Engine, ref state, Player, CardDuelAction.Play, true);
            Assert.AreEqual(Enemy, Ticks(play).Single().Effect.Target); Assert.AreEqual(ScoredDuelPhase.AwaitingEnemyAction, state.Duel.Phase);
            CollectionAssert.AreEqual(new[] { Other }, state.Duel.PendingEnemies); Assert.AreEqual(Other, state.Duel.NextEnemy);
        }

        [Test] public void LastEnemyPhaseTickUsesShieldBeforeClearAndRunsBeforeRoundExpiry()
        {
            var session = Session(Catalog(Periodic(Poison, CombatTimingKind.PhaseEnding, 6, target: CombatTargetSpec.Opponents(),
                    lifetime: StatusLifetimeSpec.ThroughCurrentRound())),
                new PlayerRule(Apply(Poison, EffectTargetSpec.Opponents())), energy: 1, playerBlock: 10,
                blockExpiry: PlayerBlockExpiry.AfterEnemyPhase);
            var result = Do(session, CardDuelAction.Play, true);
            var enemy = result.Steps.Single(item => item.Action == CardDuelAction.EnemyAction).Transition;
            var tick = Ticks(enemy).Single(); Assert.AreEqual(6, tick.BlockAbsorbed); Assert.AreEqual(0, tick.DamageToHealth);
            Assert.AreEqual(100, session.State.Duel.Combat.Get(Player).Health); Assert.AreEqual(0, session.State.Duel.Combat.Get(Player).Block);
            Assert.IsEmpty(session.State.Duel.Combat.Statuses);
            var facts = enemy.RuleEvents.ToList();
            Assert.Less(facts.IndexOf(tick), facts.FindIndex(item => item is CombatEvent && item.Effect.Kind == CombatEffectKind.ClearBlock));
            Assert.Less(facts.IndexOf(tick), facts.FindIndex(item => item is CombatStatusChangedEvent && ((CombatStatusChangedEvent)item).Reason == CombatStatusChangeReason.Expired));
        }

        [Test] public void PlayAndDiscardPreviewsIncludeExactlyTheCommittedPeriodicEffects()
        {
            var session = Session(Catalog(Periodic(Bleed, CombatTimingKind.ActionCompleted, 3), Periodic(Poison, CombatTimingKind.PhaseEnding, 4)),
                new PlayerRule(Apply(Bleed), Apply(Poison)), energy: 2);
            var before = session.State; var card = Card(before, Player);
            var preview = session.Engine.PreviewPlay(before, before.Revision, Player, card);
            Assert.IsTrue(preview.Accepted); Assert.AreSame(before, session.State);
            var play = Do(session, CardDuelAction.Play, true).Steps[0].Transition;
            SameState(preview.State, play.State); Assert.AreEqual(1, Ticks(play).Count());
            before = session.State; card = Card(before, Player);
            var discardPreview = session.Engine.PreviewDiscard(before, before.Revision, Player, card);
            var discarded = session.Engine.Execute(before, before.Revision, Player, CardDuelAction.Discard, card);
            Assert.IsTrue(discardPreview.Accepted); Assert.IsTrue(discarded.Accepted); SameState(discardPreview.State, discarded.State);
            CollectionAssert.AreEqual(new long[] { 3, 4 }, Ticks(discarded).Select(item => item.Effect.Amount));
        }

        [Test] public void EnemyPreviewDisappearsWhenPlayerPhasePoisonEndsTheBattleFirst()
        {
            var session = Session(Catalog(Periodic(Poison, CombatTimingKind.PhaseEnding, 100, target: CombatTargetSpec.Opponents())), new PlayerRule(Apply(Poison)));
            Do(session, CardDuelAction.Play, true);
            var before = session.State;
            Assert.IsNull(session.Engine.Duel.PreviewEnemyAction(before.Duel, Enemy));
            Assert.AreSame(before, session.State);
            var end = Do(session, CardDuelAction.EndTurn);
            Assert.AreEqual(1, end.Steps.Count); Assert.AreEqual(ScoredDuelPhase.Won, session.State.Duel.Phase);
        }

        [Test] public void PeriodicOverflowRollsBackPlayCardsEnergyScoreAndNewStatus()
        {
            var session = Session(Catalog(Periodic(Bleed, CombatTimingKind.ActionCompleted, long.MaxValue)), new PlayerRule(Apply(Bleed, stacks: 2), Hit(7)));
            var before = session.State; var card = Card(before, Player);
            var preview = session.Engine.PreviewPlay(before, before.Revision, Player, card);
            var result = session.Execute(before.Revision, Player, CardDuelAction.Play, card);
            Assert.AreEqual(CardDuelError.Overflow, preview.Error); Assert.AreSame(before, preview.State); Assert.IsEmpty(preview.RuleEvents);
            Assert.AreEqual(CardDuelError.Overflow, result.Error); Assert.AreSame(before, result.State); Assert.AreSame(before, session.State);
            Assert.IsEmpty(result.Steps); Assert.IsEmpty(before.Duel.Combat.Statuses); Assert.IsEmpty(before.Duel.Scores);
            Assert.AreEqual(3, before.Duel.Player(Player).Energy.Energy); Assert.AreEqual(100, before.Duel.Combat.Get(Enemy).Health);
        }

        [Test] public void FailureInFinalEnemyPhaseTickRollsBackTheWholeAutomaticSession()
        {
            var session = Session(Catalog(Periodic(Poison, CombatTimingKind.PhaseEnding, 4, 1)),
                new PlayerRule(Apply(Poison, EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving))), energy: 1, enemyDamage: 7,
                otherEnemy: true, otherDamageRules: new BattleActorDamageRules(incomingDamage: new ScaledIncomingDamageModifier(decimal.MaxValue)));
            var before = session.State; var state = before;
            Step(session.Engine, ref state, Player, CardDuelAction.Play, true);
            Step(session.Engine, ref state, Enemy, CardDuelAction.EnemyAction);
            Assert.AreEqual(93, state.Duel.Combat.Get(Player).Health);
            var failed = session.Engine.Execute(state, state.Revision, Other, CardDuelAction.EnemyAction);
            Assert.AreEqual(CardDuelError.Overflow, failed.Error); Assert.AreSame(state, failed.State); Assert.IsEmpty(failed.RuleEvents);
            Assert.AreEqual(100, state.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(1, Status(state, Poison, Enemy).Duration.RemainingTriggers);
            var result = session.Execute(before.Revision, Player, CardDuelAction.Play, Card(before, Player));
            Assert.AreEqual(CardDuelError.Overflow, result.Error); Assert.AreSame(before, session.State); Assert.AreSame(before, result.State); Assert.IsEmpty(result.Steps);
            SameState(before, result.State); Assert.IsEmpty(session.State.Duel.Combat.Statuses); Assert.IsEmpty(session.State.Duel.Scores);
        }

        [Test] public void LethalBleedingClosesOnlyTheDeadPlayersWindowWhileALivingAllyCanStillAct()
        {
            var session = Session(Catalog(Periodic(Bleed, CombatTimingKind.ActionCompleted, 3)), new PlayerRule(Apply(Bleed)), playerHealth: 3, ally: true);
            var result = Do(session, CardDuelAction.Play, true);
            // The shared player phase stays open, so Session collects the dead actor's submitted card without advancing enemies.
            CollectionAssert.AreEqual(new[] { CardDuelAction.Play, CardDuelAction.CollectPlayed }, result.Steps.Select(step => step.Action));
            Assert.AreEqual(0, session.State.Duel.Combat.Get(Player).Health);
            Assert.IsFalse(session.State.Duel.HasPlayerWindow(Player)); Assert.IsTrue(session.State.Duel.CanAct(Ally));
            Assert.AreEqual(ScoredDuelPhase.AwaitingPlayerHand, session.State.Duel.Phase);
            Assert.AreEqual(2, Status(session.State, Bleed, Player).Duration.RemainingTriggers);
        }

        [Test] public void StartedPhaseBatchContinuesAfterItsFirstTickDefeatsTheLastEnemy()
        {
            var session = Session(Catalog(Periodic(Poison, CombatTimingKind.PhaseEnding, 100, 1, CombatTargetSpec.Opponents()),
                    Periodic(Bleed, CombatTimingKind.PhaseEnding, 5, 1)),
                new PlayerRule(Apply(Poison), Apply(Bleed, EffectTargetSpec.Allies(EffectTargetSelection.AllLiving))), energy: 1, ally: true);
            var state = session.State;
            Step(session.Engine, ref state, Player, CardDuelAction.Play, true);
            var ended = Step(session.Engine, ref state, Ally, CardDuelAction.EndTurn);
            CollectionAssert.AreEqual(new[] { Enemy, Player, Ally }, Ticks(ended).Select(item => item.Effect.Target));
            Assert.AreEqual(ScoredDuelPhase.Won, state.Duel.Phase); Assert.AreEqual(95, state.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(95, state.Duel.Combat.Get(Ally).Health); Assert.IsEmpty(state.Duel.Combat.Statuses);
        }

        [Test] public void ConfiguredButUnappliedPeriodicBehaviorsPreserveExistingEventsAndRevisions()
        {
            var baseline = Session(null, new PlayerRule(Hit(9)), discardReward: new PerCardBlockRewardRule(5), enemyDamage: 7,
                blockExpiry: PlayerBlockExpiry.AfterEnemyPhase);
            var configured = Session(Catalog(Periodic(Bleed, CombatTimingKind.ActionCompleted, 3), Periodic(Poison, CombatTimingKind.PhaseEnding, 4)),
                new PlayerRule(Hit(9)), discardReward: new PerCardBlockRewardRule(5), enemyDamage: 7, blockExpiry: PlayerBlockExpiry.AfterEnemyPhase);
            foreach (var action in new[] { CardDuelAction.Play, CardDuelAction.Discard, CardDuelAction.EndTurn })
            {
                var left = Do(baseline, action, action != CardDuelAction.EndTurn);
                var right = Do(configured, action, action != CardDuelAction.EndTurn);
                SameState(left.State, right.State);
                CollectionAssert.AreEqual(left.Steps.SelectMany(item => item.Transition.RuleEvents).Select(item => item.GetType()),
                    right.Steps.SelectMany(item => item.Transition.RuleEvents).Select(item => item.GetType()));
                Assert.IsEmpty(right.Steps.SelectMany(item => Ticks(item.Transition)));
            }
        }
    }
}
