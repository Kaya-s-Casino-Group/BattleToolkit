using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    public sealed class AttackContextTests
    {
        private static readonly ParticipantId Player = new ParticipantId("p"), Enemy = new ParticipantId("e"), Other = new ParticipantId("e2");
        private sealed class AllTargets : IBattleTargetPolicy
        { public IReadOnlyList<ParticipantId> SelectTargets(BattleTargetContext c) => c.Opponents; }
        // Test observers capture arguments only; return values never depend on invocation count.
        private sealed class ObserveRule : IScoredHandDamageRule
        {
            public string Version => "observe.1";
            public HandAttackInfo Hand;
            public DamagePlan Resolve(ScoredHandDamageContext c)
            { Hand = c.Hand; return new EqualScoreDamageRule(Version).Resolve(c); }
        }
        private sealed class ObserveModifier : IAttackDamageModifier, IIncomingDamageModifier
        {
            private readonly bool incoming;
            public readonly List<DamageModifierContext> Seen = new List<DamageModifierContext>();
            public ObserveModifier(bool incoming = false) { this.incoming = incoming; }
            public DamageModification Modify(DamageModifierContext c)
            {
                Seen.Add(c);
                if (c.Attack.Hand == null) return new DamageModification("plain", "没有出牌条件", c.Amount, c.Amount);
                var scope = incoming ? AttackCardScope.Scoring : AttackCardScope.Submitted;
                var matching = c.Attack.Hand.Cards(scope).Where(card => card.Suit == (incoming ? Suit.Hearts : Suit.Spades)).ToArray();
                return new DamageModification("test-only", "仅用于接口验证", c.Amount, incoming ? c.Amount / 2 : c.Amount + 10,
                    new[] { new DamageTriggerReason(incoming ? "test.defender" : "test.attacker",
                        incoming ? "实际计分牌包含红桃" : "提交牌包含黑桃", scope, matching.Select(card => card.Id)) });
            }
        }
        private sealed class InvalidEvidence : IAttackDamageModifier, IIncomingDamageModifier
        {
            public DamageModification Modify(DamageModifierContext c) => new DamageModification("invalid", "错误范围", c.Amount, c.Amount,
                new[] { new DamageTriggerReason("test", "把杂牌当计分牌", AttackCardScope.Scoring,
                    c.Attack.Hand.SubmittedCards.Where(card => card.Suit == Suit.Spades).Select(card => card.Id)) });
        }
        private static CardDuelState Create(CardDuelEngine engine, bool allIneffective = false, IAttackDamageModifier outgoing = null, IIncomingDamageModifier incoming = null, IScoredHandDamageRule rule = null)
        {
            var faces = StandardDeck.Cards().ToArray();
            var deck = new[] {
                new DeckEntry("red-king", faces.Single(c => c.Rank == Rank.King && c.Suit == Suit.Hearts).Id, allIneffective ? CardEffects.Ineffective : CardEffects.Enhanced),
                new DeckEntry("inactive-king", faces.Single(c => c.Rank == Rank.King && c.Suit == Suit.Diamonds).Id, CardEffects.Ineffective),
                new DeckEntry("black-kicker", faces.Single(c => c.Rank == Rank.Two && c.Suit == Suit.Spades).Id)
            };
            var state = engine.Create("origin", HmacDeckShuffler.FromSeed(new byte[32]),
                new[] { new BattlePlayerSetup(Player, new CombatantDefinition("p", 100), deck, rules: new BattlePlayerRules(handSize: 3, damagePolicy: rule), damageRules: new BattleActorDamageRules(outgoing, incoming), targetRules: new BattleActorTargetRules(new AllTargets())) },
                new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("e", 10000), 7, new BattleActorDamageRules(outgoing, incoming)), new BattleEnemySetup(Other, new CombatantDefinition("e2", 10000), 8, new BattleActorDamageRules(outgoing, incoming)) });
            return engine.Execute(state, state.Revision, Player, CardDuelAction.Start).State;
        }
        private static CardDuelEngine Engine() =>
            new CardDuelEngine(new BattleRules("origin.1", 10));

        [Test] public void FormalHandSnapshotIsSharedAcrossConstructionBothModifiersAndTargets()
        {
            var outgoing = new ObserveModifier(); var incoming = new ObserveModifier(true); var rule = new ObserveRule();
            var engine = Engine(); var state = Create(engine, outgoing: outgoing, incoming: incoming, rule: rule);
            var result = engine.Execute(state, state.Revision, Player, CardDuelAction.Play, state.Zone(CardZoneKinds.Hand, Player).Cards);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(40, result.Score.Total); Assert.AreEqual(2, result.Events.Count);
            var hand = result.Events[0].Effect.Attack.Hand;
            Assert.AreSame(result.Pattern, hand.Pattern); Assert.AreSame(rule.Hand, hand);
            CollectionAssert.AreEqual(result.Pattern.SubmittedCards, hand.SubmittedCards.Select(card => card.Id));
            Assert.AreEqual(3, hand.SubmittedCards.Count); Assert.AreEqual(2, hand.PatternCards.Count); Assert.AreEqual(1, hand.ScoringCards.Count);
            Assert.AreEqual(Suit.Hearts, hand.ScoringCards.Single().Suit); Assert.AreEqual(CardEffects.Enhanced, hand.ScoringCards.Single().Effects);
            Assert.IsTrue(hand.PatternCards.Any(card => !card.CanScore));
            Assert.IsFalse(hand.ScoringCards.Any(card => card.Suit == Suit.Spades));
            for (int i = 0; i < result.Events.Count; i++)
            {
                var hit = result.Events[i]; var attack = hit.Effect.Attack;
                Assert.AreSame(hand, attack.Hand); Assert.AreEqual(AttackOrigin.ScoredHand, attack.Origin);
                Assert.AreEqual(Player, attack.Source); Assert.AreEqual(40, attack.BaseDamage);
                Assert.AreSame(attack, outgoing.Seen[i].Attack); Assert.AreSame(attack, incoming.Seen[i].Attack);
                Assert.AreEqual(40, outgoing.Seen[i].Amount); Assert.AreEqual(50, incoming.Seen[i].Amount); Assert.AreEqual(25, hit.HealthLost);
                Assert.AreEqual("提交牌包含黑桃", hit.Effect.AttackModification.Triggers.Single().Reason);
                Assert.AreEqual("实际计分牌包含红桃", hit.IncomingModification.Triggers.Single().Reason);
                Assert.AreEqual(AttackCardScope.Submitted, hit.Effect.AttackModification.Triggers.Single().CardScope);
                Assert.AreEqual(AttackCardScope.Scoring, hit.IncomingModification.Triggers.Single().CardScope);
            }
            var moved = engine.Execute(result.State, result.State.Revision, Player, CardDuelAction.CollectPlayed);
            Assert.IsTrue(moved.Accepted); Assert.AreEqual(3, moved.State.Zone(CardZoneKinds.Discard, Player).Cards.Count);
            Assert.AreEqual(3, hand.SubmittedCards.Count); Assert.AreEqual(1, hand.ScoringCards.Count);
            Assert.AreEqual(3, state.Zone(CardZoneKinds.Hand, Player).Cards.Count);
        }
        [Test] public void PreviewAndCommitProduceIdenticalFactsAndTriggerReasonsWithoutWritingState()
        {
            var engine = Engine(); var state = Create(engine, outgoing: new ObserveModifier(), incoming: new ObserveModifier(true));
            var cards = state.Zone(CardZoneKinds.Hand, Player).Cards;
            var first = engine.PreviewPlay(state, state.Revision, Player, cards);
            var second = engine.PreviewPlay(state, state.Revision, Player, cards);
            var actual = engine.Execute(state, state.Revision, Player, CardDuelAction.Play, cards);
            foreach (var result in new[] { first, second, actual })
            {
                Assert.IsTrue(result.Accepted); Assert.AreEqual(actual.Score.Total, result.Score.Total);
                for (int i = 0; i < result.Events.Count; i++)
                {
                    var hit = result.Events[i]; var expected = actual.Events[i];
                    CollectionAssert.AreEqual(expected.Effect.Attack.Hand.SubmittedCards.Select(card => card.Id), hit.Effect.Attack.Hand.SubmittedCards.Select(card => card.Id));
                    CollectionAssert.AreEqual(expected.Effect.Attack.Hand.ScoringCards.Select(card => card.Id), hit.Effect.Attack.Hand.ScoringCards.Select(card => card.Id));
                    Assert.AreEqual(expected.IncomingModification.Triggers.Single().Reason, hit.IncomingModification.Triggers.Single().Reason);
                    Assert.AreEqual(expected.After.Health, hit.After.Health);
                }
            }
            Assert.AreEqual(3, state.Zone(CardZoneKinds.Hand, Player).Cards.Count); Assert.AreEqual(3, state.Duel.Player(Player).Energy.Energy);
            Assert.IsEmpty(state.Duel.Scores); Assert.AreEqual(10000, state.Duel.Combat.Get(Enemy).Health);
        }
        [Test] public void EnemyOrdinaryAttackHasOriginButNoFabricatedHand()
        {
            var outgoing = new ObserveModifier(); var incoming = new ObserveModifier(true); var engine = Engine();
            var state = Create(engine, outgoing: outgoing, incoming: incoming); state = engine.Execute(state, state.Revision, Player, CardDuelAction.EndTurn).State;
            var result = engine.Execute(state, state.Revision, Enemy, CardDuelAction.EnemyAction);
            Assert.IsTrue(result.Accepted); var hit = result.Events.Single();
            Assert.AreEqual(AttackOrigin.EnemyAttack, hit.Effect.Attack.Origin); Assert.IsNull(hit.Effect.Attack.Hand);
            Assert.AreSame(outgoing.Seen.Single().Attack, incoming.Seen.Single().Attack);
            Assert.IsEmpty(hit.Effect.AttackModification.Triggers); Assert.IsEmpty(hit.IncomingModification.Triggers);
            Assert.AreEqual(7, hit.HealthLost);
        }
        [Test] public void IneffectivePatternCardsRemainSubmittedButDoNotBecomeScoringCards()
        {
            var engine = Engine(); var state = Create(engine, true);
            var result = engine.PreviewPlay(state, state.Revision, Player, state.Zone(CardZoneKinds.Hand, Player).Cards);
            Assert.IsTrue(result.Accepted); var hand = result.Events[0].Effect.Attack.Hand;
            Assert.AreEqual(HandCategory.OnePair, hand.Pattern.Category); Assert.AreEqual(3, hand.SubmittedCards.Count);
            Assert.AreEqual(2, hand.PatternCards.Count); Assert.IsEmpty(hand.ScoringCards); Assert.AreEqual(20, result.Score.Total);
        }
        [TestCase(false)] [TestCase(true)] public void EvidenceCannotClaimAKickerBelongsToTheScoringScope(bool incoming)
        {
            var invalid = new InvalidEvidence(); var engine = Engine(); var state = Create(engine, outgoing: incoming ? null : invalid, incoming: incoming ? invalid : null);
            var result = engine.PreviewPlay(state, state.Revision, Player, state.Zone(CardZoneKinds.Hand, Player).Cards);
            Assert.AreEqual(CardDuelError.InvalidDamageModifier, result.Error); Assert.AreSame(state, result.State); Assert.IsEmpty(result.Events);
        }
        [Test] public void SnapshotsAndTriggerCollectionsAreCopiedAndReadOnly()
        {
            var engine = Engine(); var state = Create(engine);
            var result = engine.PreviewPlay(state, state.Revision, Player, state.Zone(CardZoneKinds.Hand, Player).Cards);
            var original = result.Events[0].Effect.Attack.Hand; var cards = original.SubmittedCards.ToArray();
            var hand = new HandAttackInfo(original.Pattern, cards); cards[0] = null;
            Assert.IsNotNull(hand.SubmittedCards[0]); Assert.Throws<NotSupportedException>(() => ((IList<AttackCardSnapshot>)hand.SubmittedCards)[0] = null);
            Assert.Throws<ArgumentException>(() => new HandAttackInfo(original.Pattern, original.SubmittedCards.Reverse()));
            var ids = new[] { hand.SubmittedCards[0].Id }; var id = ids[0];
            var trigger = new DamageTriggerReason("test", "提交条件", AttackCardScope.Submitted, ids); ids[0] = default;
            var reasons = new[] { trigger }; var change = new DamageModification("test", "测试记录", 1, 1, reasons); reasons[0] = null;
            Assert.AreEqual(id, change.Triggers.Single().CardIds.Single());
            Assert.Throws<NotSupportedException>(() => ((IList<DamageTriggerReason>)change.Triggers)[0] = null);
            Assert.Throws<ArgumentException>(() => new AttackContext(Player, AttackOrigin.EnemyAttack, 1, hand));
            Assert.Throws<ArgumentException>(() => new AttackContext(Player, AttackOrigin.ScoredHand, 1));
        }
        [Test] public void AttackSourceAndBaseDamageCannotChangeWhileEnteringCombat()
        {
            Assert.Throws<ArgumentException>(() => new CombatEffect(CombatEffectKind.Damage, Player, Enemy, 10,
                attack: new AttackContext(Enemy, AttackOrigin.Other, 10)));
            Assert.Throws<ArgumentException>(() => new CombatEffect(CombatEffectKind.Damage, Player, Enemy, 10,
                attack: new AttackContext(Player, AttackOrigin.Other, 20)));
        }
    }
}
