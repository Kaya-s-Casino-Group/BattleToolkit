using System;
using System.Linq;

namespace PokerEngine
{
    public enum AbilityDenial { None = 0, UnknownAbility, MissingDefinition, WrongMechanism, WrongOwner, MatchNotRunning,
        ModeDisabled, WrongWindow, WrongAction, InactiveOwner, NoCharges, Cooldown, ConditionFailed, ResolutionPending }

    /// <summary>Pure gate check. Does not pay costs, consume charges, or authorize a network caller.</summary>
    public static class AbilityEligibility
    {
        public static AbilityDenial Check(AbilityWorld state, ContentCatalog catalog, AbilityInstanceId id,
            AbilityMechanism mechanism, ParticipantId? requestingPlayer = null)
        {
            if (state == null || catalog == null) throw new ArgumentNullException();
            AbilityWorldValidator.Validate(state, catalog);
            var ability = state.Abilities.FirstOrDefault(a => a.Id == id);
            if (ability == null) return AbilityDenial.UnknownAbility;
            AbilityDefinition definition;
            if (!catalog.TryGetAbility(ability.DefinitionId, out definition)) return AbilityDenial.MissingDefinition;
            if (definition.Mechanism != mechanism) return AbilityDenial.WrongMechanism;
            if ((mechanism == AbilityMechanism.Activated && requestingPlayer != ability.OwnerId) ||
                (mechanism != AbilityMechanism.Activated && requestingPlayer.HasValue)) return AbilityDenial.WrongOwner;
            if (!state.IsRunning) return AbilityDenial.MatchNotRunning;
            if (mechanism == AbilityMechanism.Activated &&
                ((state.ActiveDecision != null && state.ActiveDecision.Kind != DecisionKind.ModeAction) || state.Resolution.PendingEffects.Count > 0))
                return AbilityDenial.ResolutionPending;
            var mode = state.Mode == MatchMode.Solo ? AbilityModes.Solo : AbilityModes.Pvp;
            if ((definition.Modes & mode) == 0) return AbilityDenial.ModeDisabled;
            if (state.AbilityWindow == null || !definition.Windows.Contains(state.AbilityWindow.Timing)) return AbilityDenial.WrongWindow;
            if (definition.RequiredAction != null && definition.RequiredAction != state.AbilityWindow.Action) return AbilityDenial.WrongAction;
            var owner = state.Participants.First(p => p.Id == ability.OwnerId);
            if (!owner.Active) return AbilityDenial.InactiveOwner;
            if (ability.RemainingCharges == 0) return AbilityDenial.NoCharges;
            if (ability.CooldownRounds > 0) return AbilityDenial.Cooldown;
            foreach (var condition in definition.Conditions)
            {
                bool satisfied;
                switch (condition.Kind)
                {
                    case ConditionKind.MinimumEnergy: satisfied = owner.Resources.Energy >= condition.Threshold; break;
                    case ConditionKind.UsesThisRoundBelow: satisfied = ability.UsesThisRound < condition.Threshold; break;
                    case ConditionKind.CounterAtLeast: satisfied = ability.Memory.CounterValue(condition.MemoryKey) >= condition.Threshold; break;
                    case ConditionKind.HasMark: satisfied = ability.Memory.Marks.Any(m => m.Key == condition.MemoryKey); break;
                    case ConditionKind.OwnerIsEventActor: satisfied = state.AbilityWindow.EventActorId == owner.Id; break;
                    case ConditionKind.OwnerIsCurrentActor: satisfied = state.CurrentActorId == owner.Id; break;
                    default: satisfied = false; break;
                }
                if (!satisfied) return AbilityDenial.ConditionFailed;
            }
            return AbilityDenial.None;
        }
    }
}
