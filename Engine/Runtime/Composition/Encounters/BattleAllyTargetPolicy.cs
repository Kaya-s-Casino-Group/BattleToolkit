using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Pure targeting for supportive actions. Kept separate from hostile attack targeting.</summary>
    public interface IBattleAllyTargetPolicy
    {
        IReadOnlyList<ParticipantId> SelectTargets(BattleAllyTargetContext context);
    }
    public sealed class BattleAllyTargetContext
    {
        public TeamBattleState State { get; }
        public ParticipantId Source { get; }
        public IReadOnlyList<ParticipantId> Requested { get; }
        public IReadOnlyList<ParticipantId> Allies { get; }
        internal BattleAllyTargetContext(TeamBattleState state, ParticipantId source, IEnumerable<ParticipantId> requested)
        {
            State = state; Source = source;
            Requested = Guard.View((requested ?? Array.Empty<ParticipantId>()).ToArray());
            string team = state.Combat.Get(source).TeamId;
            Allies = Guard.View(state.Combat.Combatants.Where(unit => unit.IsAlive && unit.TeamId == team).Select(unit => unit.Id).ToArray());
        }
    }
    public sealed class SelfOrRequestedAllyPolicy : IBattleAllyTargetPolicy
    {
        public IReadOnlyList<ParticipantId> SelectTargets(BattleAllyTargetContext context)
        {
            if (context.Requested.Count > 1) throw new ArgumentException("This policy accepts one ally.");
            return context.Requested.Count == 0 ? new[] { context.Source } : context.Requested;
        }
    }
}
