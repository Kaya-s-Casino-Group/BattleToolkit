using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class DataFoundationTests
    {
        internal sealed class Fixture
        {
            public readonly ParticipantId A = new ParticipantId("player-a"), B = new ParticipantId("player-b"), C = new ParticipantId("player-c");
            public readonly MatchId Match = new MatchId("match-1");
            public readonly HandId Hand = new HandId("hand-1");
            public readonly AbilityInstanceId Ability = new AbilityInstanceId("ability-1");
            public readonly CardDefinitionId CardDefinition = new CardDefinitionId("spades-ace");
            public readonly AbilityDefinitionId AbilityDefinition = new AbilityDefinitionId("replace-card");
            public readonly StatusDefinitionId StatusDefinition = new StatusDefinitionId("rank-bonus");
            public readonly SeatId[] Seats = { new SeatId("seat-0"), new SeatId("seat-1"), new SeatId("seat-2") };
            public MatchDefinition Definition = new MatchDefinition("holdem", "1", "content-1", 6, new Chips(10), new Chips(20));
            public ContentCatalog Catalog;
            public ParticipantState[] Participants;
            public HandParticipantState[] Contributions;
            public CardInstance[] Cards;
            public CardZone[] Zones;
            public AbilityInstance[] Abilities;
            public StatusInstance[] Statuses = new StatusInstance[0];
            public BettingRoundState Betting;
            public DecisionState Decision;
            public ResolutionState Resolution = ResolutionState.Empty;
            public PotSettlement Settlement;
            public HandStage Stage = HandStage.Betting;
            public AbilityWindowState Window = null;
            public KnowledgeGrant[] Knowledge = new KnowledgeGrant[0];

            public Fixture()
            {
                Catalog = new ContentCatalog("content-1", new[] { new CardDefinition(CardDefinition, Rank.Ace, Suit.Spades, new string[0]) },
                    new[] { AbilityDefinition }, new[] { StatusDefinition });
                Participants = new[] { Player(A, 0, 990), Player(B, 1, 980), Player(C, 2, 1000) };
                Contributions = new[] { Contribution(A, 10), Contribution(B, 20), Contribution(C, 0) };
                Cards = Enumerable.Range(1, 9).Select(i => new CardInstance(new CardInstanceId("card-" + i), CardDefinition)).ToArray();
                Zones = new[] {
                    new CardZone(new ZoneId("deck"), CardZoneKinds.Deck, null, Cards.Skip(6).Select(c => c.Id)),
                    new CardZone(new ZoneId("board"), HoldemZoneKinds.Board, null, new CardInstanceId[0]),
                    new CardZone(new ZoneId("hole-a"), HoldemZoneKinds.HoleCards, A, Cards.Take(2).Select(c => c.Id)),
                    new CardZone(new ZoneId("hole-b"), HoldemZoneKinds.HoleCards, B, Cards.Skip(2).Take(2).Select(c => c.Id)),
                    new CardZone(new ZoneId("hole-c"), HoldemZoneKinds.HoleCards, C, Cards.Skip(4).Take(2).Select(c => c.Id)) };
                Abilities = new[] { new AbilityInstance(Ability, AbilityDefinition, A, EntityRef.Participant(A), 2) };
                Betting = new BettingRoundState(Street.Preflop, new Chips(20), new Chips(20), new[] { C, A, B },
                    new[] { A, B, C }.Select(p => new BettingPlayerState(p, false, Chips.Zero, null)));
                Decision = DecisionState.ModeAction(new DecisionId("decision-1"), C);
            }
            public ParticipantState Player(ParticipantId id, int seat, long stack) =>
                new ParticipantState(id, Seats[seat], ParticipantStatus.Active, new ResourceState(new Chips(stack), 2, 10));
            public HandParticipantState Contribution(ParticipantId id, long amount, Participation participation = Participation.InHand) =>
                new HandParticipantState(id, participation, new Chips(amount), new Chips(amount));
            public EffectContinuation Continuation() => new EffectContinuation(Ability, 1, new[] { EntityRef.Card(Cards[0].Id) });
            public MatchState Build() => new MatchState(Match, 42, Definition, MatchStatus.Running, new TableState(Seats),
                Participants, Abilities, Statuses, new HandState(Hand, Stage, Seats[2], A, B, Contributions, Cards, Zones, Betting, Settlement),
                Decision, Resolution, new RandomState("fixture-provider", 1, new byte[] { 3, 1, 4, 1, 5 }), Window, Knowledge);
            public void Settled()
            {
                Contributions = new[] { Contribution(A, 100), Contribution(B, 300), Contribution(C, 300) };
                Participants = new[] { Player(A, 0, 1200), Player(B, 1, 1100), Player(C, 2, 700) };
                Stage = HandStage.Settled; Betting = null; Decision = null;
                Settlement = new PotSettlement("settlement-1", new HandEvaluation[0],
                    new[] { new PotAward(0, A, new Chips(300)), new PotAward(1, B, new Chips(400)) }, new ChipTransfer[0]);
            }
        }

        private static void Invalid(Fixture f, string code)
        { Assert.That(Assert.Throws<StateValidationException>(() => StateValidator.Validate(f.Build(), f.Catalog)).Code, Is.EqualTo(code)); }

        [Test] public void RuntimeHasNoUnityDependencies()
        {
            Assert.That(typeof(MatchState).Assembly.GetReferencedAssemblies().Any(a => a.Name.StartsWith("Unity", StringComparison.Ordinal)), Is.False);
        }

        [Test] public void IdsAreOrdinalAndDefaultIsInvalid()
        {
            Assert.That(new ParticipantId("A"), Is.EqualTo(new ParticipantId("A")));
            Assert.That(new ParticipantId("A"), Is.Not.EqualTo(new ParticipantId("a")));
            Assert.That(default(ParticipantId).IsValid, Is.False);
            Assert.Throws<ArgumentException>(() => new CardInstanceId(" "));
            Assert.That(new HashSet<ParticipantId> { new ParticipantId("A"), new ParticipantId("A") }.Count, Is.EqualTo(1));
        }

        [Test] public void ChipsRejectNegativeUnderflowAndOverflow()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Chips(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => { var unused = new Chips(1) - new Chips(2); GC.KeepAlive(unused); });
            Assert.Throws<OverflowException>(() => { var unused = new Chips(long.MaxValue) + new Chips(1); GC.KeepAlive(unused); });
            Assert.That((new Chips(30) - new Chips(10)).Value, Is.EqualTo(20));
        }

        [Test] public void StateDefensivelyCopiesInputsAndExposesReadOnlyCollections()
        {
            var f = new Fixture(); var state = f.Build(); var first = state.Participants[0];
            f.Participants[0] = f.Player(f.A, 0, 1); f.Seats[0] = new SeatId("mutated");
            Assert.That(state.Participants[0], Is.SameAs(first));
            Assert.That(state.Table.Seats[0].Value, Is.EqualTo("seat-0"));
            Assert.Throws<NotSupportedException>(() => ((IList<ParticipantState>)state.Participants)[0] = f.Participants[0]);
            var bytes = new byte[] { 1, 2 }; var random = new RandomState("test", 1, bytes); bytes[0] = 9;
            Assert.That(random.StateBytes[0], Is.EqualTo(1));
        }

        [Test] public void MultipleInstancesOfOneCardDefinitionAreValid()
        { var f = new Fixture(); StateValidator.Validate(f.Build(), f.Catalog); }

        [Test] public void CardCannotOccupyTwoZones()
        {
            var f = new Fixture(); f.Zones[1] = new CardZone(new ZoneId("board"), HoldemZoneKinds.Board, null, new[] { f.Cards[0].Id });
            Invalid(f, "DuplicateCardLocation");
        }

        [Test] public void EveryCardMustHaveALocation()
        {
            var f = new Fixture(); f.Zones[0] = new CardZone(new ZoneId("deck"), CardZoneKinds.Deck, null, new CardInstanceId[0]);
            Invalid(f, "UnlocatedCard");
        }

        [Test] public void UnknownCardInZoneIsRejected()
        {
            var f = new Fixture(); f.Zones[1] = new CardZone(new ZoneId("board"), HoldemZoneKinds.Board, null, new[] { new CardInstanceId("missing") });
            Invalid(f, "UnknownCard");
        }

        [Test] public void CardIdentityMustBeUnique()
        { var f = new Fixture(); f.Cards[1] = f.Cards[0]; Invalid(f, "DuplicateId"); }

        [Test] public void SeatCannotHaveTwoOccupants()
        { var f = new Fixture(); f.Participants[1] = f.Player(f.B, 0, 980); Invalid(f, "DuplicateId"); }

        [Test] public void HandRequiresEveryParticipantsHoleZone()
        { var f = new Fixture(); f.Zones[4] = new CardZone(new ZoneId("hole-c"), HoldemZoneKinds.HoleCards, f.A, f.Zones[4].Cards); Invalid(f, "InvalidHoleZone"); }

        [Test] public void FoldedOrAllInPlayersCannotBePendingActors()
        {
            var f = new Fixture(); f.Contributions[2] = f.Contribution(f.C, 0, Participation.Folded); Invalid(f, "InvalidPendingActor");
            f = new Fixture(); f.Participants[2] = f.Player(f.C, 2, 0); Invalid(f, "InvalidPendingActor");
        }

        [Test] public void BettingDecisionMustMatchTheCurrentActor()
        { var f = new Fixture(); f.Decision = DecisionState.ModeAction(new DecisionId("d"), f.A); Invalid(f, "InvalidModeDecision"); }

        [Test] public void StatusAndContinuationReferencesMustResolve()
        {
            var f = new Fixture();
            f.Statuses = new[] { new StatusInstance(new StatusInstanceId("s"), f.StatusDefinition, EntityRef.Ability(f.Ability),
                EntityRef.Card(new CardInstanceId("missing")), 1, StatusDuration.UntilRoundEnd(f.Hand.Value)) };
            Invalid(f, "UnknownReference");
            f = new Fixture(); f.Resolution = new ResolutionState(new[] { new EffectContinuation(new AbilityInstanceId("missing"), 0, new EntityRef[0]) });
            Invalid(f, "InvalidContinuation");
        }

        [Test] public void ExpiryCannotPointToAnotherHand()
        {
            var f = new Fixture(); f.Statuses = new[] { new StatusInstance(new StatusInstanceId("s"), f.StatusDefinition,
                EntityRef.Ability(f.Ability), EntityRef.Participant(f.A), 1, StatusDuration.UntilRoundEnd("old-hand")) };
            Invalid(f, "UnknownRound");
        }

        [Test] public void SelectionBoundsAndCandidateUniquenessAreChecked()
        {
            var f = new Fixture(); f.Decision = DecisionState.SelectCards(new DecisionId("d"), f.A, new[] { f.Cards[0].Id }, 2, 2, f.Continuation());
            Invalid(f, "InvalidCardSelection");
            f.Decision = DecisionState.SelectCards(new DecisionId("d"), f.A, new[] { f.Cards[0].Id, f.Cards[0].Id }, 1, 1, f.Continuation());
            Invalid(f, "DuplicateId");
        }

        [Test] public void SidePotsKeepContributorsSeparateFromEligibleWinners()
        {
            var f = new Fixture(); var result = PotCalculator.Calculate(new[] { f.Contribution(f.A, 100),
                f.Contribution(f.B, 300, Participation.Folded), f.Contribution(f.C, 300) });
            Assert.That(result.Pots.Select(p => p.Amount.Value), Is.EqualTo(new long[] { 300, 400 }));
            Assert.That(result.Pots[0].Contributors.Count, Is.EqualTo(3));
            Assert.That(result.Pots[0].EligibleWinners, Is.EqualTo(new[] { f.A, f.C }));
            Assert.That(result.Pots[1].EligibleWinners, Is.EqualTo(new[] { f.C }));
        }

        [Test] public void UncalledExcessIsARefundNotAPot()
        {
            var f = new Fixture(); var result = PotCalculator.Calculate(new[] { f.Contribution(f.A, 100), f.Contribution(f.B, 300), f.Contribution(f.C, 500) });
            Assert.That(result.Pots.Select(p => p.Amount.Value), Is.EqualTo(new long[] { 300, 400 }));
            Assert.That(result.Refunds.Single().ParticipantId, Is.EqualTo(f.C));
            Assert.That(result.Refunds.Single().Amount.Value, Is.EqualTo(200));
        }

        [Test] public void PotBreakdownConservesContributionsAcrossRandomInputs()
        {
            var random = new Random(123); var f = new Fixture();
            for (int trial = 0; trial < 1000; trial++)
            {
                var contributions = new[] { f.A, f.B, f.C }.Select(p => f.Contribution(p, random.Next(0, 10001),
                    random.Next(2) == 0 ? Participation.InHand : Participation.Folded)).ToArray();
                var result = PotCalculator.Calculate(contributions);
                Assert.That(result.Pots.Sum(p => p.Amount.Value) + result.Refunds.Sum(r => r.Amount.Value),
                    Is.EqualTo(contributions.Sum(p => p.HandCommitted.Value)));
            }
        }

        [Test] public void AggregatePotOverflowIsRejected()
        {
            var f = new Fixture();
            Assert.Throws<OverflowException>(() => PotCalculator.Calculate(new[] { f.Contribution(f.A, long.MaxValue), f.Contribution(f.B, long.MaxValue) }));
        }

        [Test] public void SettlementCannotAwardASidePotToAnIneligiblePlayer()
        {
            var f = new Fixture(); f.Settled(); StateValidator.Validate(f.Build(), f.Catalog);
            f.Settlement = new PotSettlement("s", new HandEvaluation[0], new[] {
                new PotAward(0, f.A, new Chips(300)), new PotAward(1, f.A, new Chips(400)) }, new ChipTransfer[0]);
            Invalid(f, "InvalidAward");
        }

        [Test] public void SettlementMustBalanceEachPot()
        {
            var f = new Fixture(); f.Settled(); f.Settlement = new PotSettlement("s", new HandEvaluation[0],
                new[] { new PotAward(0, f.A, new Chips(299)), new PotAward(1, f.B, new Chips(401)) }, new ChipTransfer[0]);
            Invalid(f, "UnbalancedSettlement");
        }

        [Test] public void SnapshotRoundTripPreservesPrivateDeckDecisionAndRandomState()
        {
            var f = new Fixture(); var original = f.Build(); var json = MatchSnapshotCodec.Serialize(original, f.Catalog);
            var restored = MatchSnapshotCodec.Deserialize(json, f.Catalog, f.Definition);
            Assert.That(MatchSnapshotCodec.Serialize(restored, f.Catalog), Is.EqualTo(json));
            Assert.That(restored.CurrentHand.Zones[0].Cards, Is.EqualTo(original.CurrentHand.Zones[0].Cards));
            Assert.That(restored.Random.StateBytes, Is.EqualTo(original.Random.StateBytes));
            Assert.That(restored.ActiveDecision.Id, Is.EqualTo(original.ActiveDecision.Id));
            Assert.That(restored, Is.Not.SameAs(original));
        }

        [Test] public void SnapshotRoundTripPreservesPausedAbilityAndDuration()
        {
            var f = new Fixture(); f.Decision = DecisionState.SelectCards(new DecisionId("select-card"), f.A,
                new[] { f.Cards[0].Id, f.Cards[1].Id }, 1, 1, f.Continuation());
            f.Resolution = new ResolutionState(new[] { f.Continuation() });
            f.Statuses = new[] { new StatusInstance(new StatusInstanceId("s"), f.StatusDefinition, EntityRef.Ability(f.Ability),
                EntityRef.Card(f.Cards[0].Id), 2, StatusDuration.UntilPhaseEnd(f.Hand.Value, HoldemAbilityAdapter.Phase(Street.Preflop))) };
            string json = MatchSnapshotCodec.Serialize(f.Build(), f.Catalog);
            var restored = MatchSnapshotCodec.Deserialize(json, f.Catalog, f.Definition);
            Assert.That(MatchSnapshotCodec.Serialize(restored, f.Catalog), Is.EqualTo(json));
            Assert.That(restored.ActiveDecision.Continuation.NextEffectIndex, Is.EqualTo(1));
            Assert.That(restored.Statuses[0].Duration.PhaseKey, Is.EqualTo(HoldemAbilityAdapter.Phase(Street.Preflop)));
        }

        [Test] public void SettledSnapshotDoesNotAddThePotToStacksAgain()
        {
            var f = new Fixture(); f.Settled(); string json = MatchSnapshotCodec.Serialize(f.Build(), f.Catalog);
            var restored = MatchSnapshotCodec.Deserialize(json, f.Catalog, f.Definition);
            Assert.That(restored.Participants.Sum(p => p.Resources.Chips.Value), Is.EqualTo(3000));
            Assert.That(restored.CurrentHand.Settlement.SettlementId, Is.EqualTo("settlement-1"));
        }

        [Test] public void SnapshotRejectsFutureSchemaAndWrongRulesOrContent()
        {
            var f = new Fixture(); string json = MatchSnapshotCodec.Serialize(f.Build(), f.Catalog);
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json.Replace("\"SchemaVersion\":" + MatchSnapshotCodec.SchemaVersion,
                "\"SchemaVersion\":" + (MatchSnapshotCodec.SchemaVersion + 1)), f.Catalog, f.Definition));
            var rules = new MatchDefinition("holdem", "2", "content-1", 6, new Chips(10), new Chips(20));
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json, f.Catalog, rules));
            var catalog = new ContentCatalog("content-2", new CardDefinition[0], new AbilityDefinitionId[0], new StatusDefinitionId[0]);
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json, catalog, f.Definition));
        }

        [Test] public void SnapshotRejectsTamperedAmountsAndNullCollections()
        {
            var f = new Fixture(); string json = MatchSnapshotCodec.Serialize(f.Build(), f.Catalog);
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json.Replace("\"Value\":990", "\"Value\":-1"), f.Catalog, f.Definition));
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json.Replace("\"Statuses\":[]", "\"Statuses\":null"), f.Catalog, f.Definition));
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json.Replace("\"Revision\":42", "\"Revision\":-1"), f.Catalog, f.Definition));
        }

        [Test] public void SnapshotRejectsMalformedOrOversizedInput()
        {
            var f = new Fixture();
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize("{broken", f.Catalog, f.Definition));
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize("null", f.Catalog, f.Definition));
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(new string(' ', MatchSnapshotCodec.MaxUtf8Bytes + 1), f.Catalog, f.Definition));
        }

        [Test] public void SnapshotRejectsUnknownEnumsAndDefinitionReferences()
        {
            var f = new Fixture(); string json = MatchSnapshotCodec.Serialize(f.Build(), f.Catalog);
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json.Replace("\"Stage\":2", "\"Stage\":99"), f.Catalog, f.Definition));
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json.Replace("spades-ace", "missing-definition"), f.Catalog, f.Definition));
        }
    }
}
