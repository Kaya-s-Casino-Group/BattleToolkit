using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class RelicNumericBattleTests
    {
        private static readonly ParticipantId A = new ParticipantId("numeric-a"), B = new ParticipantId("numeric-b"),
            Enemy = new ParticipantId("numeric-enemy"), Other = new ParticipantId("numeric-other");
        private static RelicNumericModifierSpec Flat(string id, BattleNumericStat stat, long value, CombatEffectOrigin origin = CombatEffectOrigin.Primary) =>
            new RelicNumericModifierSpec(id, stat, RelicNumericOperation.AddFlat, value, new[] { origin });
        private static RelicNumericModifierSpec Rate(string id, BattleNumericStat stat, decimal value, CombatEffectOrigin origin = CombatEffectOrigin.Primary) =>
            new RelicNumericModifierSpec(id, stat, RelicNumericOperation.AddRate, value, new[] { origin });
        private static BattleRelicSetup[] Gear(string id, params RelicNumericModifierSpec[] modifiers) => new[] { new BattleRelicSetup(
            new RelicDefinition("def/" + id, new[] { new ScoreEffect(ScoreTiming.AfterCards, ScoreOperation.AddPoints, 999) },
                abilityId: new AbilityDefinitionId("uninstalled"), charges: 4),
            new RunRelic(id, "def/" + id, charges: 3, cooldownRounds: 2,
                memory: AbilityMemory.Empty.WithCounter("persistent", 7, MemoryScope.Encounter)), new BattleRelicRules("numeric.1", modifiers)) };
        private sealed class Sequence : IScoredHandEffectRule
        {
            private readonly BattleEffectStep[] steps;
            public string Version => "numeric.sequence.1";
            public Sequence(params BattleEffectStep[] steps) { this.steps = steps; }
            public BattleEffectSequence Resolve(ScoredHandEffectContext context) => new BattleEffectSequence(Version, "test numeric effects", steps);
        }
        private static BattleEffectStep Self(BattleEffectSpec effect) => new BattleEffectStep(effect, EffectTargetSpec.Self());
        private static BattleEffectStep Hit(long amount) => new BattleEffectStep(BattleEffectSpec.Damage(amount), EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving));
        private static BattleEffectStep Apply(StatusDefinitionId id, EffectTargetSpec target = null) =>
            new BattleEffectStep(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(id, 1)), target ?? EffectTargetSpec.Self());

        private sealed class Fixture
        {
            public readonly CardDuelEngine Engine;
            public CardDuelState State;
            public Fixture(IScoredHandEffectRule effectRule = null, BattleRelicSetup[] a = null, BattleRelicSetup[] b = null,
                int aHealth = 600, int bHealth = 600, int aBlock = 0, bool otherEnemy = false, int enemyHealth = 10000,
                CombatStatusCatalog catalog = null, IDiscardRewardRule discard = null, BattleActorDamageRules aDamage = null,
                BattleActorDamageRules bDamage = null, BattlePlayerRules playerRules = null, IEnumerable<DeckEntry> deck = null)
            {
                Engine = new CardDuelEngine(new BattleRules("numeric.battle.1", 20, statusCatalog: catalog));
                var players = new[] {
                    new BattlePlayerSetup(A, new CombatantDefinition("hero-a", 1000, aBlock), deck, aHealth,
                        playerRules ?? new BattlePlayerRules(effectRule: effectRule, discardReward: discard), aDamage, relics: a),
                    new BattlePlayerSetup(B, new CombatantDefinition("hero-b", 1000), initialHealth: bHealth, damageRules: bDamage, relics: b)
                };
                var enemies = new List<BattleEnemySetup> { new BattleEnemySetup(Enemy, new CombatantDefinition("foe", enemyHealth), 10) };
                if (otherEnemy) enemies.Add(new BattleEnemySetup(Other, new CombatantDefinition("other", 10000), 10));
                State = Engine.Create("numeric-battle", HmacDeckShuffler.FromSeed(new byte[32]), players, enemies);
                Act(CardDuelAction.Start);
            }
            public CardInstanceId[] Cards(int count = 1) => State.Zone(CardZoneKinds.Hand, A).Cards.Take(count).ToArray();
            public CardDuelTransition Act(CardDuelAction action, int count = 0, BattleTargetBindings bindings = null, ParticipantId? actor = null)
            {
                var result = Engine.Execute(State, State.Revision, actor ?? A, action, count == 0 ? null : Cards(count), targetBindings: bindings);
                Assert.IsTrue(result.Accepted, action + ": " + result.Error); State = result.State; return result;
            }
        }

        [TestCase(CombatEffectKind.Damage, BattleNumericStat.DamageDealt, BattleNumericStat.DamageReceived)]
        [TestCase(CombatEffectKind.Heal, BattleNumericStat.HealingDone, BattleNumericStat.HealingReceived)]
        [TestCase(CombatEffectKind.GainBlock, BattleNumericStat.BlockGranted, BattleNumericStat.BlockReceived)]
        public void BothOwnersModifyOnlyTheirStageAndPreserveAuthoritativeRequests(CombatEffectKind kind, BattleNumericStat outgoing, BattleNumericStat incoming)
        {
            var effect = kind == CombatEffectKind.Damage ? BattleEffectSpec.Damage(100) : kind == CombatEffectKind.Heal ? BattleEffectSpec.Heal(100) : BattleEffectSpec.GainBlock(100);
            var fixture = new Fixture(new Sequence(new BattleEffectStep(effect, EffectTargetSpec.Allies(EffectTargetSelection.Requested))),
                Gear("a", Rate("p", outgoing, 0.2m), Rate("q", outgoing, 0.3m), Flat("f", outgoing, 10)),
                Gear("b", Rate("half", incoming, -0.5m), Flat("less", incoming, -3)));
            var before = fixture.State; var aRuntime = BattleRelicSnapshotCodec.Serialize(before.Duel.Player(A).Relics);
            var bRuntime = BattleRelicSnapshotCodec.Serialize(before.Duel.Player(B).Relics);
            var result = fixture.Act(CardDuelAction.Play, 1, new BattleTargetBindings("default", new[] { B }));
            var fact = result.Events.Single();
            Assert.AreEqual(100, fact.Effect.Amount); Assert.AreEqual(100, result.EffectPlan.Effects.Single().Amount);
            Assert.AreEqual(160, fact.OutgoingAmountModification.OutputAmount); Assert.AreEqual(160, fact.IncomingAmountModification.InputAmount);
            Assert.AreEqual(77, fact.SettledAmount); Assert.AreEqual(2, result.State.Duel.Player(A).Energy.Energy);
            Assert.AreEqual(kind == CombatEffectKind.Damage ? 523 : kind == CombatEffectKind.Heal ? 677 : 600, result.State.Duel.Combat.Get(B).Health);
            Assert.AreEqual(kind == CombatEffectKind.GainBlock ? 77 : 0, result.State.Duel.Combat.Get(B).Block);
            Assert.IsTrue(fact.OutgoingAmountModification.Contributions.All(item => item.SourceInstanceId == "a"));
            Assert.IsTrue(fact.IncomingAmountModification.Contributions.All(item => item.SourceInstanceId == "b"));
            Assert.AreEqual(aRuntime, BattleRelicSnapshotCodec.Serialize(result.State.Duel.Player(A).Relics));
            Assert.AreEqual(bRuntime, BattleRelicSnapshotCodec.Serialize(result.State.Duel.Player(B).Relics));
        }

        [Test] public void ExistingActorDamageModifiersRemainInTheirOriginalStages()
        {
            var fixture = new Fixture(new Sequence(new BattleEffectStep(BattleEffectSpec.Damage(100), EffectTargetSpec.Allies(EffectTargetSelection.Requested))),
                Gear("a", Rate("rate", BattleNumericStat.DamageDealt, 0.5m), Flat("flat", BattleNumericStat.DamageDealt, 10)),
                Gear("b", Rate("rate", BattleNumericStat.DamageReceived, -0.5m), Flat("flat", BattleNumericStat.DamageReceived, -2)),
                aDamage: new BattleActorDamageRules(new ScaledAttackDamageModifier(2, 3)),
                bDamage: new BattleActorDamageRules(incomingDamage: new ScaledIncomingDamageModifier(0.5m, 1)));
            var fact = fixture.Act(CardDuelAction.Play, 1, new BattleTargetBindings("default", new[] { B })).Events.Single();
            Assert.AreEqual(203, fact.Effect.Amount); Assert.AreEqual(100, fact.Effect.Attack.BaseDamage);
            Assert.AreEqual(314, fact.OutgoingAmountModification.OutputAmount);
            Assert.AreEqual(314, fact.IncomingModification.InputAmount); Assert.AreEqual(158, fact.IncomingModification.OutputAmount);
            Assert.AreEqual(77, fact.SettledDamage); Assert.AreEqual(77, fact.DamageToHealth);
        }

        [Test] public void MultiHitAoeAppliesFlatOncePerLivingTargetPerHit()
        {
            var fixture = new Fixture(new Sequence(Hit(10), Hit(10)), Gear("a", Flat("bonus", BattleNumericStat.DamageDealt, 5)), otherEnemy: true);
            var result = fixture.Act(CardDuelAction.Play, 1);
            Assert.AreEqual(4, result.Events.Count); Assert.IsTrue(result.Events.All(item => item.SettledDamage == 15));
            Assert.AreEqual(9970, result.State.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(9970, result.State.Duel.Combat.Get(Other).Health);
            var lethal = new Fixture(new Sequence(Hit(10), Hit(10)), Gear("a", Flat("bonus", BattleNumericStat.DamageDealt, 5)), otherEnemy: true, enemyHealth: 1);
            var killed = lethal.Act(CardDuelAction.Play, 1);
            Assert.AreEqual(3, killed.Events.Count); Assert.AreEqual(1, killed.Events.Count(item => item.Effect.Target == Enemy));
        }

        [TestCase(true)] [TestCase(false)]
        public void DiscardBlockUsesBothStagesWhileEmptyDiscardRewardsStayEmpty(bool rewards)
        {
            var gear = Gear("a", Rate("rate", BattleNumericStat.BlockGranted, 0.5m), Flat("give", BattleNumericStat.BlockGranted, 1),
                Flat("take", BattleNumericStat.BlockReceived, 2));
            var fixture = new Fixture(a: gear, discard: rewards ? (IDiscardRewardRule)new PerCardBlockRewardRule(4) : null);
            var before = fixture.State; var selected = fixture.Cards(2);
            var preview = fixture.Engine.PreviewDiscard(before, before.Revision, A, selected);
            Assert.IsTrue(preview.Accepted); Assert.AreSame(before, fixture.State);
            var result = fixture.Act(CardDuelAction.Discard, 2);
            Assert.AreEqual(rewards ? 15 : 0, result.State.Duel.Combat.Get(A).Block);
            Assert.AreEqual(preview.State.Duel.Combat.Get(A).Block, result.State.Duel.Combat.Get(A).Block);
            Assert.AreEqual(2, result.State.DiscardsFor(A)); Assert.AreEqual(2, result.State.Duel.Player(A).Energy.Energy);
            Assert.AreEqual(8, result.State.Zone(CardZoneKinds.Hand, A).Cards.Count);
            if (rewards) Assert.AreEqual(8, result.Events.Single().Effect.Amount);
            else { Assert.IsEmpty(result.Events); Assert.AreSame(before.Duel.Combat, result.State.Duel.Combat); }
        }

        [Test] public void SelfHealingUsesBothStagesBeforeOverhealAndPreservesPreviewAndRuntime()
        {
            var overflowId = new StatusDefinitionId("numeric-overheal");
            var catalog = new CombatStatusCatalog("numeric-statuses.1", new[] { new CombatStatusDefinition(overflowId, StatusTags.None,
                new StatusBehaviorSpec[] { new OverhealDamageSpec(2) }, StatusLifetimeSpec.ForTriggers(3)) });
            var fixture = new Fixture(new Sequence(Apply(overflowId), Self(BattleEffectSpec.Heal(10))),
                Gear("a", Rate("out", BattleNumericStat.HealingDone, 1), Flat("in", BattleNumericStat.HealingReceived, 3),
                    Flat("primary-damage", BattleNumericStat.DamageDealt, 100), Flat("overflow-damage", BattleNumericStat.DamageDealt, 1, CombatEffectOrigin.Overheal)),
                aHealth: 995, catalog: catalog);
            var before = fixture.State; var runtime = BattleRelicSnapshotCodec.Serialize(before.Duel.Player(A).Relics); var cards = fixture.Cards();
            var preview = fixture.Engine.PreviewPlay(before, before.Revision, A, cards);
            var second = fixture.Engine.PreviewPlay(before, before.Revision, A, cards);
            Assert.IsTrue(preview.Accepted); Assert.IsTrue(second.Accepted); Assert.AreSame(before, fixture.State);
            var result = fixture.Act(CardDuelAction.Play, 1); var heal = result.Events.Single(item => item.Effect.Kind == CombatEffectKind.Heal);
            var overflow = result.Events.Single(item => item.Provenance.Origin == CombatEffectOrigin.Overheal);
            Assert.AreEqual(10, heal.Effect.Amount); Assert.AreEqual(23, heal.SettledAmount);
            Assert.AreEqual(5, heal.HealthRestored); Assert.AreEqual(18, heal.Overheal);
            Assert.AreEqual(36, overflow.Effect.Amount); Assert.AreEqual(37, overflow.SettledDamage);
            Assert.AreEqual(9963, result.State.Duel.Combat.Get(Enemy).Health);
            CollectionAssert.AreEqual(preview.Events.Select(item => item.SettledAmount), result.Events.Select(item => item.SettledAmount));
            CollectionAssert.AreEqual(second.Events.Select(item => item.SettledAmount), result.Events.Select(item => item.SettledAmount));
            CollectionAssert.AreEqual(before.Random.StateBytes, fixture.State.Random.StateBytes);
            Assert.AreEqual(runtime, BattleRelicSnapshotCodec.Serialize(before.Duel.Player(A).Relics));
            Assert.AreEqual(runtime, BattleRelicSnapshotCodec.Serialize(result.State.Duel.Player(A).Relics));
        }

        [Test] public void LifestealUsesActualModifiedHealthDamageAndExplicitHealingOriginOnly()
        {
            var leech = new StatusDefinitionId("numeric-leech");
            var catalog = new CombatStatusCatalog("numeric-statuses.1", new[] { new CombatStatusDefinition(leech, StatusTags.None,
                new StatusBehaviorSpec[] { new LifeStealSpec(0.5m) }, StatusLifetimeSpec.ForTriggers(3)) });
            var fixture = new Fixture(new Sequence(Apply(leech), Hit(10)), Gear("a",
                Flat("damage", BattleNumericStat.DamageDealt, 10), Flat("primary-heal", BattleNumericStat.HealingDone, 100),
                Rate("leech", BattleNumericStat.HealingDone, 1, CombatEffectOrigin.Lifesteal),
                Flat("receive", BattleNumericStat.HealingReceived, 3, CombatEffectOrigin.Lifesteal)), catalog: catalog, enemyHealth: 7);
            var result = fixture.Act(CardDuelAction.Play, 1); var hit = result.Events.First(); var heal = result.Events.Last();
            Assert.AreEqual(20, hit.SettledDamage); Assert.AreEqual(7, hit.DamageToHealth); Assert.AreEqual(13, hit.Overkill);
            Assert.AreEqual(CombatEffectOrigin.Lifesteal, heal.Provenance.Origin);
            Assert.AreEqual(3, heal.Effect.Amount); Assert.AreEqual(9, heal.HealthRestored); Assert.AreEqual(609, fixture.State.Duel.Combat.Get(A).Health);
        }

        [Test] public void DefenderReflectionUsesItsOwnRelicsAndAttackersReceivingRelics()
        {
            var reflect = new StatusDefinitionId("numeric-reflect");
            var catalog = new CombatStatusCatalog("numeric-statuses.1", new[] { new CombatStatusDefinition(reflect, StatusTags.None,
                new StatusBehaviorSpec[] { new ReflectBlockedDamageSpec(1) }, StatusLifetimeSpec.ForTriggers(3)) });
            var fixture = new Fixture(new Sequence(Apply(reflect, EffectTargetSpec.Allies(EffectTargetSelection.Requested)),
                new BattleEffectStep(BattleEffectSpec.GainBlock(5), EffectTargetSpec.Allies(EffectTargetSelection.Requested)),
                new BattleEffectStep(BattleEffectSpec.Damage(10), EffectTargetSpec.Allies(EffectTargetSelection.Requested))),
                Gear("a", Rate("reflect-received", BattleNumericStat.DamageReceived, -0.5m, CombatEffectOrigin.Reflection)),
                Gear("b", Flat("reflect-dealt", BattleNumericStat.DamageDealt, 3, CombatEffectOrigin.Reflection)), catalog: catalog);
            var result = fixture.Act(CardDuelAction.Play, 1, new BattleTargetBindings("default", new[] { B }));
            var bounce = result.Events.Single(item => item.Provenance.Origin == CombatEffectOrigin.Reflection);
            Assert.AreEqual(B, bounce.Effect.Source); Assert.AreEqual(A, bounce.Effect.Target);
            Assert.AreEqual(5, bounce.Effect.Amount); Assert.AreEqual(8, bounce.OutgoingAmountModification.OutputAmount);
            Assert.AreEqual(4, bounce.SettledDamage); Assert.AreEqual(596, fixture.State.Duel.Combat.Get(A).Health);
        }

        [TestCase(CombatTimingKind.ActionCompleted)] [TestCase(CombatTimingKind.PhaseEnding)]
        public void BothPeriodicClocksUseExplicitPeriodicModifiers(CombatTimingKind timing)
        {
            var poison = new StatusDefinitionId("numeric-dot");
            var catalog = new CombatStatusCatalog("numeric-statuses.1", new[] { new CombatStatusDefinition(poison, StatusTags.None,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(timing, 10) }, StatusLifetimeSpec.ForTriggers(3)) });
            var fixture = new Fixture(new Sequence(Apply(poison)), Gear("a",
                Flat("primary-only", BattleNumericStat.DamageDealt, 999),
                Flat("periodic-out", BattleNumericStat.DamageDealt, 4, CombatEffectOrigin.Periodic),
                Rate("periodic-in", BattleNumericStat.DamageReceived, -0.5m, CombatEffectOrigin.Periodic)), catalog: catalog);
            var result = fixture.Act(CardDuelAction.Play, 1);
            if (timing == CombatTimingKind.PhaseEnding)
            {
                fixture.Act(CardDuelAction.CollectPlayed); fixture.Act(CardDuelAction.EndTurn);
                result = fixture.Act(CardDuelAction.EndTurn, actor: B);
            }
            var tick = result.Events.Single(item => item.Provenance.Origin == CombatEffectOrigin.Periodic);
            Assert.AreEqual(10, tick.Effect.Amount); Assert.AreEqual(7, tick.SettledDamage);
            Assert.AreEqual(593, fixture.State.Duel.Combat.Get(A).Health);
            Assert.AreEqual(2, fixture.State.Duel.Combat.Statuses.Single().Duration.RemainingTriggers);
        }

        [TestCase(CombatEffectKind.Heal)] [TestCase(CombatEffectKind.GainBlock)]
        public void NumericOverflowRollsBackEarlierEffectsCardsBudgetsAndRuntime(CombatEffectKind kind)
        {
            var stat = kind == CombatEffectKind.Heal ? BattleNumericStat.HealingDone : BattleNumericStat.BlockGranted;
            var effect = kind == CombatEffectKind.Heal ? BattleEffectSpec.Heal(10) : BattleEffectSpec.GainBlock(10);
            var fixture = new Fixture(new Sequence(Hit(10), Self(effect)), Gear("a", Rate("overflow", stat, decimal.MaxValue)));
            var before = fixture.State; var runtime = BattleRelicSnapshotCodec.Serialize(before.Duel.Player(A).Relics);
            var result = fixture.Engine.Execute(before, before.Revision, A, CardDuelAction.Play, fixture.Cards());
            Assert.AreEqual(CardDuelError.Overflow, result.Error); Assert.AreSame(before, result.State);
            Assert.IsEmpty(result.RuleEvents); Assert.AreEqual(10000, before.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(3, before.Duel.Player(A).Energy.Energy); Assert.AreEqual(8, before.Zone(CardZoneKinds.Hand, A).Cards.Count);
            Assert.AreEqual(runtime, BattleRelicSnapshotCodec.Serialize(before.Duel.Player(A).Relics));
        }

        [Test] public void FlushHandConditionsReachHealingWithoutChangingScoreOrLeakingIntoDiscard()
        {
            var profile = new PokerHandEffectProfile("numeric-hand.1", Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>()
                .Where(category => category != HandCategory.FullHouse).Select(category => new KeyValuePair<HandCategory, PokerHandDamageSettings>(category,
                    new PokerHandDamageSettings(1, category == HandCategory.Straight ? 2 : 1))), 0.5m);
            var deck = new[] { Rank.Two, Rank.Four, Rank.Six, Rank.Eight, Rank.Ten }.Select((rank, index) =>
                new DeckEntry("flush/" + index, StandardDeck.CardId(Suit.Hearts, rank))).ToArray();
            var rules = new BattlePlayerRules(handSize: 5, effectRule: new PokerHandEffectRule(profile), discardReward: new PerCardBlockRewardRule(4));
            var gear = Gear("a",
                new RelicNumericModifierSpec("heal", BattleNumericStat.HealingDone, RelicNumericOperation.AddFlat, 5, new[] { CombatEffectOrigin.Primary }, HandCategory.Flush),
                new RelicNumericModifierSpec("block", BattleNumericStat.BlockGranted, RelicNumericOperation.AddFlat, 999, new[] { CombatEffectOrigin.Primary }, HandCategory.Flush));
            var catalog = profile.CreateStatusCatalog("numeric-hand-status.1");
            var fixture = new Fixture(a: gear, catalog: catalog, playerRules: rules, deck: deck);
            var baseline = new Fixture(catalog: catalog, playerRules: rules, deck: deck);
            var preview = fixture.Engine.PreviewPlay(fixture.State, fixture.State.Revision, A, fixture.Cards(5));
            var result = fixture.Act(CardDuelAction.Play, 5); var plain = baseline.Act(CardDuelAction.Play, 5);
            Assert.AreEqual(HandCategory.Flush, result.Pattern.Category); Assert.AreEqual(plain.Score.Total, result.Score.Total);
            var heal = result.Events.Single(item => item.Effect.Kind == CombatEffectKind.Heal);
            Assert.AreEqual(plain.Events.Single(item => item.Effect.Kind == CombatEffectKind.Heal).HealthRestored + 5, heal.HealthRestored);
            CollectionAssert.AreEqual(preview.Events.Select(item => item.SettledAmount), result.Events.Select(item => item.SettledAmount));
            var discard = new Fixture(a: gear, catalog: catalog, playerRules: rules, deck: deck).Act(CardDuelAction.Discard, 1);
            Assert.AreEqual(4, discard.State.Duel.Combat.Get(A).Block);
        }
    }
}
