using System;
using System.Linq;
namespace PokerEngine
{
    public static partial class StateValidator
    {
        private static void ValidateAbilityWindow(MatchState m)
        {
            var w = m.AbilityWindow;
            if (w == null) return;
            Require(m.Status == MatchStatus.Running && !string.IsNullOrWhiteSpace(w.Id) && new[] { HoldemTiming.BeforeHand, HoldemTiming.BeforeBettingAction, HoldemTiming.AfterBettingAction, HoldemTiming.AfterBoardDealt, HoldemTiming.BeforeShowdown, HoldemTiming.AfterHandSettled }.Contains(w.Timing), "InvalidAbilityWindow", "AbilityWindow");
            Require((w.Timing != HoldemTiming.BeforeHand) == (w.ScopeId != null) &&
                (w.Timing == HoldemTiming.AfterBettingAction) == (w.Action != null), "InvalidAbilityWindow", "AbilityWindow");
            if (w.EventActorId.HasValue) Require(m.Participants.Any(p => p.Id == w.EventActorId.Value), "UnknownParticipant", "AbilityWindow.EventActorId");
            if ((w.Action != null)) Require(new[] { HoldemActionKeys.Fold, HoldemActionKeys.Check, HoldemActionKeys.Call, HoldemActionKeys.RaiseTo }.Contains(w.Action) && w.EventActorId.HasValue, "InvalidAbilityWindow", "AbilityWindow.Action");
            if ((w.ScopeId != null)) Require(m.CurrentHand != null && w.ScopeId == m.CurrentHand.Id.Value, "UnknownHand", "AbilityWindow.HandId");
            var h = m.CurrentHand;
            switch (w.Timing)
            {
                case HoldemTiming.BeforeHand:
                    Require(h == null || h.Stage == HandStage.Settled, "InvalidAbilityWindowPhase", "AbilityWindow"); break;
                case HoldemTiming.BeforeBettingAction:
                    Require(h.Stage == HandStage.Betting && h.Betting.CurrentActor.HasValue &&
                        (!w.EventActorId.HasValue || w.EventActorId == h.Betting.CurrentActor), "InvalidAbilityWindowPhase", "AbilityWindow"); break;
                case HoldemTiming.AfterBettingAction:
                    Require(h.Stage == HandStage.Betting && h.Participants.Any(p => p.ParticipantId == w.EventActorId.Value), "InvalidAbilityWindowPhase", "AbilityWindow"); break;
                case HoldemTiming.AfterBoardDealt:
                    Require(h.Stage == HandStage.Betting && h.Betting.Street != Street.Preflop, "InvalidAbilityWindowPhase", "AbilityWindow"); break;
                case HoldemTiming.BeforeShowdown:
                    Require(h.Stage == HandStage.Showdown, "InvalidAbilityWindowPhase", "AbilityWindow"); break;
                case HoldemTiming.AfterHandSettled:
                    Require(h.Stage == HandStage.Settled, "InvalidAbilityWindowPhase", "AbilityWindow"); break;
            }
        }

        private static void ValidateHoldemPhases(MatchState state)
        {
            var phases = Enum.GetValues(typeof(Street)).Cast<Street>().Select(HoldemAbilityAdapter.Phase).ToArray();
            foreach (var status in state.Statuses)
                if (status.Duration.PhaseKey != null) Require(phases.Contains(status.Duration.PhaseKey), "InvalidDuration", "Statuses.Duration.PhaseKey");
            foreach (var grant in state.Knowledge)
                if (grant.UntilPhaseEnd != null)
                    Require(phases.Contains(grant.UntilPhaseEnd) && (state.CurrentHand?.Betting == null ||
                        Array.IndexOf(phases, grant.UntilPhaseEnd) <= (int)state.CurrentHand.Betting.Street - 1), "InvalidKnowledgeExpiry", "Knowledge.UntilPhaseEnd");
        }
    }
}
