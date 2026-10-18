using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class PokerHandEffectRuleTests
    {
        private static readonly ParticipantId Player = new ParticipantId("poker-rule-player"), Enemy = new ParticipantId("poker-rule-enemy");
        private static readonly StatusDefinitionId CustomStun = new StatusDefinitionId("custom.amber"),
            CustomImmunity = new StatusDefinitionId("custom.violet");

        private static Dictionary<HandCategory, PokerHandDamageSettings> Damage(decimal? factor = null, int straightHits = 3) =>
            Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>().Where(category => category != HandCategory.FullHouse)
                .ToDictionary(category => category, category => new PokerHandDamageSettings(factor ?? (decimal)(int)category / 10,
                    category == HandCategory.Straight ? straightHits : 1));
        private static PokerHandEffectProfile Profile(decimal? factor = null, decimal heal = 0.5m, int straightHits = 3) =>
            new PokerHandEffectProfile("poker-effects.test.1", Damage(factor, straightHits), heal);
        private static ScoredHandEffectContext Context(HandCategory category, long points = 11, decimal multiplier = 2.5m, HandAttackInfo hand = null)
        {
            var scoring = new ScoreProfile("poker-effects.score.1", Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>()
                .Select(item => new HandScoreRule(item, points, multiplier)));
            var score = ScoreEngine.Evaluate("poker-effects/score", scoring, category, 1, Array.Empty<ScoringCard>(), Array.Empty<ScoringRelic>());
            var combat = new CombatState("poker-effects", 0, new[] {
                new CombatantDefinition("hero", 100).Create(Player, "player", 40),
                new CombatantDefinition("foe", 100).Create(Enemy, "enemy")
            });
            return new ScoredHandEffectContext(combat, Player, score, hand);
        }
        private static HandAttackInfo Hand(int[] ranks, bool flush = false)
        {
            var cards = ranks.Select((rank, index) => new EffectiveCard(new CardInstanceId("hand/" + index), (Rank)rank,
                flush ? Suit.Hearts : (Suit)(index % 4 + 1))).ToArray();
            var definitions = StandardDeck.Cards().ToArray();
            return new HandAttackInfo(PokerHandClassifier.Classify(cards), cards.Select(card => new AttackCardSnapshot(
                new CardInstance(card.Id, definitions.Single(face => face.Rank == card.Rank && face.Suit == card.Suit).Id), card)));
        }
        private static void AssertTarget(BattleEffectStep step, EffectTargetGroup group, EffectTargetSelection selection)
        {
            Assert.AreEqual(group, step.Target.Group); Assert.AreEqual(selection, step.Target.Selection);
        }

        [TestCase(HandCategory.HighCard, 1, EffectTargetSelection.FirstLiving)]
        [TestCase(HandCategory.OnePair, 1, EffectTargetSelection.FirstLiving)]
        [TestCase(HandCategory.TwoPair, 1, EffectTargetSelection.FirstLiving)]
        [TestCase(HandCategory.ThreeOfAKind, 1, EffectTargetSelection.AllLiving)]
        [TestCase(HandCategory.Straight, 3, EffectTargetSelection.FirstLiving)]
        [TestCase(HandCategory.FourOfAKind, 1, EffectTargetSelection.AllLiving)]
        [TestCase(HandCategory.StraightFlush, 1, EffectTargetSelection.FirstLiving)]
        [TestCase(HandCategory.FiveOfAKind, 1, EffectTargetSelection.FirstLiving)]
        public void DamageCategoriesUseTheirOwnSettingsAndKeepTargetsSeparate(HandCategory category, int count, EffectTargetSelection selection)
        {
            var profile = Profile(); var rule = new PokerHandEffectRule(profile);
            var hand = category == HandCategory.StraightFlush ? Hand(new[] { 6, 7, 8, 9, 10 }, true) : null;
            var context = Context(category, hand: hand); var sequence = rule.Resolve(context);
            Assert.AreEqual(profile.Version, rule.Version); Assert.AreEqual(rule.Version, sequence.RuleVersion);
            Assert.IsFalse(string.IsNullOrWhiteSpace(sequence.Explanation)); Assert.AreEqual(count, sequence.Steps.Count);
            foreach (var step in sequence.Steps)
            {
                Assert.AreEqual(CombatEffectKind.Damage, step.Effect.Kind);
                Assert.AreEqual((long)decimal.Floor(27m * (int)category / 10), step.Effect.Amount);
                AssertTarget(step, EffectTargetGroup.Opponents, selection);
            }
            Assert.AreEqual(0, context.Combat.Revision); Assert.IsEmpty(context.Combat.Statuses);
        }

        [Test] public void FlushHealsFromChipPointsWhileDamageUsesTheMultipliedScore()
        {
            var rule = new PokerHandEffectRule(Profile(0.25m, 0.5m));
            var context = Context(HandCategory.Flush); var sequence = rule.Resolve(context);
            Assert.AreEqual(11, context.Score.Points); Assert.AreEqual(27, context.Score.Total);
            CollectionAssert.AreEqual(new[] { CombatEffectKind.Damage, CombatEffectKind.Heal }, sequence.Steps.Select(step => step.Effect.Kind));
            CollectionAssert.AreEqual(new long[] { 6, 5 }, sequence.Steps.Select(step => step.Effect.Amount));
            AssertTarget(sequence.Steps[0], EffectTargetGroup.Opponents, EffectTargetSelection.FirstLiving);
            AssertTarget(sequence.Steps[1], EffectTargetGroup.Self, EffectTargetSelection.AllLiving);
            var changedMultiplier = rule.Resolve(Context(HandCategory.Flush, multiplier: 4m));
            Assert.AreEqual(11, changedMultiplier.Steps[0].Effect.Amount);
            Assert.AreEqual(5, changedMultiplier.Steps[1].Effect.Amount);
        }

        [Test] public void FullHouseAppliesConfiguredControlAndSelfImmunityWithoutDamage()
        {
            var lifetime = StatusLifetimeSpec.ThroughNextPhase("enemy");
            var profile = new PokerHandEffectProfile("full-house.1", Damage(), 0.5m, stunActionWindows: 3,
                controlImmunityLifetime: lifetime, stunStatusId: CustomStun, controlImmunityStatusId: CustomImmunity);
            var sequence = new PokerHandEffectRule(profile).Resolve(Context(HandCategory.FullHouse));
            Assert.AreEqual(2, sequence.Steps.Count); Assert.IsTrue(sequence.Steps.All(step => step.Effect.Kind == CombatEffectKind.ApplyStatus));
            var stun = sequence.Steps[0]; var immunity = sequence.Steps[1];
            AssertTarget(stun, EffectTargetGroup.Opponents, EffectTargetSelection.FirstLiving);
            AssertTarget(immunity, EffectTargetGroup.Self, EffectTargetSelection.AllLiving);
            Assert.AreEqual(CustomStun, stun.Effect.Status.DefinitionId); Assert.AreEqual(1, stun.Effect.Status.Stacks);
            Assert.AreEqual(3, stun.Effect.Status.Duration?.RemainingTriggers ?? stun.Effect.Status.Lifetime?.Count);
            Assert.AreEqual(CustomImmunity, immunity.Effect.Status.DefinitionId); Assert.AreEqual(1, immunity.Effect.Status.Stacks);
            Assert.AreEqual(StatusLifetimeKind.ThroughNextPhase, immunity.Effect.Status.Lifetime.Kind);
            Assert.AreEqual("enemy", immunity.Effect.Status.Lifetime.PhaseKey);
        }

        [Test] public void RoyalFlushUsesAuthoritativeAceHighStraightFlushAndEliminatesAllOpponents()
        {
            var hand = Hand(new[] { 10, 11, 12, 13, 14 }, true);
            Assert.AreEqual(HandCategory.StraightFlush, hand.Pattern.Category); Assert.AreEqual((int)Rank.Ace, hand.Pattern.TieBreakRanks[0]);
            var sequence = new PokerHandEffectRule(Profile(decimal.MaxValue)).Resolve(Context(hand.Pattern.Category, hand: hand));
            Assert.AreEqual(1, sequence.Steps.Count); Assert.AreEqual(CombatEffectKind.Eliminate, sequence.Steps[0].Effect.Kind);
            Assert.AreEqual(0, sequence.Steps[0].Effect.Amount);
            AssertTarget(sequence.Steps[0], EffectTargetGroup.Opponents, EffectTargetSelection.AllLiving);
        }

        [TestCase(false)] [TestCase(true)]
        public void OrdinaryAndWheelStraightFlushesUseSingleTargetDamageFallback(bool wheel)
        {
            var hand = Hand(wheel ? new[] { 14, 2, 3, 4, 5 } : new[] { 9, 10, 11, 12, 13 }, true);
            Assert.AreEqual(HandCategory.StraightFlush, hand.Pattern.Category);
            Assert.AreEqual(wheel ? 5 : 13, hand.Pattern.TieBreakRanks[0]);
            var sequence = new PokerHandEffectRule(Profile(0.25m)).Resolve(Context(hand.Pattern.Category, hand: hand));
            Assert.AreEqual(1, sequence.Steps.Count); Assert.AreEqual(CombatEffectKind.Damage, sequence.Steps[0].Effect.Kind);
            Assert.AreEqual(6, sequence.Steps[0].Effect.Amount);
            AssertTarget(sequence.Steps[0], EffectTargetGroup.Opponents, EffectTargetSelection.FirstLiving);
        }

        [Test] public void UnsuitedTenThroughAceRemainsAMultiHitStraight()
        {
            var hand = Hand(new[] { 10, 11, 12, 13, 14 });
            Assert.AreEqual(HandCategory.Straight, hand.Pattern.Category); Assert.AreEqual((int)Rank.Ace, hand.Pattern.TieBreakRanks[0]);
            var sequence = new PokerHandEffectRule(Profile()).Resolve(Context(hand.Pattern.Category, hand: hand));
            Assert.AreEqual(3, sequence.Steps.Count); Assert.IsTrue(sequence.Steps.All(step => step.Effect.Kind == CombatEffectKind.Damage));
        }

        [Test] public void StraightFlushRequiresAHandSnapshotAndDoesNotGuessFromTheScoreCategory()
        {
            Assert.Catch<ArgumentException>(() => new PokerHandEffectRule(Profile()).Resolve(Context(HandCategory.StraightFlush)));
        }

        [Test] public void PerHitRoundingHappensBeforeRepeatingTheStraightAndZeroCoefficientsStayLegal()
        {
            var sequence = new PokerHandEffectRule(Profile(0.25m)).Resolve(Context(HandCategory.Straight));
            CollectionAssert.AreEqual(new long[] { 6, 6, 6 }, sequence.Steps.Select(step => step.Effect.Amount));
            Assert.AreEqual(18, sequence.Steps.Sum(step => step.Effect.Amount));
            var zero = new PokerHandEffectRule(Profile(0m, 0m)).Resolve(Context(HandCategory.Flush));
            Assert.AreEqual(2, zero.Steps.Count); Assert.IsTrue(zero.Steps.All(step => step.Effect.Amount == 0));
            var sixtyFour = new PokerHandEffectRule(Profile(0.25m, straightHits: 64)).Resolve(Context(HandCategory.Straight));
            Assert.AreEqual(64, sixtyFour.Steps.Count);
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void NumericOverflowIsRejectedInsteadOfWrappingOrClamping(bool healing, bool decimalOverflow)
        {
            decimal factor = decimalOverflow ? decimal.MaxValue : (decimal)long.MaxValue;
            var rule = new PokerHandEffectRule(Profile(healing ? 0m : factor, healing ? factor : 0m));
            var context = Context(healing ? HandCategory.Flush : HandCategory.HighCard, points: 2, multiplier: 1m);
            Assert.Throws<OverflowException>(() => rule.Resolve(context));
            Assert.AreEqual(0, context.Combat.Revision); Assert.AreEqual(40, context.Combat.Get(Player).Health);
        }

        [Test] public void ProfileCopiesTheSettingsMapAndExposesReadOnlyCollections()
        {
            var input = Damage(); var original = input[HandCategory.HighCard];
            var profile = new PokerHandEffectProfile("copied-profile.1", input, 0.75m);
            input[HandCategory.HighCard] = new PokerHandDamageSettings(99); input.Remove(HandCategory.Straight);
            Assert.AreEqual(9, profile.DamageByHand.Count); Assert.AreSame(original, profile.DamageByHand[HandCategory.HighCard]);
            Assert.AreEqual(0.1m, profile.DamageByHand[HandCategory.HighCard].ScoreFactorPerHit);
            Assert.AreEqual(3, profile.DamageByHand[HandCategory.Straight].HitCount); Assert.AreEqual(0.75m, profile.FlushHealPerChip);
            Assert.Throws<NotSupportedException>(() => ((IDictionary<HandCategory, PokerHandDamageSettings>)profile.DamageByHand).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<CombatStatusDefinition>)profile.StatusDefinitions).Clear());
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void ProfileRejectsMissingDuplicateUnknownFullHouseAndNullSettings(int malformed)
        {
            var entries = Damage().ToList();
            if (malformed == 0) entries.RemoveAt(0);
            if (malformed == 1) entries.Add(entries[0]);
            if (malformed == 2) entries.Add(new KeyValuePair<HandCategory, PokerHandDamageSettings>((HandCategory)999, new PokerHandDamageSettings(1)));
            if (malformed == 3) entries.Add(new KeyValuePair<HandCategory, PokerHandDamageSettings>(HandCategory.FullHouse, new PokerHandDamageSettings(1)));
            if (malformed == 4) entries[0] = new KeyValuePair<HandCategory, PokerHandDamageSettings>(entries[0].Key, null);
            Assert.Catch<ArgumentException>(() => new PokerHandEffectProfile("invalid-map.1", entries, 0.5m));
        }

        [Test] public void InvalidNumbersStatusIdsAndLifetimePhasesAreRejectedAtConfigurationTime()
        {
            Assert.Catch<ArgumentException>(() => new PokerHandDamageSettings(-0.01m));
            Assert.Catch<ArgumentException>(() => new PokerHandDamageSettings(1m, 0));
            Assert.Catch<ArgumentException>(() => new PokerHandEffectProfile("invalid.1", Damage(), -0.01m));
            Assert.Catch<ArgumentException>(() => new PokerHandEffectProfile("invalid.1", Damage(), 1m, stunActionWindows: 0));
            Assert.Catch<ArgumentException>(() => new PokerHandEffectProfile(" ", Damage(), 1m));
            Assert.Catch<ArgumentException>(() => new PokerHandEffectProfile("invalid.1", null, 1m));
            Assert.Catch<ArgumentException>(() => new PokerHandEffectProfile("invalid.1", Damage(), 1m, stunStatusId: default(StatusDefinitionId)));
            Assert.Catch<ArgumentException>(() => new PokerHandEffectProfile("invalid.1", Damage(), 1m, controlImmunityStatusId: default(StatusDefinitionId)));
            Assert.Catch<ArgumentException>(() => new PokerHandEffectProfile("invalid.1", Damage(), 1m,
                stunStatusId: CustomStun, controlImmunityStatusId: CustomStun));
            Assert.Catch<ArgumentException>(() => new PokerHandEffectProfile("invalid.1", Damage(), 1m,
                controlImmunityLifetime: StatusLifetimeSpec.ThroughNextPhase("animation")));
        }

        [Test] public void OnlyStraightsCanHaveMultipleHitsAndTheirCountMustBeBetweenTwoAndSixtyFour()
        {
            foreach (var category in Damage().Keys.Where(category => category != HandCategory.Straight))
            {
                var entries = Damage(); entries[category] = new PokerHandDamageSettings(1m, 2);
                Assert.Catch<ArgumentException>(() => new PokerHandEffectProfile("invalid-hits.1", entries, 1m), category.ToString());
            }
            Assert.Catch<ArgumentException>(() => Profile(straightHits: 1));
            Assert.Catch<ArgumentException>(() => Profile(straightHits: 65));
        }

        [TestCase(EffectTargetSelection.Requested)] [TestCase(EffectTargetSelection.FirstLiving)]
        [TestCase(EffectTargetSelection.LowestHealth)] [TestCase(EffectTargetSelection.HighestHealth)]
        public void SingleTargetConfigurationIsPreservedBySingleTargetEffects(EffectTargetSelection selection)
        {
            var target = EffectTargetSpec.Opponents(selection, selection == EffectTargetSelection.Requested ? "victim" : null);
            var rule = new PokerHandEffectRule(Profile(), target);
            foreach (var category in new[] { HandCategory.HighCard, HandCategory.OnePair, HandCategory.TwoPair, HandCategory.Straight, HandCategory.Flush, HandCategory.FullHouse, HandCategory.FiveOfAKind })
            {
                var sequence = rule.Resolve(Context(category));
                foreach (var step in sequence.Steps.Where(step => step.Target.Group == EffectTargetGroup.Opponents))
                {
                    AssertTarget(step, EffectTargetGroup.Opponents, selection);
                    Assert.AreEqual(target.RequestSlot, step.Target.RequestSlot);
                }
            }
        }

        [Test] public void SingleTargetRequestsDoNotLeakIntoAoeRoyalOrSelfEffects()
        {
            var rule = new PokerHandEffectRule(Profile(), EffectTargetSpec.Opponents(EffectTargetSelection.Requested, "victim"));
            foreach (var category in new[] { HandCategory.ThreeOfAKind, HandCategory.FourOfAKind })
            {
                var step = rule.Resolve(Context(category)).Steps.Single();
                AssertTarget(step, EffectTargetGroup.Opponents, EffectTargetSelection.AllLiving); Assert.IsNull(step.Target.RequestSlot);
            }
            var royal = Hand(new[] { 10, 11, 12, 13, 14 }, true);
            var eliminate = rule.Resolve(Context(royal.Pattern.Category, hand: royal)).Steps.Single();
            AssertTarget(eliminate, EffectTargetGroup.Opponents, EffectTargetSelection.AllLiving); Assert.IsNull(eliminate.Target.RequestSlot);
            var healing = rule.Resolve(Context(HandCategory.Flush)).Steps[1];
            AssertTarget(healing, EffectTargetGroup.Self, EffectTargetSelection.AllLiving); Assert.IsNull(healing.Target.RequestSlot);
        }

        [Test] public void RuleRejectsFriendlyAoeAndActorPolicySingleTargetConfigurations()
        {
            foreach (var target in new[] { EffectTargetSpec.Self(), EffectTargetSpec.Allies(EffectTargetSelection.Requested),
                EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving), EffectTargetSpec.Opponents(EffectTargetSelection.ActorPolicy) })
                Assert.Catch<ArgumentException>(() => new PokerHandEffectRule(Profile(), target));
            Assert.Catch<ArgumentException>(() => new PokerHandEffectRule(null));
            Assert.Catch<ArgumentException>(() => new PokerHandEffectRule(Profile()).Resolve(null));
        }

        [Test] public void GeneratedDefinitionsExpressControlAndProtectionWithTheirConfiguredLifetimes()
        {
            var profile = new PokerHandEffectProfile("status-definitions.1", Damage(), 0.5m, stunActionWindows: 2,
                stunStatusId: CustomStun, controlImmunityStatusId: CustomImmunity);
            Assert.AreEqual(2, profile.StatusDefinitions.Count); Assert.AreEqual(2, profile.StunActionWindows);
            Assert.AreEqual(CustomStun, profile.StunStatusId); Assert.AreEqual(CustomImmunity, profile.ControlImmunityStatusId);
            var stun = profile.StatusDefinitions.Single(definition => definition.Id == CustomStun);
            var immunity = profile.StatusDefinitions.Single(definition => definition.Id == CustomImmunity);
            Assert.AreEqual(StatusTags.Control, stun.Tags); Assert.IsInstanceOf<BlockActionWindowSpec>(stun.Behaviors.Single());
            Assert.AreEqual(StatusLifetimeKind.TriggerCount, stun.DefaultLifetime.Kind); Assert.AreEqual(2, stun.DefaultLifetime.Count);
            Assert.AreEqual(StatusTags.None, immunity.Tags);
            Assert.AreEqual(StatusTags.Control, ((PreventStatusApplicationSpec)immunity.Behaviors.Single()).BlockedTags);
            Assert.AreEqual(StatusLifetimeKind.ThroughNextPhase, profile.ControlImmunityLifetime.Kind); Assert.AreEqual("enemy", profile.ControlImmunityLifetime.PhaseKey);
            Assert.AreEqual(StatusLifetimeKind.ThroughNextPhase, immunity.DefaultLifetime.Kind); Assert.AreEqual("enemy", immunity.DefaultLifetime.PhaseKey);
            Assert.IsTrue(profile.StatusDefinitions.All(definition => definition.ReapplyPolicy == StatusReapplyPolicy.KeepLonger));
            var defaults = Profile(); Assert.IsTrue(defaults.StunStatusId.IsValid); Assert.IsTrue(defaults.ControlImmunityStatusId.IsValid);
            Assert.AreNotEqual(defaults.StunStatusId, defaults.ControlImmunityStatusId);
        }

        [Test] public void CatalogCreationIncludesAdditionalDefinitionsAndRejectsIdentityCollisions()
        {
            var profile = Profile(); var extra = new CombatStatusDefinition(new StatusDefinitionId("other.definition"), StatusTags.None,
                Array.Empty<StatusBehaviorSpec>(), StatusLifetimeSpec.UntilRemoved());
            var additions = new[] { extra }; var catalog = profile.CreateStatusCatalog("merged.catalog.1", additions); additions[0] = null;
            Assert.AreEqual("merged.catalog.1", catalog.Version); Assert.AreEqual(3, catalog.Definitions.Count); Assert.AreSame(extra, catalog.Get(extra.Id));
            Assert.DoesNotThrow(() => ((IBattleStatusCatalogValidator)new PokerHandEffectRule(profile)).ValidateStatusCatalog(catalog));
            Assert.Catch<ArgumentException>(() => profile.CreateStatusCatalog("duplicate.1", new[] { profile.StatusDefinitions.First() }));
            Assert.Catch<ArgumentException>(() => profile.CreateStatusCatalog("duplicate.1", new[] { extra, extra }));
        }

        [Test] public void SupportedImmunityLifetimeOverridesAreCarriedIntoTheActualFullHouseEffect()
        {
            foreach (var lifetime in new[] { StatusLifetimeSpec.UntilRemoved(), StatusLifetimeSpec.ForTriggers(2),
                StatusLifetimeSpec.ThroughCurrentRound(), StatusLifetimeSpec.ThroughNextPhase("player") })
            {
                var profile = new PokerHandEffectProfile("override-lifetime.1", Damage(), 0.5m, controlImmunityLifetime: lifetime);
                var effect = new PokerHandEffectRule(profile).Resolve(Context(HandCategory.FullHouse)).Steps[1].Effect.Status;
                Assert.AreEqual(lifetime.Kind, effect.Lifetime.Kind); Assert.AreEqual(lifetime.Count, effect.Lifetime.Count);
                Assert.AreEqual(lifetime.PhaseKey, effect.Lifetime.PhaseKey);
                Assert.AreEqual(lifetime.Kind, profile.StatusDefinitions.Single(definition => definition.Id == profile.ControlImmunityStatusId).DefaultLifetime.Kind);
            }
        }

        [Test] public void CatalogValidatorRequiresMatchingBehaviorAndReapplyPolicyButAllowsDifferentDefaultLifetimes()
        {
            var profile = new PokerHandEffectProfile("custom-validation.1", Damage(), 0.5m,
                stunStatusId: CustomStun, controlImmunityStatusId: CustomImmunity);
            var validator = (IBattleStatusCatalogValidator)new PokerHandEffectRule(profile);
            var stun = profile.StatusDefinitions.Single(definition => definition.Id == CustomStun);
            var immunity = profile.StatusDefinitions.Single(definition => definition.Id == CustomImmunity);
            Assert.Catch<ArgumentException>(() => validator.ValidateStatusCatalog(null));
            Assert.Catch<ArgumentException>(() => validator.ValidateStatusCatalog(new CombatStatusCatalog("empty.1", Array.Empty<CombatStatusDefinition>())));
            Assert.Catch<ArgumentException>(() => validator.ValidateStatusCatalog(new CombatStatusCatalog("missing.1", new[] { stun })));
            var invalidStuns = new[] {
                new CombatStatusDefinition(CustomStun, StatusTags.None, stun.Behaviors, stun.DefaultLifetime),
                new CombatStatusDefinition(CustomStun, StatusTags.Control, Array.Empty<StatusBehaviorSpec>(), stun.DefaultLifetime),
                new CombatStatusDefinition(CustomStun, StatusTags.Control, stun.Behaviors, stun.DefaultLifetime, StatusReapplyPolicy.IndependentInstances)
            };
            foreach (var invalid in invalidStuns)
                Assert.Catch<ArgumentException>(() => validator.ValidateStatusCatalog(new CombatStatusCatalog("bad-stun.1", new[] { invalid, immunity })));
            var invalidImmunities = new[] {
                new CombatStatusDefinition(CustomImmunity, StatusTags.Control, immunity.Behaviors, immunity.DefaultLifetime),
                new CombatStatusDefinition(CustomImmunity, StatusTags.None, new StatusBehaviorSpec[] { new BlockActionWindowSpec() }, immunity.DefaultLifetime),
                new CombatStatusDefinition(CustomImmunity, StatusTags.None, immunity.Behaviors, immunity.DefaultLifetime, StatusReapplyPolicy.IndependentInstances)
            };
            foreach (var invalid in invalidImmunities)
                Assert.Catch<ArgumentException>(() => validator.ValidateStatusCatalog(new CombatStatusCatalog("bad-immunity.1", new[] { stun, invalid })));
            var changedDefaults = new CombatStatusCatalog("alternate-defaults.1", new[] {
                new CombatStatusDefinition(CustomStun, stun.Tags, stun.Behaviors, StatusLifetimeSpec.UntilRemoved()),
                new CombatStatusDefinition(CustomImmunity, immunity.Tags, immunity.Behaviors, StatusLifetimeSpec.ForTriggers(2))
            });
            Assert.DoesNotThrow(() => validator.ValidateStatusCatalog(changedDefaults));
        }
    }
}
