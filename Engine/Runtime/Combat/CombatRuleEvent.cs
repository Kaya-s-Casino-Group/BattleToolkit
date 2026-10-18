using System.Collections.Generic;

namespace PokerEngine
{
    /// <summary>Immutable fact. Index is the original effect position (-1 for lifecycle); SequenceIndex identifies the fact within its revision.</summary>
    public abstract class CombatRuleEvent
    {
        public long Revision { get; }
        public int Index { get; }
        public int SequenceIndex { get; }
        /// <summary>Null for lifecycle facts that were not produced by an effect.</summary>
        public CombatEffect Effect { get; }
        /// <summary>Null for uncaused lifecycle facts. Periodic effects and their consumption use root index -1.</summary>
        public CombatEffectProvenance Provenance { get; }

        internal CombatRuleEvent(long revision, int index, CombatEffect effect, int sequenceIndex = -1, CombatEffectProvenance provenance = null)
        {
            Revision = revision; Index = index; SequenceIndex = sequenceIndex < 0 ? index : sequenceIndex; Effect = effect;
            Provenance = provenance ?? (effect == null ? null : new CombatEffectProvenance(index));
        }
    }

    /// <summary>A newly applied status. It is not a zero-damage or other vitals event.</summary>
    public sealed class CombatStatusEvent : CombatRuleEvent
    {
        public StatusInstance Status => Effect.Status;

        internal CombatStatusEvent(long revision, int index, CombatEffect effect, int sequenceIndex = -1, CombatEffectProvenance provenance = null)
            : base(revision, index, effect, sequenceIndex, provenance) { }
    }

    public enum CombatStatusChangeReason { Refreshed = 1, Consumed = 2, Expired = 3 }
    public sealed class CombatStatusChangedEvent : CombatRuleEvent
    {
        public StatusInstance StatusBefore { get; }
        public StatusInstance StatusAfter { get; }
        public CombatStatusChangeReason Reason { get; }
        public string ScopeId { get; }
        public string PhaseKey { get; }
        internal CombatStatusChangedEvent(long revision, int index, int sequenceIndex, CombatEffect effect,
            StatusInstance before, StatusInstance after, CombatStatusChangeReason reason, string scopeId = null, string phaseKey = null,
            CombatEffectProvenance provenance = null)
            : base(revision, index, effect, sequenceIndex, provenance)
        { StatusBefore = before; StatusAfter = after; Reason = reason; ScopeId = scopeId; PhaseKey = phaseKey; }
    }
    public sealed class CombatStatusBlockedEvent : CombatRuleEvent
    {
        public StatusInstance Status => Effect.Status;
        public StatusInstanceId BlockingStatusId { get; }
        internal CombatStatusBlockedEvent(long revision, int index, int sequenceIndex, CombatEffect effect, StatusInstanceId blockingStatusId,
            CombatEffectProvenance provenance = null)
            : base(revision, index, effect, sequenceIndex, provenance) { BlockingStatusId = blockingStatusId; }
    }
    public sealed class CombatActionWindowSkippedEvent : CombatRuleEvent
    {
        public ParticipantId Actor { get; }
        public IReadOnlyList<StatusInstanceId> StatusIds { get; }
        internal CombatActionWindowSkippedEvent(long revision, int sequenceIndex, ParticipantId actor, IEnumerable<StatusInstanceId> statusIds)
            : base(revision, -1, null, sequenceIndex)
        { Actor = actor; StatusIds = Guard.View(Guard.Copy(statusIds, nameof(statusIds))); }
    }

    public enum CombatDeathCause { None = 0, Damage = 1, Eliminate = 2 }
}
