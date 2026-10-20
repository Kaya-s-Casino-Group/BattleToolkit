using System.Collections.Generic;
namespace PokerEngine
{
    /// <summary>Optional encounter mechanics. Use a fresh instance per transaction; the resolver owns all combat writes and reaction limits.</summary>
    public interface ICombatInteractions
    {
        int StatusStacks(StatusInstance status);
        ParticipantId Redirect(CombatState current, CombatEffect effect, CombatEffectProvenance provenance);
        bool BypassBlock(CombatState current, CombatEffect effect);
        IEnumerable<CombatReaction> React(CombatReactionContext context);
    }
}
