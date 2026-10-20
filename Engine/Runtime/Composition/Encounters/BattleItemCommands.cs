using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public sealed partial class CardDuelEngine
    {
        public CardDuelTransition UseItem(CardDuelState state, long revision, ParticipantId actor, string instanceId, string targetInstanceId = null)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (revision != state.Revision) return new CardDuelTransition(state, CardDuelError.StaleRevision);
            if (!ReferenceEquals(state.Duel.Rules, Duel.Definition)) return new CardDuelTransition(state, CardDuelError.InvalidState);
            if (!BattleItemRuntime.CanUse(state, actor, instanceId)) return new CardDuelTransition(state, CardDuelError.InvalidPhase);
            try {
                var player = state.Duel.Player(actor); var loadout = player.Relics; var relic = loadout.Relics.Single(r => r.InstanceId == instanceId);
                var spec = loadout.RulesFor(instanceId).Item; var random = new BattleItemResolution(state.Random, state.Duel);
                var effects = new List<CombatEffect>();
                if (spec.Activation == BattleItemActivation.ReplaceHeld) {
                    var target = loadout.Relics.FirstOrDefault(r => r.InstanceId == targetInstanceId && r.InstanceId != instanceId);
                    if (target == null) return new CardDuelTransition(state, CardDuelError.InvalidSelection);
                    var candidates = itemCatalog.Values.Where(c => c.Definition.Id != target.DefinitionId && c.Definition.Id != relic.DefinitionId &&
                        c.Rules.Item?.Activation != BattleItemActivation.ReplaceHeld &&
                        loadout.Relics.Count(r => r.DefinitionId == c.Definition.Id) < c.Definition.MaxCopies).OrderBy(c => c.Definition.Id, StringComparer.Ordinal).ToArray();
                    if (candidates.Length == 0) return new CardDuelTransition(state, CardDuelError.InvalidSelection);
                    var result = candidates[random.Roll(candidates.Length)];
                    string newId = state.Table.MatchId + "/item/" + revision + "/" + target.Order;
                    loadout = loadout.ReplaceItem(target.InstanceId, new BattleRelicSetup(result.Definition,
                        new RunRelic(newId, result.Definition.Id, charges: result.Definition.Charges), result.Rules));
                } else if (targetInstanceId != null) return new CardDuelTransition(state, CardDuelError.InvalidSelection);
                if (spec.Activation == BattleItemActivation.Roulette) {
                    int bomb = random.Roll(spec.RouletteTurns) + 1;
                    var memory = relic.Memory.WithCounter(BattleItemRuntime.RouletteActiveKey, 1, MemoryScope.Encounter)
                        .WithCounter(BattleItemRuntime.RouletteTurnKey, 1, MemoryScope.Encounter)
                        .WithCounter(BattleItemRuntime.RouletteBombKey, bomb, MemoryScope.Encounter)
                        .WithCounter(BattleItemRuntime.RouletteRoundKey, state.Duel.CompletedRounds, MemoryScope.Encounter);
                    if (bomb == 1) { memory = BattleItemRuntime.ClearRoulette(memory); effects.Add(new CombatEffect(CombatEffectKind.Damage, actor, actor, spec.ExplosionDamage)); }
                    loadout = loadout.WithRuntime(loadout.Relics.Select(r => r.InstanceId == instanceId ? r.WithRuntime(r.RemainingCharges, r.UsesThisRound, r.CooldownRounds, memory) : r));
                }
                if (spec.Activation == BattleItemActivation.Effects)
                    foreach (var effect in spec.Effects)
                        foreach (var target in CombatTargetResolver.Resolve(state.Duel.Combat, actor, effect.Target))
                            effects.Add(new CombatEffect(effect.Effect.Kind, actor, target, effect.Effect.Amount));
                loadout = BattleItemRuntime.Pay(loadout, instanceId, spec);
                var draft = ReplaceItemLoadout(state.Duel, actor, loadout);
                var resolved = Duel.ResolveItemEffects(draft, actor, effects, random.Random);
                if (!resolved.Accepted) return new CardDuelTransition(state, resolved.Error);
                var table = new CardTableState(state.Table.MatchId, state.Table.Cards, state.Table.Zones, checked(state.Revision + 1));
                var next = new CardDuelState(table, resolved.State, resolved.ItemRandom ?? random.Random,
                    state.Duel.Players.ToDictionary(p => p.Id, p => state.DiscardsFor(p.Id)), true, state.CopyFlows(), state.CopyGold());
                var timeline = new BattleTimelineRecorder(state, CardDuelAction.UseItem, actor, Array.Empty<CardInstanceId>(), null);
                timeline.Add(BattleTimelineKind.ActionStarted, reason: instanceId); timeline.ObserveCombat(resolved.RuleEvents); timeline.SetState(next);
                timeline.Add(BattleTimelineKind.ActionResolved, reason: instanceId);
                if (next.Duel.IsFinished) timeline.Add(BattleTimelineKind.BattleEnded);
                return CompleteCardCommand(state, new CardDuelTransition(next, events: resolved.RuleEvents, timeline: timeline.Entries), actor, CardDuelAction.UseItem);
            } catch (OverflowException) { return new CardDuelTransition(state, CardDuelError.Overflow); }
              catch (ArgumentException) { return new CardDuelTransition(state, CardDuelError.InvalidSelection); }
        }
        private static TeamBattleState ReplaceItemLoadout(TeamBattleState state, ParticipantId actor, BattleRelicLoadout loadout) =>
            new TeamBattleState(state.Rules, state.Combat, state.Players.Select(p => p.Id != actor ? p : new BattlePlayerTurn(p.Id, p.Energy, p.HasEnded,
                p.Rules, p.DamageRules, p.TargetRules, loadout, new BattleRuleResolver().ResolveLimits(new BattleLimitQuery(p.Rules, loadout)))),
                state.Enemies, state.Phase, state.PendingEnemies, state.Revision, state.CompletedRounds, state.Scores, state.Actions);
    }

    public sealed partial class TeamBattleEngine
    {
        internal TeamBattleTransition ResolveItemEffects(TeamBattleState state, ParticipantId actor, IEnumerable<CombatEffect> effects, RandomState random = null)
        {
            var runtime = RelicReactionRuntime.Create(state, actor, ScoredDuelActionKind.EndPlayerTurn, null, null, random);
            var list = effects.ToArray(); var combat = state.Combat; var events = new List<CombatRuleEvent>();
            if (list.Length > 0) {
                var resolved = CombatEngine.Resolve(combat, combat.Revision, list, new ActorIncomingDamageModifier(state),
                    statusCatalog: Definition.StatusCatalog, reactionProvider: Definition.ReactionProvider, reactionLimits: Definition.ReactionLimits,
                    amountModifier: new RelicCombatAmountModifier(state), reactionRuntime: runtime);
                if (!resolved.Accepted) return new TeamBattleTransition(state, EffectFailure(resolved.Error));
                runtime = (RelicReactionRuntime)resolved.ReactionRuntime; combat = resolved.State; events.AddRange(resolved.RuleEvents);
            }
            var players = ApplyRuntime(state.Players, runtime);
            var phase = EnemiesDefeated(combat, state.Enemies) ? ScoredDuelPhase.Won : players.All(p => !combat.Get(p.Id).IsAlive) ? ScoredDuelPhase.Lost : state.Phase;
            if (phase == ScoredDuelPhase.Won || phase == ScoredDuelPhase.Lost) players = players.Select(p => new BattlePlayerTurn(p.Id,p.Energy,p.HasEnded,p.Rules,p.DamageRules,p.TargetRules,BattleItemRuntime.EndRoulette(p.Relics),p.Limits)).ToArray();
            var next = new TeamBattleState(state.Rules, combat, players, state.Enemies, phase, phase == ScoredDuelPhase.Won || phase == ScoredDuelPhase.Lost ? Array.Empty<ParticipantId>() : state.PendingEnemies,
                checked(state.Revision + 1), state.CompletedRounds, state.Scores, state.Actions);
            return new TeamBattleTransition(next, events: events, itemRandom: runtime?.ItemRandom ?? random);
        }
        private TeamBattleTransition ResolveItemBoundary(TeamBattleState state, CombatTimingSignal signal)
        {
            var draft = state; var events = new List<CombatRuleEvent>();
            foreach (var owner in state.Players) {
                var loadout = draft.Player(owner.Id).Relics;
                if (signal.Kind == CombatTimingKind.BattleEnded) loadout = BattleItemRuntime.EndRoulette(loadout);
                var effects = new List<CombatEffect>();
                if (signal.Kind == CombatTimingKind.TurnStarted && signal.Participants.Contains(owner.Id)) {
                    loadout = loadout.WithRuntime(loadout.Relics.Select(r => {
                        if (r.Memory.CounterValue(BattleItemRuntime.RouletteActiveKey) == 0 || r.Memory.CounterValue(BattleItemRuntime.RouletteRoundKey) >= state.CompletedRounds) return r;
                        var spec = loadout.RulesFor(r.InstanceId).Item; long turn = r.Memory.CounterValue(BattleItemRuntime.RouletteTurnKey) + 1;
                        var memory = r.Memory.WithCounter(BattleItemRuntime.RouletteTurnKey, turn, MemoryScope.Encounter).WithCounter(BattleItemRuntime.RouletteRoundKey, state.CompletedRounds, MemoryScope.Encounter);
                        if (turn >= memory.CounterValue(BattleItemRuntime.RouletteBombKey)) { effects.Add(new CombatEffect(CombatEffectKind.Damage, owner.Id, owner.Id, spec.ExplosionDamage)); memory = BattleItemRuntime.ClearRoulette(memory); }
                        return r.WithRuntime(r.RemainingCharges,r.UsesThisRound,r.CooldownRounds,memory);
                    }));
                }
                draft = WindowState(draft,draft.Combat,draft.Players.Select(p => p.Id != owner.Id ? p : new BattlePlayerTurn(p.Id,p.Energy,p.HasEnded,p.Rules,p.DamageRules,p.TargetRules,loadout,p.Limits)),draft.Phase,draft.CompletedRounds,draft.PendingEnemies);
                if (effects.Count == 0) continue;
                var settled = ResolveItemEffects(draft,owner.Id,effects); if (!settled.Accepted) return settled;
                draft = settled.State; events.AddRange(settled.RuleEvents);
            }
            return new TeamBattleTransition(draft,events:events);
        }
    }
}
