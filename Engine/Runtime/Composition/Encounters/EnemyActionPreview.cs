using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Read-only forecast from a battle revision. Hits are the real resolver's damage events,
    /// including attack/incoming modifications, block absorption and health loss. No state is committed.</summary>
    public sealed class EnemyActionPreview
    {
        public ParticipantId Source { get; }
        public long ExpectedRevision { get; }
        public CardDuelError Error { get; }
        public bool Accepted => Error == CardDuelError.None;
        public IReadOnlyList<CombatRuleEvent> RuleEvents { get; }
        public bool IsSkipped => RuleEvents.OfType<CombatActionWindowSkippedEvent>().Any(item => item.Actor == Source);
        public IReadOnlyList<CombatEvent> Hits { get; }

        internal EnemyActionPreview(ParticipantId source, long revision, TeamBattleTransition transition)
        {
            Source = source; ExpectedRevision = revision; Error = transition.Error;
            RuleEvents = Guard.View(transition.RuleEvents.ToArray());
            Hits = Guard.View(transition.Events.Where(item => item.Effect.Kind == CombatEffectKind.Damage).ToArray());
        }
    }
}
