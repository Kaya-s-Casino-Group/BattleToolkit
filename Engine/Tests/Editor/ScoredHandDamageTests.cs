using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    public sealed class ScoredHandDamageTests
    {
        private static readonly ParticipantId Player = new ParticipantId("p"), Enemy = new ParticipantId("e"), Other = new ParticipantId("e2");
        private sealed class AllTargets : IBattleTargetPolicy
        { public IReadOnlyList<ParticipantId> SelectTargets(BattleTargetContext context) => context.Opponents; }
        private sealed class PerTargetRule : IScoredHandDamageRule
        {
            public string Version => "test.per-target.1";
            public DamagePlan Resolve(ScoredHandDamageContext c) => new DamagePlan(Version, "按目标状态生成伤害",
                c.Targets.Select(id => new DamageRequest(c.Source, id, c.Combat.Get(id).Block + c.Score.Total)));
        }
        private sealed class BadRule : IScoredHandDamageRule
        {
            private readonly int kind;
            public BadRule(int kind) { this.kind = kind; }
            public string Version => "bad.1";
            public DamagePlan Resolve(ScoredHandDamageContext c)
            {
                if (kind == 0) return null;
                if (kind == 4) throw new ArgumentException("invalid rule output");
                return new DamagePlan(kind == 3 ? "wrong-version" : Version, "bad", new[] {
                    new DamageRequest(kind == 1 ? c.Targets[0] : c.Source, kind == 2 ? c.Source : c.Targets[0], 5) });
            }
        }
        private static CardDuelEngine Engine() =>
            new CardDuelEngine(new BattleRules("damage.test.1", 10));
        private static CardDuelState Create(CardDuelEngine engine, bool smallDeck = false, IScoredHandDamageRule damage = null, BattleActorDamageRules modifiers = null, IBattleTargetPolicy targets = null)
        {
            var rules = new BattlePlayerRules(dealPolicy: new CardDuelDealPolicy(CardDuelRefillTiming.AfterAction, CardDuelRefillTiming.NextPlayerTurn), damagePolicy: damage);
            var deck = smallDeck ? StandardDeck.Cards().Take(8).Select((card, i) => new DeckEntry("card" + i, card.Id)).ToArray() : null;
            var state = engine.Create("battle", HmacDeckShuffler.FromSeed(new byte[32]), new[] {
                new BattlePlayerSetup(Player, new CombatantDefinition("player", 40), deck, rules: rules, damageRules: modifiers, targetRules: new BattleActorTargetRules(targets))
            }, new[] {
                new BattleEnemySetup(Enemy, new CombatantDefinition("enemy", 10, 2), 7, modifiers),
                new BattleEnemySetup(Other, new CombatantDefinition("other", 100, 9), 8, modifiers)
            });
            return engine.Execute(state, state.Revision, Player, CardDuelAction.Start).State;
        }
        [Test] public void RuleGeneratesRequestsAndOnlyCombatAppliesBlockOverkillAndDeath()
        {
            var engine = Engine(); var state = Create(engine);
            var selected = state.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            var result = engine.PreviewPlay(state, state.Revision, Player, selected);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(result.Score.Total, result.Damage.Requests.Single().Amount);
            var hit = result.Events.Single(); Assert.AreEqual(2, hit.BlockAbsorbed);
            Assert.AreEqual(Math.Min(10, result.Score.Total - 2), hit.HealthLost);
            Assert.AreEqual(Math.Max(0, result.Score.Total - 12), hit.Overkill);
            Assert.AreEqual(hit.After.Health == 0, hit.Died);
            Assert.AreEqual(10, state.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(2, state.Duel.Combat.Get(Enemy).Block);
        }
        [Test] public void AlternateRuleUsesEachTargetContextAndPreservesOneActionCost()
        {
            var engine = Engine(); var state = Create(engine, damage: new PerTargetRule(), targets: new AllTargets());
            var result = engine.Execute(state, state.Revision, Player, CardDuelAction.Play, state.Zone(CardZoneKinds.Hand, Player).Cards.Take(1));
            Assert.IsTrue(result.Accepted); CollectionAssert.AreEqual(new[] { Enemy, Other }, result.Damage.Requests.Select(x => x.Target));
            Assert.AreEqual(result.Score.Total + 2, result.Events[0].Effect.Amount);
            Assert.AreEqual(result.Score.Total + 9, result.Events[1].Effect.Amount);
            Assert.AreEqual(2, result.State.Duel.Player(Player).Energy.Energy); Assert.AreEqual(1, result.State.Duel.Scores.Count);
        }
        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void InvalidDamageOutputRejectsWholeCommandWithoutSpendingCardsEnergyOrScore(int kind)
        {
            var engine = Engine(); var state = Create(engine, damage: new BadRule(kind));
            var result = engine.Execute(state, state.Revision, Player, CardDuelAction.Play, state.Zone(CardZoneKinds.Hand, Player).Cards.Take(1));
            Assert.AreEqual(CardDuelError.InvalidDamagePlan, result.Error); Assert.AreSame(state, result.State);
            Assert.IsEmpty(result.Events); Assert.IsNull(result.Score); Assert.IsNull(result.Damage);
            Assert.AreEqual(3, state.Duel.Player(Player).Energy.Energy); Assert.IsEmpty(state.Duel.Scores);
        }
        [Test] public void RepeatedPreviewMatchesCommitIncludingRecycleRandomnessWithoutMutatingInput()
        {
            var engine = Engine(); var state = Create(engine, true, new ScoreDamagePolicy(100));
            state = engine.Execute(state, state.Revision, Player, CardDuelAction.Discard, state.Zone(CardZoneKinds.Hand, Player).Cards.Take(2)).State;
            var cards = state.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            var hand = state.Zone(CardZoneKinds.Hand, Player).Cards.ToArray(); var random = state.Random.StateBytes.ToArray(); long revision = state.Revision;
            var first = engine.PreviewPlay(state, revision, Player, cards); var second = engine.PreviewPlay(state, revision, Player, cards);
            var actual = engine.Execute(state, revision, Player, CardDuelAction.Play, cards);
            foreach (var result in new[] { first, second })
            {
                Assert.IsTrue(result.Accepted); Assert.AreEqual(actual.Score.Total, result.Score.Total);
                Assert.AreEqual(actual.Damage.Explanation, result.Damage.Explanation);
                Assert.AreEqual(actual.Events.Single().After.Health, result.Events.Single().After.Health);
                CollectionAssert.AreEqual(actual.State.Random.StateBytes, result.State.Random.StateBytes);
                foreach (var zone in actual.State.Table.Zones) CollectionAssert.AreEqual(zone.Cards, result.State.Table.GetZone(zone.Id).Cards);
            }
            Assert.AreEqual(revision, state.Revision); Assert.AreEqual(2, state.Duel.Player(Player).Energy.Energy); Assert.IsEmpty(state.Duel.Scores);
            CollectionAssert.AreEqual(hand, state.Zone(CardZoneKinds.Hand, Player).Cards); CollectionAssert.AreEqual(random, state.Random.StateBytes);
            Assert.IsFalse(random.SequenceEqual(actual.State.Random.StateBytes));
        }
        [Test] public void PreviewUsesActualLegalityChecksAndCannotReturnDamageForRejectedCards()
        {
            var engine = Engine(); var state = Create(engine, damage: new ScoreDamagePolicy(5));
            var result = engine.PreviewPlay(state, state.Revision, Player, state.Zone(CardZoneKinds.Deck, Player).Cards.Take(1));
            Assert.AreEqual(CardDuelError.InvalidSelection, result.Error); Assert.AreSame(state, result.State); Assert.IsNull(result.Damage);
            Assert.AreEqual(CardDuelError.StaleRevision, engine.PreviewPlay(state, state.Revision - 1, Player, new CardInstanceId[0]).Error);
        }
        private sealed class DoubleAttack : IAttackDamageModifier
        {
            public DamageModification Modify(DamageModifierContext c) =>
                new DamageModification("double.1", "攻击翻倍", c.Amount, checked(c.Amount * 2));
        }
        private sealed class TargetAdjustment : IIncomingDamageModifier
        {
            public DamageModification Modify(DamageModifierContext c) =>
                new DamageModification("target.1", "各目标分别修正", c.Amount, c.Target == Enemy ? c.Amount / 2 : checked(c.Amount + 3));
        }
        private sealed class BadModifier : IAttackDamageModifier, IIncomingDamageModifier
        {
            private readonly int kind;
            public BadModifier(int kind) { this.kind = kind; }
            public DamageModification Modify(DamageModifierContext c)
            {
                if (kind == 0) return null;
                if (kind == 1) return new DamageModification("bad", "错误输入", c.Amount + 1, 0);
                if (kind == 2) return new DamageModification("bad", "负数", c.Amount, -1);
                throw new OverflowException();
            }
        }
        [Test] public void PreviewAndCommitShareScoreAttackIncomingAndBlockStagesPerTarget()
        {
            var engine = new CardDuelEngine(new BattleRules("stages", 10));
            var state = Create(engine, modifiers: new BattleActorDamageRules(new DoubleAttack(), new TargetAdjustment()), targets: new AllTargets()); var cards = state.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            var preview = engine.PreviewPlay(state, state.Revision, Player, cards);
            var actual = engine.Execute(state, state.Revision, Player, CardDuelAction.Play, cards);
            Assert.IsTrue(preview.Accepted); Assert.IsTrue(actual.Accepted);
            foreach (var result in new[] { preview, actual })
            {
                long score = result.Score.Total;
                Assert.IsTrue(result.Damage.Requests.All(x => x.Amount == score));
                Assert.AreEqual(2, result.Events.Count);
                for (int i = 0; i < result.Events.Count; i++)
                {
                    var hit = result.Events[i]; long incoming = i == 0 ? score : score * 2 + 3;
                    Assert.AreEqual(score, hit.Effect.AttackModification.InputAmount);
                    Assert.AreEqual(score * 2, hit.Effect.Amount);
                    Assert.AreEqual(score * 2, hit.IncomingModification.InputAmount);
                    Assert.AreEqual(incoming, hit.SettledDamage);
                    Assert.AreEqual(Math.Min(incoming, hit.Before.Block), hit.BlockAbsorbed);
                    Assert.AreEqual(Math.Min(incoming - hit.BlockAbsorbed, hit.Before.Health), hit.HealthLost);
                    Assert.AreEqual(actual.Events[i].After.Health, hit.After.Health);
                }
                Assert.AreEqual(2, result.State.Duel.Player(Player).Energy.Energy);
                Assert.AreEqual(score, result.State.Duel.Scores.Single().Total);
            }
            Assert.AreEqual(10, state.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(3, state.Duel.Player(Player).Energy.Energy); Assert.IsEmpty(state.Duel.Scores);
        }
        [TestCase(false, 0)] [TestCase(false, 1)] [TestCase(false, 2)] [TestCase(false, 3)]
        [TestCase(true, 0)] [TestCase(true, 1)] [TestCase(true, 2)] [TestCase(true, 3)]
        public void InvalidOrOverflowingModifierRejectsEntirePlay(bool incoming, int kind)
        {
            var modifier = new BadModifier(kind);
            var engine = new CardDuelEngine(new BattleRules("bad-modifier", 10));
            var state = Create(engine, modifiers: new BattleActorDamageRules(incoming ? null : modifier, incoming ? modifier : null)); var cards = state.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            foreach (var result in new[] { engine.PreviewPlay(state, state.Revision, Player, cards),
                engine.Execute(state, state.Revision, Player, CardDuelAction.Play, cards) })
            {
                Assert.AreEqual(kind == 3 ? CardDuelError.Overflow : CardDuelError.InvalidDamageModifier, result.Error);
                Assert.AreSame(state, result.State); Assert.IsEmpty(result.Events); Assert.IsNull(result.Score);
            }
            Assert.AreEqual(3, state.Duel.Player(Player).Energy.Energy); Assert.IsEmpty(state.Duel.Scores);
        }
        [Test] public void EnemyBypassesScoringButUsesBothDamageModifiers()
        {
            var engine = new CardDuelEngine(new BattleRules("enemy-modifiers", 10));
            var state = Create(engine, damage: new BadRule(4), modifiers: new BattleActorDamageRules(new DoubleAttack(), new TargetAdjustment()));
            state = engine.Execute(state, state.Revision, Player, CardDuelAction.EndTurn).State;
            var result = engine.Execute(state, state.Revision, Enemy, CardDuelAction.EnemyAction);
            Assert.IsTrue(result.Accepted); Assert.IsNull(result.Score); Assert.IsNull(result.Damage);
            var hit = result.Events.Single(); Assert.AreEqual(7, hit.Effect.AttackModification.InputAmount);
            Assert.AreEqual(14, hit.Effect.Amount); Assert.AreEqual(17, hit.SettledDamage); Assert.AreEqual(17, hit.HealthLost);
        }
        [Test] public void EqualScorePreservesLargeValuesUntilTargetHealthClamping()
        {
            var engine = new TeamBattleEngine(new BattleRules("large", 10));
            var state = engine.Create("large", new[] { new BattlePlayerSetup(Player, new CombatantDefinition("p", 40)) },
                new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("e", 1000000, 100), 7) });
            var score = ScoreEngine.Fixed(engine.NextScoreId(state), 5000000);
            var result = engine.Execute(state, state.Revision, Player, ScoredDuelActionKind.ScoredHand, score);
            Assert.IsTrue(result.Accepted); var hit = result.Events.Single();
            Assert.AreEqual(5000000, hit.Effect.Amount); Assert.AreEqual(5000000, hit.SettledDamage);
            Assert.AreEqual(100, hit.BlockAbsorbed); Assert.AreEqual(1000000, hit.HealthLost);
            Assert.AreEqual(3999900, hit.Overkill); Assert.IsTrue(hit.Died);
        }
        [Test] public void EnemyAttackDoesNotInvokeScoredHandRule()
        {
            var engine = Engine(); var state = Create(engine, damage: new BadRule(4));
            state = engine.Execute(state, state.Revision, Player, CardDuelAction.EndTurn).State;
            var result = engine.Execute(state, state.Revision, Enemy, CardDuelAction.EnemyAction);
            Assert.IsTrue(result.Accepted); Assert.IsNull(result.Score); Assert.IsNull(result.Damage); Assert.AreEqual(7, result.Events.Single().HealthLost);
        }
    }
}
