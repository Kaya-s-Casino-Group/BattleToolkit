using System;

namespace PokerEngine
{
    /// <summary>Pure authority rule. Build target-independent effects from scored hand facts; no randomness or state writes.</summary>
    public interface IScoredHandEffectRule
    {
        string Version { get; }
        BattleEffectSequence Resolve(ScoredHandEffectContext context);
    }

    /// <summary>Optional content dependency validation at battle creation, before accepting any action.</summary>
    public interface IBattleStatusCatalogValidator
    {
        void ValidateStatusCatalog(CombatStatusCatalog catalog);
    }

    public sealed class ScoredHandEffectContext
    {
        public CombatState Combat { get; }
        public ParticipantId Source { get; }
        public ScoreResult Score { get; }
        public HandAttackInfo Hand { get; }
        public ScoredHandEffectContext(CombatState combat, ParticipantId source, ScoreResult score, HandAttackInfo hand = null)
        {
            Combat = combat ?? throw new ArgumentNullException(nameof(combat)); ScoreEngine.Validate(score);
            if (!combat.Get(source).IsAlive || hand != null && hand.Pattern.Category != score.Category)
                throw new ArgumentException("Scored effects require a living source and matching hand facts.");
            Source = source; Score = score; Hand = hand;
        }
    }
}
