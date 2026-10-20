using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public sealed partial class CardDuelEngine
    {
        // One transaction-local dispatcher shared by opening, manual/effect draws and closing.
        // No mutable state is stored on the engine, so previews are safe to run concurrently.
        private bool ResolveCardBoundary(long revision, CardTableState table, CardZone[] zones, List<BattleDrawRecord> draws,
            BattleTimelineRecorder timeline, CombatTimingKind kind, ParticipantId owner, IReadOnlyList<CardInstanceId> cards, string trigger,
            ref TeamBattleState team, ref RandomState random, List<CombatRuleEvent> events, List<RelicTriggerActivation> activations,
            ref int count, Func<CombatTimingKind, ParticipantId, IReadOnlyList<CardInstanceId>, string, bool> hook)
        {
            if (++count > 256) throw new OverflowException("Card hook recursion limit.");
            if (team.IsFinished) return false;
            var signal = CombatTimingSignal.CardBoundary(kind, table.MatchId + "/cards/" + revision + "/" + count, owner, cards,
                zones.Single(z => z.Kind == CardZoneKinds.Hand && z.ControllerId == owner).Cards.Count, trigger);
            var response = Duel.ResolveBoundary(team, signal);
            if (!response.Accepted) throw new CardHookException(response.Error);
            team = response.State; events.AddRange(response.RuleEvents); activations.AddRange(response.RelicActivations);
            timeline.SetTeam(team); timeline.ObserveCombat(response.RuleEvents, response.RelicActivations);
            foreach (var drawn in response.Events.Where(ev => ev.Effect.Kind == CombatEffectKind.DrawCards)) {
                if (team.IsFinished) break;
                var recipient = team.Players.FirstOrDefault(p => p.Id == drawn.Effect.Target);
                if (recipient == null) throw new CardHookException(CardDuelError.InvalidEffectPlan);
                int held = zones.Single(z => z.Kind == CardZoneKinds.Hand && z.ControllerId == recipient.Id).Cards.Count;
                int cap = recipient.Rules.Flow.EffectDrawOverflow ? 512 : recipient.Limits.HandLimit.Value;
                DealExplicit(table, zones, recipient, (int)Math.Min(drawn.Effect.Amount, Math.Max(0, cap - held)), CardDuelDealTrigger.EffectDraw, ref random, draws, timeline, hook);
            }
            return !team.IsFinished;
        }
        private sealed class CardHookException : Exception
        { internal CardDuelError Error { get; } internal CardHookException(CardDuelError error) { Error = error; } }
        /// <summary>Authority effect API: discard selected hand cards without spending an action or changing the player's window.</summary>
        public CardDuelTransition DiscardByEffect(CardDuelState state, long revision, ParticipantId actor, IEnumerable<CardInstanceId> cards)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (!ReferenceEquals(state.Duel.Rules, Duel.Definition)) return new CardDuelTransition(state, CardDuelError.InvalidState);
            if (revision != state.Revision) return new CardDuelTransition(state, CardDuelError.StaleRevision);
            if (!state.HasStarted || state.Duel.IsFinished || !state.Duel.Players.Any(p => p.Id == actor && state.Duel.Combat.Get(p.Id).IsAlive)) return new CardDuelTransition(state, CardDuelError.InvalidPhase);
            var ids = (cards ?? Array.Empty<CardInstanceId>()).ToArray();
            if (ids.Length == 0 || ids.Distinct().Count() != ids.Length || ids.Any(id => !state.Zone(CardZoneKinds.Hand, actor).Cards.Contains(id) || !state.Table.GetCard(id).CanDiscard)) return new CardDuelTransition(state, CardDuelError.InvalidSelection);
            try {
                var team = state.Duel; var zones = state.Table.Zones.ToArray(); var random = state.Random;
                var draws = new List<BattleDrawRecord>(); var hookEvents = new List<CombatRuleEvent>(); var hookActivations = new List<RelicTriggerActivation>();
                var timeline = new BattleTimelineRecorder(state, CardDuelAction.Discard, actor, ids, null);
                Func<CombatTimingKind, ParticipantId, IReadOnlyList<CardInstanceId>, string, bool> hook = null;
                int hookCount = 0;
                hook = (kind, owner, cards, trigger) => ResolveCardBoundary(state.Revision, state.Table, zones, draws, timeline,
                        kind, owner, cards, trigger, ref team, ref random, hookEvents, hookActivations, ref hookCount, hook);
                Move(zones, actor, CardZoneKinds.Hand, CardZoneKinds.Discard, ids, timeline);
                var discarded = Duel.ResolveAuxiliaryDiscard(team, actor, ids.Select(id => { var card = state.Table.GetCard(id); return new DiscardCardSnapshot(card, new EffectiveCard(id, Face(card).Rank, Face(card).Suit)); }), BattleDiscardKind.Effect);
                if (!discarded.Accepted) return new CardDuelTransition(state, discarded.Error);
                team = discarded.State; timeline.SetTeam(team); timeline.ObserveCombat(discarded.RuleEvents, discarded.RelicActivations);
                foreach (var draw in discarded.Events.Where(ev => ev.Effect.Kind == CombatEffectKind.DrawCards)) {
                    if (team.IsFinished) break;
                    var recipient = team.Players.FirstOrDefault(p => p.Id == draw.Effect.Target);
                    if (recipient == null) throw new CardHookException(CardDuelError.InvalidEffectPlan);
                    int held = zones.Single(z => z.Kind == CardZoneKinds.Hand && z.ControllerId == recipient.Id).Cards.Count;
                    int cap = recipient.Rules.Flow.EffectDrawOverflow ? 512 : recipient.Limits.HandLimit.Value;
                    DealExplicit(state.Table, zones, recipient, (int)Math.Min(draw.Effect.Amount, Math.Max(0, cap - held)), CardDuelDealTrigger.EffectDraw, ref random, draws, timeline, hook);
                }
                var next = new CardDuelState(new CardTableState(state.Table.MatchId, state.Table.Cards, zones, checked(state.Revision + 1)), team, random,
                    team.Players.ToDictionary(p => p.Id, p => state.DiscardsFor(p.Id)), true, state.CopyFlows(), state.CopyGold());
                timeline.SetState(next); timeline.Add(BattleTimelineKind.ActionResolved);
                return CompleteCardCommand(state, new CardDuelTransition(next, events: discarded.RuleEvents.Concat(hookEvents), draws: draws, relicActivations: discarded.RelicActivations.Concat(hookActivations), actionCompletion: discarded.ActionCompletion, timeline: timeline.Entries), actor, CardDuelAction.Discard, ids);
            }
            catch (CardHookException exception) { return new CardDuelTransition(state, exception.Error); }
            catch (OverflowException) { return new CardDuelTransition(state, CardDuelError.Overflow); }
        }
        public int PlayLimit(CardDuelState state, ParticipantId actor, bool minimum)
        {
            var player = state.Duel.Player(actor);
            int max = BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.MaxPlayCards).Value;
            if(RelicPrograms.Entries(player.Relics).Any(e=>e.Item2.Kind==RelicProgramKind.IgnorePlayLimit))max=Math.Max(max,state.Zone(CardZoneKinds.Hand,actor).Cards.Count);
            return minimum ? Math.Min(max, BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.MinPlayCards).Value) : max;
        }
        public int DrawLimit(CardDuelState state, ParticipantId actor)
        {
            var p = state.Duel.Player(actor);
            return Math.Min(Math.Max(0, p.Limits.HandLimit.Value - state.Zone(CardZoneKinds.Hand, actor).Cards.Count),
                BattleRuleResolver.Value(p.Rules, p.Relics, BattleRuleValueKey.DrawCount).Value);
        }
        public bool RequiresDrawWarning(CardDuelState state, ParticipantId actor)
        {
            var p = state.Duel.Player(actor);
            return state.FlowFor(actor).Window == BattlePlayerWindow.Draw && state.Zone(CardZoneKinds.Hand, actor).Cards.Count >
                BattleRuleResolver.Value(p.Rules, p.Relics, BattleRuleValueKey.DrawWarningThreshold).Value;
        }
        public int RequiredDiscards(CardDuelState state, ParticipantId actor) =>
            Math.Max(0, state.Zone(CardZoneKinds.Hand, actor).Cards.Count - state.Duel.Player(actor).Limits.HandLimit.Value);

        private CardDuelTransition ExecuteFlow(CardDuelState state, long revision, ParticipantId actor, CardDuelAction action,
            IEnumerable<CardInstanceId> selected, IEnumerable<ParticipantId> targets, BattleTargetBindings bindings, int drawCount)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (!ReferenceEquals(state.Duel.Rules, Duel.Definition)) return new CardDuelTransition(state, CardDuelError.InvalidState);
            if (revision != state.Revision) return new CardDuelTransition(state, CardDuelError.StaleRevision);
            var player = state.Duel.Players.FirstOrDefault(p => p.Id == actor);
            bool manual = player != null && player.Rules.Flow.UsesTurnFlow;
            if (!manual && action >= CardDuelAction.Draw || action != CardDuelAction.Draw && drawCount != 0)
                return new CardDuelTransition(state, CardDuelError.InvalidPhase);
            var ids = (selected ?? Array.Empty<CardInstanceId>()).ToArray();
            var flows = state.CopyFlows();
            var flow = manual ? flows[actor] : null;
            if (manual && (action == CardDuelAction.Play || action == CardDuelAction.Discard && player.Rules.Flow.DiscardCostsAction && flow.Window == BattlePlayerWindow.Action) && flow.ActionsRemaining == 0)
                return new CardDuelTransition(state, CardDuelError.InsufficientEnergy);
            bool ordinary = action == CardDuelAction.Start || action == CardDuelAction.EnemyAction || action == CardDuelAction.CollectPlayed;
            if (!ordinary && manual)
            {
                if (!state.HasStarted || state.Duel.IsFinished || !state.Duel.HasPlayerWindow(actor) ||
                    state.Zone(CardZoneKinds.Played, actor).Cards.Count != 0)
                    return new CardDuelTransition(state, CardDuelError.InvalidPhase);
                if (flow.Window == BattlePlayerWindow.Draw && action != CardDuelAction.Draw && action != CardDuelAction.SkipDraw && action != CardDuelAction.DiscardForDraw && action != CardDuelAction.EndTurn ||
                    flow.Window == BattlePlayerWindow.ForcedDiscard && action != CardDuelAction.ConfirmOverflow ||
                    flow.Window == BattlePlayerWindow.Ending && action != CardDuelAction.Discard && action != CardDuelAction.FinishTurn && action != CardDuelAction.EndTurn && action != CardDuelAction.SkipPlayerTurn ||
                    flow.Window == BattlePlayerWindow.Action && (action == CardDuelAction.Draw || action == CardDuelAction.SkipDraw || action == CardDuelAction.DiscardForDraw || action == CardDuelAction.ConfirmOverflow || action == CardDuelAction.FinishTurn))
                    return new CardDuelTransition(state, CardDuelError.InvalidPhase);
            }
            bool special = action == CardDuelAction.Draw || action == CardDuelAction.SkipDraw || action == CardDuelAction.DiscardForDraw ||
                action == CardDuelAction.ConfirmOverflow || manual && action == CardDuelAction.EndTurn && flow.Window != BattlePlayerWindow.Ending;
            if (special)
            {
                if (!manual || targets != null && targets.Any() || bindings != null && bindings.Slots.Count > 0)
                    return new CardDuelTransition(state, CardDuelError.InvalidPhase);
                bool discard = action == CardDuelAction.DiscardForDraw || action == CardDuelAction.ConfirmOverflow;
                if (discard ? ids.Length < 1 || ids.Distinct().Count() != ids.Length || ids.Any(id => !state.Zone(CardZoneKinds.Hand, actor).Cards.Contains(id)) : ids.Length != 0)
                    return new CardDuelTransition(state, CardDuelError.InvalidSelection);
                if (action == CardDuelAction.DiscardForDraw && !RequiresDrawWarning(state, actor))
                    return new CardDuelTransition(state, CardDuelError.InvalidPhase);
                if (action == CardDuelAction.ConfirmOverflow && ids.Length != RequiredDiscards(state, actor))
                    return new CardDuelTransition(state, CardDuelError.InvalidSelection);
                if (action == CardDuelAction.Draw && (drawCount < 0 || drawCount > BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.DrawCount).Value || flow.DrawUsesRemaining == 0))
                    return new CardDuelTransition(state, CardDuelError.InvalidSelection);
                if (action == CardDuelAction.Draw && drawCount > 0 && DrawLimit(state, actor) == 0)
                    return new CardDuelTransition(state, CardDuelError.InvalidSelection);
                try
                {
                    var timeline = new BattleTimelineRecorder(state, action, actor, ids, targets);
                    timeline.Add(BattleTimelineKind.ActionStarted);
                    var zones = state.Table.Zones.ToArray(); var random = state.Random; var draws = new List<BattleDrawRecord>();
                    TeamBattleTransition discarded = null; var team = state.Duel;
                    var hookEvents = new List<CombatRuleEvent>(); var hookActivations = new List<RelicTriggerActivation>();
                    Func<CombatTimingKind, ParticipantId, IReadOnlyList<CardInstanceId>, string, bool> hook = null;
                int hookCount = 0;
                hook = (kind, owner, cards, trigger) => ResolveCardBoundary(state.Revision, state.Table, zones, draws, timeline,
                        kind, owner, cards, trigger, ref team, ref random, hookEvents, hookActivations, ref hookCount, hook);
                    if (discard)
                    {
                        Move(zones, actor, CardZoneKinds.Hand, CardZoneKinds.Discard, ids, timeline);
                        timeline.Add(BattleTimelineKind.ResourcesChanged, reason: action == CardDuelAction.DiscardForDraw ? "discard:BeforeDraw" : "discard:Overflow");
                        discarded = Duel.ResolveAuxiliaryDiscard(state.Duel, actor, ids.Select(id => {
                            var card = state.Table.GetCard(id); return new DiscardCardSnapshot(card, new EffectiveCard(id, Face(card).Rank, Face(card).Suit));
                        }), action == CardDuelAction.DiscardForDraw ? BattleDiscardKind.BeforeDraw : BattleDiscardKind.Overflow);
                        if (!discarded.Accepted) return new CardDuelTransition(state, discarded.Error);
                        team = discarded.State; timeline.ObserveCombat(discarded.RuleEvents, discarded.RelicActivations);
                        foreach (var draw in discarded.Events.Where(e => e.Effect.Kind == CombatEffectKind.DrawCards))
                        {
                            var recipient = team.Players.FirstOrDefault(p => p.Id == draw.Effect.Target);
                            if (recipient == null) return new CardDuelTransition(state, CardDuelError.InvalidEffectPlan);
                            int held = zones.Single(z => z.Kind == CardZoneKinds.Hand && z.ControllerId == recipient.Id).Cards.Count;
                            int cap = recipient.Rules.Flow.EffectDrawOverflow ? 512 : recipient.Limits.HandLimit.Value;
                            DealExplicit(state.Table, zones, recipient, (int)Math.Min(draw.Effect.Amount, Math.Max(0, cap - held)), CardDuelDealTrigger.EffectDraw, ref random, draws, timeline, hook);
                        }
                    }
                    if (action == CardDuelAction.Draw || action == CardDuelAction.SkipDraw)
                    {
                        int used = action == CardDuelAction.SkipDraw || drawCount == 0 ? flow.DrawUsesRemaining : 1;
                        if (drawCount > 0 && action == CardDuelAction.Draw)
                            DealExplicit(state.Table, zones, player, Math.Min(drawCount, DrawLimit(state, actor)), CardDuelDealTrigger.ManualDraw, ref random, draws, timeline, hook);
                        int remaining = flow.DrawUsesRemaining - used;
                        flows[actor] = new BattlePlayerFlow(remaining > 0 ? BattlePlayerWindow.Draw : BattlePlayerWindow.Action, remaining, actionsRemaining: flow.ActionsRemaining);
                    }
                    else if (action == CardDuelAction.EndTurn)
                    {
                        hook(CombatTimingKind.PlayPhaseEnd, actor, Array.Empty<CardInstanceId>(), null);
                        flows[actor] = flow.With(player.Rules.Flow.ForceDiscardOverflow && zones.Single(z => z.Kind == CardZoneKinds.Hand && z.ControllerId == actor).Cards.Count > player.Limits.HandLimit.Value ? BattlePlayerWindow.ForcedDiscard : BattlePlayerWindow.Ending);
                    }
                    else if (action == CardDuelAction.ConfirmOverflow) flows[actor] = flow.With(
                        zones.Single(z => z.Kind == CardZoneKinds.Hand && z.ControllerId == actor).Cards.Count > player.Limits.HandLimit.Value ? BattlePlayerWindow.ForcedDiscard : BattlePlayerWindow.Ending);
                    var table = new CardTableState(state.Table.MatchId, state.Table.Cards, zones, checked(state.Revision + 1));
                    var result = new CardDuelState(table, team, random, state.Duel.Players.ToDictionary(p => p.Id, p => state.DiscardsFor(p.Id)), true, flows, state.CopyGold());
                    timeline.SetState(result); timeline.Add(BattleTimelineKind.ActionResolved);
                    return new CardDuelTransition(result, events: (discarded?.RuleEvents ?? Array.Empty<CombatRuleEvent>()).Concat(hookEvents), draws: draws, relicActivations: (discarded?.RelicActivations ?? Array.Empty<RelicTriggerActivation>()).Concat(hookActivations), actionCompletion: discarded?.ActionCompletion, timeline: timeline.Entries);
                }
                catch (CardHookException exception) { return new CardDuelTransition(state, exception.Error); }
                catch (OverflowException) { return new CardDuelTransition(state, CardDuelError.Overflow); }
            }
            if (manual && action == CardDuelAction.Discard && (flow.DefenseUsed && BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.DiscardEndsTurn).Value > 0 || flow.Window == BattlePlayerWindow.Ending && !player.Rules.Flow.EndDefense))
                return new CardDuelTransition(state, CardDuelError.InvalidPhase);
            bool freeDiscard = manual && action == CardDuelAction.Discard && (!player.Rules.Flow.DiscardCostsAction || flow.Window == BattlePlayerWindow.Ending);
            var coreAction = action == CardDuelAction.FinishTurn ? CardDuelAction.EndTurn : action;
            if (manual && coreAction == CardDuelAction.EndTurn && state.Duel.IsActionBlocked(actor)) coreAction = CardDuelAction.SkipPlayerTurn;
            var resolved = ExecuteCore(state, revision, actor, coreAction, ids, targets, bindings, freeDiscard);
            if (!resolved.Accepted) return resolved;
            var next = resolved.State;
            if (next.Duel.CompletedRounds != state.Duel.CompletedRounds && !next.Duel.IsFinished)
                foreach (var p in next.Duel.Players) flows[p.Id] = CardDuelState.NewFlow(p);
            else if (manual && !next.Duel.IsFinished)
            {
                int remaining = flow.ActionsRemaining - (action == CardDuelAction.Play || action == CardDuelAction.Discard && player.Rules.Flow.DiscardCostsAction && flow.Window == BattlePlayerWindow.Action ? 1 : 0);
                flow = flow.With(flow.Window, actions: remaining);
                if (action == CardDuelAction.Discard)
                    flows[actor] = flow.With((remaining == 0 || BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.DiscardEndsTurn).Value > 0) ? BattlePlayerWindow.Ending : BattlePlayerWindow.Action, true, BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.DiscardEndsTurn).Value > 0);
                else if (action == CardDuelAction.Play && remaining == 0)
                    flows[actor] = flow.With(BattlePlayerWindow.Ending);
                else flows[actor] = flow;
            }
            if (manual && !next.Duel.IsFinished && flow.Window == BattlePlayerWindow.Action && flows[actor].Window == BattlePlayerWindow.Ending)
            {
                var closing = ClosePlayPhase(next, actor);
                if (!closing.Accepted) return new CardDuelTransition(state, closing.Error);
                next = closing.State;
                resolved = new CardDuelTransition(next, score: resolved.Score, events: resolved.RuleEvents.Concat(closing.RuleEvents), pattern: resolved.Pattern, damage: resolved.Damage,
                    discardReward: resolved.DiscardReward, effectPlan: resolved.EffectPlan, actionCost: resolved.ActionCost, draws: resolved.Draws.Concat(closing.Draws),
                    relicActivations: resolved.RelicActivations.Concat(closing.RelicActivations), actionCompletion: resolved.ActionCompletion, timeline: resolved.Timeline.Concat(closing.Timeline));
            }
            if (manual && player.Rules.Flow.ForceDiscardOverflow && flows[actor].Window == BattlePlayerWindow.Ending && RequiredDiscards(next, actor) > 0)
                flows[actor] = flows[actor].With(BattlePlayerWindow.ForcedDiscard);
            next = new CardDuelState(next.Table, next.Duel, next.Random, next.Duel.Players.ToDictionary(p => p.Id, p => next.DiscardsFor(p.Id)), next.HasStarted, flows, next.CopyGold());
            return CopyTransition(resolved, next);
        }
        private CardDuelTransition ClosePlayPhase(CardDuelState state, ParticipantId actor)
        {
            try {
                var team = state.Duel; var zones = state.Table.Zones.ToArray(); var random = state.Random;
                var draws = new List<BattleDrawRecord>(); var hookEvents = new List<CombatRuleEvent>(); var hookActivations = new List<RelicTriggerActivation>();
                var timeline = new BattleTimelineRecorder(state, CardDuelAction.EndTurn, actor, Array.Empty<CardInstanceId>(), null);
                Func<CombatTimingKind, ParticipantId, IReadOnlyList<CardInstanceId>, string, bool> hook = null;
                int hookCount = 0;
                hook = (kind, owner, cards, trigger) => ResolveCardBoundary(state.Revision, state.Table, zones, draws, timeline,
                        kind, owner, cards, trigger, ref team, ref random, hookEvents, hookActivations, ref hookCount, hook);
                hook(CombatTimingKind.PlayPhaseEnd, actor, Array.Empty<CardInstanceId>(), null);
                var next = new CardDuelState(new CardTableState(state.Table.MatchId, state.Table.Cards, zones, state.Revision), team, random,
                    team.Players.ToDictionary(p => p.Id, p => state.DiscardsFor(p.Id)), true, state.CopyFlows(), state.CopyGold());
                return new CardDuelTransition(next, events: hookEvents, draws: draws, relicActivations: hookActivations, timeline: timeline.Entries);
            }
            catch (CardHookException exception) { return new CardDuelTransition(state, exception.Error); }
            catch (OverflowException) { return new CardDuelTransition(state, CardDuelError.Overflow); }
        }
        private static CardDuelTransition CopyTransition(CardDuelTransition t, CardDuelState next) =>
            new CardDuelTransition(next, t.Error, t.Score, t.RuleEvents, t.Pattern, t.Damage, t.DiscardReward, t.EffectPlan,
                t.ActionCost, t.Draws, t.RelicActivations, t.ActionCompletion,
                t.Timeline.Select((entry, index) => index == t.Timeline.Count - 1 ? entry.WithState(next) : entry));

        private void DealExplicit(CardTableState before, CardZone[] zones, BattlePlayerTurn player, int count, CardDuelDealTrigger trigger,
            ref RandomState random, List<BattleDrawRecord> draws, BattleTimelineRecorder timeline, Func<CombatTimingKind, ParticipantId, IReadOnlyList<CardInstanceId>, string, bool> hook = null)
        {
            var context = new CardDuelDealContext(trigger, before, player.Limits.HandLimit.Value, player.Id);
            var plan = new BattleDealResolution(context, count, BattleRefillMode.FixedCount, count, 0, count, Array.Empty<BattleRefillContribution>());
            int remaining = count;
            int d = Array.FindIndex(zones, z => z.Kind == CardZoneKinds.Deck && z.ControllerId == player.Id);
            int h = Array.FindIndex(zones, z => z.Kind == CardZoneKinds.Hand && z.ControllerId == player.Id);
            int x = Array.FindIndex(zones, z => z.Kind == CardZoneKinds.Discard && z.ControllerId == player.Id);
            if (remaining > 0 && hook?.Invoke(CombatTimingKind.BeforeDraw, player.Id, Array.Empty<CardInstanceId>(), trigger.ToString()) == false) remaining = 0;
            var dealt = new List<CardInstanceId>();
            while (remaining > 0)
            {
                if (zones[d].Cards.Count == 0)
                {
                    if (zones[x].Cards.Count == 0) break;
                    var shuffled = shuffler.Shuffle(zones[x].Cards, random); random = shuffled.Random;
                    zones[d] = new CardZone(zones[d].Id, zones[d].Kind, player.Id, shuffled.Cards);
                    zones[x] = new CardZone(zones[x].Id, zones[x].Kind, player.Id, Array.Empty<CardInstanceId>());
                    timeline.SetCards(new CardTableState(before.MatchId, before.Cards, zones, before.Revision), random);
                    timeline.Add(BattleTimelineKind.DeckRecycled, cards: shuffled.Cards, from: CardZoneKinds.Discard, to: CardZoneKinds.Deck, cardOwner: player.Id, trigger: trigger);
                    if (hook?.Invoke(CombatTimingKind.Reshuffle, player.Id, shuffled.Cards, trigger.ToString()) == false) break;
                }
                int amount = Math.Min(remaining, zones[d].Cards.Count);
                var transfer = zones[d].DrawTo(zones[h], amount, zones[h].Cards.Count); zones[d] = transfer.Source; zones[h] = transfer.Target;
                timeline.SetCards(new CardTableState(before.MatchId, before.Cards, zones, before.Revision), random);
                timeline.Add(BattleTimelineKind.CardsDealt, cards: transfer.Cards, from: CardZoneKinds.Deck, to: CardZoneKinds.Hand, cardOwner: player.Id, trigger: trigger);
                dealt.AddRange(transfer.Cards);
                remaining -= amount;
            }
            if (dealt.Count > 0) hook?.Invoke(CombatTimingKind.Draw, player.Id, dealt.AsReadOnly(), trigger.ToString());
            draws.Add(new BattleDrawRecord(plan, dealt.Count));
        }
    }
}
