using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    /// <summary>Complete rule-neutral card ownership for one match. Not an observer/public projection.</summary>
    [DataContract]
    public sealed class CardTableState
    {
        [DataMember(Order = 1, IsRequired = true)] public MatchId MatchId { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public long Revision { get; private set; }
        [DataMember(Name = "Cards", Order = 3, IsRequired = true)] private CardInstance[] cards;
        [DataMember(Name = "Zones", Order = 4, IsRequired = true)] private CardZone[] zones;
        public IReadOnlyList<CardInstance> Cards => Guard.View(cards);
        public IReadOnlyList<CardZone> Zones => Guard.View(zones);

        public CardTableState(MatchId matchId, IEnumerable<CardInstance> cards, IEnumerable<CardZone> zones, long revision = 0)
        {
            MatchId = matchId; Revision = revision; this.cards = Guard.Copy(cards, nameof(cards));
            this.zones = Guard.Copy(zones, nameof(zones)); Validate();
        }
        public CardZone GetZone(ZoneId id) => zones.FirstOrDefault(zone => zone.Id == id) ?? throw new ArgumentException("Unknown match zone.", nameof(id));
        public CardInstance GetCard(CardInstanceId id) => cards.FirstOrDefault(card => card.Id == id) ?? throw new ArgumentException("Unknown match card.", nameof(id));
        public void Validate()
        {
            if (!MatchId.IsValid || Revision < 0 || cards == null || zones == null) throw new ArgumentException("Invalid card table state.");
            var known = new HashSet<CardInstanceId>(); var locations = new HashSet<CardInstanceId>(); var zoneIds = new HashSet<ZoneId>();
            foreach (var card in cards)
            {
                if (card == null || !known.Add(card.Id)) throw new ArgumentException("Card identities must be unique across the match.");
                new CardInstance(card.Id, card.DefinitionId, card.OriginId, card.Effects, card.IsDisabled);
            }
            foreach (var zone in zones)
            {
                if (zone == null || !zoneIds.Add(zone.Id)) throw new ArgumentException("Zone identities must be unique across the match.");
                new CardZone(zone.Id, zone.Kind, zone.ControllerId, zone.Cards);
                foreach (var id in zone.Cards)
                    if (!known.Contains(id) || !locations.Add(id)) throw new ArgumentException("Each match card must have exactly one known zone.");
            }
            if (!known.SetEquals(locations)) throw new ArgumentException("Match cards cannot be orphaned.");
        }
    }

    public sealed class CardTableChange
    {
        internal CardTableSession Owner { get; }
        public CardTableState Before { get; }
        public CardTableState After { get; }
        public IReadOnlyList<CardInstanceId> Transferred { get; }
        internal CardTableChange(CardTableSession owner, CardTableState before, CardTableState after, IEnumerable<CardInstanceId> transferred)
        { Owner = owner; Before = before; After = after; Transferred = Guard.View(Guard.Copy(transferred, nameof(transferred))); }
    }

    /// <summary>Owns the match's immutable table. Prepare/commit permits presentation validation before an atomic commit.</summary>
    public sealed class CardTableSession
    {
        public CardTableState State { get; private set; }
        public CardTableSession(CardTableState state) { if (state == null) throw new ArgumentNullException(nameof(state)); state.Validate(); State = state; }
        public void Commit(CardTableChange change)
        {
            if (change == null || change.Owner != this || !ReferenceEquals(change.Before, State))
                throw new InvalidOperationException("Stale or foreign match command.");
            State = change.After;
        }
        public CardTableChange PrepareDraw(ZoneId deck, ZoneId hand, int count, int capacity)
        {
            RequireKind(deck, CardZoneKinds.Deck); RequireKind(hand, CardZoneKinds.Hand);
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            var transfer = State.GetZone(deck).DrawTo(State.GetZone(hand), count, State.GetZone(hand).Cards.Count);
            CheckCapacity(transfer.Target, capacity); return FromTransfer(transfer);
        }
        public CardTableChange PreparePlay(ZoneId hand, ZoneId played, IEnumerable<CardInstanceId> selected, int capacity)
        {
            RequireKind(hand, CardZoneKinds.Hand); RequireKind(played, CardZoneKinds.Played);
            return PrepareSelected(hand, played, selected, capacity, discard: false);
        }
        public CardTableChange PrepareDiscard(ZoneId hand, ZoneId discard, IEnumerable<CardInstanceId> selected)
        {
            RequireKind(hand, CardZoneKinds.Hand); RequireKind(discard, CardZoneKinds.Discard);
            return PrepareSelected(hand, discard, selected, int.MaxValue, discard: true);
        }
        private CardTableChange PrepareSelected(ZoneId source, ZoneId target, IEnumerable<CardInstanceId> selected, int capacity, bool discard)
        {
            var ids = CardZoneOperations.CopyUnique(selected, nameof(selected)); var set = new HashSet<CardInstanceId>(ids);
            var from = State.GetZone(source); var to = State.GetZone(target);
            if (!set.IsSubsetOf(from.Cards)) throw new ArgumentException("Selection contains cards outside the source.");
            var ordered = from.Cards.Where(set.Contains).ToArray();
            if (ordered.Any(id => discard ? !State.GetCard(id).CanDiscard : !State.GetCard(id).CanPlay))
                throw new InvalidOperationException("Disabled cards reject the entire command.");
            var transfer = from.TransferRangeTo(to, ordered, to.Cards.Count);
            CheckCapacity(transfer.Target, capacity); return FromTransfer(transfer);
        }
        public CardTableChange PrepareReorder(ZoneId zone, IEnumerable<CardInstanceId> order) =>
            PrepareZones(new[] { State.GetZone(zone).Reorder(order) }, Array.Empty<CardInstance>());

        /// <summary>Mode integration supplies a complete replacement for affected zones and changed card definitions.</summary>
        public CardTableChange PrepareZones(IEnumerable<CardZone> replacements, IEnumerable<CardInstance> updates)
        {
            var changed = Guard.Copy(replacements, nameof(replacements)); var updated = Guard.Copy(updates, nameof(updates));
            if (changed.Any(zone => zone == null) || updated.Any(card => card == null)) throw new ArgumentException("Missing zone or card update.");
            if (changed.Select(zone => zone.Id).Distinct().Count() != changed.Length || updated.Select(card => card.Id).Distinct().Count() != updated.Length)
                throw new ArgumentException("Duplicate update identities.");
            var allZones = State.Zones.ToDictionary(zone => zone.Id);
            foreach (var zone in changed) allZones[zone.Id] = zone;
            var allCards = State.Cards.ToDictionary(card => card.Id);
            foreach (var card in updated) allCards[card.Id] = card;
            var used = new HashSet<CardInstanceId>(allZones.Values.SelectMany(zone => zone.Cards));
            if (updated.Any(card => !used.Contains(card.Id))) throw new ArgumentException("Updated card has no zone.");
            var after = new CardTableState(State.MatchId, allCards.Values.Where(card => used.Contains(card.Id)),
                allZones.Values, checked(State.Revision + 1));
            return new CardTableChange(this, State, after, Array.Empty<CardInstanceId>());
        }
        private CardTableChange FromTransfer(CardZoneTransfer transfer)
        {
            var change = PrepareZones(new[] { transfer.Source, transfer.Target }, Array.Empty<CardInstance>());
            return new CardTableChange(this, change.Before, change.After, transfer.Cards);
        }
        private void RequireKind(ZoneId id, string kind)
        { if (State.GetZone(id).Kind != kind) throw new InvalidOperationException("Command does not match the zone kind."); }
        private static void CheckCapacity(CardZone zone, int capacity)
        { if (capacity < 0 || zone.Cards.Count > capacity) throw new InvalidOperationException("Destination capacity exceeded."); }
    }
}
