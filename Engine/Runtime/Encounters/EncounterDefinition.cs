using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public enum EncounterObjectiveKind { ReachScore = 1, DefeatOpposition, SurviveRounds }

    /// <summary>Objective data only. The selected mode supplies the evaluator.</summary>
    public sealed class EncounterObjectiveDefinition
    {
        public EncounterObjectiveKind Kind { get; }
        public long? Target { get; }
        public EncounterObjectiveDefinition(EncounterObjectiveKind kind, long? target = null)
        {
            if (!Enum.IsDefined(typeof(EncounterObjectiveKind), kind) ||
                (kind == EncounterObjectiveKind.DefeatOpposition ? target.HasValue : !target.HasValue || target <= 0) ||
                kind == EncounterObjectiveKind.SurviveRounds && target > 1000)
                throw new ArgumentException("Invalid encounter objective.");
            Kind = kind; Target = target;
        }
    }

    /// <summary>A content slot; actual actor identities and controllers are bound by the host.</summary>
    public sealed class EncounterParticipantDefinition
    {
        public string SlotId { get; }
        public string TeamId { get; }
        /// <summary>Optional legacy mode resource description (Draw/Holdem). Null when the mode owns different typed data.</summary>
        public ResourceState InitialResources { get; }
        public EncounterParticipantDefinition(string slotId, string teamId)
        {
            SlotId = Guard.Text(slotId, nameof(slotId)); TeamId = Guard.Text(teamId, nameof(teamId));
        }
        public EncounterParticipantDefinition(string slotId, string teamId, ResourceState initialResources) : this(slotId, teamId)
        {
            InitialResources = initialResources ?? throw new ArgumentNullException(nameof(initialResources));
        }
    }

    /// <summary>Shared immutable encounter metadata, without Run rewards or mode-specific card/turn rules.</summary>
    public sealed class EncounterDefinition
    {
        public string Id { get; }
        public string Version { get; }
        public string ModeId { get; }
        public string ModeRulesVersion { get; }
        public EncounterObjectiveDefinition Objective { get; }
        public int RoundLimit { get; }
        public IReadOnlyList<EncounterParticipantDefinition> Participants { get; }
        public IReadOnlyList<string> ModifierIds { get; }
        public EncounterDefinition(string id, string version, string modeId, string modeRulesVersion,
            EncounterObjectiveDefinition objective, int roundLimit, IEnumerable<EncounterParticipantDefinition> participants,
            IEnumerable<string> modifierIds = null)
        {
            Id = Guard.Text(id, nameof(id)); Version = Guard.Text(version, nameof(version));
            ModeId = Guard.Text(modeId, nameof(modeId)); ModeRulesVersion = Guard.Text(modeRulesVersion, nameof(modeRulesVersion));
            Objective = objective ?? throw new ArgumentNullException(nameof(objective));
            var actors = Guard.Copy(participants, nameof(participants)); var modifiers = Guard.Copy(modifierIds ?? new string[0], nameof(modifierIds));
            if (roundLimit < 1 || roundLimit > 1000 || actors.Length < 1 || actors.Length > 64 ||
                actors.Select(a => a.SlotId).Distinct(StringComparer.Ordinal).Count() != actors.Length || modifiers.Length > 128 ||
                modifiers.Any(string.IsNullOrWhiteSpace) || modifiers.Distinct(StringComparer.Ordinal).Count() != modifiers.Length ||
                objective.Kind == EncounterObjectiveKind.SurviveRounds && objective.Target > roundLimit ||
                objective.Kind == EncounterObjectiveKind.DefeatOpposition && actors.Select(a => a.TeamId).Distinct(StringComparer.Ordinal).Count() < 2)
                throw new ArgumentException("Invalid encounter definition.");
            RoundLimit = roundLimit; Participants = Guard.View(actors); ModifierIds = Guard.View(modifiers);
        }
    }
}
