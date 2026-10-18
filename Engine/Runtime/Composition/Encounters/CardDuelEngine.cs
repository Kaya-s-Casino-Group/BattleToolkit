using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public enum CardDuelAction { Play = 1, Discard, EnemyAction, EndTurn, CollectPlayed, Start, SkipPlayerTurn }
    public enum CardDuelError { None, StaleRevision, InvalidPhase, InvalidSelection, NoDiscards, InsufficientEnergy, Overflow, InvalidTarget, InvalidState, InvalidDamagePlan, InvalidDamageModifier, InvalidDiscardReward, InvalidEffectPlan, ActionBlocked, InvalidReaction, ReactionLimitExceeded, InvalidRelicState, InvalidAmountModifier, InvalidHandEffectModifier, InvalidRuleModifier, InvalidDealPlan }
    public enum CardDuelRefillTiming { NextPlayerTurn = 0, AfterAction = 1 }
    public enum CardDuelDealTrigger { OpeningTurn, AfterPlay, AfterDiscard, PlayerTurnStart }

    /// <summary>Read-only input to a pure draw policy. It decides how many cards, never moves or creates cards.</summary>
    public sealed class CardDuelDealContext
    {
        public CardDuelDealTrigger Trigger { get; }
        public CardTableState Table { get; }
        public int HandLimit { get; }
        public ParticipantId PlayerId { get; }
        public int HandCount => Table.Zones.Single(zone => zone.Kind == CardZoneKinds.Hand && zone.ControllerId == PlayerId).Cards.Count;
        public int MissingCards => Math.Max(0, HandLimit - HandCount);
        internal CardDuelDealContext(CardDuelDealTrigger trigger, CardTableState table, int handLimit, ParticipantId player)
        { Trigger = trigger; Table = table; HandLimit = handLimit; PlayerId = player; }
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
            if (!Enum.IsDefined(typeof(CardDuelRefillTiming), afterPlay) || !Enum.IsDefined(typeof(CardDuelRefillTiming), afterDiscard))
                throw new ArgumentException("Unknown refill timing.");
            AfterPlay = afterPlay; AfterDiscard = afterDiscard;
        }
        public int GetDrawCount(CardDuelDealContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
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
        public bool HasStarted { get; }
        public long Revision => Table.Revision;
        public CardZone Zone(string kind) => Zone(kind, Duel.PlayerId);
        public CardZone Zone(string kind, ParticipantId player) => Table.Zones.Single(zone => zone.Kind == kind && zone.ControllerId == player);
        internal CardDuelState(CardTableState table, TeamBattleState duel, RandomState random, IDictionary<ParticipantId, int> discards, bool hasStarted)
        { Table = table; Duel = duel; Random = random; this.discards = new Dictionary<ParticipantId, int>(discards); HasStarted = hasStarted; }
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
        internal CardDuelTransition(CardDuelState state, CardDuelError error = CardDuelError.None,
            ScoreResult score = null, IEnumerable<CombatRuleEvent> events = null, PokerHandClassification pattern = null, DamagePlan damage = null, DiscardRewardResolution discardReward = null, BattleEffectPlan effectPlan = null,
            BattleActionCostResolution actionCost = null, IEnumerable<BattleDrawRecord> draws = null,
            IEnumerable<RelicTriggerActivation> relicActivations = null, BattleActionCompletion actionCompletion = null)
        {
            State = state; Error = error; Score = score; Pattern = pattern; Damage = damage; DiscardReward = discardReward; EffectPlan = effectPlan;
            ActionCost = actionCost; ActionCompletion = actionCompletion; Draws = Guard.View(Guard.Copy(draws ?? Array.Empty<BattleDrawRecord>(), nameof(draws)));
            RelicActivations = Guard.View(Guard.Copy(relicActivations ?? Array.Empty<RelicTriggerActivation>(), nameof(relicActivations)));
            RuleEvents = Guard.View(Guard.Copy(events ?? Array.Empty<CombatRuleEvent>(), nameof(events)));
            Events = Guard.View(RuleEvents.OfType<CombatEvent>().ToArray());
        }
    }

    /// <summary>
    /// Card/health coordinator for both rosters: independent player decks and costs, then ordered enemy actions.
    /// No Draw target-score victory, AbilityWorld, Run rewards or Unity dependency.
    /// </summary>
    public sealed class CardDuelEngine
    {
        private readonly Dictionary<CardDefinitionId, CardDefinition> faces = StandardDeck.Cards().ToDictionary(card => card.Id);
        private readonly HmacDeckShuffler shuffler = new HmacDeckShuffler();
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
            ICardDuelDealPolicy dealPolicy = null)
        {
            Duel = new TeamBattleEngine(rules); Scoring = scoring ?? ScoreProfile.Default;
            defaultPlayerRules = new BattlePlayerRules(handSize: handSize, discards: discards, dealPolicy: dealPolicy);
        }
        public CardDefinition Face(CardInstance card) => faces[card.DefinitionId];
        public static ZoneId ZoneId(string encounterId, string kind) => new ZoneId(encounterId + "/" + kind);

        /// <summary>Create an undealt battle. Start enters the first player turn and deals the hand for free.</summary>
        public CardDuelState Create(string id, RandomState random, IEnumerable<DeckEntry> deck = null, int? initialHealth = null,
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
                if (entries.Length < duel.Player(player.Id).Limits.HandLimit.Value || entries.Length > 512 || entries.Any(entry => entry == null || !faces.ContainsKey(entry.DefinitionId)) ||
                    entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
                    throw new ArgumentException("Each player needs a playable deck of standard faces with unique origins, at least the hand size.");
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
                return new CardDuelTransition(new CardDuelState(table, result.State, state.Random, discards, state.HasStarted));
            }
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
        public CardDuelTransition PreviewHand(CardDuelState state, ParticipantId actor, IEnumerable<CardInstanceId> selected)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (!ReferenceEquals(state.Duel.Rules, Duel.Definition)) return new CardDuelTransition(state, CardDuelError.InvalidState);
            if (!state.HasStarted || !state.Duel.Players.Any(player => player.Id == actor)) return new CardDuelTransition(state, CardDuelError.InvalidPhase);
            var ids = (selected ?? new CardInstanceId[0]).ToArray();
            var hand = state.Zone(CardZoneKinds.Hand, actor);
            if (ids.Length < 1 || ids.Length > state.Duel.Player(actor).Rules.MaxPlayCards || ids.Distinct().Count() != ids.Length ||
                ids.Any(id => !hand.Cards.Contains(id) || state.Table.GetCard(id).IsDisabled))
                return new CardDuelTransition(state, CardDuelError.InvalidSelection);
            try
            {
                var ordered = hand.Cards.Where(ids.Contains).Select(state.Table.GetCard).ToArray();
                var pattern = PokerHandClassifier.Classify(ordered.Select(card => new EffectiveCard(card.Id, Face(card).Rank, Face(card).Suit)));
                var level = state.Duel.Player(actor).Rules.HandLevels.LevelFor(pattern.Category);
                var score = ScoreEngine.EvaluatePattern(Duel.NextScoreId(state.Duel), Scoring, pattern, level,
                    ordered.Select(card => new ScoringCard(card.Id.Value, new CardFace(Face(card).Rank, Face(card).Suit), effects: card.Effects)), new ScoringRelic[0]);
                return new CardDuelTransition(state, score: score, pattern: pattern);
            }
            catch (OverflowException) { return new CardDuelTransition(state, CardDuelError.Overflow); }
        }

        // Compatibility overload: first player, or the next enemy. Multiplayer commands always identify their actor.
        public CardDuelTransition Execute(CardDuelState state, long expectedRevision, CardDuelAction action,
            IEnumerable<CardInstanceId> selected = null, BattleTargetBindings targetBindings = null) =>
            Execute(state, expectedRevision, action == CardDuelAction.EnemyAction ? state.Duel.NextEnemy : state.Duel.PlayerId,
                action, selected, targetBindings: targetBindings);
        public CardDuelTransition Execute(CardDuelState state, long expectedRevision, ParticipantId actor, CardDuelAction action,
            IEnumerable<CardInstanceId> selected = null, IEnumerable<ParticipantId> targets = null, BattleTargetBindings targetBindings = null)
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
                ids.Length > (action == CardDuelAction.Play ? state.Duel.Player(actor).Rules.MaxPlayCards : state.Duel.Player(actor).Rules.MaxDiscardCards) ||
                ids.Distinct().Count() != ids.Length || ids.Any(id => !state.Zone(CardZoneKinds.Hand, actor).Cards.Contains(id) || state.Table.GetCard(id).IsDisabled))
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
                    if (!actionCost.CanAfford) return new CardDuelTransition(state, CardDuelError.InsufficientEnergy);
                }
                var draws = new List<BattleDrawRecord>();
                BattleActionCompletion actionCompletion = null;
                IReadOnlyList<RelicTriggerActivation> relicActivations = Array.Empty<RelicTriggerActivation>();
                var zones = state.Table.Zones.ToArray(); var random = state.Random; var duel = state.Duel;
                var discards = state.Duel.Players.ToDictionary(x => x.Id, x => state.DiscardsFor(x.Id)); ScoreResult score = null; PokerHandClassification pattern = null; DamagePlan damage = null; DiscardRewardResolution discardReward = null; BattleEffectPlan effectPlan = null; IReadOnlyList<CombatRuleEvent> events = Array.Empty<CombatRuleEvent>();
                if (action == CardDuelAction.Start)
                {
                    var opening = Duel.ResolveOpening(duel);
                    if (!opening.Accepted) return new CardDuelTransition(state, opening.Error);
                    duel = opening.State; events = opening.RuleEvents; relicActivations = opening.RelicActivations;
                    foreach (var player in duel.Players.Where(player => !duel.IsFinished && duel.Combat.Get(player.Id).IsAlive))
                    {
                        var error = Deal(state.Table, zones, player, CardDuelDealTrigger.OpeningTurn, ref random, draws);
                        if (error != CardDuelError.None) return new CardDuelTransition(state, error);
                    }
                }
                else if (action == CardDuelAction.Play)
                {
                    // Authority determines both submitted membership and score, never accepting a UI-provided numeric score.
                    var ordered = state.Zone(CardZoneKinds.Hand, actor).Cards.Where(ids.Contains).Select(state.Table.GetCard).ToArray();
                    var candidate = PreviewHand(state, actor, ids);
                    if (!candidate.Accepted) return candidate;
                    pattern = candidate.Pattern; score = candidate.Score;
                    var hand = new HandAttackInfo(pattern, ordered.Select(card => new AttackCardSnapshot(card,
                        new EffectiveCard(card.Id, Face(card).Rank, Face(card).Suit))));
                    var hit = Duel.Execute(duel, duel.Revision, actor, ScoredDuelActionKind.ScoredHand, score, targets, hand,
                        targetBindings: targetBindings);
                    if (!hit.Accepted) return new CardDuelTransition(state, hit.Error);
                    actionCost = hit.ActionCost;
                    relicActivations = hit.RelicActivations; actionCompletion = hit.ActionCompletion;
                    duel = hit.State; events = hit.RuleEvents; damage = hit.Damage; effectPlan = hit.EffectPlan; Move(zones, actor, CardZoneKinds.Hand, CardZoneKinds.Played, ordered.Select(card => card.Id));
                    if (!duel.IsFinished)
                    {
                        var error = Deal(state.Table, zones, duel.Player(actor), CardDuelDealTrigger.AfterPlay, ref random, draws);
                        if (error != CardDuelError.None) return new CardDuelTransition(state, error);
                    }
                }
                else if (action == CardDuelAction.Discard)
                {
                    var cards = state.Zone(CardZoneKinds.Hand, actor).Cards.Where(ids.Contains).Select(state.Table.GetCard)
                        .Select(card => new DiscardCardSnapshot(card, new EffectiveCard(card.Id, Face(card).Rank, Face(card).Suit))).ToArray();
                    var spent = Duel.Execute(duel, duel.Revision, actor, ScoredDuelActionKind.Discard, requestedTargets: targets, discardedCards: cards);
                    if (!spent.Accepted) return new CardDuelTransition(state, spent.Error);
                    actionCost = spent.ActionCost;
                    relicActivations = spent.RelicActivations; actionCompletion = spent.ActionCompletion;
                    duel = spent.State; events = spent.RuleEvents; discardReward = spent.DiscardReward; effectPlan = spent.EffectPlan;
                    Move(zones, actor, CardZoneKinds.Hand, CardZoneKinds.Discard, state.Zone(CardZoneKinds.Hand, actor).Cards.Where(ids.Contains));
                    if (!duel.Player(actor).Rules.UnlimitedDiscards) discards[actor]--;
                    // Completion/boundary victories stop dealing; preserve legacy discard settlement otherwise.
                    if (!duel.IsFinished || !relicActivations.Any(activation => activation.BoundaryTiming.HasValue || activation.ActionTiming.HasValue))
                    {
                        var error = Deal(state.Table, zones, duel.Player(actor), CardDuelDealTrigger.AfterDiscard, ref random, draws);
                        if (error != CardDuelError.None) return new CardDuelTransition(state, error);
                    }
                }
                else if (action == CardDuelAction.EndTurn)
                {
                    var ended = Duel.Execute(duel, duel.Revision, actor, ScoredDuelActionKind.EndPlayerTurn);
                    if (!ended.Accepted) return new CardDuelTransition(state, ended.Error);
                    duel = ended.State; events = ended.RuleEvents;
                    relicActivations = ended.RelicActivations;
                }
                else if (action == CardDuelAction.SkipPlayerTurn)
                {
                    var skipped = Duel.Execute(duel, duel.Revision, actor, ScoredDuelActionKind.SkipPlayerTurn);
                    if (!skipped.Accepted) return new CardDuelTransition(state, skipped.Error);
                    duel = skipped.State; events = skipped.RuleEvents;
                    relicActivations = skipped.RelicActivations;
                }
                else if (action == CardDuelAction.CollectPlayed)
                {
                    Move(zones, actor, CardZoneKinds.Played, CardZoneKinds.Discard, state.Zone(CardZoneKinds.Played, actor).Cards);
                }
                else
                {
                    var response = Duel.Execute(duel, duel.Revision, actor, ScoredDuelActionKind.EnemyAction, requestedTargets: targets);
                    if (!response.Accepted) return new CardDuelTransition(state, response.Error);
                    duel = response.State; events = response.RuleEvents; effectPlan = response.EffectPlan;
                    relicActivations = response.RelicActivations;
                    foreach (var player in duel.Players)
                    {
                        Move(zones, player.Id, CardZoneKinds.Played, CardZoneKinds.Discard, state.Zone(CardZoneKinds.Played, player.Id).Cards);
                        if (!duel.IsFinished && duel.Phase == ScoredDuelPhase.AwaitingPlayerHand && duel.Combat.Get(player.Id).IsAlive)
                        {
                            var error = Deal(state.Table, zones, player, CardDuelDealTrigger.PlayerTurnStart, ref random, draws);
                            if (error != CardDuelError.None) return new CardDuelTransition(state, error);
                        }
                    }
                }
                var table = new CardTableState(state.Table.MatchId, state.Table.Cards, zones, checked(state.Revision + 1));
                return new CardDuelTransition(new CardDuelState(table, duel, random, discards, true), score: score, events: events, pattern: pattern, damage: damage, discardReward: discardReward, effectPlan: effectPlan, actionCost: actionCost, draws: draws,
                    relicActivations: relicActivations, actionCompletion: actionCompletion);
            }
            catch (OverflowException) { return new CardDuelTransition(state, CardDuelError.Overflow); }
        }
        private static void Move(CardZone[] zones, ParticipantId player, string from, string to, IEnumerable<CardInstanceId> ids)
        {
            int a = Array.FindIndex(zones, zone => zone.Kind == from && zone.ControllerId == player), b = Array.FindIndex(zones, zone => zone.Kind == to && zone.ControllerId == player);
            var transfer = zones[a].TransferRangeTo(zones[b], ids, zones[b].Cards.Count); zones[a] = transfer.Source; zones[b] = transfer.Target;
        }
        private CardDuelError Deal(CardTableState before, CardZone[] zones, BattlePlayerTurn playerTurn, CardDuelDealTrigger trigger,
            ref RandomState random, List<BattleDrawRecord> draws)
        {
            var player = playerTurn.Id;
            var context = new CardDuelDealContext(trigger, new CardTableState(before.MatchId, before.Cards, zones, before.Revision), playerTurn.Limits.HandLimit.Value, player);
            BattleDealResolution plan;
            try { plan = new BattleRuleResolver().ResolveDeal(new BattleDealQuery(context, playerTurn.Rules.DealPolicy, playerTurn.Relics)); }
            catch (ArgumentException) { return CardDuelError.InvalidDealPlan; }
            int remaining = plan.Count;
            int d = Array.FindIndex(zones, zone => zone.Kind == CardZoneKinds.Deck && zone.ControllerId == player), h = Array.FindIndex(zones, zone => zone.Kind == CardZoneKinds.Hand && zone.ControllerId == player),
                x = Array.FindIndex(zones, zone => zone.Kind == CardZoneKinds.Discard && zone.ControllerId == player);
            while (remaining > 0)
            {
                if (zones[d].Cards.Count == 0)
                {
                    if (zones[x].Cards.Count == 0) break;
                    var shuffled = shuffler.Shuffle(zones[x].Cards, random); random = shuffled.Random;
                    zones[d] = new CardZone(zones[d].Id, zones[d].Kind, zones[d].ControllerId, shuffled.Cards);
                    zones[x] = new CardZone(zones[x].Id, zones[x].Kind, zones[x].ControllerId, new CardInstanceId[0]);
                }
                int count = Math.Min(remaining, zones[d].Cards.Count);
                var transfer = zones[d].DrawTo(zones[h], count, zones[h].Cards.Count); zones[d] = transfer.Source; zones[h] = transfer.Target;
                remaining -= count;
            }
            draws.Add(new BattleDrawRecord(plan, plan.Count - remaining));
            return CardDuelError.None;
        }
    }
}
