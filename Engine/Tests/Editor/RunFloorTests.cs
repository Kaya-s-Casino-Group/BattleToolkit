using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class RunFloorTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player");
        private static RunEncounterNodeDefinition Combat(string id, string[] next = null, RunNodeKind kind = RunNodeKind.Encounter,
            long target = 1, int rounds = 4) => new RunEncounterNodeDefinition(id, new RunStageDefinition(id, target, rounds), next, kind);
        private static RunFloorDefinition[] FourFloors()
        {
            var floors = new List<RunFloorDefinition>();
            for (int i = 1; i <= 3; i++) floors.Add(new RunFloorDefinition("floor-" + i, new RunNodeTree("tree.1", "start", new RunNodeDefinition[] {
                Combat("start", new[] { "gift" }),
                new RunRewardNodeDefinition("gift", new[] { "normal", "elite" }, new[] { "pair-level" }),
                Combat("normal", new[] { "shop" }), Combat("elite", new[] { "shop" }, RunNodeKind.Elite),
                new RunShopNodeDefinition("shop", offerPool: new[] { "enhance" })
            })));
            floors.Add(new RunFloorDefinition("boss-floor", new RunNodeTree("boss.1", "boss", new RunNodeDefinition[] {
                Combat("boss", new[] { "final-gift" }, RunNodeKind.Boss), new RunRewardNodeDefinition("final-gift")
            }), RunFloorKind.Boss));
            return floors.ToArray();
        }
        private static RunContent Content(IEnumerable<RunFloorDefinition> floors)
        {
            var sample = ExampleRunContent.Create(true);
            var definition = new RunDefinition("floors.run.1", ScoreProfile.Default, floors, new[] { "enhance" }, new[] { "gold" },
                new[] { "growing" }, startingGold: 50);
            return new RunContent(sample.CardsAndAbilities, definition, new[] { sample.Relic("growing") },
                new[] { sample.Offer("enhance"), sample.Offer("gold"), sample.Offer("pair-level") }, sample.StartingDeckVersion, sample.StartingDeck);
        }
        private sealed class Fixture
        {
            public readonly RunContent Content;
            public readonly DrawRunAdapter Adapter = new DrawRunAdapter(ExampleRunContent.Runtime(true));
            public readonly RunEngine<DrawState, DrawCommand, DrawEvent> Engine;
            public RunState<DrawState> State;
            public Fixture(IEnumerable<RunFloorDefinition> floors = null)
            {
                Content = RunFloorTests.Content(floors ?? FourFloors()); Engine = new RunEngine<DrawState, DrawCommand, DrawEvent>(Content, Adapter);
                State = Engine.Create("floors-run", Player, HmacDeckShuffler.FromSeed(new byte[32]));
            }
            public void Apply(RunAction action, string next = null, string item = null, string target = null)
            {
                var command = new RunCommand<DrawCommand>("run-" + State.Revision, State.Revision, action, item, target, nextNodeId: next);
                var result = action == RunAction.StartRun ? Engine.ExecuteSystem(State, command) : Engine.Execute(State, command, Player);
                Assert.IsTrue(result.Accepted, result.Error + "/" + result.EncounterError); State = result.State;
            }
            public RunTransitionResult<DrawState, DrawEvent> Play()
            {
                var hand = State.Encounter.World.Zones.Single(z => z.Kind == CardZoneKinds.Hand).Cards;
                var command = new DrawCommand("draw-" + State.Encounter.Revision, State.Encounter.Revision, DrawAction.Play, hand.Take(1));
                var result = Engine.Execute(State, new RunCommand<DrawCommand>("run-" + State.Revision, State.Revision,
                    RunAction.PlayerEncounter, encounterCommand: command), Player);
                Assert.IsTrue(result.Accepted, result.Error + "/" + result.EncounterError); State = result.State; return result;
            }
            public string Json() => RunSnapshotCodec<DrawState>.Serialize(State, Content, Adapter);
            public void Restore() => State = RunSnapshotCodec<DrawState>.Deserialize(Json(), Content, Adapter);
        }

        [Test] public void ThreeBranchingFloorsAndSeparateBossShareBuildAndCompleteOnlyAfterFinalReward()
        {
            var f = new Fixture(); f.Restore(); f.Apply(RunAction.StartRun);
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(i, f.State.FloorIndex); Assert.AreEqual(RunPhase.Encounter, f.State.Phase);
                Assert.AreEqual("floor-" + (i + 1), RunEncounterContext.From(f.State, f.Content).Floor.Id);
                Assert.AreEqual(i, RunEncounterContext.From(f.State, f.Content).FloorIndex);
                Assert.IsTrue(f.State.Floors.Take(i).All(s => s.IsCompleted));
                Assert.IsTrue(f.State.Floors.Skip(i + 1).All(s => !s.IsCompleted && s.Nodes.Path.Count == 0));
                if (i > 0) Assert.AreEqual(10, f.State.Encounter.Cards.First().BonusPoints);
                f.Play(); f.Apply(RunAction.ChooseReward, item: f.State.Offers.Single().Id); f.Restore();
                Assert.AreEqual(RunPhase.NodeChoice, f.State.Phase);
                CollectionAssert.AreEqual(new[] { "normal", "elite" }, f.Engine.GetAvailableNextNodes(f.State).Select(n => n.Id));
                string route = i == 1 ? "elite" : "normal";
                f.Apply(RunAction.ChooseNextNode, route); f.Play(); f.Restore();
                Assert.AreEqual(i, f.State.FloorIndex); Assert.AreEqual("shop", f.State.Nodes.CurrentNodeId);
                if (i == 0) f.Apply(RunAction.Buy, item: f.State.Offers.Single().Id, target: f.State.Deck[0].Id);
                var path = f.State.Nodes.Path.ToArray(); var completed = f.State.Nodes.CompletedNodeIds.ToArray();
                Assert.IsFalse(path.Contains(route == "normal" ? "elite" : "normal"));
                f.Apply(RunAction.CompleteNode); f.Restore();
                Assert.AreEqual(i + 1, f.State.FloorIndex); Assert.AreEqual(RunPhase.Encounter, f.State.Phase);
                CollectionAssert.AreEqual(path, f.State.Floors[i].Nodes.Path);
                CollectionAssert.AreEqual(completed.Concat(new[] { "shop" }), f.State.Floors[i].Nodes.CompletedNodeIds);
                Assert.IsTrue(f.State.Floors[i].IsCompleted);
            }
            Assert.AreEqual(RunFloorKind.Boss, f.Content.Definition.Floors[3].Kind);
            Assert.AreEqual(RunNodeKind.Boss, RunEncounterContext.From(f.State, f.Content).Node.Kind);
            Assert.AreEqual(4, f.State.HandLevels.Single(l => l.Category == HandCategory.OnePair).Level);
            f.Play(); Assert.AreEqual(RunPhase.Reward, f.State.Phase); Assert.IsFalse(f.State.Floors[3].IsCompleted);
            Assert.AreEqual(7, f.State.Scores.Count); Assert.AreEqual(7, f.State.Relics.Single().Growth);
            var gold = f.State.Gold; f.Apply(RunAction.ChooseReward, item: f.State.Offers.Single().Id); f.Restore();
            Assert.AreEqual(gold + 3, f.State.Gold); Assert.AreEqual(RunPhase.Completed, f.State.Phase);
            Assert.IsTrue(f.State.Floors.All(s => s.IsCompleted));
            Assert.AreEqual(7, f.State.Scores.Select(s => s.Id).Distinct().Count());
        }

        [Test] public void FloorAndNodeNamespacesPermitRepeatedLocalIdsWithoutMixingScores()
        {
            var tree = new RunNodeTree("same-tree.1", "battle", new[] { Combat("battle") });
            var f = new Fixture(new[] { new RunFloorDefinition("first", tree), new RunFloorDefinition("second", tree) });
            f.Apply(RunAction.StartRun); var first = f.State.Encounter.Id;
            var transition = f.Play(); Assert.AreEqual(1, f.State.FloorIndex); var second = f.State.Encounter.Id;
            Assert.AreNotEqual(first, second); Assert.AreEqual(1, f.State.Scores.Count); Assert.AreEqual(0, f.State.StageScore);
            CollectionAssert.AreEqual(transition.EncounterEvents, transition.EventsFor(Player));
            f.Restore(); f.Play(); f.Restore(); Assert.AreEqual(2, f.State.Scores.Count);
            Assert.IsTrue(f.State.Scores[0].Id.StartsWith(first + "/", StringComparison.Ordinal));
            Assert.IsTrue(f.State.Scores[1].Id.StartsWith(second + "/", StringComparison.Ordinal));
            Assert.IsTrue(f.State.Floors.All(s => s.Nodes.CurrentNodeId == "battle" && s.IsCompleted));
        }

        [Test] public void NoncombatFloorsRetainFrozenEncounterUntilTheNextCombatStarts()
        {
            var f = new Fixture(new[] {
                new RunFloorDefinition("first", new RunNodeTree("first.1", "battle", new[] { Combat("battle") })),
                new RunFloorDefinition("shop", new RunNodeTree("shop.1", "shop", new[] { new RunShopNodeDefinition("shop") })),
                new RunFloorDefinition("gift", new RunNodeTree("gift.1", "gift", new[] { new RunRewardNodeDefinition("gift") })),
                new RunFloorDefinition("boss", new RunNodeTree("boss.1", "boss", new[] { Combat("boss", kind: RunNodeKind.Boss) }), RunFloorKind.Boss)
            });
            f.Apply(RunAction.StartRun); f.Play(); var frozen = f.State.Encounter; var total = f.State.StageScore;
            Assert.AreEqual(1, f.State.FloorIndex); Assert.AreEqual(0, RunEncounterContext.From(f.State, f.Content).FloorIndex);
            f.Apply(RunAction.Buy, item: f.State.Offers.Single().Id, target: f.State.Deck[0].Id); f.Restore();
            f.Apply(RunAction.CompleteNode); Assert.AreEqual(2, f.State.FloorIndex); f.Restore();
            Assert.AreEqual(frozen.Id, f.State.Encounter.Id); Assert.AreEqual(total, f.State.StageScore); Assert.AreEqual(1, f.State.Scores.Count);
            f.Apply(RunAction.SkipReward); Assert.AreEqual(3, f.State.FloorIndex); Assert.AreEqual(1, f.State.StageIndex);
            Assert.AreEqual(10, f.State.Encounter.Cards.First().BonusPoints); f.Play(); f.Restore(); Assert.AreEqual(RunPhase.Completed, f.State.Phase);
        }

        [Test] public void EntireRunCanTraverseNoncombatFloorsWithoutAnEncounter()
        {
            var f = new Fixture(new[] {
                new RunFloorDefinition("shop", new RunNodeTree("shop.1", "shop", new[] { new RunShopNodeDefinition("shop") })),
                new RunFloorDefinition("gift", new RunNodeTree("gift.1", "gift", new[] { new RunRewardNodeDefinition("gift") }))
            });
            f.Apply(RunAction.StartRun); f.Apply(RunAction.CompleteNode); f.Restore();
            Assert.AreEqual(1, f.State.FloorIndex); Assert.IsNull(f.State.Encounter); Assert.AreEqual(-1, f.State.StageIndex);
            f.Apply(RunAction.SkipReward); f.Restore(); Assert.AreEqual(RunPhase.Completed, f.State.Phase); Assert.IsEmpty(f.State.Scores);
        }

        [Test] public void FailedFloorStopsRunAndLeavesFutureTreesUnvisited()
        {
            var f = new Fixture(new[] {
                new RunFloorDefinition("shop", new RunNodeTree("shop.1", "shop", new[] { new RunShopNodeDefinition("shop") })),
                new RunFloorDefinition("elite", new RunNodeTree("elite.1", "elite", new[] { Combat("elite", kind: RunNodeKind.Elite, target: 1000000, rounds: 1) })),
                new RunFloorDefinition("boss", new RunNodeTree("boss.1", "boss", new[] { Combat("boss", kind: RunNodeKind.Boss) }), RunFloorKind.Boss)
            });
            f.Apply(RunAction.StartRun); f.Apply(RunAction.CompleteNode); f.Play(); f.Restore();
            Assert.AreEqual(RunPhase.Failed, f.State.Phase); Assert.AreEqual(1, f.State.FloorIndex);
            Assert.IsTrue(f.State.Floors[0].IsCompleted); Assert.IsFalse(f.State.Floors[1].IsCompleted);
            Assert.IsFalse(f.State.Floors[2].IsCompleted); Assert.IsEmpty(f.State.Floors[2].Nodes.Path);
            var denied = f.Engine.Execute(f.State, new RunCommand<DrawCommand>("escape", f.State.Revision, RunAction.CompleteNode), Player);
            Assert.IsFalse(denied.Accepted); Assert.AreSame(f.State, denied.State);
        }

        [Test] public void FutureFloorSelectionForgedCompletionAndReorderedFloorsAreRejected()
        {
            var f = new Fixture(); f.Apply(RunAction.StartRun); f.Play(); f.Apply(RunAction.SkipReward);
            var before = f.State;
            var skip = f.Engine.Execute(before, new RunCommand<DrawCommand>("boss", before.Revision, RunAction.ChooseNextNode, nextNodeId: "boss"), Player);
            Assert.AreEqual(RunError.InvalidTarget, skip.Error); Assert.AreSame(before, skip.State);
            var json = f.Json();
            foreach (var forged in new[] {
                json.Replace("\"FloorIndex\":0", "\"FloorIndex\":2"),
                json.Replace("\"FloorId\":\"floor-1\"", "\"FloorId\":\"boss-floor\""),
                json.Replace("\"IsCompleted\":false", "\"IsCompleted\":true") })
            {
                Assert.IsTrue(json != forged); Assert.Throws<SnapshotException>(() => RunSnapshotCodec<DrawState>.Deserialize(forged, f.Content, f.Adapter));
            }
            Assert.Throws<SnapshotException>(() => RunSnapshotCodec<DrawState>.Deserialize(json, Content(FourFloors().Reverse()), f.Adapter));
            for (int version = 1; version < RunSnapshotCodec<DrawState>.SchemaVersion; version++)
            {
                var old = json.Replace("\"SchemaVersion\":" + RunSnapshotCodec<DrawState>.SchemaVersion, "\"SchemaVersion\":" + version);
                Assert.Throws<SnapshotException>(() => RunSnapshotCodec<DrawState>.Deserialize(old, f.Content, f.Adapter));
            }
        }

        [Test] public void FutureTreeConfigurationIsPinnedBeforeThatFloorHasBeenEntered()
        {
            var f = new Fixture(); f.Apply(RunAction.StartRun); var json = f.Json(); var floors = FourFloors();
            floors[3] = new RunFloorDefinition("boss-floor", new RunNodeTree("boss.1", "boss", new RunNodeDefinition[] {
                Combat("boss", new[] { "final-gift" }, RunNodeKind.Boss, target: 2), new RunRewardNodeDefinition("final-gift") }), RunFloorKind.Boss);
            Assert.Throws<SnapshotException>(() => RunSnapshotCodec<DrawState>.Deserialize(json, Content(floors), f.Adapter));
        }

        [Test] public void FloorDefinitionsRejectEmptyOrDuplicateFloorsAndCrossTreeEdges()
        {
            Assert.Throws<ArgumentException>(() => Content(new RunFloorDefinition[0]));
            var floor = FourFloors()[0]; Assert.Throws<ArgumentException>(() => Content(new[] { floor, floor }));
            Assert.Throws<ArgumentException>(() => new RunNodeTree("v", "shop", new[] { new RunShopNodeDefinition("shop", new[] { "other-floor/boss" }) }));
            Assert.Throws<ArgumentException>(() => new RunFloorDefinition("bad", floor.NodeTree, (RunFloorKind)0));
        }

        [Test] public void FourFloorExampleStartsBothEncounterModesWithIndependentTrees()
        {
            foreach (bool drawMode in new[] { true, false })
            {
                var content = ExampleRunContent.CreateFourFloors(drawMode); Assert.AreEqual(4, content.Definition.Floors.Count);
                Assert.AreEqual(3, content.Definition.Floors.Count(f => f.Kind == RunFloorKind.Normal));
                Assert.AreEqual(RunFloorKind.Boss, content.Definition.Floors[3].Kind);
                Assert.AreEqual(4, content.Definition.Floors.Select(f => f.NodeTree).Distinct().Count());
                if (drawMode)
                {
                    var adapter = new DrawRunAdapter(ExampleRunContent.Runtime(true));
                    var engine = new RunEngine<DrawState, DrawCommand, DrawEvent>(content, adapter); var state = engine.Create("draw", Player);
                    Assert.IsTrue(engine.ExecuteSystem(state, new RunCommand<DrawCommand>("start", state.Revision, RunAction.StartRun)).Accepted);
                }
                else
                {
                    var adapter = new HoldemRunAdapter(ExampleRunContent.Runtime());
                    var engine = new RunEngine<HoldemEncounterState, MatchCommand, MatchEvent>(content, adapter); var state = engine.Create("holdem", Player);
                    Assert.IsTrue(engine.ExecuteSystem(state, new RunCommand<MatchCommand>("start", state.Revision, RunAction.StartRun)).Accepted);
                }
            }
        }
    }
}
