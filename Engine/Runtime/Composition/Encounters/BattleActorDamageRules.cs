namespace PokerEngine
{
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
