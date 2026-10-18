using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Server/local-authority context. This contains private state and is not a client DTO.</summary>
    public sealed class AbilityContext
    {
        public AbilityWorld State { get; }
        public ContentCatalog Catalog { get; }
        public AbilityInstance Ability { get; }
        public AbilityDefinition Definition { get; }
        public AbilityContext(AbilityWorld state, ContentCatalog catalog, AbilityInstanceId abilityId)
        {
            AbilityWorldValidator.Validate(state, catalog);
            State = state; Catalog = catalog;
            Ability = state.Abilities.FirstOrDefault(a => a.Id == abilityId) ?? throw new ArgumentException("Unknown ability.");
            AbilityDefinition definition;
            if (!catalog.TryGetAbility(Ability.DefinitionId, out definition)) throw new ArgumentException("Ability has no behavioral definition.");
            Definition = definition;
        }
    }

    public interface IAbilityCondition { bool IsSatisfied(AbilityContext context); }
    public interface IAbilityTargetProvider { SelectionSpec DescribeTargets(AbilityContext context); }
    public interface IActivatedAbility { IReadOnlyList<EffectIntent> Plan(AbilityContext context); }
    public interface ITriggeredAbility { IReadOnlyList<EffectIntent> Plan(AbilityContext context); }
    public interface IRuleModifier<TQuery, TValue> { TValue Apply(AbilityContext context, TQuery query, TValue current); }
    public interface IOutcomeModifier<TOutcome> { TOutcome Apply(AbilityContext context, TOutcome proposedOutcome); }
    public interface IEffectReplacement { EffectReplacement Replace(AbilityContext context, EffectIntent proposedEffect); }

    public enum ReplacementKind { Keep = 1, Cancel, Replace }
    public sealed class EffectReplacement
    {
        public ReplacementKind Kind { get; }
        public IReadOnlyList<EffectIntent> Effects { get; }
        private EffectReplacement(ReplacementKind kind, IEnumerable<EffectIntent> effects)
        {
            Kind = kind; Effects = Guard.View(Guard.Copy(effects, nameof(effects)));
            if (kind == ReplacementKind.Replace && Effects.Count == 0) throw new ArgumentException("Use Cancel for an empty replacement.");
        }
        public static EffectReplacement Keep() => new EffectReplacement(ReplacementKind.Keep, new EffectIntent[0]);
        public static EffectReplacement Cancel() => new EffectReplacement(ReplacementKind.Cancel, new EffectIntent[0]);
        public static EffectReplacement With(IEnumerable<EffectIntent> effects) => new EffectReplacement(ReplacementKind.Replace, effects);
    }

    public sealed class AbilityHook<THandler> where THandler : class
    {
        public AbilityInstanceId AbilityId { get; }
        public THandler Handler { get; }
        public IReadOnlyList<IAbilityCondition> AdditionalConditions { get; }
        public AbilityHook(AbilityInstanceId abilityId, THandler handler, IEnumerable<IAbilityCondition> additionalConditions = null)
        {
            if (!abilityId.IsValid) throw new ArgumentException("Invalid hook identity.");
            AbilityId = abilityId; Handler = handler ?? throw new ArgumentNullException(nameof(handler));
            AdditionalConditions = Guard.View(Guard.Copy(additionalConditions ?? new IAbilityCondition[0], nameof(additionalConditions)));
        }
    }

    /// <summary>Deterministic proposal composition. No random draws, cost payment, counters or state transitions.</summary>
    public static class AbilityHooks
    {
        public const int MaxProposals = 64;

        private static bool CanRun<T>(AbilityWorld state, ContentCatalog catalog, AbilityHook<T> hook, AbilityMechanism mechanism) where T : class
        {
            if (AbilityEligibility.Check(state, catalog, hook.AbilityId, mechanism) != AbilityDenial.None) return false;
            var context = new AbilityContext(state, catalog, hook.AbilityId);
            return hook.AdditionalConditions.All(c => c.IsSatisfied(context));
        }

        private static EffectIntent[] CopyPlan(AbilityContext context, IEnumerable<EffectIntent> effects)
        {
            var copy = Guard.Copy(effects, nameof(effects));
            if (copy.Length > MaxProposals || copy.Any(e => e.SourceAbilityId != context.Ability.Id))
                throw new InvalidOperationException("Ability plan exceeds its limit or has an incorrect source.");
            return copy;
        }

        private static IEnumerable<AbilityHook<T>> Ordered<T>(AbilityWorld state, ContentCatalog catalog,
            IEnumerable<AbilityHook<T>> registrations, AbilityMechanism mechanism) where T : class
        {
            AbilityWorldValidator.Validate(state, catalog);
            var hooks = Guard.Copy(registrations, nameof(registrations));
            if (hooks.Select(h => h.AbilityId).Distinct().Count() != hooks.Length) throw new ArgumentException("Duplicate hook registration.");
            var contexts = hooks.ToDictionary(h => h.AbilityId, h => new AbilityContext(state, catalog, h.AbilityId));
            if (contexts.Values.Any(c => c.Definition.Mechanism != mechanism)) throw new ArgumentException("Wrong hook mechanism.");
            return hooks.OrderBy(h => contexts[h.AbilityId].Definition.Priority)
                .ThenBy(h => state.Participants.ToList().FindIndex(p => p.Id == contexts[h.AbilityId].Ability.OwnerId))
                .ThenBy(h => contexts[h.AbilityId].Ability.Order).ThenBy(h => h.AbilityId.Value, StringComparer.Ordinal);
        }

        public static IReadOnlyList<EffectIntent> PlanActivated(AbilityContext context, ParticipantId authenticatedActor, IActivatedAbility handler,
            IEnumerable<IAbilityCondition> additionalConditions = null)
        {
            if (context == null || handler == null) throw new ArgumentNullException();
            var denial = AbilityEligibility.Check(context.State, context.Catalog, context.Ability.Id, AbilityMechanism.Activated, authenticatedActor);
            if (denial != AbilityDenial.None) throw new InvalidOperationException("Ability denied: " + denial);
            if (!Guard.Copy(additionalConditions ?? new IAbilityCondition[0], nameof(additionalConditions)).All(c => c.IsSatisfied(context)))
                throw new InvalidOperationException("Ability denied: additional condition failed.");
            return Guard.View(CopyPlan(context, handler.Plan(context)));
        }

        public static IReadOnlyList<EffectIntent> PlanTriggered(AbilityWorld state, ContentCatalog catalog, IEnumerable<AbilityHook<ITriggeredAbility>> hooks)
        {
            var effects = new List<EffectIntent>();
            foreach (var hook in Ordered(state, catalog, hooks, AbilityMechanism.Triggered))
                if (CanRun(state, catalog, hook, AbilityMechanism.Triggered))
                {
                    var context = new AbilityContext(state, catalog, hook.AbilityId);
                    effects.AddRange(CopyPlan(context, hook.Handler.Plan(context)));
                    if (effects.Count > MaxProposals) throw new InvalidOperationException("Trigger expansion exceeds its proposal limit.");
                }
            return effects.AsReadOnly();
        }

        public static TValue Modify<TQuery, TValue>(AbilityWorld state, ContentCatalog catalog, TQuery query, TValue initial,
            IEnumerable<AbilityHook<IRuleModifier<TQuery, TValue>>> hooks)
        {
            TValue value = initial;
            foreach (var hook in Ordered(state, catalog, hooks, AbilityMechanism.RuleModifier))
                if (CanRun(state, catalog, hook, AbilityMechanism.RuleModifier))
                    value = hook.Handler.Apply(new AbilityContext(state, catalog, hook.AbilityId), query, value);
            return value;
        }

        public static TOutcome ModifyOutcome<TOutcome>(AbilityWorld state, ContentCatalog catalog, TOutcome initial,
            IEnumerable<AbilityHook<IOutcomeModifier<TOutcome>>> hooks)
        {
            TOutcome value = initial;
            foreach (var hook in Ordered(state, catalog, hooks, AbilityMechanism.OutcomeModifier))
                if (CanRun(state, catalog, hook, AbilityMechanism.OutcomeModifier))
                    value = hook.Handler.Apply(new AbilityContext(state, catalog, hook.AbilityId), value);
            return value;
        }

        /// <summary>Each hook sees each current proposal once. Replacements only visit later hooks; cancellation is terminal.</summary>
        public static IReadOnlyList<EffectIntent> Replace(AbilityWorld state, ContentCatalog catalog, EffectIntent initial,
            IEnumerable<AbilityHook<IEffectReplacement>> hooks)
        {
            if (initial == null) throw new ArgumentNullException(nameof(initial));
            var current = new List<EffectIntent> { initial };
            foreach (var hook in Ordered(state, catalog, hooks, AbilityMechanism.EffectReplacement))
            {
                if (!CanRun(state, catalog, hook, AbilityMechanism.EffectReplacement)) continue;
                var next = new List<EffectIntent>(); var context = new AbilityContext(state, catalog, hook.AbilityId);
                foreach (var effect in current)
                {
                    var replacement = hook.Handler.Replace(context, effect) ?? throw new InvalidOperationException("Replacement handler returned null.");
                    if (replacement.Kind == ReplacementKind.Keep) next.Add(effect);
                    else if (replacement.Kind == ReplacementKind.Replace) next.AddRange(replacement.Effects);
                    if (next.Count > MaxProposals) throw new InvalidOperationException("Replacement expansion exceeds its proposal limit.");
                }
                current = next;
            }
            return current.AsReadOnly();
        }
    }
}
