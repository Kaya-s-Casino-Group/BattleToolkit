using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CardDuelTests
    {
        private static CardDuelEngine Engine(int enemyHealth = 10000, long attack = 1, int rounds = 8, int discards = 3, long divisor = 5, int energy = 1) =>
            new CardDuelEngine(new ScoredDuelDefinition("test.1", new CombatantDefinition("hero", 40), new CombatantDefinition("enemy", enemyHealth),
                new ScoreDamagePolicy(divisor), attack, rounds, maxEnergy: energy), 8, discards);
        private static CardDuelState Create(CardDuelEngine engine)
        {
            var state = engine.Create("test", HmacDeckShuffler.FromSeed(new byte[32]));
            return engine.Execute(state, state.Revision, CardDuelAction.Start).State;
        }
        private static void Conserved(CardDuelState state)
        {
            state.Table.Validate();
            Assert.That(state.Table.Zones.SelectMany(zone => zone.Cards).Distinct().Count(), Is.EqualTo(state.Table.Cards.Count));
        }
        [Test] public void CreationHasNoHandAndFirstTurnDealsExactlyOnceWithoutSpendingEnergy()
        {
            var engine = Engine(energy: 3); var initial = engine.Create("new", HmacDeckShuffler.FromSeed(new byte[32]));
            Assert.That(initial.HasStarted, Is.False); Assert.That(initial.Revision, Is.Zero);
            Assert.That(initial.Zone(CardZoneKinds.Hand).Cards, Is.Empty); Assert.That(initial.Zone(CardZoneKinds.Deck).Cards.Count, Is.EqualTo(52));
            Assert.That(initial.Zone(CardZoneKinds.Played).Cards, Is.Empty); Assert.That(initial.Zone(CardZoneKinds.Discard).Cards, Is.Empty);
            foreach (CardDuelAction action in Enum.GetValues(typeof(CardDuelAction)))
                if (action != CardDuelAction.Start) Assert.That(engine.Execute(initial, initial.Revision, action).Error, Is.EqualTo(CardDuelError.InvalidPhase));
            var invalid = engine.Execute(initial, initial.Revision, CardDuelAction.Start, initial.Zone(CardZoneKinds.Deck).Cards.Take(1));
            Assert.That(invalid.Error, Is.EqualTo(CardDuelError.InvalidSelection)); Assert.That(invalid.State, Is.SameAs(initial));
            var opening = engine.Execute(initial, initial.Revision, CardDuelAction.Start);
            Assert.That(opening.Accepted, Is.True); Assert.That(opening.State.HasStarted, Is.True); Assert.That(opening.State.Revision, Is.EqualTo(1));
            CollectionAssert.AreEqual(initial.Zone(CardZoneKinds.Deck).Cards.Reverse().Take(8), opening.State.Zone(CardZoneKinds.Hand).Cards);
            Assert.That(opening.State.Zone(CardZoneKinds.Deck).Cards.Count, Is.EqualTo(44));
            Assert.That(opening.State.Duel, Is.SameAs(initial.Duel)); Assert.That(opening.State.Random, Is.SameAs(initial.Random));
            Assert.That(opening.State.DiscardsRemaining, Is.EqualTo(initial.DiscardsRemaining)); Assert.That(opening.Events, Is.Empty); Assert.That(opening.Score, Is.Null);
            Assert.That(engine.Execute(opening.State, initial.Revision, CardDuelAction.Start).Error, Is.EqualTo(CardDuelError.StaleRevision));
            var retry = engine.Execute(opening.State, opening.State.Revision, CardDuelAction.Start);
            Assert.That(retry.Error, Is.EqualTo(CardDuelError.InvalidPhase)); Assert.That(retry.State, Is.SameAs(opening.State)); Conserved(opening.State);
        }
        [Test] public void StartsWithIndependent52CardsAndFourZones()
        {
            var state = Create(Engine());
            Assert.That(state.Table.Cards.Count, Is.EqualTo(52)); Assert.That(state.Table.Cards.Select(card => card.DefinitionId).Distinct().Count(), Is.EqualTo(52));
            Assert.That(state.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(8)); Assert.That(state.Zone(CardZoneKinds.Deck).Cards.Count, Is.EqualTo(44));
            Assert.That(state.Zone(CardZoneKinds.Played).Cards, Is.Empty); Assert.That(state.Zone(CardZoneKinds.Discard).Cards, Is.Empty);
            Assert.That(state.Duel.CompletedRounds, Is.Zero); Conserved(state);
        }
        [Test] public void LegalCardsProduceRealScoreAndStayPlayedUntilEnemyFinishes()
        {
            var engine = Engine(); var before = Create(engine); var ids = before.Zone(CardZoneKinds.Hand).Cards.Take(2).ToArray();
            var hit = engine.Execute(before, before.Revision, CardDuelAction.Play, ids);
            Assert.That(hit.Accepted, Is.True); Assert.That(hit.Score.Total, Is.GreaterThan(0));
            Assert.That(hit.State.Duel.Combat.Get(hit.State.Duel.EnemyId).Health, Is.EqualTo(10000 - hit.Score.Total / 5));
            CollectionAssert.AreEqual(ids, hit.State.Zone(CardZoneKinds.Played).Cards);
            Assert.That(hit.State.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(6)); Assert.That(before.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(8));
            Assert.That(engine.Execute(hit.State, hit.State.Revision, CardDuelAction.Discard, new[] { hit.State.Zone(CardZoneKinds.Hand).Cards[0] }).Accepted, Is.False);
            var response = engine.Execute(hit.State, hit.State.Revision, CardDuelAction.EnemyAction);
            Assert.That(response.Accepted, Is.True); Assert.That(response.State.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(8));
            Assert.That(response.State.Zone(CardZoneKinds.Played).Cards, Is.Empty); CollectionAssert.AreEqual(ids, response.State.Zone(CardZoneKinds.Discard).Cards);
            Assert.That(response.State.Duel.CompletedRounds, Is.EqualTo(1)); Assert.That(response.State.Duel.Combat.Get(response.State.Duel.PlayerId).Health, Is.EqualTo(39));
            Assert.That(engine.Execute(response.State, response.State.Revision, CardDuelAction.EnemyAction).Accepted, Is.False); Conserved(response.State);
        }
        [Test] public void DiscardRefillsConsumesBudgetButNotRoundAndRejectsRetry()
        {
            var engine = Engine(discards: 1, energy: 3); var state = Create(engine);
            var result = engine.Execute(state, state.Revision, CardDuelAction.Discard, state.Zone(CardZoneKinds.Hand).Cards.Take(3));
            Assert.That(result.Accepted, Is.True); Assert.That(result.State.Duel.PlayerTurn.Energy, Is.EqualTo(2));
            Assert.That(result.State.Duel.Combat, Is.SameAs(state.Duel.Combat)); Assert.That(result.State.Duel.CompletedRounds, Is.Zero);
            Assert.That(result.State.DiscardsRemaining, Is.Zero); Assert.That(result.State.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(8));
            Assert.That(result.State.Zone(CardZoneKinds.Discard).Cards.Count, Is.EqualTo(3));
            Assert.That(engine.Execute(result.State, 0, CardDuelAction.Discard, state.Zone(CardZoneKinds.Hand).Cards.Take(1)).Error, Is.EqualTo(CardDuelError.StaleRevision));
            Assert.That(engine.Execute(result.State, result.State.Revision, CardDuelAction.Discard, result.State.Zone(CardZoneKinds.Hand).Cards.Take(1)).Error, Is.EqualTo(CardDuelError.NoDiscards));
            Conserved(result.State);
        }
        [Test] public void ForeignDuplicateEmptyAndOversizedSelectionsLeaveStateUntouched()
        {
            var engine = Engine(); var state = Create(engine); var id = state.Zone(CardZoneKinds.Hand).Cards[0];
            foreach (var selection in new[] { new CardInstanceId[0], new[] { id, id }, new[] { state.Zone(CardZoneKinds.Deck).Cards[0] }, state.Zone(CardZoneKinds.Hand).Cards.Take(6).ToArray() })
            {
                var result = engine.Execute(state, state.Revision, CardDuelAction.Play, selection);
                Assert.That(result.Error, Is.EqualTo(CardDuelError.InvalidSelection)); Assert.That(result.State, Is.SameAs(state));
                Assert.That(result.Events, Is.Empty); Assert.That(result.Score, Is.Null);
            }
        }
        [Test] public void KillStopsRetaliationAndFurtherCardCommands()
        {
            var engine = Engine(enemyHealth: 1, attack: 100); var state = Create(engine);
            var won = engine.Execute(state, state.Revision, CardDuelAction.Play, state.Zone(CardZoneKinds.Hand).Cards.Take(1)).State;
            Assert.That(won.Duel.Phase, Is.EqualTo(ScoredDuelPhase.Won)); Assert.That(won.Duel.Combat.Get(won.Duel.PlayerId).Health, Is.EqualTo(40));
            Assert.That(engine.Execute(won, won.Revision, CardDuelAction.EnemyAction).Accepted, Is.False);
            Assert.That(engine.Execute(won, won.Revision, CardDuelAction.Play, won.Zone(CardZoneKinds.Hand).Cards.Take(1)).Accepted, Is.False); Conserved(won);
        }
        [Test] public void DeathOrBudgetEndsWithoutRefill()
        {
            foreach (var engine in new[] { Engine(attack: 100), Engine(attack: 0, rounds: 1) })
            {
                var state = Create(engine); state = engine.Execute(state, state.Revision, CardDuelAction.Play, state.Zone(CardZoneKinds.Hand).Cards.Take(1)).State;
                state = engine.Execute(state, state.Revision, CardDuelAction.EnemyAction).State;
                Assert.That(state.Duel.Phase, Is.EqualTo(ScoredDuelPhase.Lost)); Assert.That(state.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(7));
                Assert.That(state.Zone(CardZoneKinds.Played).Cards, Is.Empty); Assert.That(engine.Duel.ReadProgress(state.Duel).Result, Is.Not.Null); Conserved(state);
            }
        }
        [Test] public void ExhaustedDeckRecyclesDiscardDeterministicallyWithoutDuplicatingCards()
        {
            var engine = Engine(attack: 0, rounds: 70, divisor: long.MaxValue); var initial = Create(engine);
            var state = initial; var replay = initial;
            for (int i = 0; i < 55; i++)
            {
                foreach (var action in new[] { CardDuelAction.Play, CardDuelAction.EnemyAction })
                {
                    var ids = action == CardDuelAction.Play ? state.Zone(CardZoneKinds.Hand).Cards.Take(1).ToArray() : null;
                    state = engine.Execute(state, state.Revision, action, ids).State; replay = engine.Execute(replay, replay.Revision, action, ids).State;
                }
                Conserved(state); Assert.That(state.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(8));
            }
            Assert.That(state.Duel.CompletedRounds, Is.EqualTo(55)); Assert.That(state.Zone(CardZoneKinds.Deck).Cards.Count, Is.GreaterThan(0));
            CollectionAssert.AreEqual(state.Random.StateBytes, replay.Random.StateBytes);
            CollectionAssert.AreEqual(state.Zone(CardZoneKinds.Hand).Cards, replay.Zone(CardZoneKinds.Hand).Cards);
            Assert.That(initial.Revision, Is.EqualTo(1));
        }
        [Test] public void DisabledRunEntriesKeepTheirAttributesAndRejectActionsWithoutSpendingResources()
        {
            var engine = Engine(energy: 3);
            var deck = StandardDeck.Cards().Select((card, i) => new DeckEntry("run/" + i, card.Id, (CardEffects)7, true)).ToArray();
            var initial = engine.Create("disabled", HmacDeckShuffler.FromSeed(new byte[32]), deck);
            Assert.That(initial.Table.Cards.All(card => card.IsDisabled && card.Effects == (CardEffects)7), Is.True);
            var state = engine.Execute(initial, initial.Revision, CardDuelAction.Start).State;
            foreach (var action in new[] { CardDuelAction.Play, CardDuelAction.Discard })
            {
                var result = engine.Execute(state, state.Revision, action, state.Zone(CardZoneKinds.Hand).Cards.Take(1));
                Assert.That(result.Error, Is.EqualTo(CardDuelError.InvalidSelection));
                Assert.That(result.State, Is.SameAs(state)); Assert.That(result.Events, Is.Empty);
            }
            Conserved(state);
        }
        [Test] public void ExplicitDeckKeepsRunOriginsAndCurrentSize()
        {
            var engine = Engine(); var deck = StandardDeck.Cards().Select((card, i) => new DeckEntry("run/" + i, card.Id)).ToList();
            deck.Add(new DeckEntry("run/bonus", deck[0].DefinitionId));
            var state = engine.Create("one", HmacDeckShuffler.FromSeed(new byte[32]), deck);
            Assert.That(state.Table.Cards.Count, Is.EqualTo(53)); Assert.That(state.Table.Cards.Any(card => card.OriginId == "run/bonus"), Is.True);
            deck.RemoveAt(0); deck.RemoveAt(0);
            var next = engine.Create("two", HmacDeckShuffler.FromSeed(new byte[32]), deck);
            Assert.That(next.Table.Cards.Count, Is.EqualTo(51)); Assert.That(next.Table.Cards.Select(card => card.Id).Intersect(state.Table.Cards.Select(card => card.Id)), Is.Empty);
        }
        [Test] public void PlayerCanPlaySeveralHandsAfterCollectingEachWithoutRefillOrRetaliation()
        {
            var engine = Engine(energy: 3); var state = Create(engine);
            for (int i = 0; i < 3; i++)
            {
                var hit = engine.Execute(state, state.Revision, CardDuelAction.Play, state.Zone(CardZoneKinds.Hand).Cards.Take(1));
                Assert.That(hit.Accepted, Is.True); state = hit.State;
                Assert.That(state.Duel.PlayerTurn.Energy, Is.EqualTo(2 - i)); Assert.That(state.Duel.CompletedRounds, Is.Zero);
                Assert.That(state.Duel.Combat.Get(state.Duel.PlayerId).Health, Is.EqualTo(40));
                Assert.That(state.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(7 - i));
                if (i < 2)
                {
                    Assert.That(engine.Execute(state, state.Revision, CardDuelAction.EndTurn).Accepted, Is.False);
                    Assert.That(engine.Execute(state, state.Revision, CardDuelAction.Play, state.Zone(CardZoneKinds.Hand).Cards.Take(1)).Accepted, Is.False);
                    state = engine.Execute(state, state.Revision, CardDuelAction.CollectPlayed).State;
                    Assert.That(state.Zone(CardZoneKinds.Played).Cards, Is.Empty);
                }
                Conserved(state);
            }
            Assert.That(state.Duel.Phase, Is.EqualTo(ScoredDuelPhase.AwaitingEnemyAction));
            state = engine.Execute(state, state.Revision, CardDuelAction.EnemyAction).State;
            Assert.That(state.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(8)); Assert.That(state.Duel.PlayerTurn.Energy, Is.EqualTo(3));
            Assert.That(state.Duel.CompletedRounds, Is.EqualTo(1)); Assert.That(state.Zone(CardZoneKinds.Discard).Cards.Count, Is.EqualTo(3)); Conserved(state);
        }
        [Test] public void DiscardSpendsEnergyAndManualEndNeedsNoSelectedCards()
        {
            var engine = Engine(energy: 3); var state = Create(engine);
            state = engine.Execute(state, state.Revision, CardDuelAction.Discard, state.Zone(CardZoneKinds.Hand).Cards.Take(2)).State;
            Assert.That(state.Duel.PlayerTurn.Energy, Is.EqualTo(2));
            var ended = engine.Execute(state, state.Revision, CardDuelAction.EndTurn);
            Assert.That(ended.Accepted, Is.True); Assert.That(ended.State.Duel.PlayerTurn.Energy, Is.Zero);
            Assert.That(ended.State.Duel.Scores, Is.Empty); Assert.That(ended.State.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(8));
            Assert.That(engine.Execute(ended.State, ended.State.Revision, CardDuelAction.EndTurn).Accepted, Is.False);
            var response = engine.Execute(ended.State, ended.State.Revision, CardDuelAction.EnemyAction).State;
            Assert.That(response.Duel.CompletedRounds, Is.EqualTo(1)); Assert.That(response.Duel.PlayerTurn.Energy, Is.EqualTo(3)); Conserved(response);
        }
        [Test] public void EmptyHandCanStillEndTurnAndRefillsOnlyAfterEnemyAction()
        {
            var engine = Engine(energy: 3); var state = Create(engine);
            foreach (int count in new[] { 5, 3 })
            {
                state = engine.Execute(state, state.Revision, CardDuelAction.Play, state.Zone(CardZoneKinds.Hand).Cards.Take(count)).State;
                state = engine.Execute(state, state.Revision, CardDuelAction.CollectPlayed).State;
            }
            Assert.That(state.Zone(CardZoneKinds.Hand).Cards, Is.Empty); Assert.That(state.Duel.PlayerTurn.Energy, Is.EqualTo(1));
            var ended = engine.Execute(state, state.Revision, CardDuelAction.EndTurn); Assert.That(ended.Accepted, Is.True);
            state = engine.Execute(ended.State, ended.State.Revision, CardDuelAction.EnemyAction).State;
            Assert.That(state.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(8)); Assert.That(state.Duel.PlayerTurn.Energy, Is.EqualTo(3)); Conserved(state);
        }
        [Test] public void PaidDiscardExhaustsEnergyAndAutomaticRefillIsFree()
        {
            var engine = Engine(energy: 1); var before = Create(engine);
            var discarded = engine.Execute(before, before.Revision, CardDuelAction.Discard, before.Zone(CardZoneKinds.Hand).Cards.Take(3));
            Assert.That(discarded.Accepted, Is.True); Assert.That(discarded.State.Duel.PlayerTurn.Energy, Is.Zero);
            Assert.That(discarded.State.Duel.Phase, Is.EqualTo(ScoredDuelPhase.AwaitingEnemyAction));
            Assert.That(discarded.State.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(8));
            Assert.That(discarded.State.DiscardsRemaining, Is.EqualTo(2)); Assert.That(discarded.Events, Is.Empty);
            var response = engine.Execute(discarded.State, discarded.State.Revision, CardDuelAction.EnemyAction);
            Assert.That(response.Accepted, Is.True); Assert.That(response.State.Duel.PlayerTurn.Energy, Is.EqualTo(1));
            Assert.That(response.State.Duel.CompletedRounds, Is.EqualTo(1)); Conserved(response.State);
        }
        [Test] public void RejectedPaidDiscardDoesNotMoveCardsSpendBudgetOrConsumeRandomness()
        {
            var engine = new CardDuelEngine(new ScoredDuelDefinition("discard-cost", new CombatantDefinition("hero", 40),
                new CombatantDefinition("enemy", 1000), new ScoreDamagePolicy(5), 0, 3, initialEnergy: 1, discardEnergyCost: 2));
            var before = Create(engine);
            var result = engine.Execute(before, before.Revision, CardDuelAction.Discard, before.Zone(CardZoneKinds.Hand).Cards.Take(2));
            Assert.That(result.Error, Is.EqualTo(CardDuelError.InsufficientEnergy)); Assert.That(result.State, Is.SameAs(before));
            Assert.That(result.State.DiscardsRemaining, Is.EqualTo(3)); Assert.That(result.State.Random, Is.SameAs(before.Random)); Conserved(before);
        }
        [Test] public void ImmediatePolicyRefillsAfterEachActionWithoutReusingPlayedCards()
        {
            var engine = new CardDuelEngine(Engine(energy: 3).Duel.Definition,
                dealPolicy: new CardDuelDealPolicy(CardDuelRefillTiming.AfterAction, CardDuelRefillTiming.AfterAction));
            var state = Create(engine); var played = state.Zone(CardZoneKinds.Hand).Cards.Take(3).ToArray();
            state = engine.Execute(state, state.Revision, CardDuelAction.Play, played).State;
            Assert.That(state.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(8)); Assert.That(state.Zone(CardZoneKinds.Deck).Cards.Count, Is.EqualTo(41));
            Assert.That(state.Zone(CardZoneKinds.Hand).Cards.Intersect(played), Is.Empty); CollectionAssert.AreEqual(played, state.Zone(CardZoneKinds.Played).Cards);
            Assert.That(state.Duel.PlayerTurn.Energy, Is.EqualTo(2)); Conserved(state);
            state = engine.Execute(state, state.Revision, CardDuelAction.CollectPlayed).State;
            Assert.That(state.Zone(CardZoneKinds.Deck).Cards.Count, Is.EqualTo(41));
            state = engine.Execute(state, state.Revision, CardDuelAction.Discard, state.Zone(CardZoneKinds.Hand).Cards.Take(2)).State;
            Assert.That(state.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(8)); Assert.That(state.Zone(CardZoneKinds.Deck).Cards.Count, Is.EqualTo(39));
            Assert.That(state.Duel.PlayerTurn.Energy, Is.EqualTo(1)); Assert.That(state.Duel.CompletedRounds, Is.Zero); Conserved(state);
        }
        [Test] public void DeferredPolicyRefillsOnlyWhenTheNextPlayerTurnBegins()
        {
            var engine = new CardDuelEngine(Engine(energy: 3).Duel.Definition,
                dealPolicy: new CardDuelDealPolicy(CardDuelRefillTiming.NextPlayerTurn, CardDuelRefillTiming.NextPlayerTurn));
            var state = Create(engine);
            state = engine.Execute(state, state.Revision, CardDuelAction.Play, state.Zone(CardZoneKinds.Hand).Cards.Take(1)).State;
            state = engine.Execute(state, state.Revision, CardDuelAction.CollectPlayed).State;
            state = engine.Execute(state, state.Revision, CardDuelAction.Discard, state.Zone(CardZoneKinds.Hand).Cards.Take(2)).State;
            Assert.That(state.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(5)); Assert.That(state.Zone(CardZoneKinds.Deck).Cards.Count, Is.EqualTo(44));
            Assert.That(state.Duel.PlayerTurn.Energy, Is.EqualTo(1)); Assert.That(state.DiscardsRemaining, Is.EqualTo(2));
            state = engine.Execute(state, state.Revision, CardDuelAction.EndTurn).State;
            Assert.That(state.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(5));
            state = engine.Execute(state, state.Revision, CardDuelAction.EnemyAction).State;
            Assert.That(state.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(8)); Assert.That(state.Zone(CardZoneKinds.Deck).Cards.Count, Is.EqualTo(41));
            Assert.That(state.Duel.CompletedRounds, Is.EqualTo(1)); Assert.That(state.Duel.PlayerTurn.Energy, Is.EqualTo(3)); Conserved(state);
        }
        private sealed class FixedDrawPolicy : ICardDuelDealPolicy
        {
            public int GetDrawCount(CardDuelDealContext context) => context.Trigger == CardDuelDealTrigger.OpeningTurn ||
                context.Trigger == CardDuelDealTrigger.PlayerTurnStart ? Math.Min(2, context.MissingCards) : 0;
        }
        [Test] public void CustomPolicyCanDrawAFixedCountThroughTheSameSafeTransferInterface()
        {
            var engine = new CardDuelEngine(Engine().Duel.Definition, handSize: 3, dealPolicy: new FixedDrawPolicy());
            var state = Create(engine); Assert.That(state.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(2));
            state = engine.Execute(state, state.Revision, CardDuelAction.EndTurn).State;
            state = engine.Execute(state, state.Revision, CardDuelAction.EnemyAction).State;
            Assert.That(state.Zone(CardZoneKinds.Hand).Cards.Count, Is.EqualTo(3));
            Assert.That(state.Zone(CardZoneKinds.Deck).Cards.Count, Is.EqualTo(49)); Conserved(state);
        }
    }
}
