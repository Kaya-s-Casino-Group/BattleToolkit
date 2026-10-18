using System;
using System.Linq;

namespace PokerEngine
{
    /// <summary>The mode chooses phase and round boundaries; shared expiry never infers a mode's progression.</summary>
    public static class AbilityLifecycle
    {
        public static AbilityWorld EndPhase(AbilityWorld world, string phaseKey)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            Guard.Text(phaseKey, nameof(phaseKey)); RequireNoSelection(world);
            var edit = new AbilityWorldEdit(world);
            edit.Statuses.RemoveAll(status => status.Duration.Kind == DurationKind.UntilPhaseEnd &&
                status.Duration.ScopeId == world.ScopeId && status.Duration.PhaseKey == phaseKey);
            edit.Knowledge.RemoveAll(grant => grant.ScopeId == world.ScopeId && grant.UntilPhaseEnd == phaseKey);
            return edit.Build();
        }
        public static AbilityWorld EndRound(AbilityWorld world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world)); RequireNoSelection(world);
            var edit = new AbilityWorldEdit(world);
            edit.Statuses.RemoveAll(status => status.Duration.ScopeId == world.ScopeId &&
                (status.Duration.Kind == DurationKind.UntilRoundEnd || status.Duration.Kind == DurationKind.UntilPhaseEnd));
            edit.Knowledge.RemoveAll(grant => grant.ScopeId == world.ScopeId);
            return edit.Build();
        }
        public static AbilityWorld BeginRound(AbilityWorld world, bool decrementCooldowns = true)
        {
            if (world == null) throw new ArgumentNullException(nameof(world)); RequireNoSelection(world);
            var edit = new AbilityWorldEdit(world);
            edit.Abilities = edit.Abilities.Select(a => new AbilityInstance(a.Id, a.DefinitionId, a.OwnerId, a.Source,
                a.RemainingCharges, 0, decrementCooldowns ? Math.Max(0, a.CooldownRounds - 1) : a.CooldownRounds,
                a.Memory.ResetRound(), a.Order)).ToList();
            return edit.Build();
        }
        private static void RequireNoSelection(AbilityWorld world)
        {
            if (world.ActiveDecision != null && world.ActiveDecision.Kind != DecisionKind.ModeAction || world.Resolution.PendingEffects.Count > 0)
                throw new ArgumentException("Resolve selections and pending effects before closing a lifecycle boundary.");
        }
    }
}
