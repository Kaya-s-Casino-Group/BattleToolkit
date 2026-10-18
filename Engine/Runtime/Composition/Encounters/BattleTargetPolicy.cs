using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Pure actor-owned attack targeting seam. UI may supply a request; policy decides the valid target set.
    /// Returning multiple living opponents enables area attacks without changing turn scheduling.</summary>
    public interface IBattleTargetPolicy
    {
        IReadOnlyList<ParticipantId> SelectTargets(BattleTargetContext context);
    }
    public sealed class BattleTargetContext
    {
        public TeamBattleState State { get; }
        public ParticipantId Source { get; }
        public IReadOnlyList<ParticipantId> Requested { get; }
        public IReadOnlyList<ParticipantId> Opponents { get; }
        internal BattleTargetContext(TeamBattleState state, ParticipantId source, IEnumerable<ParticipantId> requested)
        {
            State = state; Source = source; Requested = Guard.View((requested ?? new ParticipantId[0]).ToArray());
            string team = state.Combat.Get(source).TeamId;
            Opponents = Guard.View(state.Combat.Combatants.Where(unit => unit.IsAlive && unit.TeamId != team).Select(unit => unit.Id).ToArray());
        }
    }
    public sealed class FirstLivingOpponentPolicy : IBattleTargetPolicy
    {
        public IReadOnlyList<ParticipantId> SelectTargets(BattleTargetContext context)
        {
            if (context.Requested.Count > 1) throw new ArgumentException("This policy accepts one target.");
            return context.Requested.Count == 0 ? context.Opponents.Take(1).ToArray() : context.Requested;
        }
    }
}
