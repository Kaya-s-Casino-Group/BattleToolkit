using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class RelicRuleBattleTests
    {
        private static readonly ParticipantId Owner = RelicRuleTestData.Owner, Ally = RelicRuleTestData.Ally, Enemy = RelicRuleTestData.Enemy;
        private static BattleRelicSetup[] Free(bool play, bool discard) => new[] { RelicRuleTestData.Gear("free", new[] {
            RelicRuleTestData.Value("play", BattleRuleValueKey.PlayEnergyCost, play ? -1 : 0),
            RelicRuleTestData.Value("discard", BattleRuleValueKey.DiscardEnergyCost, discard ? -1 : 0) }) };
        private static BattleRelicSetup[] Refill(CardDuelDealTrigger trigger, BattleRefillMode? mode = null, int? fixedCount = null, int delta = 0) =>
            new[] { RelicRuleTestData.Gear("refill", refills: new[] { new RelicRefillRuleSpec("draw", trigger, mode, fixedCount, delta) }) };

        [Test] public void EffectiveLimitsDriveOpeningCardsAndRoundRefillWithoutChangingBaseSelectionLimits()
        {
            var rules = new BattlePlayerRules();
            var fixture = new RelicRuleTestData.Fixture(rules, new[] { RelicRuleTestData.Gear("limits", new[] {
                RelicRuleTestData.Value("max", BattleRuleValueKey.MaxEnergy, 2),
                RelicRuleTestData.Value("initial", BattleRuleValueKey.InitialEnergy, 1),
                RelicRuleTestData.Value("hand", BattleRuleValueKey.HandLimit, 2) }) }, start: false);
            var opening = fixture.Act(CardDuelAction.Start);
            Assert.AreEqual(10, fixture.State.Zone(CardZoneKinds.Hand).Cards.Count);
            Assert.AreEqual(10, opening.Draws.Single().Plan.HandLimit); Assert.AreEqual(10, opening.Draws.Single().DrawnCount);
            Assert.AreEqual(5, fixture.State.Duel.PlayerTurn.MaxEnergy); Assert.AreEqual(4, fixture.State.Duel.PlayerTurn.Energy);
            Assert.AreEqual(8, rules.HandSize); Assert.AreEqual(3, rules.MaxEnergy); Assert.AreEqual(3, rules.InitialEnergy);
            Assert.AreEqual(5, rules.MaxPlayCards); Assert.AreEqual(5, rules.MaxDiscardCards);
            var tooMany = fixture.Engine.Execute(fixture.State, fixture.State.Revision, CardDuelAction.Play,
                fixture.State.Zone(CardZoneKinds.Hand).Cards.Take(6));
            Assert.AreEqual(CardDuelError.InvalidSelection, tooMany.Error);
            fixture.Act(CardDuelAction.Play, 3); fixture.Act(CardDuelAction.CollectPlayed); fixture.Act(CardDuelAction.EndTurn);
            var next = fixture.Act(CardDuelAction.EnemyAction);
            Assert.AreEqual(5, fixture.State.Duel.PlayerTurn.Energy); Assert.AreEqual(10, fixture.State.Zone(CardZoneKinds.Hand).Cards.Count);
            Assert.AreEqual(CardDuelDealTrigger.PlayerTurnStart, next.Draws.Single().Plan.Trigger);
            Assert.AreEqual(3, next.Draws.Single().DrawnCount);
        }

        [Test] public void MaximumEnergyDoesNotAlsoGrantInitialEnergyAndDeckValidationUsesEffectiveHandLimit()
        {
            var gear = new[] { RelicRuleTestData.Gear("limits", new[] {
                RelicRuleTestData.Value("max", BattleRuleValueKey.MaxEnergy, 1), RelicRuleTestData.Value("hand", BattleRuleValueKey.HandLimit, 1) }) };
            var fixture = new RelicRuleTestData.Fixture(gear: gear);
            Assert.AreEqual(3, fixture.State.Duel.PlayerTurn.Energy); Assert.AreEqual(4, fixture.State.Duel.PlayerTurn.MaxEnergy);
            Assert.Throws<ArgumentException>(() => new RelicRuleTestData.Fixture(gear: gear, deckSize: 8));
            var smaller = new RelicRuleTestData.Fixture(gear: new[] { RelicRuleTestData.Gear("small",
                new[] { RelicRuleTestData.Value("hand", BattleRuleValueKey.HandLimit, -4) }) }, deckSize: 4);
            Assert.AreEqual(4, smaller.State.Zone(CardZoneKinds.Hand).Cards.Count);
        }

        [TestCase(CardDuelAction.Play)] [TestCase(CardDuelAction.Discard)]
        public void PreviewAndPaymentUseTheModifiedCostAndFailedAffordabilityIsAtomic(CardDuelAction action)
        {
            var key = action == CardDuelAction.Play ? BattleRuleValueKey.PlayEnergyCost : BattleRuleValueKey.DiscardEnergyCost;
            var fixture = new RelicRuleTestData.Fixture(gear: new[] { RelicRuleTestData.Gear("cost",
                new[] { RelicRuleTestData.Value("tax", key, 1) }) });
            var before = fixture.State; var selected = before.Zone(CardZoneKinds.Hand).Cards.Take(1).ToArray();
            var preview = action == CardDuelAction.Play ? fixture.Engine.PreviewPlay(before, before.Revision, Owner, selected) :
                fixture.Engine.PreviewDiscard(before, before.Revision, Owner, selected);
            Assert.IsTrue(preview.Accepted); Assert.AreSame(before, fixture.State);
            var result = fixture.Act(action, 1);
            Assert.AreEqual(2, result.ActionCost.Cost); Assert.AreEqual(3, result.ActionCost.AvailableEnergy);
            Assert.AreEqual(before.Duel.Revision, result.ActionCost.Revision);
            Assert.AreEqual(1, result.State.Duel.PlayerTurn.Energy); Assert.AreEqual(preview.ActionCost.Cost, result.ActionCost.Cost);
            if (action == CardDuelAction.Play) fixture.Act(CardDuelAction.CollectPlayed);
            before = fixture.State;
            var rejected = fixture.Engine.Execute(before, before.Revision, action, before.Zone(CardZoneKinds.Hand).Cards.Take(1));
            Assert.AreEqual(CardDuelError.InsufficientEnergy, rejected.Error); Assert.AreSame(before, rejected.State);
            Assert.IsEmpty(rejected.RuleEvents); Assert.IsEmpty(rejected.Draws); Assert.IsNull(rejected.ActionCost);
        }

        [TestCase(true)] [TestCase(false)]
        public void PayingTheLastEnergyLeavesAWindowForTheOtherFreeAction(bool freePlay)
        {
            var fixture = new RelicRuleTestData.Fixture(new BattlePlayerRules(initialEnergy: 1), Free(freePlay, !freePlay));
            fixture.Act(freePlay ? CardDuelAction.Discard : CardDuelAction.Play, 1);
            if (!freePlay) fixture.Act(CardDuelAction.CollectPlayed);
            Assert.AreEqual(0, fixture.State.Duel.PlayerTurn.Energy); Assert.IsTrue(fixture.State.Duel.CanAct(Owner));
            var runtime = BattleRelicSnapshotCodec.Serialize(fixture.State.Duel.Player(Owner).Relics);
            var free = fixture.Act(freePlay ? CardDuelAction.Play : CardDuelAction.Discard, 1);
            Assert.AreEqual(0, free.ActionCost.Cost); Assert.AreEqual(0, fixture.State.Duel.PlayerTurn.Energy);
            Assert.IsTrue(fixture.State.Duel.CanAct(Owner));
            Assert.AreEqual(runtime, BattleRelicSnapshotCodec.Serialize(fixture.State.Duel.Player(Owner).Relics));
            if (freePlay) fixture.Act(CardDuelAction.CollectPlayed);
            fixture.Act(CardDuelAction.EndTurn);
            Assert.IsTrue(fixture.State.Duel.Player(Owner).HasEnded);
            Assert.AreEqual(ScoredDuelPhase.AwaitingEnemyAction, fixture.State.Duel.Phase);
        }

        [TestCase(false)] [TestCase(true)]
        public void FreeDiscardStillHonorsFiniteOrUnlimitedDiscardBudget(bool unlimited)
        {
            var fixture = new RelicRuleTestData.Fixture(new BattlePlayerRules(initialEnergy: 0, discards: 1, unlimitedDiscards: unlimited), Free(false, true));
            fixture.Act(CardDuelAction.Discard, 1);
            Assert.AreEqual(unlimited ? 1 : 0, fixture.State.DiscardsRemaining);
            var before = fixture.State;
            var next = fixture.Engine.Execute(before, before.Revision, CardDuelAction.Discard, before.Zone(CardZoneKinds.Hand).Cards.Take(1));
            Assert.AreEqual(unlimited ? CardDuelError.None : CardDuelError.NoDiscards, next.Error);
            Assert.IsTrue(before.Duel.CanAct(Owner), "The zero-cost window remains explicit even when its finite discard budget is exhausted.");
            if (!unlimited) Assert.AreSame(before, next.State);
            fixture.Act(CardDuelAction.EndTurn);
        }

        [Test] public void FreeActionsAreOwnerScopedAndDoNotReopenAnExplicitlyEndedPlayer()
        {
            var ally = new BattlePlayerSetup(Ally, new CombatantDefinition("ally", 1000), rules: new BattlePlayerRules(initialEnergy: 1));
            var fixture = new RelicRuleTestData.Fixture(new BattlePlayerRules(initialEnergy: 1), Free(false, true), ally: ally);
            fixture.Act(CardDuelAction.Discard, 1, Ally);
            Assert.IsTrue(fixture.State.Duel.Player(Ally).HasEnded); Assert.IsTrue(fixture.State.Duel.CanAct(Owner));
            Assert.AreEqual(1, fixture.Engine.Duel.QueryActionCost(fixture.State.Duel, Ally, ScoredDuelActionKind.Discard).Cost);
            fixture.Act(CardDuelAction.EndTurn);
            Assert.AreEqual(ScoredDuelPhase.AwaitingEnemyAction, fixture.State.Duel.Phase);
            var rejected = fixture.Engine.Execute(fixture.State, fixture.State.Revision, CardDuelAction.Discard,
                fixture.State.Zone(CardZoneKinds.Hand).Cards.Take(1));
            Assert.AreEqual(CardDuelError.InvalidPhase, rejected.Error);
        }

        private sealed class ImmediatePresentation : IBattleSessionPresentation
        {
            public bool IsPresenting => false;
            public void Initialize(CardDuelState state) { }
            public void Present(BattleSessionResult result, bool animate) { }
            public void Synchronize(CardDuelState state) { }
        }

        [Test] public void SessionAndInputAvailabilityAllowRepeatedFreePlaysWithoutAutomaticEnemyTurns()
        {
            var fixture = new RelicRuleTestData.Fixture(new BattlePlayerRules(initialEnergy: 0), Free(true, false));
            var session = new BattleSession(fixture.Engine, fixture.State);
            var playback = new BattleSessionPlayback(session, new ImmediatePresentation());
            for (int i = 0; i < 3; i++)
            {
                var card = session.State.Zone(CardZoneKinds.Hand).Cards.Take(1).ToArray();
                Assert.IsTrue(playback.CanPlay(Owner, card)); Assert.IsFalse(playback.CanDiscard(Owner, card));
                var result = playback.Submit(Owner, CardDuelAction.Play, card);
                Assert.IsTrue(result.Accepted);
                CollectionAssert.AreEqual(new[] { CardDuelAction.Play, CardDuelAction.CollectPlayed }, result.Steps.Select(step => step.Action));
                Assert.AreEqual(0, session.State.Duel.PlayerTurn.Energy); Assert.AreEqual(0, session.State.Duel.CompletedRounds);
            }
            var ended = playback.Submit(Owner, CardDuelAction.EndTurn);
            Assert.IsTrue(ended.Accepted); Assert.AreEqual(1, session.State.Duel.CompletedRounds);
            CollectionAssert.AreEqual(new[] { CardDuelAction.EndTurn, CardDuelAction.EnemyAction }, ended.Steps.Select(step => step.Action));
        }

        private static readonly StatusDefinitionId Stun = new StatusDefinitionId("rule-stun");
        private sealed class SelfStun : IScoredHandEffectRule
        {
            public string Version => "self-stun.1";
            public BattleEffectSequence Resolve(ScoredHandEffectContext context) => new BattleEffectSequence(Version, "test control", new[] {
                new BattleEffectStep(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(Stun, 1)), EffectTargetSpec.Self()) });
        }
        [Test] public void StunStillClosesAFreeActionWindowAndExpiresThroughTheExistingLifecycle()
        {
            var catalog = new CombatStatusCatalog("rule-status.1", new[] { new CombatStatusDefinition(Stun, StatusTags.Control,
                new StatusBehaviorSpec[] { new BlockActionWindowSpec() }, StatusLifetimeSpec.ForTriggers(1)) });
            var fixture = new RelicRuleTestData.Fixture(new BattlePlayerRules(initialEnergy: 0, effectRule: new SelfStun()),
                Free(true, true), catalog: catalog);
            var session = new BattleSession(fixture.Engine, fixture.State);
            var result = session.Execute(session.State.Revision, Owner, CardDuelAction.Play, session.State.Zone(CardZoneKinds.Hand).Cards.Take(1));
            Assert.IsTrue(result.Accepted);
            CollectionAssert.AreEqual(new[] { CardDuelAction.Play, CardDuelAction.SkipPlayerTurn, CardDuelAction.EnemyAction }, result.Steps.Select(step => step.Action));
            Assert.IsTrue(result.Steps[1].Transition.State.Duel.Player(Owner).HasEnded);
            Assert.IsEmpty(result.State.Duel.Combat.Statuses); Assert.AreEqual(1, result.State.Duel.CompletedRounds);
            Assert.AreEqual(3, result.State.Duel.PlayerTurn.Energy);
        }

        [TestCase(BattleRefillMode.FillToLimit, 0, 8)]
        [TestCase(BattleRefillMode.FixedCount, 1, 6)]
        [TestCase(BattleRefillMode.Skip, 20, 5)]
        public void AfterPlayModesUsePostMoveHandAndPreservePlayedCards(BattleRefillMode mode, int amount, int expectedHand)
        {
            var fixture = new RelicRuleTestData.Fixture(gear: Refill(CardDuelDealTrigger.AfterPlay, mode,
                mode == BattleRefillMode.FixedCount ? (int?)amount : null, mode == BattleRefillMode.Skip ? amount : 0));
            var result = fixture.Act(CardDuelAction.Play, 3); var draw = result.Draws.Single();
            Assert.AreEqual(expectedHand, result.State.Zone(CardZoneKinds.Hand).Cards.Count);
            Assert.AreEqual(3, result.State.Zone(CardZoneKinds.Played).Cards.Count);
            Assert.AreEqual(5, draw.Plan.HandCount); Assert.AreEqual(3, draw.Plan.MissingCards);
            Assert.AreEqual(0, draw.Plan.BaseCount); Assert.AreEqual(expectedHand - 5, draw.DrawnCount);
            Assert.AreEqual(mode, draw.Plan.Mode);
        }

        [TestCase(-10, 0)] [TestCase(1, 1)] [TestCase(int.MaxValue, 3)]
        public void InheritedZeroDrawPolicyCanReceiveAnAdditiveBonusClampedToFreeSpace(int delta, int drawn)
        {
            var fixture = new RelicRuleTestData.Fixture(gear: Refill(CardDuelDealTrigger.AfterPlay, delta: delta));
            var result = fixture.Act(CardDuelAction.Play, 3); var plan = result.Draws.Single().Plan;
            Assert.IsNull(plan.Mode); Assert.AreEqual(0, plan.BaseCount); Assert.AreEqual(delta, plan.CountDelta);
            Assert.AreEqual(drawn, plan.Count); Assert.AreEqual(5 + drawn, result.State.Zone(CardZoneKinds.Hand).Cards.Count);
        }

        [Test] public void EquivalentModesMergeWithAllDeltasIndependentOfEquipmentOrder()
        {
            Func<string, int, BattleRelicSetup> gear = (id, delta) => RelicRuleTestData.Gear(id, refills: new[] {
                new RelicRefillRuleSpec("opening", CardDuelDealTrigger.OpeningTurn, BattleRefillMode.FixedCount, 3, delta) });
            var a = gear("a", 2); var b = gear("b", -1);
            var first = new RelicRuleTestData.Fixture(gear: new[] { a, b }, start: false);
            var second = new RelicRuleTestData.Fixture(gear: new[] { b, a }, start: false);
            var left = first.Act(CardDuelAction.Start).Draws.Single(); var right = second.Act(CardDuelAction.Start).Draws.Single();
            Assert.AreEqual(3, left.Plan.SelectedCount); Assert.AreEqual(1, left.Plan.CountDelta); Assert.AreEqual(4, left.DrawnCount);
            Assert.AreEqual(left.DrawnCount, right.DrawnCount);
            CollectionAssert.AreEqual(new[] { "a", "b" }, right.Plan.Contributions.Select(item => item.SourceInstanceId));
            CollectionAssert.AreEqual(first.State.Zone(CardZoneKinds.Hand).Cards, second.State.Zone(CardZoneKinds.Hand).Cards);
        }

        [Test] public void EachRefillBoundaryAndEachOwnerAreIndependent()
        {
            var fixture = new RelicRuleTestData.Fixture(gear: new[] { RelicRuleTestData.Gear("refills", refills: new[] {
                new RelicRefillRuleSpec("opening", CardDuelDealTrigger.OpeningTurn, BattleRefillMode.FixedCount, 5),
                new RelicRefillRuleSpec("discard", CardDuelDealTrigger.AfterDiscard, BattleRefillMode.Skip),
                new RelicRefillRuleSpec("start", CardDuelDealTrigger.PlayerTurnStart, BattleRefillMode.FixedCount, 1) }) },
                ally: new BattlePlayerSetup(Ally, new CombatantDefinition("ally", 1000)));
            Assert.AreEqual(5, fixture.State.Zone(CardZoneKinds.Hand).Cards.Count);
            Assert.AreEqual(8, fixture.State.Zone(CardZoneKinds.Hand, Ally).Cards.Count);
            var discard = fixture.Act(CardDuelAction.Discard, 2);
            Assert.AreEqual(3, discard.State.Zone(CardZoneKinds.Hand).Cards.Count);
            Assert.AreEqual(5, discard.Draws.Single().Plan.BaseCount); Assert.AreEqual(0, discard.Draws.Single().DrawnCount);
            fixture.Act(CardDuelAction.EndTurn); fixture.Act(CardDuelAction.EndTurn, actor: Ally);
            var next = fixture.Act(CardDuelAction.EnemyAction);
            Assert.AreEqual(4, next.State.Zone(CardZoneKinds.Hand).Cards.Count);
            Assert.AreEqual(8, next.State.Zone(CardZoneKinds.Hand, Ally).Cards.Count);
            Assert.AreEqual(2, next.Draws.Count); Assert.AreEqual(1, next.Draws.Single(item => item.Plan.PlayerId == Owner).DrawnCount);
        }

        [Test] public void ShortDeckReportsActualDrawAndDoesNotRecyclePlayedDuringImmediateRefill()
        {
            var fixture = new RelicRuleTestData.Fixture(gear: Refill(CardDuelDealTrigger.AfterPlay, BattleRefillMode.FillToLimit), deckSize: 8);
            var before = fixture.State; var selected = before.Zone(CardZoneKinds.Hand).Cards.Take(3).ToArray();
            var result = fixture.Act(CardDuelAction.Play, 3);
            Assert.AreEqual(3, result.Draws.Single().Plan.Count); Assert.AreEqual(0, result.Draws.Single().DrawnCount);
            Assert.AreEqual(5, result.State.Zone(CardZoneKinds.Hand).Cards.Count);
            CollectionAssert.AreEqual(selected, result.State.Zone(CardZoneKinds.Played).Cards);
            CollectionAssert.AreEqual(before.Random.StateBytes, result.State.Random.StateBytes);
            fixture.Act(CardDuelAction.CollectPlayed);
            var shuffleBefore = fixture.State;
            var preview = fixture.Engine.PreviewDiscard(shuffleBefore, shuffleBefore.Revision, Owner, shuffleBefore.Zone(CardZoneKinds.Hand).Cards.Take(1));
            var discard = fixture.Act(CardDuelAction.Discard, 1);
            Assert.AreEqual(4, discard.Draws.Single().DrawnCount);
            CollectionAssert.AreEqual(preview.State.Random.StateBytes, discard.State.Random.StateBytes);
            CollectionAssert.AreEqual(preview.State.Zone(CardZoneKinds.Hand).Cards, discard.State.Zone(CardZoneKinds.Hand).Cards);
            CollectionAssert.AreNotEqual(shuffleBefore.Random.StateBytes, discard.State.Random.StateBytes);
        }

        private sealed class InvalidDraw : ICardDuelDealPolicy
        {
            private readonly CardDuelDealTrigger trigger;
            internal InvalidDraw(CardDuelDealTrigger trigger) { this.trigger = trigger; }
            public int GetDrawCount(CardDuelDealContext context) => context.Trigger == trigger ? context.MissingCards + 1 :
                new CardDuelDealPolicy().GetDrawCount(context);
        }

        [TestCase(CardDuelAction.Play, CardDuelDealTrigger.AfterPlay)]
        [TestCase(CardDuelAction.Discard, CardDuelDealTrigger.AfterDiscard)]
        public void InvalidBaseDrawPolicyIsNotMaskedAndRollsBackEffectsCostsCardsAndDiscards(CardDuelAction action, CardDuelDealTrigger trigger)
        {
            var fixture = new RelicRuleTestData.Fixture(new BattlePlayerRules(dealPolicy: new InvalidDraw(trigger),
                discardReward: new PerCardBlockRewardRule(5)), Refill(trigger, BattleRefillMode.Skip));
            var before = fixture.State; var runtime = BattleRelicSnapshotCodec.Serialize(before.Duel.Player(Owner).Relics);
            var result = fixture.Engine.Execute(before, before.Revision, action, before.Zone(CardZoneKinds.Hand).Cards.Take(2));
            Assert.AreEqual(CardDuelError.InvalidDealPlan, result.Error); Assert.AreSame(before, result.State);
            Assert.IsEmpty(result.RuleEvents); Assert.IsEmpty(result.Draws); Assert.IsNull(result.Score); Assert.IsNull(result.ActionCost);
            Assert.AreEqual(100000, result.State.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(0, result.State.Duel.Combat.Get(Owner).Block);
            Assert.AreEqual(3, result.State.DiscardsRemaining); Assert.AreEqual(3, result.State.Duel.PlayerTurn.Energy);
            Assert.AreEqual(runtime, BattleRelicSnapshotCodec.Serialize(result.State.Duel.Player(Owner).Relics));
        }

        [Test] public void FailedAutomaticTurnStartRefillRollsBackTheEntireSessionCommand()
        {
            var fixture = new RelicRuleTestData.Fixture(new BattlePlayerRules(dealPolicy: new InvalidDraw(CardDuelDealTrigger.PlayerTurnStart)), Free(true, false));
            var before = fixture.State; var session = new BattleSession(fixture.Engine, before);
            var result = session.Execute(before.Revision, Owner, CardDuelAction.EndTurn);
            Assert.AreEqual(CardDuelError.InvalidDealPlan, result.Error); Assert.AreSame(before, session.State);
            Assert.AreSame(before, result.State); Assert.IsEmpty(result.Steps); Assert.AreEqual(1000, session.State.Duel.Combat.Get(Owner).Health);
        }

        [Test] public void EmptyRuleConfigurationPreservesExistingDamageDiscardAndRefillBehavior()
        {
            var baseline = new RelicRuleTestData.Fixture();
            var empty = new RelicRuleTestData.Fixture(gear: new[] { RelicRuleTestData.Gear("empty") });
            foreach (var action in new[] { CardDuelAction.Play, CardDuelAction.CollectPlayed, CardDuelAction.Discard, CardDuelAction.EndTurn, CardDuelAction.EnemyAction })
            {
                int count = action == CardDuelAction.Play || action == CardDuelAction.Discard ? 2 : 0;
                var a = baseline.Act(action, count); var b = empty.Act(action, count);
                Assert.AreEqual(a.State.Duel.Combat.Get(Enemy).Health, b.State.Duel.Combat.Get(Enemy).Health);
                Assert.AreEqual(a.State.Duel.Combat.Get(Owner).Health, b.State.Duel.Combat.Get(Owner).Health);
                Assert.AreEqual(a.State.Duel.Combat.Get(Owner).Block, b.State.Duel.Combat.Get(Owner).Block);
                Assert.AreEqual(a.State.Duel.PlayerTurn.Energy, b.State.Duel.PlayerTurn.Energy);
                Assert.AreEqual(a.State.Duel.Phase, b.State.Duel.Phase); Assert.AreEqual(a.State.DiscardsRemaining, b.State.DiscardsRemaining);
                CollectionAssert.AreEqual(a.Events.Select(item => item.SettledAmount), b.Events.Select(item => item.SettledAmount));
                CollectionAssert.AreEqual(a.State.Random.StateBytes, b.State.Random.StateBytes);
                foreach (var zone in a.State.Table.Zones) CollectionAssert.AreEqual(zone.Cards, b.State.Table.GetZone(zone.Id).Cards);
            }
        }
    }
}
