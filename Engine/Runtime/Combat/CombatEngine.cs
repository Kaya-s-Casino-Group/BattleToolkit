using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public enum CombatEffectKind { Damage = 1, Heal = 2, GainBlock = 3, ClearBlock = 4, ApplyStatus = 5, Eliminate = 6 }
    public enum CombatError { None, InvalidState, StaleRevision, InvalidEffect, Overflow, InvalidDamageModifier, InvalidReaction, ReactionLimitExceeded, InvalidAmountModifier }

    /// <summary>Authority-resolved effect, not a player command. The mode validates action costs, targets and amounts first.</summary>
    public sealed class CombatEffect
    {
        public CombatEffectKind Kind { get; }
        public ParticipantId Source { get; }
        public ParticipantId Target { get; }
        public long Amount { get; }
        public DamageModification AttackModification { get; }
        public AttackContext Attack { get; }
        /// <summary>Required only for ApplyStatus. Identity, attribution, stacks and duration are authority-resolved.</summary>
        public StatusInstance Status { get; }
        public CombatEffect(CombatEffectKind kind, ParticipantId source, ParticipantId target, long amount = 0, DamageModification attackModification = null, AttackContext attack = null, StatusInstance status = null)
        {
            if (!Enum.IsDefined(typeof(CombatEffectKind), kind) || !source.IsValid || !target.IsValid || amount < 0 ||
                (kind == CombatEffectKind.ClearBlock || kind == CombatEffectKind.ApplyStatus || kind == CombatEffectKind.Eliminate) && amount != 0)
                throw new ArgumentException("Invalid combat effect.");
            if ((kind == CombatEffectKind.ApplyStatus) != (status != null))
                throw new ArgumentException("Only ApplyStatus requires a status payload.");
            if (status != null && (status.Source == null || status.Target == null ||
                status.Source.Kind != EntityKind.Participant || status.Source.Id != source.Value ||
                status.Target.Kind != EntityKind.Participant || status.Target.Id != target.Value))
                throw new ArgumentException("Status attribution must match the effect's source and target participants.");
            if (attackModification != null && (kind != CombatEffectKind.Damage || attackModification.OutputAmount != amount))
                throw new ArgumentException("Attack report must match the outgoing damage.");
            if (kind != CombatEffectKind.Damage && attack != null) throw new ArgumentException("Only damage effects carry attack facts.");
            Attack = kind == CombatEffectKind.Damage ? attack ?? new AttackContext(source, AttackOrigin.Other, attackModification?.InputAmount ?? amount) : null;
            if (Attack != null && (Attack.Source != source || Attack.BaseDamage != (attackModification?.InputAmount ?? amount)))
                throw new ArgumentException("Attack facts must match the original damage request.");
            if (attackModification != null) DamageModification.Validate(attackModification, Attack.BaseDamage, Attack);
            Kind = kind; Source = source; Target = target; Amount = amount; AttackModification = attackModification; Status = status;
        }
        public static CombatEffect ApplyStatus(StatusInstance status)
        {
            if (status == null) throw new ArgumentNullException(nameof(status));
            if (status.Source == null || status.Target == null || status.Source.Kind != EntityKind.Participant || status.Target.Kind != EntityKind.Participant)
                throw new ArgumentException("Combat status effects require participant references.");
            return new CombatEffect(CombatEffectKind.ApplyStatus, new ParticipantId(status.Source.Id), new ParticipantId(status.Target.Id), status: status);
        }
        public static CombatEffect Eliminate(ParticipantId source, ParticipantId target) => new CombatEffect(CombatEffectKind.Eliminate, source, target);
    }
    public sealed class CombatEvent : CombatRuleEvent
    {
        public int BlockAbsorbed { get; }
        /// <summary>Actual health decrease, including elimination. Damage reactions must use DamageToHealth instead.</summary>
        public int HealthLost { get; }
        public int DamageToHealth => Effect.Kind == CombatEffectKind.Damage ? HealthLost : 0;
        public int HealthRestored { get; }
        /// <summary>Accepted healing that exceeded missing health. Skipped heals produce no fact or overheal.</summary>
        public long Overheal => Effect.Kind == CombatEffectKind.Heal ? SettledAmount - HealthRestored : 0;
        public long Overkill { get; }
        public DamageModification IncomingModification { get; }
        public CombatAmountModification OutgoingAmountModification { get; }
        public CombatAmountModification IncomingAmountModification { get; }
        /// <summary>Effective amount before block/health caps. Effect retains the original authority request.</summary>
        public long SettledAmount => IncomingAmountModification?.OutputAmount ?? IncomingModification?.OutputAmount ??
            OutgoingAmountModification?.OutputAmount ?? Effect.Amount;
        public long SettledDamage => Effect.Kind == CombatEffectKind.Damage ? SettledAmount : 0;
        public CombatantState Before { get; }
        public CombatantState After { get; }
        public bool Died => Before.IsAlive && !After.IsAlive;
        public CombatDeathCause DeathCause => !Died ? CombatDeathCause.None :
            Effect.Kind == CombatEffectKind.Eliminate ? CombatDeathCause.Eliminate : CombatDeathCause.Damage;
        internal CombatEvent(long revision, int index, CombatEffect effect, CombatantState before, CombatantState after, int absorbed, long overkill,
            DamageModification incomingModification = null, int sequenceIndex = -1, CombatEffectProvenance provenance = null,
            CombatAmountModification outgoingAmountModification = null, CombatAmountModification incomingAmountModification = null)
            : base(revision, index, effect, sequenceIndex, provenance)
        {
            IncomingModification = incomingModification; Before = before; After = after; BlockAbsorbed = absorbed; Overkill = overkill;
            OutgoingAmountModification = outgoingAmountModification; IncomingAmountModification = incomingAmountModification;
            HealthLost = Math.Max(0, before.Health - after.Health); HealthRestored = Math.Max(0, after.Health - before.Health);
        }
    }
    public sealed class CombatTransition
    {
        public CombatState State { get; }
        public CombatError Error { get; }
        public bool Accepted => Error == CombatError.None;
        public IReadOnlyList<CombatRuleEvent> RuleEvents { get; }
        /// <summary>Compatibility projection of vitals events; Index still identifies the original effect position.</summary>
        public IReadOnlyList<CombatEvent> Events { get; }
        /// <summary>Accepted provisional runtime; null on failure or when no runtime was supplied.</summary>
        public ICombatReactionRuntime ReactionRuntime { get; }
        internal CombatTransition(CombatState state, CombatError error = CombatError.None, IEnumerable<CombatRuleEvent> events = null,
            ICombatReactionRuntime reactionRuntime = null)
        {
            State = state; Error = error;
            ReactionRuntime = reactionRuntime;
            RuleEvents = Guard.View(Guard.Copy(events ?? Array.Empty<CombatRuleEvent>(), nameof(events)));
            Events = Guard.View(RuleEvents.OfType<CombatEvent>().ToArray());
        }
    }

    /// <summary>Shared deterministic authority resolver. No turn, AI, card score, faction targeting or reward policy.</summary>
    public static class CombatEngine
    {
        public const string RulesVersion = "combat.8";
        public static CombatTransition Resolve(CombatState state, long expectedRevision, IEnumerable<CombatEffect> effects, IIncomingDamageModifier incomingDamage = null,
            CombatStatusCatalog statusCatalog = null, ICombatReactionProvider reactionProvider = null, CombatReactionLimits reactionLimits = null,
            ICombatAmountModifier amountModifier = null, ICombatReactionRuntime reactionRuntime = null)
        {
            if (state == null || effects == null) throw new ArgumentNullException();
            return ResolveCore(state, expectedRevision, effects, null, incomingDamage, statusCatalog, reactionProvider, reactionLimits, amountModifier, reactionRuntime);
        }

        /// <summary>Resolve one authority-owned boundary atomically. Periodic damage is a leaf and never re-enters reaction rules.</summary>
        public static CombatTransition ResolveTiming(CombatState state, long expectedRevision, CombatTimingSignal signal,
            IIncomingDamageModifier incomingDamage = null, CombatStatusCatalog statusCatalog = null, CombatReactionLimits reactionLimits = null,
            ICombatAmountModifier amountModifier = null, ICombatReactionRuntime reactionRuntime = null, ICombatReactionProvider reactionProvider = null)
        {
            if (state == null || signal == null) throw new ArgumentNullException();
            return ResolveCore(state, expectedRevision, Array.Empty<CombatEffect>(), signal, incomingDamage, statusCatalog, reactionProvider, reactionLimits, amountModifier, reactionRuntime);
        }

        private static CombatTransition ResolveCore(CombatState state, long expectedRevision, IEnumerable<CombatEffect> effects,
            CombatTimingSignal signal, IIncomingDamageModifier incomingDamage, CombatStatusCatalog statusCatalog,
            ICombatReactionProvider reactionProvider, CombatReactionLimits reactionLimits, ICombatAmountModifier amountModifier,
            ICombatReactionRuntime reactionRuntime)
        {
            try { Validate(state); CombatStatusRules.ValidateCatalog(state, statusCatalog); }
            catch (ArgumentException) { return new CombatTransition(state, CombatError.InvalidState); }
            if (state.Revision != expectedRevision) return new CombatTransition(state, CombatError.StaleRevision);
            incomingDamage = incomingDamage ?? new IdentityIncomingDamageModifier();
            reactionLimits = reactionLimits ?? new CombatReactionLimits();
            // Catalog behaviors are always active when a catalog is supplied. External rules extend them
            // in a fixed order instead of requiring callers to remember a second configuration switch.
            var statusReactions = statusCatalog == null ? null : new CombatStatusReactionProvider(statusCatalog);
            try
            {
                var batch = Guard.Copy(effects, nameof(effects));
                if (signal == null && (batch.Length < 1 || batch.Length > 64))
                    throw new ArgumentException("Resolve between 1 and 64 effects atomically.");
                var units = state.Combatants.ToDictionary(unit => unit.Id); var events = new List<CombatRuleEvent>();
                var statuses = state.Statuses.ToList();
                var unitIds = new HashSet<ParticipantId>(units.Keys);
                if (signal != null && signal.Participants.Any(id => !unitIds.Contains(id)))
                    throw new ArgumentException("Clock participants must belong to this combat.");
                // Validate the entire primary declaration against the action-start state. Later deaths cancel
                // active effects, but must never hide an initially dead source or an unknown participant.
                foreach (var effect in batch)
                    if (effect == null || !units.ContainsKey(effect.Source) || !units.ContainsKey(effect.Target) || !units[effect.Source].IsAlive)
                        throw new ArgumentException("Primary effects require known units and an initially living source.");
                long revision = checked(state.Revision + 1);
                var queue = new Stack<QueuedEffect>();
                CombatTimingEvent clock = null;
                if (signal != null)
                {
                    clock = new CombatTimingEvent(revision, events.Count, signal); events.Add(clock);
                    // A marker under the periodic entries dispatches once, using the post-tick snapshot.
                    if (signal.Kind == CombatTimingKind.ActionCompleted ? reactionRuntime is ICombatActionCompletionReactionRuntime :
                        reactionRuntime is ICombatBoundaryReactionRuntime)
                        queue.Push(new QueuedEffect(clock));
                    // Eligibility/targets are evaluated when each status reaches the head, so an earlier
                    // tick can kill a later holder. Snapshot only identities, in stable stored order.
                    if (statusCatalog != null)
                        for (int index = statuses.Count - 1; index >= 0; index--)
                            if (statuses[index].Target.Kind == EntityKind.Participant &&
                                signal.Participants.Contains(new ParticipantId(statuses[index].Target.Id)) &&
                                statusCatalog.Get(statuses[index].DefinitionId).Behaviors.OfType<PeriodicDamageSpec>()
                                    .Any(behavior => behavior.Timing == signal.Kind))
                                queue.Push(new QueuedEffect(statuses[index].Id));
                }
                for (int index = batch.Length - 1; index >= 0; index--)
                    queue.Push(new QueuedEffect(batch[index], new CombatEffectProvenance(index)));
                int reactionCount = 0;
                var activationIds = new HashSet<string>(StringComparer.Ordinal);
                while (queue.Count > 0)
                {
                    var queued = queue.Pop();
                    if (queued.Boundary != null)
                    {
                        try
                        {
                            var current = new CombatState(state.Id, state.Revision, state.Combatants.Select(unit => units[unit.Id]), statuses);
                            var evaluation = queued.Boundary.Signal.Kind == CombatTimingKind.ActionCompleted ?
                                ((ICombatActionCompletionReactionRuntime)reactionRuntime).ReactActionCompleted(new CombatActionCompletionReactionContext(current, queued.Boundary)) :
                                ((ICombatBoundaryReactionRuntime)reactionRuntime).ReactBoundary(new CombatBoundaryReactionContext(current, queued.Boundary));
                            var responses = new List<QueuedEffect>();
                            var error = AddRuntimeReactions(evaluation, queued.Boundary, unitIds, activationIds, reactionLimits, ref reactionCount, responses);
                            if (error != CombatError.None) return new CombatTransition(state, error);
                            reactionRuntime = evaluation.NextRuntime;
                            for (int index = responses.Count - 1; index >= 0; index--) queue.Push(responses[index]);
                        }
                        catch (ArgumentException) { return new CombatTransition(state, CombatError.InvalidReaction); }
                        catch (InvalidOperationException) { return new CombatTransition(state, CombatError.InvalidReaction); }
                        continue;
                    }
                    if (queued.PeriodicStatusId.HasValue)
                    {
                        var status = statuses.FirstOrDefault(item => item.Id == queued.PeriodicStatusId.Value);
                        if (status == null) continue;
                        if (queued.ConsumePeriodic)
                        {
                            // Unlike reactive rewards, a periodic opportunity is spent even for zero or
                            // fully absorbed damage. Consume after all siblings, including the final tick.
                            CombatStatusRules.Consume(statuses, status, revision, -1, null, events, queued.Provenance);
                            continue;
                        }
                        var holder = new ParticipantId(status.Target.Id);
                        if (!units[holder].IsAlive) continue;
                        var definition = statusCatalog.Get(status.DefinitionId);
                        var behavior = definition.Behaviors.OfType<PeriodicDamageSpec>().Single(item => item.Timing == signal.Kind);
                        var current = new CombatState(state.Id, state.Revision, state.Combatants.Select(unit => units[unit.Id]), statuses);
                        var targets = CombatTargetResolver.Resolve(current, holder, behavior.Target);
                        if (targets.Count == 0) continue;
                        long amount = checked(behavior.DamagePerStack * status.Stacks);
                        if (targets.Count > reactionLimits.MaxEffects - reactionCount)
                            return new CombatTransition(state, CombatError.ReactionLimitExceeded);
                        reactionCount += targets.Count;
                        var source = new ParticipantId(status.Source.Id);
                        string ruleId = statusCatalog.Version + "/" + definition.Id.Value + "/" + nameof(PeriodicDamageSpec);
                        var ticks = targets.Select(target =>
                        {
                            var response = CombatReaction.FromStatus(new CombatEffect(CombatEffectKind.Damage, source, target, amount),
                                ruleId, holder, status.Id, CombatEffectOrigin.Periodic, false, CombatReactionSourcePolicy.AllowDefeated);
                            return new QueuedEffect(response.Effect, new CombatEffectProvenance(response, clock));
                        }).ToArray();
                        queue.Push(new QueuedEffect(status.Id, true, ticks[0].Provenance));
                        for (int index = ticks.Length - 1; index >= 0; index--) queue.Push(ticks[index]);
                        continue;
                    }
                    var effect = queued.Effect; var provenance = queued.Provenance;
                    int effectIndex = provenance.RootEffectIndex;
                    // Identities are checked before enqueueing. Death is a normal settlement result, not an error.
                    if (!units[effect.Source].IsAlive && provenance.SourcePolicy == CombatReactionSourcePolicy.RequireLiving) continue;
                    if (!units[effect.Target].IsAlive) continue;
                    int firstEvent = events.Count;
                    var before = units[effect.Target]; int health = before.Health, block = before.Block, absorbed = 0; long overkill = 0;
                    DamageModification incoming = null;
                    CombatAmountModification outgoingAmount = null, incomingAmount = null;
                    long settledAmount = effect.Amount;
                    try
                    {
                        if (effect.Kind == CombatEffectKind.Damage || effect.Kind == CombatEffectKind.Heal || effect.Kind == CombatEffectKind.GainBlock)
                        {
                            var current = new CombatState(state.Id, state.Revision, state.Combatants.Select(unit => units[unit.Id]), statuses);
                            try
                            {
                                outgoingAmount = ModifyAmount(amountModifier,
                                    provenance.Origin == CombatEffectOrigin.Primary ? state : current, effect, provenance, CombatAmountStage.Outgoing, settledAmount);
                                settledAmount = outgoingAmount?.OutputAmount ?? settledAmount;
                            }
                            catch (ArgumentException) { return new CombatTransition(state, CombatError.InvalidAmountModifier); }
                            if (effect.Kind == CombatEffectKind.Damage)
                            {
                                try
                                {
                                    incoming = DamageModification.Validate(incomingDamage.Modify(new DamageModifierContext(current,
                                        effect.Source, effect.Target, settledAmount, effect.Attack,
                                        provenance.SourcePolicy == CombatReactionSourcePolicy.AllowDefeated)), settledAmount, effect.Attack);
                                    settledAmount = incoming.OutputAmount;
                                }
                                catch (ArgumentException) { return new CombatTransition(state, CombatError.InvalidDamageModifier); }
                            }
                            try
                            {
                                incomingAmount = ModifyAmount(amountModifier, current, effect, provenance, CombatAmountStage.Incoming, settledAmount);
                                settledAmount = incomingAmount?.OutputAmount ?? settledAmount;
                            }
                            catch (ArgumentException) { return new CombatTransition(state, CombatError.InvalidAmountModifier); }
                        }
                        switch (effect.Kind)
                        {
                            case CombatEffectKind.Damage:
                                absorbed = (int)Math.Min(settledAmount, block); block -= absorbed;
                                long remaining = settledAmount - absorbed; int lost = (int)Math.Min(remaining, health);
                                health -= lost; overkill = remaining - lost; break;
                            case CombatEffectKind.Heal: health += (int)Math.Min(settledAmount, before.MaxHealth - health); break;
                            case CombatEffectKind.GainBlock: block = checked(block + checked((int)settledAmount)); break;
                            case CombatEffectKind.ClearBlock: block = 0; break;
                            case CombatEffectKind.ApplyStatus:
                                ValidateStatus(effect.Status, state, unitIds);
                                CombatStatusRules.Apply(state, statuses, effect, statusCatalog, revision, effectIndex, events, provenance);
                                break;
                            case CombatEffectKind.Eliminate: health = 0; break;
                            default: throw new ArgumentException("Unknown combat effect.");
                        }
                        if (effect.Kind != CombatEffectKind.ApplyStatus)
                        {
                            var after = before.WithVitals(health, block); units[after.Id] = after;
                            events.Add(new CombatEvent(revision, effectIndex, effect, before, after, absorbed, overkill, incoming, events.Count, provenance,
                                outgoingAmount, incomingAmount));
                        }
                    }
                    catch (ArgumentException)
                    { return new CombatTransition(state, provenance.Depth == 0 ? CombatError.InvalidEffect : CombatError.InvalidReaction); }

                    if ((statusReactions == null && reactionProvider == null && reactionRuntime == null) || !provenance.CanTriggerReactions) continue;
                    // Complete all writes/facts for this effect before taking one reaction snapshot. For example,
                    // an immunity Blocked fact and its Consumed fact both see the already-consumed charge.
                    var settled = new CombatState(state.Id, state.Revision, state.Combatants.Select(unit => units[unit.Id]), statuses);
                    var derived = new List<QueuedEffect>();
                    int parentEventsEnd = events.Count;
                    try
                    {
                        for (int eventIndex = firstEvent; eventIndex < parentEventsEnd; eventIndex++)
                        {
                            var parent = events[eventIndex];
                            var reactions = Reactions(new CombatReactionContext(settled, parent), statusReactions, reactionProvider);
                            // Bound enumeration before materializing. Even skipped reactions spend the budget.
                            foreach (var reaction in reactions)
                            {
                                if (++reactionCount > reactionLimits.MaxEffects || provenance.Depth >= reactionLimits.MaxDepth)
                                    return new CombatTransition(state, CombatError.ReactionLimitExceeded);
                                if (reaction == null || !unitIds.Contains(reaction.Owner) ||
                                    !unitIds.Contains(reaction.Effect.Source) || !unitIds.Contains(reaction.Effect.Target))
                                    return new CombatTransition(state, CombatError.InvalidReaction);
                                if (reaction.TriggerStatusId.HasValue && !statuses.Any(status => status.Id == reaction.TriggerStatusId.Value &&
                                    CombatStatusRules.Targets(status, reaction.Owner)))
                                    return new CombatTransition(state, CombatError.InvalidReaction);
                                derived.Add(new QueuedEffect(reaction.Effect, new CombatEffectProvenance(reaction, parent)));
                            }
                            if (reactionRuntime != null)
                            {
                                var evaluation = reactionRuntime.React(new CombatReactionContext(settled, parent));
                                var error = AddRuntimeReactions(evaluation, parent, unitIds, activationIds, reactionLimits, ref reactionCount, derived);
                                if (error != CombatError.None) return new CombatTransition(state, error);
                                reactionRuntime = evaluation.NextRuntime;
                            }
                        }
                        // One activation spends one charge, independent of target count. Once a positive,
                        // target-bound response is earned, later death skips do not refund that charge.
                        // Freeze the original fact range above: these consumption facts are not new hooks.
                        var consumed = new HashSet<Tuple<int, StatusInstanceId>>();
                        foreach (var response in derived)
                        {
                            var cause = response.Provenance;
                            if (!cause.TriggerStatusId.HasValue) continue;
                            int parentIndex = cause.ParentSequenceIndex.Value;
                            var key = Tuple.Create(parentIndex, cause.TriggerStatusId.Value);
                            if (!consumed.Add(key)) continue;
                            var status = statuses.FirstOrDefault(item => item.Id == cause.TriggerStatusId.Value);
                            if (status == null) return new CombatTransition(state, CombatError.InvalidReaction);
                            CombatStatusRules.Consume(statuses, status, revision, effectIndex, events[parentIndex].Effect, events, cause);
                        }
                    }
                    catch (ArgumentException) { return new CombatTransition(state, CombatError.InvalidReaction); }
                    catch (InvalidOperationException) { return new CombatTransition(state, CombatError.InvalidReaction); }
                    // Depth-first immediate reactions: A -> A's descendants -> B -> next primary effect.
                    // Reverse insertion preserves the rule's stable sibling order without recursive calls.
                    for (int index = derived.Count - 1; index >= 0; index--) queue.Push(derived[index]);
                }
                return new CombatTransition(new CombatState(state.Id, revision, state.Combatants.Select(unit => units[unit.Id]), statuses), events: events,
                    reactionRuntime: reactionRuntime);
            }
            catch (ArgumentException) { return new CombatTransition(state, CombatError.InvalidEffect); }
            catch (OverflowException) { return new CombatTransition(state, CombatError.Overflow); }
        }
        private static CombatError AddRuntimeReactions(CombatReactionEvaluation evaluation, CombatRuleEvent parent,
            HashSet<ParticipantId> unitIds, HashSet<string> activationIds, CombatReactionLimits limits, ref int count, List<QueuedEffect> output)
        {
            if (evaluation == null) return CombatError.InvalidReaction;
            foreach (var activation in evaluation.Batches)
            {
                if (!activationIds.Add(activation.ActivationId)) return CombatError.InvalidReaction;
                foreach (var reaction in activation.Reactions)
                {
                    if (++count > limits.MaxEffects || (parent.Provenance?.Depth ?? 0) >= limits.MaxDepth) return CombatError.ReactionLimitExceeded;
                    if (!unitIds.Contains(reaction.Owner) || !unitIds.Contains(reaction.Effect.Source) || !unitIds.Contains(reaction.Effect.Target)) return CombatError.InvalidReaction;
                    output.Add(new QueuedEffect(reaction.Effect, new CombatEffectProvenance(reaction, parent, activation.ActivationId)));
                }
            }
            return CombatError.None;
        }

        private static CombatAmountModification ModifyAmount(ICombatAmountModifier modifier, CombatState state, CombatEffect effect,
            CombatEffectProvenance provenance, CombatAmountStage stage, long amount)
        {
            if (modifier == null) return null;
            var result = CombatAmountModification.Validate(modifier.Modify(new CombatAmountContext(state, effect, provenance, stage, amount)), amount);
            return result.IsIdentity ? null : result;
        }

        private static IEnumerable<CombatReaction> Reactions(CombatReactionContext context,
            ICombatReactionProvider statusReactions, ICombatReactionProvider externalReactions)
        {
            if (statusReactions != null)
                foreach (var reaction in statusReactions.React(context)) yield return reaction;
            if (externalReactions == null) yield break;
            var reactions = externalReactions.React(context);
            if (reactions == null) throw new ArgumentException("Reaction rules must return a nonnull sequence.");
            foreach (var reaction in reactions) yield return reaction;
        }
        private sealed class QueuedEffect
        {
            internal CombatEffect Effect { get; }
            internal CombatEffectProvenance Provenance { get; }
            internal StatusInstanceId? PeriodicStatusId { get; }
            internal bool ConsumePeriodic { get; }
            internal CombatTimingEvent Boundary { get; }
            internal QueuedEffect(CombatTimingEvent boundary) { Boundary = boundary; }
            internal QueuedEffect(CombatEffect effect, CombatEffectProvenance provenance)
            { Effect = effect; Provenance = provenance; }
            internal QueuedEffect(StatusInstanceId statusId, bool consume = false, CombatEffectProvenance provenance = null)
            { PeriodicStatusId = statusId; ConsumePeriodic = consume; Provenance = provenance; }
        }
        public static void Validate(CombatState state)
        {
            try
            {
                if (state == null || string.IsNullOrWhiteSpace(state.Id) || state.Revision < 0 || state.RulesVersion != RulesVersion ||
                    state.Combatants.Count < 1 || state.Combatants.Count > 64 || state.Statuses.Count > 256)
                    throw new ArgumentException("Invalid combat state.");
                var ids = new HashSet<ParticipantId>();
                foreach (var unit in state.Combatants)
                {
                    if (unit == null || !ids.Add(unit.Id)) throw new ArgumentException("Missing or duplicate combatant.");
                    new CombatantState(unit.Id, unit.DefinitionId, unit.TeamId, unit.MaxHealth, unit.Health, unit.Block);
                }
                var statuses = new HashSet<StatusInstanceId>();
                foreach (var status in state.Statuses)
                {
                    if (status == null || !statuses.Add(status.Id)) throw new ArgumentException("Missing or duplicate combat status.");
                    ValidateStatus(status, state, ids);
                }
            }
            catch (NullReferenceException error) { throw new ArgumentException("Missing combat state data.", error); }
        }
        private static void ValidateStatus(StatusInstance status, CombatState state, HashSet<ParticipantId> ids)
        {
            if (status == null) throw new ArgumentException("Missing status payload.");
            new StatusInstance(status.Id, status.DefinitionId, status.Source, status.Target, status.Stacks, status.Duration);
            ValidateEntity(status.Source, state, ids); ValidateEntity(status.Target, state, ids);
            var d = status.Duration;
            switch (d.Kind)
            {
                case DurationKind.UntilRemoved:
                    if (d.ScopeId != null || d.PhaseKey != null || d.RemainingTriggers.HasValue) throw new ArgumentException("Invalid unbounded status."); break;
                case DurationKind.UntilRoundEnd:
                    if (string.IsNullOrWhiteSpace(d.ScopeId) || d.PhaseKey != null || d.RemainingTriggers.HasValue) throw new ArgumentException("Invalid round status."); break;
                case DurationKind.UntilPhaseEnd:
                    if (string.IsNullOrWhiteSpace(d.ScopeId) || string.IsNullOrWhiteSpace(d.PhaseKey) || d.RemainingTriggers.HasValue) throw new ArgumentException("Invalid phase status."); break;
                case DurationKind.TriggerCount:
                    if (!d.RemainingTriggers.HasValue || d.RemainingTriggers <= 0 || d.ScopeId != null || d.PhaseKey != null) throw new ArgumentException("Invalid trigger status."); break;
                default: throw new ArgumentException("Unknown duration.");
            }
        }
        private static void ValidateEntity(EntityRef reference, CombatState state, HashSet<ParticipantId> ids)
        {
            if (reference == null || string.IsNullOrWhiteSpace(reference.Id) ||
                !(reference.Kind == EntityKind.Match && reference.Id == state.Id || reference.Kind == EntityKind.Participant && ids.Contains(new ParticipantId(reference.Id))))
                throw new ArgumentException("This combat state supports match and participant status references only.");
        }
    }
}
