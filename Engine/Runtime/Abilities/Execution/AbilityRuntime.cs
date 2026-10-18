using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Typed outcome registration without a dependency on any mode's outcome model.</summary>
    public abstract class AbilityOutcomeHandler
    {
        private AbilityOutcomeHandler() { }
        public static AbilityOutcomeHandler For<T>(IOutcomeModifier<T> handler) => new Typed<T>(handler);
        internal IOutcomeModifier<T> Get<T>() => (this as Typed<T>)?.Handler;
        private sealed class Typed<T> : AbilityOutcomeHandler
        {
            public IOutcomeModifier<T> Handler { get; }
            public Typed(IOutcomeModifier<T> handler) { Handler = handler ?? throw new ArgumentNullException(nameof(handler)); }
        }
    }

    public enum AbilityEventKind { Activated = 1, SelectionOpened, SelectionResolved }
    public sealed class AbilityEvent
    {
        public AbilityEventKind Kind { get; }
        public ParticipantId OwnerId { get; }
        public AbilityInstanceId AbilityId { get; }
        internal AbilityEvent(AbilityEventKind kind, ParticipantId ownerId, AbilityInstanceId abilityId)
        { Kind = kind; OwnerId = ownerId; AbilityId = abilityId; }
    }
    public sealed class AbilityExecutionResult
    {
        public AbilityWorld World { get; }
        public IReadOnlyList<AbilityEvent> Events { get; }
        internal AbilityExecutionResult(AbilityWorld world, IEnumerable<AbilityEvent> events)
        { World = world; Events = Guard.View(Guard.Copy(events, nameof(events))); }
    }

    /// <summary>Runs shared effects atomically. A rejected operation exposes no partial world or events.</summary>
    public sealed class AbilityExecutor
    {
        private readonly AbilityRuntime runtime;
        private readonly ContentCatalog catalog;
        public AbilityExecutor(AbilityRuntime runtime, ContentCatalog catalog)
        { this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime)); this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog)); }
        private AbilityExecutionResult Execute(AbilityWorld world, Func<AbilityExecution, AbilityWorld> execute)
        {
            AbilityWorldValidator.Validate(world, catalog);
            if (!runtime.Supports(world, catalog)) throw new ArgumentException("Unsupported ability behavior.");
            if (world.Resolution.PendingEffects.Count > 0 || world.ActiveDecision != null &&
                world.ActiveDecision.Kind != DecisionKind.ModeAction && world.ActiveDecision.Kind != DecisionKind.SelectTargets)
                throw new ArgumentException("Legacy selections and queued continuations require a mode-specific executor.");
            var events = new List<AbilityEvent>(); var result = execute(new AbilityExecution(runtime, catalog, events));
            AbilityWorldValidator.Validate(result, catalog);
            return new AbilityExecutionResult(result, events);
        }
        public AbilityExecutionResult Triggers(AbilityWorld world) => Execute(world, execution => execution.Triggers(world));
        public AbilityExecutionResult Activate(AbilityWorld world, string windowId, AbilityInstanceId abilityId, ParticipantId actor) =>
            Execute(world, execution => execution.Activate(world, windowId, abilityId, actor));
        public AbilityExecutionResult Select(AbilityWorld world, DecisionId decisionId, ParticipantId actor, IEnumerable<string> keys,
            bool skip = false, bool timeout = false) => Execute(world, execution => execution.Select(world, decisionId, actor, keys, skip, timeout));
    }

    /// <summary>Continuation code is registered by content version; only its step and selection are saved.</summary>
    public interface IAbilitySelectionHandler
    {
        IReadOnlyList<EffectIntent> Resume(AbilityContext context, int step, SelectionResult selection);
    }

    public sealed class AbilityBehavior
    {
        public AbilityDefinitionId DefinitionId { get; }
        public IActivatedAbility Activated { get; }
        public ITriggeredAbility Triggered { get; }
        public IAbilitySelectionHandler Selection { get; }
        public IEffectReplacement Replacement { get; }
        public IRuleModifier<CardFaceQuery, CardFace> FaceModifier { get; }
        public IRuleModifier<AbilityCostQuery, AbilityCost> CostModifier { get; }
        public AbilityOutcomeHandler OutcomeModifier { get; }
        public AbilityCost Cost { get; }
        public int CooldownRounds { get; }
        public AbilityBehavior(AbilityDefinitionId id, IActivatedAbility activated = null, ITriggeredAbility triggered = null,
            IAbilitySelectionHandler selection = null, IEffectReplacement replacement = null,
            IRuleModifier<CardFaceQuery, CardFace> faceModifier = null, IRuleModifier<AbilityCostQuery, AbilityCost> costModifier = null,
            AbilityOutcomeHandler outcomeModifier = null, AbilityCost cost = null, int cooldownRounds = 0)
        {
            if (!id.IsValid || cooldownRounds < 0) throw new ArgumentException("Invalid ability behavior.");
            DefinitionId = id; Activated = activated; Triggered = triggered; Selection = selection; Replacement = replacement;
            FaceModifier = faceModifier; CostModifier = costModifier; OutcomeModifier = outcomeModifier;
            Cost = cost ?? new AbilityCost(Chips.Zero, 0); CooldownRounds = cooldownRounds;
        }
    }

    /// <summary>Immutable code registry. Register the same version on the authority and when restoring.</summary>
    public sealed class AbilityRuntime : ICardFaceResolver
    {
        private readonly Dictionary<AbilityDefinitionId, AbilityBehavior> behaviors;
        public AbilityRuntime(IEnumerable<AbilityBehavior> behaviors)
        { this.behaviors = Guard.Copy(behaviors, nameof(behaviors)).ToDictionary(b => b.DefinitionId); }
        internal AbilityBehavior Get(AbilityDefinitionId id) => behaviors[id];
        public bool Supports(AbilityWorld state, ContentCatalog catalog)
        {
            foreach (var a in state.Abilities)
            {
                AbilityBehavior b; AbilityDefinition d;
                if (!behaviors.TryGetValue(a.DefinitionId, out b) || !catalog.TryGetAbility(a.DefinitionId, out d)) return false;
                if (d.Mechanism == AbilityMechanism.Activated && b.Activated == null ||
                    d.Mechanism == AbilityMechanism.Triggered && b.Triggered == null ||
                    d.Mechanism == AbilityMechanism.EffectReplacement && b.Replacement == null ||
                    d.Mechanism == AbilityMechanism.RuleModifier && b.FaceModifier == null && b.CostModifier == null ||
                    d.Mechanism == AbilityMechanism.OutcomeModifier && b.OutcomeModifier == null) return false;
            }
            return true;
        }
        internal IEnumerable<AbilityHook<T>> Hooks<T>(AbilityWorld state, Func<AbilityBehavior, T> get) where T : class =>
            state.Abilities.Select(a => new { a.Id, Handler = get(Get(a.DefinitionId)) }).Where(x => x.Handler != null)
                .Select(x => new AbilityHook<T>(x.Id, x.Handler));
        public CardFace Resolve(AbilityWorld state, ContentCatalog catalog, CardInstanceId cardId) =>
            AbilityHooks.Modify(state, catalog, new CardFaceQuery(cardId), new BaseCardFaceResolver().Resolve(state, catalog, cardId),
                Hooks(state, b => b.FaceModifier));
        public TOutcome ModifyOutcome<TOutcome>(AbilityWorld state, ContentCatalog catalog, TOutcome outcome) =>
            AbilityHooks.ModifyOutcome(state, catalog, outcome, Hooks(state, b => b.OutcomeModifier?.Get<TOutcome>()));
        internal AbilityCost Cost(AbilityWorld state, ContentCatalog catalog, AbilityInstance a) =>
            AbilityHooks.Modify(state, catalog, new AbilityCostQuery(a.OwnerId, a.Id), Get(a.DefinitionId).Cost, Hooks(state, b => b.CostModifier));
        internal IReadOnlyList<EffectIntent> Replace(AbilityWorld state, ContentCatalog catalog, EffectIntent effect) =>
            AbilityHooks.Replace(state, catalog, effect, Hooks(state, b => b.Replacement));
    }

    /// <summary>Runs a bounded transaction against a private state copy. Selection must be the last proposal in a plan.</summary>
    internal sealed class AbilityExecution
    {
        private readonly AbilityRuntime runtime;
        private readonly ContentCatalog catalog;
        private readonly List<AbilityEvent> events;
        internal AbilityExecution(AbilityRuntime runtime, ContentCatalog catalog, List<AbilityEvent> events)
        { this.runtime = runtime; this.catalog = catalog; this.events = events; }
        private void Emit(AbilityWorld state, AbilityEventKind kind, AbilityInstance a) =>
            events.Add(new AbilityEvent(kind, a.OwnerId, a.Id));
        private AbilityWorld Window(AbilityWorld state)
        {
            if (state.AbilityWindow == null || state.Resolution.WindowId == state.AbilityWindow.Id) return state;
            var edit = new AbilityWorldEdit(state); edit.Resolution = new ResolutionState(new EffectContinuation[0], state.AbilityWindow.Id); return edit.Build();
        }
        internal AbilityWorld Triggers(AbilityWorld state)
        {
            if (state.AbilityWindow == null || state.ActiveDecision?.Kind == DecisionKind.SelectTargets) return state;
            state = Window(state);
            var ordered = state.Abilities.Where(a => catalog.TryGetAbility(a.DefinitionId, out var d) && d.Mechanism == AbilityMechanism.Triggered)
                .OrderBy(a => { catalog.TryGetAbility(a.DefinitionId, out var d); return d.Priority; })
                .ThenBy(a => state.Participants.ToList().FindIndex(p => p.Id == a.OwnerId))
                .ThenBy(a => a.Order).ThenBy(a => a.Id.Value, StringComparer.Ordinal).Select(a => a.Id).ToArray();
            foreach (var id in ordered)
            {
                if (state.Resolution.ProcessedTriggers.Contains(id)) continue;
                var edit = new AbilityWorldEdit(state); var r = edit.Resolution;
                edit.Resolution = new ResolutionState(r.PendingEffects, r.WindowId, r.ProcessedTriggers.Concat(new[] { id }), r.SuspendedDecision, r.StepsUsed);
                state = edit.Build();
                if (AbilityEligibility.Check(state, catalog, id, AbilityMechanism.Triggered) != AbilityDenial.None) continue;
                var context = new AbilityContext(state, catalog, id); var cost = runtime.Cost(state, catalog, context.Ability);
                if (!CanPay(state, context.Ability, cost)) continue;
                state = Begin(state, context.Ability, runtime.Get(context.Ability.DefinitionId).Triggered.Plan(context), cost);
                if (state.ActiveDecision?.Kind == DecisionKind.SelectTargets) break;
            }
            return state;
        }
        internal AbilityWorld Activate(AbilityWorld state, string windowId, AbilityInstanceId abilityId, ParticipantId actor)
        {
            state = Window(state);
            if (state.AbilityWindow?.Id != windowId) throw new ArgumentException("Stale ability window.");
            if (AbilityEligibility.Check(state, catalog, abilityId, AbilityMechanism.Activated, actor) != AbilityDenial.None)
                throw new ArgumentException("Ability activation denied.");
            var context = new AbilityContext(state, catalog, abilityId);
            var cost = runtime.Cost(state, catalog, context.Ability);
            if (!CanPay(state, context.Ability, cost)) throw new ArgumentException("Insufficient ability resources.");
            return Triggers(Begin(state, context.Ability, runtime.Get(context.Ability.DefinitionId).Activated.Plan(context), cost));
        }
        private static bool CanPay(AbilityWorld state, AbilityInstance ability, AbilityCost cost)
        {
            if (cost == null) throw new ArgumentException("Missing cost.");
            var p = state.Participants.First(x => x.Id == ability.OwnerId);
            return p.Resources.Chips >= cost.Chips && p.Resources.Energy >= cost.Energy &&
                (cost.Chips == Chips.Zero || state.AllowChipChanges);
        }
        private AbilityWorld Begin(AbilityWorld state, AbilityInstance ability, IEnumerable<EffectIntent> plan, AbilityCost cost)
        {
            var effects = CheckPlan(plan, ability.Id); var edit = new AbilityWorldEdit(state);
            if (cost.Chips.Value > 0) edit.Resource(ability.OwnerId, ResourceKind.Chips, -cost.Chips.Value);
            if (cost.Energy > 0) edit.Resource(ability.OwnerId, ResourceKind.Energy, -cost.Energy);
            int i = edit.Abilities.FindIndex(a => a.Id == ability.Id);
            edit.Abilities[i] = new AbilityInstance(ability.Id, ability.DefinitionId, ability.OwnerId, ability.Source,
                ability.RemainingCharges.HasValue ? (int?)(ability.RemainingCharges.Value - 1) : null,
                checked(ability.UsesThisRound + 1), runtime.Get(ability.DefinitionId).CooldownRounds == 0 ? 0 : checked(runtime.Get(ability.DefinitionId).CooldownRounds + 1), ability.Memory, ability.Order);
            Emit(state, AbilityEventKind.Activated, ability);
            return Apply(edit.Build(), ability.Id, effects);
        }
        internal AbilityWorld Select(AbilityWorld state, DecisionId decisionId, ParticipantId actor, IEnumerable<string> keys, bool skip, bool timeout = false)
        {
            var decision = state.ActiveDecision;
            var selection = timeout ? SelectionResolver.ResolveTimeout(decision, decisionId) : SelectionResolver.Resolve(decision, actor, decisionId, keys, skip);
            var id = decision.Continuation.AbilityId; var context = new AbilityContext(state, catalog, id);
            var handler = runtime.Get(context.Ability.DefinitionId).Selection ?? throw new ArgumentException("Missing selection continuation.");
            var effects = CheckPlan(handler.Resume(context, decision.Continuation.NextEffectIndex, selection), id);
            var edit = new AbilityWorldEdit(state); var r = edit.Resolution;
            edit.Decision = r.SuspendedDecision;
            edit.Resolution = new ResolutionState(r.PendingEffects, r.WindowId, r.ProcessedTriggers, null, r.StepsUsed);
            Emit(state, AbilityEventKind.SelectionResolved, context.Ability);
            return Triggers(Apply(edit.Build(), id, effects));
        }
        private static EffectIntent[] CheckPlan(IEnumerable<EffectIntent> source, AbilityInstanceId id)
        {
            var plan = Guard.Copy(source, nameof(source));
            if (plan.Length > 64 || plan.Any(e => e.SourceAbilityId != id) || plan.Take(Math.Max(0, plan.Length - 1)).Any(e => e is RequestSelectionEffect))
                throw new ArgumentException("Invalid ability plan; selection must end the plan.");
            return plan;
        }
        private AbilityWorld Apply(AbilityWorld state, AbilityInstanceId source, IEnumerable<EffectIntent> plan)
        {
            foreach (var intent in plan)
            {
                var replacements = runtime.Replace(state, catalog, intent);
                foreach (var effect in replacements)
                {
                    if (state.ActiveDecision?.Kind == DecisionKind.SelectTargets) throw new ArgumentException("Selection must end an expanded plan.");
                    var edit = new AbilityWorldEdit(state); var r = edit.Resolution;
                    if (r.StepsUsed >= 256) throw new ArgumentException("Ability window budget exhausted.");
                    edit.Resolution = new ResolutionState(r.PendingEffects, r.WindowId, r.ProcessedTriggers, r.SuspendedDecision, r.StepsUsed + 1);
                    ApplyOne(edit, effect, source);
                    state = edit.Build();
                }
            }
            AbilityWorldValidator.Validate(state, catalog);
            return state;
        }
        private void ApplyOne(AbilityWorldEdit edit, EffectIntent effect, AbilityInstanceId source)
        {
            var state = edit.Build();
            if (effect.SourceAbilityId.HasValue && !state.Abilities.Any(a => a.Id == effect.SourceAbilityId.Value)) throw new ArgumentException("Unknown effect source.");
            var resource = effect as ChangeResourceEffect;
            if (resource != null) { edit.Resource(resource.Target, resource.Resource, resource.Delta); return; }
            var counter = effect as SetAbilityCounterEffect;
            if (counter != null)
            {
                int index = edit.Abilities.FindIndex(ability => ability.Id == counter.SourceAbilityId); var a = edit.Abilities[index];
                edit.Abilities[index] = new AbilityInstance(a.Id, a.DefinitionId, a.OwnerId, a.Source, a.RemainingCharges, a.UsesThisRound,
                    a.CooldownRounds, a.Memory.WithCounter(counter.Key, counter.Value, counter.Scope), a.Order); return;
            }
            var status = effect as ApplyStatusEffect;
            if (status != null) { if (edit.Statuses.Any(s => s.Id == status.Status.Id)) throw new ArgumentException("Duplicate status."); edit.Statuses.Add(status.Status); return; }
            var consume = effect as ConsumeStatusEffect;
            if (consume != null)
            {
                int index = edit.Statuses.FindIndex(statusEntry => statusEntry.Id == consume.StatusId); if (index < 0) throw new ArgumentException("Unknown status.");
                var s = edit.Statuses[index]; if (s.Duration.Kind != DurationKind.TriggerCount) throw new ArgumentException("Status has no trigger budget.");
                if (s.Duration.RemainingTriggers.Value == 1) edit.Statuses.RemoveAt(index);
                else edit.Statuses[index] = new StatusInstance(s.Id, s.DefinitionId, s.Source, s.Target, s.Stacks, StatusDuration.ForTriggers(s.Duration.RemainingTriggers.Value - 1));
                return;
            }
            var knowledge = effect as GrantKnowledgeEffect;
            if (knowledge != null) { edit.Knowledge.Add(knowledge.Grant); return; }
            var selection = effect as RequestSelectionEffect;
            if (selection != null)
            {
                if (runtime.Get(state.Abilities.First(a => a.Id == source).DefinitionId).Selection == null) throw new ArgumentException("Missing continuation.");
                var r = edit.Resolution;
                edit.Resolution = new ResolutionState(r.PendingEffects, r.WindowId, r.ProcessedTriggers, edit.Decision, r.StepsUsed);
                edit.Decision = DecisionState.SelectTargets(new DecisionId(state.Id + "/selection/" + state.Revision + "/" + r.StepsUsed),
                    selection.Chooser, selection.Selection, new EffectContinuation(source, selection.NextStep, new EntityRef[0]));
                Emit(state, AbilityEventKind.SelectionOpened, state.Abilities.First(a => a.Id == source)); return;
            }
            if (!state.AllowCardChanges) throw new ArgumentException("This mode window locks card changes.");
            var zones = edit.Zones.ToDictionary(z => z.Id);
            var move = effect as MoveCardEffect; var draw = effect as DrawCardsEffect; var reorder = effect as ReorderCardsEffect;
            CardZoneTransfer transfer = null;
            if (move != null)
            {
                if (!zones.ContainsKey(move.From) || !zones.ContainsKey(move.To)) throw new ArgumentException("Invalid card move.");
                transfer = zones[move.From].TransferTo(zones[move.To], move.CardId, zones[move.To].Cards.Count);
            }
            else if (draw != null)
            {
                if (!zones.ContainsKey(draw.From) || !zones.ContainsKey(draw.To)) throw new ArgumentException("Invalid draw zones.");
                transfer = zones[draw.From].DrawTo(zones[draw.To], draw.Count, zones[draw.To].Cards.Count);
            }
            else if (reorder != null)
            {
                if (!zones.ContainsKey(reorder.ZoneId)) throw new ArgumentException("Invalid reorder zone.");
                zones[reorder.ZoneId] = zones[reorder.ZoneId].Reorder(reorder.OrderedCards);
            }
            else throw new ArgumentException("Unsupported effect type.");
            if (transfer != null) { zones[transfer.Source.Id] = transfer.Source; zones[transfer.Target.Id] = transfer.Target; }
            edit.Zones = edit.Zones.Select(z => zones[z.Id]).ToList();
        }
    }
}
