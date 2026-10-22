using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Restrictions belong to the five encounter identities, so cleanse and Revive cannot erase them.</summary>
    internal static class WrathHandEffectRestrictions
    {
        internal static IEnumerable<ParticipantId> HandIds(IEnumerable<BattleEnemySetup> enemies)
        {
            var units = enemies.ToArray();
            return units.Where(e => e.Behavior?.Wrath != null)
                .SelectMany(body => WrathBossRules.Hands(units, body.Behavior.Wrath)).Select(hand => hand.Id).Distinct();
        }
        internal static bool IsHand(IEnumerable<BattleEnemySetup> enemies, ParticipantId id) => HandIds(enemies).Contains(id);
        internal static CombatAmountContribution NegativeOnly(CombatAmountContribution contribution) => new CombatAmountContribution(
            contribution.SourceInstanceId, contribution.RuleId, Math.Min(0, contribution.FlatAdjustment), Math.Min(0m, contribution.RateAdjustment));
    }

    internal sealed class WrathHandAttackModifier : IAttackDamageModifier
    {
        private readonly IAttackDamageModifier original;
        internal WrathHandAttackModifier(IAttackDamageModifier original) { this.original = original; }
        public DamageModification Modify(DamageModifierContext context)
        {
            // Configured attack rules expose their components; remove each positive part independently.
            var scaled = original as ScaledAttackDamageModifier;
            var allowed = scaled == null ? original : new ScaledAttackDamageModifier(Math.Min(1m, scaled.Multiplier), Math.Min(0, scaled.FlatAdjustment), scaled.Version);
            var result = DamageModification.Validate(allowed.Modify(context), context.Amount, context.Attack);
            // An opaque extension is one atomic contribution. Its negative result remains valid.
            return new DamageModification(result.RuleVersion, "手臂拒绝正向攻击加成；" + result.Explanation,
                result.InputAmount, Math.Min(context.Amount, result.OutputAmount), result.Triggers);
        }
    }
}
