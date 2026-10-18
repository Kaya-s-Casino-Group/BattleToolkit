using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    /// <summary>Shared character/monster template. Control, decks, turns and rewards belong to their respective owners.</summary>
    public sealed class CombatantDefinition
    {
        public string Id { get; }
        public int MaxHealth { get; }
        public int InitialBlock { get; }
        public CombatantDefinition(string id, int maxHealth, int initialBlock = 0)
        {
            Id = Guard.Text(id, nameof(id));
            if (maxHealth <= 0 || initialBlock < 0) throw new ArgumentException("Invalid combatant template.");
            MaxHealth = maxHealth; InitialBlock = initialBlock;
        }
        public CombatantState Create(ParticipantId id, string teamId, int? health = null) =>
            new CombatantState(id, Id, teamId, MaxHealth, health ?? MaxHealth, InitialBlock);
    }

    /// <summary>Shared combat vitals only. Player action resources and mode-specific currencies live outside the unit.</summary>
    [DataContract]
    public sealed class CombatantState
    {
        [DataMember(Order = 1, IsRequired = true)] public ParticipantId Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public string DefinitionId { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public string TeamId { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public int MaxHealth { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public int Health { get; private set; }
        [DataMember(Order = 6, IsRequired = true)] public int Block { get; private set; }
        public bool IsAlive => Health > 0;
        public CombatantState(ParticipantId id, string definitionId, string teamId, int maxHealth, int health, int block = 0)
        {
            if (!id.IsValid || maxHealth <= 0 || health < 0 || health > maxHealth || block < 0)
                throw new ArgumentException("Combat units require bounded health and nonnegative block.");
            Id = id; DefinitionId = Guard.Text(definitionId, nameof(definitionId)); TeamId = Guard.Text(teamId, nameof(teamId));
            MaxHealth = maxHealth; Health = health; Block = block;
        }
        internal CombatantState WithVitals(int health, int block) => new CombatantState(Id, DefinitionId, TeamId, MaxHealth, health, block);
    }

    /// <summary>Common combat state. No player/monster subclasses or duplicate AbilityWorld are stored.</summary>
    [DataContract]
    public sealed class CombatState
    {
        [DataMember(Order = 1, IsRequired = true)] public string Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public long Revision { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public string RulesVersion { get; private set; }
        [DataMember(Name = "Combatants", Order = 4, IsRequired = true)] private CombatantState[] combatants;
        [DataMember(Name = "Statuses", Order = 5, IsRequired = true)] private StatusInstance[] statuses;
        public IReadOnlyList<CombatantState> Combatants => Guard.View(combatants);
        public IReadOnlyList<StatusInstance> Statuses => Guard.View(statuses);
        public CombatState(string id, long revision, IEnumerable<CombatantState> combatants, IEnumerable<StatusInstance> statuses = null,
            string rulesVersion = CombatEngine.RulesVersion)
        {
            Id = Guard.Text(id, nameof(id)); Revision = revision; RulesVersion = rulesVersion;
            this.combatants = Guard.Copy(combatants, nameof(combatants)); this.statuses = Guard.Copy(statuses ?? new StatusInstance[0], nameof(statuses));
            CombatEngine.Validate(this);
        }
        public CombatantState Get(ParticipantId id) => combatants.FirstOrDefault(unit => unit.Id == id) ?? throw new ArgumentException("Unknown combatant.", nameof(id));
    }
}
