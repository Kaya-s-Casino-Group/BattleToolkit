using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public enum CombatTargetGroup { Self = 1, EventSource, EventTarget, Opponents, Allies, EventActor }
    public enum CombatTargetSelection { FirstLiving = 1, LowestHealth, HighestHealth, AllLiving }

    /// <summary>Automatic targets relative to a reaction owner. Player requests and actor policies belong to Composition.</summary>
    public sealed class CombatTargetSpec
    {
        public CombatTargetGroup Group { get; }
        public CombatTargetSelection Selection { get; }

        private CombatTargetSpec(CombatTargetGroup group, CombatTargetSelection selection)
        {
            if (!Enum.IsDefined(typeof(CombatTargetGroup), group) || !Enum.IsDefined(typeof(CombatTargetSelection), selection))
                throw new ArgumentException("Unknown automatic combat target selection.");
            Group = group; Selection = selection;
        }

        public static CombatTargetSpec EventActor() => new CombatTargetSpec(CombatTargetGroup.EventActor, CombatTargetSelection.FirstLiving);
        public static CombatTargetSpec Self() => new CombatTargetSpec(CombatTargetGroup.Self, CombatTargetSelection.FirstLiving);
        public static CombatTargetSpec EventSource() => new CombatTargetSpec(CombatTargetGroup.EventSource, CombatTargetSelection.FirstLiving);
        public static CombatTargetSpec EventTarget() => new CombatTargetSpec(CombatTargetGroup.EventTarget, CombatTargetSelection.FirstLiving);
        public static CombatTargetSpec Opponents(CombatTargetSelection selection = CombatTargetSelection.FirstLiving) =>
            new CombatTargetSpec(CombatTargetGroup.Opponents, selection);
        public static CombatTargetSpec Allies(CombatTargetSelection selection = CombatTargetSelection.FirstLiving) =>
            new CombatTargetSpec(CombatTargetGroup.Allies, selection);
    }

    /// <summary>
    /// Resolves against the supplied immutable snapshot. Empty results are valid; fixed event targets never retarget.
    /// Owner death does not prevent selection: effect source eligibility remains the resolver's separate responsibility.
    /// </summary>
    public static class CombatTargetResolver
    {
        public static IReadOnlyList<ParticipantId> Resolve(CombatState state, ParticipantId owner, CombatTargetSpec spec,
            CombatRuleEvent settledEvent = null)
        {
            if (state == null || spec == null) throw new ArgumentNullException();
            if (!owner.IsValid) throw new ArgumentException("Automatic target selection requires a known owner.");
            var ownerState = state.Get(owner);
            if (spec.Group == CombatTargetGroup.Self) return LivingTarget(ownerState);
            if (spec.Group == CombatTargetGroup.EventSource || spec.Group == CombatTargetGroup.EventTarget)
            {
                if (settledEvent == null || settledEvent.Effect == null)
                    throw new ArgumentException("An event-relative target requires a settled effect.");
                var source = state.Get(settledEvent.Effect.Source);
                var target = state.Get(settledEvent.Effect.Target);
                return LivingTarget(spec.Group == CombatTargetGroup.EventSource ? source : target);
            }

            if (spec.Group == CombatTargetGroup.EventActor)
            {
                var clock = settledEvent as CombatTimingEvent;
                if (clock?.Signal.Kind != CombatTimingKind.ActionCompleted || clock.Signal.Participants.Count != 1)
                    throw new ArgumentException("An event actor requires an action-completion clock with one participant.");
                return LivingTarget(state.Get(clock.Signal.Participants[0]));
            }
            var candidates = state.Combatants.Where(unit => unit.IsAlive &&
                (spec.Group == CombatTargetGroup.Opponents ? unit.TeamId != ownerState.TeamId : unit.TeamId == ownerState.TeamId));
            switch (spec.Selection)
            {
                case CombatTargetSelection.FirstLiving: candidates = candidates.Take(1); break;
                // Stable ordering preserves roster order for ties. These compare absolute health, not health percentage.
                case CombatTargetSelection.LowestHealth: candidates = candidates.OrderBy(unit => unit.Health).Take(1); break;
                case CombatTargetSelection.HighestHealth: candidates = candidates.OrderByDescending(unit => unit.Health).Take(1); break;
                case CombatTargetSelection.AllLiving: break;
                default: throw new ArgumentException("Unknown automatic combat target selection.");
            }
            return Guard.View(candidates.Select(unit => unit.Id).ToArray());
        }

        private static IReadOnlyList<ParticipantId> LivingTarget(CombatantState unit) =>
            Guard.View(unit.IsAlive ? new[] { unit.Id } : Array.Empty<ParticipantId>());
    }
}
