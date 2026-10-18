using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public enum AttackOrigin { Other = 0, ScoredHand, EnemyAttack }
    public enum AttackCardScope { None = 0, Submitted, Pattern, Scoring }

    /// <summary>Immutable effective face and card state captured before the played cards move zones.</summary>
    public sealed class AttackCardSnapshot
    {
        public CardInstanceId Id { get; }
        public CardDefinitionId DefinitionId { get; }
        public string OriginId { get; }
        public Rank Rank { get; }
        public Suit Suit { get; }
        public CardEffects Effects { get; }
        public bool IsDisabled { get; }
        public bool CanScore => CardEffectRules.CanScore(Effects);
        public AttackCardSnapshot(CardInstance card, EffectiveCard face)
        {
            if (card == null || face == null || card.Id != face.Id) throw new ArgumentException("Snapshot must describe the same card.");
            Id = card.Id; DefinitionId = card.DefinitionId; OriginId = card.OriginId;
            Rank = face.Rank; Suit = face.Suit; Effects = card.Effects; IsDisabled = card.IsDisabled;
        }
    }

    /// <summary>Authoritative classification snapshot, never reclassified by damage or presentation.
    /// PatternCards includes ineffective cards; ScoringCards excludes them but may still contribute zero chips.</summary>
    public sealed class HandAttackInfo
    {
        public PokerHandClassification Pattern { get; }
        public IReadOnlyList<AttackCardSnapshot> SubmittedCards { get; }
        public IReadOnlyList<AttackCardSnapshot> PatternCards { get; }
        public IReadOnlyList<AttackCardSnapshot> ScoringCards { get; }
        public HandAttackInfo(PokerHandClassification pattern, IEnumerable<AttackCardSnapshot> submitted)
        {
            Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
            var cards = Guard.Copy(submitted, nameof(submitted));
            if (cards.Length < 1 || cards.Length > 5 || cards.Any(x => x == null) ||
                cards.Select(x => x.Id).Distinct().Count() != cards.Length ||
                !cards.Select(x => x.Id).SequenceEqual(pattern.SubmittedCards) ||
                pattern.ScoringCards.Count == 0 || pattern.ScoringCards.Distinct().Count() != pattern.ScoringCards.Count ||
                pattern.ScoringCards.Any(id => !cards.Any(card => card.Id == id)))
                throw new ArgumentException("Card snapshots must preserve the authoritative classification membership and order.");
            SubmittedCards = Guard.View(cards);
            PatternCards = Guard.View(pattern.ScoringCards.Select(id => cards.Single(card => card.Id == id)).ToArray());
            ScoringCards = Guard.View(PatternCards.Where(card => card.CanScore).ToArray());
        }
        public IReadOnlyList<AttackCardSnapshot> Cards(AttackCardScope scope)
        {
            switch (scope)
            {
                case AttackCardScope.Submitted: return SubmittedCards;
                case AttackCardScope.Pattern: return PatternCards;
                case AttackCardScope.Scoring: return ScoringCards;
                case AttackCardScope.None: return Array.Empty<AttackCardSnapshot>();
                default: throw new ArgumentOutOfRangeException(nameof(scope));
            }
        }
    }

    /// <summary>One target's original attack facts, shared unchanged by outgoing and incoming modifiers.</summary>
    public sealed class AttackContext
    {
        public ParticipantId Source { get; }
        public AttackOrigin Origin { get; }
        public long BaseDamage { get; }
        public HandAttackInfo Hand { get; }
        public AttackContext(ParticipantId source, AttackOrigin origin, long baseDamage, HandAttackInfo hand = null)
        {
            if (!source.IsValid || !Enum.IsDefined(typeof(AttackOrigin), origin) || baseDamage < 0 ||
                (origin == AttackOrigin.ScoredHand) != (hand != null))
                throw new ArgumentException("Only a scored-hand attack carries authoritative hand facts.");
            Source = source; Origin = origin; BaseDamage = baseDamage; Hand = hand;
        }
    }

    /// <summary>Evidence emitted by a modifier, not a condition interpreter or an ability definition.</summary>
    public sealed class DamageTriggerReason
    {
        public string SourceId { get; }
        public string Reason { get; }
        public AttackCardScope CardScope { get; }
        public IReadOnlyList<CardInstanceId> CardIds { get; }
        public DamageTriggerReason(string sourceId, string reason, AttackCardScope cardScope = AttackCardScope.None,
            IEnumerable<CardInstanceId> cardIds = null)
        {
            SourceId = Guard.Text(sourceId, nameof(sourceId)); Reason = Guard.Text(reason, nameof(reason));
            var ids = Guard.Copy(cardIds ?? Array.Empty<CardInstanceId>(), nameof(cardIds));
            if (!Enum.IsDefined(typeof(AttackCardScope), cardScope) || ids.Length > 5 || ids.Any(id => !id.IsValid) ||
                ids.Distinct().Count() != ids.Length || cardScope == AttackCardScope.None && ids.Length != 0)
                throw new ArgumentException("Invalid trigger evidence.");
            CardScope = cardScope; CardIds = Guard.View(ids);
        }
        internal void Validate(AttackContext attack)
        {
            if (CardScope == AttackCardScope.None) return;
            if (attack?.Hand == null || CardIds.Any(id => !attack.Hand.Cards(CardScope).Any(card => card.Id == id)))
                throw new ArgumentException("Trigger evidence must belong to its declared attack card scope.");
        }
    }
}
