using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public enum CombatTimingKind { ActionCompleted = 1, PhaseEnding = 2, BattleStarted = 3, PhaseStarted = 4 }

    /// <summary>Mode-neutral clock declaration. Composition owns which actions and phase rosters qualify.</summary>
    public sealed class CombatTimingSignal
    {
        public CombatTimingKind Kind { get; }
        public string BoundaryId { get; }
        public IReadOnlyList<ParticipantId> Participants { get; }
        public string ActionKey { get; }
        public string PhaseKey { get; }
        private CombatTimingSignal(CombatTimingKind kind, string boundaryId, IEnumerable<ParticipantId> participants,
            string actionKey = null, string phaseKey = null)
        {
            Kind = kind; BoundaryId = Guard.Text(boundaryId, nameof(boundaryId));
            var copy = Guard.Copy(participants, nameof(participants));
            if (copy.Length < 1 || copy.Length > 64 || copy.Any(id => !id.IsValid) || copy.Distinct().Count() != copy.Length)
                throw new ArgumentException("A clock requires 1..64 distinct participant identities.");
            Participants = Guard.View(copy); ActionKey = actionKey; PhaseKey = phaseKey;
        }
        public static CombatTimingSignal ActionCompleted(string boundaryId, ParticipantId actor, string actionKey) =>
            new CombatTimingSignal(CombatTimingKind.ActionCompleted, boundaryId, new[] { actor },
                actionKey: Guard.Text(actionKey, nameof(actionKey)));
        public static CombatTimingSignal PhaseEnding(string boundaryId, IEnumerable<ParticipantId> participants, string phaseKey) =>
            new CombatTimingSignal(CombatTimingKind.PhaseEnding, boundaryId, participants,
                phaseKey: Guard.Text(phaseKey, nameof(phaseKey)));
        public static CombatTimingSignal BattleStarted(string boundaryId, IEnumerable<ParticipantId> participants) =>
            new CombatTimingSignal(CombatTimingKind.BattleStarted, boundaryId, participants);
        public static CombatTimingSignal PhaseStarted(string boundaryId, IEnumerable<ParticipantId> participants, string phaseKey) =>
            new CombatTimingSignal(CombatTimingKind.PhaseStarted, boundaryId, participants, phaseKey: Guard.Text(phaseKey, nameof(phaseKey)));
    }

    /// <summary>Fixed per-stack damage; target selection is relative to the holder, attribution to the applier.</summary>
    public sealed class PeriodicDamageSpec : StatusBehaviorSpec
    {
        public CombatTimingKind Timing { get; }
        public long DamagePerStack { get; }
        public CombatTargetSpec Target { get; }
        public PeriodicDamageSpec(CombatTimingKind timing, long damagePerStack, CombatTargetSpec target = null)
        {
            if ((timing != CombatTimingKind.ActionCompleted && timing != CombatTimingKind.PhaseEnding) || damagePerStack < 0)
                throw new ArgumentException("Periodic damage requires a supported clock and nonnegative strength.");
            Target = target ?? CombatTargetSpec.Self();
            if (Target.Group == CombatTargetGroup.EventSource || Target.Group == CombatTargetGroup.EventTarget || Target.Group == CombatTargetGroup.EventActor)
                throw new ArgumentException("A clock has no settled effect endpoints; select relative to the status holder.");
            Timing = timing; DamagePerStack = damagePerStack;
        }
    }

    /// <summary>Actual boundary fact; periodic children refer to this revision and sequence, with root index -1.</summary>
    public sealed class CombatTimingEvent : CombatRuleEvent
    {
        public CombatTimingSignal Signal { get; }
        internal CombatTimingEvent(long revision, int sequenceIndex, CombatTimingSignal signal)
            : base(revision, -1, null, sequenceIndex) { Signal = signal; }
    }
}
