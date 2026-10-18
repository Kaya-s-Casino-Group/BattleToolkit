using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class StatusBattleIntegrationTests
    {
        private static readonly ParticipantId Player = new ParticipantId("hero"), Ally = new ParticipantId("partner"),
            Enemy = new ParticipantId("foe-a"), Other = new ParticipantId("foe-b"), Last = new ParticipantId("foe-c");
        // Deliberately unrelated to any gameplay status name: behavior comes entirely from the catalog.
        private static readonly StatusDefinitionId Pause = new StatusDefinitionId("test.amber-17"),
            Protection = new StatusDefinitionId("test.violet-42");

        private sealed class PlayerRule : IScoredHandEffectRule
        {
            private readonly Func<ScoredHandEffectContext, IEnumerable<BattleEffectStep>> resolve;
            public string Version => "status-player.integration.1";
            public PlayerRule(params BattleEffectStep[] steps) : this(context => steps) { }
            public PlayerRule(Func<ScoredHandEffectContext, IEnumerable<BattleEffectStep>> resolve) { this.resolve = resolve; }
            public BattleEffectSequence Resolve(ScoredHandEffectContext context) => new BattleEffectSequence(Version, "configured player statuses", resolve(context));
        }
        private sealed class EnemyRule : IEnemyBattleEffectRule
        {
            private readonly Func<EnemyBattleEffectContext, IEnumerable<BattleEffectStep>> resolve;
            public string Version => "status-enemy.integration.1";
            public EnemyRule(params BattleEffectStep[] steps) : this(context => steps) { }
            public EnemyRule(Func<EnemyBattleEffectContext, IEnumerable<BattleEffectStep>> resolve) { this.resolve = resolve; }
            public BattleEffectSequence Resolve(EnemyBattleEffectContext context) => new BattleEffectSequence(Version, "configured enemy statuses", resolve(context));
        }

        private static CombatStatusCatalog Catalog() => new CombatStatusCatalog("status-integration.catalog.1", new[] {
            new CombatStatusDefinition(Pause, StatusTags.Control, new StatusBehaviorSpec[] { new BlockActionWindowSpec() }, StatusLifetimeSpec.ForTriggers(1)),
            new CombatStatusDefinition(Protection, StatusTags.None, new StatusBehaviorSpec[] { new PreventStatusApplicationSpec(StatusTags.Control) },
                StatusLifetimeSpec.ThroughNextPhase("enemy"))
        });
        private static BattleEffectStep Step(BattleEffectSpec effect, EffectTargetSpec target) => new BattleEffectStep(effect, target);
        private static BattleEffectStep Status(StatusDefinitionId id, EffectTargetSpec target) =>
            Step(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(id)), target);
        private static BattleEnemySetup Foe(ParticipantId id, long damage = 7, IEnemyBattleEffectRule rule = null) =>
            new BattleEnemySetup(id, new CombatantDefinition("foe", 1000), damage, effectRule: rule);
        private static EnemyRule FirstAttackControls(long damage) => new EnemyRule(context => {
            var hit = Step(BattleEffectSpec.Damage(damage), EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving));
            return context.Combat.Get(context.Source).Block == 0 ? new[] {
                Status(Pause, EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving)), hit,
                Step(BattleEffectSpec.GainBlock(1), EffectTargetSpec.Self())
            } : new[] { hit };
        });
        private static BattleSession Session(IScoredHandEffectRule rule = null, int energy = 3, IScoredHandEffectRule allyRule = null,
            BattleEnemySetup[] enemies = null)
        {
            var engine = new CardDuelEngine(new BattleRules("status-battle.integration.1", 12, statusCatalog: Catalog()));
            var players = new List<BattlePlayerSetup> {
                new BattlePlayerSetup(Player, new CombatantDefinition("hero", 100), rules: new BattlePlayerRules(maxEnergy: energy,
                    initialEnergy: energy, effectRule: rule ?? new PlayerRule(Step(BattleEffectSpec.GainBlock(0), EffectTargetSpec.Self()))))
            };
            if (allyRule != null) players.Add(new BattlePlayerSetup(Ally, new CombatantDefinition("partner", 100),
                rules: new BattlePlayerRules(maxEnergy: energy, initialEnergy: energy, effectRule: allyRule)));
            return new BattleSession(engine, engine.Create("status-battle", HmacDeckShuffler.FromSeed(new byte[32]), players,
                enemies ?? new[] { Foe(Enemy), Foe(Other, 11) }));
        }
        private static BattleSessionResult Do(BattleSession session, ParticipantId actor, CardDuelAction action,
            IEnumerable<CardInstanceId> cards = null, IEnumerable<ParticipantId> targets = null)
        {
            var result = session.Execute(session.State.Revision, actor, action, cards, targets);
            Assert.IsTrue(result.Accepted, action + ": " + result.Error);
            Assert.AreSame(result.State, session.State); result.State.Table.Validate(); return result;
        }
        private static void Start(BattleSession session) => Do(session, Player, CardDuelAction.Start);
        private static CardInstanceId[] OneCard(BattleSession session, ParticipantId actor) => session.State.Zone(CardZoneKinds.Hand, actor).Cards.Take(1).ToArray();
        private static IEnumerable<CombatRuleEvent> Facts(BattleSessionResult result) => result.Steps.SelectMany(step => step.Transition.RuleEvents);
        private static IEnumerable<StatusInstance> Statuses(CardDuelState state, StatusDefinitionId definition) =>
            state.Duel.Combat.Statuses.Where(status => status.DefinitionId == definition);
        private static string StatusKey(StatusInstance status) => status.Id.Value + "/" + status.DefinitionId.Value + "/" +
            status.Source.Id + "/" + status.Target.Id + "/" + status.Stacks + "/" + status.Duration.Kind + "/" +
            status.Duration.ScopeId + "/" + status.Duration.PhaseKey + "/" + status.Duration.RemainingTriggers;
        private static void SameCardState(CardDuelState expected, CardDuelState actual)
        {
            Assert.AreEqual(expected.Revision, actual.Revision); Assert.AreEqual(expected.Duel.Revision, actual.Duel.Revision);
            Assert.AreEqual(expected.Duel.Combat.Revision, actual.Duel.Combat.Revision);
            Assert.AreEqual(expected.Duel.Phase, actual.Duel.Phase); Assert.AreEqual(expected.Duel.CompletedRounds, actual.Duel.CompletedRounds);
            CollectionAssert.AreEqual(expected.Random.StateBytes, actual.Random.StateBytes);
            CollectionAssert.AreEqual(expected.Duel.Combat.Statuses.Select(StatusKey), actual.Duel.Combat.Statuses.Select(StatusKey));
            CollectionAssert.AreEqual(expected.Duel.Scores.Select(score => score.Total), actual.Duel.Scores.Select(score => score.Total));
            CollectionAssert.AreEqual(expected.Duel.PendingEnemies, actual.Duel.PendingEnemies);
            foreach (var unit in expected.Duel.Combat.Combatants)
            {
                Assert.AreEqual(unit.Health, actual.Duel.Combat.Get(unit.Id).Health);
                Assert.AreEqual(unit.Block, actual.Duel.Combat.Get(unit.Id).Block);
            }
            foreach (var player in expected.Duel.Players)
            {
                Assert.AreEqual(player.Energy.Energy, actual.Duel.Player(player.Id).Energy.Energy);
                Assert.AreEqual(player.HasEnded, actual.Duel.Player(player.Id).HasEnded);
            }
            foreach (var zone in expected.Table.Zones) CollectionAssert.AreEqual(zone.Cards, actual.Table.GetZone(zone.Id).Cards);
        }

        [Test] public void ProtectionCoversEveryEnemyInTheUpcomingPhaseAndExpiresAfterTheLast()
        {
            var session = Session(new PlayerRule(Status(Protection, EffectTargetSpec.Self())), energy: 1,
                enemies: new[] { Foe(Enemy, rule: FirstAttackControls(7)), Foe(Other, rule: FirstAttackControls(11)) });
            Start(session);
            var result = Do(session, Player, CardDuelAction.Play, OneCard(session, Player));
            var enemies = result.Steps.Where(step => step.Action == CardDuelAction.EnemyAction).ToArray();
            Assert.AreEqual(2, enemies.Length);
            Assert.AreEqual(1, Statuses(enemies[0].Transition.State, Protection).Count());
            Assert.IsEmpty(Statuses(enemies[1].Transition.State, Protection));
            CollectionAssert.AreEqual(new[] { Enemy, Other }, Facts(result).OfType<CombatStatusBlockedEvent>().Select(fact => fact.Effect.Source));
            Assert.AreEqual(1, Facts(result).OfType<CombatStatusChangedEvent>().Count(fact => fact.Reason == CombatStatusChangeReason.Expired));
            Assert.IsEmpty(Facts(result).OfType<CombatActionWindowSkippedEvent>());
            Assert.IsEmpty(session.State.Duel.Combat.Statuses);
            Assert.AreEqual(82, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(1, session.State.Duel.CompletedRounds); Assert.IsTrue(session.State.Duel.CanAct(Player));
        }

        [Test] public void StunnedEnemiesSkipOnceAndTheFinalSkippedEnemyStillClosesThePhase()
        {
            var session = Session(new PlayerRule(Status(Pause, EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving)),
                Status(Protection, EffectTargetSpec.Self())), energy: 1);
            Start(session);
            var first = Do(session, Player, CardDuelAction.Play, OneCard(session, Player));
            CollectionAssert.AreEqual(new[] { Enemy, Other }, Facts(first).OfType<CombatActionWindowSkippedEvent>().Select(fact => fact.Actor));
            Assert.IsTrue(first.Steps.Where(step => step.Action == CardDuelAction.EnemyAction).All(step => step.Transition.EffectPlan == null));
            Assert.IsEmpty(session.State.Duel.Combat.Statuses); Assert.AreEqual(100, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(1, session.State.Duel.CompletedRounds); Assert.IsEmpty(session.State.Duel.PendingEnemies);
            Assert.AreEqual(8, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Count);
            var next = Do(session, Player, CardDuelAction.EndTurn);
            Assert.IsEmpty(Facts(next).OfType<CombatActionWindowSkippedEvent>());
            Assert.AreEqual(82, session.State.Duel.Combat.Get(Player).Health); Assert.AreEqual(2, session.State.Duel.CompletedRounds);
        }

        [Test] public void ReapplyingTheSameControlDoesNotAccumulateSkippedRounds()
        {
            var session = Session(new PlayerRule(Status(Pause, EffectTargetSpec.Opponents())), enemies: new[] { Foe(Enemy) });
            Start(session); Do(session, Player, CardDuelAction.Play, OneCard(session, Player));
            var original = Statuses(session.State, Pause).Single();
            var refreshed = Do(session, Player, CardDuelAction.Play, OneCard(session, Player));
            var current = Statuses(session.State, Pause).Single();
            Assert.AreEqual(original.Id, current.Id); Assert.AreEqual(1, current.Duration.RemainingTriggers);
            Assert.AreEqual(1, Facts(refreshed).OfType<CombatStatusChangedEvent>().Count(fact => fact.Reason == CombatStatusChangeReason.Refreshed));
            var skipped = Do(session, Player, CardDuelAction.EndTurn);
            Assert.AreEqual(1, Facts(skipped).OfType<CombatActionWindowSkippedEvent>().Count());
            Assert.IsEmpty(session.State.Duel.Combat.Statuses);
            Do(session, Player, CardDuelAction.EndTurn);
            Assert.AreEqual(93, session.State.Duel.Combat.Get(Player).Health); Assert.AreEqual(2, session.State.Duel.CompletedRounds);
        }

        [Test] public void EnemyControlAutomaticallySkipsTheNextPlayerWindowAfterItsNormalDraw()
        {
            var session = Session(energy: 1, enemies: new[] { Foe(Enemy, rule: FirstAttackControls(7)) });
            Start(session); var card = OneCard(session, Player);
            var result = Do(session, Player, CardDuelAction.Play, card);
            CollectionAssert.AreEqual(new[] { CardDuelAction.Play, CardDuelAction.EnemyAction, CardDuelAction.SkipPlayerTurn, CardDuelAction.EnemyAction },
                result.Steps.Select(step => step.Action));
            var skipped = result.Steps.Single(step => step.Action == CardDuelAction.SkipPlayerTurn);
            Assert.AreEqual(1, skipped.Before.Duel.CompletedRounds); Assert.IsTrue(skipped.Before.Duel.IsActionBlocked(Player));
            Assert.AreEqual(8, skipped.Before.Zone(CardZoneKinds.Hand, Player).Cards.Count);
            CollectionAssert.AreEqual(skipped.Before.Zone(CardZoneKinds.Hand, Player).Cards, skipped.Transition.State.Zone(CardZoneKinds.Hand, Player).Cards);
            Assert.IsEmpty(skipped.Selected); Assert.AreEqual(0, skipped.Transition.State.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(1, session.State.Duel.Scores.Count); Assert.AreEqual(2, session.State.Duel.CompletedRounds);
            Assert.AreEqual(86, session.State.Duel.Combat.Get(Player).Health); Assert.IsEmpty(session.State.Duel.Combat.Statuses);
            Assert.IsTrue(session.State.Duel.CanAct(Player)); Assert.AreEqual(1, session.State.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(8, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Count);
            Assert.That(session.State.Zone(CardZoneKinds.Discard, Player).Cards, Does.Contain(card[0]));
        }

        [Test] public void AllControlledPlayersAdvanceWithoutInputAndRecoverOnTheFollowingPlayerPhase()
        {
            var harmless = new PlayerRule(Step(BattleEffectSpec.GainBlock(0), EffectTargetSpec.Self()));
            var session = Session(harmless, energy: 1, allyRule: harmless, enemies: new[] { Foe(Enemy, rule: FirstAttackControls(3)) });
            Start(session); Do(session, Player, CardDuelAction.Play, OneCard(session, Player));
            var result = Do(session, Ally, CardDuelAction.Play, OneCard(session, Ally));
            CollectionAssert.AreEqual(new[] { Player, Ally }, result.Steps.Where(step => step.Action == CardDuelAction.SkipPlayerTurn).Select(step => step.Actor));
            Assert.AreEqual(2, session.State.Duel.CompletedRounds); Assert.AreEqual(2, session.State.Duel.Scores.Count);
            Assert.IsEmpty(session.State.Duel.Combat.Statuses); Assert.IsFalse(session.State.Duel.NextBlockedPlayer.IsValid);
            foreach (var id in new[] { Player, Ally })
            {
                Assert.IsTrue(session.State.Duel.CanAct(id)); Assert.AreEqual(1, session.State.Duel.Player(id).Energy.Energy);
                Assert.AreEqual(94, session.State.Duel.Combat.Get(id).Health); Assert.AreEqual(8, session.State.Zone(CardZoneKinds.Hand, id).Cards.Count);
            }
            foreach (var skip in result.Steps.Where(step => step.Action == CardDuelAction.SkipPlayerTurn))
                Assert.AreEqual(8, skip.Before.Zone(CardZoneKinds.Hand, skip.Actor).Cards.Count);
        }

        [Test] public void ControlAppliedMidPhaseAutomaticallyForfeitsOnlyTheAffectedPlayersRemainingWindow()
        {
            var harmless = new PlayerRule(Step(BattleEffectSpec.GainBlock(0), EffectTargetSpec.Self()));
            var session = Session(harmless, allyRule: new PlayerRule(Status(Pause, EffectTargetSpec.Allies(EffectTargetSelection.Requested))));
            Start(session); Do(session, Player, CardDuelAction.Play, OneCard(session, Player));
            Assert.AreEqual(2, session.State.Duel.Player(Player).Energy.Energy);
            var before = session.State;
            var result = Do(session, Ally, CardDuelAction.Play, OneCard(session, Ally), new[] { Player });
            var skip = result.Steps.Single(step => step.Action == CardDuelAction.SkipPlayerTurn);
            Assert.AreEqual(Player, skip.Actor); Assert.AreEqual(2, skip.Before.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(0, session.State.Duel.Player(Player).Energy.Energy); Assert.IsTrue(session.State.Duel.Player(Player).HasEnded);
            Assert.IsTrue(session.State.Duel.CanAct(Ally)); Assert.AreEqual(2, session.State.Duel.Player(Ally).Energy.Energy);
            Assert.IsEmpty(session.State.Duel.Combat.Statuses); Assert.AreEqual(0, session.State.Duel.CompletedRounds);
            Assert.IsEmpty(result.Steps.Where(step => step.Action == CardDuelAction.EnemyAction));
            CollectionAssert.AreEqual(before.Zone(CardZoneKinds.Hand, Player).Cards, session.State.Zone(CardZoneKinds.Hand, Player).Cards);
        }

        [Test] public void PlayPreviewBindsDefaultLifetimesAndStatusIdsWithoutMutatingTheSession()
        {
            var session = Session(new PlayerRule(Status(Protection, EffectTargetSpec.Self()), Status(Pause, EffectTargetSpec.Opponents())));
            Start(session); var before = session.State; var cards = OneCard(session, Player);
            var first = session.Engine.PreviewPlay(before, before.Revision, Player, cards);
            var second = session.Engine.PreviewPlay(before, before.Revision, Player, cards);
            Assert.IsTrue(first.Accepted); Assert.IsTrue(second.Accepted); Assert.AreSame(before, session.State); Assert.IsEmpty(before.Duel.Combat.Statuses);
            var protection = Statuses(first.State, Protection).Single();
            Assert.AreEqual(DurationKind.UntilPhaseEnd, protection.Duration.Kind);
            Assert.AreEqual(BattleStatusTiming.RoundScope(before.Duel), protection.Duration.ScopeId); Assert.AreEqual("enemy", protection.Duration.PhaseKey);
            Assert.AreEqual(1, Statuses(first.State, Pause).Single().Duration.RemainingTriggers);
            var actual = Do(session, Player, CardDuelAction.Play, cards).Steps[0].Transition;
            SameCardState(first.State, second.State); SameCardState(first.State, actual.State);
            CollectionAssert.AreEqual(first.RuleEvents.Select(fact => fact.GetType()), actual.RuleEvents.Select(fact => fact.GetType()));
        }

        [Test] public void EnemyPreviewReportsSkipAndExpiryWithoutConsumingAuthoritativeStatuses()
        {
            var session = Session(new PlayerRule(Status(Pause, EffectTargetSpec.Opponents()), Status(Protection, EffectTargetSpec.Self())),
                energy: 1, enemies: new[] { Foe(Enemy) });
            Start(session); var original = session.State;
            var played = session.Engine.Execute(original, original.Revision, Player, CardDuelAction.Play, OneCard(session, Player));
            Assert.IsTrue(played.Accepted); Assert.AreEqual(2, played.State.Duel.Combat.Statuses.Count);
            var before = played.State; var snapshots = before.Duel.Combat.Statuses.Select(StatusKey).ToArray();
            var one = session.Engine.Duel.PreviewEnemyAction(before.Duel, Enemy);
            var two = session.Engine.Duel.PreviewEnemyAction(before.Duel, Enemy);
            Assert.IsTrue(one.Accepted); Assert.IsTrue(two.Accepted); Assert.IsTrue(one.IsSkipped); Assert.IsEmpty(one.Hits);
            CollectionAssert.AreEqual(snapshots, before.Duel.Combat.Statuses.Select(StatusKey)); Assert.AreSame(original, session.State);
            var actual = session.Engine.Execute(before, before.Revision, Enemy, CardDuelAction.EnemyAction);
            Assert.IsTrue(actual.Accepted); Assert.IsEmpty(actual.State.Duel.Combat.Statuses);
            Assert.AreEqual(1, actual.State.Duel.CompletedRounds); Assert.AreEqual(100, actual.State.Duel.Combat.Get(Player).Health);
            CollectionAssert.AreEqual(one.RuleEvents.Select(fact => fact.GetType()), two.RuleEvents.Select(fact => fact.GetType()));
            CollectionAssert.AreEqual(one.RuleEvents.Select(fact => fact.GetType()), actual.RuleEvents.Select(fact => fact.GetType()));
            CollectionAssert.AreEqual(one.RuleEvents.Select(fact => fact.Revision), actual.RuleEvents.Select(fact => fact.Revision));
            CollectionAssert.AreEqual(new[] { CombatStatusChangeReason.Consumed, CombatStatusChangeReason.Expired },
                one.RuleEvents.OfType<CombatStatusChangedEvent>().Select(fact => fact.Reason));
        }

        [Test] public void ProtectionGrantedDuringEnemyPhaseLastsThroughTheNextEnemyPhase()
        {
            var rule = new EnemyRule(context => context.Combat.Get(context.Source).Block == 0 ? new[] {
                Status(Protection, EffectTargetSpec.Self()), Step(BattleEffectSpec.GainBlock(1), EffectTargetSpec.Self()),
                Step(BattleEffectSpec.Damage(3), EffectTargetSpec.Opponents())
            } : new[] { Step(BattleEffectSpec.Damage(3), EffectTargetSpec.Opponents()) });
            var session = Session(new PlayerRule(Status(Pause, EffectTargetSpec.Opponents())), energy: 1, enemies: new[] { Foe(Enemy, rule: rule) });
            Start(session); Do(session, Player, CardDuelAction.EndTurn);
            var protection = Statuses(session.State, Protection).Single();
            Assert.AreEqual(BattleStatusTiming.RoundScope(session.State.Duel), protection.Duration.ScopeId);
            var result = Do(session, Player, CardDuelAction.Play, OneCard(session, Player));
            Assert.AreEqual(1, Facts(result).OfType<CombatStatusBlockedEvent>().Count());
            Assert.IsEmpty(Facts(result).OfType<CombatActionWindowSkippedEvent>()); Assert.IsEmpty(session.State.Duel.Combat.Statuses);
            Assert.AreEqual(94, session.State.Duel.Combat.Get(Player).Health); Assert.AreEqual(2, session.State.Duel.CompletedRounds);
        }

        [Test] public void BattleCatalogRejectsDefaultLifetimesWithAnUnknownPhase()
        {
            var catalog = new CombatStatusCatalog("unknown-phase.catalog.1", new[] {
                new CombatStatusDefinition(Protection, StatusTags.None, new StatusBehaviorSpec[] { new PreventStatusApplicationSpec(StatusTags.Control) },
                    StatusLifetimeSpec.ThroughNextPhase("after-presentation"))
            });
            Assert.Throws<ArgumentException>(() => new BattleRules("unknown-phase.battle.1", 12, statusCatalog: catalog));
        }

        [Test] public void LateFailureRollsBackExpiryControlConsumptionDamageEnergyScoreAndCardMovement()
        {
            string playerPhaseScope = null;
            var rule = new PlayerRule(context => context.Combat.Statuses.Count == 0 ? new[] {
                Status(Pause, EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving)),
                Step(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(Protection, 1,
                    StatusDuration.UntilPhaseEnd(playerPhaseScope, "player"))), EffectTargetSpec.Self())
            } : new[] { Step(BattleEffectSpec.GainBlock(0), EffectTargetSpec.Self()) });
            var failingEnemy = new EnemyRule(Step(BattleEffectSpec.Damage(5), EffectTargetSpec.Opponents()),
                Step(BattleEffectSpec.GainBlock(long.MaxValue), EffectTargetSpec.Self()));
            var session = Session(rule, energy: 2, enemies: new[] { Foe(Enemy), Foe(Other), Foe(Last, rule: failingEnemy) });
            playerPhaseScope = BattleStatusTiming.RoundScope(session.State.Duel);
            Start(session); Do(session, Player, CardDuelAction.Play, OneCard(session, Player));
            var before = session.State; var cards = OneCard(session, Player); var statuses = before.Duel.Combat.Statuses.Select(StatusKey).ToArray();
            Assert.AreEqual(2, statuses.Length); Assert.AreEqual(1, before.Duel.Player(Player).Energy.Energy);
            // Demonstrate that the pending automatic chain really reaches each mutation before its final error.
            var primary = session.Engine.Execute(before, before.Revision, Player, CardDuelAction.Play, cards);
            Assert.IsTrue(primary.Accepted); Assert.IsEmpty(Statuses(primary.State, Protection));
            var skipped = session.Engine.Execute(primary.State, primary.State.Revision, Enemy, CardDuelAction.EnemyAction);
            Assert.IsTrue(skipped.Accepted); Assert.IsEmpty(skipped.State.Duel.Combat.Statuses);
            var hit = session.Engine.Execute(skipped.State, skipped.State.Revision, Other, CardDuelAction.EnemyAction);
            Assert.IsTrue(hit.Accepted); Assert.AreEqual(93, hit.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(CardDuelError.Overflow, session.Engine.Execute(hit.State, hit.State.Revision, Last, CardDuelAction.EnemyAction).Error);
            var rejected = session.Execute(before.Revision, Player, CardDuelAction.Play, cards);
            Assert.AreEqual(CardDuelError.Overflow, rejected.Error); Assert.AreSame(before, rejected.State); Assert.AreSame(before, session.State);
            Assert.IsEmpty(rejected.Steps); CollectionAssert.AreEqual(statuses, session.State.Duel.Combat.Statuses.Select(StatusKey));
            Assert.AreEqual(100, session.State.Duel.Combat.Get(Player).Health); Assert.AreEqual(1, session.State.Duel.Scores.Count);
            Assert.AreEqual(1, session.State.Duel.Player(Player).Energy.Energy); Assert.IsEmpty(session.State.Zone(CardZoneKinds.Played, Player).Cards);
            Assert.That(session.State.Zone(CardZoneKinds.Hand, Player).Cards, Does.Contain(cards[0]));
        }
    }
}
