using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Authority-owned facts; card conditions apply only to primary effects of the corresponding action.</summary>
    public sealed class BattleRelicTriggerContext
    {
        public TeamBattleState ActionStart { get; }
        public CombatReactionContext Fact { get; }
        public BattleRelicLoadout Loadout { get; }
        public BattleRelicState Relic { get; }
        public RelicTriggerSpec Trigger { get; }
        public HandAttackInfo Hand { get; }
        public int? DiscardedCardCount { get; }
        public int UsesThisAction { get; }
        public bool TriggerUsedThisAction { get; }
        public CombatTimingSignal Boundary { get; }
        public string ActionId { get; }
        public int UsesThisWindow { get; }
        public bool TriggerUsedThisWindow { get; }
        internal BattleRelicTriggerContext(TeamBattleState actionStart, CombatReactionContext fact, BattleRelicLoadout loadout,
            BattleRelicState relic, RelicTriggerSpec trigger, ParticipantId actor, ScoredDuelActionKind action,
            HandAttackInfo hand, int? discardedCardCount, int uses, bool used, CombatTimingSignal boundary = null, string actionId = null)
        {
            ActionStart = actionStart; Fact = fact; Loadout = loadout; Relic = relic; Trigger = trigger;
            Boundary = boundary; ActionId = actionId; UsesThisWindow = uses; TriggerUsedThisWindow = used;
            bool primaryAction = boundary == null && fact.Event.Provenance?.Origin == CombatEffectOrigin.Primary && fact.Event.Effect?.Source == actor;
            Hand = primaryAction && action == ScoredDuelActionKind.ScoredHand ? hand : null;
            DiscardedCardCount = primaryAction && action == ScoredDuelActionKind.Discard ? discardedCardCount : null;
            UsesThisAction = boundary == null ? uses : 0; TriggerUsedThisAction = boundary == null && used;
        }
    }

    public sealed class RelicTriggerActivation
    {
        public string ActivationId => Batch.ActivationId;
        public ParticipantId Owner => Before.OwnerId;
        public string SourceInstanceId => Before.InstanceId;
        public string TriggerId { get; }
        public RelicTriggerTiming? Timing { get; }
        public RelicBoundaryTiming? BoundaryTiming { get; }
        public RelicActionTiming? ActionTiming { get; }
        /// <summary>Completion window identity, also inherited by its descendant facts.</summary>
        public string ActionId { get; }
        /// <summary>The enclosing boundary window, also present on its descendant fact activations.</summary>
        public string BoundaryId { get; }
        public long ParentRevision { get; }
        public int ParentSequenceIndex { get; }
        public BattleRelicState Before { get; }
        public BattleRelicState After { get; }
        public CombatReactionBatch Batch { get; }
        internal RelicTriggerActivation(BattleRelicTriggerContext context, BattleRelicState after, CombatReactionBatch batch)
        {
            TriggerId = context.Trigger.Id; Timing = context.Trigger.Timing; ParentRevision = context.Fact.Event.Revision;
            ParentSequenceIndex = context.Fact.Event.SequenceIndex; Before = context.Relic; After = after; Batch = batch;
            BoundaryId = context.Boundary?.BoundaryId; ActionId = context.ActionId;
        }
        internal RelicTriggerActivation(RelicActionTriggerSpec spec, CombatTimingEvent clock, BattleRelicState before,
            BattleRelicState after, CombatReactionBatch batch)
        {
            TriggerId = spec.Id; ActionTiming = spec.Timing; ActionId = clock.Signal.BoundaryId;
            ParentRevision = clock.Revision; ParentSequenceIndex = clock.SequenceIndex; Before = before; After = after; Batch = batch;
        }
        internal RelicTriggerActivation(RelicBoundaryTriggerSpec spec, CombatTimingEvent clock, BattleRelicState before,
            BattleRelicState after, CombatReactionBatch batch)
        {
            TriggerId = spec.Id; BoundaryTiming = spec.Timing; BoundaryId = clock.Signal.BoundaryId;
            ParentRevision = clock.Revision; ParentSequenceIndex = clock.SequenceIndex; Before = before; After = after; Batch = batch;
        }
    }

    public interface IBattleRelicTriggerResolver
    {
        /// <summary>Null means ineligible or no target-bound nonzero effect. No mutation or payment occurs here.</summary>
        RelicTriggerActivation Resolve(BattleRelicTriggerContext context);
    }

    public sealed class BattleRelicTriggerResolver : IBattleRelicTriggerResolver
    {
        public RelicTriggerActivation Resolve(BattleRelicTriggerContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var spec = context.Trigger; var relic = context.Relic; var fact = context.Fact.Event; var combat = context.Fact.State;
            var budget = context.Loadout.RulesFor(relic.InstanceId).TriggerBudget;
            if (fact.Provenance == null || !fact.Provenance.CanTriggerReactions || !spec.Origins.Contains(fact.Provenance.Origin) ||
                !MatchesTiming(spec.Timing, fact) || !MatchesRelation(spec.Relation, combat, relic.OwnerId, fact) ||
                !combat.Get(relic.OwnerId).IsAlive && spec.SourcePolicy == CombatReactionSourcePolicy.RequireLiving ||
                !RelicTriggerSettlement.CanPay(relic, budget, context.UsesThisWindow, context.Boundary != null) ||
                spec.Occurrence == RelicTriggerOccurrence.PerAction && (context.Boundary != null || context.TriggerUsedThisWindow) ||
                spec.Occurrence == RelicTriggerOccurrence.PerBoundary && (context.Boundary == null || context.TriggerUsedThisWindow) ||
                spec.Conditions.Any(condition => !MatchesCondition(condition, context))) return null;

            var batch = RelicTriggerSettlement.Bind(context.ActionStart, combat, fact, context.Loadout, relic, spec.Id,
                spec.Effects, spec.CanTriggerReactions, spec.SourcePolicy, context.Boundary);
            return batch == null ? null : new RelicTriggerActivation(context, RelicTriggerSettlement.Pay(relic, budget), batch);
        }
        internal static bool OwnerCondition(RelicTriggerConditionSpec condition, CombatState combat, ParticipantId owner)
        {
            if (condition.Kind == RelicTriggerConditionKind.OwnerHealthAtMost)
                return combat.Get(owner).Health <= combat.Get(owner).MaxHealth * condition.HealthRatio;
            return condition.Kind == RelicTriggerConditionKind.OwnerHasStatus && combat.Statuses.Any(status =>
                status.DefinitionId == condition.StatusId.Value && status.Target.Kind == EntityKind.Participant && status.Target.Id == owner.Value);
        }
        private static bool MatchesTiming(RelicTriggerTiming timing, CombatRuleEvent fact)
        {
            var vitals = fact as CombatEvent;
            switch (timing)
            {
                case RelicTriggerTiming.DamageSettled: return vitals?.Effect.Kind == CombatEffectKind.Damage;
                case RelicTriggerTiming.HealingSettled: return vitals?.Effect.Kind == CombatEffectKind.Heal;
                case RelicTriggerTiming.BlockGained: return vitals?.Effect.Kind == CombatEffectKind.GainBlock && vitals.After.Block > vitals.Before.Block;
                case RelicTriggerTiming.UnitDied: return vitals != null && vitals.Died;
                case RelicTriggerTiming.StatusApplied: return fact is CombatStatusEvent || (fact as CombatStatusChangedEvent)?.Reason == CombatStatusChangeReason.Refreshed;
                case RelicTriggerTiming.StatusBlocked: return fact is CombatStatusBlockedEvent;
                default: return false;
            }
        }
        private static bool MatchesRelation(RelicEventRelation relation, CombatState state, ParticipantId owner, CombatRuleEvent fact)
        {
            if (fact.Effect == null) return false;
            var team = state.Get(owner).TeamId;
            switch (relation)
            {
                case RelicEventRelation.OwnerIsSource: return fact.Effect.Source == owner;
                case RelicEventRelation.OwnerIsTarget: return fact.Effect.Target == owner;
                case RelicEventRelation.AllyIsSource: return state.Get(fact.Effect.Source).TeamId == team;
                case RelicEventRelation.AllyIsTarget: return state.Get(fact.Effect.Target).TeamId == team;
                case RelicEventRelation.OpponentIsSource: return state.Get(fact.Effect.Source).TeamId != team;
                case RelicEventRelation.OpponentIsTarget: return state.Get(fact.Effect.Target).TeamId != team;
                case RelicEventRelation.Any: return true;
                default: return false;
            }
        }
        private static bool MatchesCondition(RelicTriggerConditionSpec condition, BattleRelicTriggerContext context)
        {
            var fact = context.Fact.Event;
            switch (condition.Kind)
            {
                case RelicTriggerConditionKind.FactAtLeast:
                    var amount = FactAmount(fact as CombatEvent, condition.Fact.Value);
                    return amount.HasValue && amount.Value >= condition.Threshold;
                case RelicTriggerConditionKind.OwnerHealthAtMost:
                case RelicTriggerConditionKind.OwnerHasStatus: return OwnerCondition(condition, context.Fact.State, context.Relic.OwnerId);
                case RelicTriggerConditionKind.EventStatus: return fact.Effect?.Status?.DefinitionId == condition.StatusId;
                case RelicTriggerConditionKind.HandCategory: return context.Hand?.Pattern.Category == condition.Category;
                case RelicTriggerConditionKind.SubmittedCardsAtLeast: return context.Hand != null && context.Hand.SubmittedCards.Count >= condition.Threshold;
                case RelicTriggerConditionKind.DiscardedCardsAtLeast: return context.DiscardedCardCount.HasValue && context.DiscardedCardCount.Value >= condition.Threshold;
                default: return false;
            }
        }
        private static long? FactAmount(CombatEvent fact, RelicFactAmount kind)
        {
            if (fact == null) return null;
            switch (kind)
            {
                case RelicFactAmount.DamageToHealth: return fact.Effect.Kind == CombatEffectKind.Damage ? (long?)fact.DamageToHealth : null;
                case RelicFactAmount.BlockAbsorbed: return fact.Effect.Kind == CombatEffectKind.Damage ? (long?)fact.BlockAbsorbed : null;
                case RelicFactAmount.HealthRestored: return fact.Effect.Kind == CombatEffectKind.Heal ? (long?)fact.HealthRestored : null;
                case RelicFactAmount.Overheal: return fact.Effect.Kind == CombatEffectKind.Heal ? (long?)fact.Overheal : null;
                case RelicFactAmount.BlockGained: return fact.Effect.Kind == CombatEffectKind.GainBlock ? (long?)(fact.After.Block - fact.Before.Block) : null;
                default: return null;
            }
        }
    }

    internal static class RelicTriggerSettlement
    {
        internal static bool CanPay(BattleRelicState relic, RelicTriggerBudgetSpec budget, int uses, bool boundary)
        {
            int? windowLimit = boundary ? budget.MaxUsesPerBoundary : budget.MaxUsesPerAction;
            return relic.CooldownRounds == 0 && !(relic.RemainingCharges < budget.ChargeCost) &&
                !(budget.MaxUsesPerRound.HasValue && relic.UsesThisRound >= budget.MaxUsesPerRound.Value) &&
                !(windowLimit.HasValue && uses >= windowLimit.Value);
        }
        internal static BattleRelicState Pay(BattleRelicState relic, RelicTriggerBudgetSpec budget) =>
            relic.WithRuntime(relic.RemainingCharges.HasValue ? (int?)(relic.RemainingCharges.Value - budget.ChargeCost) : null,
                checked(relic.UsesThisRound + 1), budget.CooldownRounds == 0 ? 0 : checked(budget.CooldownRounds + 1), relic.Memory);

        internal static CombatReactionBatch Bind(TeamBattleState windowStart, CombatState combat, CombatRuleEvent fact,
            BattleRelicLoadout loadout, BattleRelicState relic, string triggerId, IReadOnlyList<RelicTriggeredEffectSpec> steps,
            bool canTriggerReactions, CombatReactionSourcePolicy sourcePolicy, CombatTimingSignal boundary)
        {
            string activationId = "relic-activation/" + Part(combat.Id) + fact.Revision.ToString(CultureInfo.InvariantCulture) + "/" +
                fact.SequenceIndex.ToString(CultureInfo.InvariantCulture) + "/" + Part(relic.InstanceId) + Part(triggerId);
            var effects = new List<CombatReaction>();
            for (int index = 0; index < steps.Count; index++)
            {
                var step = steps[index]; var effect = step.Effect;
                if ((effect.Kind == CombatEffectKind.Damage || effect.Kind == CombatEffectKind.Heal || effect.Kind == CombatEffectKind.GainBlock) && effect.Amount == 0) continue;
                var targets = CombatTargetResolver.Resolve(combat, relic.OwnerId, step.Target, fact);
                if (effects.Count + targets.Count > 64) throw new ArgumentException("Relic activation exceeds 64 bound effects.");
                for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
                {
                    var target = targets[targetIndex]; CombatEffect bound;
                    if (effect.Kind == CombatEffectKind.ApplyStatus)
                    {
                        var status = effect.Status;
                        var definition = windowStart.Rules.StatusCatalog.Get(status.DefinitionId);
                        var duration = status.Duration ?? BattleStatusTiming.Bind(windowStart, status.Lifetime ?? definition.DefaultLifetime, boundary);
                        var id = new StatusInstanceId(activationId + "/" + index.ToString(CultureInfo.InvariantCulture) + "/" + targetIndex.ToString(CultureInfo.InvariantCulture));
                        bound = CombatEffect.ApplyStatus(new StatusInstance(id, status.DefinitionId, EntityRef.Participant(relic.OwnerId),
                            EntityRef.Participant(target), status.Stacks, duration));
                    }
                    else bound = new CombatEffect(effect.Kind, relic.OwnerId, target, effect.Amount);
                    effects.Add(new CombatReaction(bound, loadout.RulesFor(relic.InstanceId).Version + "/" + triggerId, relic.OwnerId,
                        CombatEffectOrigin.Triggered, canTriggerReactions, sourcePolicy, relic.InstanceId));
                }
            }
            return effects.Count == 0 ? null : new CombatReactionBatch(activationId, effects);
        }
        private static string Part(string value) => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value + "/";
    }

    /// <summary>One immutable runtime per action or boundary window. Persistent budgets survive window changes.</summary>
    internal sealed class RelicReactionRuntime : ICombatBoundaryReactionRuntime, ICombatActionCompletionReactionRuntime
    {
        private readonly TeamBattleState actionStart;
        private readonly ParticipantId actor;
        private readonly ScoredDuelActionKind action;
        private readonly HandAttackInfo hand;
        private readonly int? discardedCount;
        private readonly CombatTimingSignal boundary;
        private readonly bool boundaryDispatched;
        private readonly BattleActionCompletionContext completionContext;
        private readonly bool completionDispatched;
        internal BattleActionCompletion Completion { get; }
        private readonly Dictionary<ParticipantId, BattleRelicLoadout> loadouts;
        private readonly Dictionary<string, int> uses;
        private readonly HashSet<Tuple<string, string>> used;
        internal IReadOnlyList<RelicTriggerActivation> Activations { get; }
        internal BattleRelicLoadout Loadout(ParticipantId owner) => loadouts[owner];
        private RelicReactionRuntime(TeamBattleState state, ParticipantId actor, ScoredDuelActionKind action, HandAttackInfo hand, int? discardedCount,
            Dictionary<ParticipantId, BattleRelicLoadout> loadouts, Dictionary<string, int> uses, HashSet<Tuple<string, string>> used,
            IEnumerable<RelicTriggerActivation> activations, CombatTimingSignal boundary = null, bool boundaryDispatched = false,
            BattleActionCompletionContext completionContext = null, bool completionDispatched = false, BattleActionCompletion completion = null)
        {
            actionStart = state; this.actor = actor; this.action = action; this.hand = hand; this.discardedCount = discardedCount;
            this.loadouts = loadouts; this.uses = uses; this.used = used; Activations = Guard.View(activations.ToArray());
            this.boundary = boundary; this.boundaryDispatched = boundaryDispatched;
            this.completionContext = completionContext; this.completionDispatched = completionDispatched; Completion = completion;
        }
        internal static RelicReactionRuntime Create(TeamBattleState state, ParticipantId actor, ScoredDuelActionKind action, HandAttackInfo hand, int? discardedCount)
        {
            if (!state.Players.Any(player => player.Relics.Relics.Any(relic => player.Relics.RulesFor(relic.InstanceId).HasTriggers))) return null;
            return new RelicReactionRuntime(state, actor, action, hand, discardedCount, state.Players.ToDictionary(player => player.Id, player => player.Relics),
                new Dictionary<string, int>(StringComparer.Ordinal), new HashSet<Tuple<string, string>>(), Array.Empty<RelicTriggerActivation>());
        }
        internal static RelicReactionRuntime CreateBoundary(TeamBattleState state, CombatTimingSignal signal) =>
            new RelicReactionRuntime(state, default(ParticipantId), ScoredDuelActionKind.EndPlayerTurn, null, null,
                state.Players.ToDictionary(player => player.Id, player => player.Relics), new Dictionary<string, int>(StringComparer.Ordinal),
                new HashSet<Tuple<string, string>>(), Array.Empty<RelicTriggerActivation>(), signal);

        internal static bool Handles(TeamBattleState state, CombatTimingSignal signal) => state.Players.Any(player =>
            player.Relics.Relics.Any(relic => player.Relics.RulesFor(relic.InstanceId).BoundaryTriggers.Any(spec => spec.Matches(state.Combat, player.Id, signal))));

        internal static bool HandlesAction(TeamBattleState state, ParticipantId actor, ScoredDuelActionKind action) => state.Players.Any(player =>
            player.Relics.Relics.Any(relic => player.Relics.RulesFor(relic.InstanceId).ActionTriggers.Any(spec => spec.Matches(state.Combat, player.Id, actor, action))));

        internal RelicReactionRuntime WithCompletion(BattleActionCompletionContext context)
        {
            if (context == null || boundary != null || completionContext != null || context.Actor != actor ||
                context.Signal.ActionKey != action.ToString()) throw new ArgumentException("Completion must belong to this action runtime.");
            return new RelicReactionRuntime(actionStart, actor, action, hand, discardedCount, loadouts, uses, used, Activations,
                completionContext: context);
        }

        public CombatReactionEvaluation ReactActionCompleted(CombatActionCompletionReactionContext context)
        {
            // Legacy periodic-only clocks must not manufacture completion subscriptions.
            if (completionContext == null) return new CombatReactionEvaluation(this);
            if (context == null || context.Event.Signal.Kind != CombatTimingKind.ActionCompleted ||
                context.Event.Signal.BoundaryId != completionContext.ActionId || context.Event.Signal.Participants.Count != 1 ||
                context.Event.Signal.Participants[0] != actor || context.Event.Signal.ActionKey != action.ToString())
                throw new ArgumentException("Completion runtime must match its authority-owned action.");
            if (completionDispatched) return new CombatReactionEvaluation(this);
            var nextLoadouts = new Dictionary<ParticipantId, BattleRelicLoadout>(loadouts);
            var nextUses = new Dictionary<string, int>(uses, StringComparer.Ordinal); var nextUsed = new HashSet<Tuple<string, string>>(used);
            var reports = Activations.ToList(); var batches = new List<CombatReactionBatch>();
            bool finished = actionStart.Players.All(player => !context.State.Get(player.Id).IsAlive) || actionStart.Enemies.All(enemy => !context.State.Get(enemy.Id).IsAlive);
            var entries = loadouts.Values.SelectMany(loadout => loadout.Relics.SelectMany(relic => loadout.RulesFor(relic.InstanceId).ActionTriggers
                .Select(spec => new { Owner = loadout.OwnerId, Source = relic.InstanceId, Spec = spec })))
                .OrderBy(entry => entry.Spec.Priority).ThenBy(entry => entry.Source, StringComparer.Ordinal).ThenBy(entry => entry.Spec.Id, StringComparer.Ordinal);
            if (!finished) foreach (var entry in entries)
            {
                var loadout = nextLoadouts[entry.Owner]; var relic = loadout.Relics.Single(item => item.InstanceId == entry.Source);
                var spec = entry.Spec; var budget = loadout.RulesFor(entry.Source).TriggerBudget;
                int count; nextUses.TryGetValue(entry.Source, out count);
                if (!spec.Matches(context.State, entry.Owner, actor, action) ||
                    !context.State.Get(entry.Owner).IsAlive && spec.SourcePolicy == CombatReactionSourcePolicy.RequireLiving ||
                    !RelicTriggerSettlement.CanPay(relic, budget, count, false) || !spec.MatchesConditions(context.State, entry.Owner, completionContext)) continue;
                var batch = RelicTriggerSettlement.Bind(actionStart, context.State, context.Event, loadout, relic, spec.Id,
                    spec.Effects, spec.CanTriggerReactions, spec.SourcePolicy, null);
                if (batch == null) continue;
                var activation = new RelicTriggerActivation(spec, context.Event, relic, RelicTriggerSettlement.Pay(relic, budget), batch);
                nextLoadouts[entry.Owner] = loadout.WithRuntime(loadout.Relics.Select(item => item.InstanceId == entry.Source ? activation.After : item));
                nextUses[entry.Source] = checked(count + 1); nextUsed.Add(Tuple.Create(entry.Source, spec.Id)); reports.Add(activation); batches.Add(batch);
            }
            return new CombatReactionEvaluation(new RelicReactionRuntime(actionStart, actor, action, hand, discardedCount,
                nextLoadouts, nextUses, nextUsed, reports, completionContext: completionContext, completionDispatched: true,
                completion: finished ? null : new BattleActionCompletion(completionContext, context.Event)), batches);
        }

        public CombatReactionEvaluation ReactBoundary(CombatBoundaryReactionContext context)
        {
            // An action runtime can pass through periodic-only clocks without acquiring a boundary window.
            if (boundary == null) return new CombatReactionEvaluation(this);
            if (context == null || context.Event.Signal.BoundaryId != boundary.BoundaryId || context.Event.Signal.Kind != boundary.Kind)
                throw new ArgumentException("Boundary runtime must match its authority-owned window.");
            if (boundaryDispatched) return new CombatReactionEvaluation(this);
            var nextLoadouts = new Dictionary<ParticipantId, BattleRelicLoadout>(loadouts);
            var nextUses = new Dictionary<string, int>(uses, StringComparer.Ordinal); var nextUsed = new HashSet<Tuple<string, string>>(used);
            var reports = Activations.ToList(); var batches = new List<CombatReactionBatch>();
            bool finished = actionStart.Players.All(player => !context.State.Get(player.Id).IsAlive) || actionStart.Enemies.All(enemy => !context.State.Get(enemy.Id).IsAlive);
            var entries = loadouts.Values.SelectMany(loadout => loadout.Relics.SelectMany(relic => loadout.RulesFor(relic.InstanceId).BoundaryTriggers
                .Select(spec => new { Owner = loadout.OwnerId, Source = relic.InstanceId, Spec = spec })))
                .OrderBy(entry => entry.Spec.Priority).ThenBy(entry => entry.Source, StringComparer.Ordinal).ThenBy(entry => entry.Spec.Id, StringComparer.Ordinal);
            if (!finished) foreach (var entry in entries)
            {
                var loadout = nextLoadouts[entry.Owner]; var relic = loadout.Relics.Single(item => item.InstanceId == entry.Source);
                var spec = entry.Spec; var budget = loadout.RulesFor(entry.Source).TriggerBudget;
                int count; nextUses.TryGetValue(entry.Source, out count);
                if (!spec.Matches(context.State, entry.Owner, boundary) ||
                    !context.State.Get(entry.Owner).IsAlive && spec.SourcePolicy == CombatReactionSourcePolicy.RequireLiving ||
                    !RelicTriggerSettlement.CanPay(relic, budget, count, true) ||
                    spec.Conditions.Any(condition => !BattleRelicTriggerResolver.OwnerCondition(condition, context.State, entry.Owner))) continue;
                var batch = RelicTriggerSettlement.Bind(actionStart, context.State, context.Event, loadout, relic, spec.Id,
                    spec.Effects, spec.CanTriggerReactions, spec.SourcePolicy, boundary);
                if (batch == null) continue;
                var activation = new RelicTriggerActivation(spec, context.Event, relic, RelicTriggerSettlement.Pay(relic, budget), batch);
                nextLoadouts[entry.Owner] = loadout.WithRuntime(loadout.Relics.Select(item => item.InstanceId == entry.Source ? activation.After : item));
                nextUses[entry.Source] = checked(count + 1); nextUsed.Add(Tuple.Create(entry.Source, spec.Id)); reports.Add(activation); batches.Add(batch);
            }
            return new CombatReactionEvaluation(new RelicReactionRuntime(actionStart, actor, action, hand, discardedCount,
                nextLoadouts, nextUses, nextUsed, reports, boundary, true), batches);
        }
        public CombatReactionEvaluation React(CombatReactionContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var nextLoadouts = new Dictionary<ParticipantId, BattleRelicLoadout>(loadouts);
            var nextUses = new Dictionary<string, int>(uses, StringComparer.Ordinal); var nextUsed = new HashSet<Tuple<string, string>>(used);
            var reports = Activations.ToList(); var batches = new List<CombatReactionBatch>(); var resolver = new BattleRelicTriggerResolver();
            var entries = loadouts.Values.SelectMany(loadout => loadout.Relics.SelectMany(relic => loadout.RulesFor(relic.InstanceId).Triggers
                .Select(trigger => new { Owner = loadout.OwnerId, Source = relic.InstanceId, Trigger = trigger })))
                .OrderBy(entry => entry.Trigger.Priority).ThenBy(entry => entry.Source, StringComparer.Ordinal).ThenBy(entry => entry.Trigger.Id, StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var loadout = nextLoadouts[entry.Owner]; var relic = loadout.Relics.Single(item => item.InstanceId == entry.Source);
                int count; nextUses.TryGetValue(entry.Source, out count); var key = Tuple.Create(entry.Source, entry.Trigger.Id);
                var activation = resolver.Resolve(new BattleRelicTriggerContext(actionStart, context, loadout, relic, entry.Trigger,
                    actor, action, hand, discardedCount, count, nextUsed.Contains(key), boundary, completionContext?.ActionId));
                if (activation == null) continue;
                nextLoadouts[entry.Owner] = loadout.WithRuntime(loadout.Relics.Select(item => item.InstanceId == entry.Source ? activation.After : item));
                nextUses[entry.Source] = checked(count + 1); nextUsed.Add(key); reports.Add(activation); batches.Add(activation.Batch);
            }
            return new CombatReactionEvaluation(batches.Count == 0 ? this : new RelicReactionRuntime(actionStart, actor, action, hand, discardedCount,
                nextLoadouts, nextUses, nextUsed, reports, boundary, boundaryDispatched, completionContext, completionDispatched, Completion), batches);
        }
    }

    internal static class RelicTriggerValidation
    {
        internal static void Catalog(BattleRelicLoadout loadout, CombatStatusCatalog catalog)
        {
            foreach (var rules in loadout.Relics.Select(relic => loadout.RulesFor(relic.InstanceId)))
            {
                foreach (var condition in rules.Triggers.SelectMany(item => item.Conditions).Concat(rules.BoundaryTriggers.SelectMany(item => item.Conditions)).Concat(rules.ActionTriggers.SelectMany(item => item.Conditions))
                    .Where(item => item.StatusId.HasValue))
                    if (catalog?.Get(condition.StatusId.Value) == null) throw new ArgumentException("Relic status conditions require a catalog.");
                foreach (var status in rules.Triggers.SelectMany(item => item.Effects).Concat(rules.BoundaryTriggers.SelectMany(item => item.Effects)).Concat(rules.ActionTriggers.SelectMany(item => item.Effects))
                    .Where(step => step.Effect.Status != null).Select(step => step.Effect.Status))
                {
                    var definition = catalog?.Get(status.DefinitionId) ?? throw new ArgumentException("Relic status effects require a catalog.");
                    bool counted = status.Duration != null ? status.Duration.Kind == DurationKind.TriggerCount :
                        (status.Lifetime ?? definition.DefaultLifetime).Kind == StatusLifetimeKind.TriggerCount;
                    if (counted && definition.Behaviors.Count > 1) throw new ArgumentException("Different status behaviors cannot share a trigger budget.");
                }
            }
        }
        internal static BattleRelicLoadout BeginRound(BattleRelicLoadout loadout)
        {
            if (!loadout.Relics.Any(relic => loadout.RulesFor(relic.InstanceId).HasTriggers)) return loadout;
            return loadout.WithRuntime(loadout.Relics.Select(relic => !loadout.RulesFor(relic.InstanceId).HasTriggers ? relic :
                relic.WithRuntime(relic.RemainingCharges, 0, Math.Max(0, relic.CooldownRounds - 1), relic.Memory.ResetRound())));
        }
    }
}
