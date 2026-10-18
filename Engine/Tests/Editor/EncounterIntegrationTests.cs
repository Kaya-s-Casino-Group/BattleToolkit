using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class EncounterIntegrationTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player");
        private static RunContent Content(bool drawMode = true, long target = 1, string[] starters = null)
        {
            var sample = ExampleRunContent.Create(drawMode);
            var ids = new[] { "growing", "focus", "exchange", "double", "steady" };
            var offers = new[] {
                new RunOfferDefinition("enhance", RunOfferKind.EnhanceCard, 2, amount: 7, multiplier: 1),
                new RunOfferDefinition("remove", RunOfferKind.RemoveCard, 2),
                new RunOfferDefinition("double", RunOfferKind.Relic, 2, relicId: "double")
            };
            return new RunContent(sample.CardsAndAbilities, new RunDefinition("integration.run.1", ScoreProfile.Default,
                new[] { new RunStageDefinition("first", target, 4), new RunStageDefinition("second", target, 4) },
                offers.Select(o => o.Id), new[] { "enhance" }, starters ?? new[] { "growing", "focus" }, startingGold: 50, offerCount: 3),
                ids.Select(sample.Relic), offers, sample.StartingDeckVersion, sample.StartingDeck);
        }
        private sealed class DrawFixture
        {
            public readonly RunContent Content;
            public readonly DrawRunAdapter Adapter;
            public readonly RunEngine<DrawState, DrawCommand, DrawEvent> Engine;
            public RunState<DrawState> State;
            public IReadOnlyList<CardInstanceId> Hand => State.Encounter.World.Zones.Single(z => z.Kind == CardZoneKinds.Hand).Cards;
            public int Energy => State.Encounter.World.Participants.Single(p => p.Id == Player).Resources.Energy;
            public DrawFixture(long target = 1, string[] starters = null, AbilityRuntime runtime = null)
            {
                Content = EncounterIntegrationTests.Content(target: target, starters: starters);
                Adapter = new DrawRunAdapter(runtime ?? ExampleRunContent.Runtime(true));
                Engine = new RunEngine<DrawState, DrawCommand, DrawEvent>(Content, Adapter);
                State = Engine.Create("draw-run", Player, HmacDeckShuffler.FromSeed(new byte[32]));
            }
            public void Accept(RunTransitionResult<DrawState, DrawEvent> result)
            { Assert.IsTrue(result.Accepted, result.Error + "/" + result.EncounterError); State = result.State; }
            public void Run(RunAction action, string item = null, string target = null, IEnumerable<string> order = null)
            {
                var c = new RunCommand<DrawCommand>("run-" + State.Revision, State.Revision, action, item, target, order);
                Accept(action == RunAction.StartEncounter ? Engine.ExecuteSystem(State, c) : Engine.Execute(State, c, Player));
            }
            public RunCommand<DrawCommand> Command(DrawAction action, IEnumerable<CardInstanceId> cards = null, bool system = false,
                AbilityInstanceId ability = default(AbilityInstanceId), IEnumerable<string> choices = null)
            {
                var s = State.Encounter;
                return new RunCommand<DrawCommand>("outer-" + State.Revision, State.Revision, system ? RunAction.SystemEncounter : RunAction.PlayerEncounter,
                    encounterCommand: new DrawCommand("inner-" + s.Revision, s.Revision, action, cards, s.World.AbilityWindow?.Id,
                        ability, s.World.ActiveDecision?.Id ?? default(DecisionId), choices));
            }
            public void Draw(DrawAction action, IEnumerable<CardInstanceId> cards = null, bool system = false,
                AbilityInstanceId ability = default(AbilityInstanceId), IEnumerable<string> choices = null)
            {
                var c = Command(action, cards, system, ability, choices);
                Accept(system ? Engine.ExecuteSystem(State, c) : Engine.Execute(State, c, Player));
            }
            public void Restore() => State = RunSnapshotCodec<DrawState>.Deserialize(RunSnapshotCodec<DrawState>.Serialize(State, Content, Adapter), Content, Adapter);
            public string Offer(string id) => State.Offers.Single(o => o.DefinitionId == id).Id;
        }

        [Test] public void PreRenameHoldemSnapshotKeepsGrowRelicsAndFrozenScoresAcrossShopActions()
        {
            var content = Content(false, starters: new[] { "growing", "steady" });
            var adapter = new HoldemRunAdapter(ExampleRunContent.Runtime(), new HoldemEncounterSettings(winnersOnly: false));
            var state = RunSnapshotCodec<HoldemEncounterState>.Deserialize(LegacyRelicSnapshots.Holdem, content, adapter);
            Assert.IsTrue(state.Encounter.GrowRelics); Assert.AreEqual(RunPhase.Reward, state.Phase);
            Assert.AreEqual(664, state.StageScore); Assert.AreEqual(664, state.Encounter.LastScore.Total);
            Assert.AreEqual(1, state.Relics.Single(item => item.DefinitionId == "growing").Growth);
            Assert.AreEqual("legacy-holdem/joker/start/0", state.Relics[0].Id);
            Assert.AreEqual(LegacyRelicSnapshots.Holdem, RunSnapshotCodec<HoldemEncounterState>.Serialize(state, content, adapter));
            var engine = new RunEngine<HoldemEncounterState, MatchCommand, MatchEvent>(content, adapter);
            var skipped = engine.Execute(state, new RunCommand<MatchCommand>("shop", state.Revision, RunAction.SkipReward), Player);
            Assert.IsTrue(skipped.Accepted); Assert.AreEqual(RunPhase.Shop, skipped.State.Phase);
            var bought = engine.Execute(skipped.State, new RunCommand<MatchCommand>("buy", skipped.State.Revision, RunAction.Buy,
                skipped.State.Offers.Single(offer => offer.DefinitionId == "double").Id), Player);
            Assert.IsTrue(bought.Accepted); Assert.AreEqual(3, bought.State.Relics.Count);
            StringAssert.Contains("/joker/", bought.State.Relics.Single(item => item.DefinitionId == "double").Id);
            Assert.AreEqual(664, bought.State.Scores.Single().Total); Assert.IsTrue(bought.State.Encounter.GrowRelics);
            Assert.AreEqual(1, bought.State.Relics.Single(item => item.DefinitionId == "growing").Growth);
        }

        [Test] public void SameRunBuildAndRewardsComposeWithBothEncounterModes()
        {
            var content = Content(false, starters: new[] { "growing", "steady" });
            var holdemAdapter = new HoldemRunAdapter(ExampleRunContent.Runtime());
            var drawAdapter = new DrawRunAdapter(ExampleRunContent.Runtime(true));
            var holdem = new RunEngine<HoldemEncounterState, MatchCommand, MatchEvent>(content, holdemAdapter);
            var draw = new RunEngine<DrawState, DrawCommand, DrawEvent>(content, drawAdapter);
            var h = holdem.Create("same-run", Player, HmacDeckShuffler.FromSeed(new byte[32]));
            var d = draw.Create("same-run", Player, HmacDeckShuffler.FromSeed(new byte[32]));
            CollectionAssert.AreEqual(h.Deck.Select(c => c.Id), d.Deck.Select(c => c.Id));
            CollectionAssert.AreEqual(h.Relics.Select(j => j.DefinitionId), d.Relics.Select(j => j.DefinitionId));
            var hs = holdem.ExecuteSystem(h, new RunCommand<MatchCommand>("start", h.Revision, RunAction.StartEncounter)); Assert.IsTrue(hs.Accepted); h = hs.State;
            int guard = 0;
            while (h.Phase == RunPhase.Encounter)
            {
                Assert.Less(++guard, 20); var m = h.Encounter.Match;
                RunTransitionResult<HoldemEncounterState, MatchEvent> r;
                if (m.AbilityWindow.Timing == HoldemTiming.BeforeBettingAction)
                    r = holdem.Execute(h, new RunCommand<MatchCommand>("bet", h.Revision, RunAction.PlayerEncounter,
                        encounterCommand: new BettingCommand("bet", m.Revision, m.ActiveDecision.Id,
                            m.ActiveDecision.ActorId == Player ? BettingAction.Call : BettingAction.Fold)), m.ActiveDecision.ActorId);
                else r = holdem.ExecuteSystem(h, new RunCommand<MatchCommand>("advance", h.Revision, RunAction.SystemEncounter,
                    encounterCommand: new AdvanceWindowCommand("advance", m.Revision, m.AbilityWindow.Id)));
                Assert.IsTrue(r.Accepted, r.Error + "/" + r.EncounterError); h = r.State;
            }
            var ds = draw.ExecuteSystem(d, new RunCommand<DrawCommand>("start", d.Revision, RunAction.StartEncounter)); Assert.IsTrue(ds.Accepted); d = ds.State;
            var played = draw.Execute(d, new RunCommand<DrawCommand>("play", d.Revision, RunAction.PlayerEncounter,
                encounterCommand: new DrawCommand("play", d.Encounter.Revision, DrawAction.Play,
                    d.Encounter.World.Zones.Single(z => z.Kind == CardZoneKinds.Hand).Cards.Take(1))), Player);
            Assert.IsTrue(played.Accepted, played.Error + "/" + played.EncounterError); d = played.State;
            Assert.AreEqual(RunPhase.Reward, h.Phase); Assert.AreEqual(RunPhase.Reward, d.Phase);
            Assert.AreEqual(h.Gold, d.Gold); Assert.AreEqual(h.RoundsScored, d.RoundsScored);
            Assert.AreEqual(0, h.Relics.First().Growth, "Fold wins follow the Holdem score policy.");
            Assert.AreEqual(1, d.Relics.First().Growth, "Draw scoring advances the same Run growth mechanism.");
        }
        [Test] public void DrawRunCompletesRewardsShopUpgradeAndNextEncounterAfterRestoring()
        {
            var f = new DrawFixture(); f.Run(RunAction.StartEncounter); f.Draw(DrawAction.Play, f.Hand.Take(1));
            Assert.AreEqual(RunPhase.Reward, f.State.Phase); Assert.AreEqual(55, f.State.Gold);
            Assert.AreEqual(1, f.State.Relics.Single(j => j.DefinitionId == "growing").Growth);
            Assert.AreEqual(1, f.State.Relics.Single(j => j.DefinitionId == "focus").Memory.CounterValue("calls"));
            string enhanced = f.State.Deck[0].Id;
            f.Run(RunAction.ChooseReward, f.State.Offers.Single().Id, enhanced);
            f.Run(RunAction.Buy, f.Offer("double"));
            string doubleId = f.State.Relics.Single(j => j.DefinitionId == "double").Id;
            f.Run(RunAction.UpgradeRelic, doubleId); f.Run(RunAction.ReorderRelics, order: f.State.Relics.Reverse().Select(j => j.Id));
            f.Restore(); f.Run(RunAction.StartEncounter);
            Assert.AreEqual(1, f.State.StageIndex); Assert.AreEqual(0, f.State.StageScore);
            Assert.AreEqual(7, f.State.Encounter.Cards.Single(c => c.Card.OriginId == enhanced).BonusPoints);
            Assert.AreEqual(2, f.State.Relics.Single(j => j.Id == doubleId).Level);
            f.Draw(DrawAction.Play, f.Hand.Take(1)); f.Run(RunAction.SkipReward); f.Restore();
            Assert.AreEqual(RunPhase.Completed, f.State.Phase); Assert.AreEqual(2, f.State.Scores.Count);
            Assert.AreEqual(2, f.State.Relics.Single(j => j.DefinitionId == "growing").Growth);
            Assert.AreEqual(2, f.State.Relics.Single(j => j.DefinitionId == "focus").Memory.CounterValue("calls"));
        }
        [Test] public void DrawRunRestoresPaidCardSelectionAndResumesWithoutDoubleCost()
        {
            var f = new DrawFixture(starters: new[] { "exchange" }); f.Run(RunAction.StartEncounter);
            var old = f.Hand.ToArray(); var ability = f.State.Encounter.World.Abilities.Single().Id;
            f.Draw(DrawAction.ActivateAbility, ability: ability); Assert.AreEqual(2, f.Energy); f.Restore();
            Assert.AreEqual(DecisionKind.SelectTargets, f.State.Encounter.World.ActiveDecision.Kind);
            f.Draw(DrawAction.SelectAbilityTargets, choices: new[] { "choice-0" });
            Assert.AreEqual(2, f.Energy); Assert.IsNull(f.State.Encounter.World.ActiveDecision);
            Assert.AreEqual(8, f.Hand.Count); Assert.IsFalse(f.Hand.Contains(old[0]));
            Assert.AreEqual(old[0], f.State.Encounter.World.Zones.Single(z => z.Kind == CardZoneKinds.Discard).Cards.Single());
            Assert.AreEqual(1, f.State.Encounter.World.Abilities.Single().UsesThisRound);
        }
        [Test] public void DrawSelectionTimeoutUsesAuthorityRouteAndRejectsPlayerOrRepeatedTimeout()
        {
            var f = new DrawFixture(starters: new[] { "exchange" }); f.Run(RunAction.StartEncounter);
            f.Draw(DrawAction.ActivateAbility, ability: f.State.Encounter.World.Abilities.Single().Id); f.Restore();
            var before = f.State; var denied = f.Engine.Execute(before, f.Command(DrawAction.Timeout), Player);
            Assert.AreEqual(RunError.EncounterRejected, denied.Error); Assert.AreSame(before, denied.State); Assert.AreEqual(0, denied.EncounterEvents.Count);
            var timeout = f.Command(DrawAction.Timeout, system: true); f.Accept(f.Engine.ExecuteSystem(f.State, timeout));
            Assert.IsNull(f.State.Encounter.World.ActiveDecision); Assert.AreEqual(2, f.Energy);
            Assert.AreEqual(RunError.StaleRevision, f.Engine.ExecuteSystem(f.State, timeout).Error);
        }
        [Test] public void DrawCheckpointFreezesScoreBeforeGrowthAndDoesNotHarvestOnContinue()
        {
            var f = new DrawFixture(target: 100000); f.Run(RunAction.StartEncounter); f.Draw(DrawAction.Play, f.Hand.Take(1));
            var growing = f.State.Relics.Single(j => j.DefinitionId == "growing"); var first = f.State.Scores.Single();
            Assert.AreEqual(10, first.Steps.Single(s => s.SourceId == growing.Id).Amount); Assert.AreEqual(1, growing.Growth);
            f.Restore(); Assert.AreEqual(first.Total, f.State.Encounter.Scores.Single().Total);
            f.Draw(DrawAction.Continue, system: true); Assert.AreEqual(1, f.State.RoundsScored); Assert.AreEqual(first.Total, f.State.StageScore);
            f.Draw(DrawAction.Play, f.Hand.Take(1));
            Assert.AreEqual(15, f.State.Scores[1].Steps.Single(s => s.SourceId == growing.Id).Amount);
            Assert.AreEqual(2, f.State.Relics.Single(j => j.Id == growing.Id).Growth); Assert.AreEqual(50, f.State.Gold);
        }
        [Test] public void DrawShopCanRemoveScoringCardAndSellSourceRelicWithoutRecomputingHistory()
        {
            var f = new DrawFixture(); f.Run(RunAction.StartEncounter); var selected = f.Hand[0];
            string origin = f.State.Encounter.Cards.Single(c => c.Card.Id == selected).Card.OriginId;
            f.Draw(DrawAction.Play, new[] { selected }); var recorded = f.State.Scores.Single();
            string growing = f.State.Relics.Single(j => j.DefinitionId == "growing").Id;
            f.Run(RunAction.SkipReward); f.Run(RunAction.Sell, growing); f.Run(RunAction.Buy, f.Offer("remove"), origin); f.Restore();
            Assert.IsFalse(f.State.Deck.Any(c => c.Id == origin)); Assert.IsFalse(f.State.Relics.Any(j => j.Id == growing));
            Assert.IsTrue(f.State.Encounter.Cards.Any(c => c.Card.OriginId == origin));
            Assert.AreEqual(recorded.Total, f.State.Scores.Single().Total); Assert.AreEqual(recorded.Total, f.State.Encounter.Scores.Single().Total);
            Assert.IsTrue(f.State.Scores.Single().Steps.Any(s => s.SourceId == growing));
            f.Run(RunAction.StartEncounter); Assert.IsFalse(f.State.Encounter.Cards.Any(c => c.Card.OriginId == origin));
        }
        [Test] public void DeckMinimumIsEightForDrawAndTwelveForHoldem()
        {
            var c = Content(starters: new string[0]); var cards = c.StartingDeck.Take(8).Select((id, i) => new RunCard("small/" + i, id)).ToArray();
            var drawAdapter = new DrawRunAdapter(ExampleRunContent.Runtime(true)); var holdemAdapter = new HoldemRunAdapter(ExampleRunContent.Runtime());
            var draw = new RunEngine<DrawState, DrawCommand, DrawEvent>(c, drawAdapter);
            var state = draw.Create("small-draw", Player, deck: cards);
            Assert.AreEqual(8, drawAdapter.MinimumDeckSize); Assert.AreEqual(12, holdemAdapter.MinimumDeckSize);
            Assert.IsTrue(draw.ExecuteSystem(state, new RunCommand<DrawCommand>("start", 0, RunAction.StartEncounter)).Accepted);
            var holdem = new RunEngine<HoldemEncounterState, MatchCommand, MatchEvent>(c, holdemAdapter);
            Assert.Throws<StateValidationException>(() => holdem.Create("small-holdem", Player, deck: cards));
            Assert.AreEqual(12, holdem.Create("twelve", Player, deck: c.StartingDeck.Take(12).Select((id, i) => new RunCard("valid/" + i, id))).Deck.Count);
        }
        [Test] public void TypedRunSnapshotsCannotCrossEncounterModes()
        {
            var f = new DrawFixture(); f.Run(RunAction.StartEncounter);
            string drawJson = RunSnapshotCodec<DrawState>.Serialize(f.State, f.Content, f.Adapter);
            var holdemAdapter = new HoldemRunAdapter(ExampleRunContent.Runtime(true));
            Assert.Throws<SnapshotException>(() => RunSnapshotCodec<HoldemEncounterState>.Deserialize(drawJson, f.Content, holdemAdapter));
            var engine = new RunEngine<HoldemEncounterState, MatchCommand, MatchEvent>(f.Content, holdemAdapter);
            string holdemJson = RunSnapshotCodec<HoldemEncounterState>.Serialize(engine.Create("holdem", Player), f.Content, holdemAdapter);
            Assert.Throws<SnapshotException>(() => RunSnapshotCodec<DrawState>.Deserialize(holdemJson, f.Content, f.Adapter));
        }
        private sealed class PostScoreChoice : ITriggeredAbility, IAbilitySelectionHandler
        {
            public IReadOnlyList<EffectIntent> Plan(AbilityContext c) => new EffectIntent[] {
                new RequestSelectionEffect(c.Ability.Id, c.Ability.OwnerId, new SelectionSpec(SelectionTargetKind.Options,
                    new[] { new SelectionCandidate("ok") }, 1, 1)) };
            public IReadOnlyList<EffectIntent> Resume(AbilityContext c, int step, SelectionResult selection) => new EffectIntent[0];
        }
        [Test] public void DrawRunDefersRewardAndGrowthUntilPostScoreChoiceFinishes()
        {
            var handler = new PostScoreChoice();
            var f = new DrawFixture(runtime: new AbilityRuntime(new[] { new AbilityBehavior(ExampleRunContent.CallEnergy, triggered: handler, selection: handler) }));
            f.Run(RunAction.StartEncounter); f.Draw(DrawAction.Play, f.Hand.Take(1));
            Assert.AreEqual(DrawPhase.AfterScoring, f.State.Encounter.Phase); Assert.Greater(f.State.Encounter.Score, 0);
            Assert.AreEqual(0, f.State.StageScore); Assert.AreEqual(0, f.State.Scores.Count); Assert.AreEqual(RunPhase.Encounter, f.State.Phase);
            Assert.AreEqual(0, f.State.Relics.Single(j => j.DefinitionId == "growing").Growth); f.Restore();
            f.Draw(DrawAction.Timeout, system: true);
            Assert.AreEqual(RunPhase.Reward, f.State.Phase); Assert.AreEqual(1, f.State.Scores.Count);
            Assert.AreEqual(1, f.State.Relics.Single(j => j.DefinitionId == "growing").Growth);
        }
    }
}
