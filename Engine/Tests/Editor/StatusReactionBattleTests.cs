using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class StatusReactionBattleTests
    {
        private static readonly ParticipantId Player = new ParticipantId("status-reaction-player"), Enemy = new ParticipantId("status-reaction-enemy"),
            Other = new ParticipantId("status-reaction-other");
        private static readonly StatusDefinitionId Reflect = new StatusDefinitionId("configured-reflect"),
            Leech = new StatusDefinitionId("configured-leech"), Overflow = new StatusDefinitionId("configured-overflow");

        private sealed class SequenceRule : IScoredHandEffectRule
        {
            private readonly BattleEffectStep[] steps;
            public string Version => "status-reaction.sequence.1";
            public SequenceRule(params BattleEffectStep[] steps) { this.steps = steps; }
            public BattleEffectSequence Resolve(ScoredHandEffectContext context) => new BattleEffectSequence(Version, "configured status reactions", steps);
        }
        private sealed class EnemySequenceRule : IEnemyBattleEffectRule
        {
            private readonly BattleEffectStep[] steps;
            public string Version => "status-reaction.enemy.1";
            public EnemySequenceRule(params BattleEffectStep[] steps) { this.steps = steps; }
            public BattleEffectSequence Resolve(EnemyBattleEffectContext context) => new BattleEffectSequence(Version, "enemy applies its own status", steps);
        }
        private sealed class HealingDiscardRule : IDiscardRewardRule
        {
            public string Version => "status-reaction.discard-heal.1";
            public DiscardRewardPlan Resolve(DiscardRewardContext context) => new DiscardRewardPlan(Version, "heal selected ally on discard",
                context.Targets.Select(target => new CombatEffect(CombatEffectKind.Heal, context.Source, target, 5)));
        }
        private static BattleEffectStep Self(BattleEffectSpec effect) => new BattleEffectStep(effect, EffectTargetSpec.Self());
        private static BattleEffectStep Opponent(BattleEffectSpec effect) => new BattleEffectStep(effect,
            EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving));
        private static BattleEffectStep Apply(StatusDefinitionId id, int stacks = 1, bool toOpponent = false) =>
            toOpponent ? Opponent(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(id, stacks))) :
                Self(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(id, stacks)));
        private static CombatStatusDefinition Definition(StatusDefinitionId id, StatusBehaviorSpec behavior, int? charges = null) =>
            new CombatStatusDefinition(id, StatusTags.None, new[] { behavior },
                charges.HasValue ? StatusLifetimeSpec.ForTriggers(charges.Value) : StatusLifetimeSpec.UntilRemoved());
        private static CombatStatusCatalog Catalog(params CombatStatusDefinition[] definitions) => new CombatStatusCatalog("status-reaction.catalog.1", definitions);
        private static BattleSession Session(CombatStatusCatalog catalog, IScoredHandEffectRule rule, int playerHealth = 40,
            int playerBlock = 0, int energy = 3, int enemyHealth = 100, int enemyBlock = 0, int? otherHealth = null,
            IDiscardRewardRule discardReward = null, BattleActorDamageRules playerDamageRules = null,
            BattleActorDamageRules enemyDamageRules = null, BattleActorDamageRules otherDamageRules = null,
            IEnemyBattleEffectRule enemyRule = null, CombatReactionLimits limits = null)
        {
            // Built-in behavior comes from the catalog. No custom reaction provider is installed.
            var engine = new CardDuelEngine(new BattleRules("status-reaction.battle.1", 10, statusCatalog: catalog, reactionLimits: limits));
            var rules = new BattlePlayerRules(maxEnergy: energy, initialEnergy: energy, discards: 3,
                effectRule: rule, discardReward: discardReward,
                dealPolicy: new CardDuelDealPolicy(CardDuelRefillTiming.AfterAction, CardDuelRefillTiming.AfterAction));
            var enemies = new List<BattleEnemySetup> { new BattleEnemySetup(Enemy, new CombatantDefinition("foe", enemyHealth, enemyBlock), 7,
                damageRules: enemyDamageRules, effectRule: enemyRule) };
            if (otherHealth.HasValue) enemies.Add(new BattleEnemySetup(Other, new CombatantDefinition("other", otherHealth.Value), 5,
                damageRules: otherDamageRules));
            var state = engine.Create("status-reaction", HmacDeckShuffler.FromSeed(new byte[32]),
                new[] { new BattlePlayerSetup(Player, new CombatantDefinition("hero", 100, playerBlock), initialHealth: playerHealth,
                    rules: rules, damageRules: playerDamageRules) }, enemies);
            var session = new BattleSession(engine, state);
            Do(session, CardDuelAction.Start);
            return session;
        }
        private static CardInstanceId[] OneCard(BattleSession session) => session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
        private static BattleSessionResult Do(BattleSession session, CardDuelAction action, IEnumerable<CardInstanceId> cards = null)
        {
            var result = session.Execute(session.State.Revision, Player, action, cards);
            Assert.IsTrue(result.Accepted, action + ": " + result.Error); result.State.Table.Validate(); return result;
        }
        private static StatusInstance FindStatus(CardDuelState state, StatusDefinitionId definition, ParticipantId owner) =>
            state.Duel.Combat.Statuses.Single(status => status.DefinitionId == definition && status.Target.Id == owner.Value);
        private static IEnumerable<CombatEvent> Derived(CardDuelTransition result, CombatEffectOrigin origin) =>
            result.Events.Where(item => item.Provenance.Origin == origin);
        private static void SameState(CardDuelState expected, CardDuelState actual)
        {
            Assert.AreEqual(expected.Revision, actual.Revision); Assert.AreEqual(expected.Duel.Revision, actual.Duel.Revision);
            Assert.AreEqual(expected.Duel.Combat.Revision, actual.Duel.Combat.Revision); Assert.AreEqual(expected.Duel.Phase, actual.Duel.Phase);
            Assert.AreEqual(expected.Duel.Player(Player).Energy.Energy, actual.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(expected.DiscardsFor(Player), actual.DiscardsFor(Player));
            CollectionAssert.AreEqual(expected.Random.StateBytes, actual.Random.StateBytes);
            CollectionAssert.AreEqual(expected.Duel.Scores.Select(score => score.Total), actual.Duel.Scores.Select(score => score.Total));
            CollectionAssert.AreEqual(expected.Duel.Combat.Statuses.Select(status => status.Id), actual.Duel.Combat.Statuses.Select(status => status.Id));
            foreach (var status in expected.Duel.Combat.Statuses)
            {
                var other = actual.Duel.Combat.Statuses.Single(candidate => candidate.Id == status.Id);
                Assert.AreEqual(status.Stacks, other.Stacks); Assert.AreEqual(status.Duration.RemainingTriggers, other.Duration.RemainingTriggers);
            }
            foreach (var unit in expected.Duel.Combat.Combatants)
            {
                Assert.AreEqual(unit.Health, actual.Duel.Combat.Get(unit.Id).Health);
                Assert.AreEqual(unit.Block, actual.Duel.Combat.Get(unit.Id).Block);
            }
            foreach (var zone in expected.Table.Zones) CollectionAssert.AreEqual(zone.Cards, actual.Table.GetZone(zone.Id).Cards);
        }

        [Test] public void RealStatusConfigurationProducesTheSamePreviewAndCommittedLifestealOverflowChain()
        {
            var catalog = Catalog(Definition(Leech, new LifeStealSpec(0.25m)), Definition(Overflow, new OverhealDamageSpec(2m)));
            var session = Session(catalog, new SequenceRule(Apply(Leech, 2), Apply(Overflow), Opponent(BattleEffectSpec.Damage(10))), playerHealth: 98);
            var before = session.State; var cards = OneCard(session);
            var first = session.Engine.PreviewPlay(before, before.Revision, Player, cards);
            var second = session.Engine.PreviewPlay(before, before.Revision, Player, cards);
            Assert.IsTrue(first.Accepted); Assert.IsTrue(second.Accepted); Assert.AreSame(before, session.State);
            SameState(first.State, second.State);
            var actual = Do(session, CardDuelAction.Play, cards).Steps[0].Transition;
            SameState(first.State, actual.State);
            Assert.AreEqual(3, actual.EffectPlan.Effects.Count); Assert.AreEqual(100, actual.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(84, actual.State.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(2, actual.State.Duel.Combat.Statuses.Count);
            var healed = Derived(actual, CombatEffectOrigin.Lifesteal).Single(); var overflow = Derived(actual, CombatEffectOrigin.Overheal).Single();
            Assert.AreEqual(5, healed.Effect.Amount); Assert.AreEqual(2, healed.HealthRestored); Assert.AreEqual(3, healed.Overheal);
            Assert.AreEqual(6, overflow.Effect.Amount); Assert.AreEqual(2, overflow.Provenance.Depth);
            Assert.AreEqual(healed.SequenceIndex, overflow.Provenance.ParentSequenceIndex); Assert.AreEqual(healed.Revision, overflow.Provenance.ParentRevision);
            Assert.AreEqual(2, actual.State.Duel.Player(Player).Energy.Energy); Assert.AreEqual(1, actual.State.Duel.Scores.Count);
            Assert.IsEmpty(before.Duel.Combat.Statuses); Assert.AreEqual(98, before.Duel.Combat.Get(Player).Health);
        }

        [Test] public void LastEnemyKillStillHealsOnlyActualHealthDamageAndConsumesOneCharge()
        {
            var session = Session(Catalog(Definition(Leech, new LifeStealSpec(0.5m), 2)),
                new SequenceRule(Apply(Leech), Opponent(BattleEffectSpec.Damage(100))), enemyHealth: 6);
            var cards = OneCard(session); var result = Do(session, CardDuelAction.Play, cards);
            var play = result.Steps[0].Transition; var hit = play.Events.Single(item => item.Provenance.Origin == CombatEffectOrigin.Primary);
            Assert.AreEqual(ScoredDuelPhase.Won, session.State.Duel.Phase); Assert.AreEqual(1, result.Steps.Count);
            Assert.AreEqual(6, hit.DamageToHealth); Assert.AreEqual(94, hit.Overkill);
            Assert.AreEqual(3, Derived(play, CombatEffectOrigin.Lifesteal).Single().HealthRestored);
            Assert.AreEqual(43, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(1, FindStatus(session.State, Leech, Player).Duration.RemainingTriggers);
            CollectionAssert.AreEqual(cards, session.State.Zone(CardZoneKinds.Played, Player).Cards);
        }

        [Test] public void OverhealChoosesTheNextLivingEnemyAfterThePrimaryKill()
        {
            var catalog = Catalog(Definition(Leech, new LifeStealSpec(1m)), Definition(Overflow, new OverhealDamageSpec(1m)));
            var session = Session(catalog, new SequenceRule(Apply(Leech), Apply(Overflow), Opponent(BattleEffectSpec.Damage(100))),
                playerHealth: 100, enemyHealth: 6, otherHealth: 20);
            var play = Do(session, CardDuelAction.Play, OneCard(session)).Steps[0].Transition;
            var overflow = Derived(play, CombatEffectOrigin.Overheal).Single();
            Assert.AreEqual(Other, overflow.Effect.Target); Assert.AreEqual(6, overflow.Effect.Amount);
            Assert.AreEqual(0, session.State.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(14, session.State.Duel.Combat.Get(Other).Health);
            Assert.AreEqual(ScoredDuelPhase.AwaitingPlayerHand, session.State.Duel.Phase);
        }

        [Test] public void AoeOverhealAppliesFullScaledAmountToEachEnemyButConsumesOneCharge()
        {
            var catalog = Catalog(Definition(Overflow, new OverhealDamageSpec(0.5m,
                CombatTargetSpec.Opponents(CombatTargetSelection.AllLiving)), 2));
            var session = Session(catalog, new SequenceRule(Apply(Overflow, 3), Self(BattleEffectSpec.Heal(5))), playerHealth: 100, otherHealth: 80);
            var play = Do(session, CardDuelAction.Play, OneCard(session)).Steps[0].Transition;
            var damage = Derived(play, CombatEffectOrigin.Overheal).ToArray();
            CollectionAssert.AreEqual(new[] { Enemy, Other }, damage.Select(item => item.Effect.Target));
            Assert.IsTrue(damage.All(item => item.Effect.Amount == 7));
            Assert.AreEqual(93, session.State.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(73, session.State.Duel.Combat.Get(Other).Health);
            Assert.AreEqual(1, FindStatus(session.State, Overflow, Player).Duration.RemainingTriggers);
            Assert.AreEqual(1, play.RuleEvents.OfType<CombatStatusChangedEvent>().Count(item => item.Reason == CombatStatusChangeReason.Consumed));
        }

        [Test] public void EnemyStatusHolderReflectsEvenWhenTheStatusWasAppliedByTheAttacker()
        {
            var session = Session(Catalog(Definition(Reflect, new ReflectBlockedDamageSpec(0.5m))),
                new SequenceRule(Apply(Reflect, 3, true), Opponent(BattleEffectSpec.Damage(10))), enemyBlock: 8,
                playerDamageRules: new BattleActorDamageRules(incomingDamage: new ScaledIncomingDamageModifier(0.5m)));
            var play = Do(session, CardDuelAction.Play, OneCard(session)).Steps[0].Transition;
            var status = FindStatus(session.State, Reflect, Enemy); var reflected = Derived(play, CombatEffectOrigin.Reflection).Single();
            Assert.AreEqual(Player.Value, status.Source.Id); Assert.AreEqual(Enemy.Value, status.Target.Id);
            Assert.AreEqual(Enemy, reflected.Effect.Source); Assert.AreEqual(Player, reflected.Effect.Target);
            Assert.AreEqual(12, reflected.Effect.Amount); Assert.AreEqual(6, reflected.SettledDamage);
            Assert.AreEqual(34, session.State.Duel.Combat.Get(Player).Health); Assert.AreEqual(98, session.State.Duel.Combat.Get(Enemy).Health);
        }

        [Test] public void EnemyActionCanApplyItsOwnReflectionStatusAndPreviewSubsequentHits()
        {
            var enemyRule = new EnemySequenceRule(Apply(Reflect), Self(BattleEffectSpec.GainBlock(8)), Opponent(BattleEffectSpec.Damage(7)));
            var session = Session(Catalog(Definition(Reflect, new ReflectBlockedDamageSpec(1m))),
                new SequenceRule(Opponent(BattleEffectSpec.Damage(10))), enemyRule: enemyRule);
            Do(session, CardDuelAction.EndTurn);
            Assert.AreEqual(Enemy.Value, FindStatus(session.State, Reflect, Enemy).Source.Id);
            var before = session.State; var cards = OneCard(session);
            var preview = session.Engine.PreviewPlay(before, before.Revision, Player, cards);
            Assert.IsTrue(preview.Accepted); Assert.AreEqual(8, Derived(preview, CombatEffectOrigin.Reflection).Single().DamageToHealth);
            var actual = Do(session, CardDuelAction.Play, cards).Steps[0].Transition;
            SameState(preview.State, actual.State); Assert.AreEqual(25, session.State.Duel.Combat.Get(Player).Health);
        }

        [Test] public void ReflectedDamageDoesNotRecursivelyReflectOrCreateLifesteal()
        {
            var catalog = Catalog(Definition(Reflect, new ReflectBlockedDamageSpec(1m), 2),
                Definition(Leech, new LifeStealSpec(1m), 2), Definition(Overflow, new OverhealDamageSpec(1m), 2));
            var rule = new SequenceRule(Apply(Reflect), Apply(Reflect, toOpponent: true), Apply(Leech), Apply(Leech, toOpponent: true), Apply(Overflow),
                Self(BattleEffectSpec.GainBlock(10)), Opponent(BattleEffectSpec.GainBlock(10)), Opponent(BattleEffectSpec.Damage(3)));
            var session = Session(catalog, rule, playerHealth: 100);
            var play = Do(session, CardDuelAction.Play, OneCard(session)).Steps[0].Transition;
            Assert.AreEqual(2, play.Events.Count(item => item.Effect.Kind == CombatEffectKind.Damage));
            Assert.AreEqual(1, Derived(play, CombatEffectOrigin.Reflection).Count());
            Assert.IsEmpty(Derived(play, CombatEffectOrigin.Lifesteal)); Assert.IsEmpty(Derived(play, CombatEffectOrigin.Overheal));
            Assert.AreEqual(7, session.State.Duel.Combat.Get(Player).Block); Assert.AreEqual(7, session.State.Duel.Combat.Get(Enemy).Block);
            Assert.AreEqual(2, FindStatus(session.State, Reflect, Player).Duration.RemainingTriggers);
            Assert.AreEqual(1, FindStatus(session.State, Reflect, Enemy).Duration.RemainingTriggers);
            Assert.AreEqual(2, FindStatus(session.State, Leech, Enemy).Duration.RemainingTriggers);
        }

        [Test] public void RegisteredButUnappliedBehaviorsKeepExistingPlayAndDiscardResults()
        {
            var catalog = Catalog(Definition(Reflect, new ReflectBlockedDamageSpec(1m)), Definition(Leech, new LifeStealSpec(1m)),
                Definition(Overflow, new OverhealDamageSpec(1m)));
            var rule = new SequenceRule(Opponent(BattleEffectSpec.Damage(9)));
            var baseline = Session(null, rule, discardReward: new PerCardBlockRewardRule(5));
            var configured = Session(catalog, rule, discardReward: new PerCardBlockRewardRule(5));
            foreach (var action in new[] { CardDuelAction.Play, CardDuelAction.Discard, CardDuelAction.EndTurn })
            {
                var left = Do(baseline, action, action == CardDuelAction.EndTurn ? null : OneCard(baseline));
                var right = Do(configured, action, action == CardDuelAction.EndTurn ? null : OneCard(configured));
                SameState(left.State, right.State);
                CollectionAssert.AreEqual(left.Steps.SelectMany(step => step.Transition.Events).Select(item => item.Effect.Kind),
                    right.Steps.SelectMany(step => step.Transition.Events).Select(item => item.Effect.Kind));
                Assert.IsTrue(right.Steps.SelectMany(step => step.Transition.RuleEvents)
                    .All(item => item.Provenance == null || item.Provenance.Origin == CombatEffectOrigin.Primary));
            }
            Assert.AreEqual(91, configured.State.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(38, configured.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(2, configured.State.DiscardsFor(Player)); Assert.AreEqual(1, configured.State.Duel.Scores.Count);
        }

        [Test] public void DiscardHealingUsesTheSameOverhealBehaviorAndTargetIncomingModifier()
        {
            var session = Session(Catalog(Definition(Overflow, new OverhealDamageSpec(2m), 2)), new SequenceRule(Apply(Overflow)),
                playerHealth: 100, discardReward: new HealingDiscardRule(),
                enemyDamageRules: new BattleActorDamageRules(incomingDamage: new ScaledIncomingDamageModifier(0.5m)));
            Do(session, CardDuelAction.Play, OneCard(session));
            var before = session.State; var cards = OneCard(session);
            var preview = session.Engine.PreviewDiscard(before, before.Revision, Player, cards);
            var actual = Do(session, CardDuelAction.Discard, cards).Steps[0].Transition;
            Assert.IsTrue(preview.Accepted); SameState(preview.State, actual.State);
            var damage = Derived(actual, CombatEffectOrigin.Overheal).Single();
            Assert.AreEqual(10, damage.Effect.Amount); Assert.AreEqual(5, damage.SettledDamage);
            Assert.AreEqual(95, session.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(1, FindStatus(session.State, Overflow, Player).Duration.RemainingTriggers);
            Assert.AreEqual(1, session.State.Duel.Player(Player).Energy.Energy); Assert.AreEqual(2, session.State.DiscardsFor(Player));
            Assert.AreEqual(1, session.State.Duel.Scores.Count); Assert.AreEqual(1, actual.DiscardReward.Plan.Effects.Count);
        }

        [Test] public void FailedLifestealOverflowChainRollsBackAppliedStatusesChargesCardsAndScore()
        {
            var catalog = Catalog(Definition(Leech, new LifeStealSpec(1m), 2), Definition(Overflow, new OverhealDamageSpec(1m), 2));
            var session = Session(catalog, new SequenceRule(Apply(Leech), Apply(Overflow), Opponent(BattleEffectSpec.Damage(10))),
                playerHealth: 99, limits: new CombatReactionLimits(maxEffects: 1));
            var before = session.State; var cards = OneCard(session);
            var preview = session.Engine.PreviewPlay(before, before.Revision, Player, cards);
            Assert.AreEqual(CardDuelError.ReactionLimitExceeded, preview.Error); Assert.AreSame(before, preview.State); Assert.IsEmpty(preview.RuleEvents);
            var result = session.Execute(before.Revision, Player, CardDuelAction.Play, cards);
            Assert.AreEqual(CardDuelError.ReactionLimitExceeded, result.Error); Assert.AreSame(before, result.State); Assert.AreSame(before, session.State);
            Assert.IsEmpty(result.Steps); Assert.IsEmpty(session.State.Duel.Combat.Statuses); Assert.IsEmpty(session.State.Duel.Scores);
            Assert.AreEqual(3, session.State.Duel.Player(Player).Energy.Energy); Assert.AreEqual(3, session.State.DiscardsFor(Player));
            Assert.AreEqual(99, session.State.Duel.Combat.Get(Player).Health); Assert.AreEqual(100, session.State.Duel.Combat.Get(Enemy).Health);
        }

        [Test] public void LaterEnemyReflectionFailureRollsBackEarlierSuccessfulDamageAndConsumption()
        {
            var session = Session(Catalog(Definition(Reflect, new ReflectBlockedDamageSpec(1m), 2)),
                new SequenceRule(Apply(Reflect), Self(BattleEffectSpec.GainBlock(10))), energy: 1, otherHealth: 100,
                otherDamageRules: new BattleActorDamageRules(incomingDamage: new ScaledIncomingDamageModifier(decimal.MaxValue)));
            var before = session.State; var cards = OneCard(session);
            var played = session.Engine.Execute(before, before.Revision, Player, CardDuelAction.Play, cards);
            Assert.IsTrue(played.Accepted);
            var firstEnemy = session.Engine.Execute(played.State, played.State.Revision, Enemy, CardDuelAction.EnemyAction);
            Assert.IsTrue(firstEnemy.Accepted); Assert.AreEqual(93, firstEnemy.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(3, firstEnemy.State.Duel.Combat.Get(Player).Block);
            Assert.AreEqual(1, FindStatus(firstEnemy.State, Reflect, Player).Duration.RemainingTriggers);
            var failedEnemy = session.Engine.Execute(firstEnemy.State, firstEnemy.State.Revision, Other, CardDuelAction.EnemyAction);
            Assert.AreEqual(CardDuelError.Overflow, failedEnemy.Error); Assert.AreSame(firstEnemy.State, failedEnemy.State); Assert.IsEmpty(failedEnemy.RuleEvents);
            var result = session.Execute(before.Revision, Player, CardDuelAction.Play, cards);
            Assert.AreEqual(CardDuelError.Overflow, result.Error); Assert.AreSame(before, session.State); Assert.AreSame(before, result.State);
            Assert.IsEmpty(result.Steps); Assert.IsEmpty(session.State.Duel.Combat.Statuses); Assert.IsEmpty(session.State.Duel.Scores);
            Assert.AreEqual(1, session.State.Duel.Player(Player).Energy.Energy); Assert.AreEqual(0, session.State.Duel.Combat.Get(Player).Block);
            Assert.AreEqual(40, session.State.Duel.Combat.Get(Player).Health); Assert.AreEqual(100, session.State.Duel.Combat.Get(Enemy).Health);
        }
    }
}
