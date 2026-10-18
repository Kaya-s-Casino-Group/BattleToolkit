using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PokerEngine
{
    [DataContract]
    public sealed class CardInstance
    {
        [DataMember(Order = 1, IsRequired = true)] public CardInstanceId Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public CardDefinitionId DefinitionId { get; private set; }
        [DataMember(Order = 3)] public string OriginId { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public CardEffects Effects { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public bool IsDisabled { get; private set; }
        // These are card-level permissions; the mode still checks turns, selection limits, and budgets.
        public bool CanPlay => !IsDisabled;
        public bool CanDiscard => !IsDisabled;
        public bool CanScore => CardEffectRules.CanScore(Effects);

        public CardInstance(CardInstanceId id, CardDefinitionId definitionId, string originId = null,
            CardEffects effects = CardEffects.None, bool isDisabled = false)
        {
            if (!id.IsValid || !definitionId.IsValid) throw new ArgumentException("Invalid card identity.");
            CardEffectRules.Validate(effects);
            Id = id; DefinitionId = definitionId;
            OriginId = originId == null ? null : Guard.Text(originId, nameof(originId));
            Effects = effects; IsDisabled = isDisabled;
        }

        public CardInstance WithEffects(CardEffects effects) => new CardInstance(Id, DefinitionId, OriginId, effects, IsDisabled);
        public CardInstance AddEffects(CardEffects effects) { CardEffectRules.Validate(effects); return WithEffects(Effects | effects); }
        public CardInstance RemoveEffects(CardEffects effects) { CardEffectRules.Validate(effects); return WithEffects(Effects & ~effects); }
        public CardInstance WithDisabled(bool disabled) => new CardInstance(Id, DefinitionId, OriginId, Effects, disabled);
    }

    public sealed class DeckEntry
    {
        public string Id { get; }
        public CardDefinitionId DefinitionId { get; }
        public CardEffects Effects { get; }
        public bool IsDisabled { get; }
        public DeckEntry(string id, CardDefinitionId definitionId, CardEffects effects = CardEffects.None, bool isDisabled = false)
        {
            Id = Guard.Text(id, nameof(id)); if (!definitionId.IsValid) throw new ArgumentException("Invalid definition.");
            CardEffectRules.Validate(effects); DefinitionId = definitionId; Effects = effects; IsDisabled = isDisabled;
        }
    }

    public static class CardZoneKinds
    {
        public const string Deck = "deck", Hand = "hand", Discard = "discard", Removed = "removed", Played = "played";
    }

    /// <summary>The only authoritative card location. The last element of Deck is its top.</summary>
    [DataContract]
    public sealed class CardZone
    {
        [DataMember(Order = 1, IsRequired = true)] public ZoneId Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public string Kind { get; private set; }
        [DataMember(Order = 3)] public ParticipantId? ControllerId { get; private set; }
        [DataMember(Name = "Cards", Order = 4, IsRequired = true)] private CardInstanceId[] cards;
        public IReadOnlyList<CardInstanceId> Cards => Guard.View(cards);
        public CardZone(ZoneId id, string kind, ParticipantId? controllerId, IEnumerable<CardInstanceId> cards)
        {
            if (!id.IsValid || string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("Invalid zone.");
            if (controllerId.HasValue && !controllerId.Value.IsValid)
                throw new ArgumentException("Invalid zone controller.");
            Id = id; Kind = kind; ControllerId = controllerId; this.cards = CardZoneOperations.CopyUnique(cards, nameof(cards));
        }
    }

    /// <summary>Derived evaluation input; never an additional authoritative copy of a card.</summary>
    public sealed class EffectiveCard
    {
        public CardInstanceId Id { get; }
        public Rank Rank { get; }
        public Suit Suit { get; }
        public EffectiveCard(CardInstanceId id, Rank rank, Suit suit)
        {
            if (!id.IsValid || !Enum.IsDefined(typeof(Rank), rank) || !Enum.IsDefined(typeof(Suit), suit))
                throw new ArgumentException("Invalid effective card.");
            Id = id; Rank = rank; Suit = suit;
        }
    }
}
