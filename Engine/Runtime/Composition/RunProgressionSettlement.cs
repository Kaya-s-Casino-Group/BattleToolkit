using System;
using System.Linq;

namespace PokerEngine
{
    /// <summary>The host resolves meta rewards. Run gold, cards and Relics are never implicitly transferred.</summary>
    public static class RunProgressionSettlement
    {
        public static RunSettlement Create<TState>(RunState<TState> state, RunContent content, IRunEncounterState<TState> encounter,
            ProgressionReward reward, bool abandoned = false) where TState : class
        {
            RunValidator.Validate(state, content, encounter);
            bool terminal = state.Phase == RunPhase.Completed || state.Phase == RunPhase.Failed;
            if (abandoned ? terminal : !terminal) throw new InvalidOperationException("Run is not eligible for this settlement outcome.");
            var outcome = abandoned ? RunOutcome.Abandoned : state.Phase == RunPhase.Completed ? RunOutcome.Won : RunOutcome.Lost;
            long score = state.Scores.Aggregate(0L, (sum, value) => checked(sum + value.Total));
            return new RunSettlement(state.Id, state.PlayerId, outcome, state.EncounterKind, state.RulesVersion, state.StartingDeckVersion,
                score, state.Floors.Count(f => f.IsCompleted), state.Floors.Count, reward);
        }
    }
}
