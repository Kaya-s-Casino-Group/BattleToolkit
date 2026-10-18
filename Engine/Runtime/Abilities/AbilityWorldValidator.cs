using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Shared structural checks only. Each mode separately validates its phases, commands and card rules.</summary>
    public static class AbilityWorldValidator
    {
        public static void Validate(AbilityWorld state, ContentCatalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            try { ValidateCore(state, catalog); }
            catch (NullReferenceException) { throw new StateValidationException("MissingData", "AbilityWorld"); }
            catch (ArgumentNullException) { throw new StateValidationException("MissingData", "AbilityWorld"); }
            catch (OverflowException) { throw new StateValidationException("AmountOverflow", "AbilityWorld"); }
        }
        private static void Require(bool condition, string code, string path)
        { if (!condition) throw new StateValidationException(code, path); }
        private static bool Defined<T>(T value) => Enum.IsDefined(typeof(T), value);
        private static void Unique<T>(IEnumerable<T> values, string path)
        { var seen = new HashSet<T>(); foreach (var value in values) Require(seen.Add(value), "DuplicateId", path); }
        private static void ValidateCore(AbilityWorld m, ContentCatalog catalog)
        {
            Require(m != null, "MissingData", "AbilityWorld");
            Require(!string.IsNullOrWhiteSpace(m.Id) && m.Revision >= 0 && Defined(m.Mode) &&
                (m.ScopeId == null || !string.IsNullOrWhiteSpace(m.ScopeId)) && (m.PhaseKey == null || !string.IsNullOrWhiteSpace(m.PhaseKey)), "InvalidHeader", "AbilityWorld");
            Unique(m.Participants.Select(p => p.Id), "Participants");
            Require(m.Participants.Count > 0, "InvalidParticipants", "Participants");
            foreach (var p in m.Participants)
                Require(p.Id.IsValid && p.Resources.Chips.Value >= 0 && p.Resources.Energy >= 0 && !(p.Resources.Health < 0), "InvalidResource", "Participants.Resources");
            if (m.CurrentActorId.HasValue) Require(m.Participants.Any(p => p.Id == m.CurrentActorId.Value), "UnknownParticipant", "CurrentActorId");
            Unique(m.Cards.Select(c => c.Id), "Cards");
            Unique(m.Cards.Where(c => c.OriginId != null).Select(c => c.OriginId), "Cards.OriginId");
            foreach (var card in m.Cards) Require(card.Id.IsValid && catalog.Contains(card.DefinitionId) && CardEffectRules.IsValid(card.Effects) &&
                (card.OriginId == null || !string.IsNullOrWhiteSpace(card.OriginId)), "UnknownCardDefinition", "Cards");
            Unique(m.Zones.Select(z => z.Id), "Zones"); var locations = new HashSet<CardInstanceId>();
            foreach (var zone in m.Zones)
            {
                Require(zone.Id.IsValid && !string.IsNullOrWhiteSpace(zone.Kind), "InvalidZone", "Zones");
                if (zone.ControllerId.HasValue) Require(m.Participants.Any(p => p.Id == zone.ControllerId.Value), "UnknownParticipant", "Zones.ControllerId");
                foreach (var id in zone.Cards)
                { Require(m.Cards.Any(c => c.Id == id), "UnknownCard", "Zones.Cards"); Require(locations.Add(id), "DuplicateCardLocation", "Zones.Cards"); }
            }
            Require(locations.Count == m.Cards.Count, "UnlocatedCard", "Cards");
            Unique(m.Abilities.Select(a => a.Id), "Abilities"); Unique(m.Statuses.Select(s => s.Id), "Statuses");
            foreach (var a in m.Abilities)
            {
                Require(a.Id.IsValid && catalog.Contains(a.DefinitionId) && m.Participants.Any(p => p.Id == a.OwnerId), "InvalidAbility", "Abilities");
                Require(!(a.RemainingCharges < 0) && a.UsesThisRound >= 0 && a.CooldownRounds >= 0, "InvalidAbilityCounters", "Abilities");
                Reference(m, a.Source, "Abilities.Source"); ValidateMemory(m, a.Memory);
            }
            foreach (var status in m.Statuses)
            {
                Require(status.Id.IsValid && catalog.Contains(status.DefinitionId) && status.Stacks > 0, "InvalidStatus", "Statuses");
                Reference(m, status.Source, "Statuses.Source"); Reference(m, status.Target, "Statuses.Target");
                var d = status.Duration; Require(Defined(d.Kind), "InvalidDuration", "Statuses.Duration");
                bool bound = d.Kind == DurationKind.UntilRoundEnd || d.Kind == DurationKind.UntilPhaseEnd;
                Require(bound == (d.ScopeId != null) && (d.Kind == DurationKind.UntilPhaseEnd) == (d.PhaseKey != null) &&
                    (d.Kind == DurationKind.TriggerCount) == d.RemainingTriggers.HasValue, "InvalidDuration", "Statuses.Duration");
                if (bound) Require(d.ScopeId == m.ScopeId && !string.IsNullOrWhiteSpace(d.ScopeId), "UnknownRound", "Statuses.Duration");
                if (d.PhaseKey != null) Require(!string.IsNullOrWhiteSpace(d.PhaseKey), "InvalidDuration", "Statuses.Duration");
                if (d.RemainingTriggers.HasValue) Require(d.RemainingTriggers.Value > 0, "InvalidDuration", "Statuses.Duration");
            }
            foreach (var continuation in m.Resolution.PendingEffects) Continuation(m, continuation, "Resolution.PendingEffects");
            Require(m.IsRunning || m.ActiveDecision == null && m.Resolution.PendingEffects.Count == 0, "TerminalWork", "AbilityWorld");
            if (m.AbilityWindow != null)
            {
                var window = m.AbilityWindow;
                Require(m.IsRunning && !string.IsNullOrWhiteSpace(window.Id) && !string.IsNullOrWhiteSpace(window.Timing), "InvalidAbilityWindow", "AbilityWindow");
                Require(window.ScopeId == null || window.ScopeId == m.ScopeId, "UnknownRound", "AbilityWindow.ScopeId");
                if (window.EventActorId.HasValue) Require(m.Participants.Any(p => p.Id == window.EventActorId.Value), "UnknownParticipant", "AbilityWindow.EventActorId");
                if (window.Action != null) Require(!string.IsNullOrWhiteSpace(window.Action), "InvalidAbilityWindow", "AbilityWindow.Action");
            }
            var r = m.Resolution;
            Require(r.StepsUsed >= 0 && r.StepsUsed <= 256, "ResolutionBudget", "Resolution");
            Unique(r.ProcessedTriggers, "Resolution.ProcessedTriggers");
            Require(r.ProcessedTriggers.All(id => m.Abilities.Any(a => a.Id == id)), "UnknownAbility", "Resolution.ProcessedTriggers");
            Require(r.WindowId == null ? r.ProcessedTriggers.Count == 0 && r.StepsUsed == 0 && r.SuspendedDecision == null :
                m.AbilityWindow != null && r.WindowId == m.AbilityWindow.Id, "StaleResolution", "Resolution.WindowId");
            if (r.SuspendedDecision != null)
            {
                Require(r.SuspendedDecision.Kind == DecisionKind.ModeAction && m.ActiveDecision?.Kind == DecisionKind.SelectTargets,
                    "InvalidSuspendedDecision", "Resolution.SuspendedDecision");
                ValidateDecision(m, r.SuspendedDecision);
            }
            if (m.ActiveDecision != null) ValidateDecision(m, m.ActiveDecision);
            ValidateKnowledge(m);
        }
        private static void ValidateMemory(AbilityWorld m, AbilityMemory memory)
        {
            Require(memory != null && memory.Version == 1, "InvalidMemoryVersion", "Abilities.Memory");
            Unique(memory.Counters.Select(c => c.Key).Concat(memory.Marks.Select(x => x.Key)), "Abilities.Memory.Keys");
            foreach (var c in memory.Counters)
                Require(!string.IsNullOrWhiteSpace(c.Key) && c.Value >= 0 && Defined(c.Scope), "InvalidCounter", "Abilities.Memory.Counters");
            foreach (var mark in memory.Marks)
            {
                Require(!string.IsNullOrWhiteSpace(mark.Key) && Defined(mark.Scope), "InvalidMark", "Abilities.Memory.Marks");
                Reference(m, mark.Target, "Abilities.Memory.Marks.Target");
                Require(mark.Target.Kind != EntityKind.Card || mark.Scope == MemoryScope.Round, "InvalidMarkScope", "Abilities.Memory.Marks");
            }
        }
        private static void ValidateDecision(AbilityWorld m, DecisionState d)
        {
            Require(m.IsRunning, "InvalidPhase", "Decision");
            Require(d.Id.IsValid && Defined(d.Kind) && m.Participants.Any(p => p.Id == d.ActorId && p.Active), "InvalidDecision", "Decision");
            if (d.Kind == DecisionKind.SelectTargets)
            {
                Require(d.CardCandidates.Count == 0 && d.OptionIds.Count == 0 && d.MinSelections == 0 && d.MaxSelections == 0, "MixedSelectionFormat", "Decision");
                Continuation(m, d.Continuation, "Decision.Continuation"); ValidateSelectionSpec(d.Selection);
                foreach (var candidate in d.Selection.Candidates) if (candidate.Entity != null) Reference(m, candidate.Entity, "Decision.Selection.Candidates");
                return;
            }
            Require(d.Selection == null, "MixedSelectionFormat", "Decision.Selection");
            Require(d.MinSelections >= 0 && d.MaxSelections >= d.MinSelections, "InvalidSelectionCount", "Decision");
            Unique(d.CardCandidates, "Decision.CardCandidates"); Unique(d.OptionIds, "Decision.OptionIds");
            if (d.Kind == DecisionKind.ModeAction)
                Require(m.CurrentActorId == d.ActorId && d.Continuation == null && d.CardCandidates.Count == 0 && d.OptionIds.Count == 0 &&
                    d.MinSelections == 0 && d.MaxSelections == 0, "InvalidModeDecision", "Decision");
            else
            {
                Continuation(m, d.Continuation, "Decision.Continuation");
                if (d.Kind == DecisionKind.SelectCards)
                    Require(d.OptionIds.Count == 0 && d.MaxSelections <= d.CardCandidates.Count &&
                        d.CardCandidates.All(id => m.Cards.Any(c => c.Id == id)), "InvalidCardSelection", "Decision");
                else Require(d.CardCandidates.Count == 0 && d.OptionIds.Count > 0 && d.OptionIds.All(id => !string.IsNullOrWhiteSpace(id)) &&
                    d.MinSelections == 1 && d.MaxSelections == 1, "InvalidOptionSelection", "Decision");
            }
        }
        public static void ValidateSelectionSpec(SelectionSpec spec)
        {
            Require(spec != null && Defined(spec.TargetKind) && Defined(spec.TimeoutPolicy), "InvalidSelectionSpec", "Selection");
            Require(spec.MinCount >= 0 && spec.MaxCount >= spec.MinCount && spec.MaxCount <= spec.Candidates.Count, "InvalidSelectionCount", "Selection");
            Require(spec.TimeoutPolicy != SelectionTimeoutPolicy.Skip || spec.AllowSkip, "InvalidSelectionTimeout", "Selection");
            Unique(spec.Candidates.Select(c => c.Key), "Selection.Candidates.Keys");
            Unique(spec.Candidates.Where(c => c.Entity != null).Select(c => Tuple.Create(c.Entity.Kind, c.Entity.Id)), "Selection.Candidates.Entities");
            foreach (var candidate in spec.Candidates)
            {
                Require(!string.IsNullOrWhiteSpace(candidate.Key), "InvalidCandidate", "Selection.Candidates");
                if (spec.TargetKind == SelectionTargetKind.Options) Require(candidate.Entity == null, "InvalidCandidateType", "Selection.Candidates");
                else Require(candidate.Entity != null && !string.IsNullOrWhiteSpace(candidate.Entity.Id) &&
                    candidate.Entity.Kind == (spec.TargetKind == SelectionTargetKind.Cards ? EntityKind.Card : EntityKind.Participant), "InvalidCandidateType", "Selection.Candidates");
            }
        }
        private static void ValidateKnowledge(AbilityWorld m)
        {
            Unique(m.Knowledge.Select(g => g.Id), "Knowledge");
            foreach (var g in m.Knowledge)
            {
                Require(g.Id.IsValid && Defined(g.Mode) && Defined(g.Audience) && g.GrantedAtRevision >= 0 && g.GrantedAtRevision <= m.Revision, "InvalidKnowledge", "Knowledge");
                Require(g.ScopeId == m.ScopeId && !string.IsNullOrWhiteSpace(g.ScopeId) && m.Cards.Any(c => c.Id == g.CardId), "UnknownCard", "Knowledge.CardId");
                Reference(m, g.Source, "Knowledge.Source");
                Require((g.Audience == KnowledgeAudience.Participant) == g.ViewerId.HasValue &&
                    (!g.ViewerId.HasValue || m.Participants.Any(p => p.Id == g.ViewerId.Value)), "InvalidKnowledgeAudience", "Knowledge");
                Require((g.Mode == KnowledgeMode.Snapshot) == (g.ObservedFace != null) &&
                    (g.Mode == KnowledgeMode.TrackCardInZone) == g.BoundZoneId.HasValue, "InvalidKnowledgeMode", "Knowledge");
                if (g.ObservedFace != null) Require(Defined(g.ObservedFace.Rank) && Defined(g.ObservedFace.Suit), "InvalidFace", "Knowledge.ObservedFace");
                if (g.BoundZoneId.HasValue) Require(m.Zones.Any(z => z.Id == g.BoundZoneId.Value), "UnknownZone", "Knowledge.BoundZoneId");
                if (g.UntilPhaseEnd != null) Require(!string.IsNullOrWhiteSpace(g.UntilPhaseEnd), "InvalidKnowledgeExpiry", "Knowledge.UntilPhaseEnd");
            }
        }
        private static void Continuation(AbilityWorld m, EffectContinuation c, string path)
        {
            Require(c != null && c.NextEffectIndex >= 0 && m.Abilities.Any(a => a.Id == c.AbilityId), "InvalidContinuation", path);
            foreach (var target in c.Targets) Reference(m, target, path + ".Targets");
        }
        private static void Reference(AbilityWorld m, EntityRef reference, string path)
        {
            Require(reference != null && !string.IsNullOrWhiteSpace(reference.Id), "InvalidReference", path);
            bool exists = reference.Kind == EntityKind.Match ? m.Id == reference.Id :
                reference.Kind == EntityKind.Participant ? m.Participants.Any(p => p.Id.Value == reference.Id) :
                reference.Kind == EntityKind.Card ? m.Cards.Any(c => c.Id.Value == reference.Id) :
                reference.Kind == EntityKind.Ability && m.Abilities.Any(a => a.Id.Value == reference.Id);
            Require(exists, "UnknownReference", path);
        }
    }
}
