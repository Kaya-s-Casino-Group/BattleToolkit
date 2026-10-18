using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Immutable transaction-local runtime. React must not mutate this object or external state.
    /// Its returned snapshot becomes visible to later facts only after the complete response is validated.</summary>
    public interface ICombatReactionRuntime
    {
        CombatReactionEvaluation React(CombatReactionContext context);
    }

    /// <summary>Optional clock hook. Fact-only runtimes never receive an event without effect endpoints.</summary>
    public interface ICombatBoundaryReactionRuntime : ICombatReactionRuntime
    {
        CombatReactionEvaluation ReactBoundary(CombatBoundaryReactionContext context);
    }

    /// <summary>Optional post-periodic action hook; separate from lifecycle boundaries and effect facts.</summary>
    public interface ICombatActionCompletionReactionRuntime : ICombatReactionRuntime
    {
        CombatReactionEvaluation ReactActionCompleted(CombatActionCompletionReactionContext context);
    }

    public sealed class CombatActionCompletionReactionContext
    {
        public CombatState State { get; }
        public CombatTimingEvent Event { get; }
        internal CombatActionCompletionReactionContext(CombatState state, CombatTimingEvent clock) { State = state; Event = clock; }
    }

    public sealed class CombatBoundaryReactionContext
    {
        public CombatState State { get; }
        public CombatTimingEvent Event { get; }
        internal CombatBoundaryReactionContext(CombatState state, CombatTimingEvent clock) { State = state; Event = clock; }
    }

    /// <summary>One activation, potentially containing several effects/targets. Attribution never authorizes runtime writes.</summary>
    public sealed class CombatReactionBatch
    {
        public string ActivationId { get; }
        public IReadOnlyList<CombatReaction> Reactions { get; }
        public CombatReactionBatch(string activationId, IEnumerable<CombatReaction> reactions)
        {
            ActivationId = Guard.Text(activationId, nameof(activationId));
            if (reactions == null) throw new ArgumentNullException(nameof(reactions));
            var items = Guard.Copy(reactions.Take(65), nameof(reactions));
            if (items.Length == 0 || items.Length > 64 || items.Any(item => item.TriggerStatusId.HasValue))
                throw new ArgumentException("Runtime activations require 1..64 external reactions.");
            var first = items[0];
            if (items.Any(item => item.Owner != first.Owner || item.RuleId != first.RuleId || item.SourceInstanceId != first.SourceInstanceId))
                throw new ArgumentException("One activation must have one owner, rule and source instance.");
            Reactions = Guard.View(items);
        }
    }

    public sealed class CombatReactionEvaluation
    {
        public ICombatReactionRuntime NextRuntime { get; }
        public IReadOnlyList<CombatReactionBatch> Batches { get; }
        public CombatReactionEvaluation(ICombatReactionRuntime nextRuntime, IEnumerable<CombatReactionBatch> batches = null)
        {
            NextRuntime = nextRuntime ?? throw new ArgumentNullException(nameof(nextRuntime));
            var items = Guard.Copy((batches ?? Array.Empty<CombatReactionBatch>()).Take(4097), nameof(batches));
            if (items.Length > 4096 || items.Select(item => item.ActivationId).Distinct(StringComparer.Ordinal).Count() != items.Length)
                throw new ArgumentException("Invalid activation batch collection.");
            Batches = Guard.View(items);
        }
    }
}
