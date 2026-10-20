using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class DesignFlow2951Tests
    {
        private static readonly ParticipantId P = new ParticipantId("flow-player"), E = new ParticipantId("flow-enemy");
        private sealed class PlayerEffects : IScoredHandEffectRule
        {
            private readonly BattleEffectStep[] steps;
            public string Version => "flow2951.effects";
            public PlayerEffects(params BattleEffectStep[] steps)
            {
                this.steps = (steps.Length == 0 ? new[] { new BattleEffectStep(BattleEffectSpec.GainBlock(0), EffectTargetSpec.Self()) } : steps)
                    .Select((step, i) => new BattleEffectStep("flow-step-" + i, step.Effect, step.Target)).ToArray();
            }
            public BattleEffectSequence Resolve(ScoredHandEffectContext context) => new BattleEffectSequence(Version, "flow test", steps);
        }
        private sealed class EnemyEffects : IEnemyBattleEffectRule
        {
            private readonly BattleEffectStep[] steps;
            public string Version => "flow2951.enemy";
            public EnemyEffects(params BattleEffectStep[] steps) { this.steps = steps; }
            public BattleEffectSequence Resolve(EnemyBattleEffectContext context) => new BattleEffectSequence(Version, "flow test", steps);
        }
        private static BattleSession Create(IScoredHandEffectRule effects = null, int deckSize = 30,
            CombatStatusCatalog catalog = null, IEnemyBattleEffectRule enemyEffects = null, int roundLimit = 20,
            long block = 0, bool empty = false)
        {
            var engine = new CardDuelEngine(new BattleRules("flow2951", roundLimit, PlayerBlockExpiry.PlayerTurnStart, catalog));
            var flow = new BattleTurnFlowRules(openingDeal: true, autoDrawToLimit: true,
                discardEndsTurn: false, endDefense: false, forceDiscardOverflow: false);
            var rules = new BattlePlayerRules(handSize: 8, unlimitedDiscards: true, maxDiscardCards: 512,
                dealPolicy: new CardDuelDealPolicy(afterDiscard: CardDuelRefillTiming.DiscardedCount),
                effectRule: effects ?? new PlayerEffects(), discardReward: new ChipBlockRewardRule(1), flow: flow);
            var deck = StandardDeck.Cards().Take(empty ? 0 : deckSize).Select((card, i) => new DeckEntry("flow/" + i, card.Id));
            var state = engine.Create("flow2951", HmacDeckShuffler.FromSeed(new byte[32]),
                new[] { new BattlePlayerSetup(P, new CombatantDefinition("hero", 1000, block), deck, rules: rules) },
                new[] { new BattleEnemySetup(E, new CombatantDefinition("enemy", 10000), 0, effectRule: enemyEffects) });
            var session = new BattleSession(engine, state);
            Act(session, CardDuelAction.Start);
            return session;
        }
        private static CardInstanceId[] Hand(BattleSession session) => session.State.Zone(CardZoneKinds.Hand, P).Cards.ToArray();
        private static BattleSessionResult Act(BattleSession session, CardDuelAction action, IEnumerable<CardInstanceId> cards = null)
        {
            var result = session.Execute(session.State.Revision, P, action, cards);
            Assert.IsTrue(result.Accepted, action + ": " + result.Error);
            return result;
        }

        [Test] public void DiscardReplacesOnlyDiscardedCountAndPlayNeverRefills()
        {
            var s = Create();
            Act(s, CardDuelAction.Play, Hand(s).Take(3));
            Assert.AreEqual(5, Hand(s).Length);
            var result = Act(s, CardDuelAction.Discard, Hand(s).Take(2));
            Assert.AreEqual(5, Hand(s).Length);
            Assert.AreEqual(2, result.Steps.Single().Transition.Draws.Single().DrawnCount);
            Assert.AreEqual(1, s.State.FlowFor(P).ActionsRemaining);
            Assert.AreEqual(0, s.State.Duel.CompletedRounds);
        }

        [TestCase(9, 8, 0)]
        [TestCase(10, 8, 1)]
        public void OverflowDiscardCanExceedHandLimitAndReplacementStopsAtCap(int discard, int hand, int drawn)
        {
            var s = Create(new PlayerEffects(new BattleEffectStep(BattleEffectSpec.DrawCards(10), EffectTargetSpec.Self())));
            Act(s, CardDuelAction.Play, Hand(s).Take(1));
            Assert.AreEqual(17, Hand(s).Length);
            var result = Act(s, CardDuelAction.Discard, Hand(s).Take(discard));
            Assert.AreEqual(hand, Hand(s).Length);
            Assert.AreEqual(drawn, result.Steps.Single().Transition.Draws.Single().DrawnCount);
            Assert.AreEqual(BattlePlayerWindow.Action, s.State.FlowFor(P).Window);
        }

        [Test] public void EndTurnRetainsOverflowWithoutForcedDiscardOrExtraDraw()
        {
            var s = Create(new PlayerEffects(new BattleEffectStep(BattleEffectSpec.DrawCards(10), EffectTargetSpec.Self())));
            Act(s, CardDuelAction.Play, Hand(s).Take(1));
            var held = Hand(s);
            Act(s, CardDuelAction.EndTurn);
            Assert.AreEqual(1, s.State.Duel.CompletedRounds);
            Assert.AreEqual(BattlePlayerWindow.Action, s.State.FlowFor(P).Window);
            CollectionAssert.AreEqual(held, Hand(s));
        }

        [Test] public void LastActionCompletesItsDrawThenEndsAutomaticallyWithoutOverflowPrompt()
        {
            var s = Create(new PlayerEffects(new BattleEffectStep(BattleEffectSpec.DrawCards(3), EffectTargetSpec.Self())));
            for (int i = 0; i < 3; i++) Act(s, CardDuelAction.Play, Hand(s).Take(1));
            Assert.AreEqual(14, Hand(s).Length);
            Assert.AreEqual(1, s.State.Duel.CompletedRounds);
            Assert.AreEqual(BattlePlayerWindow.Action, s.State.FlowFor(P).Window);
            Assert.AreEqual(3, s.State.FlowFor(P).ActionsRemaining);
        }

        [Test] public void EmptyDeckAndDiscardLeaveAnActionWindowForItemsOrManualEnd()
        {
            var s = Create(empty: true);
            Assert.IsFalse(s.State.Duel.IsFinished);
            Assert.AreEqual(0, s.State.Duel.CompletedRounds);
            Assert.IsEmpty(Hand(s));
            Assert.AreEqual(3, s.State.FlowFor(P).ActionsRemaining);
            Act(s, CardDuelAction.EndTurn);
            Assert.AreEqual(1, s.State.Duel.CompletedRounds);
            Assert.AreEqual(BattlePlayerWindow.Action, s.State.FlowFor(P).Window);
        }

        [Test] public void ReplacementDrawExhaustsDeckThenRecyclesOnlyForTheRemainder()
        {
            var s = Create(deckSize: 11);
            var result = Act(s, CardDuelAction.Discard, Hand(s).Take(5));
            var timeline = result.Timeline.ToArray();
            var deals = timeline.Where(t => t.Kind == BattleTimelineKind.CardsDealt).ToArray();
            Assert.AreEqual(8, Hand(s).Length);
            CollectionAssert.AreEqual(new[] { 3, 2 }, deals.Select(d => d.Cards.Count));
            Assert.AreEqual(1, timeline.Count(t => t.Kind == BattleTimelineKind.DeckRecycled));
            Assert.Less(Array.IndexOf(timeline, deals[0]), Array.FindIndex(timeline, t => t.Kind == BattleTimelineKind.DeckRecycled));
            Assert.AreEqual(11, s.State.Table.Cards.Count);
        }

        [Test] public void ShieldAbsorbsEnemyEndPoisonThenExpiresBeforePlayerStartPoison()
        {
            var ending = new StatusDefinitionId("end-poison");
            var starting = new StatusDefinitionId("start-poison");
            var catalog = new CombatStatusCatalog("flow2951.poison", new[] {
                new CombatStatusDefinition(ending, StatusTags.Negative,
                    new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.PhaseEnding, 5, timingSubject: PeriodicTimingSubject.Applier) }, StatusLifetimeSpec.ForTriggers(1)),
                new CombatStatusDefinition(starting, StatusTags.Negative,
                    new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.TurnStarted, 7) }, StatusLifetimeSpec.ForTriggers(1)) });
            var enemy = new EnemyEffects(
                new BattleEffectStep(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(ending)), EffectTargetSpec.Opponents()),
                new BattleEffectStep(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(starting)), EffectTargetSpec.Opponents()));
            var s = Create(catalog: catalog, enemyEffects: enemy, block: 30);
            var result = Act(s, CardDuelAction.EndTurn);
            var facts = result.Steps.SelectMany(step => step.Transition.Events).ToArray();
            var endHit = facts.Single(e => e.Effect.Kind == CombatEffectKind.Damage && e.Effect.Amount == 5);
            var startHit = facts.Single(e => e.Effect.Kind == CombatEffectKind.Damage && e.Effect.Amount == 7);
            var clear = facts.Single(e => e.Effect.Kind == CombatEffectKind.ClearBlock);
            Assert.AreEqual(5, endHit.BlockAbsorbed);
            Assert.AreEqual(7, startHit.DamageToHealth);
            Assert.Less(Array.IndexOf(facts, endHit), Array.IndexOf(facts, clear));
            Assert.Less(Array.IndexOf(facts, clear), Array.IndexOf(facts, startHit));
            Assert.AreEqual(993, s.State.Duel.Combat.Get(P).Health);
        }

        [Test] public void FinalAllowedEnemyPhaseResolvesPoisonBeforeTimeoutAndVictoryWins()
        {
            var poison = new StatusDefinitionId("lethal-end");
            var catalog = new CombatStatusCatalog("flow2951.last", new[] {
                new CombatStatusDefinition(poison, StatusTags.Negative,
                    new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.PhaseEnding, 10000) }, StatusLifetimeSpec.ForTriggers(1)) });
            var s = Create(new PlayerEffects(new BattleEffectStep(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(poison)), EffectTargetSpec.Opponents())),
                catalog: catalog, roundLimit: 1);
            Act(s, CardDuelAction.Play, Hand(s).Take(1));
            Act(s, CardDuelAction.EndTurn);
            Assert.AreEqual(ScoredDuelPhase.Won, s.State.Duel.Phase);
            Assert.AreEqual(1, s.State.Duel.CompletedRounds);
            Assert.AreEqual(7, Hand(s).Length);
        }

        [Test] public void HealthShieldAndActualDamageRemainExactAboveIntMaxValue()
        {
            var state = new CombatState("wide", 0, new[] {
                new CombatantState(P, "hero", "player", 9000000000L, 9000000000L, 3000000000L),
                new CombatantState(E, "enemy", "enemy", 9000000000L, 9000000000L) });
            var hit = CombatEngine.Resolve(state, 0, new[] { new CombatEffect(CombatEffectKind.Damage, E, P, 5000000000L) });
            Assert.IsTrue(hit.Accepted, hit.Error.ToString());
            Assert.AreEqual(3000000000L, hit.Events.Single().BlockAbsorbed);
            Assert.AreEqual(2000000000L, hit.Events.Single().DamageToHealth);
            Assert.AreEqual(7000000000L, hit.State.Get(P).Health);
            var shield = CombatEngine.Resolve(hit.State, hit.State.Revision, new[] { new CombatEffect(CombatEffectKind.GainBlock, P, P, 6000000000L) });
            Assert.IsTrue(shield.Accepted, shield.Error.ToString());
            Assert.AreEqual(6000000000L, shield.State.Get(P).Block);
            var overflow = CombatEngine.Resolve(shield.State, shield.State.Revision, new[] { new CombatEffect(CombatEffectKind.GainBlock, P, P, long.MaxValue) });
            Assert.AreEqual(CombatError.Overflow, overflow.Error);
            Assert.AreSame(shield.State, overflow.State);
        }
    }
}
