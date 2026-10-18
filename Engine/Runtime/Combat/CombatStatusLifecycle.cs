using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Mode-neutral status maintenance. Callers explicitly identify actual action and expiry boundaries.</summary>
    public static class CombatStatusLifecycle
    {
        public static IReadOnlyList<StatusInstance> Blockers(CombatState state, ParticipantId actor, CombatStatusCatalog catalog)
        {
            CombatEngine.Validate(state); CombatStatusRules.ValidateCatalog(state, catalog);
            var unit = state.Get(actor);
            return Guard.View(!unit.IsAlive || catalog == null ? Array.Empty<StatusInstance>() : state.Statuses.Where(status =>
                CombatStatusRules.Targets(status, actor) && catalog.Get(status.DefinitionId).Behaviors.Any(item => item is BlockActionWindowSpec)).ToArray());
        }

        public static CombatTransition SkipActionWindow(CombatState state, long expectedRevision, ParticipantId actor, CombatStatusCatalog catalog)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            try { CombatEngine.Validate(state); CombatStatusRules.ValidateCatalog(state, catalog); }
            catch (ArgumentException) { return new CombatTransition(state, CombatError.InvalidState); }
            if (state.Revision != expectedRevision) return new CombatTransition(state, CombatError.StaleRevision);
            try
            {
                var blockers = Blockers(state, actor, catalog);
                if (blockers.Count == 0) return new CombatTransition(state, CombatError.InvalidEffect);
                long revision = checked(state.Revision + 1); var statuses = state.Statuses.ToList();
                var events = new List<CombatRuleEvent> { new CombatActionWindowSkippedEvent(revision, 0, actor, blockers.Select(item => item.Id)) };
                foreach (var blocker in blockers)
                    CombatStatusRules.Consume(statuses, blocker, revision, -1, null, events);
                return new CombatTransition(new CombatState(state.Id, revision, state.Combatants, statuses), events: events);
            }
            catch (ArgumentException) { return new CombatTransition(state, CombatError.InvalidEffect); }
            catch (OverflowException) { return new CombatTransition(state, CombatError.Overflow); }
        }

        public static CombatTransition EndPhase(CombatState state, long expectedRevision, string scopeId, string phaseKey) =>
            Expire(state, expectedRevision, scopeId, phaseKey, DurationKind.UntilPhaseEnd);
        public static CombatTransition EndRound(CombatState state, long expectedRevision, string scopeId) =>
            Expire(state, expectedRevision, scopeId, null, DurationKind.UntilRoundEnd);

        private static CombatTransition Expire(CombatState state, long expectedRevision, string scopeId, string phaseKey, DurationKind kind)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            try { CombatEngine.Validate(state); }
            catch (ArgumentException) { return new CombatTransition(state, CombatError.InvalidState); }
            if (state.Revision != expectedRevision) return new CombatTransition(state, CombatError.StaleRevision);
            try
            {
                Guard.Text(scopeId, nameof(scopeId));
                if (kind == DurationKind.UntilPhaseEnd) Guard.Text(phaseKey, nameof(phaseKey));
                var expired = state.Statuses.Where(status => status.Duration.Kind == kind && status.Duration.ScopeId == scopeId &&
                    (kind != DurationKind.UntilPhaseEnd || status.Duration.PhaseKey == phaseKey)).ToArray();
                if (expired.Length == 0) return new CombatTransition(state);
                long revision = checked(state.Revision + 1);
                var ids = new HashSet<StatusInstanceId>(expired.Select(status => status.Id));
                var events = expired.Select((status, index) => new CombatStatusChangedEvent(revision, -1, index, null,
                    status, null, CombatStatusChangeReason.Expired, scopeId, phaseKey));
                return new CombatTransition(new CombatState(state.Id, revision, state.Combatants,
                    state.Statuses.Where(status => !ids.Contains(status.Id))), events: events);
            }
            catch (ArgumentException) { return new CombatTransition(state, CombatError.InvalidEffect); }
            catch (OverflowException) { return new CombatTransition(state, CombatError.Overflow); }
        }
    }

    internal static class CombatStatusRules
    {
        internal static bool Targets(StatusInstance status, ParticipantId actor) =>
            status.Target.Kind == EntityKind.Participant && status.Target.Id == actor.Value;

        internal static void ValidateCatalog(CombatState state, CombatStatusCatalog catalog)
        {
            if (catalog == null) return;
            foreach (var status in state.Statuses)
            {
                var definition = catalog.Get(status.DefinitionId);
                definition.ValidateTriggerBudget(status.Duration.Kind == DurationKind.TriggerCount);
                if (definition.Behaviors.Count > 0 && status.Target.Kind != EntityKind.Participant)
                    throw new ArgumentException("Behavior statuses require a participant target.");
                if (definition.Behaviors.Any(item => item is PeriodicDamageSpec) && status.Source.Kind != EntityKind.Participant)
                    throw new ArgumentException("Periodic damage requires a participant applier.");
            }
            if (state.Statuses.Where(status => catalog.Get(status.DefinitionId).ReapplyPolicy == StatusReapplyPolicy.KeepLonger)
                .GroupBy(status => new { status.DefinitionId, status.Target.Kind, status.Target.Id }).Any(group => group.Count() > 1))
                throw new ArgumentException("KeepLonger status definitions require one instance per target.");
        }

        internal static void Apply(CombatState state, List<StatusInstance> statuses, CombatEffect effect, CombatStatusCatalog catalog,
            long revision, int effectIndex, List<CombatRuleEvent> events, CombatEffectProvenance provenance = null)
        {
            var incoming = effect.Status;
            var definition = catalog?.Get(incoming.DefinitionId);
            if (definition != null)
            {
                definition.ValidateTriggerBudget(incoming.Duration.Kind == DurationKind.TriggerCount);
                // Stable stored order chooses one actual interceptor; one incoming status spends at most one immunity charge.
                var blocker = statuses.FirstOrDefault(status => Targets(status, effect.Target) &&
                    catalog.Get(status.DefinitionId).Behaviors.OfType<PreventStatusApplicationSpec>()
                        .Any(behavior => (behavior.BlockedTags & definition.Tags) != 0));
                if (blocker != null)
                {
                    events.Add(new CombatStatusBlockedEvent(revision, effectIndex, events.Count, effect, blocker.Id, provenance));
                    Consume(statuses, blocker, revision, effectIndex, effect, events, provenance);
                    return;
                }
            }
            var existing = definition != null && definition.ReapplyPolicy == StatusReapplyPolicy.KeepLonger
                ? statuses.FirstOrDefault(status => status.DefinitionId == incoming.DefinitionId && Targets(status, effect.Target)) : null;
            if (statuses.Any(status => status.Id == incoming.Id && !ReferenceEquals(status, existing)))
                throw new ArgumentException("Status instance ID is already present.");
            if (existing != null)
            {
                var duration = Longer(state.Id, existing.Duration, incoming.Duration);
                var refreshed = new StatusInstance(existing.Id, existing.DefinitionId, incoming.Source, existing.Target,
                    Math.Max(existing.Stacks, incoming.Stacks), duration);
                statuses[statuses.IndexOf(existing)] = refreshed;
                events.Add(new CombatStatusChangedEvent(revision, effectIndex, events.Count, effect, existing, refreshed, CombatStatusChangeReason.Refreshed,
                    provenance: provenance));
                return;
            }
            if (statuses.Count >= 256) throw new ArgumentException("Status capacity exceeded.");
            statuses.Add(incoming);
            events.Add(new CombatStatusEvent(revision, effectIndex, effect, events.Count, provenance));
        }

        internal static void Consume(List<StatusInstance> statuses, StatusInstance status, long revision, int effectIndex,
            CombatEffect effect, List<CombatRuleEvent> events, CombatEffectProvenance provenance = null)
        {
            if (status.Duration.Kind != DurationKind.TriggerCount) return;
            int index = statuses.FindIndex(item => item.Id == status.Id);
            if (index < 0) throw new ArgumentException("Unknown status instance.");
            var after = status.Duration.RemainingTriggers.Value == 1 ? null : new StatusInstance(status.Id, status.DefinitionId,
                status.Source, status.Target, status.Stacks, StatusDuration.ForTriggers(status.Duration.RemainingTriggers.Value - 1));
            if (after == null) statuses.RemoveAt(index); else statuses[index] = after;
            events.Add(new CombatStatusChangedEvent(revision, effectIndex, events.Count, effect, status, after, CombatStatusChangeReason.Consumed,
                provenance: provenance));
        }

        private static StatusDuration Longer(string combatId, StatusDuration before, StatusDuration incoming)
        {
            if (before.Kind != incoming.Kind) throw new ArgumentException("Reapplied lifetimes must have the same kind.");
            switch (before.Kind)
            {
                case DurationKind.UntilRemoved: return before;
                case DurationKind.TriggerCount: return incoming.RemainingTriggers > before.RemainingTriggers ? incoming : before;
                case DurationKind.UntilPhaseEnd:
                    if (before.PhaseKey != incoming.PhaseKey) throw new ArgumentException("Reapplied lifetimes must close at the same phase.");
                    break;
                case DurationKind.UntilRoundEnd: break;
                default: throw new ArgumentException("Unknown status lifetime.");
            }
            if (before.ScopeId == incoming.ScopeId) return before;
            return RoundNumber(combatId, incoming.ScopeId) > RoundNumber(combatId, before.ScopeId) ? incoming : before;
        }
        private static int RoundNumber(string combatId, string scope)
        {
            var prefix = combatId + "/round/"; int round;
            if (!scope.StartsWith(prefix, StringComparison.Ordinal) || scope.Length != prefix.Length + 10 ||
                !int.TryParse(scope.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out round) || round <= 0)
                throw new ArgumentException("Comparing lifetimes requires stable battle/round/0000000001 scopes.");
            return round;
        }
    }
}
