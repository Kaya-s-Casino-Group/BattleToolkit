using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Binds mode-neutral lifetime declarations to this mode's stable player/enemy phase boundaries.</summary>
    public static class BattleStatusTiming
    {
        public const string PlayerPhase = "player";
        public const string EnemyPhase = "enemy";

        public static string RoundScope(TeamBattleState state, int roundsAhead = 0)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (roundsAhead < 0) throw new ArgumentOutOfRangeException(nameof(roundsAhead));
            return state.Combat.Id + "/round/" + checked(state.CompletedRounds + 1 + roundsAhead).ToString("D10", CultureInfo.InvariantCulture);
        }

        public static CombatTimingSignal ActionCompleted(TeamBattleState state, ParticipantId actor, ScoredDuelActionKind action)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (action != ScoredDuelActionKind.ScoredHand && action != ScoredDuelActionKind.Discard && action != ScoredDuelActionKind.EnemyAction)
                throw new ArgumentException("Only actual completed actions emit an action clock.", nameof(action));
            return CombatTimingSignal.ActionCompleted(state.Combat.Id + "/action/" + state.Revision.ToString("D20", CultureInfo.InvariantCulture),
                actor, action.ToString());
        }

        public static CombatTimingSignal PhaseEnding(TeamBattleState state, IEnumerable<ParticipantId> participants, bool enemyPhase)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            string phase = enemyPhase ? EnemyPhase : PlayerPhase;
            return CombatTimingSignal.PhaseEnding(RoundScope(state) + "/phase/" + phase, participants, phase);
        }

        internal static CombatTimingSignal PhaseStarted(TeamBattleState state)
        {
            bool enemy = state.Phase == ScoredDuelPhase.AwaitingEnemyAction;
            string phase = enemy ? EnemyPhase : PlayerPhase;
            return CombatTimingSignal.PhaseStarted(RoundScope(state) + "/phase/" + phase + "/start",
                enemy ? state.Enemies.Select(unit => unit.Id) : state.Players.Select(unit => unit.Id), phase);
        }

        public static StatusDuration Bind(TeamBattleState state, StatusLifetimeSpec lifetime, CombatTimingSignal boundary = null)
        {
            if (state == null || lifetime == null) throw new ArgumentNullException();
            switch (lifetime.Kind)
            {
                case StatusLifetimeKind.UntilRemoved: return StatusDuration.UntilRemoved();
                case StatusLifetimeKind.TriggerCount: return StatusDuration.ForTriggers(lifetime.Count.Value);
                case StatusLifetimeKind.ThroughCurrentRound: return StatusDuration.UntilRoundEnd(RoundScope(state));
                case StatusLifetimeKind.ThroughNextPhase:
                    if (lifetime.PhaseKey != PlayerPhase && lifetime.PhaseKey != EnemyPhase)
                        throw new ArgumentException("This battle supports player and enemy phase lifetimes only.");
                    // The upcoming complete phase: player -> enemy is this round; all other combinations start next round.
                    int ahead = state.Phase == ScoredDuelPhase.AwaitingPlayerHand && lifetime.PhaseKey == EnemyPhase ? 0 : 1;
                    // At entry the current phase is still a complete upcoming phase. Opening precedes the first player phase.
                    if (boundary?.Kind == CombatTimingKind.BattleStarted || boundary?.Kind == CombatTimingKind.PhaseStarted &&
                        lifetime.PhaseKey == boundary.PhaseKey) ahead = 0;
                    return StatusDuration.UntilPhaseEnd(RoundScope(state, ahead), lifetime.PhaseKey);
                default: throw new ArgumentException("Unknown status lifetime.");
            }
        }
    }
}
