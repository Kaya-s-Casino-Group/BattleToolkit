using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    public sealed class DiscardRewardTests
    {
        private static readonly ParticipantId Player = new ParticipantId("p"), Ally = new ParticipantId("a"), Enemy = new ParticipantId("e");
        private sealed class NoAttacks : IScoredHandDamageRule, IBattleTargetPolicy
        {
            public string Version => "never";
            public DamagePlan Resolve(ScoredHandDamageContext c) => throw new InvalidOperationException("Discard must not build attacks.");
            public IReadOnlyList<ParticipantId> SelectTargets(BattleTargetContext c) => throw new InvalidOperationException("Discard must not use hostile targeting.");
        }
        // Contract probe only, not a production benefit formula.
        private sealed class Reward : IDiscardRewardRule
        {
            private readonly long amount;
            public string Version => "test.reward.1";
            public Reward(long amount = 7) { this.amount = amount; }
            public DiscardRewardPlan Resolve(DiscardRewardContext c) => new DiscardRewardPlan(Version, "测试友方收益通路",
                c.Targets.Select(target => new CombatEffect(CombatEffectKind.GainBlock, c.Source, target, amount)));
        }
        private sealed class BadReward : IDiscardRewardRule
        {
            private readonly int kind;
            public BadReward(int kind) { this.kind = kind; }
            public string Version => "bad";
            public DiscardRewardPlan Resolve(DiscardRewardContext c)
            {
                if (kind == 0) return null;
                if (kind == 7 || kind == 8)
                {
                    var effect = kind == 7 ? CombatEffect.Eliminate(c.Source, c.Targets[0]) : CombatEffect.ApplyStatus(
                        new StatusInstance(new StatusInstanceId("discard/status"), new StatusDefinitionId("mark"),
                            EntityRef.Participant(c.Source), EntityRef.Participant(c.Targets[0]), 1, StatusDuration.UntilRemoved()));
                    return new DiscardRewardPlan(Version, "new primitives are not discard rewards", new[] { effect });
                }
                return new DiscardRewardPlan(kind == 1 ? "wrong" : Version, "invalid", new[] {
                    new CombatEffect(kind == 4 ? CombatEffectKind.Damage : kind == 5 ? CombatEffectKind.ClearBlock : CombatEffectKind.GainBlock,
                        kind == 3 ? Ally : c.Source, kind == 2 ? Enemy : kind == 6 ? Ally : c.Targets[0], kind == 5 ? 0 : 7) });
            }
        }
        private static CardDuelEngine Engine() => new CardDuelEngine(
            new BattleRules("discard.test", 10));
        private static CardDuelState Create(CardDuelEngine engine, bool smallDeck = false, IDiscardRewardRule reward = null)
        {
            var deck = smallDeck ? StandardDeck.Cards().Take(8).Select((card, i) => new DeckEntry("c" + i, card.Id)).ToArray() : null;
            var state = engine.Create("discard", HmacDeckShuffler.FromSeed(new byte[32]), new[] {
                new BattlePlayerSetup(Player, new CombatantDefinition("p", 40), deck, rules: new BattlePlayerRules(discardReward: reward, damagePolicy: new NoAttacks()), targetRules: new BattleActorTargetRules(new NoAttacks())),
                new BattlePlayerSetup(Ally, new CombatantDefinition("a", 40))
            }, new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("e", 10000), 7) });
            return engine.Execute(state, state.Revision, Player, CardDuelAction.Start).State;
        }
        [Test] public void DefaultDiscardOnlySpendsBudgetsAndMovesCardsWithoutScoringOrCombatEvents()
        {
            var engine = Engine(); var before = Create(engine); var cards = before.Zone(CardZoneKinds.Hand, Player).Cards.Take(2).ToArray();
            var result = engine.Execute(before, before.Revision, Player, CardDuelAction.Discard, cards);
            Assert.IsTrue(result.Accepted); Assert.IsNotNull(result.DiscardReward);
            Assert.IsEmpty(result.DiscardReward.Plan.Effects); Assert.IsEmpty(result.Events);
            Assert.IsNull(result.Score); Assert.IsNull(result.Pattern); Assert.IsNull(result.Damage); Assert.IsEmpty(result.State.Duel.Scores);
            Assert.AreSame(before.Duel.Combat, result.State.Duel.Combat);
            Assert.AreEqual(2, result.State.Duel.Player(Player).Energy.Energy); Assert.AreEqual(2, result.State.DiscardsFor(Player));
            Assert.AreEqual(8, result.State.Zone(CardZoneKinds.Hand, Player).Cards.Count);
            CollectionAssert.AreEqual(cards, result.State.Zone(CardZoneKinds.Discard, Player).Cards);
            CollectionAssert.AreEqual(cards, result.DiscardReward.Context.Cards.Select(card => card.Id));
            CollectionAssert.AreEqual(new[] { Player }, result.DiscardReward.Context.Targets);
        }
        [Test] public void FutureRewardUsesFriendlyTargetsAndCommonCombatSettlement()
        {
            var engine = Engine(); var state = Create(engine, reward: new Reward());
            var result = engine.Execute(state, state.Revision, Player, CardDuelAction.Discard,
                state.Zone(CardZoneKinds.Hand, Player).Cards.Take(1), new[] { Ally });
            Assert.IsTrue(result.Accepted); Assert.AreEqual(7, result.State.Duel.Combat.Get(Ally).Block);
            Assert.AreEqual(0, result.State.Duel.Combat.Get(Player).Block); Assert.AreEqual(0, result.State.Duel.Combat.Get(Enemy).Block);
            Assert.AreEqual(CombatEffectKind.GainBlock, result.Events.Single().Effect.Kind);
            Assert.AreEqual(Player, result.Events.Single().Effect.Source); Assert.IsNull(result.Events.Single().Effect.Attack);
            Assert.IsNull(result.Score); Assert.AreEqual(3, result.State.Duel.Player(Ally).Energy.Energy);
        }
        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)] [TestCase(8)]
        public void InvalidRewardRejectsWholeDiscard(int kind)
        {
            var engine = Engine(); var state = Create(engine, reward: new BadReward(kind));
            var result = engine.Execute(state, state.Revision, Player, CardDuelAction.Discard, state.Zone(CardZoneKinds.Hand, Player).Cards.Take(1));
            Assert.AreEqual(CardDuelError.InvalidDiscardReward, result.Error); Assert.AreSame(state, result.State);
            Assert.IsEmpty(result.Events); Assert.IsNull(result.DiscardReward); Assert.AreEqual(3, state.DiscardsFor(Player));
            Assert.IsEmpty(result.RuleEvents); Assert.IsNull(result.EffectPlan);
        }
        [Test] public void EnemyTargetIsRejectedEvenWhenRewardIsEmpty()
        {
            var engine = Engine(); var state = Create(engine);
            var result = engine.PreviewDiscard(state, state.Revision, Player, state.Zone(CardZoneKinds.Hand, Player).Cards.Take(1), new[] { Enemy });
            Assert.AreEqual(CardDuelError.InvalidTarget, result.Error); Assert.AreSame(state, result.State); Assert.IsNull(result.DiscardReward);
        }
        [Test] public void RewardOverflowRollsBackCostsCardsAndRandomState()
        {
            var engine = Engine(); var state = Create(engine, true, new Reward(long.MaxValue));
            var result = engine.PreviewDiscard(state, state.Revision, Player, state.Zone(CardZoneKinds.Hand, Player).Cards.Take(2));
            Assert.AreEqual(CardDuelError.Overflow, result.Error); Assert.AreSame(state, result.State); Assert.IsEmpty(result.Events);
        }
        [Test] public void RepeatedPreviewAndCommitShareRewardAndRecycleResultsWithoutMutatingInput()
        {
            var engine = Engine(); var state = Create(engine, true, new Reward());
            var selected = state.Zone(CardZoneKinds.Hand, Player).Cards.Take(2).ToArray(); var random = state.Random.StateBytes.ToArray();
            var one = engine.PreviewDiscard(state, state.Revision, Player, selected);
            var two = engine.PreviewDiscard(state, state.Revision, Player, selected);
            var actual = engine.Execute(state, state.Revision, Player, CardDuelAction.Discard, selected);
            foreach (var result in new[] { one, two, actual })
            {
                Assert.IsTrue(result.Accepted); Assert.AreEqual(7, result.Events.Single().After.Block);
                Assert.AreEqual(actual.DiscardReward.Plan.Explanation, result.DiscardReward.Plan.Explanation);
                CollectionAssert.AreEqual(actual.State.Random.StateBytes, result.State.Random.StateBytes);
                foreach (var zone in actual.State.Table.Zones) CollectionAssert.AreEqual(zone.Cards, result.State.Table.GetZone(zone.Id).Cards);
                Assert.Throws<NotSupportedException>(() => ((IList<DiscardCardSnapshot>)result.DiscardReward.Context.Cards)[0] = null);
                Assert.Throws<NotSupportedException>(() => ((IList<CombatEffect>)result.DiscardReward.Plan.Effects)[0] = null);
            }
            CollectionAssert.AreEqual(random, state.Random.StateBytes); Assert.AreEqual(3, state.DiscardsFor(Player));
            Assert.AreEqual(0, state.Duel.Combat.Get(Player).Block); Assert.AreEqual(8, state.Zone(CardZoneKinds.Hand, Player).Cards.Count);
            Assert.IsFalse(random.SequenceEqual(actual.State.Random.StateBytes));
        }
    }
}
