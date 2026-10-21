using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public sealed partial class BattleAdventureSession
    {
        /// <summary>Authority-only atomic correction, recorded for deterministic adventure saves.
        /// Battle card zones are temporary; health, maximum health, gold and owned relics persist.
        /// Map/shop/rest permit only the current player's vitals (without block), gold and relics.</summary>
        public AdventureResult ApplyAdmin(long expectedRevision, BattleAdminPatch patch, bool preview = false)
        {
            if (expectedRevision != Revision) return new AdventureResult(this, "StaleRevision");
            if (patch == null) return new AdventureResult(this, "Missing administrator patch.");
            try { return Apply(AdventureLogEntry.ForAdmin(expectedRevision, patch), preview); }
            catch (ArgumentException error) { return new AdventureResult(this, error.Message); }
        }
        private void ApplyAdministrator(BattleAdminPatch patch)
        {
            LastBattleResult = null;
            Need(patch != null, "Missing administrator patch.");
            patch.Validate();
            if (Phase == AdventurePhase.Battle)
            {
                var result = battle.ApplyAdmin(battle.State.Revision, patch);
                Need(result.Accepted, result.Error.ToString());
                // This is direct state editing. No node rewards, death hooks, charges or cooldown ticks run here.
                LastBattleResult = result;
                var unit = result.State.Duel.Combat.Get(PlayerId);
                Health = unit.Health; MaxHealth = unit.MaxHealth; Gold = result.State.GoldFor(PlayerId);
                relics = result.State.Duel.Player(PlayerId).Relics.ExportRunRelics().ToList();
                ValidateRelics(); RecordObtainedRelics();
                if (result.State.Duel.IsFinished)
                    relics = relics.Select(r => new RunRelic(r.Id, r.DefinitionId, r.Level, r.Growth, r.Charges,
                        r.CooldownRounds, BattleItemRuntime.ClearRoulette(r.Memory).ResetRound())).ToList();
                if (result.State.Duel.Phase == ScoredDuelPhase.Lost) { Phase = AdventurePhase.Failed; Settle(RunOutcome.Lost); }
                else if (result.State.Duel.Phase == ScoredDuelPhase.Won) { Phase = AdventurePhase.Reward; LastRewardGold = 0; }
                return;
            }
            Need(Phase == AdventurePhase.Map || Phase == AdventurePhase.Shop || Phase == AdventurePhase.Rest,
                "Administrator edits require an active player decision.");
            Need(patch.Zones.Count == 0 && patch.Vitals.Count <= 1 && patch.Gold.Count <= 1 && patch.Relics.Count <= 1,
                "Outside battle only the current player's health, gold and relics may be edited.");
            Need(patch.Reason == null || patch.Reason.Length <= 2048 && patch.Reason.IndexOf('\0') < 0, "Invalid administrator reason.");
            Need(patch.Vitals.Count + patch.Gold.Count + patch.Relics.Count > 0, "Empty administrator patch.");
            foreach (var vital in patch.Vitals)
            {
                Need(vital != null && vital.Unit == PlayerId && !vital.Block.HasValue, "Invalid adventure vitals owner or block.");
                long maximum = vital.MaxHealth ?? MaxHealth, health = vital.Health ?? Health;
                Need(maximum > 0 && health >= 0 && health <= maximum, "Invalid adventure health.");
                MaxHealth = maximum; Health = health;
            }
            foreach (var amount in patch.Gold)
            { Need(amount != null && amount.Owner == PlayerId && amount.Amount >= 0, "Invalid adventure gold."); Gold = amount.Amount; }
            foreach (var group in patch.Relics)
            {
                Need(group != null && group.Owner == PlayerId && group.Items != null && group.Items.Count <= Definition.RelicCapacity, "Invalid adventure relic loadout.");
                var before = relics.ToDictionary(r => r.Id, StringComparer.Ordinal);
                var next = new List<RunRelic>();
                for (int i = 0; i < group.Items.Count; i++)
                {
                    var item = group.Items[i];
                    Need(item != null && !string.IsNullOrWhiteSpace(item.DefinitionId) && Content.Relics.ContainsKey(item.DefinitionId), "Unknown administrator relic definition.");
                    RunRelic old = null;
                    if (item.InstanceId != null)
                        Need(before.TryGetValue(item.InstanceId, out old) && old.DefinitionId == item.DefinitionId, "Unknown or changed relic instance.");
                    Need(!item.UsesThisRound.HasValue || item.UsesThisRound == 0, "Round counters require a running battle.");
                    var definition = Content.Relics[item.DefinitionId].Definition;
                    string id = old?.Id ?? RunId + "/relic/admin/" + Revision + "/" + i;
                    next.Add(new RunRelic(id, item.DefinitionId, item.Level ?? old?.Level ?? 1, item.Growth ?? old?.Growth ?? 0,
                        item.Charges ?? old?.Charges ?? definition.Charges, item.CooldownRounds ?? old?.CooldownRounds ?? 0, old?.Memory ?? AbilityMemory.Empty));
                }
                relics = next; ValidateRelics(); RecordObtainedRelics();
            }
            if (Health == 0) { Phase = AdventurePhase.Failed; Settle(RunOutcome.Lost); }
        }
    }
}
