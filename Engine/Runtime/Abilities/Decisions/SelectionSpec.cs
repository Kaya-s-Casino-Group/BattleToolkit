using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public enum SelectionTargetKind { Cards = 1, Participants, Options }
    public enum SelectionTimeoutPolicy { Skip = 1, FirstCandidates }

    /// <summary>Key is an opaque per-window token. Entity mapping stays on the authoritative side.</summary>
    [DataContract]
    public sealed class SelectionCandidate
    {
        [DataMember(Order = 1, IsRequired = true)] public string Key { get; private set; }
        [DataMember(Order = 2)] public EntityRef Entity { get; private set; }
        public SelectionCandidate(string key, EntityRef entity = null) { Key = Guard.Text(key, nameof(key)); Entity = entity; }
    }

    [DataContract]
    public sealed class SelectionSpec
    {
        [DataMember(Order = 1, IsRequired = true)] public SelectionTargetKind TargetKind { get; private set; }
        [DataMember(Name = "Candidates", Order = 2, IsRequired = true)] private SelectionCandidate[] candidates;
        [DataMember(Order = 3, IsRequired = true)] public int MinCount { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public int MaxCount { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public bool OrderMatters { get; private set; }
        [DataMember(Order = 6, IsRequired = true)] public bool AllowSkip { get; private set; }
        [DataMember(Order = 7, IsRequired = true)] public SelectionTimeoutPolicy TimeoutPolicy { get; private set; }
        public IReadOnlyList<SelectionCandidate> Candidates => Guard.View(candidates);
        public SelectionSpec(SelectionTargetKind targetKind, IEnumerable<SelectionCandidate> candidates, int minCount, int maxCount,
            bool orderMatters = false, bool allowSkip = false, SelectionTimeoutPolicy timeoutPolicy = SelectionTimeoutPolicy.FirstCandidates)
        {
            TargetKind = targetKind; this.candidates = Guard.Copy(candidates, nameof(candidates)); MinCount = minCount; MaxCount = maxCount;
            OrderMatters = orderMatters; AllowSkip = allowSkip; TimeoutPolicy = timeoutPolicy;
            AbilityWorldValidator.ValidateSelectionSpec(this);
        }
    }

    public sealed class SelectionResult
    {
        public bool Skipped { get; }
        public IReadOnlyList<SelectionCandidate> Selected { get; }
        internal SelectionResult(bool skipped, IEnumerable<SelectionCandidate> selected)
        { Skipped = skipped; Selected = Guard.View(Guard.Copy(selected, nameof(selected))); }
    }

    /// <summary>Validates against an existing authoritative window; never consumes it or executes effects.</summary>
    public static class SelectionResolver
    {
        public static SelectionResult Resolve(DecisionState decision, ParticipantId authenticatedActor, DecisionId submittedDecision,
            IEnumerable<string> selectedKeys, bool skip = false)
        {
            if (decision == null || decision.Kind != DecisionKind.SelectTargets || decision.Selection == null)
                throw new StateValidationException("NotASelection", "Decision");
            if (decision.Id != submittedDecision) throw new StateValidationException("StaleDecision", "Decision.Id");
            if (decision.ActorId != authenticatedActor) throw new StateValidationException("WrongDecisionActor", "Decision.ActorId");
            var spec = decision.Selection; AbilityWorldValidator.ValidateSelectionSpec(spec);
            var keys = Guard.Copy(selectedKeys, nameof(selectedKeys));
            if (skip)
            {
                if (!spec.AllowSkip || keys.Length != 0) throw new StateValidationException("CannotSkipSelection", "Selection");
                return new SelectionResult(true, new SelectionCandidate[0]);
            }
            if (keys.Length < spec.MinCount || keys.Length > spec.MaxCount || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length ||
                keys.Any(k => !spec.Candidates.Any(c => c.Key == k))) throw new StateValidationException("InvalidSelection", "Selection.Keys");
            var selected = spec.OrderMatters ? keys.Select(k => spec.Candidates.First(c => c.Key == k)) : spec.Candidates.Where(c => keys.Contains(c.Key));
            return new SelectionResult(false, selected);
        }

        public static SelectionResult ResolveTimeout(DecisionState decision, DecisionId timedOutDecision)
        {
            if (decision == null || decision.Selection == null) throw new StateValidationException("NotASelection", "Decision");
            AbilityWorldValidator.ValidateSelectionSpec(decision.Selection);
            bool skip = decision.Selection.TimeoutPolicy == SelectionTimeoutPolicy.Skip;
            return Resolve(decision, decision.ActorId, timedOutDecision,
                skip ? new string[0] : decision.Selection.Candidates.Take(decision.Selection.MinCount).Select(c => c.Key), skip);
        }
    }
}
