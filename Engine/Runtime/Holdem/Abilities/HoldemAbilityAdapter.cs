using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public static class AbilityHoldem
    {
        public const string RulesetId = "holdem.abilities";
        public const string RulesetVersion = "2";
        internal static int RunoutCards(Street street) => street == Street.Preflop ? 8 : street == Street.Flop ? 4 : street == Street.Turn ? 2 : 0;
        public static MatchDefinition Definition(string contentVersion, int maxSeats = 6, long smallBlind = 10, long bigBlind = 20, MatchMode mode = MatchMode.Solo) =>
            new MatchDefinition(RulesetId, RulesetVersion, contentVersion, maxSeats, new Chips(smallBlind), new Chips(bigBlind), mode);
    }

    /// <summary>Only this module knows how betting state maps to the shared ability world.</summary>
    public static class HoldemAbilityAdapter
    {
        public static string Phase(Street street) => "holdem." + street.ToString().ToLowerInvariant();
        public static AbilityWorld ToWorld(MatchState state)
        {
            var hand = state.CurrentHand;
            int dealer = hand == null ? 0 : state.Table.Seats.ToList().IndexOf(hand.DealerSeatId);
            var players = state.Participants.OrderBy(p => (state.Table.Seats.ToList().IndexOf(p.SeatId) - dealer + state.Table.Seats.Count) % state.Table.Seats.Count)
                .Select(p => new AbilityParticipant(p.Id, p.Status == ParticipantStatus.Active, p.Resources));
            return new AbilityWorld(state.Id.Value, state.Revision, state.Status == MatchStatus.Running, state.Definition.Mode,
                hand?.Id.Value, hand?.Betting == null ? null : Phase(hand.Betting.Street), hand?.Betting?.CurrentActor,
                players, hand?.Cards ?? new CardInstance[0], hand?.Zones ?? new CardZone[0], state.Abilities, state.Statuses,
                state.ActiveDecision, state.Resolution, state.AbilityWindow, state.Knowledge,
                allowChipChanges: hand == null || hand.Stage == HandStage.Settled,
                allowCardChanges: hand != null && hand.Stage != HandStage.Settled,
                allowKnowledgeProjection: hand != null && hand.Stage != HandStage.Settled);
        }
        public static MatchState WithRevision(MatchState state, long revision) => new MatchState(state.Id, revision, state.Definition,
            state.Status, state.Table, state.Participants, state.Abilities, state.Statuses, state.CurrentHand, state.ActiveDecision,
            state.Resolution, state.Random, state.AbilityWindow, state.Knowledge);
        public static MatchState ApplyWorld(MatchState source, AbilityWorld world)
        {
            var before = ToWorld(source);
            if (world.Id != before.Id || world.Revision != before.Revision || world.IsRunning != before.IsRunning || world.Mode != before.Mode ||
                world.ScopeId != before.ScopeId || world.PhaseKey != before.PhaseKey || world.CurrentActorId != before.CurrentActorId ||
                !world.Participants.Select(p => p.Id).SequenceEqual(before.Participants.Select(p => p.Id)) ||
                !world.Cards.Select(c => c.Id).SequenceEqual(before.Cards.Select(c => c.Id))) throw new ArgumentException("Ability changed mode-owned state.");
            var players = source.Participants.Select(p => {
                var projected = world.Participants.Single(x => x.Id == p.Id);
                if (projected.Active != (p.Status == ParticipantStatus.Active) || !before.AllowChipChanges && projected.Resources.Chips != p.Resources.Chips ||
                    source.AbilityWindow?.Timing == HoldemTiming.BeforeHand && p.Status == ParticipantStatus.Active && projected.Resources.Chips == Chips.Zero)
                    throw new ArgumentException("Ability violated betting resource rules.");
                return new ParticipantState(p.Id, p.SeatId, p.Status, projected.Resources);
            });
            var hand = source.CurrentHand;
            if (hand != null) hand = new HandState(hand.Id, hand.Stage, hand.DealerSeatId, hand.SmallBlindId, hand.BigBlindId,
                hand.Participants, hand.Cards, world.Zones, hand.Betting, hand.Settlement);
            return new MatchState(source.Id, source.Revision, source.Definition, source.Status, source.Table, players, world.Abilities,
                world.Statuses, hand, world.ActiveDecision, world.Resolution, source.Random, source.AbilityWindow, world.Knowledge);
        }
        internal static MatchState ApplyResult(MatchState source, AbilityExecutionResult result, List<MatchEvent> events)
        {
            var state = ApplyWorld(source, result.World);
            foreach (var e in result.Events)
            {
                var kind = e.Kind == AbilityEventKind.Activated ? MatchEventKind.AbilityActivated :
                    e.Kind == AbilityEventKind.SelectionOpened ? MatchEventKind.SelectionOpened : MatchEventKind.SelectionResolved;
                events.Add(new MatchEvent(state.Revision, events.Count, kind, e.OwnerId, Chips.Zero, null, null, e.AbilityId.Value, null, e.OwnerId));
            }
            return state;
        }
    }
}
