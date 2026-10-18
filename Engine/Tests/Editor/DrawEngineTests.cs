using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class DrawEngineTests
    {
        private static EffectiveCard Card(string id, Rank rank, Suit suit = Suit.Clubs) => new EffectiveCard(new CardInstanceId(id), rank, suit);
        [Test] public void SharedClassifierRecognizesPartialHandsAndSeparatesKickers()
        {
            var cards = new[] { Card("a", Rank.Ace), Card("b", Rank.Two), Card("c", Rank.Two, Suit.Hearts), Card("d", Rank.King), Card("e", Rank.Seven) };
            var pair = PokerHandClassifier.Classify(cards);
            Assert.AreEqual(HandCategory.OnePair, pair.Category);
            CollectionAssert.AreEqual(cards.Select(c => c.Id), pair.SubmittedCards);
            CollectionAssert.AreEqual(new[] { cards[1].Id, cards[2].Id }, pair.ScoringCards);
            Assert.AreEqual(HandCategory.HighCard, PokerHandClassifier.Classify(cards.Take(1)).Category);
            Assert.AreEqual(HandCategory.OnePair, PokerHandClassifier.Classify(cards.Skip(1).Take(2)).Category);
            Assert.AreEqual(HandCategory.HighCard, PokerHandClassifier.Classify(new[] { Card("1", Rank.Two), Card("2", Rank.Three), Card("3", Rank.Four), Card("4", Rank.Five) }).Category);
        }
        [Test] public void SharedClassifierHandlesWheelFullHouseAndRepeatedFaces()
        {
            Assert.AreEqual(HandCategory.StraightFlush, PokerHandClassifier.Classify(new[] { Rank.Ace, Rank.Two, Rank.Three, Rank.Four, Rank.Five }.Select((r, i) => Card(i.ToString(), r))).Category);
            Assert.AreEqual(HandCategory.FullHouse, PokerHandClassifier.Classify(new[] { Rank.Two, Rank.Two, Rank.Two, Rank.Three, Rank.Three }.Select((r, i) => Card(i.ToString(), r, (Suit)(i % 4 + 1)))).Category);
            var five = PokerHandClassifier.Classify(Enumerable.Range(0, 5).Select(i => Card(i.ToString(), Rank.Ace)));
            Assert.AreEqual(HandCategory.FiveOfAKind, five.Category); Assert.AreEqual(5, five.ScoringCards.Count);
        }
        [Test] public void PatternScoreExcludesKickerEnhancementButExplicitSubsetIsModeOwned()
        {
            var submitted = new[] { Card("a", Rank.Ace), Card("b", Rank.Two), Card("c", Rank.Two, Suit.Hearts) };
            var input = submitted.Select(c => new ScoringCard(c.Id.Value, new CardFace(c.Rank, c.Suit), c.Rank == Rank.Ace ? 10000 : 0)).ToArray();
            var pattern = PokerHandClassifier.Classify(submitted);
            var score = ScoreEngine.EvaluatePattern("score", ScoreProfile.Default, pattern, 1, input, new ScoringRelic[0]);
            Assert.AreEqual(28, score.Total); Assert.AreEqual(2, score.Steps.Count); Assert.IsFalse(score.Steps.Any(s => s.CardId == "a"));
            Assert.AreEqual(5, ScoreEngine.Evaluate("empty", ScoreProfile.Default, HandCategory.HighCard, 1, new ScoringCard[0], new ScoringRelic[0]).Total);
            Assert.Throws<ArgumentException>(() => ScoreEngine.EvaluatePattern("bad", ScoreProfile.Default, pattern, 1, input.Take(2), new ScoringRelic[0]));
        }
        [Test] public void DrawPlayScoresAndStopsAtCheckpointUntilExplicitContinue()
        {
            var f = new Fixture(); var oldHand = f.Hand.ToArray(); f.Do(DrawAction.Play, oldHand.Take(3));
            Assert.AreEqual(DrawPhase.Scored, f.State.Phase); Assert.AreEqual(1, f.State.Scores.Count); Assert.AreEqual(3, f.State.HandsRemaining);
            Assert.AreEqual(5, f.Hand.Count); Assert.AreEqual(3, f.State.Pattern.SubmittedCards.Count); Assert.AreEqual(1, f.State.Pattern.ScoringCards.Count);
            Assert.AreEqual(1, f.State.Scores.Single().Steps.Count);
            var denied = f.Engine.Execute(f.State, f.Command(DrawAction.Play, f.Hand.Take(1)), f.Player); Assert.AreEqual(DrawError.InvalidAction, denied.Error);
            f.Do(DrawAction.Continue); Assert.AreEqual(DrawPhase.Ready, f.State.Phase); Assert.AreEqual(8, f.Hand.Count);
            Assert.AreEqual(f.State.Id + "/play/1", f.State.World.ScopeId); Assert.AreEqual(1, f.State.Scores.Count);
        }
        [Test] public void DiscardConsumesOnlyDiscardBudgetAndDrawsFromDeck()
        {
            var f = new Fixture(discards: 1); var discarded = f.Hand.Take(5).ToArray(); var initialRandom = f.State.Random.StateBytes.ToArray();
            f.Do(DrawAction.Discard, discarded); Assert.AreEqual(0, f.State.DiscardsRemaining); Assert.AreEqual(4, f.State.HandsRemaining);
            Assert.AreEqual(0, f.State.Score); Assert.AreEqual(8, f.Hand.Count); Assert.IsFalse(f.Hand.Any(discarded.Contains));
            CollectionAssert.AreEqual(initialRandom, f.State.Random.StateBytes);
            Assert.AreEqual(DrawError.InvalidAction, f.Engine.Execute(f.State, f.Command(DrawAction.Discard, f.Hand.Take(1)), f.Player).Error);
        }
        [Test] public void SharedZoneTransfersKeepDiscardSelectionAndRefillOrder()
        {
            var g = new Fixture(); var before = g.State; var beforeHand = g.Hand.ToArray();
            CollectionAssert.AreEqual(new[] { "instance-23", "instance-22", "instance-21", "instance-20", "instance-19", "instance-18", "instance-17", "instance-16" },
                g.Hand.Select(c => c.Value));
            var selected = new[] { g.Hand[3], g.Hand[0] }; g.Do(DrawAction.Discard, selected);
            CollectionAssert.AreEqual(selected, g.State.World.Zones.Single(z => z.Kind == CardZoneKinds.Discard).Cards);
            CollectionAssert.AreEqual(new[] { "instance-22", "instance-21", "instance-19", "instance-18", "instance-17", "instance-16", "instance-15", "instance-14" },
                g.Hand.Select(c => c.Value));
            CollectionAssert.AreEqual(beforeHand, before.World.Zones.Single(z => z.Kind == CardZoneKinds.Hand).Cards);
            Assert.AreEqual(16, before.World.Zones.Single(z => z.Kind == CardZoneKinds.Deck).Cards.Count);
            Assert.AreEqual(24, g.State.World.Zones.SelectMany(z => z.Cards).Distinct().Count());
        }

        [Test] public void DrawTargetsAndHandExhaustionHaveIndependentTerminalResults()
        {
            var win = new Fixture(target: 1); win.Do(DrawAction.Play, win.Hand.Take(1)); Assert.AreEqual(DrawPhase.Won, win.State.Phase);
            Assert.IsFalse(win.State.World.IsRunning); Assert.IsNull(win.State.World.AbilityWindow);
            var lose = new Fixture(hands: 1); lose.Do(DrawAction.Play, lose.Hand.Take(1)); Assert.AreEqual(DrawPhase.Lost, lose.State.Phase);
            Assert.AreEqual(DrawError.InvalidAction, lose.Engine.Execute(lose.State, lose.Command(DrawAction.Continue), lose.Player).Error);
        }
        [Test] public void DrawRejectsUnauthorizedStaleDuplicateAndForeignSelectionsAtomically()
        {
            var f = new Fixture(); var command = f.Command(DrawAction.Play, f.Hand.Take(1)); var before = f.Json;
            Assert.AreEqual(DrawError.Unauthorized, f.Engine.Execute(f.State, command, new ParticipantId("other")).Error);
            Assert.AreEqual(DrawError.StaleRevision, f.Engine.Execute(f.State, new DrawCommand("stale", 1, DrawAction.Play, f.Hand.Take(1)), f.Player).Error);
            Assert.AreEqual(DrawError.InvalidSelection, f.Engine.Execute(f.State, f.Command(DrawAction.Play, new[] { f.Hand[0], f.Hand[0] }), f.Player).Error);
            var foreign = f.State.World.Zones.Single(z => z.Kind == CardZoneKinds.Deck).Cards[0];
            Assert.AreEqual(DrawError.InvalidSelection, f.Engine.Execute(f.State, f.Command(DrawAction.Play, new[] { foreign }), f.Player).Error);
            Assert.AreEqual(DrawError.InvalidAction, f.Engine.Execute(f.State, f.Command(DrawAction.Timeout), f.Player).Error);
            Assert.AreEqual(before, f.Json);
        }
        [Test] public void ExhaustedDeckEndsDrawEncounterWithoutBettingOrAutomaticRecycling()
        {
            var f = new Fixture(discards: 6); f.Do(DrawAction.Play, f.Hand.Take(5)); f.Do(DrawAction.Continue);
            while (f.State.Phase == DrawPhase.Ready) f.Do(DrawAction.Discard, f.Hand.Take(5));
            Assert.AreEqual(DrawPhase.Lost, f.State.Phase); Assert.AreEqual(3, f.State.HandsRemaining);
            Assert.AreEqual(1, f.State.Scores.Count); Assert.AreEqual(0, f.Hand.Count);
            var saved = DrawSnapshotCodec.Deserialize(f.Json, f.Engine); Assert.AreEqual(DrawPhase.Lost, saved.Phase);
        }
        [Test] public void DrawSnapshotRestoresExactContinuationAndRejectsChangedRulesOrScore()
        {
            var f = new Fixture(); f.Do(DrawAction.Play, f.Hand.Take(5)); var json = f.Json;
            var restored = DrawSnapshotCodec.Deserialize(json, f.Engine); var command = f.Command(DrawAction.Continue);
            var a = f.Engine.Execute(f.State, command, f.Player); var b = f.Engine.Execute(restored, command, f.Player);
            Assert.IsTrue(a.Accepted); Assert.AreEqual(DrawSnapshotCodec.Serialize(a.State, f.Engine), DrawSnapshotCodec.Serialize(b.State, f.Engine));
            Assert.Throws<SnapshotException>(() => DrawSnapshotCodec.Deserialize(json.Replace("\"Total\":" + f.State.Score, "\"Total\":" + (f.State.Score + 1)), f.Engine));
            Assert.Throws<SnapshotException>(() => DrawSnapshotCodec.Deserialize(json, new DrawEngine(f.Catalog, new DrawDefinition("different", long.MaxValue))));
        }
        [Test] public void PaidAbilitySelectionUsesSharedExecutorAndResumesAfterSavingOnce()
        {
            var handler = new ChoiceAbility(); var f = new Fixture(handler, AbilityMechanism.Activated, DrawTimings.BeforeAction);
            var ability = f.State.World.Abilities.Single(); var activate = new DrawCommand("activate", f.State.Revision, DrawAction.ActivateAbility, windowId: f.State.World.AbilityWindow.Id, abilityId: ability.Id);
            var first = f.Engine.Execute(f.State, activate, f.Player); Assert.IsTrue(first.Accepted); f.State = first.State;
            Assert.AreEqual(2, f.State.World.Participants[0].Resources.Energy); Assert.AreEqual(0, f.State.World.Abilities[0].RemainingCharges);
            var paused = DrawSnapshotCodec.Deserialize(f.Json, f.Engine); var choice = paused.World.ActiveDecision;
            var resumed = f.Engine.Execute(paused, new DrawCommand("choose", paused.Revision, DrawAction.SelectAbilityTargets, decisionId: choice.Id, selectionKeys: new[] { "ok" }), f.Player);
            Assert.IsTrue(resumed.Accepted); Assert.AreEqual(4, resumed.State.World.Participants[0].Resources.Energy); Assert.IsNull(resumed.State.World.ActiveDecision);
            Assert.AreEqual(1, resumed.State.World.Abilities[0].UsesThisRound);
            Assert.AreEqual(DrawError.StaleRevision, f.Engine.Execute(resumed.State, activate, f.Player).Error);
        }
        [Test] public void TriggeredChoiceBeforeScoringPreservesSubmittedCardsAndDefersScore()
        {
            var f = new Fixture(new ChoiceAbility(), AbilityMechanism.Triggered, DrawTimings.BeforeScoring);
            var chosen = f.Hand.Take(2).ToArray(); f.Do(DrawAction.Play, chosen); Assert.AreEqual(DrawPhase.BeforeScoring, f.State.Phase);
            Assert.AreEqual(0, f.State.Score); Assert.AreEqual(4, f.State.HandsRemaining); CollectionAssert.AreEqual(chosen, f.State.PendingCards);
            f.State = DrawSnapshotCodec.Deserialize(f.Json, f.Engine);
            var result = f.Engine.ResolveTimeout(f.State, f.State.Revision, f.State.World.ActiveDecision.Id); Assert.IsTrue(result.Accepted); f.State = result.State;
            Assert.AreEqual(DrawPhase.Scored, f.State.Phase); Assert.AreEqual(1, f.State.Scores.Count); Assert.AreEqual(4, f.State.World.Participants[0].Resources.Energy);
            f.Do(DrawAction.Continue); Assert.AreEqual(0, f.State.World.Abilities[0].UsesThisRound);
        }
        [Test] public void TriggeredChoiceAfterScoringResumesWithoutScoringAgain()
        {
            var f = new Fixture(new ChoiceAbility(), AbilityMechanism.Triggered, DrawTimings.AfterScoring);
            f.Do(DrawAction.Play, f.Hand.Take(1)); Assert.AreEqual(DrawPhase.AfterScoring, f.State.Phase); long score = f.State.Score;
            f.State = DrawSnapshotCodec.Deserialize(f.Json, f.Engine);
            var result = f.Engine.ResolveTimeout(f.State, f.State.Revision, f.State.World.ActiveDecision.Id); Assert.IsTrue(result.Accepted);
            Assert.AreEqual(DrawPhase.Scored, result.State.Phase); Assert.AreEqual(score, result.State.Score); Assert.AreEqual(1, result.State.Scores.Count);
        }
        [Test] public void DrawAbilityFailureAndScoreOverflowLeaveNoPartialEffects()
        {
            var f = new Fixture(new BrokenAbility(), AbilityMechanism.Activated, DrawTimings.BeforeAction); string before = f.Json;
            var command = new DrawCommand("bad", f.State.Revision, DrawAction.ActivateAbility, windowId: f.State.World.AbilityWindow.Id, abilityId: f.State.World.Abilities[0].Id);
            var failed = f.Engine.Execute(f.State, command, f.Player); Assert.IsFalse(failed.Accepted); Assert.AreEqual(0, failed.Events.Count); Assert.AreSame(f.State, failed.State); Assert.AreEqual(before, f.Json);
            var profile = new ScoreProfile("overflow", Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>().Select(c => new HandScoreRule(c, long.MaxValue, 1, 0, 0)));
            var overflow = new Fixture(profile: profile); var result = overflow.Engine.Execute(overflow.State, overflow.Command(DrawAction.Play, overflow.Hand.Take(1)), overflow.Player);
            Assert.AreEqual(DrawError.Overflow, result.Error); Assert.AreSame(overflow.State, result.State); Assert.AreEqual(0, result.Events.Count);
        }

        [Test] public void DisabledCardRejectsBothActionsAtomicallyWithoutBlockingNormalCards()
        {
            var f = new Fixture(cardMap: c => c.Id.Value == "instance-23" ? c.WithDisabled(true) : c);
            var before = f.Json; var selected = f.Hand.Take(2).ToArray();
            foreach (var action in new[] { DrawAction.Play, DrawAction.Discard })
            {
                var result = f.Engine.Execute(f.State, f.Command(action, selected), f.Player);
                Assert.AreEqual(DrawError.InvalidSelection, result.Error); Assert.AreSame(f.State, result.State);
                Assert.AreEqual(0, result.Events.Count); Assert.AreEqual(before, f.Json);
            }
            f.State = DrawSnapshotCodec.Deserialize(before, f.Engine);
            Assert.IsTrue(f.State.World.Cards.Single(c => c.Id == selected[0]).IsDisabled);
            f.Do(DrawAction.Discard, new[] { selected[1] }); Assert.IsTrue(f.Hand.Contains(selected[0]));
        }

        [Test] public void IneffectiveCardsCanBeDiscardedAndPlayedAndKeepPatternAfterRestore()
        {
            var f = new Fixture(cardMap: c => c.AddEffects(CardEffects.Enhanced | CardEffects.Laminated | CardEffects.Ineffective));
            var json = f.Json;
            Assert.Throws<SnapshotException>(() => DrawSnapshotCodec.Deserialize(json.Replace("\"Effects\":7", "\"Effects\":8"), f.Engine));
            Assert.Throws<SnapshotException>(() => DrawSnapshotCodec.Deserialize(json.Replace("\"SchemaVersion\":2", "\"SchemaVersion\":1"), f.Engine));
            f.State = DrawSnapshotCodec.Deserialize(json, f.Engine);
            f.Do(DrawAction.Discard, f.Hand.Take(1));
            var submitted = f.Hand.Take(5).ToArray();
            f.Do(DrawAction.Play, submitted);
            CollectionAssert.AreEqual(submitted, f.State.Pattern.SubmittedCards);
            Assert.Greater(f.State.Pattern.ScoringCards.Count, 0); Assert.AreEqual(0, f.State.Scores.Single().Steps.Count);
            Assert.AreEqual(f.State.Scores[0].BasePoints * f.State.Scores[0].BaseMultiplier, f.State.Score);
            Assert.IsTrue(f.State.Cards.All(c => c.Card.Effects == (CardEffects)7));
            Assert.AreEqual(f.Json, DrawSnapshotCodec.Serialize(DrawSnapshotCodec.Deserialize(f.Json, f.Engine), f.Engine));
        }

        private sealed class Fixture
        {
            public readonly ParticipantId Player = new ParticipantId("player");
            public readonly ContentCatalog Catalog;
            public readonly DrawEngine Engine;
            public DrawState State;
            public IReadOnlyList<CardInstanceId> Hand => State.World.Zones.Single(z => z.Kind == CardZoneKinds.Hand).Cards;
            public string Json => DrawSnapshotCodec.Serialize(State, Engine);
            public Fixture(object handler = null, AbilityMechanism mechanism = AbilityMechanism.Activated, string timing = DrawTimings.BeforeAction,
                long target = long.MaxValue, int hands = 4, int discards = 3, ScoreProfile profile = null, Func<CardInstance, CardInstance> cardMap = null)
            {
                var definitions = Enumerable.Range(0, 24).Select(i => new CardDefinition(new CardDefinitionId("card-" + i), (Rank)(i % 13 + 2), (Suit)(i % 4 + 1), new string[0])).ToArray();
                var id = new AbilityDefinitionId("choice");
                Catalog = new ContentCatalog("draw.test.1", definitions, handler == null ? new AbilityDefinition[0] :
                    new[] { new AbilityDefinition(id, mechanism, AbilityModes.Solo, new[] { timing }, new AbilityCondition[0]) }, new StatusDefinitionId[0]);
                var runtime = new AbilityRuntime(handler == null ? new AbilityBehavior[0] : new[] { new AbilityBehavior(id,
                    activated: handler as IActivatedAbility, triggered: handler as ITriggeredAbility, selection: handler as IAbilitySelectionHandler, cost: new AbilityCost(Chips.Zero, 1)) });
                Engine = new DrawEngine(Catalog, new DrawDefinition("draw.rules.test.1", target, hands: hands, discards: discards, scoring: profile), runtime, new IdentityShuffle());
                State = Engine.Create("draw-test", Player, definitions.Select((d, i) => new CardInstance(new CardInstanceId("instance-" + i), d.Id))
                    .Select(c => new DrawCard(cardMap == null ? c : cardMap(c))),
                    HmacDeckShuffler.FromSeed(new byte[32]), handler == null ? new AbilityInstance[0] :
                        new[] { new AbilityInstance(new AbilityInstanceId("ability-1"), id, Player, EntityRef.Participant(Player), 1) }, new ResourceState(Chips.Zero, 3));
            }
            public DrawCommand Command(DrawAction action, IEnumerable<CardInstanceId> cards = null) => new DrawCommand("command-" + State.Revision, State.Revision, action, cards);
            public void Do(DrawAction action, IEnumerable<CardInstanceId> cards = null)
            { var result = Engine.Execute(State, Command(action, cards), Player); Assert.IsTrue(result.Accepted, result.Error.ToString()); State = result.State; }
        }
        private sealed class IdentityShuffle : IDeckShuffler
        { public ShuffleResult Shuffle(IReadOnlyList<CardInstanceId> cards, RandomState random) => new ShuffleResult(cards, random); }
        private sealed class ChoiceAbility : IActivatedAbility, ITriggeredAbility, IAbilitySelectionHandler
        {
            public IReadOnlyList<EffectIntent> Plan(AbilityContext context) => new EffectIntent[] { new RequestSelectionEffect(context.Ability.Id, context.Ability.OwnerId,
                new SelectionSpec(SelectionTargetKind.Options, new[] { new SelectionCandidate("ok") }, 1, 1)) };
            public IReadOnlyList<EffectIntent> Resume(AbilityContext context, int step, SelectionResult selection)
            { Assert.AreEqual(1, step); return new EffectIntent[] { new ChangeResourceEffect(context.Ability.Id, context.Ability.OwnerId, ResourceKind.Energy, 2) }; }
        }
        private sealed class BrokenAbility : IActivatedAbility
        {
            public IReadOnlyList<EffectIntent> Plan(AbilityContext c) => new EffectIntent[] {
                new ChangeResourceEffect(c.Ability.Id, c.Ability.OwnerId, ResourceKind.Energy, 10),
                new DrawCardsEffect(c.Ability.Id, DrawEngine.Zone(c.State.Id, CardZoneKinds.Deck), DrawEngine.Zone(c.State.Id, CardZoneKinds.Hand), 999) };
        }
    }
}
