using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleTimelineTests
    {
        private static readonly ParticipantId Player = new ParticipantId("timeline-player"), Enemy = new ParticipantId("timeline-enemy");
        private static BattleSession Create(int deckSize = 52, int energy = 3, int rounds = 8, int enemyHealth = 10000,
            ICardDuelDealPolicy deal = null, IEnemyBattleEffectRule enemyRule = null)
        {
            var engine = new CardDuelEngine(new BattleRules("timeline.1", rounds));
            var rules = new BattlePlayerRules(maxEnergy: energy, initialEnergy: energy, discards: 10,
                dealPolicy: deal, effectRule: new RelicTriggerTestData.Sequence(RelicTriggerTestData.Hit(5)));
            var state = engine.Create("timeline", HmacDeckShuffler.FromSeed(new byte[32]),
                new[] { new BattlePlayerSetup(Player, new CombatantDefinition("hero", 100), rules: rules,
                    deck: StandardDeck.Cards().Take(deckSize).Select((card, i) => new DeckEntry("card/" + i, card.Id))) },
                new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("foe", enemyHealth), 7, effectRule: enemyRule) });
            return new BattleSession(engine, state);
        }
        private static BattleSessionResult Do(BattleSession session, CardDuelAction action, int count = 0)
        {
            var result = session.Execute(session.State.Revision, session.State.Duel.PlayerId, action,
                count == 0 ? null : session.State.Zone(CardZoneKinds.Hand).Cards.Take(count));
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            Validate(result); return result;
        }
        private static void Validate(BattleSessionResult batch)
        {
            CollectionAssert.AreEqual(Enumerable.Range(0, batch.Timeline.Count).Select(i => (long)i), batch.Timeline.Select(x => x.Sequence));
            CollectionAssert.AreEqual(batch.Steps.SelectMany(s => s.Transition.RuleEvents), batch.Timeline.Where(x => x.RuleEvent != null).Select(x => x.RuleEvent));
            Assert.AreSame(batch.State, batch.Timeline.Last().State);
            foreach (var entry in batch.Timeline)
            {
                entry.State.Table.Validate();
                if (entry.Kind == BattleTimelineKind.CardsDealt)
                {
                    var hand = entry.State.Zone(CardZoneKinds.Hand, entry.CardOwner).Cards;
                    CollectionAssert.AreEqual(entry.Cards, hand.Skip(hand.Count - entry.Cards.Count),
                        "Dealt identities must be the actual destination suffix in draw order.");
                }
                Assert.AreEqual(batch.Steps[entry.StepIndex].Action, entry.Action);
                Assert.AreEqual(batch.Steps[entry.StepIndex].Actor, entry.Actor);
                if (entry.RuleEvent is CombatEvent hit)
                {
                    Assert.AreEqual(hit.After.Health, entry.State.Duel.Combat.Get(hit.After.Id).Health);
                    Assert.AreEqual(hit.After.Block, entry.State.Duel.Combat.Get(hit.After.Id).Block);
                }
            }
        }
        private static BattleTimelineEntry[] Boundary(BattleSessionResult result) => result.Timeline.Where(x =>
            x.Kind == BattleTimelineKind.BattleStarted || x.Kind == BattleTimelineKind.PhaseStarted ||
            x.Kind == BattleTimelineKind.PhaseEnding || x.Kind == BattleTimelineKind.PhaseEnded ||
            x.Kind == BattleTimelineKind.PhaseClosed || x.Kind == BattleTimelineKind.BattleEnded).ToArray();

        [Test] public void EmptyBoundariesAlwaysExistWithoutAddingCombatClocksOrRandomConsumption()
        {
            var session = Create(); var initial = session.State;
            var opening = Do(session, CardDuelAction.Start);
            CollectionAssert.AreEqual(new[] { BattleTimelineKind.BattleStarted, BattleTimelineKind.PhaseStarted }, Boundary(opening).Select(x => x.Kind));
            Assert.IsEmpty(opening.Steps.Single().Transition.RuleEvents);
            Assert.AreEqual(0, session.State.Duel.Combat.Revision);
            Assert.AreEqual(0, session.State.Duel.Revision);
            CollectionAssert.AreEqual(initial.Random.StateBytes, session.State.Random.StateBytes);
            var dealt = opening.Timeline.Single(x => x.Kind == BattleTimelineKind.CardsDealt);
            Assert.IsEmpty(opening.Timeline.First().State.Zone(CardZoneKinds.Hand).Cards);
            Assert.AreEqual(8, dealt.State.Zone(CardZoneKinds.Hand).Cards.Count);
            CollectionAssert.AreEqual(initial.Zone(CardZoneKinds.Deck).Cards.Reverse().Take(8), dealt.Cards);
            var end = Do(session, CardDuelAction.EndTurn);
            CollectionAssert.AreEqual(new[] { "PhaseEnding/player", "PhaseEnded/player", "PhaseStarted/enemy",
                "PhaseEnding/enemy", "PhaseEnded/enemy", "PhaseStarted/player" }, Boundary(end).Select(x => x.Kind + "/" + x.PhaseKey));
            Assert.IsFalse(end.Timeline.Any(x => x.Kind == BattleTimelineKind.RuleTiming));
            CollectionAssert.AreEqual(initial.Random.StateBytes, session.State.Random.StateBytes);
        }

        [Test] public void IntermediateSnapshotsRetainBeforeValuesUntilActualEffectCostAndCardTransfer()
        {
            var session = Create(); Do(session, CardDuelAction.Start); var before = session.State;
            var batch = Do(session, CardDuelAction.Play, 1);
            var hit = batch.Timeline.First(x => x.Kind == BattleTimelineKind.EffectSettled);
            Assert.AreEqual(10000, batch.Timeline.First().State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(9995, hit.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(3, hit.State.Duel.PlayerTurn.Energy);
            Assert.AreEqual(8, hit.State.Zone(CardZoneKinds.Hand).Cards.Count);
            var cost = batch.Timeline.Single(x => x.Kind == BattleTimelineKind.ActionCostSettled);
            Assert.AreEqual(2, cost.State.Duel.PlayerTurn.Energy);
            Assert.Less(hit.Sequence, cost.Sequence);
            var transfer = batch.Timeline.First(x => x.Kind == BattleTimelineKind.CardsMoved);
            Assert.Greater(transfer.Sequence, cost.Sequence);
            Assert.AreEqual(7, transfer.State.Zone(CardZoneKinds.Hand).Cards.Count);
            Assert.AreEqual(1, transfer.State.Zone(CardZoneKinds.Played).Cards.Count);
            Assert.AreEqual(before.Zone(CardZoneKinds.Hand).Cards[0], transfer.Cards.Single());
            Assert.AreEqual(10000, before.Duel.Combat.Get(Enemy).Health);
        }

        [Test] public void DealAcrossRecycleBoundaryReportsEveryConcreteCardInActualOrder()
        {
            var session = Create(deckSize: 10); Do(session, CardDuelAction.Start);
            var before = session.State; var selected = before.Zone(CardZoneKinds.Hand).Cards.Take(5).ToArray();
            var batch = Do(session, CardDuelAction.Discard, 5);
            var deals = batch.Timeline.Where(x => x.Kind == BattleTimelineKind.CardsDealt).ToArray();
            Assert.AreEqual(2, deals.Length);
            CollectionAssert.AreEqual(before.Zone(CardZoneKinds.Deck).Cards.Reverse(), deals[0].Cards);
            var recycled = batch.Timeline.Single(x => x.Kind == BattleTimelineKind.DeckRecycled);
            Assert.Less(deals[0].Sequence, recycled.Sequence); Assert.Less(recycled.Sequence, deals[1].Sequence);
            CollectionAssert.AreEquivalent(selected, recycled.Cards);
            CollectionAssert.AreEqual(recycled.Cards.Reverse().Take(3), deals[1].Cards);
            Assert.IsEmpty(recycled.State.Zone(CardZoneKinds.Discard).Cards);
            Assert.AreEqual(5, recycled.State.Zone(CardZoneKinds.Hand).Cards.Count);
            Assert.AreEqual(9, recycled.State.DiscardsRemaining);
            Assert.AreEqual(8, deals[1].State.Zone(CardZoneKinds.Hand).Cards.Count);
            CollectionAssert.AreEqual(deals[1].State.Random.StateBytes, batch.State.Random.StateBytes);
        }

        [Test] public void CurrentPlayRemainsUnavailableToRefillUntilActualCollection()
        {
            var session = Create(deckSize: 8); Do(session, CardDuelAction.Start);
            var batch = Do(session, CardDuelAction.Play, 1);
            Assert.IsFalse(batch.Timeline.Any(x => x.Kind == BattleTimelineKind.DeckRecycled || x.Kind == BattleTimelineKind.CardsDealt));
            var moves = batch.Timeline.Where(x => x.Kind == BattleTimelineKind.CardsMoved).ToArray();
            Assert.AreEqual(CardZoneKinds.Played, moves[0].ToZone);
            Assert.AreEqual(CardZoneKinds.Discard, moves[1].ToZone);
            Assert.AreEqual(CardDuelAction.CollectPlayed, moves[1].Action);
            Assert.AreNotEqual(moves[0].ActionId, moves[1].ActionId);
        }

        [Test] public void LethalOpeningCompletesReactionsBeforeOneTerminalAndNeverInventsAPlayerPhase()
        {
            var gear = RelicBoundaryTestData.Gear(new[] { RelicBoundaryTestData.Boundary("kill", effects: new[] {
                RelicTriggerTestData.Response(BattleEffectSpec.Eliminate(), CombatTargetSpec.Opponents()),
                RelicTriggerTestData.Response(BattleEffectSpec.Heal(4)) }) });
            var f = new RelicTriggerTestData.Fixture(new[] { gear }, start: false);
            var batch = Do(new BattleSession(f.Engine, f.State), CardDuelAction.Start);
            Assert.AreEqual(1, batch.Timeline.Count(x => x.Kind == BattleTimelineKind.BattleEnded));
            Assert.AreEqual(BattleTimelineKind.BattleEnded, batch.Timeline.Last().Kind);
            Assert.IsFalse(batch.Timeline.Any(x => x.Kind == BattleTimelineKind.PhaseStarted || x.Kind == BattleTimelineKind.PhaseClosed || x.Kind == BattleTimelineKind.CardsDealt));
            var effects = batch.Timeline.Where(x => x.RuleEvent is CombatEvent).ToArray();
            Assert.AreEqual(600, effects[0].State.Duel.Combat.Get(RelicTriggerTestData.Owner).Health);
            Assert.AreEqual(604, effects[1].State.Duel.Combat.Get(RelicTriggerTestData.Owner).Health);
            Assert.AreEqual(ScoredDuelPhase.Won, batch.Timeline.Last().State.Duel.Phase);
        }

        [Test] public void RelicActivationIsBetweenItsParentAndChildrenAndChargesDoNotRevertAtCost()
        {
            var f = new RelicTriggerTestData.Fixture(new[] { RelicTriggerTestData.Gear() });
            var batch = Do(new BattleSession(f.Engine, f.State), CardDuelAction.Play, 1);
            var activation = batch.Timeline.Single(x => x.Kind == BattleTimelineKind.RelicActivated);
            var report = activation.RelicActivation;
            var parent = batch.Timeline.Single(x => x.RuleEvent != null && x.RuleEvent.Revision == report.ParentRevision &&
                x.RuleEvent.SequenceIndex == report.ParentSequenceIndex);
            Assert.Less(parent.Sequence, activation.Sequence);
            Assert.IsTrue(batch.Timeline.Where(x => x.RuleEvent?.Provenance?.ActivationId == report.ActivationId).All(x => x.Sequence > activation.Sequence));
            foreach (var entry in batch.Timeline.Where(x => x.Sequence >= activation.Sequence))
                Assert.AreEqual(4, entry.State.Duel.Player(RelicTriggerTestData.Owner).Relics.Relics.Single().RemainingCharges, entry.Kind.ToString());
        }

        [Test] public void ControlledEnemyPublishesSkipAndConsumptionWithoutAttackEffect()
        {
            var rules = new BattlePlayerRules(effectRule: new RelicTriggerTestData.Sequence(new BattleEffectStep(
                RelicTriggerTestData.Status(RelicTriggerTestData.Stun), EffectTargetSpec.Opponents())));
            var f = new RelicTriggerTestData.Fixture(rules: rules, catalog: RelicTriggerTestData.Catalog());
            var session = new BattleSession(f.Engine, f.State);
            var play = Do(session, CardDuelAction.Play, 1);
            var status = play.Timeline.Single(x => x.RuleEvent is CombatStatusEvent);
            Assert.AreEqual(1, status.State.Duel.Combat.Statuses.Count);
            var end = Do(session, CardDuelAction.EndTurn);
            var skip = end.Timeline.Single(x => x.Kind == BattleTimelineKind.ActionSkipped);
            Assert.AreEqual(CardDuelAction.EnemyAction, skip.Action);
            Assert.IsTrue(end.Timeline.Where(x => x.StepIndex == skip.StepIndex).All(x => x.IsActionSkipped));
            Assert.IsFalse(end.Timeline.Where(x => x.StepIndex != skip.StepIndex).Any(x => x.IsActionSkipped));
            Assert.IsFalse(end.Timeline.Any(x => x.RuleEvent is CombatEvent));
            var expiry = end.Timeline.Last(x => x.Kind == BattleTimelineKind.StatusChanged);
            Assert.IsEmpty(expiry.State.Duel.Combat.Statuses);
            Assert.Less(skip.Sequence, expiry.Sequence);
        }

        [TestCase(RelicBoundaryTiming.PhaseStarted)] [TestCase(RelicBoundaryTiming.PhaseEnding)]
        public void TerminalAtBoundaryDoesNotFabricateSubsequentPhases(RelicBoundaryTiming timing)
        {
            var gear = RelicBoundaryTestData.Gear(new[] { RelicBoundaryTestData.Boundary("kill", timing,
                RelicPhaseRelation.OpponentTeam, new[] { RelicTriggerTestData.Response(BattleEffectSpec.Eliminate(), CombatTargetSpec.Opponents()) }) });
            var f = new RelicTriggerTestData.Fixture(new[] { gear });
            var batch = Do(new BattleSession(f.Engine, f.State), CardDuelAction.EndTurn);
            Assert.AreEqual(1, batch.Timeline.Count(x => x.Kind == BattleTimelineKind.BattleEnded));
            Assert.IsFalse(batch.Timeline.Any(x => x.Kind == BattleTimelineKind.PhaseStarted && x.PhaseKey == "player"));
            if (timing == RelicBoundaryTiming.PhaseStarted)
            {
                Assert.IsFalse(batch.Timeline.Any(x => x.Action == CardDuelAction.EnemyAction));
                Assert.IsTrue(batch.Timeline.Any(x => x.Kind == BattleTimelineKind.PhaseClosed && x.PhaseKey == "enemy"));
            }
            else Assert.IsTrue(batch.Timeline.Any(x => x.Kind == BattleTimelineKind.PhaseEnded && x.PhaseKey == "enemy"));
        }

        [Test] public void RoundLimitAndPlayerKillExposeOneFinalSnapshotAfterActualMaintenanceAndCards()
        {
            var limit = Create(rounds: 1); Do(limit, CardDuelAction.Start);
            var end = Do(limit, CardDuelAction.EndTurn);
            Assert.AreEqual("RoundLimit", end.Timeline.Last().Reason);
            Assert.AreEqual(1, end.Timeline.Count(x => x.Kind == BattleTimelineKind.BattleEnded));
            Assert.IsFalse(end.Timeline.Any(x => x.Kind == BattleTimelineKind.PhaseStarted && x.PhaseKey == "player"));
            var lethal = Create(enemyHealth: 1); Do(lethal, CardDuelAction.Start);
            var kill = Do(lethal, CardDuelAction.Play, 1);
            Assert.IsTrue(kill.Timeline.Any(x => x.Kind == BattleTimelineKind.PhaseClosed && x.PhaseKey == "player"));
            Assert.IsFalse(kill.Timeline.Any(x => x.Kind == BattleTimelineKind.PhaseEnding));
            Assert.AreEqual(1, kill.Timeline.Last().State.Zone(CardZoneKinds.Played).Cards.Count);
        }

        private sealed class InvalidEnemy : IEnemyBattleEffectRule
        {
            public string Version => "invalid.1";
            public BattleEffectSequence Resolve(EnemyBattleEffectContext context) => null;
        }
        [Test] public void LaterAutomaticFailurePublishesNoFactsAndPreservesAuthority()
        {
            var session = Create(enemyRule: new InvalidEnemy()); Do(session, CardDuelAction.Start);
            var before = session.State;
            var result = session.Execute(before.Revision, Player, CardDuelAction.EndTurn);
            Assert.IsFalse(result.Accepted); Assert.AreSame(before, result.State); Assert.AreSame(before, session.State);
            Assert.IsEmpty(result.Timeline); Assert.IsEmpty(result.Steps);
            var rejected = session.Engine.Execute(before, before.Revision - 1, Player, CardDuelAction.Play);
            Assert.IsEmpty(rejected.Timeline);
        }

        [Test] public void TimelineAndPayloadCollectionsCannotBeMutatedByAFrontend()
        {
            var session = Create(); var batch = Do(session, CardDuelAction.Start);
            Assert.Throws<NotSupportedException>(() => ((IList<BattleTimelineEntry>)batch.Timeline).Clear());
            var dealt = batch.Timeline.First(x => x.Kind == BattleTimelineKind.CardsDealt);
            Assert.Throws<NotSupportedException>(() => ((IList<CardInstanceId>)dealt.Cards).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<CardZone>)dealt.State.Table.Zones).Clear());
        }
    }
}
