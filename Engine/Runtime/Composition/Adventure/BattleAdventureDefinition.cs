using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public enum AdventurePhase { Map, Battle, Reward, Shop, Rest, Completed, Failed, Chest }
    public enum AdventureNodeKind { Battle, Elite, Boss, Shop, Rest, Chest }
    public enum AdventureCommandKind { EnterNode, Continue, BuyRelic, SellRelic, DeleteCard, DuplicateCard, ChangeSuit, ChooseRest, Abandon, DrawRestReward, ClaimRelicReward, SkipRelicReward }
    public enum AdventureRestKind { Heal, Gold, Relic }

    public sealed class AdventureCommand
    {
        public AdventureCommandKind Kind { get; }
        public string Id { get; }
        public Suit? Suit { get; }
        public AdventureCommand(AdventureCommandKind kind, string id = null, Suit? suit = null)
        { Kind = kind; Id = id; Suit = suit; }
    }
    public sealed class AdventureNodeDefinition
    {
        public string Id { get; }
        public string Name { get; }
        public AdventureNodeKind Kind { get; }
        public int Chapter { get; }
        public int Row { get; }
        public int Column { get; }
        public string EncounterId { get; }
        public long Gold { get; }
        public decimal EnemyHealthMultiplier { get; }
        public IReadOnlyList<string> NextIds { get; }
        internal AdventureNodeDefinition(string id, string name, AdventureNodeKind kind, int chapter, int row, int column,
            string encounterId, long gold, IEnumerable<string> nextIds, decimal enemyHealthMultiplier = 1)
        { Id = id; Name = name; Kind = kind; Chapter = chapter; Row = row; Column = column; EncounterId = encounterId; Gold = gold; EnemyHealthMultiplier = enemyHealthMultiplier; NextIds = Array.AsReadOnly(nextIds.ToArray()); }
    }
    public sealed class AdventureTarotDefinition
    {
        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public bool InitiallyUnlocked { get; }
        public bool UnlockOnVictory { get; }
        public decimal MaxHealthMultiplier { get; }
        public long StartingGoldBonus { get; }
        public string StartingRelicId { get; }
        internal AdventureTarotDefinition(string id, string name, string description, bool initiallyUnlocked, bool unlockOnVictory,
            decimal healthMultiplier, long gold, string relic)
        { Id = id; Name = name; Description = description; InitiallyUnlocked = initiallyUnlocked; UnlockOnVictory = unlockOnVictory;
            MaxHealthMultiplier = healthMultiplier; StartingGoldBonus = gold; StartingRelicId = relic; }
    }
    public sealed class AdventureShopItem
    {
        public string Id { get; }
        public string RelicId { get; }
        public string Name { get; }
        public string Description { get; }
        public long Price { get; }
        public long SellPrice { get; }
        internal AdventureShopItem(string id, string relic, string name, string description, long price, long sellPrice)
        { Id = id; RelicId = relic; Name = name; Description = description; Price = price; SellPrice = sellPrice; }
    }
    public sealed class AdventureServicePrice
    {
        public AdventureCommandKind Kind { get; }
        public long BasePrice { get; }
        public long Increase { get; }
        internal AdventureServicePrice(AdventureCommandKind kind, long price, long increase)
        { Kind = kind; BasePrice = price; Increase = increase; }
    }
    public sealed class AdventureRestChoice
    {
        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public AdventureRestKind Kind { get; }
        public decimal HealRatio { get; }
        public long Gold { get; }
        public string RelicId { get; }
        internal AdventureRestChoice(string id, string name, string description, AdventureRestKind kind, decimal ratio, long gold, string relic)
        { Id = id; Name = name; Description = description; Kind = kind; HealRatio = ratio; Gold = gold; RelicId = relic; }
    }
    /// <summary>Separate adventure contract. References battle definitions without altering battle XML.</summary>
    public sealed class BattleAdventureDefinition
    {
        public string Id { get; }
        public string Name { get; }
        public string Version { get; }
        public string Fingerprint { get; }
        public string BattleFingerprint { get; }
        public string SourceXml { get; }
        public string StartNodeId { get; }
        public string StartingEncounterId { get; }
        public string DefaultTarotId { get; }
        public long StartingGold { get; }
        public int RelicCapacity { get; }
        public int MinimumDeckSize { get; }
        public int MaximumDeckSize { get; }
        public int EconomyVersion { get; }
        public int ShopOfferCount { get; }
        public long RestHeal { get; }
        public int TotalChapters => Nodes.Max(n => n.Chapter);
        public IReadOnlyList<AdventureNodeDefinition> Nodes { get; }
        public IReadOnlyList<AdventureTarotDefinition> Tarots { get; }
        public IReadOnlyList<AdventureShopItem> ShopItems { get; }
        public IReadOnlyList<AdventureServicePrice> ServicePrices { get; }
        public IReadOnlyList<AdventureRestChoice> RestChoices { get; }
        public ProgressionDefinition ProgressionDefinition { get; }
        internal BattleAdventureDefinition(string id, string name, string version, string fingerprint, string battleFingerprint, string xml,
            string startNode, string encounter, string defaultTarot, long gold, int capacity, int minimumDeckSize, int maximumDeckSize,
            IEnumerable<AdventureNodeDefinition> nodes, IEnumerable<AdventureTarotDefinition> tarots,
            IEnumerable<AdventureShopItem> shop, IEnumerable<AdventureServicePrice> services, IEnumerable<AdventureRestChoice> rest,
            int economyVersion = 1, int shopOfferCount = 4, long restHeal = 20)
        {
            Id = id; Name = name; Version = version; Fingerprint = fingerprint; BattleFingerprint = battleFingerprint; SourceXml = xml;
            StartNodeId = startNode; StartingEncounterId = encounter; DefaultTarotId = defaultTarot; StartingGold = gold;
            RelicCapacity = capacity; MinimumDeckSize = minimumDeckSize; MaximumDeckSize = maximumDeckSize;
            EconomyVersion = economyVersion; ShopOfferCount = shopOfferCount; RestHeal = restHeal;
            Nodes = Array.AsReadOnly(nodes.ToArray()); Tarots = Array.AsReadOnly(tarots.ToArray()); ShopItems = Array.AsReadOnly(shop.ToArray());
            ServicePrices = Array.AsReadOnly(services.ToArray()); RestChoices = Array.AsReadOnly(rest.ToArray());
            ProgressionDefinition = new ProgressionDefinition(version + "/progression", Array.Empty<string>(),
                Tarots.Select(t => new ProgressionUnlock(UnlockKind.Tarot, t.Id)), initialUnlocks:
                Tarots.Where(t => t.InitiallyUnlocked).Select(t => new ProgressionUnlock(UnlockKind.Tarot, t.Id)));
        }
        public AdventureNodeDefinition Node(string id) => Nodes.Single(n => n.Id == id);
    }
    public sealed class AdventureResult
    {
        public bool Accepted => Error == null;
        public string Error { get; }
        public BattleAdventureSession Session { get; }
        public BattleSessionResult BattleResult { get; }
        internal AdventureResult(BattleAdventureSession session, string error = null, BattleSessionResult battleResult = null)
        { Session = session; Error = error; BattleResult = battleResult; }
    }
}
