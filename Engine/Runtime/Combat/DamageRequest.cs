using System;

namespace PokerEngine
{
    /// <summary>Unmitigated damage. Only CombatEngine applies block, health clamping and death.</summary>
    public sealed class DamageRequest
    {
        public ParticipantId Source { get; }
        public ParticipantId Target { get; }
        public long Amount { get; }
        public DamageRequest(ParticipantId source, ParticipantId target, long amount)
        {
            if (!source.IsValid || !target.IsValid || amount < 0) throw new ArgumentException("Invalid damage request.");
            Source = source; Target = target; Amount = amount;
        }
        public CombatEffect ToEffect() => new CombatEffect(CombatEffectKind.Damage, Source, Target, Amount);
    }
}
