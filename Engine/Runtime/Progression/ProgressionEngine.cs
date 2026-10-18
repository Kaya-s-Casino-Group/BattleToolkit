using System;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Immutable meta state transitions. Caller authenticates the player and resolves terminal Run rewards.</summary>
    public sealed class ProgressionEngine
    {
        private readonly ProgressionDefinition definition;
        public ProgressionEngine(ProgressionDefinition definition)
        { this.definition = definition ?? throw new ArgumentNullException(nameof(definition)); }
        public PlayerProgressionState Create(ParticipantId player) => new PlayerProgressionState(player, 0, definition.Version, definition.Fingerprint,
            definition.CurrencyIds.Select(id => new ProgressionCurrency(id, 0)), definition.InitialUnlocks,
            definition.Upgrades.Select(u => new ProgressionUpgradeLevel(u.Id, 0)), new RunSettlement[0]);

        public PlayerProgressionState Settle(PlayerProgressionState state, RunSettlement settlement, long expectedRevision, ParticipantId authenticatedPlayer)
        {
            Authorize(state, authenticatedPlayer); ProgressionValidator.ValidateSettlement(settlement, state.PlayerId, definition);
            var previous = state.Settlements.SingleOrDefault(s => s.RunId == settlement.RunId);
            if (previous != null)
            {
                if (!previous.SameAs(settlement)) throw new InvalidOperationException("Conflicting settlement for the same Run.");
                return state; // A successful retry remains harmless even with its original revision.
            }
            CheckRevision(state, expectedRevision);
            if (state.Settlements.Count >= ProgressionValidator.MaxSettlements) throw new InvalidOperationException("Settlement ledger is full.");
            var balances = state.Currencies.ToDictionary(c => c.Id, c => c.Amount, StringComparer.Ordinal);
            foreach (var grant in settlement.Reward.Currencies) balances[grant.Id] = checked(balances[grant.Id] + grant.Amount);
            var next = new PlayerProgressionState(state.PlayerId, checked(state.Revision + 1), definition.Version, definition.Fingerprint,
                definition.CurrencyIds.Select(id => new ProgressionCurrency(id, balances[id])), state.Unlocks.Union(settlement.Reward.Unlocks),
                state.Upgrades, state.Settlements.Concat(new[] { settlement }));
            ProgressionValidator.Validate(next, definition); return next;
        }
        public PlayerProgressionState PurchaseUpgrade(PlayerProgressionState state, string upgradeId, long expectedRevision, ParticipantId authenticatedPlayer)
        {
            Authorize(state, authenticatedPlayer); CheckRevision(state, expectedRevision);
            var upgrade = definition.Upgrades.SingleOrDefault(u => u.Id == upgradeId);
            if (upgrade == null) throw new ArgumentException("Unknown progression upgrade.", nameof(upgradeId));
            int level = state.UpgradeLevel(upgradeId);
            if (level >= upgrade.MaxLevel || upgrade.RequiredUnlock != null && !state.IsUnlocked(upgrade.RequiredUnlock))
                throw new InvalidOperationException("Upgrade is capped or locked.");
            long cost = upgrade.LevelCosts[level];
            if (state.Balance(upgrade.CurrencyId) < cost) throw new InvalidOperationException("Insufficient progression currency.");
            var next = new PlayerProgressionState(state.PlayerId, checked(state.Revision + 1), definition.Version, definition.Fingerprint,
                state.Currencies.Select(c => new ProgressionCurrency(c.Id, c.Amount - (c.Id == upgrade.CurrencyId ? cost : 0))), state.Unlocks,
                state.Upgrades.Select(u => new ProgressionUpgradeLevel(u.Id, u.Level + (u.Id == upgradeId ? 1 : 0))), state.Settlements);
            ProgressionValidator.Validate(next, definition); return next;
        }
        private void Authorize(PlayerProgressionState state, ParticipantId authenticatedPlayer)
        {
            ProgressionValidator.Validate(state, definition);
            if (!authenticatedPlayer.IsValid || state.PlayerId != authenticatedPlayer) throw new InvalidOperationException("Wrong progression owner.");
        }
        private static void CheckRevision(PlayerProgressionState state, long expectedRevision)
        { if (state.Revision != expectedRevision) throw new InvalidOperationException("Stale progression revision."); }
    }
}
