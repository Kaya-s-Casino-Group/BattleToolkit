using System;
using System.Collections.Generic;

namespace PokerEngine
{
    /// <summary>Mode-neutral input to a typed encounter adapter. Build values belong to the run.</summary>
    public sealed class RunEncounterContext
    {
        public string RunId { get; }
        public string EncounterId => RunId + "/stage/" + StageIndex;
        public ParticipantId PlayerId { get; }
        public RunPhase Phase { get; }
        public int StageIndex { get; }
        public RunContent Content { get; }
        public RunStageDefinition Stage => Content.Definition.Stages[StageIndex];
        /// <summary>The current or most recently entered encounter, including its Normal/Elite/Boss kind.</summary>
        public RunEncounterNodeDefinition Node => Content.Definition.EncounterNodes[StageIndex];
        public int FloorIndex => Content.Definition.EncounterFloorIndex(StageIndex);
        /// <summary>Floor owning this encounter, including while its frozen state is retained on a later floor.</summary>
        public RunFloorDefinition Floor => Content.Definition.Floors[FloorIndex];
        public IReadOnlyList<RunCard> Deck { get; }
        public IReadOnlyList<RunRelic> Relics { get; }
        public IReadOnlyList<HandLevel> HandLevels { get; }
        public RandomState Random { get; }
        public RunEncounterContext(string runId, ParticipantId playerId, RunPhase phase, int stageIndex, RunContent content,
            IEnumerable<RunCard> deck, IEnumerable<RunRelic> relics, IEnumerable<HandLevel> handLevels, RandomState random)
        {
            RunId = Guard.Text(runId, nameof(runId)); PlayerId = playerId; Phase = phase; StageIndex = stageIndex;
            Content = content ?? throw new ArgumentNullException(nameof(content));
            Deck = Guard.View(Guard.Copy(deck, nameof(deck))); Relics = Guard.View(Guard.Copy(relics, nameof(relics)));
            HandLevels = Guard.View(Guard.Copy(handLevels, nameof(handLevels))); Random = random ?? throw new ArgumentNullException(nameof(random));
        }
        public static RunEncounterContext From<TState>(RunState<TState> state, RunContent content) where TState : class =>
            new RunEncounterContext(state.Id, state.PlayerId, state.Phase, state.StageIndex, content, state.Deck, state.Relics, state.HandLevels, state.Random);
    }

    /// <summary>Portable changes to an installed relic; references local to an encounter must not escape here.</summary>
    public sealed class RunRelicRuntime
    {
        public string Id { get; }
        public int? Charges { get; }
        public int CooldownRounds { get; }
        public AbilityMemory Memory { get; }
        public RunRelicRuntime(string id, int? charges, int cooldownRounds, AbilityMemory memory)
        {
            Id = Guard.Text(id, nameof(id)); Charges = charges; CooldownRounds = cooldownRounds;
            Memory = memory ?? throw new ArgumentNullException(nameof(memory));
            if (charges < 0 || cooldownRounds < 0) throw new ArgumentException("Invalid portable relic state.");
        }
    }

    /// <summary>A checkpoint has no unresolved immediate effects or choices and can be left safely.
    /// Score, when present, is the latest completed round. Its stable encounter-prefixed ID is harvested once.
    /// Exhausted means the encounter cannot continue. Growth is applied only with a newly harvested score.</summary>
    public sealed class RunEncounterProgress
    {
        public RandomState Random { get; }
        public bool IsCheckpoint { get; }
        public bool Exhausted { get; }
        public ScoreResult Score { get; }
        public bool GrowRelics { get; }
        public IReadOnlyList<RunRelicRuntime> Relics { get; }
        public RunEncounterProgress(RandomState random, bool isCheckpoint, bool exhausted = false, ScoreResult score = null,
            bool growRelics = false, IEnumerable<RunRelicRuntime> relics = null)
        {
            Random = random ?? throw new ArgumentNullException(nameof(random)); IsCheckpoint = isCheckpoint; Exhausted = exhausted;
            Score = score; GrowRelics = growRelics; Relics = Guard.View(Guard.Copy(relics ?? new RunRelicRuntime[0], nameof(relics)));
            if ((score != null || growRelics || exhausted) && !isCheckpoint || growRelics && score == null)
                throw new ArgumentException("Only a fully resolved checkpoint can report scores or exhaustion.");
        }
    }

    public sealed class RunEncounterTransition<TState, TEvent> where TState : class
    {
        public bool Accepted => Error == null;
        public string Error { get; }
        public TState State { get; }
        public IReadOnlyList<TEvent> Events { get; }
        public RunEncounterTransition(TState state, IEnumerable<TEvent> events = null, string error = null)
        {
            State = state ?? throw new ArgumentNullException(nameof(state)); Error = error;
            if (error != null) Guard.Text(error, nameof(error));
            Events = Guard.View(Guard.Copy(events ?? new TEvent[0], nameof(events)));
        }
    }

    /// <summary>Typed snapshot validation and progress projection. Implementations live in composition, never in Run.</summary>
    public interface IRunEncounterState<TState> where TState : class
    {
        string Kind { get; }
        string RulesVersion { get; }
        int SchemaVersion { get; }
        int MinimumDeckSize { get; }
        void Validate(RunEncounterContext context, TState state);
        RunEncounterProgress ReadProgress(RunEncounterContext context, TState state);
    }

    public interface IRunEncounter<TState, TCommand, TEvent> : IRunEncounterState<TState> where TState : class where TCommand : class
    {
        RunEncounterTransition<TState, TEvent> Create(RunEncounterContext context);
        RunEncounterTransition<TState, TEvent> Execute(RunEncounterContext context, TState state, TCommand command, ParticipantId actor, bool isSystem);
        /// <summary>Apply new build growth after a checkpoint without consuming another action or random draw.</summary>
        TState SyncBuild(RunEncounterContext context, TState state);
        IReadOnlyList<TEvent> FilterEvents(TState state, IEnumerable<TEvent> events, ParticipantId viewer);
    }
}
