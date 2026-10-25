using System.Linq;

namespace PokerEngine
{
    /// <summary>Selects the next internal step without executing or committing it. Ordering is part
    /// of the battle contract: collection precedes enemy actions and automatic player completion.</summary>
    internal static class BattleAutoAdvancePolicy
    {
        internal static bool TryNext(CardDuelState state, out ParticipantId actor, out CardDuelAction action)
        {
            actor = default(ParticipantId); action = default(CardDuelAction);
            if (state.Duel.IsFinished) return false;
            var uncollected = state.Duel.Players.FirstOrDefault(p => p.Rules.Flow.UsesTurnFlow && state.Zone(CardZoneKinds.Played, p.Id).Cards.Count > 0);
            if (uncollected != null)
            {
                actor = uncollected.Id; action = CardDuelAction.CollectPlayed;
                return true;
            }
            if (state.Duel.Phase == ScoredDuelPhase.AwaitingEnemyAction)
            {
                actor = state.Duel.NextEnemy; action = CardDuelAction.EnemyAction;
                return true;
            }
            if (state.Duel.Phase != ScoredDuelPhase.AwaitingPlayerHand) return false;
            var pending = state.Duel.Players.FirstOrDefault(p => state.Duel.HasPlayerWindow(p.Id) && p.Rules.Flow.UsesTurnFlow &&
                (state.FlowFor(p.Id).Window == BattlePlayerWindow.Ending && (state.FlowFor(p.Id).FinishAfterOverflow || !p.Rules.Flow.EndDefense ||
                    state.Zone(CardZoneKinds.Hand, p.Id).Cards.Count == 0 || state.Duel.IsActionBlocked(p.Id)) ||
                state.FlowFor(p.Id).Window == BattlePlayerWindow.Action && (state.Duel.IsActionBlocked(p.Id) ||
                    p.Rules.Flow.EnergyOnly && !BattleActionQuery.HasAffordableCardAction(state, p.Id))));
            if (pending != null)
            {
                actor = pending.Id;
                action = state.FlowFor(pending.Id).Window == BattlePlayerWindow.Ending ? CardDuelAction.FinishTurn : CardDuelAction.EndTurn;
                return true;
            }
            if (state.Duel.NextBlockedPlayer.IsValid && !state.Duel.Player(state.Duel.NextBlockedPlayer).Rules.Flow.UsesTurnFlow)
            {
                actor = state.Duel.NextBlockedPlayer; action = CardDuelAction.SkipPlayerTurn;
                return true;
            }
            return false;
        }

        // Keep this post-loop checkpoint separate: legacy collection must not restart automatic
        // advancement, and a terminal player action intentionally leaves its cards in Played.
        internal static bool ShouldCollectAfterCommand(CardDuelState state, ParticipantId actor) =>
            state.Duel.Phase == ScoredDuelPhase.AwaitingPlayerHand && state.Zone(CardZoneKinds.Played, actor).Cards.Count > 0;
    }
}
