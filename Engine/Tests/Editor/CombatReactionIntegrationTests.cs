using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CombatReactionIntegrationTests
    {
        private static readonly ParticipantId Player = new ParticipantId("reaction-player"), Enemy = new ParticipantId("reaction-enemy"),
            Other = new ParticipantId("reaction-other");

        private sealed class SequenceRule : IScoredHandEffectRule
        {
            private readonly BattleEffectStep[] steps;
            public string Version => "reaction-sequence.test.1";
            public SequenceRule(params BattleEffectStep[] steps) { this.steps = steps; }
            public BattleEffectSequence Resolve(ScoredHandEffectContext context) => new BattleEffectSequence(Version, "reaction integration", steps);
        }
        private sealed class Provider : ICombatReactionProvider
        {
            private readonly Func<CombatReactionContext, IEnumerable<CombatReaction>> react;
            public Provider(Func<CombatReactionContext, IEnumerable<CombatReaction>> react) { this.react = react; }
            public IEnumerable<CombatReaction> React(CombatReactionContext context) => react(context);
        }
        private static BattleEffectStep Hit(long amount) => new BattleEffectStep(BattleEffectSpec.Damage(amount),
            EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving));
        private static CombatReaction Reaction(CombatEffectKind kind, ParticipantId source, ParticipantId target, long amount,
            CombatEffectOrigin origin = CombatEffectOrigin.Triggered) =>
            new CombatReaction(new CombatEffect(kind, source, target, amount), "integration.reaction", source, origin, sourceInstanceId: "relic/7");
        private static Provider Lifesteal() => new Provider(context => {
            var hit = context.Event as CombatEvent;
            return hit != null && hit.Effect.Kind == CombatEffectKind.Damage && hit.Effect.Source == Player
                ? new[] { Reaction(CombatEffectKind.Heal, Player, Player, hit.DamageToHealth / 2, CombatEffectOrigin.Lifesteal) }
                : Array.Empty<CombatReaction>();
        });
        private static BattleSession Session(ICombatReactionProvider provider, IScoredHandEffectRule rule = null, int energy = 3,
            int enemyHealth = 200, int playerBlock = 0, IDiscardRewardRule discardReward = null, bool secondEnemy = false,
            PlayerBlockExpiry blockExpiry = PlayerBlockExpiry.Retain, BattleActorDamageRules enemyDamageRules = null,
            CombatReactionLimits limits = null)
        {
            var rules = new BattleRules("reactions.battle.1", 10, playerBlockExpiry: blockExpiry,
                reactionProvider: provider, reactionLimits: limits);
            Assert.AreSame(provider, rules.ReactionProvider); Assert.IsNotNull(rules.ReactionLimits);
            if (limits != null) Assert.AreSame(limits, rules.ReactionLimits);
            var engine = new CardDuelEngine(rules);
            var players = new[] { new BattlePlayerSetup(Player, new CombatantDefinition("hero", 100, playerBlock), initialHealth: 40,
                rules: new BattlePlayerRules(maxEnergy: energy, initialEnergy: energy, discardReward: discardReward,
                    effectRule: rule ?? new SequenceRule(Hit(10)))) };
            var enemies = new List<BattleEnemySetup> { new BattleEnemySetup(Enemy, new CombatantDefinition("foe", enemyHealth), 7, enemyDamageRules) };
            if (secondEnemy) enemies.Add(new BattleEnemySetup(Other, new CombatantDefinition("other", 200), 5));
            return new BattleSession(engine, engine.Create("reaction-session", HmacDeckShuffler.FromSeed(new byte[32]), players, enemies));
        }
        private static BattleSessionResult Do(BattleSession session, CardDuelAction action, IEnumerable<CardInstanceId> cards = null)
        {
            var result = session.Execute(session.State.Revision, Player, action, cards);
            Assert.IsTrue(result.Accepted, action + ": " + result.Error); result.State.Table.Validate(); return result;
        }
        private static CardInstanceId[] OneCard(BattleSession session) => session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
        private static void SameCardState(CardDuelState expected, CardDuelState actual)
        {
            Assert.AreEqual(expected.Revision, actual.Revision); Assert.AreEqual(expected.Duel.Revision, actual.Duel.Revision);
            Assert.AreEqual(expected.Duel.Combat.Revision, actual.Duel.Combat.Revision); Assert.AreEqual(expected.Duel.Phase, actual.Duel.Phase);
            Assert.AreEqual(expected.Duel.Player(Player).Energy.Energy, actual.Duel.Player(Player).Energy.Energy);
            CollectionAssert.AreEqual(expected.Random.StateBytes, actual.Random.StateBytes);
            CollectionAssert.AreEqual(expected.Duel.Scores.Select(score => score.Total), actual.Duel.Scores.Select(score => score.Total));
            CollectionAssert.AreEqual(expected.Duel.Combat.Statuses.Select(status => status.Id), actual.Duel.Combat.Statuses.Select(status => status.Id));
            foreach (var unit in expected.Duel.Combat.Combatants)
            {
                Assert.AreEqual(unit.Health, actual.Duel.Combat.Get(unit.Id).Health);
                Assert.AreEqual(unit.Block, actual.Duel.Combat.Get(unit.Id).Block);
            }
            foreach (var zone in expected.Table.Zones) CollectionAssert.AreEqual(zone.Cards, actual.Table.GetZone(zone.Id).Cards);
        }

        [Test] public void PreviewAndCommitIncludeTheSameReactionFactsWithoutChangingTheOriginalPlan()
        {
            var session = Session(Lifesteal()); Do(session, CardDuelAction.Start);
            var before = session.State; var cards = OneCard(session);
            var first = session.Engine.PreviewPlay(before, before.Revision, Player, cards);
            var second = session.Engine.PreviewPlay(before, before.Revision, Player, cards);
            Assert.IsTrue(first.Accepted); Assert.IsTrue(second.Accepted); Assert.AreSame(before, session.State);
            Assert.AreEqual(40, before.Duel.Combat.Get(Player).Health); Assert.AreEqual(200, before.Duel.Combat.Get(Enemy).Health);
            var actual = Do(session, CardDuelAction.Play, cards).Steps[0].Transition;
            SameCardState(first.State, second.State); SameCardState(first.State, actual.State);
            Assert.AreEqual(1, actual.EffectPlan.Effects.Count); Assert.AreSame(actual.EffectPlan.Effects[0], actual.Events[0].Effect);
            Assert.AreEqual(2, actual.Events.Count); Assert.AreEqual(45, actual.State.Duel.Combat.Get(Player).Health);
            CollectionAssert.AreEqual(new[] { 0, 1 }, actual.RuleEvents.Select(fact => fact.SequenceIndex));
            var root = actual.Events[0]; var child = actual.Events[1];
            Assert.AreEqual(CombatEffectOrigin.Primary, root.Provenance.Origin); Assert.AreEqual(CombatEffectOrigin.Lifesteal, child.Provenance.Origin);
            Assert.AreEqual(0, child.Provenance.RootEffectIndex); Assert.AreEqual(1, child.Provenance.Depth);
            Assert.AreEqual(root.Revision, child.Provenance.ParentRevision); Assert.AreEqual(root.SequenceIndex, child.Provenance.ParentSequenceIndex);
            Assert.AreEqual("integration.reaction", child.Provenance.RuleId); Assert.AreEqual(Player, child.Provenance.Owner);
            Assert.AreEqual("relic/7", child.Provenance.SourceInstanceId);
            CollectionAssert.AreEqual(first.Events.Select(fact => fact.Provenance.Origin), actual.Events.Select(fact => fact.Provenance.Origin));
            Assert.AreEqual(2, actual.State.Duel.Player(Player).Energy.Energy); Assert.AreEqual(1, actual.State.Duel.Scores.Count);
        }

        [Test] public void AReactionKillingTheActorCancelsLaterPrimaryEffectsWithoutRollingBackTheAction()
        {
            var provider = new Provider(context => context.Event.Effect.Kind == CombatEffectKind.Damage
                ? new[] { Reaction(CombatEffectKind.Damage, Enemy, Player, 1000, CombatEffectOrigin.Reflection) } : Array.Empty<CombatReaction>());
            var rule = new SequenceRule(Hit(10), Hit(10), new BattleEffectStep(BattleEffectSpec.GainBlock(3), EffectTargetSpec.Self()));
            var session = Session(provider, rule); Do(session, CardDuelAction.Start); var cards = OneCard(session);
            var result = Do(session, CardDuelAction.Play, cards); var primary = result.Steps[0].Transition;
            Assert.AreEqual(1, result.Steps.Count); Assert.AreEqual(ScoredDuelPhase.Lost, session.State.Duel.Phase);
            Assert.AreEqual(3, primary.EffectPlan.Effects.Count); Assert.AreEqual(2, primary.Events.Count);
            Assert.AreEqual(190, session.State.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(0, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(0, session.State.Duel.Combat.Get(Player).Block); Assert.AreEqual(2, session.State.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(1, session.State.Duel.Scores.Count); Assert.IsEmpty(session.State.Duel.PendingEnemies);
            CollectionAssert.AreEqual(cards, session.State.Zone(CardZoneKinds.Played, Player).Cards);
        }

        [Test] public void KillingTheLastEnemyStillResolvesTheEarnedReactionBeforeVictory()
        {
            var session = Session(Lifesteal(), energy: 1, enemyHealth: 6); Do(session, CardDuelAction.Start);
            var result = Do(session, CardDuelAction.Play, OneCard(session));
            Assert.AreEqual(1, result.Steps.Count); Assert.AreEqual(ScoredDuelPhase.Won, session.State.Duel.Phase);
            Assert.AreEqual(2, result.Steps[0].Transition.Events.Count);
            Assert.AreEqual(6, result.Steps[0].Transition.Events[0].DamageToHealth);
            Assert.AreEqual(43, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(1, session.State.Duel.CompletedRounds); Assert.IsEmpty(session.State.Duel.PendingEnemies);
        }

        [Test] public void DiscardRewardsCanReactAndDerivedDamageUsesTheTargetsIncomingModifier()
        {
            var provider = new Provider(context => context.Event.Effect.Kind == CombatEffectKind.GainBlock
                ? new[] { Reaction(CombatEffectKind.Damage, Player, Enemy, 10) } : Array.Empty<CombatReaction>());
            var session = Session(provider, discardReward: new PerCardBlockRewardRule(5),
                enemyDamageRules: new BattleActorDamageRules(incomingDamage: new ScaledIncomingDamageModifier(0.5m)));
            Do(session, CardDuelAction.Start);
            var result = Do(session, CardDuelAction.Discard, OneCard(session)).Steps[0].Transition;
            Assert.AreEqual(5, result.State.Duel.Combat.Get(Player).Block); Assert.AreEqual(195, result.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(2, result.Events.Count); Assert.AreEqual(5, result.Events[1].SettledDamage);
            Assert.AreEqual(10, result.Events[1].IncomingModification.InputAmount);
            Assert.AreEqual(1, result.EffectPlan.Effects.Count); Assert.AreSame(result.DiscardReward.Plan.Effects[0], result.EffectPlan.Effects[0]);
            Assert.IsEmpty(result.State.Duel.Scores); Assert.AreEqual(2, result.State.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(2, result.State.DiscardsFor(Player));
        }

        [Test] public void EmptyDiscardRewardsDoNotCreateACombatBatchOrInvokeTheProvider()
        {
            var session = Session(new Provider(context => { throw new ArgumentException("empty plans must not invoke reactions"); }));
            Do(session, CardDuelAction.Start); var before = session.State;
            var result = Do(session, CardDuelAction.Discard, OneCard(session)).Steps[0].Transition;
            Assert.AreSame(before.Duel.Combat, result.State.Duel.Combat); Assert.IsEmpty(result.RuleEvents);
            Assert.IsNotNull(result.EffectPlan); Assert.IsEmpty(result.EffectPlan.Effects);
            Assert.AreEqual(2, result.State.Duel.Player(Player).Energy.Energy); Assert.AreEqual(2, result.State.DiscardsFor(Player));
        }

        [Test] public void PhaseEndClearBlockUsesTheProviderAndIncomingAdapterInItsSeparateBatch()
        {
            var provider = new Provider(context => context.Event.Effect.Kind == CombatEffectKind.ClearBlock
                ? new[] { Reaction(CombatEffectKind.Damage, Player, Enemy, 10) } : Array.Empty<CombatReaction>());
            var session = Session(provider, playerBlock: 20, blockExpiry: PlayerBlockExpiry.AfterEnemyPhase,
                enemyDamageRules: new BattleActorDamageRules(incomingDamage: new ScaledIncomingDamageModifier(0.5m)));
            Do(session, CardDuelAction.Start); var result = Do(session, CardDuelAction.EndTurn);
            var enemy = result.Steps.Single(step => step.Action == CardDuelAction.EnemyAction).Transition;
            CollectionAssert.AreEqual(new[] { CombatEffectKind.Damage, CombatEffectKind.ClearBlock, CombatEffectKind.Damage }, enemy.Events.Select(fact => fact.Effect.Kind));
            Assert.AreEqual(1, enemy.EffectPlan.Effects.Count); Assert.AreEqual(0, session.State.Duel.Combat.Get(Player).Block);
            Assert.AreEqual(40, session.State.Duel.Combat.Get(Player).Health); Assert.AreEqual(195, session.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(enemy.Events[0].Revision + 1, enemy.Events[1].Revision);
            Assert.AreEqual(enemy.Events[1].Revision, enemy.Events[2].Revision);
            Assert.AreEqual(enemy.Events[1].SequenceIndex, enemy.Events[2].Provenance.ParentSequenceIndex);
            Assert.AreEqual(5, enemy.Events[2].SettledDamage); Assert.AreEqual(1, session.State.Duel.CompletedRounds);
        }

        [TestCase(false)] [TestCase(true)]
        public void ConfiguredReactionLimitsReachBothActionAndMaintenanceBatches(bool maintenance)
        {
            var trigger = maintenance ? CombatEffectKind.ClearBlock : CombatEffectKind.Damage;
            var provider = new Provider(context => context.Event.Effect.Kind == trigger ? new[] {
                Reaction(CombatEffectKind.GainBlock, Player, Player, 1), Reaction(CombatEffectKind.GainBlock, Player, Player, 2)
            } : Array.Empty<CombatReaction>());
            var session = Session(provider, playerBlock: maintenance ? 20 : 0,
                blockExpiry: maintenance ? PlayerBlockExpiry.AfterEnemyPhase : PlayerBlockExpiry.Retain, limits: new CombatReactionLimits(maxEffects: 1));
            Do(session, CardDuelAction.Start); var before = session.State;
            var result = session.Execute(before.Revision, Player, maintenance ? CardDuelAction.EndTurn : CardDuelAction.Play,
                maintenance ? null : OneCard(session));
            Assert.AreEqual(CardDuelError.ReactionLimitExceeded, result.Error); Assert.AreSame(before, session.State); Assert.AreSame(before, result.State);
            Assert.IsEmpty(result.Steps); Assert.AreEqual(3, session.State.Duel.Player(Player).Energy.Energy); Assert.IsEmpty(session.State.Duel.Scores);
        }

        [Test] public void InvalidDiscardReactionsKeepTheirOwnErrorAndDoNotSpendCardsOrEnergy()
        {
            var session = Session(new Provider(context => new CombatReaction[] { null }), discardReward: new PerCardBlockRewardRule(5));
            Do(session, CardDuelAction.Start); var before = session.State;
            var result = session.Execute(before.Revision, Player, CardDuelAction.Discard, OneCard(session));
            Assert.AreEqual(CardDuelError.InvalidReaction, result.Error); Assert.AreSame(before, result.State); Assert.AreSame(before, session.State);
            Assert.IsEmpty(result.Steps); Assert.AreEqual(3, before.DiscardsFor(Player)); Assert.AreEqual(0, before.Duel.Combat.Get(Player).Block);
        }

        [Test] public void FailureInALaterEnemyReactionRollsBackTheEntireSessionIncludingEarlierReactions()
        {
            var provider = new Provider(context => {
                if (context.Event.Effect.Kind != CombatEffectKind.Damage) return Array.Empty<CombatReaction>();
                if (context.Event.Effect.Source == Player)
                {
                    var status = new StatusInstance(new StatusInstanceId("reaction-mark/1"), new StatusDefinitionId("reaction-mark"),
                        EntityRef.Participant(Player), EntityRef.Participant(Enemy), 1, StatusDuration.UntilRemoved());
                    return new[] { Reaction(CombatEffectKind.Heal, Player, Player, 5, CombatEffectOrigin.Lifesteal),
                        new CombatReaction(CombatEffect.ApplyStatus(status), "integration.mark", Player) };
                }
                return context.Event.Effect.Source == Other
                    ? new[] { Reaction(CombatEffectKind.GainBlock, Other, Other, long.MaxValue) }
                    : Array.Empty<CombatReaction>();
            });
            var session = Session(provider, energy: 1, secondEnemy: true); Do(session, CardDuelAction.Start);
            var before = session.State; var cards = OneCard(session); var hand = before.Zone(CardZoneKinds.Hand, Player).Cards.ToArray();
            var played = session.Engine.Execute(before, before.Revision, Player, CardDuelAction.Play, cards);
            Assert.IsTrue(played.Accepted); Assert.AreEqual(45, played.State.Duel.Combat.Get(Player).Health); Assert.AreEqual(1, played.State.Duel.Combat.Statuses.Count);
            var firstEnemy = session.Engine.Execute(played.State, played.State.Revision, Enemy, CardDuelAction.EnemyAction);
            Assert.IsTrue(firstEnemy.Accepted); Assert.AreEqual(38, firstEnemy.State.Duel.Combat.Get(Player).Health);
            var failedEnemy = session.Engine.Execute(firstEnemy.State, firstEnemy.State.Revision, Other, CardDuelAction.EnemyAction);
            Assert.AreEqual(CardDuelError.Overflow, failedEnemy.Error); Assert.AreSame(firstEnemy.State, failedEnemy.State);
            var result = session.Execute(before.Revision, Player, CardDuelAction.Play, cards);
            Assert.AreEqual(CardDuelError.Overflow, result.Error); Assert.AreSame(before, result.State); Assert.AreSame(before, session.State); Assert.IsEmpty(result.Steps);
            Assert.IsEmpty(session.State.Duel.Scores); Assert.IsEmpty(session.State.Duel.Combat.Statuses);
            Assert.AreEqual(1, session.State.Duel.Player(Player).Energy.Energy); Assert.AreEqual(40, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(200, session.State.Duel.Combat.Get(Enemy).Health); Assert.IsEmpty(session.State.Zone(CardZoneKinds.Played, Player).Cards);
            CollectionAssert.AreEqual(hand, session.State.Zone(CardZoneKinds.Hand, Player).Cards);
            CollectionAssert.AreEqual(before.Random.StateBytes, session.State.Random.StateBytes);
        }
    }
}
