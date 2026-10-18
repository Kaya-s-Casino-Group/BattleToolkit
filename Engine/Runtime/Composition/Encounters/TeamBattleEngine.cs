using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public enum PlayerBlockExpiry { Retain = 0, AfterEnemyPhase = 1 }

    /// <summary>Shared encounter rules only. No character stats, player energy or enemy attacks.</summary>
    public sealed class BattleRules
    {
        public string Version { get; }
        public int RoundLimit { get; }
        public PlayerBlockExpiry PlayerBlockExpiry { get; }
        public CombatStatusCatalog StatusCatalog { get; }
        public ICombatReactionProvider ReactionProvider { get; }
        public CombatReactionLimits ReactionLimits { get; }
        public BattleRules(string version, int roundLimit,
            PlayerBlockExpiry playerBlockExpiry = PlayerBlockExpiry.Retain, CombatStatusCatalog statusCatalog = null,
            ICombatReactionProvider reactionProvider = null, CombatReactionLimits reactionLimits = null)
        {
            Version = Guard.Text(version, nameof(version));
            if (roundLimit < 1 || roundLimit > 1000) throw new ArgumentOutOfRangeException(nameof(roundLimit));
            if (!Enum.IsDefined(typeof(PlayerBlockExpiry), playerBlockExpiry)) throw new ArgumentOutOfRangeException(nameof(playerBlockExpiry));
            if (statusCatalog != null && statusCatalog.Definitions.Values.Any(definition =>
                definition.DefaultLifetime.Kind == StatusLifetimeKind.ThroughNextPhase &&
                definition.DefaultLifetime.PhaseKey != BattleStatusTiming.PlayerPhase && definition.DefaultLifetime.PhaseKey != BattleStatusTiming.EnemyPhase))
                throw new ArgumentException("Status definitions must use this battle's player or enemy phase.", nameof(statusCatalog));
            RoundLimit = roundLimit;
            PlayerBlockExpiry = playerBlockExpiry;
            StatusCatalog = statusCatalog;
            ReactionProvider = reactionProvider;
            ReactionLimits = reactionLimits ?? new CombatReactionLimits();
        }
    }
    /// <summary>Immutable player-specific card rules, action budget and discard reward, captured when entering the encounter.</summary>
    public sealed class BattlePlayerRules
    {
        public int MaxEnergy { get; }
        public int InitialEnergy { get; }
        public int PlayEnergyCost { get; }
        public int DiscardEnergyCost { get; }
        public int HandSize { get; }
        public int Discards { get; }
        public bool UnlimitedDiscards { get; }
        public int MaxPlayCards { get; }
        public int MaxDiscardCards { get; }
        // Selection envelope for views/legacy callers. Each action validates its own limit.
        public int MaxSelection => Math.Max(MaxPlayCards, MaxDiscardCards);
        public ICardDuelDealPolicy DealPolicy { get; }
        public IDiscardRewardRule DiscardReward { get; }
        public IScoredHandDamageRule DamagePolicy { get; }
        /// <summary>Optional per-effect rule. When set, it replaces the legacy damage-policy execution path.</summary>
        public IScoredHandEffectRule EffectRule { get; }
        public BattleHandLevels HandLevels { get; }
        public BattlePlayerRules(int maxEnergy = 3, int initialEnergy = 3, int playEnergyCost = 1, int discardEnergyCost = 1,
            int handSize = 8, int discards = 3, ICardDuelDealPolicy dealPolicy = null,
            bool unlimitedDiscards = false, int maxPlayCards = 5, int maxDiscardCards = 5, IDiscardRewardRule discardReward = null,
            IScoredHandDamageRule damagePolicy = null, BattleHandLevels handLevels = null, IScoredHandEffectRule effectRule = null)
        {
            if (maxEnergy < 1 || initialEnergy < 0 || playEnergyCost < 0 || discardEnergyCost < 0 || handSize < 1 || handSize > 52 || discards < 0 || discards > 1000)
                throw new ArgumentException("Invalid player card/action budget.");
            // Poker classification accepts at most five submitted cards. Discard has no poker-pattern constraint.
            if (maxPlayCards < 1 || maxPlayCards > 5 || maxDiscardCards < 1 || maxDiscardCards > 52)
                throw new ArgumentException("Invalid play/discard card limit.");
            if (damagePolicy != null && effectRule != null) throw new ArgumentException("Choose either a damage policy or a scored-hand effect rule.");
            MaxEnergy = maxEnergy; InitialEnergy = initialEnergy; PlayEnergyCost = playEnergyCost; DiscardEnergyCost = discardEnergyCost;
            HandSize = handSize; Discards = discards; DealPolicy = dealPolicy ?? new CardDuelDealPolicy();
            DiscardReward = discardReward ?? new NoDiscardRewardRule();
            DamagePolicy = damagePolicy ?? new EqualScoreDamageRule();
            EffectRule = effectRule;
            HandLevels = handLevels ?? new BattleHandLevels();
            UnlimitedDiscards = unlimitedDiscards;
            MaxPlayCards = Math.Min(maxPlayCards, handSize); MaxDiscardCards = Math.Min(maxDiscardCards, handSize);
        }
    }
    /// <summary>Encounter input. Deck entries belong to this player; no Unity or presentation references.</summary>
    public sealed class BattlePlayerSetup
    {
        public ParticipantId Id { get; }
        public CombatantDefinition Definition { get; }
        public int? InitialHealth { get; }
        public IReadOnlyList<DeckEntry> Deck { get; }
        public BattlePlayerRules Rules { get; }
        public BattleActorDamageRules DamageRules { get; }
        public BattleActorTargetRules TargetRules { get; }
        public IReadOnlyList<BattleRelicSetup> Relics { get; }
        public BattlePlayerSetup(ParticipantId id, CombatantDefinition definition, IEnumerable<DeckEntry> deck = null, int? initialHealth = null,
            BattlePlayerRules rules = null, BattleActorDamageRules damageRules = null, BattleActorTargetRules targetRules = null,
            IEnumerable<BattleRelicSetup> relics = null)
        {
            if (!id.IsValid || definition == null || initialHealth <= 0 || initialHealth > definition.MaxHealth)
                throw new ArgumentException("Invalid player setup.");
            Id = id; Definition = definition; InitialHealth = initialHealth; Rules = rules; DamageRules = damageRules ?? new BattleActorDamageRules(); TargetRules = targetRules ?? new BattleActorTargetRules();
            Deck = deck == null ? null : Guard.View(Guard.Copy(deck, nameof(deck)));
            Relics = Guard.View(Guard.Copy(relics ?? Array.Empty<BattleRelicSetup>(), nameof(relics)));
        }
    }
    public sealed class BattleEnemySetup
    {
        public ParticipantId Id { get; }
        public CombatantDefinition Definition { get; }
        public long Damage { get; }
        public BattleActorDamageRules DamageRules { get; }
        public BattleActorTargetRules TargetRules { get; }
        public IEnemyBattleEffectRule EffectRule { get; }
        public BattleEnemySetup(ParticipantId id, CombatantDefinition definition, long damage, BattleActorDamageRules damageRules = null, BattleActorTargetRules targetRules = null,
            IEnemyBattleEffectRule effectRule = null)
        {
            if (!id.IsValid || definition == null || damage < 0) throw new ArgumentException("Invalid enemy setup.");
            Id = id; Definition = definition; Damage = damage; DamageRules = damageRules ?? new BattleActorDamageRules(); TargetRules = targetRules ?? new BattleActorTargetRules();
            EffectRule = effectRule;
        }
    }
    public sealed class BattlePlayerTurn
    {
        public ParticipantId Id { get; }
        public PlayerTurnState Energy { get; }
        public BattlePlayerRules Rules { get; }
        public bool HasEnded { get; }
        public BattleActorDamageRules DamageRules { get; }
        public BattleActorTargetRules TargetRules { get; }
        public BattleRelicLoadout Relics { get; }
        /// <summary>Effective encounter capacities. Rules retains the immutable base configuration.</summary>
        public BattleLimitResolution Limits { get; }
        internal BattlePlayerTurn(ParticipantId id, PlayerTurnState energy, bool ended, BattlePlayerRules rules, BattleActorDamageRules damageRules,
            BattleActorTargetRules targetRules, BattleRelicLoadout relics, BattleLimitResolution limits)
        {
            if (relics == null || relics.OwnerId != id) throw new ArgumentException("Relics must belong to this player.");
            if (limits == null || limits.OwnerId != id || energy.MaxEnergy != limits.MaxEnergy.Value)
                throw new ArgumentException("Effective limits must match this player and energy budget.");
            Id = id; Energy = energy; HasEnded = ended; Rules = rules;
            DamageRules = damageRules ?? new BattleActorDamageRules(); TargetRules = targetRules ?? new BattleActorTargetRules(); Relics = relics;
            Limits = limits;
        }
    }
    public sealed class BattleActionRecord
    {
        public ParticipantId Actor { get; }
        public ScoredDuelActionKind Kind { get; }
        public int? ScoreIndex { get; }
        internal BattleActionRecord(ParticipantId actor, ScoredDuelActionKind kind, int? scoreIndex = null)
        { Actor = actor; Kind = kind; ScoreIndex = scoreIndex; }
    }
    /// <summary>Immutable shared player phase, then an ordered enemy queue. No active-player cursor in rules.</summary>
    public sealed class TeamBattleState
    {
        internal BattleRules Rules { get; }
        public CombatState Combat { get; }
        public IReadOnlyList<BattlePlayerTurn> Players { get; }
        public IReadOnlyList<BattleEnemySetup> Enemies { get; }
        public IReadOnlyList<ParticipantId> PendingEnemies { get; }
        public IReadOnlyList<ScoreResult> Scores { get; }
        public IReadOnlyList<BattleActionRecord> Actions { get; }
        public ScoredDuelPhase Phase { get; }
        public long Revision { get; }
        public int CompletedRounds { get; }
        public bool IsFinished => Phase == ScoredDuelPhase.Won || Phase == ScoredDuelPhase.Lost;
        public long TotalScore => Scores.Aggregate(0L, (sum, score) => checked(sum + score.Total));
        // First-seat aliases retain the existing 1v1 callers. Multi-unit consumers must use IDs.
        public ParticipantId PlayerId => Players[0].Id;
        public ParticipantId EnemyId => Enemies[0].Id;
        public PlayerTurnState PlayerTurn => Players[0].Energy;
        public ParticipantId NextEnemy => PendingEnemies.FirstOrDefault(id => Combat.Get(id).IsAlive);
        public BattlePlayerTurn Player(ParticipantId id) => Players.Single(player => player.Id == id);
        public BattleActorDamageRules DamageRulesFor(ParticipantId id)
        {
            var player = Players.FirstOrDefault(unit => unit.Id == id);
            return player != null ? player.DamageRules : Enemies.Single(unit => unit.Id == id).DamageRules;
        }
        public BattleActorTargetRules TargetRulesFor(ParticipantId id)
        {
            var player = Players.FirstOrDefault(unit => unit.Id == id);
            return player != null ? player.TargetRules : Enemies.Single(unit => unit.Id == id).TargetRules;
        }
        public bool HasPlayerWindow(ParticipantId id) => Phase == ScoredDuelPhase.AwaitingPlayerHand &&
            Players.Any(player => player.Id == id && !player.HasEnded && Combat.Get(id).IsAlive);
        public bool IsActionBlocked(ParticipantId id) => CombatStatusLifecycle.Blockers(Combat, id, Rules.StatusCatalog).Count > 0;
        public bool CanAct(ParticipantId id) => HasPlayerWindow(id) && !IsActionBlocked(id);
        public ParticipantId NextBlockedPlayer => Players.Where(player => HasPlayerWindow(player.Id) && IsActionBlocked(player.Id))
            .Select(player => player.Id).FirstOrDefault();
        internal TeamBattleState(BattleRules rules, CombatState combat, IEnumerable<BattlePlayerTurn> players,
            IEnumerable<BattleEnemySetup> enemies, ScoredDuelPhase phase, IEnumerable<ParticipantId> pending,
            long revision = 0, int rounds = 0, IEnumerable<ScoreResult> scores = null, IEnumerable<BattleActionRecord> actions = null)
        {
            Rules = rules; Combat = combat; Players = Guard.View(players.ToArray()); Enemies = Guard.View(enemies.ToArray());
            Phase = phase; PendingEnemies = Guard.View(pending.ToArray()); Revision = revision; CompletedRounds = rounds;
            Scores = Guard.View((scores ?? new ScoreResult[0]).ToArray()); Actions = Guard.View((actions ?? new BattleActionRecord[0]).ToArray());
        }
    }
    public sealed class TeamBattleTransition
    {
        public TeamBattleState State { get; }
        public CardDuelError Error { get; }
        public bool Accepted => Error == CardDuelError.None;
        public IReadOnlyList<CombatRuleEvent> RuleEvents { get; }
        public IReadOnlyList<CombatEvent> Events { get; }
        public DamagePlan Damage { get; }
        public DiscardRewardResolution DiscardReward { get; }
        /// <summary>The resolved action batch, excluding later phase maintenance. Null for commands without an effect plan.</summary>
        public BattleEffectPlan EffectPlan { get; }
        public BattleActionCostResolution ActionCost { get; }
        public IReadOnlyList<RelicTriggerActivation> RelicActivations { get; }
        public BattleActionCompletion ActionCompletion { get; }
        internal TeamBattleTransition(TeamBattleState state, CardDuelError error = CardDuelError.None, IEnumerable<CombatRuleEvent> events = null, DamagePlan damage = null, DiscardRewardResolution discardReward = null, BattleEffectPlan effectPlan = null,
            BattleActionCostResolution actionCost = null, IEnumerable<RelicTriggerActivation> relicActivations = null, BattleActionCompletion actionCompletion = null)
        {
            State = state; Error = error; Damage = damage; DiscardReward = discardReward; EffectPlan = effectPlan;
            ActionCost = actionCost; ActionCompletion = actionCompletion;
            RelicActivations = Guard.View(Guard.Copy(relicActivations ?? Array.Empty<RelicTriggerActivation>(), nameof(relicActivations)));
            RuleEvents = Guard.View(Guard.Copy(events ?? Array.Empty<CombatRuleEvent>(), nameof(events)));
            Events = Guard.View(RuleEvents.OfType<CombatEvent>().ToArray());
        }
    }
    /// <summary>Combat and round authority. Cards/scoring legality belong to CardDuelEngine; animations belong to the host.
    /// Commands serialize by revision even though players share one phase.</summary>
    public sealed class TeamBattleEngine
    {
        public BattleRules Definition { get; }
        public TeamBattleEngine(BattleRules definition)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        }
        public TeamBattleState Create(string id, IEnumerable<BattlePlayerSetup> players, IEnumerable<BattleEnemySetup> enemies)
        {
            var p = players.ToArray(); var e = enemies.ToArray();
            if (p.Length == 0 || e.Length == 0 || p.Length + e.Length > 64 || p.Any(x => x == null) || e.Any(x => x == null) ||
                p.Select(x => x.Id).Concat(e.Select(x => x.Id)).Distinct().Count() != p.Length + e.Length)
                throw new ArgumentException("A battle needs distinct participants on both teams (at most 64 units).");
            var relicIds = p.SelectMany(player => player.Relics).Select(relic => relic.Instance.Id).ToArray();
            if (relicIds.Distinct(StringComparer.Ordinal).Count() != relicIds.Length)
                throw new ArgumentException("Relic instance identities must be unique across the battle.");
            foreach (var player in p)
                (player.Rules?.EffectRule as IBattleStatusCatalogValidator)?.ValidateStatusCatalog(Definition.StatusCatalog);
            foreach (var enemy in e)
                (enemy.EffectRule as IBattleStatusCatalogValidator)?.ValidateStatusCatalog(Definition.StatusCatalog);
            return new TeamBattleState(Definition, new CombatState(id, 0,
                p.Select(x => x.Definition.Create(x.Id, "player", x.InitialHealth)).Concat(e.Select(x => x.Definition.Create(x.Id, "enemy")))),
                p.Select(x => {
                    var rules = x.Rules ?? new BattlePlayerRules();
                    var relics = new BattleRelicLoadout(x.Id, x.Relics);
                    RelicHandEffectValidation.Catalog(relics, rules, Definition.StatusCatalog);
                    RelicTriggerValidation.Catalog(relics, Definition.StatusCatalog);
                    var limits = new BattleRuleResolver().ResolveLimits(new BattleLimitQuery(rules, relics));
                    BattleRuleResolver.Value(rules, relics, BattleRuleValueKey.PlayEnergyCost);
                    BattleRuleResolver.Value(rules, relics, BattleRuleValueKey.DiscardEnergyCost);
                    return new BattlePlayerTurn(x.Id, new PlayerTurnState(limits.MaxEnergy.Value, limits.InitialEnergy.Value), false, rules, x.DamageRules, x.TargetRules,
                        relics, limits);
                }),
                e, ScoredDuelPhase.AwaitingPlayerHand, new ParticipantId[0]);
        }
        public string NextScoreId(TeamBattleState state) => state.Combat.Id + "/score/" + state.Scores.Count;
        /// <summary>Pure affordability query, not phase/selection validation or authorization to act.</summary>
        public BattleActionCostResolution QueryActionCost(TeamBattleState state, ParticipantId actor, ScoredDuelActionKind action)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (!ReferenceEquals(state.Rules, Definition)) throw new ArgumentException("Cost query requires this battle's state.");
            return new BattleRuleResolver().ResolveActionCost(new BattleActionCostQuery(state, actor, action));
        }
        /// <summary>Authority-only runtime commit for the existing loadout. This is not a player action or a snapshot restore.
        /// No combat effects, time advancement, cooldown consumption or growth are implied.</summary>
        public TeamBattleTransition UpdateRelicRuntime(TeamBattleState state, long expectedRevision, ParticipantId owner,
            IEnumerable<BattleRelicState> relics)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (!ReferenceEquals(state.Rules, Definition)) return new TeamBattleTransition(state, CardDuelError.InvalidState);
            if (expectedRevision != state.Revision) return new TeamBattleTransition(state, CardDuelError.StaleRevision);
            int index = state.Players.ToList().FindIndex(player => player.Id == owner);
            if (index < 0) return new TeamBattleTransition(state, CardDuelError.InvalidRelicState);
            try
            {
                var players = state.Players.ToArray(); var current = players[index];
                var updated = current.Relics.WithRuntime(relics);
                players[index] = new BattlePlayerTurn(current.Id, current.Energy, current.HasEnded, current.Rules,
                    current.DamageRules, current.TargetRules, updated, current.Limits);
                return new TeamBattleTransition(new TeamBattleState(Definition, state.Combat, players, state.Enemies, state.Phase,
                    state.PendingEnemies, checked(state.Revision + 1), state.CompletedRounds, state.Scores, state.Actions));
            }
            catch (OverflowException) { return new TeamBattleTransition(state, CardDuelError.Overflow); }
            catch (ArgumentException) { return new TeamBattleTransition(state, CardDuelError.InvalidRelicState); }
        }
        public IReadOnlyList<ParticipantId> Targets(TeamBattleState state, ParticipantId source, IEnumerable<ParticipantId> requested = null)
        {
            ValidateTargetSource(state, source);
            return BattleTargetResolver.Resolve(state, source, EffectTargetSpec.Opponents(), requested);
        }
        public IReadOnlyList<ParticipantId> AllyTargets(TeamBattleState state, ParticipantId source, IEnumerable<ParticipantId> requested = null)
        {
            ValidateTargetSource(state, source);
            return BattleTargetResolver.Resolve(state, source, EffectTargetSpec.Allies(), requested);
        }
        private void ValidateTargetSource(TeamBattleState state, ParticipantId source)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (!ReferenceEquals(state.Rules, Definition) ||
                !state.Combat.Combatants.Any(unit => unit.Id == source && unit.IsAlive))
                throw new ArgumentException("Targeting requires a living source in this battle.");
        }
        /// <summary>Forecast the requested enemy in this round's remaining queue, assuming all players end now.
        /// Reuses Execute for phase transitions and preceding enemies, so targeting and state-dependent modifiers
        /// see the same combat state as settlement. Returns null if the enemy will not act before the phase ends.
        /// Policies must be pure and deterministic, as for Execute. Recompute after any state change.</summary>
        public EnemyActionPreview PreviewEnemyAction(TeamBattleState state, ParticipantId actor)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (!ReferenceEquals(state.Rules, Definition))
                return new EnemyActionPreview(actor, state.Revision, new TeamBattleTransition(state, CardDuelError.InvalidState));
            if (!state.Enemies.Any(enemy => enemy.Id == actor))
                return new EnemyActionPreview(actor, state.Revision, new TeamBattleTransition(state, CardDuelError.InvalidTarget));
            if (state.IsFinished || !state.Combat.Get(actor).IsAlive) return null;

            var forecast = state;
            foreach (var player in state.Players)
            {
                if (!forecast.HasPlayerWindow(player.Id)) continue;
                var ended = Execute(forecast, forecast.Revision, player.Id, forecast.IsActionBlocked(player.Id)
                    ? ScoredDuelActionKind.SkipPlayerTurn : ScoredDuelActionKind.EndPlayerTurn);
                if (!ended.Accepted) return new EnemyActionPreview(actor, state.Revision, ended);
                forecast = ended.State;
            }
            // An enemy that already acted must not show a forecast of the next round during this phase.
            if (!forecast.PendingEnemies.Contains(actor)) return null;
            while (forecast.Phase == ScoredDuelPhase.AwaitingEnemyAction)
            {
                var next = forecast.NextEnemy;
                var action = Execute(forecast, forecast.Revision, next, ScoredDuelActionKind.EnemyAction);
                if (!action.Accepted || next == actor) return new EnemyActionPreview(actor, state.Revision, action);
                forecast = action.State;
            }
            return null;
        }

        public TeamBattleTransition Execute(TeamBattleState state, long revision, ParticipantId actor, ScoredDuelActionKind action,
            ScoreResult score = null, IEnumerable<ParticipantId> requestedTargets = null, HandAttackInfo hand = null, IEnumerable<DiscardCardSnapshot> discardedCards = null,
            BattleTargetBindings targetBindings = null)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (!ReferenceEquals(state.Rules, Definition)) return new TeamBattleTransition(state, CardDuelError.InvalidState);
            if (revision != state.Revision) return new TeamBattleTransition(state, CardDuelError.StaleRevision);
            if (hand != null && (action != ScoredDuelActionKind.ScoredHand || score == null || hand.Pattern.Category != score.Category))
                return new TeamBattleTransition(state, CardDuelError.InvalidDamagePlan);
            bool enemy = action == ScoredDuelActionKind.EnemyAction;
            bool skipPlayer = action == ScoredDuelActionKind.SkipPlayerTurn;
            if (state.IsFinished || !Enum.IsDefined(typeof(ScoredDuelActionKind), action) || (enemy
                ? state.Phase != ScoredDuelPhase.AwaitingEnemyAction || actor != state.NextEnemy : !state.HasPlayerWindow(actor)))
                return new TeamBattleTransition(state, CardDuelError.InvalidPhase);
            bool blocked = state.IsActionBlocked(actor);
            if (!enemy && blocked && !skipPlayer) return new TeamBattleTransition(state, CardDuelError.ActionBlocked);
            if (skipPlayer && !blocked) return new TeamBattleTransition(state, CardDuelError.InvalidPhase);
            if (targetBindings != null && targetBindings.Slots.Count > 0 &&
                (action != ScoredDuelActionKind.ScoredHand || state.Player(actor).Rules.EffectRule == null))
                return new TeamBattleTransition(state, CardDuelError.InvalidTarget);
            try
            {
                BattleActionCostResolution actionCost = null;
                if (action == ScoredDuelActionKind.ScoredHand || action == ScoredDuelActionKind.Discard)
                {
                    try { actionCost = QueryActionCost(state, actor, action); }
                    catch (ArgumentException) { return new TeamBattleTransition(state, CardDuelError.InvalidRuleModifier); }
                    if (!actionCost.CanAfford) return new TeamBattleTransition(state, CardDuelError.InsufficientEnergy);
                }
                var combat = state.Combat; var players = state.Players.ToArray(); var scores = state.Scores.ToArray();
                var amountModifier = new RelicCombatAmountModifier(state, hand == null ? (ParticipantId?)null : actor, hand);
                IReadOnlyList<CombatRuleEvent> events = Array.Empty<CombatRuleEvent>(); DamagePlan damagePlan = null; DiscardRewardResolution discardReward = null; BattleEffectPlan effectPlan = null;
                if (skipPlayer || enemy && blocked)
                {
                    var skipped = CombatStatusLifecycle.SkipActionWindow(combat, combat.Revision, actor, Definition.StatusCatalog);
                    if (!skipped.Accepted) return new TeamBattleTransition(state,
                        skipped.Error == CombatError.Overflow ? CardDuelError.Overflow : CardDuelError.InvalidState);
                    combat = skipped.State; events = skipped.RuleEvents;
                }
                if (action == ScoredDuelActionKind.ScoredHand || enemy && !blocked)
                {
                    if (!enemy)
                    {
                        ScoreEngine.Validate(score);
                        if (score.Id != NextScoreId(state)) return new TeamBattleTransition(state, CardDuelError.InvalidSelection);
                        checked { _ = state.TotalScore + score.Total; }
                        scores = scores.Concat(new[] { score }).ToArray();
                    }
                    IEnumerable<CombatEffect> baseEffects; string ruleVersion, explanation;
                    HandEffectModification handModification = null;
                    var effectRule = enemy ? null : state.Player(actor).Rules.EffectRule;
                    var enemyEffectRule = enemy ? state.Enemies.Single(unit => unit.Id == actor).EffectRule : null;
                    if (effectRule != null || enemyEffectRule != null)
                    {
                        BattleEffectSequence sequence;
                        try
                        {
                            sequence = enemy ? enemyEffectRule.Resolve(new EnemyBattleEffectContext(combat, actor))
                                : effectRule.Resolve(new ScoredHandEffectContext(combat, actor, score, hand));
                            if (sequence == null || sequence.RuleVersion != (enemy ? enemyEffectRule.Version : effectRule.Version))
                                return new TeamBattleTransition(state, CardDuelError.InvalidEffectPlan);
                        }
                        catch (ArgumentException) { return new TeamBattleTransition(state, CardDuelError.InvalidEffectPlan); }
                        if (!enemy)
                        {
                            try
                            {
                                var modified = new RelicHandEffectModifier().Modify(new HandEffectModifierContext(state.Player(actor).Relics,
                                    new ScoredHandEffectContext(combat, actor, score, hand), sequence));
                                if (!modified.IsIdentity) { handModification = modified; sequence = modified.Output; }
                            }
                            catch (ArgumentException) { return new TeamBattleTransition(state, CardDuelError.InvalidHandEffectModifier); }
                        }
                        var bound = BattleEffectBinder.Bind(state, actor, sequence, requestedTargets, targetBindings, hand,
                            enemy ? AttackOrigin.EnemyAttack : hand == null ? AttackOrigin.Other : AttackOrigin.ScoredHand);
                        if (bound.Error != CardDuelError.None) return new TeamBattleTransition(state, bound.Error);
                        baseEffects = bound.Effects; ruleVersion = sequence.RuleVersion; explanation = sequence.Explanation;
                    }
                    else
                    {
                        var targets = Targets(state, actor, requestedTargets);
                        IEnumerable<DamageRequest> requests;
                        if (enemy) requests = targets.Select(target => new DamageRequest(actor, target, state.Enemies.Single(x => x.Id == actor).Damage));
                        else
                        {
                            var context = new ScoredHandDamageContext(combat, actor, score, targets, hand);
                            try
                            {
                                var damageRule = state.Player(actor).Rules.DamagePolicy;
                                damagePlan = damageRule.Resolve(context);
                                if (damagePlan == null) return new TeamBattleTransition(state, CardDuelError.InvalidDamagePlan);
                                damagePlan.Validate(context, damageRule.Version);
                            }
                            catch (ArgumentException) { return new TeamBattleTransition(state, CardDuelError.InvalidDamagePlan); }
                            requests = damagePlan.Requests;
                        }
                        baseEffects = requests.Select(request => new CombatEffect(CombatEffectKind.Damage, request.Source, request.Target, request.Amount,
                            attack: new AttackContext(actor, enemy ? AttackOrigin.EnemyAttack : hand != null ? AttackOrigin.ScoredHand : AttackOrigin.Other,
                                request.Amount, hand)));
                        ruleVersion = damagePlan?.RuleVersion ?? "enemy-damage.fixed.1";
                        explanation = damagePlan?.Explanation ?? "敌人固定基础伤害（每个目标）";
                    }
                    var effects = new List<CombatEffect>();
                    try
                    {
                        foreach (var effect in baseEffects)
                        {
                            if (effect.Kind != CombatEffectKind.Damage) { effects.Add(effect); continue; }
                            // Preserve the action-start outgoing snapshot and authority-owned hand facts for every target.
                            var modifier = state.DamageRulesFor(effect.Source).AttackDamage;
                            var adjusted = DamageModification.Validate(modifier.Modify(
                                new DamageModifierContext(combat, effect.Source, effect.Target, effect.Amount, effect.Attack)), effect.Amount, effect.Attack);
                            effects.Add(new CombatEffect(CombatEffectKind.Damage, effect.Source, effect.Target, adjusted.OutputAmount, adjusted, effect.Attack));
                        }
                    }
                    catch (ArgumentException) { return new TeamBattleTransition(state, CardDuelError.InvalidDamageModifier); }
                    effectPlan = new BattleEffectPlan(ruleVersion, explanation, effects, handModification);
                }
                if (action == ScoredDuelActionKind.Discard)
                {
                    // Validate friendly targets even for the default empty reward plan.
                    var targets = AllyTargets(state, actor, requestedTargets);
                    DiscardRewardPlan plan;
                    try
                    {
                        var context = new DiscardRewardContext(state, actor, discardedCards, targets);
                        var rewardRule = state.Player(actor).Rules.DiscardReward;
                        plan = rewardRule.Resolve(context);
                        if (plan == null) return new TeamBattleTransition(state, CardDuelError.InvalidDiscardReward);
                        plan.Validate(context, rewardRule.Version);
                        discardReward = new DiscardRewardResolution(context, plan);
                    }
                    catch (ArgumentException) { return new TeamBattleTransition(state, CardDuelError.InvalidDiscardReward); }
                    effectPlan = new BattleEffectPlan(plan.RuleVersion, plan.Explanation, plan.Effects);
                }
                var triggerRuntime = RelicReactionRuntime.Create(state, actor, action, hand, discardReward?.Context.Cards.Count);
                if (effectPlan != null)
                {
                    var resolved = BattleEffectResolver.Resolve(combat, effectPlan,
                        new ActorIncomingDamageModifier(state), Definition.StatusCatalog, Definition.ReactionProvider, Definition.ReactionLimits, amountModifier, triggerRuntime);
                    if (!resolved.Accepted) return new TeamBattleTransition(state, EffectFailure(resolved.Error, action == ScoredDuelActionKind.Discard));
                    combat = resolved.State; events = resolved.RuleEvents;
                    triggerRuntime = (RelicReactionRuntime)resolved.ReactionRuntime;
                }
                if (!enemy)
                {
                    int index = Array.FindIndex(players, x => x.Id == actor);
                    bool forceEnd = action == ScoredDuelActionKind.EndPlayerTurn || skipPlayer;
                    int energy = forceEnd ? 0 : players[index].Energy.Energy - actionCost.Cost;
                    // Zero energy is not the end of an action window while either kind of action is free.
                    bool ended = forceEnd || energy == 0 && QueryActionCost(state, actor, ScoredDuelActionKind.ScoredHand).Cost > 0 &&
                        QueryActionCost(state, actor, ScoredDuelActionKind.Discard).Cost > 0;
                    players[index] = new BattlePlayerTurn(actor, new PlayerTurnState(players[index].Energy.MaxEnergy, energy), ended,
                        players[index].Rules, players[index].DamageRules, players[index].TargetRules, players[index].Relics, players[index].Limits);
                }
                // Action clocks run after the complete active batch and its reactions, once per successful action.
                // Explicit end/skip commands are boundaries, not completed actions. A finished battle starts no new clock.
                bool finished = IsFinished(combat, players, state.Enemies);
                bool hasCompletion = RelicReactionRuntime.HandlesAction(state, actor, action);
                if (!finished && (action == ScoredDuelActionKind.ScoredHand || action == ScoredDuelActionKind.Discard || enemy && !blocked) &&
                    (hasCompletion || HasPeriodic(combat, new[] { actor }, CombatTimingKind.ActionCompleted)))
                {
                    if (hasCompletion) triggerRuntime = triggerRuntime.WithCompletion(new BattleActionCompletionContext(state, actor, action,
                        hand, discardReward?.Context.Cards, actionCost, players.Single(player => player.Id == actor).Energy.Energy));
                    var periodic = CombatEngine.ResolveTiming(combat, combat.Revision,
                        BattleStatusTiming.ActionCompleted(state, actor, action), new ActorIncomingDamageModifier(state),
                        Definition.StatusCatalog, Definition.ReactionLimits, amountModifier, triggerRuntime,
                        hasCompletion ? Definition.ReactionProvider : null);
                    if (!periodic.Accepted) return new TeamBattleTransition(state, EffectFailure(periodic.Error));
                    combat = periodic.State; events = events.Concat(periodic.RuleEvents).ToArray();
                    triggerRuntime = (RelicReactionRuntime)periodic.ReactionRuntime;
                    finished = IsFinished(combat, players, state.Enemies);
                }
                var activations = new List<RelicTriggerActivation>();
                if (triggerRuntime != null) activations.AddRange(triggerRuntime.Activations);
                players = ApplyRuntime(players, triggerRuntime);
                var pending = enemy ? state.PendingEnemies.Where(id => id != actor && combat.Get(id).IsAlive).ToArray() : state.PendingEnemies.ToArray();
                bool playerPhaseEnded = !enemy && (finished || players.All(x => x.HasEnded || !combat.Get(x.Id).IsAlive));
                bool enemyPhaseEnded = enemy && (pending.Length == 0 || finished);
                if (!finished && (playerPhaseEnded || enemyPhaseEnded))
                {
                    // One clock for the entire team, including units whose action was skipped by control.
                    var participants = enemyPhaseEnded ? state.Enemies.Select(x => x.Id).ToArray() : players.Select(x => x.Id).ToArray();
                    var window = WindowState(state, combat, players, state.Phase, state.CompletedRounds, pending);
                    var ending = ResolveBoundary(window, BattleStatusTiming.PhaseEnding(window, participants, enemyPhaseEnded), amountModifier);
                    if (!ending.Accepted) return new TeamBattleTransition(state, ending.Error);
                    combat = ending.State.Combat; players = ending.State.Players.ToArray();
                    events = events.Concat(ending.RuleEvents).ToArray(); activations.AddRange(ending.RelicActivations);
                    pending = pending.Where(id => combat.Get(id).IsAlive).ToArray();
                    finished = IsFinished(combat, players, state.Enemies);
                }
                // Retain shield through the last periodic tick; enemy block remains independent.
                if (enemy && pending.Length == 0 && Definition.PlayerBlockExpiry == PlayerBlockExpiry.AfterEnemyPhase)
                {
                    var clear = players.Select(x => combat.Get(x.Id)).Where(x => x.IsAlive && x.Block > 0)
                        .Select(x => new CombatEffect(CombatEffectKind.ClearBlock, x.Id, x.Id)).ToArray();
                    if (clear.Length > 0)
                    {
                        // Shield maintenance retains its legacy provider and does not open another relic window.
                        var expired = CombatEngine.Resolve(combat, combat.Revision, clear, new ActorIncomingDamageModifier(state),
                            Definition.StatusCatalog, Definition.ReactionProvider, Definition.ReactionLimits);
                        if (!expired.Accepted) return new TeamBattleTransition(state, EffectFailure(expired.Error));
                        combat = expired.State; events = events.Concat(expired.RuleEvents).ToArray();
                    }
                }
                // Shield clearing may itself have custom reactions; preserve the original post-maintenance boundary check.
                finished = IsFinished(combat, players, state.Enemies);
                playerPhaseEnded = !enemy && (finished || players.All(x => x.HasEnded || !combat.Get(x.Id).IsAlive));
                enemyPhaseEnded = enemy && (pending.Length == 0 || finished);
                // Close exact lifecycle boundaries after periodic effects, including the final skipped action.
                if (playerPhaseEnded || enemyPhaseEnded)
                {
                    string scope = BattleStatusTiming.RoundScope(state);
                    var expired = CombatStatusLifecycle.EndPhase(combat, combat.Revision, scope,
                        enemyPhaseEnded ? BattleStatusTiming.EnemyPhase : BattleStatusTiming.PlayerPhase);
                    if (!expired.Accepted) return new TeamBattleTransition(state,
                        expired.Error == CombatError.Overflow ? CardDuelError.Overflow : CardDuelError.InvalidState);
                    combat = expired.State; events = events.Concat(expired.RuleEvents).ToArray();
                    if (enemyPhaseEnded || finished)
                    {
                        expired = CombatStatusLifecycle.EndRound(combat, combat.Revision, scope);
                        if (!expired.Accepted) return new TeamBattleTransition(state,
                            expired.Error == CombatError.Overflow ? CardDuelError.Overflow : CardDuelError.InvalidState);
                        combat = expired.State; events = events.Concat(expired.RuleEvents).ToArray();
                    }
                }
                int rounds = state.CompletedRounds;
                var phase = state.Phase;
                if (state.Enemies.All(x => !combat.Get(x.Id).IsAlive)) { phase = ScoredDuelPhase.Won; rounds++; }
                else if (players.All(x => !combat.Get(x.Id).IsAlive)) { phase = ScoredDuelPhase.Lost; rounds++; }
                else if (enemy && pending.Length == 0)
                {
                    rounds++;
                    phase = rounds >= Definition.RoundLimit ? ScoredDuelPhase.Lost : ScoredDuelPhase.AwaitingPlayerHand;
                    if (phase == ScoredDuelPhase.AwaitingPlayerHand)
                        players = players.Select(x => new BattlePlayerTurn(x.Id,
                            new PlayerTurnState(x.Energy.MaxEnergy, combat.Get(x.Id).IsAlive ? x.Energy.MaxEnergy : 0), !combat.Get(x.Id).IsAlive,
                            x.Rules, x.DamageRules, x.TargetRules, RelicTriggerValidation.BeginRound(x.Relics), x.Limits)).ToArray();
                }
                else if (!enemy && players.All(x => x.HasEnded || !combat.Get(x.Id).IsAlive))
                {
                    phase = ScoredDuelPhase.AwaitingEnemyAction;
                    pending = state.Enemies.Where(x => combat.Get(x.Id).IsAlive).Select(x => x.Id).ToArray();
                }
                if ((phase == ScoredDuelPhase.AwaitingPlayerHand || phase == ScoredDuelPhase.AwaitingEnemyAction) && phase != state.Phase)
                {
                    var window = WindowState(state, combat, players, phase, rounds, pending);
                    var entering = ResolveBoundary(window, BattleStatusTiming.PhaseStarted(window));
                    if (!entering.Accepted) return new TeamBattleTransition(state, entering.Error);
                    combat = entering.State.Combat; players = entering.State.Players.ToArray();
                    events = events.Concat(entering.RuleEvents).ToArray(); activations.AddRange(entering.RelicActivations);
                    pending = pending.Where(id => combat.Get(id).IsAlive).ToArray();
                    if (state.Enemies.All(x => !combat.Get(x.Id).IsAlive)) { phase = ScoredDuelPhase.Won; rounds++; }
                    else if (players.All(x => !combat.Get(x.Id).IsAlive)) { phase = ScoredDuelPhase.Lost; rounds++; }
                }
                if (phase == ScoredDuelPhase.Won || phase == ScoredDuelPhase.Lost) pending = new ParticipantId[0];
                return new TeamBattleTransition(new TeamBattleState(Definition, combat, players, state.Enemies, phase, pending,
                    checked(state.Revision + 1), rounds, scores, state.Actions.Concat(new[] { new BattleActionRecord(actor, action, action == ScoredDuelActionKind.ScoredHand ? (int?)state.Scores.Count : null) })), events: events, damage: damagePlan, discardReward: discardReward, effectPlan: effectPlan, actionCost: actionCost,
                    relicActivations: activations, actionCompletion: triggerRuntime?.Completion);
            }
            catch (OverflowException) { return new TeamBattleTransition(state, CardDuelError.Overflow); }
            catch (ArgumentException) { return new TeamBattleTransition(state, CardDuelError.InvalidTarget); }
        }
        // CardDuel.Start owns the once-only gate and commits opening effects together with the opening deal.
        internal TeamBattleTransition ResolveOpening(TeamBattleState state)
        {
            var draft = state; var events = new List<CombatRuleEvent>(); var activations = new List<RelicTriggerActivation>();
            try
            {
                var signals = new[] {
                    CombatTimingSignal.BattleStarted(state.Combat.Id + "/start", state.Players.Select(x => x.Id).Concat(state.Enemies.Select(x => x.Id))),
                    BattleStatusTiming.PhaseStarted(state) };
                foreach (var signal in signals)
                {
                    var result = ResolveBoundary(draft, signal);
                    if (!result.Accepted) return new TeamBattleTransition(state, result.Error);
                    draft = result.State; events.AddRange(result.RuleEvents); activations.AddRange(result.RelicActivations);
                    if (IsFinished(draft.Combat, draft.Players, draft.Enemies)) break;
                }
                if (events.Count == 0) return new TeamBattleTransition(state);
                var phase = draft.Enemies.All(x => !draft.Combat.Get(x.Id).IsAlive) ? ScoredDuelPhase.Won :
                    draft.Players.All(x => !draft.Combat.Get(x.Id).IsAlive) ? ScoredDuelPhase.Lost : draft.Phase;
                return new TeamBattleTransition(new TeamBattleState(Definition, draft.Combat, draft.Players, draft.Enemies,
                    phase, Array.Empty<ParticipantId>(), checked(state.Revision + 1), state.CompletedRounds + (phase == draft.Phase ? 0 : 1),
                    state.Scores, state.Actions), events: events, relicActivations: activations);
            }
            catch (OverflowException) { return new TeamBattleTransition(state, CardDuelError.Overflow); }
        }

        private TeamBattleTransition ResolveBoundary(TeamBattleState state, CombatTimingSignal signal, ICombatAmountModifier amountModifier = null)
        {
            bool boundary = RelicReactionRuntime.Handles(state, signal);
            if (!boundary && !HasPeriodic(state.Combat, signal.Participants, signal.Kind)) return new TeamBattleTransition(state);
            var resolved = CombatEngine.ResolveTiming(state.Combat, state.Combat.Revision, signal,
                new ActorIncomingDamageModifier(state), Definition.StatusCatalog, Definition.ReactionLimits,
                amountModifier ?? new RelicCombatAmountModifier(state), boundary ? RelicReactionRuntime.CreateBoundary(state, signal) : null,
                boundary ? Definition.ReactionProvider : null);
            if (!resolved.Accepted) return new TeamBattleTransition(state, EffectFailure(resolved.Error));
            var runtime = (RelicReactionRuntime)resolved.ReactionRuntime;
            return new TeamBattleTransition(WindowState(state, resolved.State, ApplyRuntime(state.Players, runtime), state.Phase,
                state.CompletedRounds, state.PendingEnemies), events: resolved.RuleEvents, relicActivations: runtime?.Activations);
        }

        private static BattlePlayerTurn[] ApplyRuntime(IEnumerable<BattlePlayerTurn> players, RelicReactionRuntime runtime) =>
            runtime == null ? players.ToArray() : players.Select(x => new BattlePlayerTurn(x.Id, x.Energy, x.HasEnded, x.Rules,
                x.DamageRules, x.TargetRules, runtime.Loadout(x.Id), x.Limits)).ToArray();

        private static TeamBattleState WindowState(TeamBattleState source, CombatState combat, IEnumerable<BattlePlayerTurn> players,
            ScoredDuelPhase phase, int rounds, IEnumerable<ParticipantId> pending) =>
            new TeamBattleState(source.Rules, combat, players, source.Enemies, phase, pending, source.Revision, rounds, source.Scores, source.Actions);

        private static bool IsFinished(CombatState combat, IEnumerable<BattlePlayerTurn> players, IEnumerable<BattleEnemySetup> enemies) =>
            enemies.All(unit => !combat.Get(unit.Id).IsAlive) || players.All(unit => !combat.Get(unit.Id).IsAlive);

        private bool HasPeriodic(CombatState combat, IEnumerable<ParticipantId> participants, CombatTimingKind timing)
        {
            if (Definition.StatusCatalog == null) return false;
            var living = new HashSet<string>(participants.Where(id => combat.Get(id).IsAlive).Select(id => id.Value));
            return combat.Statuses.Any(status => status.Target.Kind == EntityKind.Participant && living.Contains(status.Target.Id) &&
                Definition.StatusCatalog.Get(status.DefinitionId).Behaviors.OfType<PeriodicDamageSpec>().Any(behavior => behavior.Timing == timing));
        }

        private static CardDuelError EffectFailure(CombatError error, bool discard = false) =>
            error == CombatError.Overflow ? CardDuelError.Overflow :
            error == CombatError.InvalidAmountModifier ? CardDuelError.InvalidAmountModifier :
            error == CombatError.InvalidReaction ? CardDuelError.InvalidReaction :
            error == CombatError.ReactionLimitExceeded ? CardDuelError.ReactionLimitExceeded :
            error == CombatError.InvalidDamageModifier ? CardDuelError.InvalidDamageModifier :
            discard ? CardDuelError.InvalidDiscardReward : CardDuelError.InvalidTarget;

        // Dispatch by the actual target. CombatEngine still supplies the up-to-date atomic-batch context.
        private sealed class ActorIncomingDamageModifier : IIncomingDamageModifier
        {
            private readonly TeamBattleState battle;
            internal ActorIncomingDamageModifier(TeamBattleState state) { battle = state; }
            public DamageModification Modify(DamageModifierContext context) =>
                battle.DamageRulesFor(context.Target).IncomingDamage.Modify(context);
        }
        public EncounterProgress ReadProgress(TeamBattleState state)
        {
            if (!state.IsFinished && state.NextBlockedPlayer.IsValid)
                return new EncounterProgress(EncounterPhase.Resolving, state.Revision, state.CompletedRounds, state.NextBlockedPlayer);
            if (!state.IsFinished) return new EncounterProgress(EncounterPhase.AwaitingAction, state.Revision, state.CompletedRounds,
                state.Phase == ScoredDuelPhase.AwaitingEnemyAction ? state.NextEnemy : state.Players.First(x => state.CanAct(x.Id)).Id);
            bool won = state.Phase == ScoredDuelPhase.Won;
            var reason = won ? EncounterEndReason.ObjectiveReached : state.Players.All(x => !state.Combat.Get(x.Id).IsAlive)
                ? EncounterEndReason.ParticipantEliminated : EncounterEndReason.RoundLimit;
            return new EncounterProgress(EncounterPhase.Finished, state.Revision, state.CompletedRounds, result:
                new EncounterResult(state.Combat.Id, state.Revision, state.CompletedRounds, reason,
                    state.Players.Select(x => new EncounterParticipantResult(x.Id, won ? EncounterOutcome.Won : EncounterOutcome.Lost,
                        state.Actions.Where(a => a.Actor == x.Id && a.ScoreIndex.HasValue).Sum(a => state.Scores[a.ScoreIndex.Value].Total)))
                    .Concat(state.Enemies.Select(x => new EncounterParticipantResult(x.Id, won ? EncounterOutcome.Lost : EncounterOutcome.Won)))));
        }
    }
}
