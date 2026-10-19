using System;
using System.Collections.Generic;
using System.Linq;
namespace PokerEngine
{
    public sealed partial class TeamBattleEngine
    {
        private static TeamBattleState PlanEnemyIntents(TeamBattleState state)
        {
            if (state.IsFinished || state.Players.All(p => !state.Combat.Get(p.Id).IsAlive) || !state.Enemies.Any(e => e.Behavior != null)) return state;
            var enemies = state.Enemies.Select(e => e.Behavior == null || !state.Combat.Get(e.Id).IsAlive ? e : e.WithBehaviorState(e.BehaviorState.Plan(state, e)));
            return new TeamBattleState(state.Rules, state.Combat, state.Players, enemies, state.Phase, state.PendingEnemies,
                state.Revision, state.CompletedRounds, state.Scores, state.Actions);
        }
        private TeamBattleState CompleteEnemyBehavior(TeamBattleState state, ParticipantId actor, List<EnemyWorldEffect> cards, BattleTimelineRecorder timeline, Action<EnemyWorldEffect, TeamBattleState> worldObserver)
        {
            var owner = state.Enemies.Single(e => e.Id == actor); var enemies = state.Enemies.ToList(); var combat = state.Combat;
            foreach (var effect in owner.BehaviorState.Intent?.WorldEffects ?? Array.Empty<EnemyWorldEffect>())
            {
                if (!combat.Get(actor).IsAlive || state.Players.All(p => !combat.Get(p.Id).IsAlive)) break;
                if (effect.EnemyRef == null)
                {
                    cards.Add(effect);
                    worldObserver?.Invoke(effect, new TeamBattleState(state.Rules, combat, state.Players, enemies, state.Phase, state.PendingEnemies,
                        state.Revision, state.CompletedRounds, state.Scores, state.Actions));
                    continue;
                }
                if (owner.SummonCatalog == null || !owner.SummonCatalog.TryGetValue(effect.EnemyRef, out var template)) throw new ArgumentException("Unknown summon template.");
                for (int i = 0; i < effect.Count; i++)
                {
                    if (enemies.Count(e => combat.Get(e.Id).IsAlive) >= effect.LivingLimit || combat.Combatants.Count >= 64) break;
                    int serial = combat.Combatants.Count;
                    ParticipantId id;
                    do { id = new ParticipantId(state.Combat.Id + "/summon/" + serial++); } while (combat.Combatants.Any(u => u.Id == id));
                    var setup = new BattleEnemySetup(id, template.Definition, template.Damage, template.DamageRules, template.TargetRules,
                        template.EffectRule, template.Behavior, owner.SummonCatalog);
                    enemies.Add(setup);
                    combat = new CombatState(combat.Id, checked(combat.Revision + 1), combat.Combatants.Concat(new[] { template.Definition.Create(id, "enemy") }), combat.Statuses);
                    var spawned = new TeamBattleState(state.Rules, combat, state.Players, enemies, state.Phase, state.PendingEnemies, state.Revision, state.CompletedRounds, state.Scores, state.Actions);
                    if (template.Behavior?.OpeningEffects != null)
                    {
                        var bound = BattleEffectBinder.Bind(spawned, id, template.Behavior.OpeningEffects, null, null, null);
                        if (bound.Error != CardDuelError.None) throw new ArgumentException("Invalid summoned opening effects.");
                        var applied = CombatEngine.Resolve(combat, combat.Revision, bound.Effects, statusCatalog: Definition.StatusCatalog);
                        if (!applied.Accepted) throw new ArgumentException("Invalid summoned status.");
                        timeline?.SetTeam(spawned); timeline?.Add(BattleTimelineKind.EnemySummoned, reason: id.Value);
                        timeline?.ObserveCombat(applied.RuleEvents); combat = applied.State;
                    }
                    else { timeline?.SetTeam(spawned); timeline?.Add(BattleTimelineKind.EnemySummoned, reason: id.Value); }
                }
            }
            enemies[enemies.FindIndex(e => e.Id == actor)] = owner.WithBehaviorState(owner.BehaviorState.Complete(state.CompletedRounds));
            // PendingEnemies is deliberately unchanged: new units act in the next enemy phase.
            return new TeamBattleState(state.Rules, combat, state.Players, enemies, state.Phase, state.PendingEnemies,
                state.Revision, state.CompletedRounds, state.Scores, state.Actions);
        }
    }
}
