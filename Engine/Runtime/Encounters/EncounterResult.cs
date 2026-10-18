using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public enum EncounterOutcome { Won = 1, Lost, Draw, Aborted }
    public enum EncounterEndReason { ObjectiveReached = 1, RoundLimit, ResourcesExhausted, ParticipantEliminated, Aborted, ModeRule }

    /// <summary>An outcome is relative to one participant, including PvP and cooperative encounters.</summary>
    public sealed class EncounterParticipantResult
    {
        public ParticipantId ActorId { get; }
        public EncounterOutcome Outcome { get; }
        public long? TotalScore { get; }
        public EncounterParticipantResult(ParticipantId actorId, EncounterOutcome outcome, long? totalScore = null)
        {
            if (!actorId.IsValid || !Enum.IsDefined(typeof(EncounterOutcome), outcome) || totalScore < 0)
                throw new ArgumentException("Invalid encounter participant result.");
            ActorId = actorId; Outcome = outcome; TotalScore = totalScore;
        }
    }

    /// <summary>Frozen terminal summary. Consumers apply their own rewards once; this object executes no rewards.</summary>
    public sealed class EncounterResult
    {
        public string EncounterId { get; }
        public long FinalRevision { get; }
        public int CompletedRounds { get; }
        public EncounterEndReason Reason { get; }
        public IReadOnlyList<EncounterParticipantResult> Participants { get; }
        public EncounterResult(string encounterId, long finalRevision, int completedRounds, EncounterEndReason reason,
            IEnumerable<EncounterParticipantResult> participants)
        {
            EncounterId = Guard.Text(encounterId, nameof(encounterId)); var values = Guard.Copy(participants, nameof(participants));
            if (finalRevision < 0 || completedRounds < 0 || completedRounds > 1000 || !Enum.IsDefined(typeof(EncounterEndReason), reason) ||
                values.Length < 1 || values.Length > 64 || values.Select(p => p.ActorId).Distinct().Count() != values.Length ||
                (reason == EncounterEndReason.Aborted) != values.All(p => p.Outcome == EncounterOutcome.Aborted) ||
                reason != EncounterEndReason.Aborted && values.Any(p => p.Outcome == EncounterOutcome.Aborted) ||
                values.Any(p => p.Outcome == EncounterOutcome.Draw) && !values.All(p => p.Outcome == EncounterOutcome.Draw) ||
                reason == EncounterEndReason.ObjectiveReached && !values.Any(p => p.Outcome == EncounterOutcome.Won))
                throw new ArgumentException("Invalid encounter result.");
            FinalRevision = finalRevision; CompletedRounds = completedRounds; Reason = reason; Participants = Guard.View(values);
        }
        public EncounterParticipantResult For(ParticipantId actor) => Participants.Single(p => p.ActorId == actor);
        public void Validate(EncounterContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (EncounterId != context.Id || CompletedRounds > context.Definition.RoundLimit ||
                Reason == EncounterEndReason.RoundLimit && CompletedRounds != context.Definition.RoundLimit ||
                !new HashSet<ParticipantId>(context.Participants.Select(p => p.ActorId)).SetEquals(Participants.Select(p => p.ActorId)))
                throw new ArgumentException("Result does not match the encounter instance.");
        }
    }
}
