using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public static class ProgressionValidator
    {
        public const int MaxSettlements = 100000;
        private static void Need(bool condition, string path)
        { if (!condition) throw new StateValidationException("InvalidProgression", path); }
        public static void Validate(PlayerProgressionState state, ProgressionDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            try { Check(state, definition); }
            catch (NullReferenceException) { throw new StateValidationException("MissingProgressionData", "Progression"); }
            catch (ArgumentException) { throw new StateValidationException("InvalidProgressionData", "Progression"); }
            catch (OverflowException) { throw new StateValidationException("ProgressionOverflow", "Progression"); }
        }
        public static void ValidateSettlement(RunSettlement settlement, ParticipantId player, ProgressionDefinition definition)
        {
            if (settlement == null) throw new ArgumentNullException(nameof(settlement));
            new RunSettlement(settlement.RunId, settlement.PlayerId, settlement.Outcome, settlement.ModeId,
                settlement.RunRulesVersion, settlement.StartingDeckVersion, settlement.TotalScore,
                settlement.CompletedFloors, settlement.TotalFloors, settlement.Reward);
            Need(settlement.PlayerId == player, "Settlement.PlayerId");
            new ProgressionReward(settlement.Reward.Currencies, settlement.Reward.Unlocks);
            foreach (var currency in settlement.Reward.Currencies)
            { new ProgressionCurrency(currency.Id, currency.Amount); Need(definition.CurrencyIds.Contains(currency.Id), "Reward.CurrencyId"); }
            foreach (var unlock in settlement.Reward.Unlocks)
            { new ProgressionUnlock(unlock.Kind, unlock.Id); Need(definition.Unlocks.Contains(unlock), "Reward.UnlockId"); }
        }
        private static void Check(PlayerProgressionState state, ProgressionDefinition definition)
        {
            Need(state != null && state.PlayerId.IsValid && state.Revision >= 0, "Header");
            Need(state.RulesVersion == definition.Version && state.RulesFingerprint == definition.Fingerprint, "Rules");
            Need(state.Currencies.Count == definition.CurrencyIds.Count && state.Upgrades.Count == definition.Upgrades.Count &&
                state.Unlocks.Count <= definition.Unlocks.Count && state.Settlements.Count <= MaxSettlements, "Collections");
            var expectedBalances = definition.CurrencyIds.ToDictionary(id => id, id => 0L, StringComparer.Ordinal);
            var expectedUnlocks = new HashSet<ProgressionUnlock>(definition.InitialUnlocks);
            var settledIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var settlement in state.Settlements)
            {
                ValidateSettlement(settlement, state.PlayerId, definition); Need(settledIds.Add(settlement.RunId), "Settlements.Duplicate");
                foreach (var currency in settlement.Reward.Currencies) expectedBalances[currency.Id] = checked(expectedBalances[currency.Id] + currency.Amount);
                expectedUnlocks.UnionWith(settlement.Reward.Unlocks);
            }
            long expectedRevision = state.Settlements.Count;
            var upgradeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var level in state.Upgrades)
            {
                Need(level != null && upgradeIds.Add(level.Id), "Upgrades.Identity");
                var upgrade = definition.Upgrades.SingleOrDefault(u => u.Id == level.Id);
                Need(upgrade != null && level.Level >= 0 && level.Level <= upgrade.MaxLevel, "Upgrades.Level");
                Need(level.Level == 0 || upgrade.RequiredUnlock == null || expectedUnlocks.Contains(upgrade.RequiredUnlock), "Upgrades.Unlock");
                expectedRevision = checked(expectedRevision + level.Level);
                foreach (var cost in upgrade.LevelCosts.Take(level.Level)) expectedBalances[upgrade.CurrencyId] = checked(expectedBalances[upgrade.CurrencyId] - cost);
            }
            Need(state.Revision == expectedRevision, "Revision");
            Need(state.Unlocks.Distinct().Count() == state.Unlocks.Count && expectedUnlocks.SetEquals(state.Unlocks), "Unlocks");
            var walletIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var currency in state.Currencies)
                Need(currency != null && walletIds.Add(currency.Id) && expectedBalances.ContainsKey(currency.Id) && currency.Amount >= 0 &&
                    expectedBalances[currency.Id] == currency.Amount, "Currencies.Balance");
        }
    }
}
