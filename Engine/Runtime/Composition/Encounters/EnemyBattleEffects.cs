using System;

namespace PokerEngine
{
    /// <summary>Pure optional enemy rule; absent rules keep the fixed damage path.</summary>
    public interface IEnemyBattleEffectRule
    {
        string Version { get; }
        BattleEffectSequence Resolve(EnemyBattleEffectContext context);
    }

    public sealed class EnemyBattleEffectContext
    {
        public CombatState Combat { get; }
        public ParticipantId Source { get; }
        public EnemyBattleEffectContext(CombatState combat, ParticipantId source)
        {
            Combat = combat ?? throw new ArgumentNullException(nameof(combat));
            if (!source.IsValid || !combat.Get(source).IsAlive) throw new ArgumentException("Enemy effects require a living source.");
            Source = source;
        }
    }
}
