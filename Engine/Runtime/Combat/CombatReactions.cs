using System;
using System.Collections.Generic;

namespace PokerEngine
{
    /// <summary>Settlement provenance, independent of damage's AttackOrigin and target selection.</summary>
    public enum CombatEffectOrigin { Primary = 0, Triggered = 1, Periodic = 2, Reflection = 3, Lifesteal = 4, Overheal = 5 }
    public enum CombatReactionSourcePolicy { RequireLiving = 0, AllowDefeated = 1 }

    /// <summary>
    /// Pure authority rule. Return effects in stable rule/acquisition order, already bound to targets in State.
    /// No state writes, external side effects or randomness. Return an empty sequence when nothing triggers.
    /// Only effect settlement facts enter this hook; action-window/expiry clocks are separate.
    /// </summary>
    public interface ICombatReactionProvider
    {
        IEnumerable<CombatReaction> React(CombatReactionContext context);
    }

    public sealed class CombatReactionContext
    {
        /// <summary>Provisional immutable state after the entire parent effect; revision is still the input revision.</summary>
        public CombatState State { get; }
        public CombatRuleEvent Event { get; }
        internal CombatReactionContext(CombatState state, CombatRuleEvent settledEvent)
        { State = state; Event = settledEvent; }
    }

    /// <summary>
    /// Authority-generated reaction, not a player command. Damage is final outgoing damage and is not run
    /// through the primary attack modifier again. Incoming damage and block still apply at settlement.
    /// Descendants and defeated sources require explicit opt-in; neither is implied by Origin.
    /// </summary>
    public sealed class CombatReaction
    {
        public CombatEffect Effect { get; }
        public string RuleId { get; }
        public ParticipantId Owner { get; }
        public CombatEffectOrigin Origin { get; }
        public bool CanTriggerReactions { get; }
        public CombatReactionSourcePolicy SourcePolicy { get; }
        /// <summary>Optional stable status/relic instance identifier; RuleId identifies its behavior/configuration.</summary>
        public string SourceInstanceId { get; }
        // Only the built-in status bridge may request charge consumption. Arbitrary attribution strings
        // from external reaction rules are never interpreted as permission to modify a status instance.
        internal StatusInstanceId? TriggerStatusId { get; }

        public CombatReaction(CombatEffect effect, string ruleId, ParticipantId owner,
            CombatEffectOrigin origin = CombatEffectOrigin.Triggered, bool canTriggerReactions = false,
            CombatReactionSourcePolicy sourcePolicy = CombatReactionSourcePolicy.RequireLiving, string sourceInstanceId = null)
        {
            Effect = effect ?? throw new ArgumentNullException(nameof(effect));
            RuleId = Guard.Text(ruleId, nameof(ruleId));
            if (!owner.IsValid || !Enum.IsDefined(typeof(CombatEffectOrigin), origin) || origin == CombatEffectOrigin.Primary ||
                !Enum.IsDefined(typeof(CombatReactionSourcePolicy), sourcePolicy)) throw new ArgumentException("Invalid reaction provenance.");
            SourceInstanceId = sourceInstanceId == null ? null : Guard.Text(sourceInstanceId, nameof(sourceInstanceId));
            Owner = owner; Origin = origin; CanTriggerReactions = canTriggerReactions; SourcePolicy = sourcePolicy;
        }

        private CombatReaction(CombatEffect effect, string ruleId, ParticipantId owner, StatusInstanceId statusId,
            CombatEffectOrigin origin, bool canTriggerReactions, CombatReactionSourcePolicy sourcePolicy)
            : this(effect, ruleId, owner, origin, canTriggerReactions, sourcePolicy, statusId.Value)
        {
            if (!statusId.IsValid) throw new ArgumentException("A status reaction requires a valid instance ID.");
            TriggerStatusId = statusId;
        }
        internal static CombatReaction FromStatus(CombatEffect effect, string ruleId, ParticipantId owner, StatusInstanceId statusId,
            CombatEffectOrigin origin, bool canTriggerReactions = false,
            CombatReactionSourcePolicy sourcePolicy = CombatReactionSourcePolicy.RequireLiving) =>
            new CombatReaction(effect, ruleId, owner, statusId, origin, canTriggerReactions, sourcePolicy);
    }

    /// <summary>Separate from the 1..64 primary-effect limit. Counts every emitted reaction, even if later skipped.</summary>
    public sealed class CombatReactionLimits
    {
        public int MaxEffects { get; }
        public int MaxDepth { get; }
        public CombatReactionLimits(int maxEffects = 256, int maxDepth = 16)
        {
            if (maxEffects < 1 || maxEffects > 4096 || maxDepth < 1 || maxDepth > 64)
                throw new ArgumentOutOfRangeException(nameof(maxEffects), "Reaction limits must be 1..4096 effects and 1..64 levels.");
            MaxEffects = maxEffects; MaxDepth = maxDepth;
        }
    }

    /// <summary>Assigned by the resolver. Parent (revision, sequence) identifies an actual settled fact.</summary>
    public sealed class CombatEffectProvenance
    {
        public CombatEffectOrigin Origin { get; }
        public int RootEffectIndex { get; }
        public int Depth { get; }
        public long? ParentRevision { get; }
        public int? ParentSequenceIndex { get; }
        public string RuleId { get; }
        public ParticipantId? Owner { get; }
        public string SourceInstanceId { get; }
        /// <summary>Actual status activation identity; null for primary and external-provider effects.</summary>
        public StatusInstanceId? TriggerStatusId { get; }
        /// <summary>Shared by every effect in one transaction-runtime activation.</summary>
        public string ActivationId { get; }
        public bool CanTriggerReactions { get; }
        public CombatReactionSourcePolicy SourcePolicy { get; }

        internal CombatEffectProvenance(int rootEffectIndex)
        {
            Origin = CombatEffectOrigin.Primary; RootEffectIndex = rootEffectIndex;
            CanTriggerReactions = true; SourcePolicy = CombatReactionSourcePolicy.RequireLiving;
        }
        internal CombatEffectProvenance(CombatReaction reaction, CombatRuleEvent parent, string activationId = null)
        {
            if (parent.Provenance == null && !(parent is CombatTimingEvent))
                throw new ArgumentException("Derived effects require an effect fact or a clock boundary.");
            Origin = reaction.Origin; RootEffectIndex = parent.Provenance?.RootEffectIndex ?? -1; Depth = (parent.Provenance?.Depth ?? 0) + 1;
            ParentRevision = parent.Revision; ParentSequenceIndex = parent.SequenceIndex;
            RuleId = reaction.RuleId; Owner = reaction.Owner; SourceInstanceId = reaction.SourceInstanceId;
            TriggerStatusId = reaction.TriggerStatusId;
            ActivationId = activationId;
            CanTriggerReactions = reaction.CanTriggerReactions; SourcePolicy = reaction.SourcePolicy;
        }
    }
}
