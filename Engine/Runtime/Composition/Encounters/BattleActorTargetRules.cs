namespace PokerEngine
{
    /// <summary>Actor-owned, deterministic target policies captured at encounter creation.</summary>
    public sealed class BattleActorTargetRules
    {
        public IBattleTargetPolicy Attack { get; }
        public IBattleAllyTargetPolicy Support { get; }
        public BattleActorTargetRules(IBattleTargetPolicy attack = null, IBattleAllyTargetPolicy support = null)
        {
            Attack = attack ?? new FirstLivingOpponentPolicy();
            Support = support ?? new SelfOrRequestedAllyPolicy();
        }
    }
}
