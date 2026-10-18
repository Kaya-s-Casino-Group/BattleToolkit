using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Both replacement zones and the cards in transfer order. Commit Source and Target together.</summary>
    public sealed class CardZoneTransfer
    {
        public CardZone Source { get; }
        public CardZone Target { get; }
        public IReadOnlyList<CardInstanceId> Cards { get; }

        internal CardZoneTransfer(CardZone source, CardZone target, IEnumerable<CardInstanceId> cards)
        {
            Source = source;
            Target = target;
            Cards = Guard.View(Guard.Copy(cards, nameof(cards)));
        }
    }

    /// <summary>
    /// Pure ordered-zone operations. Inputs are never mutated, including on failure.
    /// Indices are zero-based; insertion accepts Count, while Move uses a final index in [0, Count).
    /// These operations validate local membership, not card existence or the rules of a particular mode.
    /// </summary>
    public static class CardZoneOperations
    {
        public static CardZone Insert(this CardZone zone, int index, CardInstanceId card) =>
            InsertRange(zone, index, new[] { card });

        /// <summary>Inserts cards in enumeration order, before index; Count appends.</summary>
        public static CardZone InsertRange(this CardZone zone, int index, IEnumerable<CardInstanceId> cards)
        {
            var current = Read(zone, nameof(zone));
            CheckIndex(index, current.Length, true, nameof(index));
            var added = CopyUnique(cards, nameof(cards));
            if (new HashSet<CardInstanceId>(current).Overlaps(added))
                throw new ArgumentException("An inserted card is already in the zone.", nameof(cards));
            if (added.Length == 0) return zone;
            var result = current.ToList();
            result.InsertRange(index, added);
            return WithCards(zone, result);
        }

        public static CardZone Remove(this CardZone zone, CardInstanceId card) =>
            RemoveRange(zone, new[] { card });

        /// <summary>Every requested card must exist exactly once. Remaining cards retain their order.</summary>
        public static CardZone RemoveRange(this CardZone zone, IEnumerable<CardInstanceId> cards)
        {
            var current = Read(zone, nameof(zone));
            var removed = CopyUnique(cards, nameof(cards));
            RequireMembers(current, removed);
            if (removed.Length == 0) return zone;
            var selected = new HashSet<CardInstanceId>(removed);
            return WithCards(zone, current.Where(c => !selected.Contains(c)));
        }

        public static CardZone RemoveAt(this CardZone zone, int index)
        {
            var current = Read(zone, nameof(zone));
            CheckIndex(index, current.Length, false, nameof(index));
            var result = current.ToList();
            result.RemoveAt(index);
            return WithCards(zone, result);
        }

        /// <summary>Moves one card to its final index; the other cards retain their relative order.</summary>
        public static CardZone Move(this CardZone zone, int fromIndex, int toIndex)
        {
            var current = Read(zone, nameof(zone));
            CheckIndex(fromIndex, current.Length, false, nameof(fromIndex));
            CheckIndex(toIndex, current.Length, false, nameof(toIndex));
            if (fromIndex == toIndex) return zone;
            var result = current.ToList();
            var card = result[fromIndex];
            result.RemoveAt(fromIndex);
            result.Insert(toIndex, card);
            return WithCards(zone, result);
        }

        public static CardZone Swap(this CardZone zone, int firstIndex, int secondIndex)
        {
            var current = Read(zone, nameof(zone));
            CheckIndex(firstIndex, current.Length, false, nameof(firstIndex));
            CheckIndex(secondIndex, current.Length, false, nameof(secondIndex));
            if (firstIndex == secondIndex) return zone;
            var card = current[firstIndex];
            current[firstIndex] = current[secondIndex];
            current[secondIndex] = card;
            return WithCards(zone, current);
        }

        /// <summary>Requires an exact permutation of the current cards. Empty zones accept an empty order.</summary>
        public static CardZone Reorder(this CardZone zone, IEnumerable<CardInstanceId> cards)
        {
            var current = Read(zone, nameof(zone));
            var ordered = CopyUnique(cards, nameof(cards));
            if (current.Length != ordered.Length || !new HashSet<CardInstanceId>(current).SetEquals(ordered))
                throw new ArgumentException("Reordering requires every current card exactly once.", nameof(cards));
            return current.SequenceEqual(ordered) ? zone : WithCards(zone, ordered);
        }

        /// <summary>
        /// Spades, Hearts, Clubs, Diamonds; within each suit, Ace down to Two.
        /// Resolve from card data, not artwork. Null explicitly denotes an unranked card, kept at the end.
        /// Equal faces and unranked cards retain their original relative order.
        /// </summary>
        public static CardZone SortBySuit(this CardZone zone, Func<CardInstanceId, CardDefinition> resolveDefinition) =>
            Sort(zone, resolveDefinition, rankFirst: false);

        /// <summary>Ace down to Two, then Spades, Hearts, Clubs, Diamonds. Same resolver/stability contract as SortBySuit.</summary>
        public static CardZone SortByRank(this CardZone zone, Func<CardInstanceId, CardDefinition> resolveDefinition) =>
            Sort(zone, resolveDefinition, rankFirst: true);

        private static CardZone Sort(CardZone zone, Func<CardInstanceId, CardDefinition> resolveDefinition, bool rankFirst)
        {
            var current = Read(zone, nameof(zone));
            if (resolveDefinition == null) throw new ArgumentNullException(nameof(resolveDefinition));
            // Resolve exactly once per identity, before sorting. A failed lookup leaves the original snapshot intact.
            var keyed = current.Select(id => {
                var definition = resolveDefinition(id);
                return new { Id = id, Unranked = definition == null,
                    Rank = definition == null ? 0 : -(int)definition.Rank,
                    Suit = definition == null ? 0 : SuitOrder(definition.Suit) };
            }).ToArray();
            var ordered = keyed.OrderBy(card => card.Unranked)
                .ThenBy(card => rankFirst ? card.Rank : card.Suit)
                .ThenBy(card => rankFirst ? card.Suit : card.Rank).Select(card => card.Id);
            return zone.Reorder(ordered);
        }

        private static int SuitOrder(Suit suit)
        {
            switch (suit)
            {
                case Suit.Spades: return 0;
                case Suit.Hearts: return 1;
                case Suit.Clubs: return 2;
                case Suit.Diamonds: return 3;
                default: throw new ArgumentException("Invalid sorting suit.", nameof(suit));
            }
        }

        public static CardZoneTransfer TransferTo(this CardZone source, CardZone target, CardInstanceId card, int targetIndex) =>
            TransferRangeTo(source, target, new[] { card }, targetIndex);

        /// <summary>
        /// Transfers cards in enumeration order into a distinct, disjoint target zone.
        /// Unselected source cards and existing target cards retain their relative order.
        /// </summary>
        public static CardZoneTransfer TransferRangeTo(this CardZone source, CardZone target,
            IEnumerable<CardInstanceId> cards, int targetIndex)
        {
            var from = Read(source, nameof(source));
            var to = Read(target, nameof(target));
            ValidatePair(source, target, from, to, targetIndex);
            var transferred = CopyUnique(cards, nameof(cards));
            RequireMembers(from, transferred);
            return TransferChecked(source, target, from, to, transferred, targetIndex);
        }

        /// <summary>
        /// Takes exactly count cards from the source's end, last card first, and inserts them in draw order.
        /// Zero is a no-op; insufficient cards fail without changing either zone. No shuffle or recycling occurs.
        /// </summary>
        public static CardZoneTransfer DrawTo(this CardZone source, CardZone target, int count, int targetIndex)
        {
            var from = Read(source, nameof(source));
            var to = Read(target, nameof(target));
            ValidatePair(source, target, from, to, targetIndex);
            if (count < 0 || count > from.Length) throw new ArgumentOutOfRangeException(nameof(count));
            var drawn = from.Reverse().Take(count).ToArray();
            return TransferChecked(source, target, from, to, drawn, targetIndex);
        }

        internal static CardInstanceId[] CopyUnique(IEnumerable<CardInstanceId> cards, string name)
        {
            var copy = Guard.Copy(cards, name);
            var seen = new HashSet<CardInstanceId>();
            if (copy.Any(c => !c.IsValid || !seen.Add(c)))
                throw new ArgumentException("Card IDs must be valid and unique within a zone or selection.", name);
            return copy;
        }

        private static CardInstanceId[] Read(CardZone zone, string name)
        {
            if (zone == null) throw new ArgumentNullException(name);
            // DataContract deserialization bypasses constructors. Check inputs before even a no-op.
            if (!zone.Id.IsValid || string.IsNullOrWhiteSpace(zone.Kind) ||
                zone.ControllerId.HasValue && !zone.ControllerId.Value.IsValid)
                throw new ArgumentException("Invalid zone identity.", name);
            return CopyUnique(zone.Cards, name);
        }

        private static void CheckIndex(int index, int count, bool insertion, string name)
        {
            if (index < 0 || (insertion ? index > count : index >= count))
                throw new ArgumentOutOfRangeException(name);
        }

        private static void RequireMembers(CardInstanceId[] current, CardInstanceId[] selected)
        {
            if (!new HashSet<CardInstanceId>(current).IsSupersetOf(selected))
                throw new ArgumentException("A requested card is not in the source zone.", "cards");
        }

        private static void ValidatePair(CardZone source, CardZone target, CardInstanceId[] from,
            CardInstanceId[] to, int targetIndex)
        {
            if (source.Id == target.Id) throw new ArgumentException("Transfer requires two distinct zones.", nameof(target));
            if (new HashSet<CardInstanceId>(from).Overlaps(to))
                throw new ArgumentException("The source and target already contain the same card.", nameof(target));
            CheckIndex(targetIndex, to.Length, true, nameof(targetIndex));
        }

        private static CardZone WithCards(CardZone zone, IEnumerable<CardInstanceId> cards) =>
            new CardZone(zone.Id, zone.Kind, zone.ControllerId, cards);

        private static CardZoneTransfer TransferChecked(CardZone source, CardZone target, CardInstanceId[] from,
            CardInstanceId[] to, CardInstanceId[] transferred, int targetIndex)
        {
            if (transferred.Length == 0) return new CardZoneTransfer(source, target, transferred);
            var selected = new HashSet<CardInstanceId>(transferred);
            var destination = to.ToList();
            destination.InsertRange(targetIndex, transferred);
            return new CardZoneTransfer(WithCards(source, from.Where(c => !selected.Contains(c))),
                WithCards(target, destination), transferred);
        }
    }
}
