using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Pure, deterministic attack-side adjustment. No scoring, target replacement, state writes or randomness.</summary>
    public interface IAttackDamageModifier
    {
        DamageModification Modify(DamageModifierContext context);
    }

    /// <summary>Pure target-side adjustment, evaluated immediately before block and health settlement.</summary>
    public interface IIncomingDamageModifier
    {
        DamageModification Modify(DamageModifierContext context);
    }

    public sealed class DamageModifierContext
    {
        public CombatState Combat { get; }
        public ParticipantId Source { get; }
        public ParticipantId Target { get; }
        public long Amount { get; }
        public AttackContext Attack { get; }
        public DamageModifierContext(CombatState combat, ParticipantId source, ParticipantId target, long amount, AttackContext attack = null)
            : this(combat, source, target, amount, attack, false) { }

        // Only the resolver can admit defeated sources for explicit authority-generated reactions.
        internal DamageModifierContext(CombatState combat, ParticipantId source, ParticipantId target, long amount, AttackContext attack, bool allowDefeatedSource)
        {
            Combat = combat ?? throw new ArgumentNullException(nameof(combat));
            var sourceUnit = combat.Get(source);
            if (amount < 0 || (!allowDefeatedSource && !sourceUnit.IsAlive) || !combat.Get(target).IsAlive)
                throw new ArgumentException("Damage modifiers require living units and nonnegative damage.");
            Attack = attack ?? new AttackContext(source, AttackOrigin.Other, amount);
            if (Attack.Source != source) throw new ArgumentException("Attack source cannot change between stages.");
            Source = source; Target = target; Amount = amount;
        }
    }

    /// <summary>Auditable stage output. A modifier can change only the amount, never source or target.</summary>
    public sealed class DamageModification
    {
        public string RuleVersion { get; }
        public string Explanation { get; }
        public long InputAmount { get; }
        public long OutputAmount { get; }
        public IReadOnlyList<DamageTriggerReason> Triggers { get; }
        public DamageModification(string ruleVersion, string explanation, long inputAmount, long outputAmount,
            IEnumerable<DamageTriggerReason> triggers = null)
        {
            RuleVersion = Guard.Text(ruleVersion, nameof(ruleVersion));
            Explanation = Guard.Text(explanation, nameof(explanation));
            if (inputAmount < 0 || outputAmount < 0) throw new ArgumentOutOfRangeException(nameof(outputAmount));
            var reasons = Guard.Copy(triggers ?? Array.Empty<DamageTriggerReason>(), nameof(triggers));
            if (reasons.Length > 256 || reasons.Any(reason => reason == null)) throw new ArgumentException("Invalid trigger reasons.");
            Triggers = Guard.View(reasons);
            InputAmount = inputAmount; OutputAmount = outputAmount;
        }
        public static DamageModification Validate(DamageModification result, long inputAmount, AttackContext attack = null)
        {
            if (result == null || result.InputAmount != inputAmount)
                throw new ArgumentException("Damage modifier must report the supplied input amount.");
            foreach (var reason in result.Triggers) reason.Validate(attack);
            return result;
        }
    }

    public sealed class IdentityAttackDamageModifier : IAttackDamageModifier
    {
        public DamageModification Modify(DamageModifierContext context) =>
            new DamageModification("attack.identity.1", "无攻击修正，保留基础伤害", context.Amount, context.Amount);
    }

    public sealed class IdentityIncomingDamageModifier : IIncomingDamageModifier
    {
        public DamageModification Modify(DamageModifierContext context) =>
            new DamageModification("incoming.identity.1", "无目标承伤修正，进入格挡与生命结算", context.Amount, context.Amount);
    }
}
