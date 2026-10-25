using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Read-only input budgets, windows and card membership. Does not evaluate custom
    /// effect/target policies, consume random state or decide whether a full command will succeed.</summary>
    internal static class BattleActionQuery
    {
        internal static bool CanAct(CardDuelState state, ParticipantId actor) =>
            state.HasStarted && !state.Duel.IsFinished && state.Duel.HasPlayerWindow(actor) &&
            (state.Duel.CanAct(actor) || state.Duel.Player(actor).Rules.Flow.UsesTurnFlow &&
                (state.FlowFor(actor).Window == BattlePlayerWindow.Draw || state.FlowFor(actor).Window == BattlePlayerWindow.ForcedDiscard ||
                 state.FlowFor(actor).Window == BattlePlayerWindow.Ending)) && state.Zone(CardZoneKinds.Played, actor).Cards.Count == 0;

        internal static bool CanSelect(TeamBattleEngine engine, CardDuelState state, ParticipantId actor,
            IEnumerable<CardInstanceId> selected, bool discard)
        {
            if (!CanAct(state, actor) || selected == null) return false;
            var player = state.Duel.Player(actor);
            var rules = player.Rules;
            if (!state.Duel.CanAct(actor)) return false;
            bool freeDefense = discard && !rules.Flow.EnergyOnly && rules.Flow.UsesTurnFlow && (!rules.Flow.DiscardCostsAction || state.FlowFor(actor).Window == BattlePlayerWindow.Ending);
            if (discard && !state.HasDiscardBudgetFor(actor) || !freeDefense &&
                !engine.QueryActionCost(state.Duel, actor,
                    discard ? ScoredDuelActionKind.Discard : ScoredDuelActionKind.ScoredHand).CanAfford) return false;
            if (rules.Flow.UsesTurnFlow && (discard ? state.FlowFor(actor).Window != BattlePlayerWindow.Action && state.FlowFor(actor).Window != BattlePlayerWindow.Ending :
                state.FlowFor(actor).Window != BattlePlayerWindow.Action)) return false;
            int limit = discard ? rules.MaxDiscardCards : PlayLimit(state, actor, false);
            var hand = new HashSet<CardInstanceId>(state.Zone(CardZoneKinds.Hand, actor).Cards);
            var seen = new HashSet<CardInstanceId>();
            foreach (var id in selected)
            {
                if (!id.IsValid || !seen.Add(id) || seen.Count > limit || !hand.Contains(id)) return false;
                if (!CanSelectCard(state.Table.GetCard(id), discard)) return false;
            }
            if (!rules.Flow.EnergyOnly && rules.Flow.UsesTurnFlow && (discard && (state.FlowFor(actor).DefenseUsed && BattleRuleResolver.Value(rules, player.Relics, BattleRuleValueKey.DiscardEndsTurn).Value > 0 ||
                state.FlowFor(actor).Window == BattlePlayerWindow.Ending && !rules.Flow.EndDefense) ||
                !discard && state.FlowFor(actor).ActionsRemaining == 0)) return false;
            return seen.Count >= (discard ? 1 : PlayLimit(state, actor, true));
        }

        internal static bool HasAffordableCardAction(CardDuelState state, ParticipantId actor)
        {
            var resolver = new BattleRuleResolver();
            return resolver.ResolveActionCost(new BattleActionCostQuery(state.Duel, actor, ScoredDuelActionKind.ScoredHand)).CanAfford ||
                state.HasDiscardBudgetFor(actor) && resolver.ResolveActionCost(new BattleActionCostQuery(state.Duel, actor, ScoredDuelActionKind.Discard)).CanAfford;
        }

        // Callers check count limits first, preserving short-circuiting and error precedence.
        internal static bool HasSelectableCards(CardDuelState state, ParticipantId actor, CardInstanceId[] ids, bool discard) =>
            ids.Distinct().Count() == ids.Length &&
            ids.All(id => state.Zone(CardZoneKinds.Hand, actor).Cards.Contains(id) && CanSelectCard(state.Table.GetCard(id), discard));

        private static bool CanSelectCard(CardInstance card, bool discard) => discard ? card.CanDiscard : card.CanPlay;

        internal static int PlayLimit(CardDuelState state, ParticipantId actor, bool minimum)
        {
            var player = state.Duel.Player(actor);
            int max = BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.MaxPlayCards).Value;
            if (RelicPrograms.Entries(player.Relics).Any(e => e.Item2.Kind == RelicProgramKind.IgnorePlayLimit))
                max = Math.Max(max, state.Zone(CardZoneKinds.Hand, actor).Cards.Count);
            if (player.Rules.Flow.EnergyOnly) max = Math.Min(max, 5);
            return minimum ? Math.Min(max, BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.MinPlayCards).Value) : max;
        }

        internal static int DrawLimit(CardDuelState state, ParticipantId actor)
        {
            var p = state.Duel.Player(actor);
            return Math.Min(Math.Max(0, p.Limits.HandLimit.Value - state.Zone(CardZoneKinds.Hand, actor).Cards.Count),
                BattleRuleResolver.Value(p.Rules, p.Relics, BattleRuleValueKey.DrawCount).Value);
        }

        internal static bool RequiresDrawWarning(CardDuelState state, ParticipantId actor)
        {
            var p = state.Duel.Player(actor);
            return state.FlowFor(actor).Window == BattlePlayerWindow.Draw && state.Zone(CardZoneKinds.Hand, actor).Cards.Count >
                BattleRuleResolver.Value(p.Rules, p.Relics, BattleRuleValueKey.DrawWarningThreshold).Value;
        }

        internal static int RequiredDiscards(CardDuelState state, ParticipantId actor) =>
            Math.Max(0, state.Zone(CardZoneKinds.Hand, actor).Cards.Count - state.Duel.Player(actor).Limits.HandLimit.Value);
    }
}
