using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    /// <summary>Authority-only direct state correction. Null collections leave that area unchanged;
    /// supplied zone/loadout lists replace their complete ordered contents, including an empty list.
    /// This is not a player action and never executes card, death, revival or reward effects.</summary>
    [DataContract]
    public sealed class BattleAdminPatch
    {
        [DataMember(Name = "Vitals", Order = 1)] private BattleAdminVitals[] vitals;
        [DataMember(Name = "Zones", Order = 2)] private BattleAdminZone[] zones;
        [DataMember(Name = "Relics", Order = 3)] private BattleAdminRelics[] relics;
        [DataMember(Name = "Gold", Order = 4)] private BattleAdminGold[] gold;
        [DataMember(Order = 5)] public string Reason { get; private set; }
        public IReadOnlyList<BattleAdminVitals> Vitals => Guard.View(vitals ?? Array.Empty<BattleAdminVitals>());
        public IReadOnlyList<BattleAdminZone> Zones => Guard.View(zones ?? Array.Empty<BattleAdminZone>());
        public IReadOnlyList<BattleAdminRelics> Relics => Guard.View(relics ?? Array.Empty<BattleAdminRelics>());
        public IReadOnlyList<BattleAdminGold> Gold => Guard.View(gold ?? Array.Empty<BattleAdminGold>());
        public BattleAdminPatch(IEnumerable<BattleAdminVitals> vitals = null, IEnumerable<BattleAdminZone> zones = null,
            IEnumerable<BattleAdminRelics> relics = null, IEnumerable<BattleAdminGold> gold = null, string reason = null)
        {
            this.vitals = Guard.Copy(vitals ?? Array.Empty<BattleAdminVitals>(), nameof(vitals));
            this.zones = Guard.Copy(zones ?? Array.Empty<BattleAdminZone>(), nameof(zones));
            this.relics = Guard.Copy(relics ?? Array.Empty<BattleAdminRelics>(), nameof(relics));
            this.gold = Guard.Copy(gold ?? Array.Empty<BattleAdminGold>(), nameof(gold));
            Reason = reason;
        }

        /// <summary>Validates the transport shape, including deserialized requests. State membership,
        /// catalog definitions and resulting health/ownership are validated by ApplyAdmin.</summary>
        public void Validate()
        {
            if (Reason != null && Reason.Length > 1024 || Vitals.Count > 64 || Zones.Count > 192 || Relics.Count > 64 || Gold.Count > 64 ||
                Vitals.Count + Zones.Count + Relics.Count + Gold.Count == 0 ||
                Vitals.Any(v => v == null || !v.Unit.IsValid || !v.Health.HasValue && !v.MaxHealth.HasValue && !v.Block.HasValue ||
                    v.Health < 0 || v.MaxHealth <= 0 || v.Block < 0) ||
                Vitals.Select(v => v.Unit).Distinct().Count() != Vitals.Count ||
                Zones.Any(z => z == null || !z.Owner.IsValid || z.Kind != CardZoneKinds.Hand && z.Kind != CardZoneKinds.Deck && z.Kind != CardZoneKinds.Discard ||
                    z.Cards == null || z.Cards.Count > 512 || z.Cards.Any(c => c == null || BadText(c.InstanceId) || BadText(c.DefinitionId) ||
                        c.InstanceId == null && c.DefinitionId == null)) ||
                Zones.Select(z => z.Owner.Value + "/" + z.Kind).Distinct(StringComparer.Ordinal).Count() != Zones.Count ||
                Relics.Any(r => r == null || !r.Owner.IsValid || r.Items == null || r.Items.Count > 32 || r.Items.Any(i =>
                    i == null || string.IsNullOrWhiteSpace(i.DefinitionId) || BadText(i.DefinitionId) || BadText(i.InstanceId) ||
                    i.Level < 1 || i.Charges < 0 || i.Growth < 0 || i.CooldownRounds < 0 || i.UsesThisRound < 0)) ||
                Relics.Select(r => r.Owner).Distinct().Count() != Relics.Count ||
                Gold.Any(g => g == null || !g.Owner.IsValid || g.Amount < 0) || Gold.Select(g => g.Owner).Distinct().Count() != Gold.Count)
                throw new ArgumentException("Invalid administrative patch.");
            foreach (var card in Zones.SelectMany(z => z.Cards))
                if (card.Effects.HasValue) CardEffectRules.Validate(card.Effects.Value);
        }
        private static bool BadText(string value) => value != null && (string.IsNullOrWhiteSpace(value) || value.Length > 2048);
    }

    [DataContract]
    public sealed class BattleAdminVitals
    {
        [DataMember(Order = 1, IsRequired = true)] public ParticipantId Unit { get; private set; }
        [DataMember(Order = 2)] public long? Health { get; private set; }
        [DataMember(Order = 3)] public long? MaxHealth { get; private set; }
        [DataMember(Order = 4)] public long? Block { get; private set; }
        public BattleAdminVitals(ParticipantId unit, long? health = null, long? maxHealth = null, long? block = null)
        { Unit = unit; Health = health; MaxHealth = maxHealth; Block = block; }
    }

    [DataContract]
    public sealed class BattleAdminZone
    {
        [DataMember(Order = 1, IsRequired = true)] public ParticipantId Owner { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public string Kind { get; private set; }
        [DataMember(Name = "Cards", Order = 3, IsRequired = true)] private BattleAdminCard[] cards;
        public IReadOnlyList<BattleAdminCard> Cards => cards == null ? null : Guard.View(cards);
        /// <summary>Deck ordering is bottom to top: the last card will be drawn first.</summary>
        public BattleAdminZone(ParticipantId owner, string kind, IEnumerable<BattleAdminCard> cards)
        { Owner = owner; Kind = kind; this.cards = Guard.Copy(cards, nameof(cards)); }
    }

    [DataContract]
    public sealed class BattleAdminCard
    {
        [DataMember(Order = 1)] public string InstanceId { get; private set; }
        [DataMember(Order = 2)] public string DefinitionId { get; private set; }
        [DataMember(Order = 3)] public CardEffects? Effects { get; private set; }
        [DataMember(Order = 4)] public bool? IsDisabled { get; private set; }
        /// <summary>Omit InstanceId to create a deterministic new instance. Existing cards preserve
        /// their ID, origin and omitted fields. A new card requires DefinitionId.</summary>
        public BattleAdminCard(string instanceId = null, string definitionId = null, CardEffects? effects = null, bool? isDisabled = null)
        { InstanceId = instanceId; DefinitionId = definitionId; Effects = effects; IsDisabled = isDisabled; }
    }

    [DataContract]
    public sealed class BattleAdminRelics
    {
        [DataMember(Order = 1, IsRequired = true)] public ParticipantId Owner { get; private set; }
        [DataMember(Name = "Items", Order = 2, IsRequired = true)] private BattleAdminRelic[] items;
        public IReadOnlyList<BattleAdminRelic> Items => items == null ? null : Guard.View(items);
        public BattleAdminRelics(ParticipantId owner, IEnumerable<BattleAdminRelic> items)
        { Owner = owner; this.items = Guard.Copy(items, nameof(items)); }
    }

    [DataContract]
    public sealed class BattleAdminRelic
    {
        [DataMember(Order = 1, IsRequired = true)] public string DefinitionId { get; private set; }
        [DataMember(Order = 2)] public string InstanceId { get; private set; }
        [DataMember(Order = 3)] public int? Level { get; private set; }
        [DataMember(Order = 4)] public int? Charges { get; private set; }
        [DataMember(Order = 5)] public long? Growth { get; private set; }
        [DataMember(Order = 6)] public int? CooldownRounds { get; private set; }
        [DataMember(Order = 7)] public int? UsesThisRound { get; private set; }
        /// <summary>Existing IDs retain omitted runtime fields and memory. New IDs are generated
        /// deterministically and start from their catalog definition's default charges.</summary>
        public BattleAdminRelic(string definitionId, string instanceId = null, int? level = null, int? charges = null,
            long? growth = null, int? cooldownRounds = null, int? usesThisRound = null)
        {
            DefinitionId = definitionId; InstanceId = instanceId; Level = level; Charges = charges;
            Growth = growth; CooldownRounds = cooldownRounds; UsesThisRound = usesThisRound;
        }
    }

    [DataContract]
    public sealed class BattleAdminGold
    {
        [DataMember(Order = 1, IsRequired = true)] public ParticipantId Owner { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public long Amount { get; private set; }
        public BattleAdminGold(ParticipantId owner, long amount) { Owner = owner; Amount = amount; }
    }
}
