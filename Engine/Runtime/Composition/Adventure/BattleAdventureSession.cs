using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PokerEngine
{
    /// <summary>
    /// Health-based adventure authority. Every command is applied to a private candidate and committed atomically.
    /// Permanent deck entries are never reconstructed from a battle's zones, so generated junk and temporary
    /// effects cannot leak into the next encounter. Hosts provide authentication and durable storage.
    /// </summary>
    public sealed partial class BattleAdventureSession
    {
        public BattleAdventureDefinition Definition { get; private set; }
        public CompiledBattleContent Content { get; private set; }
        public string RunId { get; private set; }
        public ulong Seed { get; private set; }
        public string TarotId { get; private set; }
        public ParticipantId PlayerId { get; private set; }
        public long Revision { get; private set; }
        public AdventurePhase Phase { get; private set; }
        public long Health { get; private set; }
        public long MaxHealth { get; private set; }
        public long Gold { get; private set; }
        public long LastRewardGold { get; private set; }
        public string RestChoiceId { get; private set; }
        public AdventureNodeDefinition CurrentNode { get; private set; }
        public int ChapterIndex => (Phase == AdventurePhase.Map && available.Count > 0
            ? Definition.Node(available[0]).Chapter : CurrentNode?.Chapter ?? 1) - 1;
        public int CompletedChapters => visited.Select(Definition.Node).Where(n => n.Kind == AdventureNodeKind.Boss).Select(n => n.Chapter).Distinct().Count();
        public IReadOnlyList<DeckEntry> Deck => deck.AsReadOnly();
        public IReadOnlyList<RunRelic> Relics => relics.AsReadOnly();
        public IReadOnlyList<string> VisitedNodeIds => visited.AsReadOnly();
        public IReadOnlyList<string> PurchasedItemIds => purchased.AsReadOnly();
        public IReadOnlyList<string> BoughtShopItemIds => PurchasedItemIds;
        public IReadOnlyList<AdventureNodeDefinition> AvailableNodes => Array.AsReadOnly(Phase != AdventurePhase.Map
            ? Array.Empty<AdventureNodeDefinition>() : available.Select(Definition.Node).ToArray());
        public IReadOnlyList<AdventureTarotDefinition> UnlockedTarots => Array.AsReadOnly(Definition.Tarots.Where(t =>
            Progression.IsUnlocked(new ProgressionUnlock(UnlockKind.Tarot, t.Id))).ToArray());
        public bool CanContinue => pendingRelicRewards.Count == 0 && (Phase == AdventurePhase.Reward || Phase == AdventurePhase.Shop ||
            Phase == AdventurePhase.Chest || Phase == AdventurePhase.Rest && (Definition.EconomyVersion == 2 || RestChoiceId != null));
        public PlayerProgressionState Progression { get; private set; }
        // Return an isolated wrapper: callers may inspect/preview without bypassing adventure history or rewards.
        public BattleSession Battle => battle == null ? null : new BattleSession(battle.Engine, battle.State);
        public BattleSessionResult LastBattleResult { get; private set; }
        private BattleSession battle;
        private List<DeckEntry> deck;
        private List<RunRelic> relics;
        private List<string> visited = new List<string>(), available = new List<string>(), purchased = new List<string>();
        private Dictionary<AdventureCommandKind, int> serviceUses = new Dictionary<AdventureCommandKind, int>();
        private List<AdventureLogEntry> history = new List<AdventureLogEntry>();
        private PlayerProgressionState startingProgression;
        private BattleHandLevels handLevels;
        private int entitySequence;

        public BattleAdventureSession(BattleAdventureDefinition definition, CompiledBattleContent battleContent,
            string runId, ulong seed, string tarotId = null, PlayerProgressionState progression = null)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            Content = battleContent ?? throw new ArgumentNullException(nameof(battleContent));
            if (definition.BattleFingerprint != battleContent.Fingerprint) throw new ArgumentException("Adventure and battle content do not match.");
            if (string.IsNullOrWhiteSpace(runId) || runId.Length > 128) throw new ArgumentException("Invalid adventure identity.");
            RunId = runId; Seed = seed;
            var slot = Content.Encounters[Definition.StartingEncounterId].Players.Single();
            PlayerId = new ParticipantId(slot.Id);
            Progression = progression ?? new ProgressionEngine(definition.ProgressionDefinition).Create(PlayerId);
            ProgressionValidator.Validate(Progression, definition.ProgressionDefinition);
            if (Progression.PlayerId != PlayerId) throw new ArgumentException("Wrong progression owner.");
            if (Progression.Settlements.Any(s => s.RunId == runId)) throw new ArgumentException("Adventure identity has already been settled.");
            startingProgression = Progression;
            TarotId = tarotId ?? definition.DefaultTarotId;
            var tarot = definition.Tarots.SingleOrDefault(t => t.Id == TarotId);
            if (tarot == null || !Progression.IsUnlocked(new ProgressionUnlock(UnlockKind.Tarot, tarot.Id)))
                throw new ArgumentException("Tarot is not unlocked.");
            var template = Content.Players[slot.TemplateId];
            MaxHealth = checked((long)decimal.Floor(template.Definition.MaxHealth * tarot.MaxHealthMultiplier));
            if (MaxHealth < 1) throw new ArgumentException("Tarot health multiplier produces zero health.");
            Health = Math.Min(MaxHealth, slot.InitialHealth.HasValue
                ? checked((long)decimal.Floor(slot.InitialHealth.Value * tarot.MaxHealthMultiplier)) : MaxHealth);
            if (Health < 1) throw new ArgumentException("Starting health must be positive.");
            Gold = checked(definition.StartingGold + tarot.StartingGoldBonus);
            var loadout = slot.LoadoutId == null ? null : Content.Loadouts[slot.LoadoutId];
            deck = (loadout?.DeckId == null ? StandardDeck.Cards().Select((c, i) => new DeckEntry("card/" + i, c.Id))
                : Content.Decks[loadout.DeckId]).ToList();
            if (deck.Count < definition.MinimumDeckSize || deck.Count > definition.MaximumDeckSize) throw new ArgumentException("Starting deck exceeds adventure limits.");
            handLevels = loadout?.HandLevels ?? template.Rules.HandLevels;
            relics = (loadout?.Relics ?? Array.Empty<BattleRelicSetup>()).Select((r, i) =>
                new RunRelic(RunId + "/relic/start/" + i, r.Instance.DefinitionId, r.Instance.Level, r.Instance.Growth,
                    r.Instance.Charges, 0, AbilityMemory.Empty)).ToList();
            if (tarot.StartingRelicId != null) AddRelic(tarot.StartingRelicId);
            ValidateRelics(); RecordObtainedRelics();
            available.Add(definition.StartNodeId); Phase = AdventurePhase.Map;
        }

        public long ServicePrice(AdventureCommandKind kind)
        {
            var price = Definition.ServicePrices.SingleOrDefault(s => s.Kind == kind);
            if (price == null) throw new ArgumentException("Service is not configured.");
            return Definition.EconomyVersion == 2 ? EconomicServicePrice(price) : checked(price.BasePrice + price.Increase * ServiceUses(kind));
        }
        public BattleAdventureSession Fork() => Copy();
        public int ServiceUses(AdventureCommandKind kind) => serviceUses.TryGetValue(kind, out var count) ? count : 0;
        public long SellPrice(string relicInstanceId)
        {
            var relic = relics.SingleOrDefault(r => r.Id == relicInstanceId);
            if (relic == null) throw new ArgumentException("Unknown owned relic.");
            return Definition.EconomyVersion == 2 ? Content.Relics[relic.DefinitionId].Definition.SellPrice
                : Definition.ShopItems.FirstOrDefault(i => i.RelicId == relic.DefinitionId)?.SellPrice ?? 0;
        }
        public AdventureResult Execute(long expectedRevision, AdventureCommand command, bool preview = false)
        {
            if (expectedRevision != Revision) return new AdventureResult(this, "StaleRevision");
            if (command == null || !Enum.IsDefined(typeof(AdventureCommandKind), command.Kind)) return new AdventureResult(this, "InvalidCommand");
            try
            {
                var entry = AdventureLogEntry.ForCommand(expectedRevision, command); entry.Validate();
                return Apply(entry, preview);
            }
            catch (ArgumentException e) { return new AdventureResult(this, e.Message); }
        }
        public AdventureResult ExecuteBattle(long expectedRevision, CardDuelAction action, IEnumerable<CardInstanceId> selected = null,
            IEnumerable<ParticipantId> targets = null, BattleTargetBindings targetBindings = null, int drawCount = 0, bool preview = false)
        {
            if (expectedRevision != Revision) return new AdventureResult(this, "StaleRevision");
            try
            { return Apply(AdventureLogEntry.ForBattle(expectedRevision, action, selected, targets, targetBindings, drawCount), preview); }
            catch (ArgumentException e) { return new AdventureResult(this, e.Message); }
        }
        public AdventureResult UseBattleItem(long expectedRevision, string instanceId, string targetInstanceId = null, bool preview = false)
        {
            if (expectedRevision != Revision) return new AdventureResult(this, "StaleRevision");
            try { return Apply(AdventureLogEntry.ForItem(expectedRevision, instanceId, targetInstanceId), preview); }
            catch (ArgumentException e) { return new AdventureResult(this, e.Message); }
        }
        private AdventureResult Apply(AdventureLogEntry entry, bool preview)
        {
            if (history.Count >= MaxCommands) return new AdventureResult(this, "Adventure command limit reached.");
            var next = Copy();
            try
            {
                if (entry.AdminPatch != null) next.ApplyAdministrator(entry.AdminPatch);
                else if (entry.IsBattle) next.ApplyBattle(entry);
                else next.ApplyCommand(new AdventureCommand(entry.Kind, entry.Id, entry.Suit));
                next.Revision = checked(Revision + 1);
                next.history.Add(entry);
                if (preview) return new AdventureResult(next, battleResult: next.LastBattleResult);
                Adopt(next); return new AdventureResult(this, battleResult: LastBattleResult);
            }
            catch (ArgumentException e) { return new AdventureResult(this, e.Message); }
            catch (InvalidOperationException e) { return new AdventureResult(this, e.Message); }
            catch (OverflowException) { return new AdventureResult(this, "Adventure numeric overflow."); }
        }
        private void ApplyCommand(AdventureCommand command)
        {
            LastBattleResult = null;
            Need(Phase != AdventurePhase.Completed && Phase != AdventurePhase.Failed, "Adventure is already terminal.");
            if (command.Kind == AdventureCommandKind.Abandon) { Phase = AdventurePhase.Failed; Settle(RunOutcome.Abandoned); return; }
            if (command.Kind == AdventureCommandKind.EnterNode)
            {
                Need(Phase == AdventurePhase.Map && available.Contains(command.Id), "Node is not available.");
                CurrentNode = Definition.Node(command.Id); available.Clear(); purchased.Clear(); RestChoiceId = null; LastRewardGold = 0;
                ResetLocationEconomy(); ApplyLocationItems(entering: true);
                if (CurrentNode.Kind == AdventureNodeKind.Shop)
                { battle = null; Phase = AdventurePhase.Shop; if (Definition.EconomyVersion == 2) GenerateShopStock(); }
                else if (CurrentNode.Kind == AdventureNodeKind.Rest)
                { battle = null; Phase = AdventurePhase.Rest; if (Definition.EconomyVersion == 2) EnterRest(); }
                else if (CurrentNode.Kind == AdventureNodeKind.Chest)
                { battle = null; Phase = AdventurePhase.Chest; OpenChest(); }
                else { Phase = AdventurePhase.Battle; StartBattle(); }
                return;
            }
            if (command.Kind == AdventureCommandKind.Continue)
            {
                Need(CanContinue, "Complete this node before continuing.");
                ApplyLocationItems(entering: false);
                visited.Add(CurrentNode.Id); available = CurrentNode.NextIds.ToList();
                if (available.Count == 0) { Phase = AdventurePhase.Completed; Settle(RunOutcome.Won); }
                else { Phase = AdventurePhase.Map; battle = null; }
                return;
            }
            if (command.Kind == AdventureCommandKind.ClaimRelicReward || command.Kind == AdventureCommandKind.SkipRelicReward)
            { ResolveRelicReward(command); return; }
            if (command.Kind == AdventureCommandKind.DrawRestReward)
            { DrawRestReward(); return; }
            if (command.Kind == AdventureCommandKind.ChooseRest)
            {
                Need(Definition.EconomyVersion == 1 && Phase == AdventurePhase.Rest && RestChoiceId == null, "A rest reward can only be chosen once.");
                var choice = Definition.RestChoices.SingleOrDefault(r => r.Id == command.Id);
                Need(choice != null, "Unknown rest choice.");
                if (choice.Kind == AdventureRestKind.Heal) Health = checked(Health + Math.Min(MaxHealth - Health, checked((long)decimal.Floor(MaxHealth * choice.HealRatio))));
                else if (choice.Kind == AdventureRestKind.Gold) Gold = checked(Gold + choice.Gold);
                else AddRelic(choice.RelicId);
                RestChoiceId = choice.Id; return;
            }
            Need(Phase == AdventurePhase.Shop, "Shop services require a shop node.");
            if (command.Kind == AdventureCommandKind.BuyRelic)
            {
                var item = ShopItems.SingleOrDefault(i => i.Id == command.Id);
                Need(item != null && !purchased.Contains(item.Id), "Shop item is unavailable.");
                Spend(item.Price); AddRelic(item.RelicId); purchased.Add(item.Id); return;
            }
            if (command.Kind == AdventureCommandKind.SellRelic)
            {
                var relic = relics.SingleOrDefault(r => r.Id == command.Id); Need(relic != null, "Unknown owned relic.");
                Gold = checked(Gold + SellPrice(relic.Id)); relics.Remove(relic); return;
            }
            Need(BattleAdventureXml.IsService(command.Kind), "Invalid shop command.");
            var card = deck.SingleOrDefault(c => c.Id == command.Id); Need(card != null, "Unknown permanent card.");
            long cost = ServicePrice(command.Kind);
            if (command.Kind == AdventureCommandKind.DeleteCard)
            { Need(deck.Count > Definition.MinimumDeckSize, "Minimum deck size reached."); deck.Remove(card); }
            else if (command.Kind == AdventureCommandKind.DuplicateCard)
            {
                Need(deck.Count < Definition.MaximumDeckSize, "Maximum deck size reached.");
                string id;
                do { id = RunId + "/card/" + entitySequence++; } while (deck.Any(c => c.Id == id));
                deck.Add(new DeckEntry(id, card.DefinitionId, card.Effects, card.IsDisabled));
            }
            else
            {
                Need(command.Suit.HasValue && Enum.IsDefined(typeof(Suit), command.Suit.Value), "Choose a valid suit.");
                var face = StandardDeck.Cards().Single(c => c.Id == card.DefinitionId);
                Need(face.Suit != command.Suit.Value, "Card already has this suit.");
                deck[deck.IndexOf(card)] = new DeckEntry(card.Id, StandardDeck.CardId(command.Suit.Value, face.Rank), card.Effects, card.IsDisabled);
            }
            Spend(cost); serviceUses[command.Kind] = checked(ServiceUses(command.Kind) + 1);
        }
        private void ApplyBattle(AdventureLogEntry entry)
        {
            Need(Phase == AdventurePhase.Battle && battle != null, "No active battle.");
            var result = entry.Action == CardDuelAction.UseItem
                ? battle.UseItem(battle.State.Revision, PlayerId, entry.ItemInstanceId, entry.TargetItemInstanceId)
                : battle.Execute(battle.State.Revision, PlayerId, entry.Action,
                    entry.Cards?.Select(c => new CardInstanceId(c)), entry.Targets?.Select(t => new ParticipantId(t)), entry.CreateBindings(), entry.DrawCount);
            Need(result.Accepted, result.Error.ToString());
            LastBattleResult = result; Health = result.State.Duel.Combat.Get(PlayerId).Health;
            MaxHealth = result.State.Duel.Combat.Get(PlayerId).MaxHealth; Gold = result.State.GoldFor(PlayerId);
            relics = result.State.Duel.Player(PlayerId).Relics.ExportRunRelics().ToList(); RecordObtainedRelics();
            if (!result.State.Duel.IsFinished) return;
            // Dynamic ownership, finite charges and completed-battle cooldowns survive location changes.
            relics = relics.Select(BattleItemRuntime.CompleteBattle).ToList();
            if (result.State.Duel.Phase == ScoredDuelPhase.Lost)
            { Phase = AdventurePhase.Failed; Settle(RunOutcome.Lost); }
            else
            { LastRewardGold = CurrentNode.Gold; Gold = checked(Gold + LastRewardGold); Phase = AdventurePhase.Reward; }
        }
        private void StartBattle()
        {
            var encounter = Content.Encounters[CurrentNode.EncounterId]; var slot = encounter.Players.Single();
            // One persistent identity owns all nodes, even when authoring uses different local player slot names.
            var template = Content.Players[slot.TemplateId];
            var rule = Content.BattleRules[encounter.RulesId]; var scoring = Content.ScoringProfiles[rule.ScoringId]; var r = template.Rules;
            var playerRules = new BattlePlayerRules(r.MaxEnergy, r.InitialEnergy, r.PlayEnergyCost, r.DiscardEnergyCost, r.HandSize, r.Discards,
                r.DealPolicy, r.UnlimitedDiscards, r.MaxPlayCards, r.MaxDiscardCards,
                r.DiscardReward is ChipBlockRewardRule chips ? new ChipBlockRewardRule(chips.Ratio, scoring.CardPoints) : r.DiscardReward,
                r.EffectRule == null ? r.DamagePolicy : null, handLevels, r.EffectRule, r.Flow);
            var player = new BattlePlayerSetup(PlayerId, new CombatantDefinition(template.Definition.Id, MaxHealth, 0), deck, Health,
                playerRules, template.DamageRules, template.TargetRules, relics.Select(BindRelic));
            var enemyCatalog = Content.Enemies.ToDictionary(pair => pair.Key, pair =>
            {
                var e = pair.Value;
                return new BattleEnemyContentDefinition(new CombatantDefinition(e.Definition.Id,
                    e.ScaleHealthWithAdventure ? Math.Max(1L, checked((long)decimal.Floor(e.Definition.MaxHealth * CurrentNode.EnemyHealthMultiplier))) : e.Definition.MaxHealth,
                    e.Definition.InitialBlock), e.Damage, e.DamageRules, e.TargetRules, e.EffectRule, e.Behavior, e.ScaleHealthWithAdventure);
            }, StringComparer.Ordinal);
            var enemies = encounter.Enemies.Select(s =>
            {
                var e = enemyCatalog[s.TemplateId];
                return new BattleEnemySetup(new ParticipantId(s.Id), e.Definition, e.Damage, e.DamageRules, e.TargetRules, e.EffectRule, e.Behavior, enemyCatalog);
            });
            var engine = new CardDuelEngine(rule.Rules, scoring: scoring, itemCatalog: Content.Relics.Values);
            byte[] seedBytes;
            using (var hash = SHA256.Create()) seedBytes = hash.ComputeHash(Encoding.UTF8.GetBytes(
                Seed.ToString(CultureInfo.InvariantCulture) + "/" + CurrentNode.Id));
            battle = new BattleSession(engine, engine.Create(RunId + "/battle/" + CurrentNode.Id, HmacDeckShuffler.FromSeed(seedBytes), new[] { player }, enemies).WithGold(new[] { new BattleAdminGold(PlayerId, Gold) }));
            LastBattleResult = battle.Execute(battle.State.Revision, PlayerId, CardDuelAction.Start);
            Need(LastBattleResult.Accepted, "Cannot start battle: " + LastBattleResult.Error);
            Health = battle.State.Duel.Combat.Get(PlayerId).Health;
            // Opening boundaries may consume/revive/replace items before the first player input.
            relics = battle.State.Duel.Player(PlayerId).Relics.ExportRunRelics().ToList(); RecordObtainedRelics();
            if (battle.State.Duel.IsFinished)
            {
                relics = relics.Select(BattleItemRuntime.CompleteBattle).ToList();
                if (battle.State.Duel.Phase == ScoredDuelPhase.Lost) { Phase = AdventurePhase.Failed; Settle(RunOutcome.Lost); }
                else { Phase = AdventurePhase.Reward; LastRewardGold = CurrentNode.Gold; Gold = checked(Gold + LastRewardGold); }
            }
        }
        private BattleRelicSetup BindRelic(RunRelic relic)
        { var d = Content.Relics[relic.DefinitionId]; return new BattleRelicSetup(d.Definition, relic, d.Rules); }
        private void ApplyLocationItems(bool entering)
        {
            // One owner-ordered hook per entered/left location, including shops and rests.
            foreach (var relic in relics)
            {
                if (relic.Charges == 0) continue;
                var item = Content.Relics[relic.DefinitionId].Rules.Item;
                if (item == null) continue;
                if (entering && item.LocationEnterHeal > 0)
                    Health = checked(Health + Math.Min(MaxHealth - Health, item.LocationEnterHeal));
                if (!entering && item.LocationLeaveGoldRatio > 0)
                    Gold = checked(Gold + checked((long)decimal.Floor(Gold * item.LocationLeaveGoldRatio)));
            }
        }
        private void AddRelic(string id)
        {
            Need(Content.Relics.ContainsKey(id), "Unknown relic.");
            var d = Content.Relics[id].Definition;
            Need(relics.Count < Definition.RelicCapacity && relics.Count(r => r.DefinitionId == id) < d.MaxCopies, "Relic capacity or copy limit reached.");
            relics.Add(new RunRelic(RunId + "/relic/" + entitySequence++, id, charges: d.Charges));
            ValidateRelics(); RecordObtainedRelics();
        }
        private void ValidateRelics()
        {
            Need(relics.Count <= Definition.RelicCapacity, "Relic capacity reached.");
            new BattleRelicLoadout(PlayerId, relics.Select(BindRelic));
        }
        private void Spend(long amount) { Need(Gold >= amount, "Insufficient gold."); Gold -= amount; }
        private void Settle(RunOutcome outcome)
        {
            var reward = new ProgressionReward(unlocks: outcome == RunOutcome.Won ? Definition.Tarots.Where(t => t.UnlockOnVictory)
                .Select(t => new ProgressionUnlock(UnlockKind.Tarot, t.Id)) : Array.Empty<ProgressionUnlock>());
            var settlement = new RunSettlement(RunId, PlayerId, outcome, Definition.Id, Definition.Version, Content.Version, 0,
                CompletedChapters, Definition.TotalChapters, reward);
            Progression = new ProgressionEngine(Definition.ProgressionDefinition).Settle(Progression, settlement, Progression.Revision, PlayerId);
        }
        private BattleAdventureSession Copy()
        {
            var copy = (BattleAdventureSession)MemberwiseClone();
            copy.battle = Battle; copy.deck = new List<DeckEntry>(deck); copy.relics = new List<RunRelic>(relics);
            copy.visited = new List<string>(visited); copy.available = new List<string>(available); copy.purchased = new List<string>(purchased);
            copy.shopItems = new List<AdventureShopItem>(shopItems); copy.obtainedRelicIds = new List<string>(obtainedRelicIds);
            copy.pendingRelicRewards = new List<string>(pendingRelicRewards);
            copy.serviceUses = new Dictionary<AdventureCommandKind, int>(serviceUses); copy.history = new List<AdventureLogEntry>(history);
            return copy;
        }
        private void Adopt(BattleAdventureSession copy)
        {
            Revision = copy.Revision; Phase = copy.Phase; Health = copy.Health; MaxHealth = copy.MaxHealth; Gold = copy.Gold;
            CurrentNode = copy.CurrentNode; LastRewardGold = copy.LastRewardGold; RestChoiceId = copy.RestChoiceId; Progression = copy.Progression;
            battle = copy.battle; deck = copy.deck; relics = copy.relics; visited = copy.visited; available = copy.available;
            purchased = copy.purchased; serviceUses = copy.serviceUses; history = copy.history; entitySequence = copy.entitySequence;
            LastBattleResult = copy.LastBattleResult; shopItems = copy.shopItems; obtainedRelicIds = copy.obtainedRelicIds;
            pendingRelicRewards = copy.pendingRelicRewards; RestLotteryUsed = copy.RestLotteryUsed; LastLocationReward = copy.LastLocationReward;
        }
        private static void Need(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    }
}
