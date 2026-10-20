namespace PokerEngine
{
    internal sealed class EnragedAttackDamageModifier : IAttackDamageModifier
    {
        private readonly IAttackDamageModifier original; private readonly long bonus;
        public string Version => "attack.enrage.1";
        internal EnragedAttackDamageModifier(IAttackDamageModifier original, long bonus) { this.original = original; this.bonus = bonus; }
        public DamageModification Modify(DamageModifierContext context)
        {
            var result = original.Modify(context);
            return new DamageModification(Version, "狂暴增伤 + " + bonus, context.Amount, checked(result.OutputAmount + bonus));
        }
    }
    /// <summary>Immutable unit-owned damage rules, shared by players and enemies. No action resources or Unity references.</summary>
    public sealed class BattleActorDamageRules
    {
        public IAttackDamageModifier AttackDamage { get; }
        public IIncomingDamageModifier IncomingDamage { get; }
        public BattleActorDamageRules(IAttackDamageModifier attackDamage = null, IIncomingDamageModifier incomingDamage = null)
        {
            AttackDamage = attackDamage ?? new IdentityAttackDamageModifier();
            IncomingDamage = incomingDamage ?? new IdentityIncomingDamageModifier();
        }
    }
}
