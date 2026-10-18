using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class HoldemMatchEngineTests
    {
        private sealed class Game
        {
            public readonly ContentCatalog Catalog = StandardHoldem.Catalog();
            public readonly HoldemMatchEngine Engine;
            public MatchState State;
            private int serial;
            public Game(long[] stacks = null, long smallBlind = 10, long bigBlind = 20, IDeckShuffler shuffler = null, IEnumerable<DeckEntry> deckEntries = null)
            {
                stacks = stacks ?? new long[] { 1000, 1000 };
                Engine = new HoldemMatchEngine(Catalog, shuffler,
                    abilityRuntime: deckEntries == null ? null : new AbilityRuntime(new AbilityBehavior[0]), deckEntries: deckEntries);
                var seats = Enumerable.Range(0, stacks.Length).Select(i => new SeatId("s" + i)).ToArray();
                var definition = deckEntries == null ? StandardHoldem.Definition(stacks.Length, smallBlind, bigBlind) :
                    AbilityHoldem.Definition(Catalog.Version, stacks.Length, smallBlind, bigBlind);
                State = Engine.Create(new MatchId("test"), definition, new TableState(seats),
                    stacks.Select((stack, i) => new ParticipantState(P(i), seats[i], ParticipantStatus.Active, new ResourceState(new Chips(stack)))),
                    HmacDeckShuffler.FromSeed(Enumerable.Range(0, 32).Select(i => (byte)i)));
            }
            public string CommandId() => "cmd-" + serial++;
            public void Accept(TransitionResult result)
            {
                Assert.That(result.Accepted, Is.True, result.CommandId + ": " + result.Error);
                Assert.That(result.State.Revision, Is.EqualTo(State.Revision + 1));
                State = result.State; StateValidator.Validate(State, Catalog);
            }
            public void Start()
            { Accept(Engine.ExecuteSystem(State, new StartHandCommand(CommandId(), State.Revision))); Drain(); }
            public void Drain(bool finishSettled = false)
            {
                int steps = 0;
                while (State.AbilityWindow != null && State.AbilityWindow.Timing != HoldemTiming.BeforeBettingAction)
                {
                    if (!finishSettled && State.AbilityWindow.Timing == HoldemTiming.AfterHandSettled) break;
                    Assert.That(++steps, Is.LessThan(20));
                    Accept(Engine.ExecuteSystem(State, new AdvanceWindowCommand(CommandId(), State.Revision, State.AbilityWindow.Id)));
                }
            }
            public TransitionResult Act(BettingAction action, long to = 0, bool drain = true)
            {
                var result = Engine.Execute(State, new BettingCommand(CommandId(), State.Revision, State.ActiveDecision.Id, action, to), State.ActiveDecision.ActorId);
                Accept(result); if (drain) Drain(); return result;
            }
            public void CheckCall()
            { var legal = Engine.GetLegalActions(State, State.ActiveDecision.ActorId); Act(legal.CanCheck ? BettingAction.Check : BettingAction.Call); }
            public void ToStreet(Street street)
            { int n = 0; while (State.CurrentHand.Stage == HandStage.Betting && State.CurrentHand.Betting.Street != street) { Assert.That(++n, Is.LessThan(100)); CheckCall(); } }
            public void Checkdown()
            { int n = 0; while (State.CurrentHand.Stage != HandStage.Settled) { Assert.That(++n, Is.LessThan(100)); CheckCall(); } }
        }
        [Test] public void DealAndSnapshotPreserveCardPropertiesAfterShuffledIdentityAssignment()
        {
            var entries = StandardHoldem.Cards().Select((c, i) => new DeckEntry("origin-" + i, c.Id,
                i % 2 == 0 ? CardEffects.Enhanced | CardEffects.Laminated : CardEffects.Ineffective, i % 3 == 0)).ToArray();
            var g = new Game(deckEntries: entries); g.Start();
            var json = MatchSnapshotCodec.Serialize(g.State, g.Catalog);
            var restored = MatchSnapshotCodec.Deserialize(json, g.Catalog, g.State.Definition);
            foreach (var card in restored.CurrentHand.Cards)
            {
                var entry = entries.Single(e => e.Id == card.OriginId);
                Assert.AreEqual(entry.DefinitionId, card.DefinitionId); Assert.AreEqual(entry.Effects, card.Effects);
                Assert.AreEqual(entry.IsDisabled, card.IsDisabled);
            }
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json.Replace("\"Effects\":3", "\"Effects\":8"), g.Catalog, g.State.Definition));
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json.Replace("\"SchemaVersion\":5", "\"SchemaVersion\":4"), g.Catalog, g.State.Definition));
        }

        private static ParticipantId P(int i) => new ParticipantId("p" + i);
        private static CardDefinitionId C(int rank, Suit suit = Suit.Clubs) => StandardHoldem.CardId(suit, (Rank)rank);
        private static long Stack(Game g, int i) => g.State.Participants.Single(p => p.Id == P(i)).Resources.Chips.Value;
        private static long Total(MatchState state) => state.Participants.Sum(p => p.Resources.Chips.Value) +
            (state.CurrentHand != null && state.CurrentHand.Stage != HandStage.Settled ? state.CurrentHand.Participants.Sum(p => p.HandCommitted.Value) : 0);

        private sealed class FixedDeck : IDeckShuffler
        {
            private readonly CardDefinitionId[] firstDraws;
            public FixedDeck(params CardDefinitionId[] firstDraws) { this.firstDraws = firstDraws; }
            public ShuffleResult Shuffle(IReadOnlyList<CardInstanceId> cards, RandomState random)
            {
                var definitions = StandardHoldem.Cards().Select(c => c.Id).ToList();
                var desired = firstDraws.Select(id => cards[definitions.IndexOf(id)]).ToArray();
                return new ShuffleResult(cards.Except(desired).Concat(desired.Reverse()), random);
            }
        }
        private sealed class CorruptDeck : IDeckShuffler
        { public ShuffleResult Shuffle(IReadOnlyList<CardInstanceId> cards, RandomState random) => new ShuffleResult(Enumerable.Repeat(cards[0], 52), random); }

        [Test] public void HeadsUpBlindsAndActionOrderAreCorrect()
        {
            var g = new Game(); g.Start();
            Assert.That(g.State.CurrentHand.SmallBlindId, Is.EqualTo(P(0))); Assert.That(g.State.CurrentHand.BigBlindId, Is.EqualTo(P(1)));
            Assert.That(g.State.ActiveDecision.ActorId, Is.EqualTo(P(0))); Assert.That(Stack(g, 0), Is.EqualTo(990));
            g.Act(BettingAction.Call);
            Assert.That(g.State.CurrentHand.Betting.Street, Is.EqualTo(Street.Preflop)); Assert.That(g.State.ActiveDecision.ActorId, Is.EqualTo(P(1)));
            g.Act(BettingAction.Check);
            Assert.That(g.State.CurrentHand.Betting.Street, Is.EqualTo(Street.Flop)); Assert.That(g.State.ActiveDecision.ActorId, Is.EqualTo(P(1)));
        }

        [Test] public void MultiwayBlindAndPostflopOrderFollowSeats()
        {
            var g = new Game(new long[] { 1000, 1000, 1000, 1000 }); g.Start();
            Assert.That(g.State.CurrentHand.SmallBlindId, Is.EqualTo(P(1))); Assert.That(g.State.CurrentHand.BigBlindId, Is.EqualTo(P(2)));
            Assert.That(g.State.ActiveDecision.ActorId, Is.EqualTo(P(3))); g.ToStreet(Street.Flop);
            Assert.That(g.State.ActiveDecision.ActorId, Is.EqualTo(P(1)));
        }

        [Test] public void AHandReachesSettlementWithFiveBoardAndThreeBurnCards()
        {
            var g = new Game(); g.Start(); g.Checkdown();
            Assert.That(g.State.CurrentHand.Zones.Single(z => z.Kind == HoldemZoneKinds.Board).Cards.Count, Is.EqualTo(5));
            Assert.That(g.State.CurrentHand.Zones.Single(z => z.Kind == HoldemZoneKinds.Burn).Cards.Count, Is.EqualTo(3));
            Assert.That(g.State.CurrentHand.Zones.SelectMany(z => z.Cards).Distinct().Count(), Is.EqualTo(52));
            Assert.That(g.State.CurrentHand.Settlement.Evaluations.Count, Is.EqualTo(2)); Assert.That(Total(g.State), Is.EqualTo(2000));
        }

        [Test] public void SharedZoneTransfersPreserveRoundRobinDealingAndBurnOrder()
        {
            var sequence = Enumerable.Range(2, 12).Select(rank => C(rank)).ToArray();
            var g = new Game(shuffler: new FixedDeck(sequence)); g.Start();
            var preflop = g.State;
            Func<string, ParticipantId?, CardDefinitionId[]> definitions = (kind, owner) =>
                g.State.CurrentHand.Zones.Single(z => z.Kind == kind && z.ControllerId == owner).Cards
                    .Select(id => g.State.CurrentHand.Cards.Single(c => c.Id == id).DefinitionId).ToArray();
            CollectionAssert.AreEqual(new[] { C(2), C(4) }, definitions(HoldemZoneKinds.HoleCards, P(1)));
            CollectionAssert.AreEqual(new[] { C(3), C(5) }, definitions(HoldemZoneKinds.HoleCards, P(0)));
            g.Checkdown();
            CollectionAssert.AreEqual(new[] { C(6), C(10), C(12) }, definitions(HoldemZoneKinds.Burn, null));
            CollectionAssert.AreEqual(new[] { C(7), C(8), C(9), C(11), C(13) }, definitions(HoldemZoneKinds.Board, null));
            Assert.AreEqual(48, preflop.CurrentHand.Zones.Single(z => z.Kind == CardZoneKinds.Deck).Cards.Count);
            Assert.AreEqual(0, preflop.CurrentHand.Zones.Single(z => z.Kind == HoldemZoneKinds.Board).Cards.Count);
        }

        [Test] public void RejectedCommandsKeepIdenticalStateAndRandomness()
        {
            var g = new Game(); g.Start(); var before = g.State; string json = MatchSnapshotCodec.Serialize(before, g.Catalog);
            var result = g.Engine.Execute(before, new BettingCommand("bad", before.Revision, before.ActiveDecision.Id, BettingAction.Check), P(0));
            Assert.That(result.Error, Is.EqualTo(CommandError.CannotCheck)); Assert.That(result.State, Is.SameAs(before)); Assert.That(result.Events.Count, Is.Zero);
            Assert.That(MatchSnapshotCodec.Serialize(result.State, g.Catalog), Is.EqualTo(json));
            result = g.Engine.Execute(before, new BettingCommand("bad", before.Revision, before.ActiveDecision.Id, BettingAction.RaiseTo, 30), P(0));
            Assert.That(result.Error, Is.EqualTo(CommandError.RaiseTooSmall)); Assert.That(result.State, Is.SameAs(before));
        }

        [Test] public void StaleRevisionWrongActorAndStaleDecisionAreRejected()
        {
            var g = new Game(); g.Start(); var s = g.State;
            Assert.That(g.Engine.Execute(s, new BettingCommand("stale", s.Revision - 1, s.ActiveDecision.Id, BettingAction.Call), P(0)).Error, Is.EqualTo(CommandError.StaleRevision));
            Assert.That(g.Engine.Execute(s, new BettingCommand("actor", s.Revision, s.ActiveDecision.Id, BettingAction.Call), P(1)).Error, Is.EqualTo(CommandError.WrongActor));
            Assert.That(g.Engine.Execute(s, new BettingCommand("decision", s.Revision, new DecisionId("old"), BettingAction.Call), P(0)).Error, Is.EqualTo(CommandError.StaleDecision));
            Assert.That(g.Engine.GetLegalActions(s, P(1)).CanFold, Is.False);
        }

        [Test] public void SameCommandCannotPayTwiceWhenRetried()
        {
            var g = new Game(); g.Start(); var command = new BettingCommand("retry", g.State.Revision, g.State.ActiveDecision.Id, BettingAction.Call);
            g.Accept(g.Engine.Execute(g.State, command, P(0))); var state = g.State;
            var retry = g.Engine.Execute(state, command, P(0));
            Assert.That(retry.Error, Is.EqualTo(CommandError.StaleRevision)); Assert.That(retry.State, Is.SameAs(state)); Assert.That(Stack(g, 0), Is.EqualTo(980));
        }

        [Test] public void FoldingSettlesWithoutRevealingHoleCards()
        {
            var g = new Game(); g.Start(); g.Act(BettingAction.Fold, drain: false);
            var result = g.Engine.ExecuteSystem(g.State, new AdvanceWindowCommand("settle", g.State.Revision, g.State.AbilityWindow.Id)); g.Accept(result);
            Assert.That(g.State.CurrentHand.Stage, Is.EqualTo(HandStage.Settled)); Assert.That(Stack(g, 0), Is.EqualTo(990)); Assert.That(Stack(g, 1), Is.EqualTo(1010));
            Assert.That(result.Events.Any(e => e.Kind == MatchEventKind.CardsRevealed), Is.False);
            Assert.That(g.State.CurrentHand.Settlement.Refunds.Single().Amount.Value, Is.EqualTo(10));
        }

        [Test] public void PrivateDealEventsAreFilteredPerParticipant()
        {
            var g = new Game(); g.Accept(g.Engine.ExecuteSystem(g.State, new StartHandCommand("start", 0)));
            var result = g.Engine.ExecuteSystem(g.State, new AdvanceWindowCommand("deal", g.State.Revision, g.State.AbilityWindow.Id)); g.Accept(result);
            Assert.That(result.Events.Where(e => e.Kind == MatchEventKind.CardsDealt).Sum(e => e.Cards.Count), Is.EqualTo(4));
            foreach (var viewer in new[] { P(0), P(1) })
            {
                var dealt = result.EventsFor(viewer).Where(e => e.Kind == MatchEventKind.CardsDealt).ToArray();
                Assert.That(dealt.Single().Recipient, Is.EqualTo(viewer)); Assert.That(dealt.Single().Cards.Count, Is.EqualTo(2));
            }
            Assert.Throws<ArgumentException>(() => result.EventsFor(P(99)));
            Assert.That(result.Events.Select(e => e.Index), Is.EqualTo(Enumerable.Range(0, result.Events.Count)));
        }

        [Test] public void FullRaiseUpdatesMinimumAndPreservesBigBlindOption()
        {
            var g = new Game(new long[] { 1000, 1000, 1000 }); g.Start(); g.Act(BettingAction.RaiseTo, 60);
            var legal = g.Engine.GetLegalActions(g.State, g.State.ActiveDecision.ActorId);
            Assert.That(legal.MinimumRaiseTo.Value.Value, Is.EqualTo(100)); Assert.That(legal.CallCost.Value, Is.EqualTo(50));
        }

        [Test] public void ShortAllInDoesNotReopenAPreviousCall()
        {
            var g = new Game(new long[] { 1000, 30, 1000 }); g.Start(); g.Act(BettingAction.Call); // p0 called 20
            g.Act(BettingAction.RaiseTo, 30); // p1 all-in, only +10
            Assert.That(g.Engine.GetLegalActions(g.State, P(2)).CanRaise, Is.True); g.Act(BettingAction.Call);
            Assert.That(g.State.ActiveDecision.ActorId, Is.EqualTo(P(0)));
            Assert.That(g.Engine.GetLegalActions(g.State, P(0)).CanRaise, Is.False);
            Assert.That(g.Engine.Execute(g.State, new BettingCommand("bad-raise", g.State.Revision, g.State.ActiveDecision.Id, BettingAction.RaiseTo, 50), P(0)).Error,
                Is.EqualTo(CommandError.CannotRaise));
            g.Act(BettingAction.Call); g.Checkdown(); Assert.That(Total(g.State), Is.EqualTo(2030));
        }

        [Test] public void CumulativeShortAllInsReopenWithoutChangingFullRaiseSize()
        {
            var g = new Game(new long[] { 30, 40, 1000, 1000 }); g.Start();
            Assert.That(g.State.ActiveDecision.ActorId, Is.EqualTo(P(3))); g.Act(BettingAction.Call);
            g.Act(BettingAction.RaiseTo, 30); g.Act(BettingAction.RaiseTo, 40); g.Act(BettingAction.Call);
            Assert.That(g.State.ActiveDecision.ActorId, Is.EqualTo(P(3)));
            var legal = g.Engine.GetLegalActions(g.State, P(3)); Assert.That(legal.CanRaise, Is.True); Assert.That(legal.MinimumRaiseTo.Value.Value, Is.EqualTo(60));
            g.Act(BettingAction.RaiseTo, 60); g.Checkdown(); Assert.That(Total(g.State), Is.EqualTo(2070));
        }

        [Test] public void ShortOpeningAllInUsesTheMinimumBetAsRaiseIncrement()
        {
            var g = new Game(new long[] { 1000, 25, 1000 }); g.Start(); g.ToStreet(Street.Flop);
            Assert.That(g.State.ActiveDecision.ActorId, Is.EqualTo(P(1))); g.Act(BettingAction.RaiseTo, 5);
            Assert.That(g.Engine.GetLegalActions(g.State, P(2)).MinimumRaiseTo.Value.Value, Is.EqualTo(25));
        }

        [Test] public void NoSideBetIsOfferedAgainstOnlyAllInOpponents()
        {
            var g = new Game(new long[] { 1000, 40 }); g.Start(); g.Act(BettingAction.Call); g.Act(BettingAction.RaiseTo, 40);
            Assert.That(g.Engine.GetLegalActions(g.State, P(0)).CanRaise, Is.False); g.Act(BettingAction.Call);
            Assert.That(g.State.CurrentHand.Stage, Is.EqualTo(HandStage.Settled)); Assert.That(Total(g.State), Is.EqualTo(1040));
        }

        [Test] public void ShortBlindsRunOutWithoutPhantomCalls()
        {
            foreach (var stacks in new[] { new long[] { 7, 1000 }, new long[] { 1000, 7 }, new long[] { 7, 8 } })
            {
                var g = new Game(stacks); g.Start(); Assert.That(g.State.CurrentHand.Stage, Is.EqualTo(HandStage.Settled));
                Assert.That(Total(g.State), Is.EqualTo(stacks.Sum()));
            }
        }

        [Test] public void MultiwayShortBigBlindKeepsNominalBringInForFundedPlayers()
        {
            var g = new Game(new long[] { 1000, 1000, 7 }); g.Start();
            Assert.That(g.Engine.GetLegalActions(g.State, P(0)).CallCost.Value, Is.EqualTo(20));
            g.Act(BettingAction.Call); Assert.That(g.Engine.GetLegalActions(g.State, P(1)).CallCost.Value, Is.EqualTo(10));
            g.Checkdown(); Assert.That(Total(g.State), Is.EqualTo(2007));
        }

        [Test] public void ALastFundedPlayerOnlyMatchesActualAllInContributions()
        {
            var g = new Game(new long[] { 1000, 10, 7 }); g.Start();
            Assert.That(g.State.ActiveDecision.ActorId, Is.EqualTo(P(0)));
            Assert.That(g.Engine.GetLegalActions(g.State, P(0)).CallCost.Value, Is.EqualTo(10));
            g.Act(BettingAction.Call); Assert.That(g.State.CurrentHand.Stage, Is.EqualTo(HandStage.Settled)); Assert.That(Total(g.State), Is.EqualTo(1017));
        }

        [Test] public void AllInsCreateSidePotsAndReturnUncalledExcess()
        {
            var g = new Game(new long[] { 100, 300, 500 }); g.Start();
            g.Act(BettingAction.RaiseTo, 100); g.Act(BettingAction.RaiseTo, 300); g.Act(BettingAction.Call);
            Assert.That(g.State.CurrentHand.Stage, Is.EqualTo(HandStage.Settled));
            var pots = PotCalculator.Calculate(g.State.CurrentHand.Participants);
            Assert.That(pots.Pots.Select(p => p.Amount.Value), Is.EqualTo(new long[] { 300, 400 })); Assert.That(Total(g.State), Is.EqualTo(900));
            var h = new Game(new long[] { 100, 500 }); h.Start(); h.Act(BettingAction.RaiseTo, 100); h.Act(BettingAction.Call);
            Assert.That(h.State.CurrentHand.Participants.Select(p => p.HandCommitted.Value), Is.EqualTo(new long[] { 100, 100 }));
        }

        [Test] public void OverbetIsRefundedWhenOpponentCanOnlyCallShort()
        {
            var g = new Game(new long[] { 500, 100 }); g.Start(); g.Act(BettingAction.RaiseTo, 500); g.Act(BettingAction.Call);
            Assert.That(g.State.CurrentHand.Settlement.Refunds.Single().Amount.Value, Is.EqualTo(400)); Assert.That(Total(g.State), Is.EqualTo(600));
        }

        [Test] public void OddChipOnATieGoesClockwiseAfterDealer()
        {
            var deck = new FixedDeck(C(2), C(3), C(4), C(5), C(6), C(7), C(8),
                C(10, Suit.Hearts), C(11, Suit.Hearts), C(12, Suit.Hearts), C(9), C(13, Suit.Hearts), C(2, Suit.Spades), C(14, Suit.Hearts));
            var g = new Game(new long[] { 100, 100, 100 }, 1, 2, deck); g.Start(); g.Act(BettingAction.RaiseTo, 5); g.Act(BettingAction.Call); g.Act(BettingAction.Call);
            Assert.That(g.State.ActiveDecision.ActorId, Is.EqualTo(P(1))); g.Act(BettingAction.Fold); g.Checkdown();
            Assert.That(g.State.CurrentHand.Settlement.Awards.Single(a => a.ParticipantId == P(2)).Amount.Value, Is.EqualTo(8));
            Assert.That(g.State.CurrentHand.Settlement.Awards.Single(a => a.ParticipantId == P(0)).Amount.Value, Is.EqualTo(7));
        }

        [Test] public void TimeoutsCheckWhenFreeAndFoldWhenFacingABet()
        {
            var g = new Game(); g.Start(); g.Accept(g.Engine.ExecuteSystem(g.State, new DecisionTimeoutCommand("timeout", g.State.Revision, g.State.ActiveDecision.Id))); g.Drain();
            Assert.That(g.State.CurrentHand.Stage, Is.EqualTo(HandStage.Settled));
            var h = new Game(); h.Start(); h.Act(BettingAction.Call);
            h.Accept(h.Engine.ExecuteSystem(h.State, new DecisionTimeoutCommand("timeout", h.State.Revision, h.State.ActiveDecision.Id))); h.Drain();
            Assert.That(h.State.CurrentHand.Betting.Street, Is.EqualTo(Street.Flop));
        }

        [Test] public void ButtonRotatesBetweenHands()
        {
            var g = new Game(); g.Start(); g.Act(BettingAction.Fold); g.Drain(true); g.Start();
            Assert.That(g.State.CurrentHand.DealerSeatId, Is.EqualTo(new SeatId("s1"))); Assert.That(g.State.CurrentHand.SmallBlindId, Is.EqualTo(P(1)));
        }

        [Test] public void EliminatedSeatsAreSkippedWhenTheTableBecomesHeadsUp()
        {
            var deck = new FixedDeck(C(13), C(12), C(14), C(13, Suit.Diamonds), C(12, Suit.Diamonds), C(14, Suit.Diamonds),
                C(4), C(2), C(3, Suit.Diamonds), C(7, Suit.Hearts), C(5), C(9, Suit.Spades), C(6), C(11));
            var g = new Game(new long[] { 100, 20, 100 }, shuffler: deck); g.Start(); g.Checkdown(); g.Drain(true);
            Assert.That(Stack(g, 0), Is.EqualTo(140)); Assert.That(Stack(g, 1), Is.Zero); Assert.That(Stack(g, 2), Is.EqualTo(80));
            Assert.That(g.State.Participants.Single(p => p.Id == P(1)).Status, Is.EqualTo(ParticipantStatus.Eliminated));
            Assert.That(g.State.Status, Is.EqualTo(MatchStatus.Running));
            g.Start();
            Assert.That(g.State.CurrentHand.DealerSeatId, Is.EqualTo(new SeatId("s2")));
            Assert.That(g.State.CurrentHand.SmallBlindId, Is.EqualTo(P(2))); Assert.That(g.State.CurrentHand.BigBlindId, Is.EqualTo(P(0)));
        }

        [Test] public void LastOpponentEliminatedCompletesMatchAndCannotSettleTwice()
        {
            var deck = new FixedDeck(C(13), C(14), C(13, Suit.Diamonds), C(14, Suit.Diamonds),
                C(4), C(2), C(3, Suit.Diamonds), C(7, Suit.Hearts), C(5), C(9, Suit.Spades), C(6), C(11));
            var g = new Game(new long[] { 100, 100 }, shuffler: deck); g.Start(); g.Act(BettingAction.RaiseTo, 100); g.Act(BettingAction.Call);
            var settled = g.State; string oldWindow = settled.AbilityWindow.Id;
            g.State = MatchSnapshotCodec.Deserialize(MatchSnapshotCodec.Serialize(settled, g.Catalog), g.Catalog, settled.Definition);
            g.Drain(true);
            Assert.That(Stack(g, 0), Is.EqualTo(200)); Assert.That(Stack(g, 1), Is.Zero);
            Assert.That(g.State.Status, Is.EqualTo(MatchStatus.Completed));
            Assert.That(g.State.Participants.Single(p => p.Id == P(1)).Status, Is.EqualTo(ParticipantStatus.Eliminated));
            Assert.That(g.Engine.ExecuteSystem(g.State, new StartHandCommand("new", g.State.Revision)).Error, Is.EqualTo(CommandError.MatchEnded));
            var duplicate = g.Engine.ExecuteSystem(g.State, new AdvanceWindowCommand("twice", g.State.Revision, oldWindow));
            Assert.That(duplicate.Accepted, Is.False); Assert.That(duplicate.State, Is.SameAs(g.State)); Assert.That(Stack(g, 0), Is.EqualTo(200));
        }

        [Test] public void InvalidShuffleDoesNotCommitBlindsOrRandomConsumption()
        {
            var g = new Game(shuffler: new CorruptDeck()); g.Accept(g.Engine.ExecuteSystem(g.State, new StartHandCommand("start", 0)));
            var before = g.State; var result = g.Engine.ExecuteSystem(before, new AdvanceWindowCommand("bad", before.Revision, before.AbilityWindow.Id));
            Assert.That(result.Error, Is.EqualTo(CommandError.InvalidShuffle)); Assert.That(result.State, Is.SameAs(before)); Assert.That(Total(result.State), Is.EqualTo(2000));
        }

        [Test] public void ShuffleIsDeterministicAndNeverMutatesItsInput()
        {
            var cards = Enumerable.Range(0, 52).Select(i => new CardInstanceId("c" + i)).ToArray(); var copy = cards.ToArray();
            var random = HmacDeckShuffler.FromSeed(new byte[32]); var shuffler = new HmacDeckShuffler();
            var a = shuffler.Shuffle(cards, random); var b = shuffler.Shuffle(cards, random);
            Assert.That(a.Cards, Is.EqualTo(b.Cards)); Assert.That(a.Random.StateBytes, Is.EqualTo(b.Random.StateBytes)); Assert.That(cards, Is.EqualTo(copy));
            Assert.That(a.Cards.Distinct().Count(), Is.EqualTo(52)); Assert.That(a.Random.StateBytes, Is.Not.EqualTo(random.StateBytes));
            Assert.That(shuffler.Shuffle(cards, a.Random).Cards, Is.Not.EqualTo(a.Cards));
            // Fixed reference vector also runs in Unity; catches changes to counter byte order and shuffle consumption.
            var expected = new[] { 26, 9, 7, 2, 12, 22, 47, 21, 27, 36, 8, 18, 37, 38, 24, 34, 25, 43, 1, 42, 6, 50, 31, 51,
                20, 33, 40, 45, 32, 0, 48, 19, 10, 3, 49, 46, 44, 29, 11, 4, 17, 16, 23, 30, 15, 14, 41, 35, 13, 28, 5, 39 };
            Assert.That(a.Cards, Is.EqualTo(expected.Select(i => new CardInstanceId("c" + i))));
            Assert.That(a.Random.StateBytes.Skip(32), Is.EqualTo(new byte[] { 51, 0, 0, 0, 0, 0, 0, 0 }));
        }

        [Test] public void EveryCheckpointCanRestoreIntoAFreshEngineAndContinueIdentically()
        {
            var g = new Game(new long[] { 1000, 700, 300 });
            int handsStarted = 0;
            for (int step = 0; step < 200; step++)
            {
                string snapshot = MatchSnapshotCodec.Serialize(g.State, g.Catalog);
                var restored = MatchSnapshotCodec.Deserialize(snapshot, g.Catalog, g.State.Definition);
                var engine = new HoldemMatchEngine(g.Catalog);
                TransitionResult left, right;
                if (g.State.ActiveDecision == null && g.State.AbilityWindow == null && handsStarted < 3)
                { handsStarted++; var c = new StartHandCommand("start-" + handsStarted, g.State.Revision); left = g.Engine.ExecuteSystem(g.State, c); right = engine.ExecuteSystem(restored, c); }
                else if (g.State.ActiveDecision != null)
                {
                    var actor = g.State.ActiveDecision.ActorId; var legal = g.Engine.GetLegalActions(g.State, actor);
                    var c = new BettingCommand("bet-" + step, g.State.Revision, g.State.ActiveDecision.Id, legal.CanCheck ? BettingAction.Check : BettingAction.Call);
                    left = g.Engine.Execute(g.State, c, actor); right = engine.Execute(restored, c, actor);
                }
                else if (g.State.AbilityWindow != null)
                { var c = new AdvanceWindowCommand("advance-" + step, g.State.Revision, g.State.AbilityWindow.Id); left = g.Engine.ExecuteSystem(g.State, c); right = engine.ExecuteSystem(restored, c); }
                else break;
                g.Accept(left); Assert.That(right.Accepted, Is.True);
                Assert.That(MatchSnapshotCodec.Serialize(right.State, g.Catalog), Is.EqualTo(MatchSnapshotCodec.Serialize(g.State, g.Catalog)));
                Assert.That(right.Events.Select(e => e.Kind), Is.EqualTo(left.Events.Select(e => e.Kind)));
            }
            Assert.That(handsStarted, Is.EqualTo(3)); Assert.That(Total(g.State), Is.EqualTo(2000));
            Assert.That(g.State.CurrentHand.Stage, Is.EqualTo(HandStage.Settled)); Assert.That(g.State.AbilityWindow, Is.Null);
        }

        [Test] public void StandardEngineRejectsLoadedAbilitiesInsteadOfSkippingTheirEffects()
        {
            var g = new Game(); var s = g.State; var definition = new AbilityDefinitionId("test-ability");
            var catalog = new ContentCatalog(StandardHoldem.ContentVersion, StandardHoldem.Cards(), new[] { definition }, new StatusDefinitionId[0]);
            var state = new MatchState(s.Id, s.Revision, s.Definition, s.Status, s.Table, s.Participants,
                new[] { new AbilityInstance(new AbilityInstanceId("ability"), definition, P(0), EntityRef.Participant(P(0))) },
                s.Statuses, s.CurrentHand, s.ActiveDecision, s.Resolution, s.Random, s.AbilityWindow, s.Knowledge);
            StateValidator.Validate(state, catalog);
            var result = new HoldemMatchEngine(catalog).ExecuteSystem(state, new StartHandCommand("start", state.Revision));
            Assert.That(result.Error, Is.EqualTo(CommandError.UnsupportedExtensions)); Assert.That(result.State, Is.SameAs(state));
        }

        [Test] public void SidePotWinnerMustBeEligibleEvenWhenShortStackHasTheBestHand()
        {
            var deck = new FixedDeck(C(13), C(12), C(14), C(13, Suit.Diamonds), C(12, Suit.Diamonds), C(14, Suit.Diamonds),
                C(4), C(2), C(3, Suit.Diamonds), C(7, Suit.Hearts), C(5), C(9, Suit.Spades), C(6), C(11));
            var g = new Game(new long[] { 100, 300, 500 }, shuffler: deck); g.Start();
            g.Act(BettingAction.RaiseTo, 100); g.Act(BettingAction.RaiseTo, 300); g.Act(BettingAction.Call);
            Assert.That(Stack(g, 0), Is.EqualTo(300)); Assert.That(Stack(g, 1), Is.EqualTo(400)); Assert.That(Stack(g, 2), Is.EqualTo(200));
            Assert.That(g.State.CurrentHand.Settlement.Awards.Single(a => a.PotIndex == 0).ParticipantId, Is.EqualTo(P(0)));
            Assert.That(g.State.CurrentHand.Settlement.Awards.Single(a => a.PotIndex == 1).ParticipantId, Is.EqualTo(P(1)));
        }

        [Test] public void RandomLegalHandsTerminateAndConserveEveryChip()
        {
            var random = new Random(831);
            for (int trial = 0; trial < 250; trial++)
            {
                var stacks = Enumerable.Range(0, random.Next(2, 9)).Select(i => (long)random.Next(1, 501)).ToArray();
                var g = new Game(stacks); g.Start(); int actions = 0;
                while (g.State.CurrentHand.Stage != HandStage.Settled)
                {
                    Assert.That(++actions, Is.LessThan(200)); Assert.That(Total(g.State), Is.EqualTo(stacks.Sum()));
                    var legal = g.Engine.GetLegalActions(g.State, g.State.ActiveDecision.ActorId); int choice = random.Next(6);
                    if (choice == 0) g.Act(BettingAction.Fold);
                    else if (choice <= 2 && legal.CanRaise)
                        g.Act(BettingAction.RaiseTo, choice == 1 || !legal.MinimumRaiseTo.HasValue ? legal.MaximumRaiseTo.Value : Math.Min(legal.MinimumRaiseTo.Value.Value, legal.MaximumRaiseTo.Value));
                    else g.CheckCall();
                }
                Assert.That(Total(g.State), Is.EqualTo(stacks.Sum())); Assert.That(g.State.Participants.All(p => p.Resources.Chips.Value >= 0), Is.True);
                g.Drain(true);
            }
        }

        [Test] public void EvaluatorHandlesEveryCategoryWheelAndKickers()
        {
            Func<int[], Suit[], HandEvaluation> eval = (ranks, suits) => StandardHandEvaluator.Evaluate(P(0),
                ranks.Select((rank, i) => new EffectiveCard(new CardInstanceId("c" + i), (Rank)rank, suits[i])));
            var mixed = new[] { Suit.Clubs, Suit.Hearts, Suit.Spades, Suit.Diamonds, Suit.Clubs };
            var flush = Enumerable.Repeat(Suit.Hearts, 5).ToArray();
            Assert.That(eval(new[] { 14, 5, 4, 3, 2 }, flush).Category, Is.EqualTo(HandCategory.StraightFlush));
            Assert.That(eval(new[] { 9, 9, 9, 9, 2 }, mixed).Category, Is.EqualTo(HandCategory.FourOfAKind));
            Assert.That(eval(new[] { 9, 9, 9, 2, 2 }, mixed).Category, Is.EqualTo(HandCategory.FullHouse));
            Assert.That(eval(new[] { 14, 10, 8, 5, 2 }, flush).Category, Is.EqualTo(HandCategory.Flush));
            Assert.That(eval(new[] { 14, 5, 4, 3, 2 }, mixed).TieBreakRanks.Single(), Is.EqualTo(5));
            Assert.That(eval(new[] { 9, 9, 9, 3, 2 }, mixed).Category, Is.EqualTo(HandCategory.ThreeOfAKind));
            Assert.That(eval(new[] { 9, 9, 3, 3, 2 }, mixed).Category, Is.EqualTo(HandCategory.TwoPair));
            Assert.That(eval(new[] { 9, 9, 5, 3, 2 }, mixed).Category, Is.EqualTo(HandCategory.OnePair));
            Assert.That(eval(new[] { 14, 9, 5, 3, 2 }, mixed).Category, Is.EqualTo(HandCategory.HighCard));
            Assert.That(StandardHandEvaluator.Compare(eval(new[] { 9, 9, 14, 13, 3 }, mixed), eval(new[] { 9, 9, 14, 12, 3 }, mixed)), Is.GreaterThan(0));
        }

        [Test] public void EvaluatorChoosesBestFiveAndIsIndependentOfInputOrder()
        {
            var cards = new[] { 14, 14, 14, 13, 13, 13, 2 }.Select((r, i) => new EffectiveCard(new CardInstanceId("c" + i), (Rank)r, (Suit)(i % 4 + 1))).ToArray();
            var a = StandardHandEvaluator.Evaluate(P(0), cards); var b = StandardHandEvaluator.Evaluate(P(0), cards.Reverse());
            Assert.That(a.Category, Is.EqualTo(HandCategory.FullHouse)); Assert.That(a.TieBreakRanks, Is.EqualTo(new[] { 14, 13 }));
            Assert.That(a.SelectedCards, Is.EqualTo(b.SelectedCards));
        }
    }
}
