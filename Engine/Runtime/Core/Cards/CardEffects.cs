using System;

namespace PokerEngine
{
    /// <summary>Stable, combinable card markers. Enhanced/Laminated have no implicit balancing or artwork.</summary>
    [Flags]
    public enum CardEffects
    {
        None = 0,
        Enhanced = 1 << 0,
        Laminated = 1 << 1,
        Ineffective = 1 << 2
    }

    public static class CardEffectRules
    {
        private const CardEffects Known = CardEffects.Enhanced | CardEffects.Laminated | CardEffects.Ineffective;
        public static bool IsValid(CardEffects effects) => (effects & ~Known) == 0;
        public static void Validate(CardEffects effects)
        {
            if (!IsValid(effects)) throw new ArgumentOutOfRangeException(nameof(effects), "Unknown card effect bits.");
        }
        public static bool CanScore(CardEffects effects)
        {
            Validate(effects);
            return (effects & CardEffects.Ineffective) == 0;
        }
    }
}
