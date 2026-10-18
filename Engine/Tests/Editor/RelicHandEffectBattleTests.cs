using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class RelicHandEffectBattleTests
    {
        private static readonly ParticipantId Owner = RelicHandEffectTestData.Owner, Ally = RelicHandEffectTestData.Ally,
            Enemy = RelicHandEffectTestData.Enemy, Other = RelicHandEffectTestData.Other;
        private static readonly CardFace[] Straight = Cards(new[] { 2, 3, 4, 5, 6 });
        private static readonly CardFace[] Flush = Cards(new[] { 2, 4, 6, 8, 10 }, true);
        private static readonly CardFace[] FullHouse = Cards(new[] { 3, 3, 3, 5, 5 });
        private static CardFace[] Cards(int[] ranks, bool flush = false) => ranks.Select((rank, index) =>
            new CardFace((Rank)rank, flush ? Suit.Hearts : (Suit)(index % 4 + 1))).ToArray();
        private static BattleRelicSetup Gear(params RelicHandEffectModifierSpec[] specs) => RelicHandEffectTestData.Gear("r", specs);
        private sealed class Fixture
        {
            internal readonly CardDuelEngine Engine;
            internal CardDuelState State;
            internal Fixture(CardFace[] cards, BattleRelicSetup[] gear = null, EffectTargetSpec singleTarget = null,
                int enemyHealth = 10000, bool ally = false, IDiscardRewardRule reward = null, IScoredHandEffectRule customRule = null,
                IEnumerable<CombatStatusDefinition> additionalStatuses = null, BattleRelicSetup[] allyGear = null)
            {
                var profile = RelicHandEffectTestData.Profile();
                Engine = new CardDuelEngine(new BattleRules("structure-battle.1", 20,
                    statusCatalog: profile.CreateStatusCatalog("structure-statuses.1", additionalStatuses)));
                var deck = cards.Select((card, index) => new DeckEntry("origin/" + index, StandardDeck.CardId(card.Suit, card.Rank))).ToArray();
                var rules = new BattlePlayerRules(handSize: cards.Length, effectRule: customRule ?? new PokerHandEffectRule(profile, singleTarget), discardReward: reward);
                var players = new List<BattlePlayerSetup> { new BattlePlayerSetup(Owner, new CombatantDefinition("hero", 1000),
                    deck, 400, rules, relics: gear) };
                if (ally) players.Add(new BattlePlayerSetup(Ally, new CombatantDefinition("ally", 1000), deck, 500, rules, relics: allyGear));
                State = Engine.Create("structure-battle", HmacDeckShuffler.FromSeed(new byte[32]), players, new[] {
                    new BattleEnemySetup(Enemy, new CombatantDefinition("foe", enemyHealth), 10),
                    new BattleEnemySetup(Other, new CombatantDefinition("other", 10000), 10)
                });
                Act(CardDuelAction.Start);
            }
            internal CardInstanceId[] Hand => State.Zone(CardZoneKinds.Hand, Owner).Cards.ToArray();
            internal CardDuelTransition Play(BattleTargetBindings bindings = null) => Act(CardDuelAction.Play, bindings);
            internal CardDuelTransition Act(CardDuelAction action, BattleTargetBindings bindings = null, ParticipantId? actor = null)
            {
                var result = Engine.Execute(State, State.Revision, actor ?? Owner, action,
                    action == CardDuelAction.Play || action == CardDuelAction.Discard ? Hand : null, targetBindings: bindings);
                Assert.IsTrue(result.Accepted, action + ": " + result.Error); State = result.State; return result;
            }
        }

        [Test] public void ExtraStraightHitsAreAdditiveAndEachExpandedTargetUsesNumericModifiers()
        {
            var gear = Gear(RelicHandEffectModifierSpec.AddHits("extra", HandCategory.Straight, "damage/1", 2),
                RelicHandEffectModifierSpec.Replace("replace", HandCategory.Straight, "damage/1", BattleEffectSpec.Damage(10)),
                RelicHandEffectModifierSpec.Retarget("aoe", HandCategory.Straight, "damage/1", EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving)));
            var mixed = new BattleRelicSetup(gear.Definition, gear.Instance, new BattleRelicRules("mixed.1", new[] {
                new RelicNumericModifierSpec("flat", BattleNumericStat.DamageDealt, RelicNumericOperation.AddFlat, 5, new[] { CombatEffectOrigin.Primary })
            }, gear.BattleRules.HandEffects));
            var fixture = new Fixture(Straight, new[] { mixed, RelicHandEffectTestData.Gear("s",
                RelicHandEffectModifierSpec.AddHits("extra", HandCategory.Straight, "damage/1", 1)) });
            var result = fixture.Play();
            Assert.AreEqual(HandCategory.Straight, result.Pattern.Category); Assert.AreEqual(6, result.EffectPlan.HandModification.Output.Steps.Count);
            Assert.AreEqual(10, result.Events.Count); Assert.AreEqual(3, result.EffectPlan.HandModification.Input.Steps.Count);
            Assert.IsTrue(result.Events.Take(8).All(item => item.Effect.Amount == 10 && item.SettledDamage == 15));
            Assert.AreEqual(10000 - 60 - 2 * (result.Score.Total + 5), result.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(9940, result.State.Duel.Combat.Get(Other).Health);
            Assert.AreEqual(2, result.State.Duel.Player(Owner).Energy.Energy); Assert.AreEqual(1, result.State.Duel.Scores.Count);
        }

        [Test] public void FlushReplacementAndAppendKeepDamageAndScoreUnchangedWithPurePreviews()
        {
            var fixture = new Fixture(Flush, new[] { Gear(
                RelicHandEffectModifierSpec.Replace("convert", HandCategory.Flush, "heal", BattleEffectSpec.GainBlock(7)),
                RelicHandEffectModifierSpec.Append("extra", HandCategory.Flush, BattleEffectSpec.GainBlock(3), EffectTargetSpec.Self())) });
            var before = fixture.State; var runtime = BattleRelicSnapshotCodec.Serialize(before.Duel.Player(Owner).Relics);
            var preview = fixture.Engine.PreviewPlay(before, before.Revision, Owner, fixture.Hand);
            var again = fixture.Engine.PreviewPlay(before, before.Revision, Owner, fixture.Hand);
            Assert.IsTrue(preview.Accepted); Assert.IsTrue(again.Accepted); Assert.AreSame(before, fixture.State);
            var baseline = new Fixture(Flush).Play(); var result = fixture.Play();
            Assert.AreEqual(baseline.Score.Total, result.Score.Total); Assert.AreEqual(baseline.Events[0].SettledDamage, result.Events[0].SettledDamage);
            Assert.AreEqual(400, result.State.Duel.Combat.Get(Owner).Health); Assert.AreEqual(10, result.State.Duel.Combat.Get(Owner).Block);
            CollectionAssert.AreEqual(new[] { CombatEffectKind.Damage, CombatEffectKind.GainBlock, CombatEffectKind.GainBlock }, result.Events.Select(item => item.Effect.Kind));
            CollectionAssert.AreEqual(preview.EffectPlan.HandModification.Output.Steps.Select(step => step.Id), result.EffectPlan.HandModification.Output.Steps.Select(step => step.Id));
            CollectionAssert.AreEqual(again.Events.Select(item => item.SettledAmount), result.Events.Select(item => item.SettledAmount));
            Assert.AreEqual(runtime, BattleRelicSnapshotCodec.Serialize(result.State.Duel.Player(Owner).Relics));
            CollectionAssert.AreEqual(before.Random.StateBytes, result.State.Random.StateBytes);
            CollectionAssert.AreEquivalent(before.Table.Zones.SelectMany(zone => zone.Cards), result.State.Table.Zones.SelectMany(zone => zone.Cards));
        }

        [Test] public void FullHouseAoeStunReplacementKeepsSelfImmunityAndUsesExistingExpiry()
        {
            var profile = RelicHandEffectTestData.Profile();
            var fixture = new Fixture(FullHouse, new[] { Gear(
                RelicHandEffectModifierSpec.Retarget("aoe", HandCategory.FullHouse, "stun", EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving)),
                RelicHandEffectModifierSpec.Replace("longer", HandCategory.FullHouse, "stun",
                    BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(profile.StunStatusId, 1, StatusDuration.ForTriggers(3))))) });
            var result = fixture.Play(); Assert.AreEqual(HandCategory.FullHouse, result.Pattern.Category);
            Assert.AreEqual(3, result.State.Duel.Combat.Statuses.Count);
            var stuns = result.State.Duel.Combat.Statuses.Where(status => status.DefinitionId == profile.StunStatusId).ToArray();
            Assert.AreEqual(2, stuns.Length); Assert.IsTrue(stuns.All(status => status.Duration.RemainingTriggers == 3));
            Assert.AreEqual(3, result.State.Duel.Combat.Statuses.Select(status => status.Id).Distinct().Count());
            Assert.AreEqual(Owner.Value, result.State.Duel.Combat.Statuses.Single(status => status.DefinitionId == profile.ControlImmunityStatusId).Target.Id);
            fixture.Act(CardDuelAction.CollectPlayed); fixture.Act(CardDuelAction.EndTurn);
            fixture.Act(CardDuelAction.EnemyAction, actor: Enemy); fixture.Act(CardDuelAction.EnemyAction, actor: Other);
            Assert.IsTrue(fixture.State.Duel.Combat.Statuses.All(status => status.DefinitionId == profile.StunStatusId && status.Duration.RemainingTriggers == 2));
            Assert.AreEqual(2, fixture.State.Duel.Combat.Statuses.Count); Assert.AreEqual(400, fixture.State.Duel.Combat.Get(Owner).Health);
        }

        [Test] public void ChangedTargetUsesNewRequestSlotAndRejectsMissingWrongOrObsoleteChoices()
        {
            var fixture = new Fixture(Flush, new[] { Gear(RelicHandEffectModifierSpec.Retarget("friend", HandCategory.Flush, "heal",
                EffectTargetSpec.Allies(EffectTargetSelection.Requested, "friend"))) }, ally: true);
            var before = fixture.State;
            foreach (var bindings in new[] { (BattleTargetBindings)null, new BattleTargetBindings("friend", new[] { Enemy }),
                new BattleTargetBindings("obsolete", new[] { Ally }) })
            {
                var rejected = fixture.Engine.PreviewPlay(before, before.Revision, Owner, fixture.Hand, targetBindings: bindings);
                Assert.AreEqual(CardDuelError.InvalidTarget, rejected.Error); Assert.AreSame(before, rejected.State);
            }
            var result = fixture.Play(new BattleTargetBindings("friend", new[] { Ally }));
            Assert.AreEqual(400, result.State.Duel.Combat.Get(Owner).Health);
            Assert.AreEqual(500 + result.Score.Points, result.State.Duel.Combat.Get(Ally).Health);
            var aoe = new Fixture(Flush, new[] { Gear(RelicHandEffectModifierSpec.Retarget("aoe", HandCategory.Flush, "damage/1",
                EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving))) }, singleTarget: EffectTargetSpec.Opponents(EffectTargetSelection.Requested, "old"));
            var oldRequest = aoe.Engine.PreviewPlay(aoe.State, aoe.State.Revision, Owner, aoe.Hand, targetBindings: new BattleTargetBindings("old", new[] { Enemy }));
            Assert.AreEqual(CardDuelError.InvalidTarget, oldRequest.Error);
            Assert.AreEqual(3, aoe.Play().Events.Count);
        }

        [Test] public void AppendedStatusesSkipLethallyDamagedTargetsAndContinueOtherEffects()
        {
            var profile = RelicHandEffectTestData.Profile();
            var fixture = new Fixture(Flush, new[] { Gear(
                RelicHandEffectModifierSpec.Append("a-stun", HandCategory.Flush, BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(profile.StunStatusId)),
                    EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving)),
                RelicHandEffectModifierSpec.Append("b-block", HandCategory.Flush, BattleEffectSpec.GainBlock(5), EffectTargetSpec.Self())) }, enemyHealth: 1);
            var result = fixture.Play(); Assert.AreEqual(0, result.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(Other.Value, result.State.Duel.Combat.Statuses.Single().Target.Id);
            Assert.AreEqual(5, result.State.Duel.Combat.Get(Owner).Block); Assert.AreEqual(400 + result.Score.Points, result.State.Duel.Combat.Get(Owner).Health);
        }

        [Test] public void AddedHitsSkipDeadTargetsAndStillResolveRemainingPrimaryEffects()
        {
            var fixture = new Fixture(Flush, new[] { Gear(RelicHandEffectModifierSpec.AddHits("extra", HandCategory.Flush, "damage/1", 2)) }, enemyHealth: 1);
            var result = fixture.Play(); Assert.AreEqual(4, result.EffectPlan.Effects.Count);
            Assert.AreEqual(2, result.Events.Count); Assert.IsTrue(result.Events[0].Died);
            Assert.AreEqual(CombatEffectKind.Heal, result.Events[1].Effect.Kind);
        }

        [TestCase(false)] [TestCase(true)]
        public void StructuralRelicsDoNotChangeDiscardRewardsBudgetsOrRefill(bool blockReward)
        {
            var fixture = new Fixture(Flush, new[] { Gear(RelicHandEffectModifierSpec.Append("extra", HandCategory.Flush,
                BattleEffectSpec.Damage(999), EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving))) },
                reward: blockReward ? (IDiscardRewardRule)new PerCardBlockRewardRule(4) : null);
            var result = fixture.Act(CardDuelAction.Discard);
            Assert.IsNull(result.EffectPlan.HandModification); Assert.AreEqual(blockReward ? 20 : 0, result.State.Duel.Combat.Get(Owner).Block);
            Assert.AreEqual(10000, result.State.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(2, result.State.DiscardsFor(Owner));
            Assert.AreEqual(2, result.State.Duel.Player(Owner).Energy.Energy); Assert.AreEqual(5, fixture.Hand.Length);
            Assert.AreEqual(blockReward ? 1 : 0, result.Events.Count);
        }

        [Test] public void OtherPlayersAndEnemiesDoNotUseTheOwnersStructuralRelics()
        {
            var fixture = new Fixture(Flush, ally: true, allyGear: new[] { RelicHandEffectTestData.Gear("ally-relic",
                RelicHandEffectModifierSpec.Append("extra", HandCategory.Flush, BattleEffectSpec.GainBlock(99), EffectTargetSpec.Self())) });
            var result = fixture.Play(); Assert.IsNull(result.EffectPlan.HandModification); Assert.AreEqual(0, result.State.Duel.Combat.Get(Owner).Block);
            fixture.Act(CardDuelAction.CollectPlayed); fixture.Act(CardDuelAction.EndTurn); fixture.Act(CardDuelAction.EndTurn, actor: Ally);
            var enemy = fixture.Act(CardDuelAction.EnemyAction, actor: Enemy); Assert.IsNull(enemy.EffectPlan.HandModification);
        }

        [TestCase(false)] [TestCase(true)]
        public void StepAndExpandedEffectLimitsRejectTheWholeAction(bool expandedTargets)
        {
            var fixture = new Fixture(Straight, new[] { Gear(
                RelicHandEffectModifierSpec.AddHits("extra", HandCategory.Straight, "damage/1", expandedTargets ? 32 : 62),
                RelicHandEffectModifierSpec.Retarget("aoe", HandCategory.Straight, "damage/1", EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving))) });
            var before = fixture.State; var runtime = BattleRelicSnapshotCodec.Serialize(before.Duel.Player(Owner).Relics);
            var result = fixture.Engine.Execute(before, before.Revision, Owner, CardDuelAction.Play, fixture.Hand);
            Assert.AreEqual(expandedTargets ? CardDuelError.InvalidEffectPlan : CardDuelError.InvalidHandEffectModifier, result.Error);
            Assert.AreSame(before, result.State); Assert.IsEmpty(result.RuleEvents);
            Assert.AreEqual(3, result.State.Duel.Player(Owner).Energy.Energy); Assert.AreEqual(5, fixture.Hand.Length);
            Assert.AreEqual(runtime, BattleRelicSnapshotCodec.Serialize(result.State.Duel.Player(Owner).Relics));
        }

        [Test] public void AppendedOverflowRollsBackDamageCardsAndResources()
        {
            var fixture = new Fixture(Flush, new[] { Gear(RelicHandEffectModifierSpec.Append("overflow", HandCategory.Flush,
                BattleEffectSpec.GainBlock(long.MaxValue), EffectTargetSpec.Self())) });
            var before = fixture.State; var result = fixture.Engine.Execute(before, before.Revision, Owner, CardDuelAction.Play, fixture.Hand);
            Assert.AreEqual(CardDuelError.Overflow, result.Error); Assert.AreSame(before, result.State); Assert.IsEmpty(result.RuleEvents);
            Assert.AreEqual(10000, before.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(400, before.Duel.Combat.Get(Owner).Health);
        }

        [Test] public void AddedStatusUsesExistingReactionQueueWithoutReapplyingStructuralPass()
        {
            var leech = new StatusDefinitionId("structure-leech");
            var status = new CombatStatusDefinition(leech, StatusTags.None, new StatusBehaviorSpec[] { new LifeStealSpec(1) }, StatusLifetimeSpec.ForTriggers(3));
            var fixture = new Fixture(Flush, new[] { Gear(
                RelicHandEffectModifierSpec.Append("a-status", HandCategory.Flush, BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(leech)), EffectTargetSpec.Self()),
                RelicHandEffectModifierSpec.Append("b-damage", HandCategory.Flush, BattleEffectSpec.Damage(5), EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving))) },
                additionalStatuses: new[] { status });
            var result = fixture.Play(); Assert.AreEqual(4, result.EffectPlan.HandModification.Output.Steps.Count);
            var reaction = result.Events.Single(item => item.Provenance.Origin == CombatEffectOrigin.Lifesteal);
            Assert.AreEqual(5, reaction.HealthRestored); Assert.AreEqual(2, result.State.Duel.Combat.Statuses.Single().Duration.RemainingTriggers);
        }

        [Test] public void RoyalFlushKeepsEliminationWhenDamageStepSelectorIsAbsent()
        {
            var fixture = new Fixture(Cards(new[] { 10, 11, 12, 13, 14 }, true), new[] { Gear(
                RelicHandEffectModifierSpec.AddHits("extra", HandCategory.StraightFlush, "damage/1", 1)) });
            var result = fixture.Play(); Assert.IsNull(result.EffectPlan.HandModification);
            Assert.IsTrue(result.Events.All(item => item.Effect.Kind == CombatEffectKind.Eliminate));
            Assert.AreEqual(ScoredDuelPhase.Won, result.State.Duel.Phase);
        }

        [Test] public void BattleCreationValidatesStatusDependenciesAndRejectsLegacyDamageOnlyBinding()
        {
            var unknown = Gear(RelicHandEffectModifierSpec.Append("status", HandCategory.Flush,
                BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(new StatusDefinitionId("missing"))), EffectTargetSpec.Self()));
            Assert.Throws<ArgumentException>(() => new Fixture(Flush, new[] { unknown }));
            var engine = new TeamBattleEngine(new BattleRules("legacy.1", 10));
            Assert.Throws<ArgumentException>(() => engine.Create("legacy", new[] {
                new BattlePlayerSetup(Owner, new CombatantDefinition("hero", 100), relics: new[] { Gear(
                    RelicHandEffectModifierSpec.Append("extra", HandCategory.HighCard, BattleEffectSpec.Heal(1), EffectTargetSpec.Self())) })
            }, new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("foe", 100), 10) }));
        }
    }
}
