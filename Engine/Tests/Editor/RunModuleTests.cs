using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class RunModuleTests
    {
        private static readonly ParticipantId Player = new ParticipantId("run-player");
        private static readonly ParticipantId Other = new ParticipantId("spectator");

        // Deliberately no Holdem, Draw, betting or mode-specific ability type appears in this fixture.
        [DataContract]
        public sealed class TestEncounter
        {
            [DataMember(IsRequired = true)] public string Id { get; private set; }
            [DataMember(IsRequired = true)] public RandomState Random { get; private set; }
            [DataMember(IsRequired = true)] public string[] Deck { get; private set; }
            [DataMember(IsRequired = true)] public int Round { get; private set; }
            [DataMember(IsRequired = true)] public bool IsCheckpoint { get; private set; }
            [DataMember(IsRequired = true)] public bool Exhausted { get; private set; }
            [DataMember] public ScoreResult LastScore { get; private set; }
            public TestEncounter(string id, RandomState random, IEnumerable<string> deck, int round = 0,
                bool checkpoint = false, bool exhausted = false, ScoreResult score = null)
            { Id = id; Random = random; Deck = deck.ToArray(); Round = round; IsCheckpoint = checkpoint; Exhausted = exhausted; LastScore = score; }
        }
        [DataContract]
        public sealed class DifferentEncounter
        {
            [DataMember(IsRequired = true)] public string RequiredDifferentPayload { get; private set; }
        }
        private sealed class Command
        {
            public string Action { get; }
            public Command(string action) { Action = action; }
        }
        private sealed class Event
        {
            public string Text { get; }
            public bool IsPrivate { get; }
            public Event(string text, bool isPrivate = false) { Text = text; IsPrivate = isPrivate; }
        }
        private sealed class Adapter : IRunEncounter<TestEncounter, Command, Event>
        {
            public string Kind { get; }
            public string RulesVersion { get; }
            public int SchemaVersion { get; }
            public int MinimumDeckSize { get; }
            public Adapter(string kind = "test.encounter", string rules = "1", int schema = 1, int minimum = 2)
            { Kind = kind; RulesVersion = rules; SchemaVersion = schema; MinimumDeckSize = minimum; }
            public RunEncounterTransition<TestEncounter, Event> Create(RunEncounterContext c) =>
                new RunEncounterTransition<TestEncounter, Event>(new TestEncounter(c.EncounterId, c.Random, c.Deck.Select(card => card.Id)));
            public RunEncounterTransition<TestEncounter, Event> Execute(RunEncounterContext c, TestEncounter state, Command command, ParticipantId actor, bool isSystem)
            {
                if (actor != c.PlayerId || isSystem) return new RunEncounterTransition<TestEncounter, Event>(state, error: "WrongActor");
                if (command.Action == "reject")
                    return new RunEncounterTransition<TestEncounter, Event>(new TestEncounter(state.Id, state.Random, state.Deck, state.Round + 1),
                        new[] { new Event("must not escape") }, "Rejected");
                if (command.Action == "repeat") return new RunEncounterTransition<TestEncounter, Event>(state, new[] { new Event("tick"), new Event("secret", true) });
                bool exhausted = command.Action == "exhaust";
                var score = exhausted ? null : ScoreEngine.Fixed((command.Action == "bad-score" ? "wrong/" : state.Id + "/") + "round/" + (state.Round + 1), 10);
                var next = new TestEncounter(command.Action == "bad-state" ? "wrong-identity" : state.Id,
                    state.Random, state.Deck, state.Round + (exhausted ? 0 : 1), true, exhausted, score);
                return new RunEncounterTransition<TestEncounter, Event>(next, new[] { new Event("tick"), new Event("secret", true) });
            }
            public void Validate(RunEncounterContext c, TestEncounter state)
            {
                if (state.Id != c.EncounterId || state.Random == null || state.Round < 0 ||
                    c.Phase == RunPhase.Encounter && !state.Deck.SequenceEqual(c.Deck.Select(card => card.Id)))
                    throw new StateValidationException("TestEncounter", "State");
                if (state.LastScore != null) ScoreEngine.Validate(state.LastScore);
            }
            public RunEncounterProgress ReadProgress(RunEncounterContext c, TestEncounter state) =>
                new RunEncounterProgress(state.Random, state.IsCheckpoint, state.Exhausted, state.LastScore, state.LastScore != null);
            public TestEncounter SyncBuild(RunEncounterContext context, TestEncounter state) => state;
            public IReadOnlyList<Event> FilterEvents(TestEncounter state, IEnumerable<Event> events, ParticipantId viewer)
            {
                if (viewer != Player && viewer != Other) throw new ArgumentException("Unknown viewer.");
                return events.Where(e => !e.IsPrivate || viewer == Player).ToArray();
            }
        }
        private sealed class DifferentAdapter : IRunEncounterState<DifferentEncounter>
        {
            public string Kind => "test.encounter";
            public string RulesVersion => "1";
            public int SchemaVersion => 1;
            public int MinimumDeckSize => 2;
            public void Validate(RunEncounterContext c, DifferentEncounter state) { Assert.Fail("Incompatible serialized type must be rejected before validation."); }
            public RunEncounterProgress ReadProgress(RunEncounterContext c, DifferentEncounter state) { throw new NotSupportedException(); }
        }
        private static RunContent Content(long target = 100, string deckVersion = "deck.1")
        {
            var cards = new[] {
                new CardDefinition(new CardDefinitionId("two-clubs"), Rank.Two, Suit.Clubs, new string[0]),
                new CardDefinition(new CardDefinitionId("three-clubs"), Rank.Three, Suit.Clubs, new string[0]),
                new CardDefinition(new CardDefinitionId("four-clubs"), Rank.Four, Suit.Clubs, new string[0])
            };
            var catalog = new ContentCatalog("content.1", cards, new AbilityDefinitionId[0], new StatusDefinitionId[0]);
            var definition = new RunDefinition("run.1", ScoreProfile.Default, new[] {
                new RunStageDefinition("first", target, 5, clearGold: 0), new RunStageDefinition("second", target, 5, clearGold: 0)
            }, new[] { "remove" }, new[] { "gold" }, new[] { "grower" }, startingGold: 50);
            return new RunContent(catalog, definition, new[] { new RelicDefinition("grower", new ScoreEffect[0], growthPerScoredRound: 1) }, new[] {
                new RunOfferDefinition("remove", RunOfferKind.RemoveCard, 1), new RunOfferDefinition("gold", RunOfferKind.Gold, 0, amount: 1)
            }, deckVersion, cards.Select(c => c.Id));
        }
        private sealed class Fixture
        {
            public readonly RunContent Content;
            public readonly Adapter Adapter;
            public readonly RunEngine<TestEncounter, Command, Event> Engine;
            public RunState<TestEncounter> State;
            public Fixture(long target = 100, int minimum = 2)
            {
                Content = RunModuleTests.Content(target); Adapter = new Adapter(minimum: minimum);
                Engine = new RunEngine<TestEncounter, Command, Event>(Content, Adapter);
                State = Engine.Create("test-run", Player, HmacDeckShuffler.FromSeed(new byte[32]));
            }
            public RunTransitionResult<TestEncounter, Event> Apply(RunAction action, string command = null, string item = null, string target = null, bool accept = true)
            {
                var input = new RunCommand<Command>("command", State.Revision, action, item, target, encounterCommand: command == null ? null : new Command(command));
                var result = action == RunAction.StartEncounter || action == RunAction.SystemEncounter ? Engine.ExecuteSystem(State, input) : Engine.Execute(State, input, Player);
                if (accept) { Assert.That(result.Accepted, Is.True, result.Error + "/" + result.EncounterError); State = result.State; }
                return result;
            }
            public void RoundTrip() => State = RunSnapshotCodec<TestEncounter>.Deserialize(RunSnapshotCodec<TestEncounter>.Serialize(State, Content, Adapter), Content, Adapter);
        }

        [Test] public void PreRenameRunSnapshotRestoresRelicsAndContinuesGrowthWithoutChangingItsWireFormat()
        {
            var content = Content(); var adapter = new Adapter();
            var restored = RunSnapshotCodec<TestEncounter>.Deserialize(LegacyRelicSnapshots.Run, content, adapter);
            Assert.AreEqual("97e6c90cceba31b0a14d56bf60ec394fc6cbdc4b46ccf22c5d30ee477a06ff33", content.Definition.NodeTree.Fingerprint);
            Assert.AreEqual("test-run/joker/start/0", restored.Relics.Single().Id);
            Assert.AreEqual(1, restored.Relics.Single().Growth); Assert.AreEqual(10, restored.StageScore);
            Assert.AreEqual(LegacyRelicSnapshots.Run, RunSnapshotCodec<TestEncounter>.Serialize(restored, content, adapter));
            var result = new RunEngine<TestEncounter, Command, Event>(content, adapter).Execute(restored,
                new RunCommand<Command>("resume", restored.Revision, RunAction.PlayerEncounter, encounterCommand: new Command("score")), Player);
            Assert.IsTrue(result.Accepted, result.Error.ToString()); Assert.AreEqual(20, result.State.StageScore);
            Assert.AreEqual(2, result.State.Relics.Single().Growth); Assert.AreEqual(2, result.State.Scores.Count);
            Assert.AreEqual(restored.Relics.Single().Id, result.State.Relics.Single().Id);
        }

        [Test] public void TypedEncounterSurvivesSnapshotWithoutAnyGameplayDependency()
        {
            var f = new Fixture(); f.Apply(RunAction.StartEncounter); f.Apply(RunAction.PlayerEncounter, "score"); f.RoundTrip();
            Assert.That(f.State.Encounter.Id, Is.EqualTo("test-run/stage/0"));
            Assert.That(f.State.Encounter.LastScore.Total, Is.EqualTo(10));
            Assert.That(f.State.Relics.Single().Growth, Is.EqualTo(1));
            f.Apply(RunAction.PlayerEncounter, "score"); Assert.That(f.State.StageScore, Is.EqualTo(20));
        }
        [Test] public void SnapshotPinsModeRulesFormatAndStartingDeckVersion()
        {
            var f = new Fixture(); f.Apply(RunAction.StartEncounter);
            var json = RunSnapshotCodec<TestEncounter>.Serialize(f.State, f.Content, f.Adapter);
            Assert.Throws<SnapshotException>(() => RunSnapshotCodec<TestEncounter>.Deserialize(json, f.Content, new Adapter(kind: "other")));
            Assert.Throws<SnapshotException>(() => RunSnapshotCodec<TestEncounter>.Deserialize(json, f.Content, new Adapter(rules: "2")));
            Assert.Throws<SnapshotException>(() => RunSnapshotCodec<TestEncounter>.Deserialize(json, f.Content, new Adapter(schema: 2)));
            Assert.Throws<SnapshotException>(() => RunSnapshotCodec<TestEncounter>.Deserialize(json, Content(deckVersion: "deck.2"), f.Adapter));
            Assert.Throws<SnapshotException>(() => RunSnapshotCodec<DifferentEncounter>.Deserialize(json, f.Content, new DifferentAdapter()));
            Assert.Throws<SnapshotException>(() => RunSnapshotCodec<TestEncounter>.Deserialize(json.Replace("\"SchemaVersion\":" + RunSnapshotCodec<TestEncounter>.SchemaVersion, "\"SchemaVersion\":2"), f.Content, f.Adapter));
        }
        [Test] public void EmbeddedFrozenScoreMustAgreeWithRunLedger()
        {
            var f = new Fixture(); f.Apply(RunAction.StartEncounter); f.Apply(RunAction.PlayerEncounter, "score");
            string json = RunSnapshotCodec<TestEncounter>.Serialize(f.State, f.Content, f.Adapter);
            int embedded = json.IndexOf("\"LastScore\":", StringComparison.Ordinal);
            Assert.That(embedded, Is.GreaterThan(0));
            string forged = json.Substring(0, embedded) + json.Substring(embedded).Replace("\"BasePoints\":10", "\"BasePoints\":11")
                .Replace("\"Points\":10", "\"Points\":11").Replace("\"Total\":10", "\"Total\":11");
            Assert.That(forged, Is.Not.EqualTo(json));
            Assert.Throws<SnapshotException>(() => RunSnapshotCodec<TestEncounter>.Deserialize(forged, f.Content, f.Adapter));
        }
        [Test] public void UnresolvedChoicesCannotPublishCompletedScoresOrExhaustion()
        {
            var random = HmacDeckShuffler.FromSeed(new byte[32]);
            Assert.Throws<ArgumentException>(() => new RunEncounterProgress(random, false, score: ScoreEngine.Fixed("score", 10)));
            Assert.Throws<ArgumentException>(() => new RunEncounterProgress(random, false, exhausted: true));
            Assert.Throws<ArgumentException>(() => new RunEncounterProgress(random, true, growRelics: true));
        }
        [Test] public void RepeatedCheckpointIsHarvestedOnceAcrossRestoration()
        {
            var f = new Fixture(); f.Apply(RunAction.StartEncounter); f.Apply(RunAction.PlayerEncounter, "score"); f.RoundTrip();
            var repeated = f.Apply(RunAction.PlayerEncounter, "repeat");
            Assert.That(repeated.ScoredRound, Is.Null); Assert.That(f.State.Scores.Count, Is.EqualTo(1));
            Assert.That(f.State.RoundsScored, Is.EqualTo(1)); Assert.That(f.State.StageScore, Is.EqualTo(10));
            Assert.That(f.State.Relics.Single().Growth, Is.EqualTo(1));
        }
        [Test] public void ExhaustionWithoutAScoreFailsAtAResolvedCheckpoint()
        {
            var f = new Fixture(); f.Apply(RunAction.StartEncounter); f.Apply(RunAction.PlayerEncounter, "exhaust");
            Assert.That(f.State.Phase, Is.EqualTo(RunPhase.Failed)); Assert.That(f.State.Scores.Count, Is.Zero); f.RoundTrip();
            Assert.That(f.State.Phase, Is.EqualTo(RunPhase.Failed));
        }
        [Test] public void ShopRestorationKeepsHistoricalEncounterSeparateFromCurrentBuild()
        {
            var f = new Fixture(10); f.Apply(RunAction.StartEncounter); f.Apply(RunAction.PlayerEncounter, "score"); f.Apply(RunAction.SkipReward);
            var removed = f.State.Deck[0].Id; f.Apply(RunAction.Buy, item: f.State.Offers.Single().Id, target: removed); f.RoundTrip();
            Assert.That(f.State.Deck.Count, Is.EqualTo(2)); Assert.That(f.State.Encounter.Deck.Length, Is.EqualTo(3));
            Assert.That(f.State.Encounter.LastScore.Total, Is.EqualTo(10));
            f.Apply(RunAction.StartEncounter); Assert.That(f.State.Encounter.Deck.Length, Is.EqualTo(2));
            Assert.That(f.State.StageScore, Is.Zero); Assert.That(f.State.StageIndex, Is.EqualTo(1));
        }
        [Test] public void AdapterMinimumDeckSizeControlsRemovalAndCreation()
        {
            var f = new Fixture(10, minimum: 3); f.Apply(RunAction.StartEncounter); f.Apply(RunAction.PlayerEncounter, "score"); f.Apply(RunAction.SkipReward);
            var before = f.State; var denied = f.Apply(RunAction.Buy, item: f.State.Offers.Single().Id, target: f.State.Deck[0].Id, accept: false);
            Assert.That(denied.Error, Is.EqualTo(RunError.InvalidTarget)); Assert.That(denied.State, Is.SameAs(before));
            var content = Content(); var engine = new RunEngine<TestEncounter, Command, Event>(content, new Adapter(minimum: 4));
            Assert.Throws<StateValidationException>(() => engine.Create("too-small", Player));
        }
        [Test] public void RejectionAndInvalidScoreRollbackAllStateAndEvents()
        {
            var f = new Fixture(); f.Apply(RunAction.StartEncounter); var before = f.State;
            var rejected = f.Apply(RunAction.PlayerEncounter, "reject", accept: false);
            Assert.That(rejected.Error, Is.EqualTo(RunError.EncounterRejected)); Assert.That(rejected.EncounterError, Is.EqualTo("Rejected"));
            Assert.That(rejected.State, Is.SameAs(before)); Assert.That(rejected.EncounterEvents.Count, Is.Zero);
            var malformed = f.Apply(RunAction.PlayerEncounter, "bad-score", accept: false);
            Assert.That(malformed.Error, Is.EqualTo(RunError.InvalidState)); Assert.That(malformed.State, Is.SameAs(before));
            Assert.That(malformed.EncounterEvents.Count, Is.Zero);
        }
        [Test] public void EncounterEventFilteringAndActorValidationBelongToAdapter()
        {
            var f = new Fixture(); f.Apply(RunAction.StartEncounter); var result = f.Apply(RunAction.PlayerEncounter, "repeat");
            Assert.That(result.EventsFor(Player).Count, Is.EqualTo(2)); Assert.That(result.EventsFor(Other).Count, Is.EqualTo(1));
            Assert.Throws<ArgumentException>(() => result.EventsFor(new ParticipantId("unknown")));
            var forged = f.Engine.Execute(f.State, new RunCommand<Command>("forged", f.State.Revision, RunAction.PlayerEncounter, encounterCommand: new Command("score")), Other);
            Assert.That(forged.EncounterError, Is.EqualTo("WrongActor")); Assert.That(forged.State, Is.SameAs(f.State));
            var system = f.Apply(RunAction.SystemEncounter, "score", accept: false);
            Assert.That(system.EncounterError, Is.EqualTo("WrongActor")); Assert.That(system.EncounterEvents.Count, Is.Zero);
        }
        [Test] public void InvalidCompletedEncounterCannotBeHiddenByAutomaticallyStartingItsSuccessor()
        {
            var sample = Content(target: 10);
            var tree = new RunNodeTree("direct.1", "first", new[] {
                new RunEncounterNodeDefinition("first", sample.Definition.Stages[0], new[] { "second" }),
                new RunEncounterNodeDefinition("second", sample.Definition.Stages[1])
            });
            var definition = new RunDefinition("run.direct.1", ScoreProfile.Default, tree, sample.Definition.ShopPool, sample.Definition.RewardPool);
            var content = new RunContent(sample.CardsAndAbilities, definition, new RelicDefinition[0],
                new[] { sample.Offer("remove"), sample.Offer("gold") }, sample.StartingDeckVersion, sample.StartingDeck);
            var adapter = new Adapter(); var engine = new RunEngine<TestEncounter, Command, Event>(content, adapter);
            var initial = engine.Create("direct", Player);
            var started = engine.ExecuteSystem(initial, new RunCommand<Command>("start", initial.Revision, RunAction.StartRun));
            Assert.IsTrue(started.Accepted); var before = started.State;
            var denied = engine.Execute(before, new RunCommand<Command>("bad", before.Revision, RunAction.PlayerEncounter,
                encounterCommand: new Command("bad-state")), Player);
            Assert.AreEqual(RunError.InvalidState, denied.Error); Assert.AreSame(before, denied.State); Assert.IsEmpty(denied.EncounterEvents);
            Assert.IsEmpty(denied.State.Nodes.CompletedNodeIds); Assert.IsEmpty(denied.State.Scores);
        }
    }
}
