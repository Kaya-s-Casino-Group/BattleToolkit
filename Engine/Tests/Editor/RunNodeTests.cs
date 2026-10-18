using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class RunNodeTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player");
        private static RunEncounterNodeDefinition Combat(string id, string[] next = null, RunNodeKind kind = RunNodeKind.Encounter,
            long target = 1, int rounds = 4) => new RunEncounterNodeDefinition(id, new RunStageDefinition(id, target, rounds), next, kind);
        private static RunNodeTree Branches() => new RunNodeTree("tree.1", "entry", new RunNodeDefinition[] {
            // Content ordering deliberately differs from traversal order; a branch can move to a lower stage index.
            Combat("boss", kind: RunNodeKind.Boss),
            Combat("entry", new[] { "reward" }),
            new RunRewardNodeDefinition("reward", new[] { "normal", "elite" }),
            Combat("normal", new[] { "shop" }),
            Combat("elite", new[] { "elite-reward" }, RunNodeKind.Elite),
            new RunRewardNodeDefinition("elite-reward", new[] { "shop" }, new[] { "pair-level" }),
            new RunShopNodeDefinition("shop", new[] { "boss" }, new[] { "enhance" })
        });
        private static RunContent Content(RunNodeTree tree)
        {
            var sample = ExampleRunContent.Create(true);
            return new RunContent(sample.CardsAndAbilities, new RunDefinition("nodes.run.1", ScoreProfile.Default, tree,
                new[] { "remove" }, new[] { "gold" }, startingGold: 50), new RelicDefinition[0],
                new[] { sample.Offer("remove"), sample.Offer("gold"), sample.Offer("pair-level"), sample.Offer("enhance") }, sample.StartingDeckVersion, sample.StartingDeck);
        }
        private sealed class Fixture
        {
            public readonly RunContent Content;
            public readonly DrawRunAdapter Adapter = new DrawRunAdapter(ExampleRunContent.Runtime(true));
            public readonly RunEngine<DrawState, DrawCommand, DrawEvent> Engine;
            public RunState<DrawState> State;
            public Fixture(RunNodeTree tree = null)
            {
                Content = RunNodeTests.Content(tree ?? Branches()); Engine = new RunEngine<DrawState, DrawCommand, DrawEvent>(Content, Adapter);
                State = Engine.Create("nodes-run", Player, HmacDeckShuffler.FromSeed(new byte[32]));
            }
            public RunTransitionResult<DrawState, DrawEvent> Apply(RunAction action, string next = null, string item = null, string target = null)
            {
                var command = new RunCommand<DrawCommand>("run-" + State.Revision, State.Revision, action, item, target, nextNodeId: next);
                var result = action == RunAction.StartRun || action == RunAction.StartEncounter
                    ? Engine.ExecuteSystem(State, command) : Engine.Execute(State, command, Player);
                Assert.IsTrue(result.Accepted, result.Error + "/" + result.EncounterError); State = result.State; return result;
            }
            public RunTransitionResult<DrawState, DrawEvent> Play()
            {
                var hand = State.Encounter.World.Zones.Single(z => z.Kind == CardZoneKinds.Hand).Cards;
                var command = new DrawCommand("draw-" + State.Encounter.Revision, State.Encounter.Revision, DrawAction.Play, hand.Take(1));
                var result = Engine.Execute(State, new RunCommand<DrawCommand>("run-" + State.Revision, State.Revision,
                    RunAction.PlayerEncounter, encounterCommand: command), Player);
                Assert.IsTrue(result.Accepted, result.Error + "/" + result.EncounterError); State = result.State; return result;
            }
            public void Restore() => State = RunSnapshotCodec<DrawState>.Deserialize(RunSnapshotCodec<DrawState>.Serialize(State, Content, Adapter), Content, Adapter);
            public void ToBranch()
            {
                Apply(RunAction.StartRun); Play(); Assert.AreEqual("reward", State.Nodes.CurrentNodeId);
                Apply(RunAction.SkipReward); Assert.AreEqual(RunPhase.NodeChoice, State.Phase);
            }
        }

        [Test] public void NormalAndEliteBranchesMergeAndFinishAtBossWithoutVisitingTheOtherBranch()
        {
            foreach (var route in new[] { "normal", "elite" })
            {
                var f = new Fixture(); f.Restore(); f.ToBranch();
                CollectionAssert.AreEqual(new[] { "normal", "elite" }, f.Engine.GetAvailableNextNodes(f.State).Select(n => n.Id));
                f.Restore(); f.Apply(RunAction.ChooseNextNode, route);
                Assert.AreEqual(route == "elite" ? RunNodeKind.Elite : RunNodeKind.Encounter, RunEncounterContext.From(f.State, f.Content).Node.Kind);
                f.Play();
                if (route == "elite")
                {
                    Assert.AreEqual("elite-reward", f.State.Nodes.CurrentNodeId);
                    Assert.AreEqual("pair-level", f.State.Offers.Single().DefinitionId);
                    f.Apply(RunAction.ChooseReward, item: f.State.Offers.Single().Id);
                    Assert.AreEqual(2, f.State.HandLevels.Single(l => l.Category == HandCategory.OnePair).Level);
                }
                Assert.AreEqual("shop", f.State.Nodes.CurrentNodeId); Assert.AreEqual("enhance", f.State.Offers.Single().DefinitionId);
                f.Restore(); var gold = f.State.Gold;
                f.Apply(RunAction.Buy, item: f.State.Offers.Single().Id, target: f.State.Deck[0].Id);
                Assert.AreEqual(10, f.State.Deck[0].BonusPoints); Assert.AreEqual(gold - 3, f.State.Gold);
                f.Apply(RunAction.CompleteNode); Assert.AreEqual(0, f.State.StageIndex);
                Assert.AreEqual(RunNodeKind.Boss, RunEncounterContext.From(f.State, f.Content).Node.Kind);
                Assert.AreEqual(0, f.State.StageScore); Assert.AreEqual(10, f.State.Encounter.Cards.First().BonusPoints);
                f.Play(); f.Restore();
                Assert.AreEqual(RunPhase.Completed, f.State.Phase); Assert.AreEqual(3, f.State.Scores.Count);
                CollectionAssert.AreEqual(f.State.Nodes.Path, f.State.Nodes.CompletedNodeIds);
                Assert.IsFalse(f.State.Nodes.Path.Contains(route == "elite" ? "normal" : "elite"));
                Assert.IsEmpty(f.Engine.GetAvailableNextNodes(f.State));
            }
        }

        [Test] public void InvalidUnauthorizedAndStaleBranchCommandsDoNotChangeStateOrRandomness()
        {
            var f = new Fixture(); f.ToBranch(); var before = f.State;
            var wrongEdge = f.Engine.Execute(before, new RunCommand<DrawCommand>("skip", before.Revision, RunAction.ChooseNextNode, nextNodeId: "boss"), Player);
            var wrongActor = f.Engine.Execute(before, new RunCommand<DrawCommand>("other", before.Revision, RunAction.ChooseNextNode, nextNodeId: "elite"), new ParticipantId("other"));
            var stale = f.Engine.Execute(before, new RunCommand<DrawCommand>("stale", before.Revision - 1, RunAction.ChooseNextNode, nextNodeId: "elite"), Player);
            var wrongAuthority = f.Engine.ExecuteSystem(before, new RunCommand<DrawCommand>("system", before.Revision, RunAction.ChooseNextNode, nextNodeId: "elite"));
            Assert.AreEqual(RunError.InvalidTarget, wrongEdge.Error); Assert.AreEqual(RunError.WrongActor, wrongActor.Error);
            Assert.AreEqual(RunError.StaleRevision, stale.Error); Assert.AreEqual(RunError.WrongActor, wrongAuthority.Error);
            foreach (var result in new[] { wrongEdge, wrongActor, stale, wrongAuthority })
            { Assert.AreSame(before, result.State); Assert.IsEmpty(result.EncounterEvents); Assert.AreSame(before.Random, result.State.Random); }
            f.Apply(RunAction.ChooseNextNode, "normal");
            var repeat = f.Engine.Execute(f.State, new RunCommand<DrawCommand>("repeat", f.State.Revision, RunAction.ChooseNextNode, nextNodeId: "elite"), Player);
            Assert.AreEqual(RunError.WrongPhase, repeat.Error); Assert.AreSame(f.State, repeat.State);
        }

        [Test] public void ForgedPathCompletionCurrentNodeAndUnvisitedBranchScoreAreRejectedOnRestore()
        {
            var f = new Fixture(); f.ToBranch();
            string json = RunSnapshotCodec<DrawState>.Serialize(f.State, f.Content, f.Adapter).Replace("\\/", "/");
            foreach (var forged in new[] {
                json.Replace("\"CurrentNodeId\":\"reward\"", "\"CurrentNodeId\":\"boss\""),
                json.Replace("\"Path\":[\"entry\",\"reward\"]", "\"Path\":[\"entry\",\"boss\",\"reward\"]"),
                json.Replace("\"CompletedNodeIds\":[\"entry\",\"reward\"]", "\"CompletedNodeIds\":[\"entry\"]"),
                json.Replace("nodes-run/stage/1/", "nodes-run/stage/3/"),
                json.Replace("\"TreeVersion\":\"tree.1\"", "\"TreeVersion\":\"tree.2\"") })
            {
                Assert.IsTrue(json != forged, "The mutation must change the snapshot.");
                Assert.Throws<SnapshotException>(() => RunSnapshotCodec<DrawState>.Deserialize(forged, f.Content, f.Adapter));
            }
        }

        [Test] public void ChangedTopologyOrDifficultyCannotLoadAnOldPathEvenWithTheSameVersion()
        {
            var f = new Fixture(); f.ToBranch();
            string json = RunSnapshotCodec<DrawState>.Serialize(f.State, f.Content, f.Adapter);
            var reordered = new RunNodeTree("tree.1", "entry", Branches().Nodes.Reverse());
            var changed = new RunNodeTree("tree.1", "entry", Branches().Nodes.Select(n => n.Id == "elite"
                ? Combat("elite", new[] { "elite-reward" }, RunNodeKind.Elite, target: 2) : n));
            foreach (var tree in new[] { reordered, changed })
                Assert.Throws<SnapshotException>(() => RunSnapshotCodec<DrawState>.Deserialize(json, Content(tree), f.Adapter));
        }

        [Test] public void ShopAndRewardCanExistBeforeAnyEncounterOrAsTerminalNodes()
        {
            var f = new Fixture(new RunNodeTree("noncombat.1", "shop", new RunNodeDefinition[] {
                new RunShopNodeDefinition("shop", new[] { "reward" }), new RunRewardNodeDefinition("reward") }));
            f.Restore(); f.Apply(RunAction.StartRun); f.Restore();
            Assert.IsNull(f.State.Encounter); Assert.AreEqual(-1, f.State.StageIndex);
            Assert.IsEmpty(f.Apply(RunAction.Reroll).EventsFor(Player));
            f.Apply(RunAction.CompleteNode); f.Restore(); var gold = f.State.Gold;
            f.Apply(RunAction.ChooseReward, item: f.State.Offers.Single().Id); f.Restore();
            Assert.AreEqual(RunPhase.Completed, f.State.Phase); Assert.AreEqual(gold + 3, f.State.Gold); Assert.IsEmpty(f.State.Scores);
            var terminalShop = new Fixture(new RunNodeTree("shop.1", "shop", new[] { new RunShopNodeDefinition("shop") }));
            terminalShop.Apply(RunAction.StartRun); terminalShop.Apply(RunAction.CompleteNode); terminalShop.Restore();
            Assert.AreEqual(RunPhase.Completed, terminalShop.State.Phase);
        }

        [Test] public void StartingRewardCanBranchToAnEncounterWithoutCreatingAnEarlierOne()
        {
            var f = new Fixture(new RunNodeTree("reward-first.1", "gift", new RunNodeDefinition[] {
                new RunRewardNodeDefinition("gift", new[] { "elite", "shop" }), Combat("elite", kind: RunNodeKind.Elite), new RunShopNodeDefinition("shop") }));
            f.Apply(RunAction.StartRun); f.Apply(RunAction.SkipReward); f.Restore(); Assert.IsNull(f.State.Encounter);
            f.Apply(RunAction.ChooseNextNode, "elite"); f.Play(); f.Restore(); Assert.AreEqual(RunPhase.Completed, f.State.Phase);
        }

        [Test] public void ConsecutiveEncountersAutomaticallyAdvanceAndKeepBothEventBatches()
        {
            var f = new Fixture(new RunNodeTree("back-to-back.1", "first", new[] { Combat("first", new[] { "boss" }), Combat("boss", kind: RunNodeKind.Boss) }));
            f.Apply(RunAction.StartRun); var result = f.Play();
            Assert.AreEqual("boss", f.State.Nodes.CurrentNodeId); Assert.AreEqual(RunPhase.Encounter, f.State.Phase);
            Assert.AreEqual(1, f.State.Scores.Count); Assert.AreEqual(0, f.State.RoundsScored);
            Assert.IsNotNull(result.ScoredRound); Assert.AreEqual(f.State.Scores[0].Id, result.ScoredRound.Id);
            CollectionAssert.AreEqual(result.EncounterEvents, result.EventsFor(Player));
            Assert.Throws<ArgumentException>(() => result.EventsFor(new ParticipantId("spectator")));
            f.Restore(); f.Play(); Assert.AreEqual(RunPhase.Completed, f.State.Phase);
        }

        [Test] public void FailedEliteCannotCompleteOrChooseASuccessor()
        {
            var f = new Fixture(new RunNodeTree("fail.1", "elite", new[] {
                Combat("elite", new[] { "boss", "normal" }, RunNodeKind.Elite, target: 1000000, rounds: 1), Combat("boss", kind: RunNodeKind.Boss), Combat("normal") }));
            f.Apply(RunAction.StartRun); f.Play(); f.Restore();
            Assert.AreEqual(RunPhase.Failed, f.State.Phase); Assert.IsEmpty(f.State.Nodes.CompletedNodeIds);
            Assert.IsEmpty(f.Engine.GetAvailableNextNodes(f.State));
            var denied = f.Engine.Execute(f.State, new RunCommand<DrawCommand>("escape", f.State.Revision, RunAction.ChooseNextNode, nextNodeId: "boss"), Player);
            Assert.AreSame(f.State, denied.State); Assert.AreEqual(RunError.WrongPhase, denied.Error);
        }

        [Test] public void TreeRejectsCyclesUnreachableNodesDuplicatesAndBrokenReferences()
        {
            Assert.Throws<ArgumentException>(() => new RunNodeTree("v", "a", new[] { Combat("a", new[] { "b" }) }));
            Assert.Throws<ArgumentException>(() => new RunNodeTree("v", "a", new[] { Combat("a", new[] { "b" }), Combat("b", new[] { "a" }) }));
            Assert.Throws<ArgumentException>(() => new RunNodeTree("v", "a", new[] { Combat("a"), Combat("b") }));
            Assert.Throws<ArgumentException>(() => new RunNodeTree("v", "a", new[] { Combat("a"), Combat("a") }));
            Assert.Throws<ArgumentException>(() => new RunNodeTree("v", "missing", new[] { Combat("a") }));
            Assert.Throws<ArgumentException>(() => Combat("a", new[] { "b", "b" }));
            Assert.Throws<ArgumentException>(() => Combat("a", new[] { "a" }));
            Assert.Throws<ArgumentException>(() => Combat("a", kind: RunNodeKind.Shop));
            Assert.Throws<ArgumentException>(() => new RunShopNodeDefinition("shop", offerPool: new[] { "gold", "gold" }));
            Assert.Throws<ArgumentException>(() => Content(new RunNodeTree("v", "shop", new[] { new RunShopNodeDefinition("shop", offerPool: new[] { "unknown" }) })));
        }

        [Test] public void LinearDefinitionsAndBranchingExamplesUseTheSameNodeBaseForBothModes()
        {
            foreach (bool drawMode in new[] { true, false })
            {
                var linear = ExampleRunContent.Create(drawMode); var branching = ExampleRunContent.CreateBranching(drawMode);
                Assert.AreEqual(8, linear.Definition.NodeTree.Nodes.Count);
                Assert.IsTrue(branching.Definition.NodeTree.Nodes.Any(n => n.Kind == RunNodeKind.Elite));
                Assert.IsTrue(branching.Definition.NodeTree.Nodes.Any(n => n.Kind == RunNodeKind.Boss));
                Assert.AreEqual(4, branching.Definition.Stages.Count);
                if (drawMode)
                {
                    var adapter = new DrawRunAdapter(ExampleRunContent.Runtime(true));
                    var engine = new RunEngine<DrawState, DrawCommand, DrawEvent>(branching, adapter); var state = engine.Create("draw", Player);
                    Assert.IsTrue(engine.ExecuteSystem(state, new RunCommand<DrawCommand>("start", state.Revision, RunAction.StartRun)).Accepted);
                }
                else
                {
                    var adapter = new HoldemRunAdapter(ExampleRunContent.Runtime());
                    var engine = new RunEngine<HoldemEncounterState, MatchCommand, MatchEvent>(branching, adapter); var state = engine.Create("holdem", Player);
                    Assert.IsTrue(engine.ExecuteSystem(state, new RunCommand<MatchCommand>("start", state.Revision, RunAction.StartRun)).Accepted);
                }
            }
        }
    }
}
