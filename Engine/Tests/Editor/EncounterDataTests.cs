using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class EncounterDataTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player");
        private static readonly ParticipantId Opponent = new ParticipantId("opponent");
        private static EncounterDefinition Solo() => new EncounterDefinition("battle", "1", "score", "1",
            new EncounterObjectiveDefinition(EncounterObjectiveKind.ReachScore, 200), 6,
            new[] { new EncounterParticipantDefinition("player", "party", new ResourceState(Chips.Zero, 3)) });
        private static EncounterContext SoloContext() => new EncounterContext("battle-instance", Solo(),
            new[] { new EncounterParticipantBinding("player", Player) });
        private static EncounterResult Won(string id = "battle-instance", int rounds = 1) => new EncounterResult(id, 8, rounds,
            EncounterEndReason.ObjectiveReached, new[] { new EncounterParticipantResult(Player, EncounterOutcome.Won, 250) });

        [Test] public void SoloScoreEncounterNeedsNoEnemyAndContextMayBeStandalone()
        {
            var context = SoloContext();
            Assert.IsNull(context.Run); Assert.AreEqual(Player, context.Actor("player")); Assert.IsTrue(context.Contains(Player));
            Assert.IsFalse(context.Contains(Opponent)); Assert.AreEqual(200, context.Definition.Objective.Target);
            Assert.IsNull(context.Definition.Participants[0].InitialResources.Health);
            var result = Won(); result.Validate(context); Assert.AreEqual(EncounterOutcome.Won, result.For(Player).Outcome);
        }

        [Test] public void EncounterDefinitionAndBindingsCopyInputCollections()
        {
            var actors = new[] { new EncounterParticipantDefinition("slot", "team", new ResourceState(Chips.Zero)) };
            var modifiers = new[] { "boss.rule" };
            var definition = new EncounterDefinition("battle", "1", "score", "1",
                new EncounterObjectiveDefinition(EncounterObjectiveKind.ReachScore, 10), 2, actors, modifiers);
            var bindings = new[] { new EncounterParticipantBinding("slot", Player) };
            var context = new EncounterContext("instance", definition, bindings);
            actors[0] = new EncounterParticipantDefinition("other", "team", new ResourceState(Chips.Zero)); modifiers[0] = "changed";
            bindings[0] = new EncounterParticipantBinding("other", Opponent);
            Assert.AreEqual("slot", definition.Participants[0].SlotId); Assert.AreEqual("boss.rule", definition.ModifierIds[0]);
            Assert.AreEqual(Player, context.Actor("slot"));
        }

        [Test] public void InvalidObjectivesAndBudgetsAndDuplicateDefinitionsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new EncounterObjectiveDefinition(EncounterObjectiveKind.ReachScore));
            Assert.Throws<ArgumentException>(() => new EncounterObjectiveDefinition(EncounterObjectiveKind.ReachScore, 0));
            Assert.Throws<ArgumentException>(() => new EncounterObjectiveDefinition(EncounterObjectiveKind.DefeatOpposition, 1));
            Assert.Throws<ArgumentException>(() => new EncounterObjectiveDefinition((EncounterObjectiveKind)99));
            var actors = Solo().Participants;
            Assert.Throws<ArgumentException>(() => new EncounterDefinition("id", "1", "mode", "1", Solo().Objective, 0, actors));
            Assert.Throws<ArgumentException>(() => new EncounterDefinition("id", "1", "mode", "1", Solo().Objective, 1, new[] { actors[0], actors[0] }));
            Assert.Throws<ArgumentException>(() => new EncounterDefinition("id", "1", "mode", "1", Solo().Objective, 1, actors, new[] { "rule", "rule" }));
            Assert.Throws<ArgumentException>(() => new EncounterDefinition("id", "1", "mode", "1",
                new EncounterObjectiveDefinition(EncounterObjectiveKind.SurviveRounds, 3), 2, actors));
            Assert.Throws<ArgumentException>(() => new EncounterDefinition("id", "1", "mode", "1",
                new EncounterObjectiveDefinition(EncounterObjectiveKind.DefeatOpposition), 2, actors));
        }

        [Test] public void ParticipantBindingsRequireExactUniqueSlotsAndActors()
        {
            Assert.Throws<ArgumentException>(() => new EncounterContext("id", Solo(), new EncounterParticipantBinding[0]));
            Assert.Throws<ArgumentException>(() => new EncounterContext("id", Solo(), new[] { new EncounterParticipantBinding("unknown", Player) }));
            Assert.Throws<ArgumentException>(() => new EncounterParticipantBinding("slot", default(ParticipantId)));
            var definition = new EncounterDefinition("id", "1", "mode", "1", Solo().Objective, 2, new[] {
                new EncounterParticipantDefinition("one", "a", new ResourceState(Chips.Zero)),
                new EncounterParticipantDefinition("two", "b", new ResourceState(Chips.Zero)) });
            Assert.Throws<ArgumentException>(() => new EncounterContext("id", definition, new[] {
                new EncounterParticipantBinding("one", Player), new EncounterParticipantBinding("two", Player) }));
            Assert.Throws<ArgumentException>(() => new EncounterRunBinding("run", "floor", ""));
        }

        [Test] public void PerParticipantResultsRepresentOpposingAndCooperativeOutcomes()
        {
            var actors = new[] { new EncounterParticipantResult(Player, EncounterOutcome.Won), new EncounterParticipantResult(Opponent, EncounterOutcome.Lost) };
            var result = new EncounterResult("pvp", 20, 2, EncounterEndReason.ParticipantEliminated, actors);
            actors[0] = new EncounterParticipantResult(Player, EncounterOutcome.Lost);
            Assert.AreEqual(EncounterOutcome.Won, result.For(Player).Outcome); Assert.IsNull(result.For(Player).TotalScore);
            var cooperative = new EncounterResult("coop", 3, 1, EncounterEndReason.ObjectiveReached, new[] {
                new EncounterParticipantResult(Player, EncounterOutcome.Won), new EncounterParticipantResult(Opponent, EncounterOutcome.Won) });
            Assert.IsTrue(cooperative.Participants.All(p => p.Outcome == EncounterOutcome.Won));
            Assert.Throws<ArgumentException>(() => new EncounterResult("invalid", 1, 1, EncounterEndReason.ModeRule, new[] {
                new EncounterParticipantResult(Player, EncounterOutcome.Draw), new EncounterParticipantResult(Opponent, EncounterOutcome.Won) }));
            Assert.Throws<ArgumentException>(() => new EncounterResult("invalid", 1, 1, EncounterEndReason.Aborted,
                new[] { new EncounterParticipantResult(Player, EncounterOutcome.Won) }));
            Assert.Throws<ArgumentException>(() => new EncounterResult("invalid", 1, 1, EncounterEndReason.ObjectiveReached,
                new[] { new EncounterParticipantResult(Player, EncounterOutcome.Lost) }));
            Assert.Throws<ArgumentException>(() => new EncounterParticipantResult(Player, EncounterOutcome.Won, -1));
        }

        [Test] public void ProgressProjectionValidatesWaitingChoicesAndResolvedCheckpoints()
        {
            var context = SoloContext(); var decision = new DecisionId("choose");
            var ready = new EncounterProgress(EncounterPhase.AwaitingAction, 1, 0, Player); ready.Validate(context); Assert.IsFalse(ready.IsCheckpoint);
            var choosing = new EncounterProgress(EncounterPhase.AwaitingChoice, 2, 0, Player, decision); choosing.Validate(context);
            var checkpoint = new EncounterProgress(EncounterPhase.Checkpoint, 3, 1); checkpoint.Validate(context); Assert.IsTrue(checkpoint.IsCheckpoint);
            Assert.Throws<ArgumentException>(() => new EncounterProgress(EncounterPhase.AwaitingChoice, 1, 0, Player));
            Assert.Throws<ArgumentException>(() => new EncounterProgress(EncounterPhase.Checkpoint, 1, 1, Player));
            Assert.Throws<ArgumentException>(() => new EncounterProgress(EncounterPhase.Resolving, 1, 0, Player, decision));
            Assert.Throws<ArgumentException>(() => new EncounterProgress(EncounterPhase.Preparing, 0, 1));
            Assert.Throws<ArgumentException>(() => new EncounterProgress(EncounterPhase.AwaitingAction, 1, 0, Opponent).Validate(context));
        }

        [Test] public void TerminalResultMustMatchInstanceActorsRevisionAndRoundCount()
        {
            var context = SoloContext(); var result = Won();
            new EncounterProgress(EncounterPhase.Finished, 8, 1, result: result).Validate(context);
            Assert.Throws<ArgumentException>(() => Won("other").Validate(context));
            Assert.Throws<ArgumentException>(() => Won(rounds: 7).Validate(context));
            Assert.Throws<ArgumentException>(() => new EncounterResult(context.Id, 8, 1, EncounterEndReason.RoundLimit,
                new[] { new EncounterParticipantResult(Player, EncounterOutcome.Lost) }).Validate(context));
            Assert.Throws<ArgumentException>(() => new EncounterResult(context.Id, 8, 1, EncounterEndReason.ModeRule,
                new[] { new EncounterParticipantResult(Opponent, EncounterOutcome.Lost) }).Validate(context));
            Assert.Throws<ArgumentException>(() => new EncounterProgress(EncounterPhase.Finished, 7, 1, result: result));
            Assert.Throws<ArgumentException>(() => new EncounterProgress(EncounterPhase.Finished, 8, 2, result: result));
            Assert.Throws<ArgumentException>(() => new EncounterProgress(EncounterPhase.Checkpoint, 8, 1, result: result));
            Assert.Throws<ArgumentException>(() => new EncounterProgress(EncounterPhase.Finished, 8, 1));
        }

        [Test] public void DrawAdapterDescriptionMatchesActualRunAndConcreteSettingsWithoutChangingSnapshot()
        {
            var content = ExampleRunContent.CreateFourFloors(true); var settings = new DrawEncounterSettings(handSize: 6, discards: 2);
            var adapter = new DrawRunAdapter(ExampleRunContent.Runtime(true), settings);
            var engine = new RunEngine<DrawState, DrawCommand, DrawEvent>(content, adapter);
            var initial = engine.Create("run", Player, HmacDeckShuffler.FromSeed(new byte[32]));
            var started = engine.ExecuteSystem(initial, new RunCommand<DrawCommand>("start", initial.Revision, RunAction.StartRun));
            Assert.IsTrue(started.Accepted); var state = started.State; var context = RunEncounterContext.From(state, content);
            var description = adapter.Describe(context);
            Assert.AreEqual(state.Encounter.Id, description.Context.Id); Assert.AreEqual(context.Floor.Id, description.Context.Run.FloorId);
            Assert.AreEqual(context.Node.Id, description.Context.Run.NodeId); Assert.AreEqual("run", description.Context.Run.RunId);
            Assert.AreEqual(context.Stage.TargetScore, description.Settings.TargetScore);
            Assert.AreEqual(description.Context.Definition.RoundLimit, description.Settings.Hands);
            Assert.AreEqual(6, description.Settings.HandSize); Assert.AreEqual(2, description.Settings.Discards);
            Assert.AreEqual(context.Stage.Energy, description.Context.Definition.Participants[0].InitialResources.Energy);
            Assert.AreEqual(adapter.RulesVersion, description.Context.Definition.ModeRulesVersion);
            var json = RunSnapshotCodec<DrawState>.Serialize(state, content, adapter);
            var restored = RunSnapshotCodec<DrawState>.Deserialize(json, content, adapter);
            Assert.AreEqual(json, RunSnapshotCodec<DrawState>.Serialize(restored, content, adapter));
            Assert.AreEqual(4, RunSnapshotCodec<DrawState>.SchemaVersion);
        }

        [Test] public void HoldemAdapterDescriptionKeepsPlayerOpponentAndTableRulesTyped()
        {
            var content = ExampleRunContent.CreateFourFloors(false);
            var settings = new HoldemEncounterSettings(playerChips: 800, opponentChips: 900, smallBlind: 5, bigBlind: 10);
            var adapter = new HoldemRunAdapter(ExampleRunContent.Runtime(false), settings);
            var engine = new RunEngine<HoldemEncounterState, MatchCommand, MatchEvent>(content, adapter);
            var initial = engine.Create("run", Player, HmacDeckShuffler.FromSeed(new byte[32]));
            var started = engine.ExecuteSystem(initial, new RunCommand<MatchCommand>("start", 0, RunAction.StartRun));
            Assert.IsTrue(started.Accepted); var state = started.State;
            var description = adapter.Describe(RunEncounterContext.From(state, content));
            Assert.AreEqual(state.Encounter.Match.Id.Value, description.Context.Id);
            Assert.AreEqual(Player, description.Context.Actor("player")); Assert.AreEqual(HoldemRunAdapter.OpponentId, description.Context.Actor("opponent"));
            Assert.AreEqual(800, description.Context.Definition.Participants[0].InitialResources.Chips.Value);
            Assert.AreEqual(900, description.Context.Definition.Participants[1].InitialResources.Chips.Value);
            Assert.AreEqual(state.Encounter.Match.Definition.BigBlind, description.Settings.BigBlind);
            Assert.AreEqual(5, description.Settings.SmallBlind.Value); Assert.AreEqual(10, description.Settings.BigBlind.Value);
        }
    }
}
