using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public sealed partial class CardDuelEngine
    {
        /// <summary>Direct authority correction at a player decision boundary. The host must authorize
        /// this API separately from ordinary player input. One valid batch advances revisions once,
        /// retains RNG/action budgets/history, and invokes no gameplay or revival effects.</summary>
        public CardDuelTransition ApplyAdmin(CardDuelState state, long expectedRevision, BattleAdminPatch patch)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (expectedRevision != state.Revision) return new CardDuelTransition(state, CardDuelError.StaleRevision);
            if (!ReferenceEquals(state.Duel.Rules, Duel.Definition)) return new CardDuelTransition(state, CardDuelError.InvalidState);
            if (!CanApplyAdmin(state)) return new CardDuelTransition(state, CardDuelError.InvalidPhase);
            try
            {
                if (patch == null) throw new ArgumentException("Missing administrator patch.");
                patch.Validate();
                var playerIds = new HashSet<ParticipantId>(state.Duel.Players.Select(p => p.Id));
                if (patch.Zones.Any(z => !playerIds.Contains(z.Owner)) || patch.Relics.Any(r => !playerIds.Contains(r.Owner)) ||
                    patch.Gold.Any(g => !playerIds.Contains(g.Owner))) throw new ArgumentException("Unknown player.");
                var units = state.Duel.Combat.Combatants.ToDictionary(u => u.Id);
                foreach (var edit in patch.Vitals)
                {
                    if (!units.TryGetValue(edit.Unit, out var old)) throw new ArgumentException("Unknown unit.");
                    units[edit.Unit] = new CombatantState(old.Id, old.DefinitionId, old.TeamId,
                        edit.MaxHealth ?? old.MaxHealth, edit.Health ?? old.Health, edit.Block ?? old.Block);
                }
                var players = state.Duel.Players.Select(p =>
                {
                    var edit = patch.Relics.SingleOrDefault(r => r.Owner == p.Id);
                    if (edit == null) return p;
                    var loadout = BuildAdminRelics(state, p, edit);
                    RelicHandEffectValidation.Catalog(loadout, p.Rules, Duel.Definition.StatusCatalog);
                    RelicTriggerValidation.Catalog(loadout, Duel.Definition.StatusCatalog);
                    var limits = new BattleRuleResolver().ResolveLimits(new BattleLimitQuery(p.Rules, loadout));
                    // Changing capacity does not refill AP/energy. Existing excess energy is retained,
                    // as allowed by PlayerTurnState; future normal turn setup uses the new capacity.
                    return new BattlePlayerTurn(p.Id, new PlayerTurnState(limits.MaxEnergy.Value, p.Energy.Energy),
                        p.HasEnded, p.Rules, p.DamageRules, p.TargetRules, loadout, limits);
                }).ToArray();
                var allRelics = players.SelectMany(p => p.Relics.Relics).ToArray();
                if (allRelics.Select(r => r.InstanceId).Distinct(StringComparer.Ordinal).Count() != allRelics.Length)
                    throw new ArgumentException("Duplicate relic identity.");
                var table = BuildAdminCards(state, patch.Zones);
                var gold = state.CopyGold();
                foreach (var edit in patch.Gold) gold[edit.Owner] = edit.Amount;
                var combat = new CombatState(state.Duel.Combat.Id, checked(state.Duel.Combat.Revision + 1),
                    state.Duel.Combat.Combatants.Select(u => units[u.Id]), state.Duel.Combat.Statuses, state.Duel.Combat.RulesVersion);
                // A direct edit is not damage. No on-death, resurrection, loot or battle-ending
                // reaction is run. If both sides are cleared simultaneously, player loss wins.
                var phase = players.All(p => !combat.Get(p.Id).IsAlive) ? ScoredDuelPhase.Lost :
                    Duel.EnemiesDefeated(combat, state.Duel.Enemies) ? ScoredDuelPhase.Won : state.Duel.Phase;
                if (phase == ScoredDuelPhase.AwaitingPlayerHand && !players.Any(p => !p.HasEnded && combat.Get(p.Id).IsAlive))
                    throw new ArgumentException("The edit would remove the last player decision without ending the battle.");
                var team = new TeamBattleState(state.Duel.Rules, combat, players, state.Duel.Enemies, phase,
                    phase == ScoredDuelPhase.Won || phase == ScoredDuelPhase.Lost ? Array.Empty<ParticipantId>() : state.Duel.PendingEnemies,
                    checked(state.Duel.Revision + 1), state.Duel.CompletedRounds, state.Duel.Scores, state.Duel.Actions);
                var next = new CardDuelState(table, team, state.Random,
                    state.Duel.Players.ToDictionary(p => p.Id, p => state.DiscardsFor(p.Id)), state.HasStarted, state.CopyFlows(), gold);
                var timeline = new BattleTimelineRecorder(state, CardDuelAction.AdminEdit, state.Duel.PlayerId, null, null);
                timeline.SetState(next);
                timeline.Add(BattleTimelineKind.AdminEdited, reason: patch.Reason ?? "Administrative state edit");
                return new CardDuelTransition(next, timeline: timeline.Entries);
            }
            catch (OverflowException) { return new CardDuelTransition(state, CardDuelError.Overflow); }
            catch (ArgumentException) { return new CardDuelTransition(state, CardDuelError.InvalidAdminPatch); }
            catch (InvalidOperationException) { return new CardDuelTransition(state, CardDuelError.InvalidAdminPatch); }
        }

        public static bool CanApplyAdmin(CardDuelState state) =>
            state != null && state.HasStarted && !state.Duel.IsFinished && state.Duel.Phase == ScoredDuelPhase.AwaitingPlayerHand &&
            state.Duel.Players.Any(p => state.Duel.HasPlayerWindow(p.Id)) &&
            state.Table.Zones.Where(z => z.Kind == CardZoneKinds.Played).All(z => z.Cards.Count == 0);

        private CardTableState BuildAdminCards(CardDuelState state, IReadOnlyList<BattleAdminZone> edits)
        {
            var cards = state.Table.Cards.ToDictionary(c => c.Id);
            var supplied = new HashSet<CardInstanceId>();
            var changed = new Dictionary<ZoneId, CardInstanceId[]>();
            int freshIndex = 0;
            foreach (var edit in edits)
            {
                var zone = state.Zone(edit.Kind, edit.Owner);
                var result = new List<CardInstanceId>();
                foreach (var input in edit.Cards)
                {
                    CardInstance old = null;
                    CardInstanceId id;
                    if (input.InstanceId != null)
                    {
                        id = new CardInstanceId(input.InstanceId);
                        if (!cards.TryGetValue(id, out old)) throw new ArgumentException("Unknown existing card.");
                    }
                    else
                    {
                        id = new CardInstanceId(state.Table.MatchId + "/admin/" + state.Revision + "/card/" + freshIndex++);
                        if (cards.ContainsKey(id)) throw new ArgumentException("Generated card identity collision.");
                    }
                    if (!supplied.Add(id)) throw new ArgumentException("A card cannot appear in multiple edited zones.");
                    var definition = input.DefinitionId != null ? new CardDefinitionId(input.DefinitionId) : old.DefinitionId;
                    if (!faces.ContainsKey(definition)) throw new ArgumentException("Unknown card face.");
                    cards[id] = new CardInstance(id, definition, old?.OriginId ?? "admin",
                        input.Effects ?? old?.Effects ?? CardEffects.None, input.IsDisabled ?? old?.IsDisabled ?? false);
                    result.Add(id);
                }
                changed.Add(zone.Id, result.ToArray());
            }
            // Explicit destinations move existing identities out of unchanged source zones.
            // Omitted cards in a replaced zone are removed unless listed in another destination.
            var zones = state.Table.Zones.Select(z => new CardZone(z.Id, z.Kind, z.ControllerId,
                changed.TryGetValue(z.Id, out var content) ? content : z.Cards.Where(id => !supplied.Contains(id)))).ToArray();
            if (zones.Where(z => z.ControllerId.HasValue).GroupBy(z => z.ControllerId.Value).Any(g => g.Sum(z => z.Cards.Count) > 512))
                throw new ArgumentException("An owner may have at most 512 cards.");
            var used = new HashSet<CardInstanceId>(zones.SelectMany(z => z.Cards));
            return new CardTableState(state.Table.MatchId, cards.Values.Where(c => used.Contains(c.Id)), zones, checked(state.Revision + 1));
        }

        private BattleRelicLoadout BuildAdminRelics(CardDuelState state, BattlePlayerTurn player, BattleAdminRelics edit)
        {
            var setups = new List<BattleRelicSetup>();
            var runtimes = new List<BattleRelicState>();
            for (int index = 0; index < edit.Items.Count; index++)
            {
                var input = edit.Items[index];
                var old = input.InstanceId == null ? null : player.Relics.Relics.SingleOrDefault(r => r.InstanceId == input.InstanceId);
                if (input.InstanceId != null && (old == null || old.DefinitionId != input.DefinitionId))
                    throw new ArgumentException("Existing relic must keep its identity, owner and definition.");
                BattleRelicContentDefinition content;
                if (!itemCatalog.TryGetValue(input.DefinitionId, out content))
                {
                    if (old == null) throw new ArgumentException("Unknown relic definition.");
                    content = new BattleRelicContentDefinition(player.Relics.Definition(old.InstanceId), player.Relics.RulesFor(old.InstanceId));
                }
                string id = old?.InstanceId ?? state.Table.MatchId + "/admin/" + state.Revision + "/relic/" + player.Id + "/" + index;
                var runtime = new BattleRelicState(id, input.DefinitionId, player.Id, index, input.Level ?? old?.Level ?? 1,
                    input.Growth ?? old?.Growth ?? 0, input.Charges ?? old?.RemainingCharges ?? content.Definition.Charges,
                    input.UsesThisRound ?? old?.UsesThisRound ?? 0, input.CooldownRounds ?? old?.CooldownRounds ?? 0, old?.Memory);
                setups.Add(new BattleRelicSetup(content.Definition, new RunRelic(id, input.DefinitionId, runtime.Level, runtime.Growth,
                    runtime.RemainingCharges, runtime.CooldownRounds, BattleRelicValidation.CopyMemory(runtime.Memory, true)), content.Rules));
                runtimes.Add(runtime);
            }
            return new BattleRelicLoadout(player.Id, setups).WithRuntime(runtimes);
        }
    }
}
