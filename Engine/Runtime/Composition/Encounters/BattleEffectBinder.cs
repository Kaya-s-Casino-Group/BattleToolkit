using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PokerEngine
{
    internal sealed class BattleEffectBinding
    {
        internal CardDuelError Error { get; }
        internal IReadOnlyList<CombatEffect> Effects { get; }
        internal BattleEffectBinding(CardDuelError error, IEnumerable<CombatEffect> effects = null)
        { Error = error; Effects = Guard.View(Guard.Copy(effects ?? Array.Empty<CombatEffect>(), nameof(effects))); }
    }

    /// <summary>Binds every step before settlement. Source and attack facts are owned by the engine, never by a rule.</summary>
    internal static class BattleEffectBinder
    {
        internal static BattleEffectBinding Bind(TeamBattleState state, ParticipantId source, BattleEffectSequence sequence,
            IEnumerable<ParticipantId> requestedTargets, BattleTargetBindings bindings, HandAttackInfo hand, AttackOrigin? origin = null)
        {
            try
            {
                var requested = Guard.Copy(requestedTargets ?? Array.Empty<ParticipantId>(), nameof(requestedTargets));
                if (requested.Length > 0)
                {
                    if (bindings != null && bindings.Slots.Count > 0) return new BattleEffectBinding(CardDuelError.InvalidTarget);
                    bindings = new BattleTargetBindings(BattleTargetBindings.DefaultSlot, requested);
                }
                // No request can be silently ignored by an automatic/self target or by a rule's choice of steps.
                var slots = new HashSet<string>(sequence.Steps.Select(step => step.Target.RequestSlot).Where(slot => slot != null), StringComparer.Ordinal);
                if (bindings != null && bindings.Slots.Keys.Any(slot => !slots.Contains(slot)))
                    return new BattleEffectBinding(CardDuelError.InvalidTarget);

                var effects = new List<CombatEffect>();
                for (int stepIndex = 0; stepIndex < sequence.Steps.Count; stepIndex++)
                {
                    var step = sequence.Steps[stepIndex]; IReadOnlyList<ParticipantId> selected = null;
                    StatusDuration duration = null;
                    if (step.Effect.Kind == CombatEffectKind.ApplyStatus)
                    {
                        try
                        {
                            var definition = state.Rules.StatusCatalog?.Get(step.Effect.Status.DefinitionId);
                            duration = step.Effect.Status.Duration ?? BattleStatusTiming.Bind(state,
                                step.Effect.Status.Lifetime ?? definition?.DefaultLifetime);
                        }
                        catch (ArgumentException) { return new BattleEffectBinding(CardDuelError.InvalidEffectPlan); }
                    }
                    if (step.Target.RequestSlot != null && bindings != null)
                        bindings.Slots.TryGetValue(step.Target.RequestSlot, out selected);
                    var targets = BattleTargetResolver.Resolve(state, source, step.Target, selected);
                    if (effects.Count + targets.Count > 64) return new BattleEffectBinding(CardDuelError.InvalidEffectPlan);
                    for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
                    {
                        var target = targets[targetIndex]; var spec = step.Effect;
                        if (spec.Kind == CombatEffectKind.ApplyStatus)
                        {
                            // Use the action revision: card-only/empty-reward actions need not advance Combat.Revision.
                            var id = new StatusInstanceId(state.Combat.Id + "/status/action/" + state.Revision.ToString(CultureInfo.InvariantCulture) +
                                "/" + stepIndex.ToString(CultureInfo.InvariantCulture) + "/" + targetIndex.ToString(CultureInfo.InvariantCulture));
                            // Capacity and instance uniqueness are checked only when Combat actually applies the status.
                            // An earlier effect may defeat this target, in which case this status will be skipped.
                            var status = new StatusInstance(id, spec.Status.DefinitionId, EntityRef.Participant(source), EntityRef.Participant(target),
                                spec.Status.Stacks, duration);
                            effects.Add(CombatEffect.ApplyStatus(status));
                        }
                        else
                        {
                            var attack = spec.Kind == CombatEffectKind.Damage ?
                                new AttackContext(source, origin ?? (hand == null ? AttackOrigin.Other : AttackOrigin.ScoredHand), spec.Amount, hand) : null;
                            effects.Add(new CombatEffect(spec.Kind, source, target, spec.Amount, attack: attack));
                        }
                    }
                }
                return new BattleEffectBinding(CardDuelError.None, effects);
            }
            catch (ArgumentException) { return new BattleEffectBinding(CardDuelError.InvalidTarget); }
        }
    }
}
