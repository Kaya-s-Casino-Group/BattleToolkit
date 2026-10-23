using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public enum CardDuelAction { Play = 1, Discard, EnemyAction, EndTurn, CollectPlayed, Start, SkipPlayerTurn, Draw, SkipDraw, DiscardForDraw, ConfirmOverflow, FinishTurn, UseItem, AdminEdit }
    public enum CardDuelError { None, StaleRevision, InvalidPhase, InvalidSelection, NoDiscards, InsufficientEnergy, Overflow, InvalidTarget, InvalidState, InvalidDamagePlan, InvalidDamageModifier, InvalidDiscardReward, InvalidEffectPlan, ActionBlocked, InvalidReaction, ReactionLimitExceeded, InvalidRelicState, InvalidAmountModifier, InvalidHandEffectModifier, InvalidRuleModifier, InvalidDealPlan, InvalidAdminPatch }
    public enum CardDuelRefillTiming { NextPlayerTurn = 0, AfterAction = 1, DiscardedCount = 2 }
    public enum CardDuelDealTrigger { OpeningTurn, AfterPlay, AfterDiscard, PlayerTurnStart, ManualDraw, EffectDraw }

    /// <summary>Read-only input to a pure draw policy. It decides how many cards, never moves or creates cards.</summary>
    public sealed class CardDuelDealContext
    {
        public CardDuelDealTrigger Trigger { get; }
        public CardTableState Table { get; }
        public int HandLimit { get; }
        public ParticipantId PlayerId { get; }
        public int DiscardedCount { get; }
        public int HandCount => Table.Zones.Single(zone => zone.Kind == CardZoneKinds.Hand && zone.ControllerId == PlayerId).Cards.Count;
        public int MissingCards => Math.Max(0, HandLimit - HandCount);
        internal CardDuelDealContext(CardDuelDealTrigger trigger, CardTableState table, int handLimit, ParticipantId player, int discardedCount = 0)
        { if (discardedCount < 0) throw new ArgumentOutOfRangeException(nameof(discardedCount)); Trigger = trigger; Table = table; HandLimit = handLimit; PlayerId = player; DiscardedCount = discardedCount; }
    }
    public interface ICardDuelDealPolicy
    {
        int GetDrawCount(CardDuelDealContext context);
    }
    /// <summary>Opening/turn-start fill the hand. Play and discard can each refill immediately or wait for the next turn.</summary>
    public sealed class CardDuelDealPolicy : ICardDuelDealPolicy
    {
        public CardDuelRefillTiming AfterPlay { get; }
        public CardDuelRefillTiming AfterDiscard { get; }
        public CardDuelDealPolicy(CardDuelRefillTiming afterPlay = CardDuelRefillTiming.NextPlayerTurn,
            CardDuelRefillTiming afterDiscard = CardDuelRefillTiming.AfterAction)
        {
            if (!Enum.IsDefined(typeof(CardDuelRefillTiming), afterPlay) || afterPlay == CardDuelRefillTiming.DiscardedCount || !Enum.IsDefined(typeof(CardDuelRefillTiming), afterDiscard))
                throw new ArgumentException("Unknown refill timing.");
            AfterPlay = afterPlay; AfterDiscard = afterDiscard;
        }
        public int GetDrawCount(CardDuelDealContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.Trigger == CardDuelDealTrigger.AfterDiscard && AfterDiscard == CardDuelRefillTiming.DiscardedCount)
                return Math.Min(context.DiscardedCount, context.MissingCards);
            return (context.Trigger == CardDuelDealTrigger.AfterPlay && AfterPlay != CardDuelRefillTiming.AfterAction) ||
                (context.Trigger == CardDuelDealTrigger.AfterDiscard && AfterDiscard != CardDuelRefillTiming.AfterAction) ? 0 : context.MissingCards;
        }
    }

    /// <summary>One authority owns card locations and the health duel. Views only project this state.</summary>
    public sealed class CardDuelState
    {
        public CardTableState Table { get; }
        public TeamBattleState Duel { get; }
        public RandomState Random { get; }
        private readonly Dictionary<ParticipantId, int> discards;
        public int DiscardsRemaining => DiscardsFor(Duel.PlayerId);
        // Finite counter is unchanged in unlimited mode; use RemainingDiscardBudgetFor for display/query.
        public int DiscardsFor(ParticipantId player) => discards[player];
        public int? RemainingDiscardBudgetFor(ParticipantId player) => Duel.Player(player).Rules.UnlimitedDiscards ? (int?)null : discards[player];
        public bool HasDiscardBudgetFor(ParticipantId player) => Duel.Player(player).Rules.UnlimitedDiscards || discards[player] > 0;
        private readonly Dictionary<ParticipantId, BattlePlayerFlow> flows;
        public BattlePlayerFlow FlowFor(ParticipantId player) => flows[player];
        internal Dictionary<ParticipantId, BattlePlayerFlow> CopyFlows() => new Dictionary<ParticipantId, BattlePlayerFlow>(flows);
        private readonly Dictionary<ParticipantId, long> gold;
        public long GoldFor(ParticipantId player) => gold[player];
        internal Dictionary<ParticipantId, long> CopyGold() => new Dictionary<ParticipantId, long>(gold);
        /// <summary>Sets the initial encounter wallet before Start. In-progress corrections use ApplyAdmin.</summary>
        public CardDuelState WithGold(IEnumerable<BattleAdminGold> values)
        {
            if (HasStarted) throw new InvalidOperationException("Initial gold can only be supplied before Start.");
            var next = CopyGold(); var seen = new HashSet<ParticipantId>();
            foreach (var value in values ?? throw new ArgumentNullException(nameof(values)))
            {
                if (value == null || !next.ContainsKey(value.Owner) || !seen.Add(value.Owner) || value.Amount < 0)
                    throw new ArgumentException("Invalid initial gold.");
                next[value.Owner] = value.Amount;
            }
            return new CardDuelState(Table, Duel, Random, discards, HasStarted, flows, next);
        }
        public bool HasStarted { get; }
        public long Revision => Table.Revision;
        public CardZone Zone(string kind) => Zone(kind, Duel.PlayerId);
        public CardZone Zone(string kind, ParticipantId player) => Table.Zones.Single(zone => zone.Kind == kind && zone.ControllerId == player);
        internal CardDuelState(CardTableState table, TeamBattleState duel, RandomState random, IDictionary<ParticipantId, int> discards, bool hasStarted, IDictionary<ParticipantId, BattlePlayerFlow> flows = null, IDictionary<ParticipantId, long> gold = null)
        {
            Table = table; Duel = duel; Random = random; this.discards = new Dictionary<ParticipantId, int>(discards); HasStarted = hasStarted;
            this.flows = flows == null ? duel.Players.ToDictionary(p => p.Id, p => NewFlow(p)) : new Dictionary<ParticipantId, BattlePlayerFlow>(flows);
            this.gold = gold == null ? duel.Players.ToDictionary(p => p.Id, p => 0L) : new Dictionary<ParticipantId, long>(gold);
            if (this.gold.Count != duel.Players.Count || duel.Players.Any(p => !this.gold.ContainsKey(p.Id)) || this.gold.Values.Any(v => v < 0))
                throw new ArgumentException("Gold must contain one nonnegative balance per player.");
        }
        internal static BattlePlayerFlow NewFlow(BattlePlayerTurn player) => new BattlePlayerFlow(
            player.Rules.Flow.ManualDraw && BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.DrawUses).Value > 0 ? BattlePlayerWindow.Draw : BattlePlayerWindow.Action,
            BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.DrawUses).Value, actionsRemaining: BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.ActionsPerTurn).Value);
    }

    public sealed class CardDuelTransition
    {
        public CardDuelState State { get; }
        public CardDuelError Error { get; }
        public bool Accepted => Error == CardDuelError.None;
        public ScoreResult Score { get; }
        public PokerHandClassification Pattern { get; }
        public DamagePlan Damage { get; }
        public DiscardRewardResolution DiscardReward { get; }
        public BattleEffectPlan EffectPlan { get; }
        public BattleActionCostResolution ActionCost { get; }
        public IReadOnlyList<BattleDrawRecord> Draws { get; }
        public IReadOnlyList<RelicTriggerActivation> RelicActivations { get; }
        public BattleActionCompletion ActionCompletion { get; }
        public IReadOnlyList<CombatRuleEvent> RuleEvents { get; }
        public IReadOnlyList<CombatEvent> Events { get; }
        public IReadOnlyList<BattleTimelineEntry> Timeline { get; }
        internal CardDuelTransition(CardDuelState state, CardDuelError error = CardDuelError.None,
            ScoreResult score = null, IEnumerable<CombatRuleEvent> events = null, PokerHandClassification pattern = null, DamagePlan damage = null, DiscardRewardResolution discardReward = null, BattleEffectPlan effectPlan = null,
            BattleActionCostResolution actionCost = null, IEnumerable<BattleDrawRecord> draws = null,
            IEnumerable<RelicTriggerActivation> relicActivations = null, BattleActionCompletion actionCompletion = null,
            IEnumerable<BattleTimelineEntry> timeline = null)
        {
            State = state; Error = error; Score = score; Pattern = pattern; Damage = damage; DiscardReward = discardReward; EffectPlan = effectPlan;
            ActionCost = actionCost; ActionCompletion = actionCompletion; Draws = Guard.View(Guard.Copy(draws ?? Array.Empty<BattleDrawRecord>(), nameof(draws)));
            RelicActivations = Guard.View(Guard.Copy(relicActivations ?? Array.Empty<RelicTriggerActivation>(), nameof(relicActivations)));
            RuleEvents = Guard.View(Guard.Copy(events ?? Array.Empty<CombatRuleEvent>(), nameof(events)));
            Events = Guard.View(RuleEvents.OfType<CombatEvent>().ToArray());
            Timeline = Guard.View(Guard.Copy(error == CardDuelError.None ? timeline ?? Array.Empty<BattleTimelineEntry>() :
                Array.Empty<BattleTimelineEntry>(), nameof(timeline)));
        }
    }

    /// <summary>
    /// Card/health coordinator for both rosters: independent player decks and costs, then ordered enemy actions.
    /// No Draw target-score victory, AbilityWorld, Run rewards or Unity dependency.
    /// </summary>
    public sealed partial class CardDuelEngine
    {
        private readonly Dictionary<CardDefinitionId, CardDefinition> faces = StandardDeck.Cards().ToDictionary(card => card.Id);
        private readonly HmacDeckShuffler shuffler = new HmacDeckShuffler();
        private readonly Dictionary<string, BattleRelicContentDefinition> itemCatalog;
        /// <summary>Catalog bound to this engine's session, for authority-side administrator tools.</summary>
        public IReadOnlyList<BattleRelicContentDefinition> AdminRelicCatalog => Array.AsReadOnly(itemCatalog.Values.ToArray());
        public TeamBattleEngine Duel { get; }
        public ScoreProfile Scoring { get; }
        private readonly CombatantDefinition defaultPlayer = new CombatantDefinition("hero", 40), defaultEnemy = new CombatantDefinition("enemy", 75);
        private readonly long defaultEnemyDamage = 7;
        private readonly BattlePlayerRules defaultPlayerRules;
        // Defaults used only by the legacy single-player Create overload.
        public int HandSize => defaultPlayerRules.HandSize;
        public int Discards => defaultPlayerRules.Discards;
        public ICardDuelDealPolicy DealPolicy => defaultPlayerRules.DealPolicy;
        public int MaxSelection => defaultPlayerRules.MaxSelection;
        public CardDuelEngine(ScoredDuelDefinition definition, int handSize = 8, int discards = 3, ScoreProfile scoring = null,
            ICardDuelDealPolicy dealPolicy = null)
            : this(new BattleRules(definition.Version, definition.RoundLimit), handSize, discards, scoring, dealPolicy)
        {
            defaultPlayer = definition.Player; defaultEnemy = definition.Enemy; defaultEnemyDamage = definition.EnemyDamage;
            defaultPlayerRules = new BattlePlayerRules(definition.MaxEnergy, definition.InitialEnergy, definition.PlayEnergyCost,
                definition.DiscardEnergyCost, handSize, discards, dealPolicy, damagePolicy: definition.DamagePolicy);
        }
        public CardDuelEngine(BattleRules rules, int handSize = 8, int discards = 3, ScoreProfile scoring = null,
            ICardDuelDealPolicy dealPolicy = null, IEnumerable<BattleRelicContentDefinition> itemCatalog = null)
        {
            this.itemCatalog = (itemCatalog ?? Array.Empty<BattleRelicContentDefinition>()).ToDictionary(x => x.Definition.Id, StringComparer.Ordinal);
            Duel = new TeamBattleEngine(rules); Scoring = scoring ?? ScoreProfile.Default;
            defaultPlayerRules = new BattlePlayerRules(handSize: handSize, discards: discards, dealPolicy: dealPolicy);
        }
        public CardDefinition Face(CardInstance card) => faces[card.DefinitionId];
        public static ZoneId ZoneId(string encounterId, string kind) => new ZoneId(encounterId + "/" + kind);

        /// <summary>Create an undealt battle. Start enters the first player turn and deals the hand for free.</summary>
        public CardDuelState Create(string id, RandomState random, IEnumerable<DeckEntry> deck = null, long? initialHealth = null,
            IEnumerable<BattleRelicSetup> relics = null)
        {
            return Create(id, random, new[] { new BattlePlayerSetup(new ParticipantId("player"), defaultPlayer, deck, initialHealth, defaultPlayerRules, relics: relics) },
                new[] { new BattleEnemySetup(new ParticipantId(id + "/enemy"), defaultEnemy, defaultEnemyDamage) });
        }
        public CardDuelState Create(string id, RandomState random, IEnumerable<BattlePlayerSetup> players, IEnumerable<BattleEnemySetup> enemies)
        {
            var roster = Guard.Copy(players, nameof(players)).Select(player => player == null ? null :
                new BattlePlayerSetup(player.Id, player.Definition, player.Deck, player.InitialHealth, player.Rules ?? defaultPlayerRules,
                    player.DamageRules, player.TargetRules, player.Relics)).ToArray();
            var duel = Duel.Create(id, roster, enemies);
            var cards = new List<CardInstance>(); var zones = new List<CardZone>();
            for (int seat = 0; seat < roster.Length; seat++)
            {
                var player = roster[seat];
                var entries = Guard.Copy(player.Deck ?? StandardDeck.Cards().Select((card, i) => new DeckEntry("standard/" + i, card.Id)).ToArray(), "deck");
                if (entries.Length < (player.Rules.Flow.UsesTurnFlow ? 0 : duel.Player(player.Id).Limits.HandLimit.Value) || entries.Length > 512 || entries.Any(entry => entry == null || !faces.ContainsKey(entry.DefinitionId)) ||
                    entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
                    throw new ArgumentException("Each deck needs valid faces, unique origins and at most 512 cards; legacy flow also requires the opening hand size.");
                var owned = entries.Select((entry, i) => new CardInstance(new CardInstanceId(id + "/player/" + seat + "/card/" + i), entry.DefinitionId, entry.Id, entry.Effects, entry.IsDisabled)).ToArray();
                cards.AddRange(owned);
                var shuffled = shuffler.Shuffle(owned.Select(card => card.Id).ToArray(), random); random = shuffled.Random;
                zones.AddRange(new[] { CardZoneKinds.Deck, CardZoneKinds.Hand, CardZoneKinds.Played, CardZoneKinds.Discard }
                    .Select(kind => new CardZone(ZoneId(seat == 0 ? id : id + "/player/" + seat, kind), kind, player.Id,
                        kind == CardZoneKinds.Deck ? shuffled.Cards : new CardInstanceId[0])));
            }
            return new CardDuelState(new CardTableState(new MatchId(id), cards, zones), duel, random, roster.ToDictionary(x => x.Id, x => x.Rules.Discards), false);
        }
        /// <summary>Authority-only component update. Preserves cards, resources, random state and action history;
        /// advances revisions so stale decisions cannot commit against a previous relic runtime.</summary>
        public CardDuelTransition UpdateRelicRuntime(CardDuelState state, long expectedRevision, ParticipantId owner,
            IEnumerable<BattleRelicState> relics)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (expectedRevision != state.Revision) return new CardDuelTransition(state, CardDuelError.StaleRevision);
            var result = Duel.UpdateRelicRuntime(state.Duel, state.Duel.Revision, owner, relics);
            if (!result.Accepted) return new CardDuelTransition(state, result.Error);
            try
            {
                var table = new CardTableState(state.Table.MatchId, state.Table.Cards, state.Table.Zones, checked(state.Revision + 1));
                var discards = state.Duel.Players.ToDictionary(player => player.Id, player => state.DiscardsFor(player.Id));
                return new CardDuelTransition(new CardDuelState(table, result.State, state.Random, discards, state.HasStarted, state.CopyFlows(), state.CopyGold()));
            }
            catch (CardHookException exception) { return new CardDuelTransition(state, exception.Error); }
            catch (OverflowException) { return new CardDuelTransition(state, CardDuelError.Overflow); }
        }
        /// <summary>Runs the exact command pipeline against immutable input. The caller must not commit the returned state.
        /// Injected target, damage, attack/incoming modifiers and draw policies must be pure, just as for authoritative execution.</summary>
        public CardDuelTransition PreviewPlay(CardDuelState state, long expectedRevision, ParticipantId actor,
            IEnumerable<CardInstanceId> selected, IEnumerable<ParticipantId> targets = null, BattleTargetBindings targetBindings = null) =>
            Execute(state, expectedRevision, actor, CardDuelAction.Play, selected, targets, targetBindings);

        public CardDuelTransition PreviewDiscard(CardDuelState state, long expectedRevision, ParticipantId actor,
            IEnumerable<CardInstanceId> selected, IEnumerable<ParticipantId> targets = null, BattleTargetBindings targetBindings = null) =>
            Execute(state, expectedRevision, actor, CardDuelAction.Discard, selected, targets, targetBindings);

        /// <summary>Read-only candidate classification/scoring, independent of action costs and target/damage policies.
        /// Authoritative Play uses this same scoring path. State, random and all resources remain unchanged.</summary>
        public CardDuelTransition PreviewHand(CardDuelState state, ParticipantId actor, IEnumerable<CardInstanceId> selected) =>
            PreviewHand(state, actor, selected, out _);

        private CardDuelTransition PreviewHand(CardDuelState state, ParticipantId actor, IEnumerable<CardInstanceId> selected, out HandAttackInfo facts)
        {
            facts = null;
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (!ReferenceEquals(state.Duel.Rules, Duel.Definition)) return new CardDuelTransition(state, CardDuelError.InvalidState);
            if (!state.HasStarted || !state.Duel.Players.Any(player => player.Id == actor)) return new CardDuelTransition(state, CardDuelError.InvalidPhase);
            var ids = (selected ?? new CardInstanceId[0]).ToArray();
            var hand = state.Zone(CardZoneKinds.Hand, actor);
            if (ids.Length < PlayLimit(state, actor, true) || ids.Length > PlayLimit(state, actor, false) || ids.Distinct().Count() != ids.Length ||
                ids.Any(id => !hand.Cards.Contains(id) || !state.Table.GetCard(id).CanPlay))
                return new CardDuelTransition(state, CardDuelError.InvalidSelection);
            try
            {
                var ordered = hand.Cards.Where(ids.Contains).Select(state.Table.GetCard).ToArray();
                var player = state.Duel.Player(actor); var flow = player.Rules.Flow;
                var handRule = player.Rules.EffectRule as ConfiguredHandEffectRule;
                bool compatible = handRule?.CombineCompatibleEffects == true;
                var pattern = ClassifyHand(player, ordered);
                bool royal = pattern.Category == HandCategory.StraightFlush && pattern.TieBreakRanks[0] == (int)Rank.Ace;
                var level = state.Duel.Player(actor).Rules.HandLevels.LevelFor(pattern.Category);
                facts = BuildHandFacts(state, actor, ordered, pattern);
                var relics = RelicPrograms.Scores(player.Relics, facts);
                var kicker = BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.AllCardsScore).Value > 0 ? KickerScoring.AllCards : flow.KickerScoring;
                var score = ScoreEngine.EvaluateBattle(Duel.NextScoreId(state.Duel), Scoring, pattern, level,
                    ordered.Select(card => new ScoringCard(card.Id.Value, new CardFace(Face(card).Rank, Face(card).Suit), effects: card.Effects,
                        basePointsOverride: WheelAcePoints(handRule, pattern, card))),
                    flow.UsesTurnFlow || flow.LayeredScoreRelics ? relics : Array.Empty<ScoringRelic>(), kicker, flow.LayeredScoreRelics, player.Rules.HandLevels.LevelFor,
                    includeLegacyComponents: !compatible || (player.Rules.EffectRule as ConfiguredHandEffectRule)?.FullHouseComponentDamage == true,
                    fullHouseTripleBaseMultiplier: handRule?.FullHouseTripleBaseMultiplier);
                if (handRule?.ModernScoring == true && facts.IsFlush && !royal)
                    score = ScoreEngine.ScaleMultiplier(score, handRule.FlushDamageMultiplier, "hand/flush");
                var itemMultiplier = BattleItemRuntime.HandMultiplier(player.Relics);
                if (itemMultiplier != 1) score = ScoreEngine.ScaleMultiplier(score, itemMultiplier, "item/roulette");
                return new CardDuelTransition(state, score: score, pattern: pattern);
            }
            catch (CardHookException exception) { return new CardDuelTransition(state, exception.Error); }
            catch (OverflowException) { return new CardDuelTransition(state, CardDuelError.Overflow); }
        }

        private long? WheelAcePoints(ConfiguredHandEffectRule rule, PokerHandClassification pattern, CardInstance card) =>
            rule?.WheelAceScoresOne == true && (pattern.Category == HandCategory.Straight || pattern.Category == HandCategory.StraightFlush) &&
            pattern.TieBreakRanks[0] == 5 && pattern.ScoringCards.Count == 5 && pattern.ScoringCards.Contains(card.Id) && Face(card).Rank == Rank.Ace
                ? (long?)1 : null;

        private PokerHandClassification ClassifyHand(BattlePlayerTurn player, CardInstance[] ordered)
        {
            var rule = player.Rules.EffectRule as ConfiguredHandEffectRule;
            int flush = BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.MinFlushCards).Value;
            int straight = BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.MinStraightCards).Value;
            var cards = ordered.Select(card => new EffectiveCard(card.Id, Face(card).Rank, Face(card).Suit)).ToArray();
            var pattern = rule?.CombineCompatibleEffects == true ?
                PokerHandCompatibility.Classify(cards, flush, straight, flushAsAttribute: rule.FlushAsAttribute, modernScoring: rule.ModernScoring, pureFlushScoresAllCards: rule.PureFlushScoresAllCards).Highest :
                PokerHandClassifier.ClassifyBest(cards, flush, straight);
            bool royal = pattern.Category == HandCategory.StraightFlush && pattern.TieBreakRanks[0] == (int)Rank.Ace;
            return !royal && rule?.FlushUsesRankEffects == true ? PokerHandClassifier.ClassifyBest(cards, flush, straight, true) : pattern;
        }

        /// <summary>Authoritative card/flush facts shared by play, preview and target discovery.</summary>
        public HandAttackInfo PreviewHandFacts(CardDuelState state, ParticipantId actor, IEnumerable<CardInstanceId> selected)
        {
            var ids = (selected ?? Array.Empty<CardInstanceId>()).ToArray();
            var preview = PreviewHand(state, actor, ids, out var facts);
            if (!preview.Accepted) throw new ArgumentException("Invalid hand selection: " + preview.Error);
            return facts;
        }
        private HandAttackInfo BuildHandFacts(CardDuelState state, ParticipantId actor, CardInstance[] ordered, PokerHandClassification pattern)
        {
            long? flushBase = null;
            int flushMin = BattleRuleResolver.Value(state.Duel.Player(actor).Rules, state.Duel.Player(actor).Relics, BattleRuleValueKey.MinFlushCards).Value;
            var rule = state.Duel.Player(actor).Rules.EffectRule as ConfiguredHandEffectRule;
            int straightMin = BattleRuleResolver.Value(state.Duel.Player(actor).Rules, state.Duel.Player(actor).Relics, BattleRuleValueKey.MinStraightCards).Value;
            // A compatible flush exists exactly when one suit reaches the threshold. The legacy
            // royal branch is exclusive; all other compatible hands retain their flush attribute.
            bool royal = pattern.Category == HandCategory.StraightFlush && pattern.TieBreakRanks[0] == (int)Rank.Ace;
            bool flush = rule?.CombineCompatibleEffects == true ?
                (rule.ModernScoring || !royal) && ordered.GroupBy(card => Face(card).Suit).Any(group => group.Count() >= flushMin) :
                ordered.Length >= flushMin && ordered.All(c => Face(c).Suit == Face(ordered[0]).Suit);
            if (flush)
            {
                if (rule?.ModernScoring == true)
                {
                    var faces = ordered.Select(card => new EffectiveCard(card.Id, Face(card).Rank, Face(card).Suit)).ToArray();
                    var flushIds = PokerHandCompatibility.FlushCards(faces, flushMin);
                    // Damage and flush healing use the same contextual Ace value; other cards keep the configured points.
                    flushBase = ordered.Where(card => flushIds.Contains(card.Id) && CardEffectRules.CanScore(card.Effects)).Aggregate(0L, (sum, card) =>
                        checked(sum + (WheelAcePoints(rule, pattern, card) ?? Scoring.CardPoints.GetBasePoints(new CardFace(Face(card).Rank, Face(card).Suit)))));
                }
                else flushBase = ScoreEngine.Evaluate(Duel.NextScoreId(state.Duel) + "/flush-base", Scoring, HandCategory.Flush, 1,
                    ordered.Select(c => new ScoringCard(c.Id.Value, new CardFace(Face(c).Rank, Face(c).Suit), effects: c.Effects)),
                    Array.Empty<ScoringRelic>(), true).Total;
            }
            return new HandAttackInfo(pattern, ordered.Select(card => new AttackCardSnapshot(card,
                new EffectiveCard(card.Id, Face(card).Rank, Face(card).Suit))),
                BattleRuleResolver.Value(state.Duel.Player(actor).Rules, state.Duel.Player(actor).Relics, BattleRuleValueKey.MinFlushCards).Value,
                straightMin, flushBase, rule?.FlushUsesRankEffects == true, rule?.CombineCompatibleEffects == true, rule?.FlushAsAttribute == true, rule?.ModernScoring == true, rule?.PureFlushScoresAllCards == true);
        }

        // Compatibility overload: first player, or the next enemy. Multiplayer commands always identify their actor.
        public CardDuelTransition Execute(CardDuelState state, long expectedRevision, CardDuelAction action,
            IEnumerable<CardInstanceId> selected = null, BattleTargetBindings targetBindings = null) =>
            Execute(state, expectedRevision, action == CardDuelAction.EnemyAction ? state.Duel.NextEnemy : state.Duel.PlayerId,
                action, selected, targetBindings: targetBindings);
        public CardDuelTransition Execute(CardDuelState state, long expectedRevision, ParticipantId actor, CardDuelAction action,
            IEnumerable<CardInstanceId> selected = null, IEnumerable<ParticipantId> targets = null, BattleTargetBindings targetBindings = null, int drawCount = 0)
        {
            var result = ExecuteFlow(state, expectedRevision, actor, action, selected, targets, targetBindings, drawCount);
            return CompleteCardCommand(state, result, actor, action, selected, targets);
        }
        private CardDuelTransition CompleteCardCommand(CardDuelState state, CardDuelTransition result, ParticipantId actor, CardDuelAction action,
            IEnumerable<CardInstanceId> selected = null, IEnumerable<ParticipantId> targets = null)
        {
            if (!result.Accepted || state.Duel.IsFinished || !result.State.Duel.IsFinished || result.RuleEvents.OfType<CombatTimingEvent>().Any(ev => ev.Signal.Kind == CombatTimingKind.BattleEnded)) return result;
            var next = result.State;
            var ended = Duel.ResolveBoundary(next.Duel, CombatTimingSignal.BattleEnded(next.Table.MatchId + "/end", next.Duel.Players.Select(p => p.Id).Concat(next.Duel.Enemies.Select(en => en.Id))));
            if (!ended.Accepted) return new CardDuelTransition(state, ended.Error);
            if (ended.RuleEvents.Count == 0) return result;
            next = new CardDuelState(next.Table, ended.State, next.Random, next.Duel.Players.ToDictionary(p => p.Id, p => next.DiscardsFor(p.Id)), next.HasStarted, next.CopyFlows(), next.CopyGold());
            var timeline = new BattleTimelineRecorder(result.State, action, actor, selected, targets);
            timeline.ObserveCombat(ended.RuleEvents, ended.RelicActivations); timeline.SetState(next);
            timeline.Add(BattleTimelineKind.BattleEnded, reason: Duel.ReadProgress(next.Duel).Result.Reason.ToString());
            return new CardDuelTransition(next, score: result.Score, events: result.RuleEvents.Concat(ended.RuleEvents), pattern: result.Pattern, damage: result.Damage,
                discardReward: result.DiscardReward, effectPlan: result.EffectPlan, actionCost: result.ActionCost, draws: result.Draws,
                relicActivations: result.RelicActivations.Concat(ended.RelicActivations), actionCompletion: result.ActionCompletion, timeline: result.Timeline.Where(entry => entry.Kind != BattleTimelineKind.BattleEnded).Concat(timeline.Entries));
        }

        private CardDuelTransition ExecuteCore(CardDuelState state, long expectedRevision, ParticipantId actor, CardDuelAction action,
            IEnumerable<CardInstanceId> selected = null, IEnumerable<ParticipantId> targets = null, BattleTargetBindings targetBindings = null, bool freeDiscard = false)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (expectedRevision != state.Revision) return new CardDuelTransition(state, CardDuelError.StaleRevision);
            if (action != CardDuelAction.Play && targetBindings != null && targetBindings.Slots.Count != 0)
                return new CardDuelTransition(state, CardDuelError.InvalidTarget);
            if (action == CardDuelAction.Start ? state.HasStarted : !state.HasStarted)
                return new CardDuelTransition(state, CardDuelError.InvalidPhase);
            if (!ReferenceEquals(state.Duel.Rules, Duel.Definition)) return new CardDuelTransition(state, CardDuelError.InvalidState);
            bool enemy = action == CardDuelAction.EnemyAction;
            bool knownPlayer = state.Duel.Players.Any(x => x.Id == actor);
            bool playerDecision = action == CardDuelAction.Play || action == CardDuelAction.Discard || action == CardDuelAction.EndTurn;
            bool activePlayer = knownPlayer && state.Duel.Phase == ScoredDuelPhase.AwaitingPlayerHand &&
                !state.Duel.Player(actor).HasEnded && state.Duel.Combat.Get(actor).IsAlive;
            if (playerDecision && activePlayer && !state.Duel.CanAct(actor))
                return new CardDuelTransition(state, CardDuelError.ActionBlocked);
            if (state.Duel.IsFinished || !Enum.IsDefined(typeof(CardDuelAction), action) ||
                (enemy ? state.Duel.Phase != ScoredDuelPhase.AwaitingEnemyAction || actor != state.Duel.NextEnemy : !knownPlayer ||
                    (action == CardDuelAction.SkipPlayerTurn ? !activePlayer :
                        action != CardDuelAction.Start && action != CardDuelAction.CollectPlayed && !state.Duel.CanAct(actor))))
                return new CardDuelTransition(state, CardDuelError.InvalidPhase);
            // Played is a per-player presentation checkpoint, including players who just exhausted energy.
            bool hasPlayed = !enemy && state.Zone(CardZoneKinds.Played, actor).Cards.Count > 0;
            if (action == CardDuelAction.CollectPlayed ? !hasPlayed : !enemy && action != CardDuelAction.SkipPlayerTurn && hasPlayed)
                return new CardDuelTransition(state, CardDuelError.InvalidPhase);
            var ids = (selected ?? new CardInstanceId[0]).ToArray();
            if (action != CardDuelAction.Play && action != CardDuelAction.Discard ? ids.Length != 0 : ids.Length < 1 ||
                ids.Length > (action == CardDuelAction.Play ? PlayLimit(state, actor, false) : state.Duel.Player(actor).Rules.MaxDiscardCards) ||
                ids.Distinct().Count() != ids.Length || ids.Any(id => !state.Zone(CardZoneKinds.Hand, actor).Cards.Contains(id) || (action == CardDuelAction.Play ? !state.Table.GetCard(id).CanPlay : !state.Table.GetCard(id).CanDiscard)))
                return new CardDuelTransition(state, CardDuelError.InvalidSelection);
            if (action == CardDuelAction.Discard && !state.HasDiscardBudgetFor(actor)) return new CardDuelTransition(state, CardDuelError.NoDiscards);
            try
            {
                BattleActionCostResolution actionCost = null;
                if (action == CardDuelAction.Play || action == CardDuelAction.Discard)
                {
                    try { actionCost = Duel.QueryActionCost(state.Duel, actor,
                        action == CardDuelAction.Play ? ScoredDuelActionKind.ScoredHand : ScoredDuelActionKind.Discard); }
                    catch (ArgumentException) { return new CardDuelTransition(state, CardDuelError.InvalidRuleModifier); }
                    if (!actionCost.CanAfford && !freeDiscard) return new CardDuelTransition(state, CardDuelError.InsufficientEnergy);
                }
                var requestedTargets = targets == null ? null : targets.ToArray();
                var timeline = new BattleTimelineRecorder(state, action, actor, ids, requestedTargets);
                if (action != CardDuelAction.Start) timeline.Add(BattleTimelineKind.ActionStarted);
                var draws = new List<BattleDrawRecord>();
                BattleActionCompletion actionCompletion = null;
                IReadOnlyList<RelicTriggerActivation> relicActivations = Array.Empty<RelicTriggerActivation>();
                var workingTable = state.Table; var zones = workingTable.Zones.ToArray(); var random = state.Random; var duel = state.Duel;
                var discards = state.Duel.Players.ToDictionary(x => x.Id, x => state.DiscardsFor(x.Id)); ScoreResult score = null; PokerHandClassification pattern = null; DamagePlan damage = null; DiscardRewardResolution discardReward = null; BattleEffectPlan effectPlan = null; IReadOnlyList<CombatRuleEvent> events = Array.Empty<CombatRuleEvent>();
                var hookEvents = new List<CombatRuleEvent>(); var hookActivations = new List<RelicTriggerActivation>();
                Func<CombatTimingKind, ParticipantId, IReadOnlyList<CardInstanceId>, string, bool> hook = null;
                    int hookCount = 0;
                    hook = (kind, owner, cards, trigger) => ResolveCardBoundary(state.Revision, workingTable, zones, draws, timeline,
                        kind, owner, cards, trigger, ref duel, ref random, hookEvents, hookActivations, ref hookCount, hook);
                if (action == CardDuelAction.Start)
                {
                    timeline.SetCards(state.Table, state.Random, started: true);
                    var opening = Duel.ResolveOpening(duel, timeline);
                    if (!opening.Accepted) return new CardDuelTransition(state, opening.Error);
                    duel = opening.State; events = opening.RuleEvents; relicActivations = opening.RelicActivations;
                    timeline.SetTeam(duel);
                    foreach (var player in duel.Players.Where(player => !duel.IsFinished && duel.Combat.Get(player.Id).IsAlive))
                    {
                        var error = Deal(workingTable, zones, player, CardDuelDealTrigger.OpeningTurn, ref random, draws, timeline, hook);
                        if (error != CardDuelError.None) return new CardDuelTransition(state, error);
                    }
                }
                else if (action == CardDuelAction.Play)
                {
                    // Authority determines both submitted membership and score, never accepting a UI-provided numeric score.
                    var ordered = state.Zone(CardZoneKinds.Hand, actor).Cards.Where(ids.Contains).Select(state.Table.GetCard).ToArray();
                    var candidate = PreviewHand(state, actor, ids, out var hand);
                    if (!candidate.Accepted) return candidate;
                    pattern = candidate.Pattern; score = candidate.Score;
                    timeline.Add(BattleTimelineKind.HandEvaluated, score: score, pattern: pattern);
                    var hit = Duel.ExecuteObserved(duel, duel.Revision, actor, ScoredDuelActionKind.ScoredHand, score, requestedTargets, hand,
                        targetBindings: targetBindings, timeline: timeline, itemRandom: random);
                    if (!hit.Accepted) return new CardDuelTransition(state, hit.Error);
                    actionCost = hit.ActionCost;
                    relicActivations = hit.RelicActivations; actionCompletion = hit.ActionCompletion;
                    random = hit.ItemRandom ?? random; duel = hit.State; events = hit.RuleEvents; damage = hit.Damage; effectPlan = hit.EffectPlan;
                    timeline.SetTeam(duel);
                    Move(zones, actor, CardZoneKinds.Hand, CardZoneKinds.Played, ordered.Select(card => card.Id), timeline);
                    if (!duel.IsFinished)
                    {
                        var error = Deal(workingTable, zones, duel.Player(actor), CardDuelDealTrigger.AfterPlay, ref random, draws, timeline, hook);
                        if (error != CardDuelError.None) return new CardDuelTransition(state, error);
                    }
                }
                else if (action == CardDuelAction.Discard)
                {
                    var cards = state.Zone(CardZoneKinds.Hand, actor).Cards.Where(ids.Contains).Select(state.Table.GetCard)
                        .Select(card => new DiscardCardSnapshot(card, new EffectiveCard(card.Id, Face(card).Rank, Face(card).Suit))).ToArray();
                    var spent = Duel.ExecuteObserved(duel, duel.Revision, actor, ScoredDuelActionKind.Discard,
                        requestedTargets: requestedTargets, discardedCards: cards, timeline: timeline, freeDiscard: freeDiscard, dominantDiscardSuits: state.Zone(CardZoneKinds.Hand, actor).Cards.Select(id=>Face(state.Table.GetCard(id)).Suit).GroupBy(s=>s).GroupBy(g=>g.Count()).OrderByDescending(g=>g.Key).First().Select(g=>g.Key));
                    if (!spent.Accepted) return new CardDuelTransition(state, spent.Error);
                    actionCost = spent.ActionCost;
                    relicActivations = spent.RelicActivations; actionCompletion = spent.ActionCompletion;
                    duel = spent.State; events = spent.RuleEvents; discardReward = spent.DiscardReward; effectPlan = spent.EffectPlan;
                    timeline.SetTeam(duel);
                    Move(zones, actor, CardZoneKinds.Hand, CardZoneKinds.Discard, state.Zone(CardZoneKinds.Hand, actor).Cards.Where(ids.Contains), timeline);
                    if (!duel.Player(actor).Rules.UnlimitedDiscards)
                    {
                        discards[actor]--;
                        timeline.SetCards(timeline.State.Table, random, discards);
                        timeline.Add(BattleTimelineKind.ResourcesChanged, reason: "discard-budget");
                    }
                    // Completion/boundary victories stop dealing; preserve legacy discard settlement otherwise.
                    if (!duel.IsFinished || !relicActivations.Any(activation => activation.BoundaryTiming.HasValue || activation.ActionTiming.HasValue))
                    {
                        var error = Deal(workingTable, zones, duel.Player(actor), CardDuelDealTrigger.AfterDiscard, ref random, draws, timeline, hook, ids.Length);
                        if (error != CardDuelError.None) return new CardDuelTransition(state, error);
                    }
                }
                else if (action == CardDuelAction.EndTurn)
                {
                    var ended = Duel.ExecuteObserved(duel, duel.Revision, actor, ScoredDuelActionKind.EndPlayerTurn, timeline: timeline);
                    if (!ended.Accepted) return new CardDuelTransition(state, ended.Error);
                    duel = ended.State; events = ended.RuleEvents;
                    relicActivations = ended.RelicActivations;
                    timeline.SetTeam(duel);
                }
                else if (action == CardDuelAction.SkipPlayerTurn)
                {
                    var skipped = Duel.ExecuteObserved(duel, duel.Revision, actor, ScoredDuelActionKind.SkipPlayerTurn, timeline: timeline);
                    if (!skipped.Accepted) return new CardDuelTransition(state, skipped.Error);
                    duel = skipped.State; events = skipped.RuleEvents;
                    relicActivations = skipped.RelicActivations;
                    timeline.SetTeam(duel);
                }
                else if (action == CardDuelAction.CollectPlayed)
                {
                    Move(zones, actor, CardZoneKinds.Played, CardZoneKinds.Discard, state.Zone(CardZoneKinds.Played, actor).Cards, timeline);
                }
                else
                {
                    var response = Duel.ExecuteObserved(duel, duel.Revision, actor, ScoredDuelActionKind.EnemyAction,
                        requestedTargets: requestedTargets, timeline: timeline, worldObserver: (effect, team) => {
                            workingTable = GenerateEnemyCards(workingTable, zones, ref random, team, actor, effect, timeline);
                        });
                    if (!response.Accepted) return new CardDuelTransition(state, response.Error);
                    duel = response.State; events = response.RuleEvents; effectPlan = response.EffectPlan;
                    relicActivations = response.RelicActivations;
                    timeline.SetTeam(duel);
                    foreach (var player in duel.Players)
                    {
                        Move(zones, player.Id, CardZoneKinds.Played, CardZoneKinds.Discard, state.Zone(CardZoneKinds.Played, player.Id).Cards, timeline);
                        if (!duel.IsFinished && duel.Phase == ScoredDuelPhase.AwaitingPlayerHand && duel.Combat.Get(player.Id).IsAlive)
                        {
                            var error = Deal(workingTable, zones, player, CardDuelDealTrigger.PlayerTurnStart, ref random, draws, timeline, hook);
                            if (error != CardDuelError.None) return new CardDuelTransition(state, error);
                        }
                    }
                }
                if (!duel.IsFinished)
                    foreach (var drawn in events.OfType<CombatEvent>().Where(e => e.Effect.Kind == CombatEffectKind.DrawCards))
                    {
                        var recipient = duel.Players.FirstOrDefault(p => p.Id == drawn.Effect.Target);
                        if (recipient == null) return new CardDuelTransition(state, CardDuelError.InvalidEffectPlan);
                        int held = zones.Single(z => z.Kind == CardZoneKinds.Hand && z.ControllerId == recipient.Id).Cards.Count;
                        int capacity = recipient.Rules.Flow.EffectDrawOverflow ? 512 : recipient.Limits.HandLimit.Value;
                        DealExplicit(workingTable, zones, recipient, (int)Math.Min(drawn.Effect.Amount, Math.Max(0, capacity - held)),
                            CardDuelDealTrigger.EffectDraw, ref random, draws, timeline, hook);
                    }
                events = events.Concat(hookEvents).ToArray(); relicActivations = relicActivations.Concat(hookActivations).ToArray();
                var table = new CardTableState(workingTable.MatchId, workingTable.Cards, zones, checked(state.Revision + 1));
                var final = new CardDuelState(table, duel, random, discards, true, state.CopyFlows(), state.CopyGold());
                timeline.SetState(final);
                timeline.Add(BattleTimelineKind.ActionResolved);
                if (duel.IsFinished)
                    timeline.Add(BattleTimelineKind.BattleEnded, reason: Duel.ReadProgress(duel).Result.Reason.ToString());
                return new CardDuelTransition(final, score: score, events: events, pattern: pattern, damage: damage, discardReward: discardReward, effectPlan: effectPlan, actionCost: actionCost, draws: draws,
                    relicActivations: relicActivations, actionCompletion: actionCompletion, timeline: timeline.Entries);
            }
            catch (CardHookException exception) { return new CardDuelTransition(state, exception.Error); }
            catch (OverflowException) { return new CardDuelTransition(state, CardDuelError.Overflow); }
        }
        private CardTableState GenerateEnemyCards(CardTableState table, CardZone[] zones, ref RandomState random,
            TeamBattleState team, ParticipantId actor, EnemyWorldEffect effect, BattleTimelineRecorder timeline)
        {
            timeline.SetTeam(team);
            foreach (var recipient in team.Players.Where(p => team.Combat.Get(p.Id).IsAlive))
            {
                var created = new List<CardInstance>();
                // Bound battle-local pollution; at capacity the effect becomes a no-op.
                int owned = zones.Where(z => z.ControllerId == recipient.Id).Sum(z => z.Cards.Count);
                for (int i = 0; i < Math.Min(effect.Count, Math.Max(0, 512 - owned)); i++)
                {
                    int serial = table.Cards.Count + created.Count;
                    CardInstanceId id;
                    do { id = new CardInstanceId(team.Combat.Id + "/generated/" + serial++); }
                    while (table.Cards.Any(c => c.Id == id) || created.Any(c => c.Id == id));
                    created.Add(new CardInstance(id, new CardDefinitionId(effect.CardFace), "enemy-generated", effect.CardEffects, effect.Disabled));
                }
                if (created.Count == 0) continue;
                int index = Array.FindIndex(zones, z => z.ControllerId == recipient.Id && z.Kind == effect.Zone);
                var zone = zones[index];
                var ids = created.Select(c => c.Id).ToArray();
                // CardZone draws from the end. Reverse each new batch so generation order is draw order.
                var inserted = effect.Zone != CardZoneKinds.Deck ? zone.Cards.Concat(ids).ToArray() :
                    effect.Position == CardInsertionPosition.Bottom ? ids.Reverse().Concat(zone.Cards).ToArray() :
                    zone.Cards.Concat(ids.Reverse()).ToArray();
                if (effect.Position == CardInsertionPosition.Shuffle)
                {
                    // Insertion shuffling is not discard recycling: it does not emit OnReshuffle.
                    var shuffled = shuffler.Shuffle(inserted, random); inserted = shuffled.Cards.ToArray(); random = shuffled.Random;
                }
                zones[index] = new CardZone(zone.Id, zone.Kind, zone.ControllerId, inserted);
                table = new CardTableState(table.MatchId, table.Cards.Concat(created), zones, table.Revision);
                timeline.SetCards(table, random);
                timeline.Add(BattleTimelineKind.CardsCreated, reason: actor.Value, cards: ids, to: effect.Zone, cardOwner: recipient.Id, insertionPosition: effect.Position);
            }
            return table;
        }

        private static void Move(CardZone[] zones, ParticipantId player, string from, string to, IEnumerable<CardInstanceId> ids,
            BattleTimelineRecorder timeline)
        {
            int a = Array.FindIndex(zones, zone => zone.Kind == from && zone.ControllerId == player), b = Array.FindIndex(zones, zone => zone.Kind == to && zone.ControllerId == player);
            var moved = ids.ToArray();
            var transfer = zones[a].TransferRangeTo(zones[b], moved, zones[b].Cards.Count); zones[a] = transfer.Source; zones[b] = transfer.Target;
            if (moved.Length > 0)
            {
                var table = timeline.State.Table;
                timeline.SetCards(new CardTableState(table.MatchId, table.Cards, zones, table.Revision), timeline.State.Random);
                timeline.Add(BattleTimelineKind.CardsMoved, cards: moved, from: from, to: to, cardOwner: player);
            }
        }
        private CardDuelError Deal(CardTableState before, CardZone[] zones, BattlePlayerTurn playerTurn, CardDuelDealTrigger trigger,
            ref RandomState random, List<BattleDrawRecord> draws, BattleTimelineRecorder timeline, Func<CombatTimingKind, ParticipantId, IReadOnlyList<CardInstanceId>, string, bool> hook = null, int discardedCount = 0)
        {
            var player = playerTurn.Id;
            var context = new CardDuelDealContext(trigger, new CardTableState(before.MatchId, before.Cards, zones, before.Revision), playerTurn.Limits.HandLimit.Value, player, discardedCount);
            BattleDealResolution plan;
            try { plan = new BattleRuleResolver().ResolveDeal(new BattleDealQuery(context, playerTurn.Rules.DealPolicy, playerTurn.Relics)); }
            catch (ArgumentException) { return CardDuelError.InvalidDealPlan; }
            int remaining = plan.Count;
            if (playerTurn.Rules.Flow.UsesTurnFlow)
            {
                bool opening = trigger == CardDuelDealTrigger.OpeningTurn && playerTurn.Rules.Flow.OpeningDeal;
                bool automatic = playerTurn.Rules.Flow.AutoDrawToLimit && (trigger == CardDuelDealTrigger.PlayerTurnStart || trigger == CardDuelDealTrigger.OpeningTurn);
                remaining = opening || automatic ? context.MissingCards :
                    trigger == CardDuelDealTrigger.AfterDiscard && (playerTurn.Rules.DealPolicy as CardDuelDealPolicy)?.AfterDiscard == CardDuelRefillTiming.DiscardedCount ? plan.Count : 0;
            }
            int requestedCount = remaining;
            int d = Array.FindIndex(zones, zone => zone.Kind == CardZoneKinds.Deck && zone.ControllerId == player), h = Array.FindIndex(zones, zone => zone.Kind == CardZoneKinds.Hand && zone.ControllerId == player),
                x = Array.FindIndex(zones, zone => zone.Kind == CardZoneKinds.Discard && zone.ControllerId == player);
            if (remaining > 0 && hook?.Invoke(CombatTimingKind.BeforeDraw, player, Array.Empty<CardInstanceId>(), trigger.ToString()) == false) remaining = 0;
            var dealt = new List<CardInstanceId>();
            while (remaining > 0)
            {
                if (zones[d].Cards.Count == 0)
                {
                    if (zones[x].Cards.Count == 0) break;
                    var shuffled = shuffler.Shuffle(zones[x].Cards, random); random = shuffled.Random;
                    zones[d] = new CardZone(zones[d].Id, zones[d].Kind, zones[d].ControllerId, shuffled.Cards);
                    zones[x] = new CardZone(zones[x].Id, zones[x].Kind, zones[x].ControllerId, new CardInstanceId[0]);
                    timeline.SetCards(new CardTableState(before.MatchId, before.Cards, zones, before.Revision), random);
                    timeline.Add(BattleTimelineKind.DeckRecycled, cards: shuffled.Cards, from: CardZoneKinds.Discard,
                        to: CardZoneKinds.Deck, cardOwner: player, trigger: trigger);
                    if (hook?.Invoke(CombatTimingKind.Reshuffle, player, shuffled.Cards, trigger.ToString()) == false) break;
                }
                int count = Math.Min(remaining, zones[d].Cards.Count);
                var transfer = zones[d].DrawTo(zones[h], count, zones[h].Cards.Count); zones[d] = transfer.Source; zones[h] = transfer.Target;
                timeline.SetCards(new CardTableState(before.MatchId, before.Cards, zones, before.Revision), random);
                timeline.Add(BattleTimelineKind.CardsDealt, cards: transfer.Cards, from: CardZoneKinds.Deck,
                    to: CardZoneKinds.Hand, cardOwner: player, trigger: trigger);
                dealt.AddRange(transfer.Cards);
                remaining -= count;
            }
            if (dealt.Count > 0) hook?.Invoke(CombatTimingKind.Draw, player, dealt.AsReadOnly(), trigger.ToString());
            draws.Add(new BattleDrawRecord(plan, dealt.Count));
            return CardDuelError.None;
        }
    }
}
