using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public enum OpponentTargetSelection { FirstLiving, LowestHealth, HighestHealth, AllLiving }

    /// <summary>Deterministic selection; equal health uses roster order, never external randomness.</summary>
    public sealed class OpponentTargetPolicy : IBattleTargetPolicy
    {
        public OpponentTargetSelection Selection { get; }
        public bool AllowRequestedTargets { get; }
        public OpponentTargetPolicy(OpponentTargetSelection selection, bool allowRequestedTargets = true)
        {
            if (!Enum.IsDefined(typeof(OpponentTargetSelection), selection)) throw new ArgumentOutOfRangeException(nameof(selection));
            Selection = selection; AllowRequestedTargets = allowRequestedTargets;
        }
        public IReadOnlyList<ParticipantId> SelectTargets(BattleTargetContext context)
        {
            if (context.Requested.Count > 0)
            {
                if (!AllowRequestedTargets || Selection != OpponentTargetSelection.AllLiving && context.Requested.Count > 1)
                    throw new ArgumentException("This attack policy does not accept the requested target selection.");
                return context.Requested;
            }
            switch (Selection)
            {
                case OpponentTargetSelection.LowestHealth:
                    return context.Opponents.OrderBy(id => context.State.Combat.Get(id).Health).Take(1).ToArray();
                case OpponentTargetSelection.HighestHealth:
                    return context.Opponents.OrderByDescending(id => context.State.Combat.Get(id).Health).Take(1).ToArray();
                case OpponentTargetSelection.AllLiving: return context.Opponents;
                default: return context.Opponents.Take(1).ToArray();
            }
        }
    }
}
