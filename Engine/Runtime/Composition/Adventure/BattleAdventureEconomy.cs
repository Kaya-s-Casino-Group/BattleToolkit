using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PokerEngine
{
    public enum AdventureLocationRewardKind { Chest, RestHeal, RestHighRelicGain, RestHighRelicLoss, RestLowRelics, RestGold }

    /// <summary>A committed location outcome. Granted IDs include offers waiting for a capacity decision.</summary>
    public sealed class AdventureLocationReward
    {
        public AdventureLocationRewardKind Kind { get; }
        public long HealthDelta { get; }
        public long GoldDelta { get; }
        public IReadOnlyList<string> GrantedRelicIds { get; }
        public IReadOnlyList<string> LostRelicIds { get; }
        internal AdventureLocationReward(AdventureLocationRewardKind kind, long health = 0, long gold = 0,
            IEnumerable<string> granted = null, IEnumerable<string> lost = null)
        {
            Kind = kind; HealthDelta = health; GoldDelta = gold;
            GrantedRelicIds = Array.AsReadOnly((granted ?? Array.Empty<string>()).ToArray());
            LostRelicIds = Array.AsReadOnly((lost ?? Array.Empty<string>()).ToArray());
        }
    }

    public sealed partial class BattleAdventureSession
    {
        /// <summary>Fixed offers for this visit. Purchasing never rerolls or replenishes this list.</summary>
        public IReadOnlyList<AdventureShopItem> ShopItems => Definition.EconomyVersion == 1
            ? Definition.ShopItems : shopItems.AsReadOnly();
        /// <summary>Definition IDs actually obtained at any point during this run, including subsequently lost items.</summary>
        public IReadOnlyList<string> ObtainedRelicIds => obtainedRelicIds.AsReadOnly();
        /// <summary>Resolve the first definition with ClaimRelicReward (optional replaced instance ID) or SkipRelicReward.</summary>
        public IReadOnlyList<string> PendingRelicRewards => pendingRelicRewards.AsReadOnly();
        public AdventureLocationReward LastLocationReward { get; private set; }
        public bool RestLotteryUsed { get; private set; }
        public bool CanDrawRestReward => Definition.EconomyVersion == 2 && Phase == AdventurePhase.Rest &&
            !RestLotteryUsed && pendingRelicRewards.Count == 0;
        private List<AdventureShopItem> shopItems = new List<AdventureShopItem>();
        private List<string> obtainedRelicIds = new List<string>(), pendingRelicRewards = new List<string>();

        private void RecordObtainedRelics()
        {
            foreach (var relic in relics)
                if (!obtainedRelicIds.Contains(relic.DefinitionId)) obtainedRelicIds.Add(relic.DefinitionId);
        }
        private void ResetLocationEconomy()
        {
            shopItems.Clear(); pendingRelicRewards.Clear(); RestLotteryUsed = false; LastLocationReward = null;
        }
        private long EconomicServicePrice(AdventureServicePrice price)
        {
            decimal amount = price.BasePrice;
            int purchaseNumber = checked(ServiceUses(price.Kind) + 1);
            for (int i = 4; i <= purchaseNumber; i++) amount = checked(amount * (i <= 10 ? 1.1m : 1.07m));
            return checked((long)decimal.Round(amount, 0, MidpointRounding.AwayFromZero));
        }
        private void GenerateShopStock()
        {
            var candidates = EligibleRelics(0, 5).ToList();
            for (int i = 0; i < Definition.ShopOfferCount && candidates.Count > 0; i++)
            {
                int index = LocationRandom("shop/item", i, candidates.Count);
                string id = candidates[index]; var definition = Content.Relics[id].Definition;
                long price = definition.Quality == 5 ? 50 + LocationRandom("shop/price", i, 151)
                    : new long[] { 10, 20, 30, 50, 100 }[definition.Quality];
                shopItems.Add(new AdventureShopItem(CurrentNode.Id + "/offer/" + i, id, id, "", price, definition.SellPrice));
                // Unique definitions appear once. Repeatable consumables may fill multiple distinct offer slots.
                if (definition.MaxCopies == 1) candidates.RemoveAt(index);
            }
        }
        private IEnumerable<string> EligibleRelics(int minimumQuality, int maximumQuality)
        {
            return Content.Relics.Values.Select(c => c.Definition)
                .Where(d => d.Quality >= minimumQuality && d.Quality <= maximumQuality &&
                    relics.Count(r => r.DefinitionId == d.Id) + pendingRelicRewards.Count(id => id == d.Id) < d.MaxCopies)
                .Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal);
        }
        private void OpenChest()
        {
            var candidates = EligibleRelics(0, 5).Where(id => !obtainedRelicIds.Contains(id)).ToArray();
            string reward = candidates.Length == 0 ? null : candidates[LocationRandom("chest/item", 0, candidates.Length)];
            Gold = checked(Gold + CurrentNode.Gold); LastRewardGold = CurrentNode.Gold;
            if (reward != null) GrantOrQueueRelic(reward);
            LastLocationReward = new AdventureLocationReward(AdventureLocationRewardKind.Chest, gold: CurrentNode.Gold,
                granted: reward == null ? Array.Empty<string>() : new[] { reward });
        }
        private void EnterRest()
        {
            long amount = Math.Min(MaxHealth - Health, Definition.RestHeal);
            Health = checked(Health + amount);
            LastLocationReward = new AdventureLocationReward(AdventureLocationRewardKind.RestHeal, health: amount);
        }
        private void DrawRestReward()
        {
            Need(CanDrawRestReward, "Rest lottery is unavailable or already used.");
            RestLotteryUsed = true;
            int kind = LocationRandom("rest/kind", 0, 3);
            var gained = new List<string>(); var lost = new List<string>();
            long gold = 0;
            AdventureLocationRewardKind resultKind;
            if (kind == 0)
            {
                bool gain = LocationRandom("rest/high/sign", 0, 2) == 0;
                resultKind = gain ? AdventureLocationRewardKind.RestHighRelicGain : AdventureLocationRewardKind.RestHighRelicLoss;
                if (gain)
                {
                    var candidates = EligibleRelics(3, 4).ToArray();
                    if (candidates.Length > 0)
                    {
                        string id = candidates[LocationRandom("rest/high/gain", 0, candidates.Length)];
                        gained.Add(id); GrantOrQueueRelic(id);
                    }
                }
                else if (relics.Count > 0)
                {
                    var candidates = relics.Where(r => Content.Relics[r.DefinitionId].Definition.Quality >= 3 &&
                        Content.Relics[r.DefinitionId].Definition.Quality <= 4).ToArray();
                    if (candidates.Length == 0)
                    {
                        int highest = relics.Max(r => Content.Relics[r.DefinitionId].Definition.Quality);
                        candidates = relics.Where(r => Content.Relics[r.DefinitionId].Definition.Quality == highest).ToArray();
                    }
                    var removed = candidates[LocationRandom("rest/high/loss", 0, candidates.Length)];
                    relics.Remove(removed); lost.Add(removed.DefinitionId);
                }
            }
            else if (kind == 1)
            {
                resultKind = AdventureLocationRewardKind.RestLowRelics;
                for (int i = 0; i < 2; i++)
                {
                    var candidates = EligibleRelics(0, 2).ToArray();
                    if (candidates.Length == 0) break;
                    string id = candidates[LocationRandom("rest/low/item", i, candidates.Length)];
                    gained.Add(id); GrantOrQueueRelic(id);
                }
            }
            else
            {
                resultKind = AdventureLocationRewardKind.RestGold;
                int amount = LocationRandom("rest/gold/amount", 0, 501);
                gold = LocationRandom("rest/gold/sign", 0, 2) == 0 ? amount : -Math.Min(Gold, amount);
                Gold = checked(Gold + gold);
            }
            LastLocationReward = new AdventureLocationReward(resultKind, gold: gold, granted: gained, lost: lost);
        }
        private bool CanAddRelic(string id)
        {
            if (relics.Count >= Definition.RelicCapacity || relics.Count(r => r.DefinitionId == id) >= Content.Relics[id].Definition.MaxCopies) return false;
            try
            {
                var candidate = new RunRelic(RunId + "/relic/" + entitySequence, id, charges: Content.Relics[id].Definition.Charges);
                new BattleRelicLoadout(PlayerId, relics.Concat(new[] { candidate }).Select(BindRelic));
                return true;
            }
            catch (ArgumentException) { return false; } // Incompatible grants remain skippable instead of blocking node entry.
        }
        private void GrantOrQueueRelic(string id)
        {
            if (CanAddRelic(id)) AddRelic(id);
            else pendingRelicRewards.Add(id);
        }
        private void ResolveRelicReward(AdventureCommand command)
        {
            Need((Phase == AdventurePhase.Chest || Phase == AdventurePhase.Rest) && pendingRelicRewards.Count > 0,
                "There is no pending relic reward.");
            string id = pendingRelicRewards[0];
            if (command.Kind == AdventureCommandKind.ClaimRelicReward)
            {
                if (command.Id != null)
                {
                    var replaced = relics.SingleOrDefault(r => r.Id == command.Id);
                    Need(replaced != null, "Unknown replacement relic.");
                    relics.Remove(replaced);
                }
                AddRelic(id); // Capacity and uniqueness are checked atomically, including after replacement.
            }
            else
            {
                Need(command.Id == null, "Skipping a reward does not select an owned relic.");
                if (LastLocationReward != null)
                {
                    var granted = LastLocationReward.GrantedRelicIds.ToList();
                    granted.Remove(id); // Remove this one offer, preserving other copies of a repeatable reward.
                    LastLocationReward = new AdventureLocationReward(LastLocationReward.Kind, LastLocationReward.HealthDelta,
                        LastLocationReward.GoldDelta, granted, LastLocationReward.LostRelicIds);
                }
            }
            pendingRelicRewards.RemoveAt(0);
        }
        // Purpose-separated, stateless random draws never depend on frame timing, preview calls, or UI visits.
        // Canonical ordinal ordering and explicit byte order make replay identical across .NET/Unity platforms.
        private int LocationRandom(string purpose, int ordinal, int count)
        {
            Need(count > 0, "Cannot draw from an empty pool.");
            ulong limit = ulong.MaxValue - ulong.MaxValue % (ulong)count;
            using (var hash = SHA256.Create())
                for (int retry = 0; ; retry++)
                {
                    var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(Seed.ToString(CultureInfo.InvariantCulture) + "/" +
                        CurrentNode.Id + "/" + purpose + "/" + ordinal.ToString(CultureInfo.InvariantCulture) + "/" + retry.ToString(CultureInfo.InvariantCulture)));
                    ulong value = 0;
                    for (int i = 0; i < 8; i++) value = (value << 8) | bytes[i];
                    if (value < limit) return (int)(value % (ulong)count);
                }
        }
    }
}
