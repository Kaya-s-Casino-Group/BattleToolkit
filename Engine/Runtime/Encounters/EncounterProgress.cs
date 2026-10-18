using System;

namespace PokerEngine
{
    public enum EncounterPhase { Preparing = 1, AwaitingAction, Resolving, AwaitingChoice, Checkpoint, Finished }

    /// <summary>Derived common view; never serialize it as another authoritative phase/turn/score state.</summary>
    public sealed class EncounterProgress
    {
        public EncounterPhase Phase { get; }
        public long Revision { get; }
        public int CompletedRounds { get; }
        public ParticipantId? CurrentActor { get; }
        public DecisionId? PendingDecision { get; }
        public EncounterResult Result { get; }
        public bool IsCheckpoint => Phase == EncounterPhase.Checkpoint || Phase == EncounterPhase.Finished;
        public EncounterProgress(EncounterPhase phase, long revision, int completedRounds, ParticipantId? currentActor = null,
            DecisionId? pendingDecision = null, EncounterResult result = null)
        {
            if (!Enum.IsDefined(typeof(EncounterPhase), phase) || revision < 0 || completedRounds < 0 || completedRounds > 1000 ||
                currentActor.HasValue && !currentActor.Value.IsValid || pendingDecision.HasValue && !pendingDecision.Value.IsValid ||
                (phase == EncounterPhase.AwaitingChoice) != pendingDecision.HasValue ||
                (phase == EncounterPhase.AwaitingAction || phase == EncounterPhase.AwaitingChoice) && !currentActor.HasValue ||
                (phase == EncounterPhase.Preparing || phase == EncounterPhase.Checkpoint || phase == EncounterPhase.Finished) && currentActor.HasValue ||
                phase == EncounterPhase.Preparing && completedRounds != 0 ||
                (phase == EncounterPhase.Finished) != (result != null) ||
                result != null && (result.FinalRevision != revision || result.CompletedRounds != completedRounds))
                throw new ArgumentException("Invalid encounter progress projection.");
            Phase = phase; Revision = revision; CompletedRounds = completedRounds; CurrentActor = currentActor; PendingDecision = pendingDecision; Result = result;
        }
        public void Validate(EncounterContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (CompletedRounds > context.Definition.RoundLimit || CurrentActor.HasValue && !context.Contains(CurrentActor.Value))
                throw new ArgumentException("Progress does not match the encounter instance.");
            Result?.Validate(context);
        }
    }
}
