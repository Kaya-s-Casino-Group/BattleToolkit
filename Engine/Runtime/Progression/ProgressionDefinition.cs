using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PokerEngine
{
    /// <summary>Costs to reach levels 1..N. Effects are resolved by the game's new-Run composition policy.</summary>
    public sealed class ProgressionUpgradeDefinition
    {
        public string Id { get; }
        public string CurrencyId { get; }
        public IReadOnlyList<long> LevelCosts { get; }
        public int MaxLevel => LevelCosts.Count;
        public ProgressionUnlock RequiredUnlock { get; }
        public ProgressionUpgradeDefinition(string id, string currencyId, IEnumerable<long> levelCosts, ProgressionUnlock requiredUnlock = null)
        {
            Id = Guard.Text(id, nameof(id)); CurrencyId = Guard.Text(currencyId, nameof(currencyId));
            var costs = Guard.Copy(levelCosts, nameof(levelCosts));
            if (costs.Length == 0 || costs.Length > 1000 || costs.Any(c => c < 0)) throw new ArgumentException("Invalid upgrade costs.");
            LevelCosts = Guard.View(costs); RequiredUnlock = requiredUnlock;
        }
    }

    /// <summary>Versioned meta economy and unlock catalog; separate from flow, battle, and score rules.</summary>
    public sealed class ProgressionDefinition
    {
        public string Version { get; }
        public string Fingerprint { get; }
        public IReadOnlyList<string> CurrencyIds { get; }
        public IReadOnlyList<ProgressionUnlock> Unlocks { get; }
        public IReadOnlyList<ProgressionUnlock> InitialUnlocks { get; }
        public IReadOnlyList<ProgressionUpgradeDefinition> Upgrades { get; }
        public ProgressionDefinition(string version, IEnumerable<string> currencyIds, IEnumerable<ProgressionUnlock> unlocks,
            IEnumerable<ProgressionUpgradeDefinition> upgrades = null, IEnumerable<ProgressionUnlock> initialUnlocks = null)
        {
            Version = Guard.Text(version, nameof(version));
            var currencies = Guard.Copy(currencyIds, nameof(currencyIds)); var allUnlocks = Guard.Copy(unlocks, nameof(unlocks));
            var allUpgrades = Guard.Copy(upgrades ?? new ProgressionUpgradeDefinition[0], nameof(upgrades));
            var initial = Guard.Copy(initialUnlocks ?? new ProgressionUnlock[0], nameof(initialUnlocks));
            if (currencies.Length > 256 || allUpgrades.Length > 1024 || allUnlocks.Length > 100000 ||
                currencies.Any(string.IsNullOrWhiteSpace) || currencies.Distinct(StringComparer.Ordinal).Count() != currencies.Length ||
                allUnlocks.Distinct().Count() != allUnlocks.Length || initial.Distinct().Count() != initial.Length ||
                initial.Any(u => !allUnlocks.Contains(u)) || allUpgrades.Select(u => u.Id).Distinct(StringComparer.Ordinal).Count() != allUpgrades.Length ||
                allUpgrades.Any(u => !currencies.Contains(u.CurrencyId) || u.RequiredUnlock != null && !allUnlocks.Contains(u.RequiredUnlock)))
                throw new ArgumentException("Invalid progression catalog or references.");
            CurrencyIds = Guard.View(currencies); Unlocks = Guard.View(allUnlocks); Upgrades = Guard.View(allUpgrades); InitialUnlocks = Guard.View(initial);
            Fingerprint = ComputeFingerprint();
        }
        public ProgressionUpgradeDefinition Upgrade(string id) => Upgrades.Single(u => u.Id == id);
        private string ComputeFingerprint()
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(Version); writer.Write(CurrencyIds.Count);
                    foreach (var id in CurrencyIds.OrderBy(id => id, StringComparer.Ordinal)) writer.Write(id);
                    foreach (var list in new[] { Unlocks, InitialUnlocks })
                    {
                        writer.Write(list.Count);
                        foreach (var item in list.OrderBy(u => u.Kind).ThenBy(u => u.Id, StringComparer.Ordinal))
                        { writer.Write((int)item.Kind); writer.Write(item.Id); }
                    }
                    writer.Write(Upgrades.Count);
                    foreach (var upgrade in Upgrades.OrderBy(u => u.Id, StringComparer.Ordinal))
                    {
                        writer.Write(upgrade.Id); writer.Write(upgrade.CurrencyId); writer.Write(upgrade.MaxLevel);
                        foreach (var cost in upgrade.LevelCosts) writer.Write(cost);
                        writer.Write(upgrade.RequiredUnlock != null);
                        if (upgrade.RequiredUnlock != null) { writer.Write((int)upgrade.RequiredUnlock.Kind); writer.Write(upgrade.RequiredUnlock.Id); }
                    }
                }
                using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
