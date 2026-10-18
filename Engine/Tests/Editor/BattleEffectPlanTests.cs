using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    public sealed class BattleEffectPlanTests
    {
        private static readonly ParticipantId Player = new ParticipantId("p"), Enemy = new ParticipantId("e"), Other = new ParticipantId("e2");

        // Observe the adapter boundary; these probes do not participate in production rules.
        private sealed class CapturedDamage : IScoredHandDamageRule
        {
            public string Version => "effect-plan.damage.1";
            public DamagePlan LastPlan { get; private set; }
            public DamagePlan Resolve(ScoredHandDamageContext context) => LastPlan = new DamagePlan(Version, "original damage explanation",
                context.Targets.Select(target => new DamageRequest(context.Source, target, context.Score.Total)));
        }
        private sealed class CapturedReward : IDiscardRewardRule
        {
            public string Version => "effect-plan.reward.1";
            public DiscardRewardContext LastContext { get; private set; }
            public DiscardRewardPlan LastPlan { get; private set; }
            public DiscardRewardPlan Resolve(DiscardRewardContext context)
            {
                LastContext = context;
                return LastPlan = new DiscardRewardPlan(Version, "original reward explanation", context.Targets.Select(target =>
                    new CombatEffect(CombatEffectKind.GainBlock, context.Source, target, context.Cards.Count * 8)));
            }
        }
        private sealed class ReadFirstEnemyHealth : IAttackDamageModifier, IIncomingDamageModifier
        {
            public DamageModification Modify(DamageModifierContext context) => new DamageModification(
                "effect-plan.current-health.1", "read first enemy health", context.Amount,
                context.Amount + context.Combat.Get(Enemy).Health / 100);
        }
        private static CardDuelState Create(CardDuelEngine engine, IScoredHandDamageRule damage = null,
            IDiscardRewardRule reward = null, bool start = true)
        {
            var rules = new BattlePlayerRules(unlimitedDiscards: true, damagePolicy: damage, discardReward: reward,
                dealPolicy: new CardDuelDealPolicy(CardDuelRefillTiming.AfterAction, CardDuelRefillTiming.AfterAction));
            var state = engine.Create("effect-plan", HmacDeckShuffler.FromSeed(new byte[32]),
                new[] { new BattlePlayerSetup(Player, new CombatantDefinition("player", 1000), rules: rules) },
                new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("enemy", 10000), 7) });
            return start ? engine.Execute(state, state.Revision, Player, CardDuelAction.Start).State : state;
        }
        private static CardDuelEngine Engine() => new CardDuelEngine(new BattleRules("effect-plan.test.1", 20));

        [Test] public void PlanCopiesItsEffectsPreservesResolvedFactsAndValidatesBounds()
        {
            var attack = new AttackContext(Player, AttackOrigin.Other, 5);
            var modification = new DamageModification("double.1", "double", 5, 10);
            var effect = new CombatEffect(CombatEffectKind.Damage, Player, Enemy, 10, modification, attack);
            var supplied = new[] { effect };
            var plan = new BattleEffectPlan("plan.1", "resolved plan", supplied);
            supplied[0] = null;
            Assert.AreEqual("plan.1", plan.RuleVersion); Assert.AreEqual("resolved plan", plan.Explanation);
            Assert.AreSame(effect, plan.Effects.Single()); Assert.AreSame(attack, plan.Effects[0].Attack);
            Assert.AreSame(modification, plan.Effects[0].AttackModification);
            Assert.Throws<NotSupportedException>(() => ((IList<CombatEffect>)plan.Effects)[0] = null);
            Assert.IsEmpty(new BattleEffectPlan("empty.1", "empty", Array.Empty<CombatEffect>()).Effects);
            Assert.AreEqual(64, new BattleEffectPlan("maximum.1", "maximum", Enumerable.Repeat(effect, 64)).Effects.Count);
            Assert.Throws<ArgumentException>(() => new BattleEffectPlan(" ", "explanation", new[] { effect }));
            Assert.Throws<ArgumentException>(() => new BattleEffectPlan("plan.1", " ", new[] { effect }));
            Assert.Throws<ArgumentNullException>(() => new BattleEffectPlan("plan.1", "null collection", null));
            Assert.Throws<ArgumentException>(() => new BattleEffectPlan("plan.1", "null item", new CombatEffect[] { null }));
            Assert.Throws<ArgumentException>(() => new BattleEffectPlan("plan.1", "too many", Enumerable.Repeat(effect, 65)));
        }

        [Test] public void PlayPreservesLegacyDamageAndOnlyEffectActionsReturnPlans()
        {
            var damage = new CapturedDamage(); var engine = Engine(); var initial = Create(engine, damage, start: false);
            var started = engine.Execute(initial, initial.Revision, Player, CardDuelAction.Start);
            Assert.IsTrue(started.Accepted); Assert.IsNull(started.EffectPlan);
            var state = started.State; var cards = state.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            var rejected = engine.Execute(state, state.Revision, Player, CardDuelAction.Play, cards, new[] { Player });
            Assert.AreEqual(CardDuelError.InvalidTarget, rejected.Error); Assert.IsNull(rejected.EffectPlan); Assert.AreSame(state, rejected.State);
            var preview = engine.PreviewPlay(state, state.Revision, Player, cards);
            Assert.IsTrue(preview.Accepted); Assert.AreSame(damage.LastPlan, preview.Damage);
            var played = engine.Execute(state, state.Revision, Player, CardDuelAction.Play, cards);
            Assert.IsTrue(played.Accepted); Assert.AreSame(damage.LastPlan, played.Damage); Assert.IsNull(played.DiscardReward);
            Assert.AreEqual(played.Damage.RuleVersion, played.EffectPlan.RuleVersion);
            Assert.AreEqual(played.Damage.Explanation, played.EffectPlan.Explanation);
            Assert.AreSame(played.Events.Single().Effect, played.EffectPlan.Effects.Single());
            CollectionAssert.AreEqual(played.Events, played.RuleEvents);
            Assert.AreEqual(AttackOrigin.ScoredHand, played.EffectPlan.Effects[0].Attack.Origin);
            Assert.AreSame(played.Pattern, played.EffectPlan.Effects[0].Attack.Hand.Pattern);
            Assert.AreEqual(preview.EffectPlan.Effects[0].Amount, played.EffectPlan.Effects[0].Amount);
            Assert.AreEqual(state.Duel.Combat.Revision + 1, played.State.Duel.Combat.Revision);
            var collected = engine.Execute(played.State, played.State.Revision, Player, CardDuelAction.CollectPlayed);
            Assert.IsTrue(collected.Accepted); Assert.IsNull(collected.EffectPlan); Assert.AreSame(played.State.Duel.Combat, collected.State.Duel.Combat);
            var ended = engine.Execute(collected.State, collected.State.Revision, Player, CardDuelAction.EndTurn);
            Assert.IsTrue(ended.Accepted); Assert.IsNull(ended.EffectPlan); Assert.AreSame(collected.State.Duel.Combat, ended.State.Duel.Combat);
        }

        [Test] public void DiscardPreservesLegacyContextAndTheOriginalRewardEffects()
        {
            var reward = new CapturedReward(); var engine = Engine(); var state = Create(engine, reward: reward);
            var cards = state.Zone(CardZoneKinds.Hand, Player).Cards.Take(2).ToArray();
            var preview = engine.PreviewDiscard(state, state.Revision, Player, cards);
            Assert.IsTrue(preview.Accepted); Assert.AreSame(reward.LastContext, preview.DiscardReward.Context);
            var result = engine.Execute(state, state.Revision, Player, CardDuelAction.Discard, cards);
            Assert.IsTrue(result.Accepted); Assert.AreSame(reward.LastContext, result.DiscardReward.Context);
            Assert.AreSame(reward.LastPlan, result.DiscardReward.Plan); Assert.IsNull(result.Damage); Assert.IsNull(result.Score);
            Assert.AreEqual(reward.LastPlan.RuleVersion, result.EffectPlan.RuleVersion);
            Assert.AreEqual(reward.LastPlan.Explanation, result.EffectPlan.Explanation);
            Assert.AreSame(reward.LastPlan.Effects.Single(), result.EffectPlan.Effects.Single());
            Assert.AreSame(result.EffectPlan.Effects[0], result.Events.Single().Effect);
            CollectionAssert.AreEqual(result.Events, result.RuleEvents);
            Assert.IsNull(result.EffectPlan.Effects[0].Attack); Assert.AreEqual(16, result.State.Duel.Combat.Get(Player).Block);
            Assert.AreEqual(preview.Events.Single().After.Block, result.Events.Single().After.Block);
            CollectionAssert.AreEqual(cards, result.DiscardReward.Context.Cards.Select(card => card.Id));
            Assert.AreEqual(state.Duel.Combat.Revision + 1, result.State.Duel.Combat.Revision);
            Assert.AreEqual(2, result.State.Duel.Player(Player).Energy.Energy);
        }

        [Test] public void EmptyDiscardHasAnEmptyPlanWithoutACombatRevisionOrSyntheticEvent()
        {
            var engine = Engine(); var state = Create(engine);
            var result = engine.Execute(state, state.Revision, Player, CardDuelAction.Discard,
                state.Zone(CardZoneKinds.Hand, Player).Cards.Take(1));
            Assert.IsTrue(result.Accepted); Assert.IsNotNull(result.EffectPlan); Assert.IsEmpty(result.EffectPlan.Effects);
            Assert.IsNotNull(result.DiscardReward); Assert.IsEmpty(result.DiscardReward.Plan.Effects); Assert.IsEmpty(result.Events);
            Assert.IsEmpty(result.RuleEvents);
            Assert.AreEqual(result.DiscardReward.Plan.RuleVersion, result.EffectPlan.RuleVersion);
            Assert.AreEqual(result.DiscardReward.Plan.Explanation, result.EffectPlan.Explanation);
            Assert.AreSame(state.Duel.Combat, result.State.Duel.Combat);
            Assert.AreEqual(state.Revision + 1, result.State.Revision); Assert.AreEqual(state.Duel.Revision + 1, result.State.Duel.Revision);
            Assert.AreEqual(2, result.State.Duel.Player(Player).Energy.Energy);
        }

        [Test] public void EnemyActionReturnsItsOwnResolvedPlanWithoutPlayerDamageOrRewardMetadata()
        {
            var engine = Engine(); var state = Create(engine);
            state = engine.Execute(state, state.Revision, Player, CardDuelAction.EndTurn).State;
            var result = engine.Execute(state, state.Revision, Enemy, CardDuelAction.EnemyAction);
            Assert.IsTrue(result.Accepted); Assert.IsNotNull(result.EffectPlan);
            Assert.IsNull(result.Damage); Assert.IsNull(result.DiscardReward); Assert.IsNull(result.Score); Assert.IsNull(result.Pattern);
            Assert.AreSame(result.Events.Single().Effect, result.EffectPlan.Effects.Single());
            CollectionAssert.AreEqual(result.Events, result.RuleEvents);
            var effect = result.EffectPlan.Effects[0];
            Assert.AreEqual(Enemy, effect.Source); Assert.AreEqual(Player, effect.Target); Assert.AreEqual(7, effect.Amount);
            Assert.AreEqual(AttackOrigin.EnemyAttack, effect.Attack.Origin); Assert.IsNull(effect.Attack.Hand);
            Assert.AreEqual(state.Duel.Combat.Revision + 1, result.State.Duel.Combat.Revision);
        }

        [Test] public void AttackModifiersStillReadTheStartSnapshotAndIncomingModifiersReadTheCurrentBatch()
        {
            var modifier = new ReadFirstEnemyHealth(); var engine = new TeamBattleEngine(new BattleRules("modifier-order.1", 20));
            var state = engine.Create("modifier-order", new[] {
                new BattlePlayerSetup(Player, new CombatantDefinition("player", 1000), damageRules: new BattleActorDamageRules(modifier),
                    targetRules: new BattleActorTargetRules(new OpponentTargetPolicy(OpponentTargetSelection.AllLiving)))
            }, new[] {
                new BattleEnemySetup(Enemy, new CombatantDefinition("enemy", 1000), 7, new BattleActorDamageRules(incomingDamage: modifier)),
                new BattleEnemySetup(Other, new CombatantDefinition("other", 1000), 7, new BattleActorDamageRules(incomingDamage: modifier))
            });
            var result = engine.Execute(state, state.Revision, Player, ScoredDuelActionKind.ScoredHand,
                ScoreEngine.Fixed(engine.NextScoreId(state), 10));
            Assert.IsTrue(result.Accepted); Assert.IsNotNull(result.EffectPlan);
            CollectionAssert.AreEqual(new long[] { 20, 20 }, result.EffectPlan.Effects.Select(effect => effect.Amount));
            CollectionAssert.AreEqual(new long[] { 30, 29 }, result.Events.Select(hit => hit.SettledDamage));
            CollectionAssert.AreEqual(new[] { 0, 1 }, result.Events.Select(hit => hit.Index));
            Assert.IsTrue(result.Events.All(hit => hit.Revision == state.Combat.Revision + 1));
            Assert.AreEqual(state.Combat.Revision + 1, result.State.Combat.Revision);
            Assert.AreEqual(state.Revision + 1, result.State.Revision); Assert.AreEqual(1000, state.Combat.Get(Enemy).Health);
            Assert.AreEqual(970, result.State.Combat.Get(Enemy).Health); Assert.AreEqual(971, result.State.Combat.Get(Other).Health);
        }

        [Test] public void EnemyPhaseBlockExpiryKeepsItsSeparateBatchRevisionAndEventIndex()
        {
            var engine = new TeamBattleEngine(new BattleRules("expiry-order.1", 20, PlayerBlockExpiry.AfterEnemyPhase));
            var state = engine.Create("expiry-order", new[] { new BattlePlayerSetup(Player, new CombatantDefinition("player", 1000, 40)) },
                new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("enemy", 1000), 5), new BattleEnemySetup(Other, new CombatantDefinition("other", 1000), 7) });
            state = engine.Execute(state, state.Revision, Player, ScoredDuelActionKind.EndPlayerTurn).State;
            var first = engine.Execute(state, state.Revision, Enemy, ScoredDuelActionKind.EnemyAction);
            var last = engine.Execute(first.State, first.State.Revision, Other, ScoredDuelActionKind.EnemyAction);
            Assert.IsTrue(first.Accepted); Assert.IsTrue(last.Accepted); Assert.AreEqual(1, last.EffectPlan.Effects.Count);
            Assert.AreEqual(2, last.Events.Count); Assert.AreSame(last.EffectPlan.Effects.Single(), last.Events[0].Effect);
            CollectionAssert.AreEqual(last.Events, last.RuleEvents);
            Assert.AreEqual(CombatEffectKind.ClearBlock, last.Events[1].Effect.Kind);
            Assert.AreEqual(first.State.Combat.Revision + 1, last.Events[0].Revision);
            Assert.AreEqual(first.State.Combat.Revision + 2, last.Events[1].Revision);
            Assert.AreEqual(0, last.Events[0].Index); Assert.AreEqual(0, last.Events[1].Index);
            Assert.AreEqual(first.State.Combat.Revision + 2, last.State.Combat.Revision);
            Assert.AreEqual(first.State.Revision + 1, last.State.Revision); Assert.AreEqual(0, last.State.Combat.Get(Player).Block);
            Assert.AreEqual(3, last.State.Player(Player).Energy.Energy);
        }
    }
}
