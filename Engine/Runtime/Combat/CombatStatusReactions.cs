using System;
using System.Collections.Generic;

namespace PokerEngine
{
    /// <summary>Immutable reaction behavior. Strength is floor(actual fact * Ratio * status stacks).</summary>
    public abstract class StatusReactionBehaviorSpec : StatusBehaviorSpec
    {
        public decimal Ratio { get; }
        public CombatTargetSpec Target { get; }
        public CombatReactionSourcePolicy SourcePolicy { get; }
        protected StatusReactionBehaviorSpec(decimal ratio, CombatTargetSpec target, CombatReactionSourcePolicy sourcePolicy)
        {
            if (target?.Group == CombatTargetGroup.EventActor || ratio < 0 || !Enum.IsDefined(typeof(CombatReactionSourcePolicy), sourcePolicy))
                throw new ArgumentException("Status reactions require a nonnegative ratio and a supported source policy.");
            Ratio = ratio; Target = target ?? throw new ArgumentNullException(nameof(target)); SourcePolicy = sourcePolicy;
        }
    }

    public sealed class ReflectBlockedDamageSpec : StatusReactionBehaviorSpec
    {
        public ReflectBlockedDamageSpec(decimal ratio, CombatTargetSpec target = null,
            CombatReactionSourcePolicy sourcePolicy = CombatReactionSourcePolicy.RequireLiving)
            : base(ratio, target ?? CombatTargetSpec.EventSource(), sourcePolicy) { }
    }

    public sealed class LifeStealSpec : StatusReactionBehaviorSpec
    {
        public LifeStealSpec(decimal ratio, CombatTargetSpec target = null)
            : base(ratio, target ?? CombatTargetSpec.Self(), CombatReactionSourcePolicy.RequireLiving) { }
    }

    public sealed class OverhealDamageSpec : StatusReactionBehaviorSpec
    {
        public OverhealDamageSpec(decimal ratio, CombatTargetSpec target = null)
            : base(ratio, target ?? CombatTargetSpec.Opponents(), CombatReactionSourcePolicy.RequireLiving) { }
    }

    /// <summary>Pure catalog adapter. The resolver owns activation consumption, queue order and atomic commit.</summary>
    internal sealed class CombatStatusReactionProvider : ICombatReactionProvider
    {
        private readonly CombatStatusCatalog catalog;
        internal CombatStatusReactionProvider(CombatStatusCatalog catalog)
        { this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog)); }

        public IEnumerable<CombatReaction> React(CombatReactionContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var fact = context.Event as CombatEvent;
            if (fact == null || fact.Provenance == null) yield break;
            if (fact.Effect.Kind == CombatEffectKind.Damage && fact.Provenance.Origin == CombatEffectOrigin.Primary)
            {
                // A defender's reaction precedes the attacker's healing, regardless of status insertion order.
                foreach (var reaction in Reactions<ReflectBlockedDamageSpec>(context, fact.Effect.Target, fact.BlockAbsorbed,
                    CombatEffectKind.Damage, CombatEffectOrigin.Reflection, false)) yield return reaction;
                foreach (var reaction in Reactions<LifeStealSpec>(context, fact.Effect.Source, fact.DamageToHealth,
                    CombatEffectKind.Heal, CombatEffectOrigin.Lifesteal, true)) yield return reaction;
            }
            else if (fact.Effect.Kind == CombatEffectKind.Heal &&
                (fact.Provenance.Origin == CombatEffectOrigin.Primary || fact.Provenance.Origin == CombatEffectOrigin.Lifesteal))
            {
                foreach (var reaction in Reactions<OverhealDamageSpec>(context, fact.Effect.Target, fact.Overheal,
                    CombatEffectKind.Damage, CombatEffectOrigin.Overheal, false)) yield return reaction;
            }
        }

        private IEnumerable<CombatReaction> Reactions<T>(CombatReactionContext context, ParticipantId owner, long basis,
            CombatEffectKind kind, CombatEffectOrigin origin, bool canTriggerReactions) where T : StatusReactionBehaviorSpec
        {
            if (basis <= 0) yield break;
            foreach (var status in context.State.Statuses)
            {
                if (status.Target.Kind != EntityKind.Participant || status.Target.Id != owner.Value) continue;
                var definition = catalog.Get(status.DefinitionId);
                foreach (var configured in definition.Behaviors)
                {
                    var behavior = configured as T;
                    if (behavior == null || behavior.Ratio == 0 ||
                        (!context.State.Get(owner).IsAlive && behavior.SourcePolicy == CombatReactionSourcePolicy.RequireLiving)) continue;
                    var targets = CombatTargetResolver.Resolve(context.State, owner, behavior.Target, context.Event);
                    if (targets.Count == 0) continue;
                    long amount = checked((long)decimal.Floor(basis * behavior.Ratio * status.Stacks));
                    if (amount == 0) continue;
                    string ruleId = catalog.Version + "/" + definition.Id.Value + "/" + typeof(T).Name;
                    foreach (var target in targets)
                        yield return CombatReaction.FromStatus(new CombatEffect(kind, owner, target, amount), ruleId, owner,
                            status.Id, origin, canTriggerReactions, behavior.SourcePolicy);
                }
            }
        }
    }
}
