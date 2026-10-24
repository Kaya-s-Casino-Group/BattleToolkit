using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public sealed partial class TeamBattleEngine
    {
        private static TeamBattleState BeginWrathOpeningWarning(TeamBattleState state)
        {
            if (state.CompletedRounds != 0) return state;
            var enemies = state.Enemies.Select(body => {
                var definition = body.Behavior?.Wrath;
                if (definition == null || definition.InitialWarningRound != 1 || !state.Combat.Get(body.Id).IsAlive) return body;
                var runtime = WrathBossRules.Runtime(body);
                if (runtime.ReleaseRound.HasValue || runtime.ReadyRound > 1 ||
                    WrathBossRules.Hands(state.Enemies, definition).Count(hand => state.Combat.Get(hand.Id).IsAlive) != 5) return body;
                // Only the opening warning moves to phase entry; later warnings still follow the body's ordinary action.
                return body.WithBehaviorState(body.BehaviorState.WithWrath(runtime.With(releaseRound: 2)));
            }).ToArray();
            return new TeamBattleState(state.Rules, state.Combat, state.Players, enemies, state.Phase,
                state.PendingEnemies, state.Revision, state.CompletedRounds, state.Scores, state.Actions);
        }
        private TeamBattleState RegenerateWrathHands(TeamBattleState state, List<CombatRuleEvent> facts, BattleTimelineRecorder timeline)
        {
            var draft = state;
            foreach (var body in state.Enemies.Where(e => e.Behavior?.Wrath != null))
            {
                if (!draft.Combat.Get(body.Id).IsAlive) continue;
                var runtime = WrathBossRules.Runtime(draft.Enemies.Single(e => e.Id == body.Id));
                var effects = runtime.ReviveRounds.Where(p => p.Value <= state.CompletedRounds + 1 && !draft.Combat.Get(p.Key).IsAlive)
                    .Select(p => new CombatEffect(CombatEffectKind.Revive, body.Id, p.Key, 1)).ToArray();
                if (effects.Length == 0) continue;
                var result = CombatEngine.Resolve(draft.Combat, draft.Combat.Revision, effects, statusCatalog: Definition.StatusCatalog);
                if (!result.Accepted) throw new ArgumentException("Wrath regeneration failed: " + result.Error);
                timeline?.SetTeam(draft); timeline?.ObserveCombat(result.RuleEvents); facts.AddRange(result.RuleEvents);
                draft = WindowState(draft, result.State, draft.Players, draft.Phase, draft.CompletedRounds, draft.PendingEnemies);
                // Revive removes statuses and preserves identity; commit a fresh ordinary intent for this same stage.
                draft = new TeamBattleState(draft.Rules, draft.Combat, draft.Players,
                    draft.Enemies.Select(e => effects.Any(effect => effect.Target == e.Id) ? e.WithBehaviorState(e.BehaviorState.Plan(draft, e)) : e),
                    draft.Phase, draft.PendingEnemies, draft.Revision, draft.CompletedRounds, draft.Scores, draft.Actions);
            }
            return draft;
        }
        private static ParticipantId[] EnemyPhaseQueue(TeamBattleState state)
        {
            var living = state.Enemies.Where(e => state.Combat.Get(e.Id).IsAlive);
            if (!state.Enemies.Any(e => e.Behavior?.Wrath != null)) return living.Select(e => e.Id).ToArray();
            // Hands take their own normal turn-start/status/control boundary before the body's turn.
            return living.OrderBy(e => e.Behavior?.Wrath != null ? 1 : 0).Select(e => e.Id).ToArray();
        }
        private static TeamBattleState CloseWrathEnemyPhase(TeamBattleState state)
        {
            int round = checked(state.CompletedRounds + 1);
            var enemies = state.Enemies.Select(body => {
                if (body.Behavior?.Wrath == null) return body;
                var runtime = WrathBossRules.Runtime(body);
                // A whole-phase interruption must never carry an overdue release into a later round.
                return runtime.ReleaseRound.HasValue && runtime.ReleaseRound.Value <= round
                    ? body.WithBehaviorState(body.BehaviorState.WithWrath(runtime.With(readyRound: checked(round + body.Behavior.Wrath.CooldownRounds), clearRelease: true)))
                    : body;
            }).ToArray();
            return new TeamBattleState(state.Rules, state.Combat, state.Players, enemies, state.Phase,
                state.PendingEnemies, state.Revision, state.CompletedRounds, state.Scores, state.Actions);
        }
        private TeamBattleState AdvanceWrathBody(TeamBattleState state, ParticipantId actor, bool acted,
            string ordinaryActionId, out BattleEffectSequence ultimate)
        {
            ultimate = null;
            var body = state.Enemies.Single(e => e.Id == actor); var definition = body.Behavior?.Wrath;
            if (definition == null || IsFinished(state.Combat, state.Players, state.Enemies)) return state;
            var runtime = WrathBossRules.Runtime(body); int round = checked(state.CompletedRounds + 1);
            if (acted && ordinaryActionId == definition.ChargeActionId) runtime = runtime.With(charges: checked(runtime.Charges + 1));
            bool fullHands = WrathBossRules.Hands(state.Enemies, definition).Count(h => state.Combat.Get(h.Id).IsAlive) == 5;
            if (runtime.ReleaseRound.HasValue && runtime.ReleaseRound.Value <= round)
            {
                // The ordinary action and all of its reactions have settled before the extra main attack.
                bool release = acted && fullHands;
                runtime = runtime.With(readyRound: checked(round + definition.CooldownRounds), clearRelease: true);
                if (release) ultimate = new BattleEffectSequence("wrath-ultimate", "暴怒·五手大招", new[] {
                    new BattleEffectStep("wrath-ultimate", BattleEffectSpec.DamageFromEnemyAttack(definition.UltimateScale), EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving)) });
            }
            else if (!runtime.ReleaseRound.HasValue && acted && fullHands && round >= runtime.ReadyRound)
                runtime = runtime.With(releaseRound: checked(round + 1));
            return new TeamBattleState(state.Rules, state.Combat, state.Players,
                state.Enemies.Select(e => e.Id == actor ? e.WithBehaviorState(e.BehaviorState.WithWrath(runtime)) : e),
                state.Phase, state.PendingEnemies, state.Revision, state.CompletedRounds, state.Scores, state.Actions);
        }
    }
}
