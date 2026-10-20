using System;
using System.Collections.Generic;
using System.Linq;
namespace PokerEngine
{
    public sealed partial class TeamBattleEngine
    {
        /// <summary>Pure intent schedule under the current battlefield conditions. Health thresholds refresh it; previews never spend uses or random state.</summary>
        public IReadOnlyList<EnemyBehaviorAction> PreviewEnemyIntents(TeamBattleState state, ParticipantId actor)
        {
            if (state == null || !ReferenceEquals(state.Rules, Definition)) throw new ArgumentException("Invalid battle.");
            var owner = state.Enemies.Single(e => e.Id == actor);
            var result = new List<EnemyBehaviorAction>();
            if (owner.Behavior == null || state.IsFinished || !state.Combat.Get(actor).IsAlive) return result.AsReadOnly();
            var runtime = owner.BehaviorState;
            for (int offset = 0; offset < owner.Behavior.IntentPreviewTurns; offset++)
            {
                var future = new TeamBattleState(state.Rules, state.Combat, state.Players, state.Enemies, state.Phase, state.PendingEnemies, state.Revision, state.CompletedRounds + offset, state.Scores, state.Actions);
                if (offset > 0) runtime = runtime.Plan(future, owner);
                result.Add(runtime.Intent);
                bool blocked = runtime.Intent?.WorldEffects.Any(effect => effect.EnemyRef != null && state.Enemies.Count(e => state.Combat.Get(e.Id).IsAlive && (effect.LimitScope == SummonLimitScope.Battlefield || e.Id == actor || e.SummonerId == actor)) >= effect.LivingLimit) == true;
                if (!blocked || owner.Behavior.ConsumeFailedSummon) runtime = runtime.Complete(state.CompletedRounds + offset);
            }
            return result.AsReadOnly();
        }
        private static CombatTransition ReviveUnits(CombatState combat, ParticipantId actor, EnemyWorldEffect effect, BattleTimelineRecorder timeline)
        {
            var targets = effect.ReviveRefs.SelectMany(reference => combat.Combatants.Where(u => u.DefinitionId == reference && !u.IsAlive && u.Id != actor && u.TeamId == combat.Get(actor).TeamId)).Distinct().Take(effect.Count).ToArray();
            if (targets.Length == 0) return null;
            var result = CombatEngine.Resolve(combat, combat.Revision, targets.Select(u => new CombatEffect(CombatEffectKind.Revive, actor, u.Id, u.MaxHealth)));
            if (!result.Accepted) throw new ArgumentException("Invalid revival.");
            timeline?.ObserveCombat(result.RuleEvents); return result;
        }
        private TeamBattleState EnterEnemyStages(TeamBattleState state, List<CombatRuleEvent> facts, BattleTimelineRecorder timeline, IReadOnlyCollection<string> immediateEntries = null)
        {
            if (state.IsFinished || IsFinished(state.Combat, state.Players, state.Enemies)) return state;
            var combat = state.Combat; var enemies = state.Enemies.ToArray();
            for (int i = 0; i < enemies.Length; i++)
            {
                var enemy = enemies[i];
                if (enemy.Behavior == null || !combat.Get(enemy.Id).IsAlive) continue;
                var stage = enemy.Behavior.Stages.FirstOrDefault(s => s.Condition.Matches(combat, state.CompletedRounds, enemy.Id));
                if (stage == null || stage.EntryEffects.Count == 0 || enemy.BehaviorState.Uses.ContainsKey("@entry/" + stage.Id)) continue;
                if(immediateEntries?.Contains(enemy.Id.Value+"/"+stage.Id)!=true) foreach (var effect in stage.EntryEffects) { var revived = ReviveUnits(combat, enemy.Id, effect, timeline); if (revived != null) { combat = revived.State; facts.AddRange(revived.RuleEvents); } }
                enemies[i] = enemy.WithBehaviorState(enemy.BehaviorState.MarkEntry(stage.Id));
            }
            return new TeamBattleState(state.Rules, combat, state.Players, enemies, state.Phase, state.PendingEnemies, state.Revision, state.CompletedRounds, state.Scores, state.Actions);
        }
        private static TeamBattleState RefreshEnemyThresholds(TeamBattleState before, TeamBattleState state)
        {
            if (state.IsFinished || state.Phase != ScoredDuelPhase.AwaitingPlayerHand) return state;
            var enemies = state.Enemies.Select(e => {
                if (e.Behavior == null || !(e.Behavior.RefreshOnHealthThreshold || e.Behavior.RecheckIntentOnAction) || !state.Combat.Get(e.Id).IsAlive) return e;
                var conditions = e.Behavior.Stages.SelectMany(s => new[] { s.Condition }.Concat(s.Actions.Select(a => a.Condition)));
                bool changed = e.Behavior.RecheckIntentOnAction || conditions.Any(c => !c.HealthThresholds(before, e.Id).SequenceEqual(c.HealthThresholds(state, e.Id)));
                return changed ? e.WithBehaviorState(e.BehaviorState.Plan(state, e)) : e;
            }).ToArray();
            return new TeamBattleState(state.Rules, state.Combat, state.Players, enemies, state.Phase, state.PendingEnemies, state.Revision, state.CompletedRounds, state.Scores, state.Actions);
        }
        private static TeamBattleState PlanEnemyIntents(TeamBattleState state)
        {
            if (state.IsFinished || state.Players.All(p => !state.Combat.Get(p.Id).IsAlive) || !state.Enemies.Any(e => e.Behavior != null)) return state;
            var enemies = state.Enemies.Select(e => e.Behavior == null || !state.Combat.Get(e.Id).IsAlive ? e : e.WithBehaviorState(e.BehaviorState.Plan(state, e)));
            return new TeamBattleState(state.Rules, state.Combat, state.Players, enemies, state.Phase, state.PendingEnemies,
                state.Revision, state.CompletedRounds, state.Scores, state.Actions);
        }
        private TeamBattleState CompleteEnemyBehavior(TeamBattleState state, ParticipantId actor, List<EnemyWorldEffect> cards, BattleTimelineRecorder timeline, Action<EnemyWorldEffect, TeamBattleState> worldObserver, List<CombatRuleEvent> worldFacts)
        {
            bool failedSummon = false;
            var owner = state.Enemies.Single(e => e.Id == actor); var enemies = state.Enemies.ToList(); var combat = state.Combat;
            foreach (var effect in owner.BehaviorState.Intent?.WorldEffects ?? Array.Empty<EnemyWorldEffect>())
            {
                if (!combat.Get(actor).IsAlive || state.Players.All(p => !combat.Get(p.Id).IsAlive)) break;
                if (effect.IsRevive)
                {
                    var revived = ReviveUnits(combat, actor, effect, timeline); if (revived != null) { combat = revived.State; worldFacts.AddRange(revived.RuleEvents); }
                    continue;
                }
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
                    if (enemies.Count(e => combat.Get(e.Id).IsAlive && (effect.LimitScope == SummonLimitScope.Battlefield || e.Id == actor || e.SummonerId == actor)) >= effect.LivingLimit || combat.Combatants.Count >= 64) { failedSummon = true; break; }
                    int serial = combat.Combatants.Count;
                    ParticipantId id;
                    do { id = new ParticipantId(state.Combat.Id + "/summon/" + serial++); } while (combat.Combatants.Any(u => u.Id == id));
                    var setup = new BattleEnemySetup(id, template.Definition, template.Damage, template.DamageRules, template.TargetRules,
                        template.EffectRule, template.Behavior, owner.SummonCatalog, summonerId: actor);
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
            enemies[enemies.FindIndex(e => e.Id == actor)] = owner.WithBehaviorState(failedSummon && !owner.Behavior.ConsumeFailedSummon ? owner.BehaviorState : owner.BehaviorState.Complete(state.CompletedRounds));
            // PendingEnemies is deliberately unchanged: new units act in the next enemy phase.
            return new TeamBattleState(state.Rules, combat, state.Players, enemies, state.Phase, state.PendingEnemies,
                state.Revision, state.CompletedRounds, state.Scores, state.Actions);
        }
    }
}
