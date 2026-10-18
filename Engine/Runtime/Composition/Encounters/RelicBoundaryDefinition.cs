using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PokerEngine
{
    public enum RelicBoundaryTiming { BattleStarted = 1, PhaseStarted, PhaseEnding }
    public enum RelicPhaseRelation { OwnerTeam = 1, OpponentTeam, Any }

    /// <summary>A lifecycle subscription with no fictional effect source, target or origin.</summary>
    public sealed class RelicBoundaryTriggerSpec
    {
        public string Id { get; }
        public RelicBoundaryTiming Timing { get; }
        public RelicPhaseRelation? Phase { get; }
        public IReadOnlyList<RelicTriggeredEffectSpec> Effects { get; }
        public IReadOnlyList<RelicTriggerConditionSpec> Conditions { get; }
        public int Priority { get; }
        public bool CanTriggerReactions { get; }
        public CombatReactionSourcePolicy SourcePolicy { get; }
        public RelicBoundaryTriggerSpec(string id, RelicBoundaryTiming timing, IEnumerable<RelicTriggeredEffectSpec> effects,
            RelicPhaseRelation? phase = null, IEnumerable<RelicTriggerConditionSpec> conditions = null, int priority = 0,
            bool canTriggerReactions = false, CombatReactionSourcePolicy sourcePolicy = CombatReactionSourcePolicy.RequireLiving)
        {
            Id = Guard.Text(id, nameof(id));
            var steps = Guard.Copy(effects, nameof(effects)); var predicates = Guard.Copy(conditions ?? Array.Empty<RelicTriggerConditionSpec>(), nameof(conditions));
            if (!Enum.IsDefined(typeof(RelicBoundaryTiming), timing) || !Enum.IsDefined(typeof(CombatReactionSourcePolicy), sourcePolicy) ||
                (timing == RelicBoundaryTiming.BattleStarted ? phase.HasValue : !phase.HasValue) ||
                phase.HasValue && !Enum.IsDefined(typeof(RelicPhaseRelation), phase.Value) || steps.Length < 1 || steps.Length > 64 || predicates.Length > 16 ||
                steps.Any(step => step.Target.Group == CombatTargetGroup.EventSource || step.Target.Group == CombatTargetGroup.EventTarget || step.Target.Group == CombatTargetGroup.EventActor) ||
                predicates.Any(item => item.Kind != RelicTriggerConditionKind.OwnerHealthAtMost && item.Kind != RelicTriggerConditionKind.OwnerHasStatus))
                throw new ArgumentException("Invalid relic boundary trigger or unavailable boundary facts.");
            Timing = timing; Phase = phase; Effects = Guard.View(steps); Conditions = Guard.View(predicates);
            Priority = priority; CanTriggerReactions = canTriggerReactions; SourcePolicy = sourcePolicy;
        }
        internal bool Matches(CombatState combat, ParticipantId owner, CombatTimingSignal signal)
        {
            var kind = Timing == RelicBoundaryTiming.BattleStarted ? CombatTimingKind.BattleStarted :
                Timing == RelicBoundaryTiming.PhaseStarted ? CombatTimingKind.PhaseStarted : CombatTimingKind.PhaseEnding;
            if (signal.Kind != kind) return false;
            if (!Phase.HasValue || Phase == RelicPhaseRelation.Any) return true;
            bool same = signal.Participants.All(id => combat.Get(id).TeamId == combat.Get(owner).TeamId);
            bool other = signal.Participants.All(id => combat.Get(id).TeamId != combat.Get(owner).TeamId);
            return Phase == RelicPhaseRelation.OwnerTeam ? same : other;
        }
        internal void Write(BinaryWriter writer)
        {
            writer.Write(Id); writer.Write((int)Timing); writer.Write(Phase.HasValue); if (Phase.HasValue) writer.Write((int)Phase.Value);
            writer.Write(Priority); writer.Write(CanTriggerReactions); writer.Write((int)SourcePolicy);
            writer.Write(Conditions.Count); foreach (var condition in Conditions) condition.Write(writer);
            writer.Write(Effects.Count); foreach (var effect in Effects) effect.Write(writer);
        }
    }
}
