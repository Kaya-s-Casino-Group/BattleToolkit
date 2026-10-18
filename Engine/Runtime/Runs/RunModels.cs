using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public enum RunPhase { Preparing = 1, Encounter, Reward, Shop, Completed, Failed, NodeChoice }
    public enum RunOfferKind { Relic = 1, EnhanceCard, DuplicateCard, RemoveCard, TransformCard, UpgradeHand, Gold }

    public sealed class RelicDefinition
    {
        public string Id { get; }
        public int Slots { get; }
        public int MaxCopies { get; }
        public int MaxLevel { get; }
        public long SellPrice { get; }
        public long UpgradeCost { get; }
        public long GrowthPerScoredRound { get; }
        public AbilityDefinitionId? AbilityId { get; }
        public int? Charges { get; }
        public IReadOnlyList<ScoreEffect> ScoreEffects { get; }
        public RelicDefinition(string id, IEnumerable<ScoreEffect> effects, long sellPrice = 1, long upgradeCost = 5,
            int slots = 1, int maxCopies = 1, int maxLevel = 3, long growthPerScoredRound = 0, AbilityDefinitionId? abilityId = null, int? charges = null)
        {
            Id = Guard.Text(id, nameof(id)); ScoreEffects = Guard.View(Guard.Copy(effects, nameof(effects)));
            if (sellPrice < 0 || upgradeCost < 0 || slots < 1 || slots > 32 || maxCopies < 1 || maxLevel < 1 || maxLevel > 100 || growthPerScoredRound < 0 || charges < 0 ||
                abilityId.HasValue && !abilityId.Value.IsValid) throw new ArgumentException("Invalid relic definition.");
            SellPrice = sellPrice; UpgradeCost = upgradeCost; Slots = slots; MaxCopies = maxCopies; MaxLevel = maxLevel;
            GrowthPerScoredRound = growthPerScoredRound; AbilityId = abilityId; Charges = charges;
        }
    }
    public sealed class RunOfferDefinition
    {
        public string Id { get; }
        public RunOfferKind Kind { get; }
        public long Price { get; }
        public string RelicId { get; }
        public long Amount { get; }
        public decimal Multiplier { get; }
        public int Retriggers { get; }
        public HandCategory? HandCategory { get; }
        public CardDefinitionId? CardDefinition { get; }
        public RunOfferDefinition(string id, RunOfferKind kind, long price, string relicId = null, long amount = 0,
            decimal multiplier = 0, int retriggers = 0, HandCategory? handCategory = null, CardDefinitionId? cardDefinition = null)
        {
            Id = Guard.Text(id, nameof(id));
            if (!Enum.IsDefined(typeof(RunOfferKind), kind) || price < 0 || amount < 0 || multiplier < 0 || retriggers < 0 || retriggers > 8 ||
                (kind == RunOfferKind.Relic) != (relicId != null) || (kind == RunOfferKind.UpgradeHand) != handCategory.HasValue ||
                (kind == RunOfferKind.TransformCard) != cardDefinition.HasValue || handCategory.HasValue && !Enum.IsDefined(typeof(HandCategory), handCategory.Value))
                throw new ArgumentException("Invalid run offer.");
            Kind = kind; Price = price; RelicId = relicId; Amount = amount; Multiplier = multiplier; Retriggers = retriggers;
            HandCategory = handCategory; CardDefinition = cardDefinition;
        }
    }
    public sealed class RunStageDefinition
    {
        public string Id { get; }
        public long TargetScore { get; }
        public int RoundLimit { get; }
        public long ClearGold { get; }
        public int Energy { get; }
        public RunStageDefinition(string id, long targetScore, int roundLimit, long clearGold = 5, int energy = 3)
        {
            Id = Guard.Text(id, nameof(id));
            if (targetScore <= 0 || roundLimit < 1 || roundLimit > 1000 || clearGold < 0 || energy < 0)
                throw new ArgumentException("Invalid stage.");
            TargetScore = targetScore; RoundLimit = roundLimit; ClearGold = clearGold; Energy = energy;
        }
    }
    public sealed class RunDefinition
    {
        public string Version { get; }
        public ScoreProfile Scoring { get; }
        public int RelicCapacity { get; }
        public long StartingGold { get; }
        public long RerollCost { get; }
        public int OfferCount { get; }
        public IReadOnlyList<RunStageDefinition> Stages { get; }
        public IReadOnlyList<RunFloorDefinition> Floors { get; }
        public IReadOnlyList<RunEncounterNodeDefinition> EncounterNodes { get; }
        /// <summary>Compatibility view for single-floor definitions; multi-floor callers must select a floor.</summary>
        public RunNodeTree NodeTree => Floors.Count == 1 ? Floors[0].NodeTree : throw new InvalidOperationException("Select a floor's NodeTree from Floors.");
        public IReadOnlyList<string> StartingRelics { get; }
        public IReadOnlyList<string> ShopPool { get; }
        public IReadOnlyList<string> RewardPool { get; }
        public RunDefinition(string version, ScoreProfile scoring, IEnumerable<RunStageDefinition> stages, IEnumerable<string> shopPool,
            IEnumerable<string> rewardPool, IEnumerable<string> startingRelics = null, int relicCapacity = 5, long startingGold = 10, long rerollCost = 2, int offerCount = 3)
            : this(version, scoring, RunNodeTree.Linear(stages), shopPool, rewardPool, startingRelics, relicCapacity, startingGold, rerollCost, offerCount) { }
        public RunDefinition(string version, ScoreProfile scoring, RunNodeTree nodeTree, IEnumerable<string> shopPool,
            IEnumerable<string> rewardPool, IEnumerable<string> startingRelics = null, int relicCapacity = 5, long startingGold = 10, long rerollCost = 2, int offerCount = 3)
            : this(version, scoring, new[] { new RunFloorDefinition("default", nodeTree) }, shopPool, rewardPool, startingRelics,
                relicCapacity, startingGold, rerollCost, offerCount) { }
        public RunDefinition(string version, ScoreProfile scoring, IEnumerable<RunFloorDefinition> floors, IEnumerable<string> shopPool,
            IEnumerable<string> rewardPool, IEnumerable<string> startingRelics = null, int relicCapacity = 5, long startingGold = 10, long rerollCost = 2, int offerCount = 3)
        {
            Version = Guard.Text(version, nameof(version)); Scoring = scoring ?? throw new ArgumentNullException(nameof(scoring));
            var floorArray = Guard.Copy(floors, nameof(floors));
            if (floorArray.Length == 0 || floorArray.Length > 32 || floorArray.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != floorArray.Length)
                throw new ArgumentException("Invalid floors or duplicate floor identities.");
            Floors = Guard.View(floorArray); EncounterNodes = Guard.View(floorArray.SelectMany(f => f.NodeTree.EncounterNodes).ToArray());
            Stages = Guard.View(EncounterNodes.Select(n => n.Stage).ToArray()); ShopPool = Guard.View(Guard.Copy(shopPool, nameof(shopPool)));
            RewardPool = Guard.View(Guard.Copy(rewardPool, nameof(rewardPool))); StartingRelics = Guard.View(Guard.Copy(startingRelics ?? new string[0], nameof(startingRelics)));
            if (relicCapacity < 0 || relicCapacity > 32 ||
                startingGold < 0 || rerollCost < 1 || offerCount < 1 || offerCount > 10 || ShopPool.Count == 0 || RewardPool.Count == 0 ||
                ShopPool.Any(string.IsNullOrWhiteSpace) || RewardPool.Any(string.IsNullOrWhiteSpace) ||
                ShopPool.Distinct().Count() != ShopPool.Count || RewardPool.Distinct().Count() != RewardPool.Count) throw new ArgumentException("Invalid run definition.");
            RelicCapacity = relicCapacity; StartingGold = startingGold; RerollCost = rerollCost; OfferCount = offerCount;
        }
        public int StageIndex(int floorIndex, string nodeId)
        {
            if (floorIndex < 0 || floorIndex >= Floors.Count) throw new ArgumentOutOfRangeException(nameof(floorIndex));
            return Floors.Take(floorIndex).Sum(f => f.NodeTree.EncounterNodes.Count) + Floors[floorIndex].NodeTree.StageIndex(nodeId);
        }
        public int EncounterFloorIndex(int stageIndex)
        {
            if (stageIndex < 0 || stageIndex >= Stages.Count) throw new ArgumentOutOfRangeException(nameof(stageIndex));
            int offset = 0;
            for (int i = 0; i < Floors.Count; i++)
            {
                offset += Floors[i].NodeTree.EncounterNodes.Count;
                if (stageIndex < offset) return i;
            }
            throw new ArgumentOutOfRangeException(nameof(stageIndex));
        }
    }
    public sealed class RunContent
    {
        private readonly Dictionary<string, RelicDefinition> relics;
        private readonly Dictionary<string, RunOfferDefinition> offers;
        public ContentCatalog CardsAndAbilities { get; }
        public RunDefinition Definition { get; }
        public string StartingDeckVersion { get; }
        public IReadOnlyList<CardDefinitionId> StartingDeck { get; }
        public RunContent(ContentCatalog cardsAndAbilities, RunDefinition definition, IEnumerable<RelicDefinition> relics, IEnumerable<RunOfferDefinition> offers,
            string startingDeckVersion, IEnumerable<CardDefinitionId> startingDeck)
        {
            CardsAndAbilities = cardsAndAbilities ?? throw new ArgumentNullException(nameof(cardsAndAbilities)); Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            StartingDeckVersion = Guard.Text(startingDeckVersion, nameof(startingDeckVersion));
            StartingDeck = Guard.View(Guard.Copy(startingDeck, nameof(startingDeck)));
            if (StartingDeck.Count == 0 || StartingDeck.Count > 512 || StartingDeck.Any(id => !cardsAndAbilities.Contains(id)))
                throw new ArgumentException("Invalid starting deck.");
            this.relics = Guard.Copy(relics, nameof(relics)).ToDictionary(j => j.Id, StringComparer.Ordinal);
            this.offers = Guard.Copy(offers, nameof(offers)).ToDictionary(o => o.Id, StringComparer.Ordinal);
            if (definition.StartingRelics.Any(id => !this.relics.ContainsKey(id)) || definition.ShopPool.Concat(definition.RewardPool).Any(id => !this.offers.ContainsKey(id)) ||
                definition.Floors.Any(f => f.NodeTree.Nodes.Where(n => n.Kind == RunNodeKind.Reward || n.Kind == RunNodeKind.Shop)
                    .SelectMany(n => f.NodeTree.OfferPool(n, definition)).Any(id => !this.offers.ContainsKey(id))) ||
                this.relics.Values.Any(j => j.AbilityId.HasValue && !cardsAndAbilities.Contains(j.AbilityId.Value)) ||
                this.offers.Values.Any(o => o.Kind == RunOfferKind.Relic && !this.relics.ContainsKey(o.RelicId) ||
                    o.CardDefinition.HasValue && !cardsAndAbilities.Contains(o.CardDefinition.Value))) throw new ArgumentException("Unresolved run content.");
        }
        public RelicDefinition Relic(string id) => relics.TryGetValue(id, out var value) ? value : throw new ArgumentException("Unknown relic.");
        public RunOfferDefinition Offer(string id) => offers.TryGetValue(id, out var value) ? value : throw new ArgumentException("Unknown offer.");
    }

    [DataContract]
    public sealed class RunCard
    {
        [DataMember(Order = 1, IsRequired = true)] public string Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public CardDefinitionId DefinitionId { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public long BonusPoints { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public decimal BonusMultiplier { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public int Retriggers { get; private set; }
        public RunCard(string id, CardDefinitionId definition, long bonusPoints = 0, decimal bonusMultiplier = 0, int retriggers = 0)
        {
            Id = Guard.Text(id, nameof(id)); if (!definition.IsValid || bonusPoints < 0 || bonusMultiplier < 0 || retriggers < 0 || retriggers > 8) throw new ArgumentException("Invalid run card.");
            DefinitionId = definition; BonusPoints = bonusPoints; BonusMultiplier = bonusMultiplier; Retriggers = retriggers;
        }
    }
    // Preserve the persisted contract identity; the rules API uses Relic.
    [DataContract(Name = "RunJoker")]
    public sealed class RunRelic
    {
        [DataMember(Order = 1, IsRequired = true)] public string Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public string DefinitionId { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public int Level { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public long Growth { get; private set; }
        [DataMember(Order = 5)] public int? Charges { get; private set; }
        [DataMember(Order = 6, IsRequired = true)] public int CooldownRounds { get; private set; }
        [DataMember(Order = 7, IsRequired = true)] public AbilityMemory Memory { get; private set; }
        public RunRelic(string id, string definition, int level = 1, long growth = 0, int? charges = null, int cooldownRounds = 0, AbilityMemory memory = null)
        {
            Id = Guard.Text(id, nameof(id)); DefinitionId = Guard.Text(definition, nameof(definition));
            if (level < 1 || growth < 0 || charges < 0 || cooldownRounds < 0) throw new ArgumentException("Invalid run relic.");
            Level = level; Growth = growth; Charges = charges; CooldownRounds = cooldownRounds; Memory = memory ?? AbilityMemory.Empty;
        }
    }
    [DataContract]
    public sealed class HandLevel
    {
        [DataMember(Order = 1, IsRequired = true)] public HandCategory Category { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public int Level { get; private set; }
        public HandLevel(HandCategory category, int level) { Category = category; Level = level; }
    }
    [DataContract]
    public sealed class RunOffer
    {
        [DataMember(Order = 1, IsRequired = true)] public string Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public string DefinitionId { get; private set; }
        public RunOffer(string id, string definition) { Id = Guard.Text(id, nameof(id)); DefinitionId = Guard.Text(definition, nameof(definition)); }
    }
    [DataContract]
    public sealed class RunState<TState> where TState : class
    {
        [DataMember(Order = 1, IsRequired = true)] public string Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public long Revision { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public string ContentVersion { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public string RulesVersion { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public string ScoreVersion { get; private set; }
        [DataMember(Order = 6, IsRequired = true)] public ParticipantId PlayerId { get; private set; }
        [DataMember(Order = 7, IsRequired = true)] public RunPhase Phase { get; private set; }
        [DataMember(Order = 8, IsRequired = true)] public int StageIndex { get; private set; }
        [DataMember(Order = 9, IsRequired = true)] public long Gold { get; private set; }
        [DataMember(Order = 10, IsRequired = true)] public long StageScore { get; private set; }
        [DataMember(Order = 11, IsRequired = true)] public int RoundsScored { get; private set; }
        [DataMember(Name = "Deck", Order = 12, IsRequired = true)] private RunCard[] deck;
        [DataMember(Name = "Jokers", Order = 13, IsRequired = true)] private RunRelic[] relics;
        [DataMember(Name = "HandLevels", Order = 14, IsRequired = true)] private HandLevel[] levels;
        [DataMember(Name = "Offers", Order = 15, IsRequired = true)] private RunOffer[] offers;
        [DataMember(Name = "Scores", Order = 16, IsRequired = true)] private ScoreResult[] scores;
        [DataMember(Order = 17, IsRequired = true)] public RandomState Random { get; private set; }
        [DataMember(Order = 18)] public TState Encounter { get; private set; }
        [DataMember(Order = 19, IsRequired = true)] public string EncounterKind { get; private set; }
        [DataMember(Order = 20, IsRequired = true)] public string EncounterRulesVersion { get; private set; }
        [DataMember(Order = 21, IsRequired = true)] public int EncounterSchemaVersion { get; private set; }
        [DataMember(Order = 22, IsRequired = true)] public string StartingDeckVersion { get; private set; }
        [DataMember(Order = 23, IsRequired = true)] public int FloorIndex { get; private set; }
        [DataMember(Name = "Floors", Order = 24, IsRequired = true)] private RunFloorState[] floors;
        public IReadOnlyList<RunFloorState> Floors => Guard.View(floors);
        public RunNodeState Nodes => floors[FloorIndex].Nodes;
        public IReadOnlyList<RunCard> Deck => Guard.View(deck);
        public IReadOnlyList<RunRelic> Relics => Guard.View(relics);
        public IReadOnlyList<HandLevel> HandLevels => Guard.View(levels);
        public IReadOnlyList<RunOffer> Offers => Guard.View(offers);
        public IReadOnlyList<ScoreResult> Scores => Guard.View(scores);
        public RunState(string id, long revision, string contentVersion, string rulesVersion, string scoreVersion, ParticipantId player,
            RunPhase phase, int stageIndex, long gold, long stageScore, int roundsScored, IEnumerable<RunCard> deck, IEnumerable<RunRelic> relics,
            IEnumerable<HandLevel> levels, IEnumerable<RunOffer> offers, IEnumerable<ScoreResult> scores, RandomState random, string encounterKind, string encounterRulesVersion,
            int encounterSchemaVersion, string startingDeckVersion, int floorIndex, IEnumerable<RunFloorState> floors, TState encounter = null)
        {
            Id = Guard.Text(id, nameof(id)); Revision = revision; ContentVersion = contentVersion; RulesVersion = rulesVersion; ScoreVersion = scoreVersion;
            PlayerId = player; Phase = phase; StageIndex = stageIndex; Gold = gold; StageScore = stageScore; RoundsScored = roundsScored;
            this.deck = Guard.Copy(deck, nameof(deck)); this.relics = Guard.Copy(relics, nameof(relics)); this.levels = Guard.Copy(levels, nameof(levels));
            this.offers = Guard.Copy(offers, nameof(offers)); this.scores = Guard.Copy(scores, nameof(scores)); Random = random; Encounter = encounter;
            EncounterKind = encounterKind; EncounterRulesVersion = encounterRulesVersion; EncounterSchemaVersion = encounterSchemaVersion; StartingDeckVersion = startingDeckVersion;
            FloorIndex = floorIndex; this.floors = Guard.Copy(floors, nameof(floors));
        }
    }
}
