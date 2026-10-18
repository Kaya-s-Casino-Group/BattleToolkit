using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    public sealed class PokerHandEffectIntegrationTests
    {
        private static readonly ParticipantId Player = new ParticipantId("mapped-player"), Enemy = new ParticipantId("mapped-first"), Other = new ParticipantId("mapped-second");
        private static readonly CardFace[] Flush = {
            Face(Rank.Two, Suit.Hearts), Face(Rank.Four, Suit.Hearts), Face(Rank.Six, Suit.Hearts),
            Face(Rank.Eight, Suit.Hearts), Face(Rank.Ten, Suit.Hearts)
        };
        private static CardFace Face(Rank rank, Suit suit) => new CardFace(rank, suit);
        // Explicit test values only; these are not game balance defaults.
        private static PokerHandEffectProfile Profile(HandCategory? overrideCategory = null, decimal overrideFactor = 1) =>
            new PokerHandEffectProfile("mapped-hand.integration.1", Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>()
                .Where(category => category != HandCategory.FullHouse).Select(category =>
                    new KeyValuePair<HandCategory, PokerHandDamageSettings>(category, new PokerHandDamageSettings(
                        category == overrideCategory ? overrideFactor : category == HandCategory.Straight ? 0.25m : category == HandCategory.Flush ? 1.5m : 1,
                        category == HandCategory.Straight ? 3 : 1))), 0.5m);
        private static BattleEnemySetup Foe(ParticipantId id, int health = 10000, int block = 0, long damage = 9,
            BattleActorDamageRules modifiers = null, IEnemyBattleEffectRule rule = null) =>
            new BattleEnemySetup(id, new CombatantDefinition(id.Value, health, block), damage, modifiers, effectRule: rule);
        private static BattleSession Ready(CardFace[] cards, PokerHandEffectProfile profile = null, int health = 100, int energy = 3,
            EffectTargetSpec singleTarget = null, BattleActorDamageRules playerModifiers = null, BattleEnemySetup[] enemies = null)
        {
            profile = profile ?? Profile();
            var engine = new CardDuelEngine(new BattleRules("mapped-battle.integration.1", 10,
                statusCatalog: profile.CreateStatusCatalog("mapped-statuses.integration.1")));
            var deck = cards.Select((card, index) => new DeckEntry("mapped-origin/" + index, StandardDeck.CardId(card.Suit, card.Rank)));
            var playerRules = new BattlePlayerRules(maxEnergy: energy, initialEnergy: energy, handSize: cards.Length, discards: 3,
                effectRule: new PokerHandEffectRule(profile, singleTarget));
            var state = engine.Create("mapped-battle", HmacDeckShuffler.FromSeed(new byte[32]),
                new[] { new BattlePlayerSetup(Player, new CombatantDefinition("mapped-hero", 100), deck, health, playerRules, playerModifiers) },
                enemies ?? new[] { Foe(Enemy), Foe(Other) });
            var session = new BattleSession(engine, state);
            var started = session.Execute(state.Revision, Player, CardDuelAction.Start);
            Assert.IsTrue(started.Accepted, started.Error.ToString());
            Assert.AreEqual(cards.Length, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Count);
            return session;
        }
        private static CardInstanceId[] Hand(BattleSession session) => session.State.Zone(CardZoneKinds.Hand, Player).Cards.ToArray();
        private static BattleSessionResult Play(BattleSession session, BattleTargetBindings bindings = null)
        {
            var result = session.Execute(session.State.Revision, Player, CardDuelAction.Play, Hand(session), targetBindings: bindings);
            Assert.IsTrue(result.Accepted, result.Error.ToString()); result.State.Table.Validate();
            return result;
        }
        private static void SamePlan(BattleEffectPlan expected, BattleEffectPlan actual)
        {
            Assert.AreEqual(expected.RuleVersion, actual.RuleVersion); Assert.AreEqual(expected.Explanation, actual.Explanation);
            CollectionAssert.AreEqual(expected.Effects.Select(item => item.Kind), actual.Effects.Select(item => item.Kind));
            CollectionAssert.AreEqual(expected.Effects.Select(item => item.Source), actual.Effects.Select(item => item.Source));
            CollectionAssert.AreEqual(expected.Effects.Select(item => item.Target), actual.Effects.Select(item => item.Target));
            CollectionAssert.AreEqual(expected.Effects.Select(item => item.Amount), actual.Effects.Select(item => item.Amount));
            CollectionAssert.AreEqual(expected.Effects.Where(item => item.Status != null).Select(item => item.Status.Id),
                actual.Effects.Where(item => item.Status != null).Select(item => item.Status.Id));
        }
        private static void Conserved(CardDuelState before, CardDuelState after)
        {
            CollectionAssert.AreEquivalent(before.Table.Zones.SelectMany(zone => zone.Cards), after.Table.Zones.SelectMany(zone => zone.Cards));
            Assert.AreEqual(after.Table.Cards.Count, after.Table.Zones.Sum(zone => zone.Cards.Count));
        }

        [Test] public void FormalMappingCannotEnterABattleWithoutItsRequiredStatusCatalog()
        {
            var engine = new CardDuelEngine(new BattleRules("missing-mapped-statuses.1", 10));
            var players = new[] { new BattlePlayerSetup(Player, new CombatantDefinition("hero", 100),
                rules: new BattlePlayerRules(effectRule: new PokerHandEffectRule(Profile()))) };
            Assert.Throws<ArgumentException>(() => engine.Create("missing-mapped-statuses", HmacDeckShuffler.FromSeed(new byte[32]),
                players, new[] { Foe(Enemy) }));
        }

        [TestCase(20)] [TestCase(98)]
        public void FlushUsesScoreForRequestedEnemyDamageAndChipsForSelfHealingWithPreviewParity(int initialHealth)
        {
            var session = Ready(Flush, health: initialHealth,
                singleTarget: EffectTargetSpec.Opponents(EffectTargetSelection.Requested, "victim"));
            var before = session.State; var selected = Hand(session);
            var missing = session.Engine.PreviewPlay(before, before.Revision, Player, selected);
            Assert.AreEqual(CardDuelError.InvalidTarget, missing.Error); Assert.AreSame(before, missing.State);
            var wrong = session.Execute(before.Revision, Player, CardDuelAction.Play, selected,
                targetBindings: new BattleTargetBindings("victim", new[] { Player }));
            Assert.AreEqual(CardDuelError.InvalidTarget, wrong.Error); Assert.AreSame(before, session.State); Assert.IsEmpty(wrong.Steps);
            var bindings = new BattleTargetBindings("victim", new[] { Other });
            var preview = session.Engine.PreviewPlay(before, before.Revision, Player, selected, targetBindings: bindings);
            var repeated = session.Engine.PreviewPlay(before, before.Revision, Player, selected, targetBindings: bindings);
            Assert.IsTrue(preview.Accepted, preview.Error.ToString()); Assert.IsTrue(repeated.Accepted);
            Assert.AreSame(before, session.State); SamePlan(preview.EffectPlan, repeated.EffectPlan);
            var result = Play(session, bindings); var primary = result.Steps[0].Transition;
            Assert.AreEqual(HandCategory.Flush, primary.Pattern.Category); SamePlan(preview.EffectPlan, primary.EffectPlan);
            long damage = (long)decimal.Floor(primary.Score.Total * 1.5m), healing = (long)decimal.Floor(primary.Score.Points * 0.5m);
            Assert.AreNotEqual(primary.Score.Total, primary.Score.Points);
            CollectionAssert.AreEqual(new[] { CombatEffectKind.Damage, CombatEffectKind.Heal }, primary.EffectPlan.Effects.Select(item => item.Kind));
            CollectionAssert.AreEqual(new[] { Other, Player }, primary.EffectPlan.Effects.Select(item => item.Target));
            CollectionAssert.AreEqual(new[] { damage, healing }, primary.EffectPlan.Effects.Select(item => item.Amount));
            Assert.AreEqual(10000, session.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(10000 - damage, session.State.Duel.Combat.Get(Other).Health);
            Assert.AreEqual(Math.Min(100, initialHealth + healing), session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(Math.Min(100 - initialHealth, healing), primary.Events.Single(item => item.Effect.Kind == CombatEffectKind.Heal).HealthRestored);
            Assert.AreEqual(2, session.State.Duel.Player(Player).Energy.Energy); Assert.AreEqual(3, session.State.DiscardsFor(Player));
            Assert.AreEqual(1, session.State.Duel.Scores.Count); Assert.IsEmpty(session.State.Zone(CardZoneKinds.Played, Player).Cards);
            CollectionAssert.AreEquivalent(selected, session.State.Zone(CardZoneKinds.Discard, Player).Cards);
            CollectionAssert.AreEqual(preview.State.Random.StateBytes, primary.State.Random.StateBytes);
            Assert.AreEqual(preview.State.Revision, primary.State.Revision); Conserved(before, session.State);
        }

        [Test] public void LethalFlushStillHealsItsLivingSourceBeforeTheBattleFinishes()
        {
            var session = Ready(Flush, health: 20, enemies: new[] { Foe(Enemy, health: 1) });
            var before = session.State; var selected = Hand(session); var result = Play(session); var primary = result.Steps[0].Transition;
            Assert.AreEqual(ScoredDuelPhase.Won, session.State.Duel.Phase); Assert.AreEqual(0, session.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(20 + (long)decimal.Floor(primary.Score.Points * 0.5m), session.State.Duel.Combat.Get(Player).Health);
            CollectionAssert.AreEqual(new[] { CombatEffectKind.Damage, CombatEffectKind.Heal }, primary.Events.Select(item => item.Effect.Kind));
            Assert.IsTrue(primary.Events[0].Died); Assert.Greater(primary.Events[1].HealthRestored, 0);
            Assert.AreEqual(1, result.Steps.Count); Assert.AreEqual(2, session.State.Duel.Player(Player).Energy.Energy);
            CollectionAssert.AreEquivalent(selected, session.State.Zone(CardZoneKinds.Played, Player).Cards);
            Conserved(before, session.State);
        }

        [TestCase(3, HandCategory.ThreeOfAKind)] [TestCase(4, HandCategory.FourOfAKind)]
        public void ThreeAndFourOfAKindDamageEveryEnemyForTheFullConfiguredAmountAtOneActionCost(int matchingCount, HandCategory category)
        {
            var cards = Enumerable.Range(0, matchingCount).Select(index => Face(Rank.Five, (Suit)(index + 1)))
                .Concat(matchingCount == 3 ? new[] { Face(Rank.Two, Suit.Clubs), Face(Rank.Nine, Suit.Spades) } : new[] { Face(Rank.Two, Suit.Clubs) }).ToArray();
            var session = Ready(cards); var before = session.State; var result = Play(session); var primary = result.Steps[0].Transition;
            Assert.AreEqual(category, primary.Pattern.Category); Assert.AreEqual(2, primary.EffectPlan.Effects.Count);
            CollectionAssert.AreEqual(new[] { Enemy, Other }, primary.EffectPlan.Effects.Select(item => item.Target));
            Assert.IsTrue(primary.EffectPlan.Effects.All(item => item.Kind == CombatEffectKind.Damage && item.Amount == primary.Score.Total));
            foreach (var id in new[] { Enemy, Other }) Assert.AreEqual(10000 - primary.Score.Total, session.State.Duel.Combat.Get(id).Health);
            Assert.AreEqual(2, session.State.Duel.Player(Player).Energy.Energy); Assert.AreEqual(1, session.State.Duel.Scores.Count);
            Assert.AreEqual(5, session.State.Zone(CardZoneKinds.Discard, Player).Cards.Count); Conserved(before, session.State);
        }

        private sealed class AddThreeDamage : IAttackDamageModifier
        {
            public DamageModification Modify(DamageModifierContext context) => new DamageModification("per-hit.integration.1", "add three per actual hit",
                context.Amount, checked(context.Amount + 3));
        }
        [Test] public void StraightCreatesThreeSeparateHitsAndAppliesExistingDamageModifiersPerHit()
        {
            var cards = new[] { Face(Rank.Two, Suit.Clubs), Face(Rank.Three, Suit.Diamonds), Face(Rank.Four, Suit.Hearts),
                Face(Rank.Five, Suit.Spades), Face(Rank.Six, Suit.Clubs) };
            var session = Ready(cards, playerModifiers: new BattleActorDamageRules(new AddThreeDamage()));
            var result = Play(session); var primary = result.Steps[0].Transition;
            Assert.AreEqual(HandCategory.Straight, primary.Pattern.Category); long basePerHit = (long)decimal.Floor(primary.Score.Total * 0.25m);
            Assert.AreEqual(3, primary.Events.Count); Assert.AreEqual(3, primary.EffectPlan.Effects.Count);
            foreach (var hit in primary.Events)
            {
                Assert.AreEqual(Enemy, hit.Effect.Target); Assert.AreEqual(basePerHit, hit.Effect.Attack.BaseDamage);
                Assert.AreEqual(basePerHit + 3, hit.Effect.Amount); Assert.AreEqual(basePerHit + 3, hit.DamageToHealth);
            }
            Assert.AreEqual(10000 - 3 * (basePerHit + 3), session.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(10000, session.State.Duel.Combat.Get(Other).Health);
            Assert.AreEqual(2, session.State.Duel.Player(Player).Energy.Energy); Assert.AreEqual(1, session.State.Duel.Scores.Count);
        }

        private sealed class RejectDamageModifier : IAttackDamageModifier, IIncomingDamageModifier
        {
            public DamageModification Modify(DamageModifierContext context) => throw new InvalidOperationException("Royal elimination must bypass damage modifiers.");
        }
        [Test] public void RealRoyalFlushEliminatesEveryShieldedEnemyWithoutInvokingDamageModifiers()
        {
            var cards = new[] { Rank.Ten, Rank.Jack, Rank.Queen, Rank.King, Rank.Ace }.Select(rank => Face(rank, Suit.Hearts)).ToArray();
            var rejectDamage = new RejectDamageModifier(); var modifiers = new BattleActorDamageRules(rejectDamage, rejectDamage);
            var session = Ready(cards, playerModifiers: modifiers, enemies: new[] {
                Foe(Enemy, block: int.MaxValue, modifiers: modifiers), Foe(Other, block: int.MaxValue, modifiers: modifiers) });
            var before = session.State; var result = Play(session); var primary = result.Steps[0].Transition;
            Assert.AreEqual(HandCategory.StraightFlush, primary.Pattern.Category); Assert.AreEqual((int)Rank.Ace, primary.Pattern.TieBreakRanks[0]);
            CollectionAssert.AreEqual(new[] { Enemy, Other }, primary.EffectPlan.Effects.Select(item => item.Target));
            Assert.IsTrue(primary.EffectPlan.Effects.All(item => item.Kind == CombatEffectKind.Eliminate));
            Assert.AreEqual(ScoredDuelPhase.Won, session.State.Duel.Phase); Assert.AreEqual(1, result.Steps.Count);
            foreach (var id in new[] { Enemy, Other })
            {
                Assert.AreEqual(0, session.State.Duel.Combat.Get(id).Health); Assert.AreEqual(int.MaxValue, session.State.Duel.Combat.Get(id).Block);
            }
            Assert.IsTrue(primary.Events.All(item => item.DeathCause == CombatDeathCause.Eliminate && item.DamageToHealth == 0 && item.SettledDamage == 0));
            Assert.AreEqual(2, session.State.Duel.Player(Player).Energy.Energy); Conserved(before, session.State);
        }

        private sealed class EnemyControlRule : IEnemyBattleEffectRule
        {
            private readonly StatusDefinitionId control;
            public string Version => "mapped-enemy-control.integration.1";
            public EnemyControlRule(StatusDefinitionId control) { this.control = control; }
            public BattleEffectSequence Resolve(EnemyBattleEffectContext context) => new BattleEffectSequence(Version, "control then ordinary damage", new[] {
                new BattleEffectStep(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(control)), EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving)),
                new BattleEffectStep(BattleEffectSpec.Damage(4), EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving))
            });
        }
        [Test] public void FullHouseSkipsItsEnemyAndProtectsTheSourceThroughTheRemainingEnemyPhase()
        {
            var cards = new[] { Face(Rank.Five, Suit.Clubs), Face(Rank.Five, Suit.Diamonds), Face(Rank.Five, Suit.Hearts),
                Face(Rank.Two, Suit.Clubs), Face(Rank.Two, Suit.Spades) };
            var profile = Profile(); var session = Ready(cards, profile, energy: 1,
                enemies: new[] { Foe(Enemy), Foe(Other, rule: new EnemyControlRule(profile.StunStatusId)) });
            var before = session.State; var preview = session.Engine.PreviewPlay(before, before.Revision, Player, Hand(session));
            Assert.IsTrue(preview.Accepted, preview.Error.ToString()); Assert.IsEmpty(before.Duel.Combat.Statuses);
            var result = Play(session); var primary = result.Steps[0].Transition; SamePlan(preview.EffectPlan, primary.EffectPlan);
            Assert.AreEqual(HandCategory.FullHouse, primary.Pattern.Category); Assert.IsEmpty(primary.Events);
            CollectionAssert.AreEqual(new[] { Enemy, Player }, primary.EffectPlan.Effects.Select(item => item.Target));
            Assert.IsTrue(primary.EffectPlan.Effects.All(item => item.Kind == CombatEffectKind.ApplyStatus));
            var control = primary.State.Duel.Combat.Statuses.Single(item => item.DefinitionId == profile.StunStatusId);
            var immunity = primary.State.Duel.Combat.Statuses.Single(item => item.DefinitionId == profile.ControlImmunityStatusId);
            Assert.AreEqual(1, control.Duration.RemainingTriggers); Assert.AreEqual(DurationKind.TriggerCount, control.Duration.Kind);
            Assert.AreEqual(DurationKind.UntilPhaseEnd, immunity.Duration.Kind); Assert.AreEqual(BattleStatusTiming.EnemyPhase, immunity.Duration.PhaseKey);
            Assert.AreEqual(BattleStatusTiming.RoundScope(before.Duel), immunity.Duration.ScopeId);
            var facts = result.Steps.SelectMany(step => step.Transition.RuleEvents).ToArray();
            Assert.AreEqual(Enemy, facts.OfType<CombatActionWindowSkippedEvent>().Single().Actor);
            var intercepted = facts.OfType<CombatStatusBlockedEvent>().Single(); Assert.AreEqual(Other, intercepted.Effect.Source); Assert.AreEqual(Player, intercepted.Effect.Target);
            Assert.AreEqual(immunity.Id, intercepted.BlockingStatusId);
            Assert.AreEqual(1, facts.OfType<CombatStatusChangedEvent>().Count(item => item.Reason == CombatStatusChangeReason.Expired));
            Assert.IsEmpty(session.State.Duel.Combat.Statuses); Assert.AreEqual(96, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(10000, session.State.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(10000, session.State.Duel.Combat.Get(Other).Health);
            Assert.AreEqual(1, session.State.Duel.CompletedRounds); Assert.AreEqual(1, session.State.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(5, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Count); Assert.AreEqual(3, session.State.DiscardsFor(Player));
            Assert.IsTrue(session.State.Duel.CanAct(Player)); Conserved(before, session.State);
        }

        [Test] public void FormalRuleOverflowRejectsPreviewAndSessionWithoutSpendingMovingOrScoring()
        {
            var session = Ready(Flush, Profile(HandCategory.Flush, decimal.MaxValue), health: 20);
            var before = session.State; var selected = Hand(session);
            var preview = session.Engine.PreviewPlay(before, before.Revision, Player, selected);
            Assert.AreEqual(CardDuelError.Overflow, preview.Error); Assert.AreSame(before, preview.State); Assert.IsEmpty(preview.RuleEvents);
            var result = session.Execute(before.Revision, Player, CardDuelAction.Play, selected);
            Assert.AreEqual(CardDuelError.Overflow, result.Error); Assert.AreSame(before, result.State); Assert.AreSame(before, session.State);
            Assert.IsEmpty(result.Steps); Assert.IsEmpty(session.State.Duel.Scores); Assert.IsEmpty(session.State.Duel.Combat.Statuses);
            Assert.AreEqual(3, session.State.Duel.Player(Player).Energy.Energy); Assert.AreEqual(3, session.State.DiscardsFor(Player));
            Assert.AreEqual(20, session.State.Duel.Combat.Get(Player).Health);
            CollectionAssert.AreEqual(selected, session.State.Zone(CardZoneKinds.Hand, Player).Cards);
            Assert.IsEmpty(session.State.Zone(CardZoneKinds.Played, Player).Cards); Assert.IsEmpty(session.State.Zone(CardZoneKinds.Discard, Player).Cards);
            CollectionAssert.AreEqual(before.Random.StateBytes, session.State.Random.StateBytes);
        }
    }
}
