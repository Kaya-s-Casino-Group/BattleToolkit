using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Immutable definition registry. Effect behavior is added by the later ability executor.</summary>
    public sealed class ContentCatalog
    {
        private readonly Dictionary<CardDefinitionId, CardDefinition> cards;
        private readonly HashSet<AbilityDefinitionId> abilities;
        private readonly HashSet<StatusDefinitionId> statuses;
        private readonly Dictionary<AbilityDefinitionId, AbilityDefinition> abilityDefinitions = new Dictionary<AbilityDefinitionId, AbilityDefinition>();
        public string Version { get; }
        public ContentCatalog(string version, IEnumerable<CardDefinition> cards,
            IEnumerable<AbilityDefinitionId> abilities, IEnumerable<StatusDefinitionId> statuses)
        {
            Version = Guard.Text(version, nameof(version));
            this.cards = Guard.Copy(cards, nameof(cards)).ToDictionary(c => c.Id);
            var abilityIds = Guard.Copy(abilities, nameof(abilities));
            var statusIds = Guard.Copy(statuses, nameof(statuses));
            this.abilities = new HashSet<AbilityDefinitionId>(abilityIds);
            this.statuses = new HashSet<StatusDefinitionId>(statusIds);
            if (abilityIds.Any(x => !x.IsValid) || statusIds.Any(x => !x.IsValid) ||
                this.abilities.Count != abilityIds.Length || this.statuses.Count != statusIds.Length)
                throw new ArgumentException("Definition IDs must be valid and unique.");
        }
        public bool Contains(CardDefinitionId id) => cards.ContainsKey(id);
        public bool Contains(AbilityDefinitionId id) => abilities.Contains(id);
        public bool Contains(StatusDefinitionId id) => statuses.Contains(id);
        public CardDefinition Get(CardDefinitionId id) => cards[id];
        public bool TryGetAbility(AbilityDefinitionId id, out AbilityDefinition definition) => abilityDefinitions.TryGetValue(id, out definition);
        public ContentCatalog(string version, IEnumerable<CardDefinition> cards,
            IEnumerable<AbilityDefinition> abilities, IEnumerable<StatusDefinitionId> statuses)
            : this(version, cards, new AbilityDefinitionId[0], statuses)
        {
            foreach (var ability in Guard.Copy(abilities, nameof(abilities)))
            {
                if (!this.abilities.Add(ability.Id)) throw new ArgumentException("Duplicate ability definition.");
                abilityDefinitions.Add(ability.Id, ability);
            }
        }
    }
}
