using System;
using System.Collections.Generic;
using System.Linq;
namespace PokerEngine
{
    public enum EnemyLinkKind { DamageDone, DamageTaken, BlockReceived, HealingDone, BlockDone, Intercept, BypassBlock, DamageHeal, HealFromMaxHealth }
    /// <summary>Template selectors and coefficients only; no monster identities are hardcoded.</summary>
    public sealed class EnemyLinkRule
    {
        public string Id { get; }
        public EnemyLinkKind Kind { get; }
        public IReadOnlyList<string> Targets { get; }
        public IReadOnlyList<string> CountRefs { get; }
        public string HealTarget { get; }
        public decimal Rate { get; }
        public bool Snapshot { get; }
        public EnemyCondition Condition { get; }
        public EnemyLinkRule(string id, EnemyLinkKind kind, IEnumerable<string> targets, decimal rate = 0,
            IEnumerable<string> countRefs = null, bool snapshot = false, EnemyCondition condition = null, string healTarget = null)
        {
            Id = Guard.Text(id, nameof(id)); Kind = kind; Rate = rate; Snapshot = snapshot;
            Targets = Array.AsReadOnly(targets.ToArray()); CountRefs = Array.AsReadOnly((countRefs ?? Array.Empty<string>()).ToArray());
            if (!Enum.IsDefined(typeof(EnemyLinkKind), kind) || Targets.Count == 0 || Targets.Any(string.IsNullOrWhiteSpace) || CountRefs.Any(string.IsNullOrWhiteSpace) || rate < -1 || rate > 1000000)
                throw new ArgumentException("Invalid enemy link rule.");
            if (kind == EnemyLinkKind.DamageHeal && string.IsNullOrWhiteSpace(healTarget)) throw new ArgumentException("DamageHeal requires healTarget.");
            HealTarget = healTarget; Condition = condition ?? new EnemyCondition();
        }
        internal bool Matches(CombatState combat, ParticipantId owner, ParticipantId target) => Targets.Contains(combat.Get(target).DefinitionId) ||
            Targets.Contains("Self") && target == owner || Targets.Contains("Opponents") && combat.Get(target).TeamId != combat.Get(owner).TeamId;
    }
    internal sealed class EnemyLinkRuntime : ICombatInteractions
    {
        private readonly TeamBattleState start;
        internal readonly HashSet<string> EnteredStages = new HashSet<string>();
        internal EnemyLinkRuntime(TeamBattleState start) { this.start = start; }
        private IEnumerable<Tuple<BattleEnemySetup, EnemyLinkRule>> Active(CombatState current)
        {
            foreach (var enemy in start.Enemies)
                foreach (var rule in enemy.Behavior?.Links ?? Array.Empty<EnemyLinkRule>())
                {
                    var snapshot = rule.Snapshot ? start.Combat : current;
                    if (snapshot.Get(enemy.Id).IsAlive && rule.Condition.Matches(snapshot, start.CompletedRounds, enemy.Id))
                        yield return Tuple.Create(enemy, rule);
                }
        }
        internal IEnumerable<CombatAmountContribution> Contributions(CombatAmountContext context)
        {
            foreach (var pair in Active(context.Combat))
            {
                var owner = pair.Item1.Id; var r = pair.Item2;
                bool outgoing = context.Stage == CombatAmountStage.Outgoing;
                if (r.Kind == EnemyLinkKind.HealFromMaxHealth && outgoing && context.Effect.Kind == CombatEffectKind.Heal && context.Effect.Source == owner && context.Provenance.Origin == CombatEffectOrigin.Primary && r.Matches(context.Combat, owner, context.Effect.Target))
                { yield return new CombatAmountContribution(owner.Value, r.Id, flatAdjustment: checked((long)decimal.Floor(context.Combat.Get(context.Effect.Target).MaxHealth * r.Rate))); continue; }
                bool applies = context.Effect.Kind == CombatEffectKind.Damage && (outgoing ? r.Kind == EnemyLinkKind.DamageDone : r.Kind == EnemyLinkKind.DamageTaken) ||
                    context.Effect.Kind == CombatEffectKind.GainBlock && (outgoing ? r.Kind == EnemyLinkKind.BlockDone : r.Kind == EnemyLinkKind.BlockReceived) ||
                    context.Effect.Kind == CombatEffectKind.Heal && outgoing && r.Kind == EnemyLinkKind.HealingDone;
                if (!applies || !r.Matches(context.Combat, owner, context.Owner)) continue;
                var countState = r.Snapshot ? start.Combat : context.Combat;
                int count = r.CountRefs.Count == 0 ? 1 : countState.Combatants.Count(u => u.IsAlive && u.TeamId == countState.Get(owner).TeamId && r.CountRefs.Contains(u.DefinitionId));
                yield return new CombatAmountContribution(owner.Value, r.Id, rateAdjustment: r.Rate * count);
            }
        }
        public int StatusStacks(StatusInstance status) => status.Stacks;
        public ParticipantId Redirect(CombatState current, CombatEffect effect, CombatEffectProvenance provenance)
        {
            if (effect.Kind != CombatEffectKind.Damage || provenance.Origin != CombatEffectOrigin.Primary) return effect.Target;
            foreach (var pair in Active(current))
                if (pair.Item2.Kind == EnemyLinkKind.Intercept && pair.Item1.Id != effect.Target && pair.Item2.Matches(current, pair.Item1.Id, effect.Target))
                    return pair.Item1.Id;
            return effect.Target;
        }
        public bool BypassBlock(CombatState current, CombatEffect effect) => Active(current).Any(p =>
            p.Item2.Kind == EnemyLinkKind.BypassBlock && p.Item2.Matches(current, p.Item1.Id, effect.Source));
        public IEnumerable<CombatReaction> React(CombatReactionContext context)
        {
            foreach(var enemy in start.Enemies.Where(e=>e.Behavior!=null && context.State.Get(e.Id).IsAlive)) {
                var stage=enemy.Behavior.Stages.FirstOrDefault(s=>s.Condition.Matches(context.State,start.CompletedRounds,enemy.Id));
                if(stage==null || stage.EntryEffects.Count==0 || enemy.BehaviorState.Uses.ContainsKey("@entry/"+stage.Id) || !EnteredStages.Add(enemy.Id.Value+"/"+stage.Id))continue;
                foreach(var effect in stage.EntryEffects)foreach(var unit in effect.ReviveRefs.SelectMany(r=>context.State.Combatants.Where(u=>u.DefinitionId==r && !u.IsAlive && u.Id!=enemy.Id && u.TeamId==context.State.Get(enemy.Id).TeamId)).Distinct().Take(effect.Count))
                    yield return new CombatReaction(new CombatEffect(CombatEffectKind.Revive,enemy.Id,unit.Id,unit.MaxHealth),"stage/"+stage.Id,enemy.Id,CombatEffectOrigin.Triggered);
            }
            var hit = context.Event as CombatEvent;
            if (hit?.Effect.Kind != CombatEffectKind.Damage || hit.DamageToHealth == 0) yield break;
            foreach (var p in Active(context.State))
            {
                var r = p.Item2;
                if (r.Kind != EnemyLinkKind.DamageHeal || !r.Matches(context.State, p.Item1.Id, hit.Effect.Source)) continue;
                var target = context.State.Combatants.FirstOrDefault(u => u.IsAlive && u.TeamId == context.State.Get(p.Item1.Id).TeamId && u.DefinitionId == r.HealTarget);
                if (target != null) yield return new CombatReaction(new CombatEffect(CombatEffectKind.Heal, p.Item1.Id, target.Id,
                    checked((long)decimal.Floor(hit.DamageToHealth * r.Rate))), r.Id, p.Item1.Id, CombatEffectOrigin.Lifesteal);
            }
        }
    }
}
