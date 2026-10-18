using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Pure and deterministic. Do not mutate external state, consume randomness or trigger presentation.
    /// The same instance is called by previews and committed actions; return one request per supplied target.</summary>
    public interface IScoredHandDamageRule
    {
        string Version { get; }
        DamagePlan Resolve(ScoredHandDamageContext context);
    }
    public sealed class ScoredHandDamageContext
    {
        public CombatState Combat { get; }
        public ParticipantId Source { get; }
        public ScoreResult Score { get; }
        public HandAttackInfo Hand { get; }
        public IReadOnlyList<ParticipantId> Targets { get; }
        public ScoredHandDamageContext(CombatState combat, ParticipantId source, ScoreResult score, IEnumerable<ParticipantId> targets, HandAttackInfo hand = null)
        {
            Combat = combat ?? throw new ArgumentNullException(nameof(combat)); ScoreEngine.Validate(score);
            var copy = Guard.Copy(targets, nameof(targets));
            if (!combat.Get(source).IsAlive || copy.Length == 0 || copy.Length > 64 || copy.Distinct().Count() != copy.Length ||
                copy.Any(target => !combat.Get(target).IsAlive || combat.Get(target).TeamId == combat.Get(source).TeamId))
                throw new ArgumentException("Use the validated living opponent set.");
            if (hand != null && hand.Pattern.Category != score.Category) throw new ArgumentException("Hand facts must match the scored category.");
            Hand = hand;
            Source = source; Score = score; Targets = Guard.View(copy);
        }
    }
    public sealed class DamagePlan
    {
        public string RuleVersion { get; }
        public string Explanation { get; }
        public IReadOnlyList<DamageRequest> Requests { get; }
        public DamagePlan(string ruleVersion, string explanation, IEnumerable<DamageRequest> requests)
        {
            RuleVersion = Guard.Text(ruleVersion, nameof(ruleVersion)); Explanation = Guard.Text(explanation, nameof(explanation));
            var copy = Guard.Copy(requests, nameof(requests));
            if (copy.Length == 0 || copy.Length > 64 || copy.Any(x => x == null)) throw new ArgumentException("Invalid damage plan.");
            Requests = Guard.View(copy);
        }
        internal void Validate(ScoredHandDamageContext context, string version)
        {
            if (RuleVersion != version || Requests.Any(x => x.Source != context.Source) ||
                !Requests.Select(x => x.Target).SequenceEqual(context.Targets))
                throw new ArgumentException("Damage rules must preserve the selected source, target order and version.");
        }
    }
    /// <summary>Default attack construction: preserve the entire score as each selected target's base damage.</summary>
    public sealed class EqualScoreDamageRule : IScoredHandDamageRule
    {
        public string Version { get; }
        public EqualScoreDamageRule(string version = "score-damage.equal.1") => Version = Guard.Text(version, nameof(version));
        public DamagePlan Resolve(ScoredHandDamageContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return new DamagePlan(Version, "最终得分 " + context.Score.Total + " → 等额基础伤害（每个目标）",
                context.Targets.Select(target => new DamageRequest(context.Source, target, context.Score.Total)));
        }
    }
    /// <summary>Legacy opt-in conversion retained for existing standalone 1v1 callers and snapshots.</summary>
    public sealed class ScoreDamagePolicy : IScoredHandDamageRule
    {
        public string Version { get; }
        public long PointsPerDamage { get; }
        public ScoreDamagePolicy(long pointsPerDamage, string version = "score-damage.divide.1")
        {
            if (pointsPerDamage <= 0) throw new ArgumentOutOfRangeException(nameof(pointsPerDamage));
            PointsPerDamage = pointsPerDamage; Version = Guard.Text(version, nameof(version));
        }
        // Compatibility for the standalone legacy 1v1 engine/snapshot format.
        public long Calculate(ScoreResult score) { ScoreEngine.Validate(score); return score.Total / PointsPerDamage; }
        public DamagePlan Resolve(ScoredHandDamageContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            long damage = Calculate(context.Score);
            return new DamagePlan(Version, "得分 " + context.Score.Total + " ÷ " + PointsPerDamage + "，向下取整 = " + damage + "（每个目标）",
                context.Targets.Select(target => new DamageRequest(context.Source, target, damage)));
        }
    }
}
