using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Structural validity, not proof that legal commands produced the state.</summary>
    public static partial class StateValidator
    {
        public static void Validate(MatchState state, ContentCatalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            try { ValidateCore(state, catalog); }
            catch (NullReferenceException) { throw new StateValidationException("MissingData", "Match"); }
            catch (ArgumentNullException) { throw new StateValidationException("MissingData", "Match"); }
            catch (OverflowException) { throw new StateValidationException("AmountOverflow", "Match"); }
        }

        private static void Require(bool condition, string code, string path)
        { if (!condition) throw new StateValidationException(code, path); }
        private static void Unique<T>(IEnumerable<T> values, string path)
        {
            var seen = new HashSet<T>();
            foreach (var value in values) Require(seen.Add(value), "DuplicateId", path);
        }
        private static bool Defined<T>(T value) => Enum.IsDefined(typeof(T), value);

        private static void ValidateCore(MatchState m, ContentCatalog catalog)
        {
            Require(m != null, "MissingData", "Match");
            Require(m.Id.IsValid && m.Revision >= 0 && Defined(m.Status), "InvalidHeader", "Match");
            var d = m.Definition;
            Require(!string.IsNullOrWhiteSpace(d.RulesetId) && !string.IsNullOrWhiteSpace(d.RulesetVersion), "InvalidDefinition", "Definition");
            Require(d.ContentVersion == catalog.Version, "ContentVersionMismatch", "Definition.ContentVersion");
            Require(d.MaxSeats >= 2 && d.SmallBlind.Value >= 0 && d.BigBlind.Value > 0 && d.SmallBlind <= d.BigBlind, "InvalidLimits", "Definition");
            Require(Defined(d.Mode), "InvalidMode", "Definition.Mode");
            Require(m.Table.Seats.Count >= 2 && m.Table.Seats.Count <= d.MaxSeats && m.Table.Seats.All(s => s.IsValid), "InvalidSeats", "Table.Seats");
            Unique(m.Table.Seats, "Table.Seats");
            Require(m.Participants.Count >= 2 && m.Participants.Count <= m.Table.Seats.Count, "InvalidParticipants", "Participants");
            Unique(m.Participants.Select(p => p.Id), "Participants");
            Unique(m.Participants.Select(p => p.SeatId), "Participants.SeatId");
            foreach (var p in m.Participants)
            {
                Require(p.Id.IsValid && m.Table.Seats.Contains(p.SeatId) && Defined(p.Status), "InvalidParticipant", "Participants");
                Require(p.Resources.Chips.Value >= 0 && p.Resources.Energy >= 0 && !(p.Resources.Health < 0), "InvalidResource", "Participants.Resources");
            }
            Require(!string.IsNullOrWhiteSpace(m.Random.Algorithm) && m.Random.AlgorithmVersion > 0 && m.Random.StateBytes.Count > 0,
                "InvalidRandomState", "Random");
            Unique(m.Abilities.Select(a => a.Id), "Abilities");
            Unique(m.Statuses.Select(s => s.Id), "Statuses");
            if (m.CurrentHand != null) ValidateHand(m, catalog);
            Require(m.Status != MatchStatus.Ready || m.CurrentHand == null, "InvalidPhase", "CurrentHand");
            Require(m.Status != MatchStatus.Completed || m.CurrentHand == null || m.CurrentHand.Stage == HandStage.Settled, "InvalidPhase", "CurrentHand");
            bool terminal = m.Status == MatchStatus.Completed || m.Status == MatchStatus.Aborted;
            Require(!terminal || (m.ActiveDecision == null && m.Resolution.PendingEffects.Count == 0), "TerminalWork", "Match");

            AbilityWorldValidator.Validate(HoldemAbilityAdapter.ToWorld(m), catalog);
            ValidateAbilityWindow(m);
            ValidateHoldemPhases(m);
            if (m.Resolution.SuspendedDecision != null)
                Require(m.CurrentHand?.Betting?.CurrentActor == m.Resolution.SuspendedDecision.ActorId,
                    "InvalidSuspendedDecision", "Resolution.SuspendedDecision");
            if (m.ActiveDecision != null) ValidateDecision(m);
        }

        private static void ValidateHand(MatchState m, ContentCatalog catalog)
        {
            var h = m.CurrentHand;
            Require(h.Id.IsValid && Defined(h.Stage) && m.Table.Seats.Contains(h.DealerSeatId), "InvalidHand", "Hand");
            Require(h.Participants.Count >= 2, "InvalidParticipants", "Hand.Participants");
            Unique(h.Participants.Select(p => p.ParticipantId), "Hand.Participants");
            foreach (var p in h.Participants)
            {
                Require(m.Participants.Any(x => x.Id == p.ParticipantId) && Defined(p.Participation), "UnknownParticipant", "Hand.Participants");
                Require(p.HandCommitted.Value >= 0 && p.StreetCommitted.Value >= 0 && p.StreetCommitted <= p.HandCommitted,
                    "InvalidCommitment", "Hand.Participants");
            }
            Require(h.Participants.Any(p => m.Participants.Any(x => x.Id == p.ParticipantId && x.SeatId == h.DealerSeatId)), "InvalidDealer", "Hand");
            Require(h.SmallBlindId != h.BigBlindId && h.Participants.Any(p => p.ParticipantId == h.SmallBlindId) &&
                h.Participants.Any(p => p.ParticipantId == h.BigBlindId), "InvalidBlinds", "Hand");
            Unique(h.Cards.Select(c => c.Id), "Hand.Cards");
            foreach (var c in h.Cards) Require(c.Id.IsValid && catalog.Contains(c.DefinitionId) && CardEffectRules.IsValid(c.Effects) &&
                (c.OriginId == null || !string.IsNullOrWhiteSpace(c.OriginId)), "UnknownCardDefinition", "Hand.Cards");
            Unique(h.Cards.Where(c => c.OriginId != null).Select(c => c.OriginId), "Hand.Cards.OriginId");
            Unique(h.Zones.Select(z => z.Id), "Hand.Zones");
            Require(h.Zones.Count(z => z.Kind == CardZoneKinds.Deck) == 1 && h.Zones.Count(z => z.Kind == HoldemZoneKinds.Board) == 1, "InvalidZones", "Hand.Zones");
            Unique(h.Zones.Where(z => z.Kind != HoldemZoneKinds.HoleCards).Select(z => z.Kind), "Hand.Zones.Kind");
            var locations = new HashSet<CardInstanceId>();
            foreach (var z in h.Zones)
            {
                Require(z.Id.IsValid && new[] { CardZoneKinds.Deck, CardZoneKinds.Discard, CardZoneKinds.Removed, HoldemZoneKinds.Board, HoldemZoneKinds.HoleCards, HoldemZoneKinds.Burn }.Contains(z.Kind) && (z.Kind == HoldemZoneKinds.HoleCards) == z.ControllerId.HasValue, "InvalidZone", "Hand.Zones");
                if (z.ControllerId.HasValue) Require(h.Participants.Any(p => p.ParticipantId == z.ControllerId.Value), "UnknownParticipant", "Hand.Zones.ControllerId");
                foreach (var id in z.Cards)
                {
                    Require(h.Cards.Any(c => c.Id == id), "UnknownCard", "Hand.Zones.Cards");
                    Require(locations.Add(id), "DuplicateCardLocation", "Hand.Zones.Cards");
                }
            }
            Require(locations.Count == h.Cards.Count, "UnlocatedCard", "Hand.Cards");
            foreach (var p in h.Participants)
                Require(h.Zones.Count(z => z.Kind == HoldemZoneKinds.HoleCards && z.ControllerId == p.ParticipantId) == 1, "InvalidHoleZone", "Hand.Zones");
            Require((h.Stage == HandStage.Betting) == (h.Betting != null), "InvalidPhase", "Hand.Betting");
            Require((h.Stage == HandStage.Settled) == (h.Settlement != null), "InvalidPhase", "Hand.Settlement");
            if (h.Betting != null) ValidateBetting(m);
            // Checked summation also rejects a set of individually valid amounts that cannot form a pot.
            long total = 0;
            foreach (var p in h.Participants) total = checked(total + p.HandCommitted.Value);
            if (h.Settlement != null) ValidateSettlement(m);
        }

        private static void ValidateBetting(MatchState m)
        {
            var h = m.CurrentHand; var b = h.Betting;
            Require(Defined(b.Street) && b.CurrentBetTo.Value >= 0 && b.LastFullRaiseSize.Value > 0, "InvalidBetting", "Hand.Betting");
            Unique(b.PendingActors, "Hand.Betting.PendingActors"); Unique(b.Players.Select(p => p.ParticipantId), "Hand.Betting.Players");
            Require(b.Players.Count == h.Participants.Count && b.Players.All(p => h.Participants.Any(x => x.ParticipantId == p.ParticipantId)),
                "InvalidBettingPlayers", "Hand.Betting.Players");
            foreach (var p in h.Participants) Require(p.StreetCommitted <= b.CurrentBetTo, "InvalidBetTo", "Hand.Betting");
            foreach (var p in b.Players)
            {
                Require(p.BetToAtLastAction.Value >= 0 && p.BetToAtLastAction <= b.CurrentBetTo &&
                    (!p.ReopenAtBetTo.HasValue || p.ReopenAtBetTo.Value > p.BetToAtLastAction), "InvalidRaiseRights", "Hand.Betting.Players");
                Require(p.HasActed || (p.BetToAtLastAction == Chips.Zero && !p.ReopenAtBetTo.HasValue), "InvalidRaiseRights", "Hand.Betting.Players");
            }
            foreach (var actor in b.PendingActors)
                Require(h.Participants.Any(p => p.ParticipantId == actor && p.Participation == Participation.InHand) &&
                    m.Participants.Any(p => p.Id == actor && p.Resources.Chips.Value > 0 && p.Status == ParticipantStatus.Active), "InvalidPendingActor", "Hand.Betting.PendingActors");
        }

        private static void ValidateSettlement(MatchState m)
        {
            var h = m.CurrentHand; var s = h.Settlement; var breakdown = PotCalculator.Calculate(h.Participants);
            Require(!string.IsNullOrWhiteSpace(s.SettlementId), "InvalidSettlement", "Hand.Settlement");
            Unique(s.Refunds.Select(r => r.ParticipantId), "Hand.Settlement.Refunds");
            Require(s.Refunds.Count == breakdown.Refunds.Count && s.Refunds.All(r => r.Amount.Value > 0 &&
                breakdown.Refunds.Any(x => x.ParticipantId == r.ParticipantId && x.Amount == r.Amount)), "InvalidRefund", "Hand.Settlement.Refunds");
            foreach (var a in s.Awards)
                Require(a.PotIndex >= 0 && a.PotIndex < breakdown.Pots.Count && a.Amount.Value > 0 &&
                    breakdown.Pots[a.PotIndex].EligibleWinners.Contains(a.ParticipantId), "InvalidAward", "Hand.Settlement.Awards");
            Unique(s.Awards.Select(a => Tuple.Create(a.PotIndex, a.ParticipantId)), "Hand.Settlement.Awards");
            for (int i = 0; i < breakdown.Pots.Count; i++)
            {
                long sum = 0;
                foreach (var a in s.Awards.Where(x => x.PotIndex == i)) sum = checked(sum + a.Amount.Value);
                Require(sum == breakdown.Pots[i].Amount.Value, "UnbalancedSettlement", "Hand.Settlement.Awards");
            }
            Unique(s.Evaluations.Select(e => e.ParticipantId), "Hand.Settlement.Evaluations");
            foreach (var e in s.Evaluations)
            {
                Require(h.Participants.Any(p => p.ParticipantId == e.ParticipantId && p.Participation == Participation.InHand) && Defined(e.Category),
                    "InvalidEvaluation", "Hand.Settlement.Evaluations");
                Require(e.SelectedCards.Count == 5 && e.TieBreakRanks.Count > 0 && e.TieBreakRanks.Count <= 5 &&
                    e.TieBreakRanks.All(r => r >= 2 && r <= 14), "InvalidEvaluation", "Hand.Settlement.Evaluations");
                Unique(e.SelectedCards, "Hand.Settlement.Evaluations.Cards");
                Require(e.Faces.Count == 0 || e.Faces.Count == e.SelectedCards.Count, "InvalidEvaluationFaces", "Hand.Settlement.Evaluations.Faces");
                Unique(e.Faces.Select(f => f.CardId), "Hand.Settlement.Evaluations.Faces");
                Require(e.Faces.All(f => e.SelectedCards.Contains(f.CardId) && Defined(f.Face.Rank) && Defined(f.Face.Suit)),
                    "InvalidEvaluationFaces", "Hand.Settlement.Evaluations.Faces");
                foreach (var id in e.SelectedCards)
                    Require(h.Zones.Any(z => (z.Kind == HoldemZoneKinds.Board || (z.Kind == HoldemZoneKinds.HoleCards && z.ControllerId == e.ParticipantId)) && z.Cards.Contains(id)),
                        "InvalidEvaluationCard", "Hand.Settlement.Evaluations.Cards");
                Unique(e.AppliedModifiers, "Hand.Settlement.Evaluations.Modifiers");
                Require(e.AppliedModifiers.All(id => m.Statuses.Any(sx => sx.Id == id)), "UnknownStatus", "Hand.Settlement.Evaluations.Modifiers");
            }
        }

        private static void ValidateDecision(MatchState m)
        {
            var d = m.ActiveDecision;
            Require(m.Status == MatchStatus.Running && (m.CurrentHand != null || m.AbilityWindow?.Timing == HoldemTiming.BeforeHand), "InvalidPhase", "Decision");
            Require(d.Id.IsValid && Defined(d.Kind) && m.Participants.Any(p => p.Id == d.ActorId && p.Status == ParticipantStatus.Active),
                "InvalidDecision", "Decision");
            if (d.Kind == DecisionKind.SelectTargets)
            {
                Require(d.CardCandidates.Count == 0 && d.OptionIds.Count == 0 && d.MinSelections == 0 && d.MaxSelections == 0,
                    "MixedSelectionFormat", "Decision");
                Continuation(m, d.Continuation, "Decision.Continuation");
                AbilityWorldValidator.ValidateSelectionSpec(d.Selection);
                foreach (var candidate in d.Selection.Candidates)
                    if (candidate.Entity != null) Reference(m, candidate.Entity, "Decision.Selection.Candidates");
                return;
            }
            Require(d.Selection == null, "MixedSelectionFormat", "Decision.Selection");
            Require(m.CurrentHand != null && m.CurrentHand.Stage != HandStage.Settled &&
                m.CurrentHand.Participants.Any(p => p.ParticipantId == d.ActorId && p.Participation == Participation.InHand), "InvalidDecision", "Decision");
            Require(d.MinSelections >= 0 && d.MaxSelections >= d.MinSelections, "InvalidSelectionCount", "Decision");
            Unique(d.CardCandidates, "Decision.CardCandidates"); Unique(d.OptionIds, "Decision.OptionIds");
            if (d.Kind == DecisionKind.ModeAction)
                Require(m.CurrentHand.Betting != null && m.CurrentHand.Betting.CurrentActor == d.ActorId && d.Continuation == null &&
                    d.CardCandidates.Count == 0 && d.OptionIds.Count == 0 && d.MinSelections == 0 && d.MaxSelections == 0, "InvalidBettingDecision", "Decision");
            else
            {
                Continuation(m, d.Continuation, "Decision.Continuation");
                if (d.Kind == DecisionKind.SelectCards)
                    Require(d.OptionIds.Count == 0 && d.MaxSelections <= d.CardCandidates.Count &&
                        d.CardCandidates.All(id => m.CurrentHand.Cards.Any(c => c.Id == id)), "InvalidCardSelection", "Decision");
                else Require(d.CardCandidates.Count == 0 && d.OptionIds.Count > 0 && d.OptionIds.All(id => !string.IsNullOrWhiteSpace(id)) &&
                    d.MinSelections == 1 && d.MaxSelections == 1, "InvalidOptionSelection", "Decision");
            }
        }

        private static void Continuation(MatchState m, EffectContinuation c, string path)
        {
            Require(c != null && c.NextEffectIndex >= 0 && m.Abilities.Any(a => a.Id == c.AbilityId), "InvalidContinuation", path);
            foreach (var target in c.Targets) Reference(m, target, path + ".Targets");
        }

        private static void Reference(MatchState m, EntityRef reference, string path)
        {
            Require(reference != null && !string.IsNullOrWhiteSpace(reference.Id), "InvalidReference", path);
            bool exists = false;
            switch (reference.Kind)
            {
                case EntityKind.Match: exists = m.Id.Value == reference.Id; break;
                case EntityKind.Participant: exists = m.Participants.Any(p => p.Id.Value == reference.Id); break;
                case EntityKind.Card: exists = m.CurrentHand != null && m.CurrentHand.Cards.Any(c => c.Id.Value == reference.Id); break;
                case EntityKind.Ability: exists = m.Abilities.Any(a => a.Id.Value == reference.Id); break;
            }
            Require(exists, "UnknownReference", path);
        }
    }
}
