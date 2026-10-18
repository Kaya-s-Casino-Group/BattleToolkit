using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    public sealed class BattleConfigurationTests
    {
        private static readonly ParticipantId P = new ParticipantId("p"), A = new ParticipantId("a"), E = new ParticipantId("e"), E2 = new ParticipantId("e2");
        private static CardDuelEngine Engine(PlayerBlockExpiry expiry = PlayerBlockExpiry.Retain, int rounds = 20) =>
            new CardDuelEngine(new BattleRules("config.test", rounds, playerBlockExpiry: expiry));
        private static CardDuelState Start(CardDuelEngine engine, BattlePlayerRules rules = null, int block = 0, int damage = 7,
            bool twoEnemies = false, bool ally = false, int health = 800, int enemyHealth = 10000, bool smallDeck = false)
        {
            var deck = smallDeck ? StandardDeck.Cards().Take(8).Select((c, i) => new DeckEntry("c" + i, c.Id)).ToArray() : null;
            var players = new List<BattlePlayerSetup> { new BattlePlayerSetup(P, new CombatantDefinition("p", health, block), deck, rules: rules) };
            if (ally) players.Add(new BattlePlayerSetup(A, new CombatantDefinition("a", 800, block)));
            var enemies = new List<BattleEnemySetup> { new BattleEnemySetup(E, new CombatantDefinition("e", enemyHealth, 10), damage) };
            if (twoEnemies) enemies.Add(new BattleEnemySetup(E2, new CombatantDefinition("e2", enemyHealth, 10), damage));
            var state = engine.Create("config", HmacDeckShuffler.FromSeed(new byte[32]), players, enemies);
            return Do(engine, state, CardDuelAction.Start).State;
        }
        private static CardDuelTransition Do(CardDuelEngine engine, CardDuelState state, CardDuelAction action, int count = 0, ParticipantId? actor = null,
            IEnumerable<ParticipantId> targets = null)
        {
            var id = actor ?? (action == CardDuelAction.EnemyAction ? state.Duel.NextEnemy : P);
            var result = engine.Execute(state, state.Revision, id, action, count == 0 ? null : state.Zone(CardZoneKinds.Hand, id).Cards.Take(count), targets);
            Assert.IsTrue(result.Accepted, action + ": " + result.Error); result.State.Table.Validate(); return result;
        }
        [Test] public void DefaultsRemainCompatibleAndLimitsClampToHandSize()
        {
            var rules = new BattlePlayerRules();
            Assert.AreEqual(8, rules.HandSize); Assert.AreEqual(3, rules.Discards); Assert.IsFalse(rules.UnlimitedDiscards);
            Assert.AreEqual(5, rules.MaxPlayCards); Assert.AreEqual(5, rules.MaxDiscardCards);
            Assert.AreEqual(PlayerBlockExpiry.Retain, Engine().Duel.Definition.PlayerBlockExpiry);
            var small = new BattlePlayerRules(handSize: 2, maxDiscardCards: 52);
            Assert.AreEqual(2, small.MaxPlayCards); Assert.AreEqual(2, small.MaxDiscardCards);
            var engine = Engine(); var state = Start(engine);
            state = Do(engine, state, CardDuelAction.Play, 1).State;
            Assert.AreEqual(7, state.Zone(CardZoneKinds.Hand).Cards.Count);
            state = Do(engine, state, CardDuelAction.CollectPlayed).State;
            var discarded = Do(engine, state, CardDuelAction.Discard, 1);
            Assert.AreEqual(8, discarded.State.Zone(CardZoneKinds.Hand).Cards.Count);
            Assert.AreEqual(2, discarded.State.DiscardsRemaining); Assert.IsEmpty(discarded.Events);
        }
        [TestCase(0, 5)] [TestCase(6, 5)] [TestCase(5, 0)] [TestCase(5, 53)]
        public void InvalidLimitsAreRejected(int play, int discard) =>
            Assert.Throws<ArgumentException>(() => new BattlePlayerRules(maxPlayCards: play, maxDiscardCards: discard));
        [Test] public void InvalidRewardAndExpiryAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PerCardBlockRewardRule(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Engine(expiry: (PlayerBlockExpiry)99));
            Assert.Throws<ArgumentNullException>(() => new PerCardBlockRewardRule(8).Resolve(null));
        }
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(8)]
        public void PerCardBlockUsesActualDiscardCountAndPreviewMatchesCommit(int count)
        {
            var engine = Engine();
            var state = Start(engine, new BattlePlayerRules(maxDiscardCards: 8, discardReward: new PerCardBlockRewardRule(8)), smallDeck: true);
            var cards = state.Zone(CardZoneKinds.Hand).Cards.Take(count).ToArray(); var random = state.Random.StateBytes.ToArray();
            var preview = engine.PreviewDiscard(state, state.Revision, P, cards);
            var result = Do(engine, state, CardDuelAction.Discard, count);
            Assert.IsTrue(preview.Accepted); Assert.AreEqual(count * 8, result.State.Duel.Combat.Get(P).Block);
            Assert.AreEqual(2, result.State.Duel.Player(P).Energy.Energy); Assert.AreEqual(2, result.State.DiscardsRemaining);
            Assert.AreEqual(8, result.State.Zone(CardZoneKinds.Hand).Cards.Count); Assert.IsNull(result.Score);
            Assert.AreEqual(preview.State.Duel.Combat.Get(P).Block, result.State.Duel.Combat.Get(P).Block);
            Assert.AreEqual(preview.DiscardReward.Plan.Explanation, result.DiscardReward.Plan.Explanation);
            CollectionAssert.AreEqual(preview.State.Random.StateBytes, result.State.Random.StateBytes);
            foreach (var zone in result.State.Table.Zones) CollectionAssert.AreEqual(preview.State.Table.GetZone(zone.Id).Cards, zone.Cards);
            Assert.AreEqual(0, state.Duel.Combat.Get(P).Block); Assert.AreEqual(3, state.DiscardsRemaining);
            CollectionAssert.AreEqual(random, state.Random.StateBytes);
        }
        [Test] public void RewardCanSupportSelectedAllyWithoutChangingTargetPolicy()
        {
            var engine = Engine(); var state = Start(engine, new BattlePlayerRules(discardReward: new PerCardBlockRewardRule(8)), ally: true);
            var result = Do(engine, state, CardDuelAction.Discard, 3, targets: new[] { A });
            Assert.AreEqual(24, result.State.Duel.Combat.Get(A).Block); Assert.AreEqual(0, result.State.Duel.Combat.Get(P).Block);
            Assert.AreEqual(3, result.State.Duel.Player(A).Energy.Energy);
        }
        [Test] public void ZeroBlockDoesNotCreateFakeCombatEvents()
        {
            var engine = Engine(); var state = Start(engine, new BattlePlayerRules(discardReward: new PerCardBlockRewardRule(0)));
            var result = Do(engine, state, CardDuelAction.Discard, 5);
            Assert.IsEmpty(result.Events); Assert.IsEmpty(result.DiscardReward.Plan.Effects); Assert.AreEqual(2, result.State.DiscardsRemaining);
        }
        [TestCase(0, 2)] [TestCase(int.MaxValue, 1)]
        public void BlockOverflowRejectsAllCostsCardsAndRandomChanges(int initialBlock, int count)
        {
            var engine = Engine(); var state = Start(engine, new BattlePlayerRules(discardReward: new PerCardBlockRewardRule(int.MaxValue)), block: initialBlock);
            var result = engine.PreviewDiscard(state, state.Revision, P, state.Zone(CardZoneKinds.Hand).Cards.Take(count));
            Assert.AreEqual(CardDuelError.Overflow, result.Error); Assert.AreSame(state, result.State);
            Assert.IsEmpty(result.Events); Assert.AreEqual(3, state.DiscardsRemaining); Assert.AreEqual(3, state.Duel.Player(P).Energy.Energy);
        }
        [Test] public void PlayAndDiscardLimitsAreIndependentlyEnforcedInPreviewAndExecution()
        {
            var engine = Engine(); var state = Start(engine, new BattlePlayerRules(maxPlayCards: 2, maxDiscardCards: 8, discardReward: new PerCardBlockRewardRule(8)));
            var cards = state.Zone(CardZoneKinds.Hand).Cards.Take(3).ToArray();
            foreach (var result in new[] { engine.PreviewHand(state, P, cards), engine.PreviewPlay(state, state.Revision, P, cards),
                engine.Execute(state, state.Revision, P, CardDuelAction.Play, cards) })
            { Assert.AreEqual(CardDuelError.InvalidSelection, result.Error); Assert.AreSame(state, result.State); }
            Do(engine, state, CardDuelAction.Play, 2); Do(engine, state, CardDuelAction.Discard, 8);
            state = Start(engine, new BattlePlayerRules(maxPlayCards: 5, maxDiscardCards: 2, discardReward: new PerCardBlockRewardRule(8)));
            cards = state.Zone(CardZoneKinds.Hand).Cards.Take(3).ToArray();
            foreach (var result in new[] { engine.PreviewDiscard(state, state.Revision, P, cards), engine.Execute(state, state.Revision, P, CardDuelAction.Discard, cards) })
            { Assert.AreEqual(CardDuelError.InvalidSelection, result.Error); Assert.AreSame(state, result.State); }
            Do(engine, state, CardDuelAction.Play, 5);
        }
        [Test] public void UnlimitedDiscardsIgnoreZeroFiniteBudgetButStillSpendEnergyAcrossRounds()
        {
            var engine = Engine(); var state = Start(engine, new BattlePlayerRules(discards: 0, unlimitedDiscards: true));
            for (int round = 0; round < 3; round++)
            {
                for (int action = 0; action < 3; action++) state = Do(engine, state, CardDuelAction.Discard, 5).State;
                Assert.AreEqual(0, state.Duel.Player(P).Energy.Energy); Assert.AreEqual(0, state.DiscardsFor(P));
                Assert.IsNull(state.RemainingDiscardBudgetFor(P)); Assert.IsTrue(state.HasDiscardBudgetFor(P));
                var rejected = engine.Execute(state, state.Revision, P, CardDuelAction.Discard, state.Zone(CardZoneKinds.Hand).Cards.Take(1));
                Assert.IsFalse(rejected.Accepted); Assert.AreSame(state, rejected.State);
                state = Do(engine, state, CardDuelAction.EnemyAction).State;
            }
            Assert.AreEqual(3, state.Duel.CompletedRounds);
        }
        [Test] public void UnlimitedModeDoesNotBypassEnergyCostOrOtherPlayersBudgets()
        {
            var engine = Engine(); var state = Start(engine, new BattlePlayerRules(initialEnergy: 1, discardEnergyCost: 2, discards: 0, unlimitedDiscards: true), ally: true);
            var result = engine.PreviewDiscard(state, state.Revision, P, state.Zone(CardZoneKinds.Hand).Cards.Take(1));
            Assert.AreEqual(CardDuelError.InsufficientEnergy, result.Error); Assert.AreSame(state, result.State);
            state = Do(engine, state, CardDuelAction.Discard, 1, A).State;
            Assert.AreEqual(2, state.RemainingDiscardBudgetFor(A)); Assert.IsNull(state.RemainingDiscardBudgetFor(P));
        }
        [Test] public void FiniteZeroAndExhaustedBudgetRemainUnavailableAfterNextRound()
        {
            var engine = Engine(); var state = Start(engine, new BattlePlayerRules(discards: 0));
            Assert.IsFalse(state.HasDiscardBudgetFor(P)); Assert.AreEqual(0, state.RemainingDiscardBudgetFor(P));
            var rejected = engine.PreviewDiscard(state, state.Revision, P, state.Zone(CardZoneKinds.Hand).Cards.Take(1));
            Assert.AreEqual(CardDuelError.NoDiscards, rejected.Error); Assert.AreSame(state, rejected.State);
            state = Start(engine, new BattlePlayerRules(discards: 1));
            state = Do(engine, state, CardDuelAction.Discard, 1).State;
            state = Do(engine, state, CardDuelAction.EndTurn).State; state = Do(engine, state, CardDuelAction.EnemyAction).State;
            Assert.IsFalse(state.HasDiscardBudgetFor(P)); Assert.AreEqual(0, state.DiscardsFor(P));
        }
        [TestCase(PlayerBlockExpiry.Retain, 26)] [TestCase(PlayerBlockExpiry.AfterEnemyPhase, 0)]
        public void BlockLastsThroughAllEnemiesThenExpiresForAllLivingPlayers(PlayerBlockExpiry expiry, int remaining)
        {
            var engine = Engine(expiry: expiry); var state = Start(engine, block: 40, twoEnemies: true, ally: true);
            state = Do(engine, state, CardDuelAction.EndTurn).State;
            state = Do(engine, state, CardDuelAction.EndTurn, actor: A).State;
            var first = Do(engine, state, CardDuelAction.EnemyAction);
            Assert.AreEqual(33, first.State.Duel.Combat.Get(P).Block); Assert.AreEqual(40, first.State.Duel.Combat.Get(A).Block);
            Assert.AreEqual(1, first.Events.Count); Assert.AreEqual(0, first.State.Duel.CompletedRounds);
            var last = Do(engine, first.State, CardDuelAction.EnemyAction);
            Assert.AreEqual(remaining, last.State.Duel.Combat.Get(P).Block); Assert.AreEqual(800, last.State.Duel.Combat.Get(P).Health);
            Assert.AreEqual(expiry == PlayerBlockExpiry.Retain ? 40 : 0, last.State.Duel.Combat.Get(A).Block);
            Assert.AreEqual(10, last.State.Duel.Combat.Get(E).Block); Assert.AreEqual(10, last.State.Duel.Combat.Get(E2).Block);
            Assert.AreEqual(1, last.State.Duel.CompletedRounds); Assert.AreEqual(3, last.State.Duel.Player(P).Energy.Energy);
            Assert.AreEqual(expiry == PlayerBlockExpiry.Retain ? 1 : 3, last.Events.Count);
            Assert.AreEqual(CombatEffectKind.Damage, last.Events[0].Effect.Kind);
            foreach (var effect in last.Events.Skip(1)) Assert.AreEqual(CombatEffectKind.ClearBlock, effect.Effect.Kind);
        }
        [Test] public void ExpiryDoesNotRescueLethalDamageOrCreateZeroBlockEvents()
        {
            var engine = Engine(expiry: PlayerBlockExpiry.AfterEnemyPhase);
            var state = Start(engine, block: 3, health: 4, damage: 7);
            state = Do(engine, state, CardDuelAction.EndTurn).State;
            var result = Do(engine, state, CardDuelAction.EnemyAction);
            Assert.AreEqual(ScoredDuelPhase.Lost, result.State.Duel.Phase); Assert.AreEqual(1, result.Events.Count);
            state = Start(engine, block: 7); state = Do(engine, state, CardDuelAction.EndTurn).State;
            result = Do(engine, state, CardDuelAction.EnemyAction);
            Assert.AreEqual(1, result.Events.Count); Assert.AreEqual(0, result.State.Duel.Combat.Get(P).Block);
        }
        [Test] public void FinalRoundExpiresSurvivingBlockWithoutStartingAnotherTurn()
        {
            var engine = Engine(expiry: PlayerBlockExpiry.AfterEnemyPhase, rounds: 1); var state = Start(engine, block: 40);
            state = Do(engine, state, CardDuelAction.EndTurn).State;
            var result = Do(engine, state, CardDuelAction.EnemyAction);
            Assert.AreEqual(ScoredDuelPhase.Lost, result.State.Duel.Phase); Assert.AreEqual(0, result.State.Duel.Combat.Get(P).Block);
            Assert.AreEqual(0, result.State.Duel.Player(P).Energy.Energy); Assert.AreEqual(2, result.Events.Count);
        }
        [Test] public void LethalPlayCancelsEnemyPhaseWithoutExpiringBlockEarly()
        {
            var engine = Engine(expiry: PlayerBlockExpiry.AfterEnemyPhase); var state = Start(engine, block: 40, enemyHealth: 1);
            var result = Do(engine, state, CardDuelAction.Play, 5);
            Assert.AreEqual(ScoredDuelPhase.Won, result.State.Duel.Phase); Assert.AreEqual(40, result.State.Duel.Combat.Get(P).Block);
            Assert.IsFalse(result.Events.Any(x => x.Effect.Kind == CombatEffectKind.ClearBlock));
            var rejected = engine.Execute(result.State, result.State.Revision, E, CardDuelAction.EnemyAction);
            Assert.IsFalse(rejected.Accepted); Assert.AreSame(result.State, rejected.State);
        }
        [Test] public void ProposedBaseRulesWorkTogetherWithoutChangingExistingAssets()
        {
            var engine = Engine(PlayerBlockExpiry.AfterEnemyPhase);
            var rules = new BattlePlayerRules(discards: 0, unlimitedDiscards: true, discardReward: new PerCardBlockRewardRule(8),
                dealPolicy: new CardDuelDealPolicy(CardDuelRefillTiming.AfterAction, CardDuelRefillTiming.AfterAction));
            var state = Start(engine, rules, damage: 240, enemyHealth: 1200);
            state = Do(engine, state, CardDuelAction.Discard, 5).State;
            state = Do(engine, state, CardDuelAction.Discard, 5).State;
            state = Do(engine, state, CardDuelAction.Play, 1).State;
            Assert.AreEqual(8, state.Zone(CardZoneKinds.Hand).Cards.Count); Assert.AreEqual(80, state.Duel.Combat.Get(P).Block);
            var result = Do(engine, state, CardDuelAction.EnemyAction);
            Assert.AreEqual(80, result.Events[0].BlockAbsorbed); Assert.AreEqual(160, result.Events[0].HealthLost);
            Assert.AreEqual(640, result.State.Duel.Combat.Get(P).Health); Assert.AreEqual(0, result.State.Duel.Combat.Get(P).Block);
            Assert.AreEqual(3, result.State.Duel.Player(P).Energy.Energy);
        }
    }
}
