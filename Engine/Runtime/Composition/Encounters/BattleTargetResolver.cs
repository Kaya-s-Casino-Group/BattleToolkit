using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Pure target resolution against one battle snapshot. Source rules bind named requests before calling.</summary>
    public static class BattleTargetResolver
    {
        public static IReadOnlyList<ParticipantId> Resolve(TeamBattleState state, ParticipantId source, EffectTargetSpec spec,
            IEnumerable<ParticipantId> requested = null)
        {
            if (state == null || spec == null) throw new ArgumentNullException();
            if (!source.IsValid || !state.Combat.Combatants.Any(unit => unit.Id == source && unit.IsAlive))
                throw new ArgumentException("Target resolution requires a known living source.");
            var request = Guard.Copy(requested ?? Array.Empty<ParticipantId>(), nameof(requested));
            bool usesRequest = spec.Group != EffectTargetGroup.Self &&
                (spec.Selection == EffectTargetSelection.Requested || spec.Selection == EffectTargetSelection.ActorPolicy);
            if (!usesRequest && request.Length != 0)
                throw new ArgumentException("Automatic target selections do not accept requested targets.");
            if (spec.Group == EffectTargetGroup.Self) return CombatTargetResolver.Resolve(state.Combat, source, CombatTargetSpec.Self());

            var group = spec.Group == EffectTargetGroup.Opponents
                ? CombatTargetSpec.Opponents(CombatTargetSelection.AllLiving) : CombatTargetSpec.Allies(CombatTargetSelection.AllLiving);
            var candidates = CombatTargetResolver.Resolve(state.Combat, source, group).ToArray();
            // Validate requests before invoking a policy; a policy cannot hide an illegal caller choice.
            if (request.Length > 64 || request.Distinct().Count() != request.Length ||
                request.Any(target => !target.IsValid || !candidates.Contains(target)))
                throw new ArgumentException("Requested targets must be distinct living members of the selected group.");

            ParticipantId[] targets;
            switch (spec.Selection)
            {
                case EffectTargetSelection.Requested:
                    if (request.Length != 1) throw new ArgumentException("Requested selection requires exactly one target.");
                    targets = request; break;
                case EffectTargetSelection.FirstLiving:
                case EffectTargetSelection.LowestHealth:
                case EffectTargetSelection.HighestHealth:
                case EffectTargetSelection.AllLiving:
                    targets = CombatTargetResolver.Resolve(state.Combat, source, Automatic(spec)).ToArray(); break;
                case EffectTargetSelection.ActorPolicy:
                    targets = spec.Group == EffectTargetGroup.Opponents
                        ? state.TargetRulesFor(source).Attack.SelectTargets(new BattleTargetContext(state, source, request))?.ToArray()
                        : state.TargetRulesFor(source).Support.SelectTargets(new BattleAllyTargetContext(state, source, request))?.ToArray();
                    break;
                default: throw new ArgumentException("Unknown target selection.");
            }
            if (targets == null || targets.Length < 1 || targets.Length > 64 || targets.Distinct().Count() != targets.Length ||
                targets.Any(target => !target.IsValid || !candidates.Contains(target)))
                throw new ArgumentException("Target selection must return distinct living members of the selected group.");
            return Guard.View(targets);
        }

        private static CombatTargetSpec Automatic(EffectTargetSpec spec)
        {
            CombatTargetSelection selection;
            switch (spec.Selection)
            {
                case EffectTargetSelection.FirstLiving: selection = CombatTargetSelection.FirstLiving; break;
                case EffectTargetSelection.LowestHealth: selection = CombatTargetSelection.LowestHealth; break;
                case EffectTargetSelection.HighestHealth: selection = CombatTargetSelection.HighestHealth; break;
                case EffectTargetSelection.AllLiving: selection = CombatTargetSelection.AllLiving; break;
                default: throw new ArgumentException("This target selection requires battle request or actor policy handling.");
            }
            return spec.Group == EffectTargetGroup.Opponents ? CombatTargetSpec.Opponents(selection) : CombatTargetSpec.Allies(selection);
        }
    }
}
