using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public static class DrawTimings
    {
        public const string BeforeAction = "draw.before-action";
        public const string BeforeScoring = "draw.before-scoring";
        public const string AfterScoring = "draw.after-scoring";
        public const string AfterDiscard = "draw.after-discard";
    }
    public enum DrawPhase { Ready = 1, BeforeScoring, AfterScoring, AfterDiscard, Scored, Won, Lost }
    public enum DrawAction { Play = 1, Discard, ActivateAbility, SelectAbilityTargets, Continue, Timeout }
    public enum DrawError { None, InvalidState, StaleRevision, Unauthorized, InvalidAction, InvalidSelection, InvalidAbility, Overflow }

    public sealed class DrawRelic
    {
        public ScoringRelic Scoring { get; }
        public long GrowthPerScoredHand { get; }
        public DrawRelic(ScoringRelic scoring, long growthPerScoredHand = 0)
        {
            Scoring = scoring ?? throw new ArgumentNullException(nameof(scoring));
            if (growthPerScoredHand < 0) throw new ArgumentOutOfRangeException(nameof(growthPerScoredHand));
            GrowthPerScoredHand = growthPerScoredHand;
        }
        public ScoringRelic At(int handsScored) => new ScoringRelic(Scoring.Id, Scoring.Level,
            checked(Scoring.Growth + checked(GrowthPerScoredHand * handsScored)), Scoring.Effects);
    }
    public sealed class DrawHandLevel
    {
        public HandCategory Category { get; }
        public int Level { get; }
        public DrawHandLevel(HandCategory category, int level)
        {
            if (!Enum.IsDefined(typeof(HandCategory), category) || level < 1) throw new ArgumentException("Invalid hand level.");
            Category = category; Level = level;
        }
    }
    /// <summary>Versioned mode rules and immutable score loadout. Reconstruct the same definition when restoring.</summary>
    public sealed class DrawDefinition
    {
        public string Version { get; }
        public long TargetScore { get; }
        public int HandSize { get; }
        public int Hands { get; }
        public int Discards { get; }
        public int MaxSelection { get; }
        public ScoreProfile Scoring { get; }
        public IReadOnlyList<DrawRelic> Relics { get; }
        private readonly Dictionary<HandCategory, int> levels;
        public DrawDefinition(string version, long targetScore, int handSize = 8, int hands = 4, int discards = 3, int maxSelection = 5,
            ScoreProfile scoring = null, IEnumerable<DrawRelic> relics = null, IEnumerable<DrawHandLevel> handLevels = null)
        {
            Version = Guard.Text(version, nameof(version));
            if (targetScore <= 0 || handSize < 1 || handSize > 64 || hands < 1 || hands > 1000 || discards < 0 || discards > 1000 || maxSelection < 1 || maxSelection > 5 || maxSelection > handSize)
                throw new ArgumentException("Invalid draw encounter rules.");
            TargetScore = targetScore; HandSize = handSize; Hands = hands; Discards = discards; MaxSelection = maxSelection;
            Scoring = scoring ?? ScoreProfile.Default; Relics = Guard.View(Guard.Copy(relics ?? new DrawRelic[0], nameof(relics)));
            if (Relics.Count > 32 || Relics.Select(j => j.Scoring.Id).Distinct(StringComparer.Ordinal).Count() != Relics.Count)
                throw new ArgumentException("Relic instances must be unique and fit the score budget.");
            levels = Guard.Copy(handLevels ?? new DrawHandLevel[0], nameof(handLevels)).ToDictionary(h => h.Category, h => h.Level);
        }
        public int Level(HandCategory category) { int result; return levels.TryGetValue(category, out result) ? result : 1; }
    }

    /// <summary>Persistent card identity and enhancements; location belongs exclusively to AbilityWorld.Zones.</summary>
    [DataContract]
    public sealed class DrawCard
    {
        [DataMember(Order = 1, IsRequired = true)] public CardInstance Card { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public long BonusPoints { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public decimal BonusMultiplier { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public decimal MultiplierFactor { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public int Retriggers { get; private set; }
        public DrawCard(CardInstance card, long bonusPoints = 0, decimal bonusMultiplier = 0, decimal multiplierFactor = 1, int retriggers = 0)
        {
            Card = card ?? throw new ArgumentNullException(nameof(card));
            if (bonusPoints < 0 || bonusMultiplier < 0 || multiplierFactor < 0 || retriggers < 0 || retriggers > 8) throw new ArgumentException("Invalid card enhancement.");
            BonusPoints = bonusPoints; BonusMultiplier = bonusMultiplier; MultiplierFactor = multiplierFactor; Retriggers = retriggers;
        }
        public ScoringCard WithFace(CardFace face) => new ScoringCard(Card.Id.Value, face, BonusPoints, BonusMultiplier, MultiplierFactor, Retriggers, Card.Effects);
    }

    [DataContract]
    public sealed class DrawState
    {
        [DataMember(Order = 1, IsRequired = true)] public string Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public long Revision { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public string ContentVersion { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public string RulesVersion { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public string ScoreVersion { get; private set; }
        [DataMember(Order = 6, IsRequired = true)] public ParticipantId PlayerId { get; private set; }
        [DataMember(Order = 7, IsRequired = true)] public DrawPhase Phase { get; private set; }
        [DataMember(Order = 8, IsRequired = true)] public int HandsRemaining { get; private set; }
        [DataMember(Order = 9, IsRequired = true)] public int DiscardsRemaining { get; private set; }
        [DataMember(Order = 10, IsRequired = true)] public long Score { get; private set; }
        [DataMember(Order = 11, IsRequired = true)] public AbilityWorld World { get; private set; }
        [DataMember(Order = 12, IsRequired = true)] public RandomState Random { get; private set; }
        [DataMember(Name = "Cards", Order = 13, IsRequired = true)] private DrawCard[] cards;
        [DataMember(Name = "Scores", Order = 14, IsRequired = true)] private ScoreResult[] scores;
        [DataMember(Name = "PendingCards", Order = 15, IsRequired = true)] private CardInstanceId[] pendingCards;
        [DataMember(Order = 16)] public PokerHandClassification LastPattern { get; private set; }
        public IReadOnlyList<DrawCard> Cards => Guard.View(cards);
        public IReadOnlyList<ScoreResult> Scores => Guard.View(scores);
        public IReadOnlyList<CardInstanceId> PendingCards => Guard.View(pendingCards);
        public PokerHandClassification Pattern => LastPattern;
        public DrawState(string id, long revision, string contentVersion, string rulesVersion, string scoreVersion, ParticipantId playerId,
            DrawPhase phase, int handsRemaining, int discardsRemaining, long score, AbilityWorld world, RandomState random,
            IEnumerable<DrawCard> cards, IEnumerable<ScoreResult> scores, IEnumerable<CardInstanceId> pendingCards,
            PokerHandClassification lastPattern = null)
        {
            Id = Guard.Text(id, nameof(id)); Revision = revision; ContentVersion = contentVersion; RulesVersion = rulesVersion;
            ScoreVersion = scoreVersion; PlayerId = playerId; Phase = phase; HandsRemaining = handsRemaining; DiscardsRemaining = discardsRemaining;
            Score = score; World = world ?? throw new ArgumentNullException(nameof(world)); Random = random ?? throw new ArgumentNullException(nameof(random));
            this.cards = Guard.Copy(cards, nameof(cards)); this.scores = Guard.Copy(scores, nameof(scores)); this.pendingCards = Guard.Copy(pendingCards, nameof(pendingCards)); LastPattern = lastPattern;
        }
    }

    public sealed class DrawCommand
    {
        public string Id { get; }
        public long ExpectedRevision { get; }
        public DrawAction Action { get; }
        public IReadOnlyList<CardInstanceId> Cards { get; }
        public string WindowId { get; }
        public AbilityInstanceId AbilityId { get; }
        public DecisionId DecisionId { get; }
        public IReadOnlyList<string> SelectionKeys { get; }
        public bool Skip { get; }
        public DrawCommand(string id, long expectedRevision, DrawAction action, IEnumerable<CardInstanceId> cards = null,
            string windowId = null, AbilityInstanceId abilityId = default(AbilityInstanceId), DecisionId decisionId = default(DecisionId),
            IEnumerable<string> selectionKeys = null, bool skip = false)
        {
            Id = Guard.Text(id, nameof(id)); ExpectedRevision = expectedRevision; Action = action;
            Cards = Guard.View(Guard.Copy(cards ?? new CardInstanceId[0], nameof(cards))); WindowId = windowId; AbilityId = abilityId;
            DecisionId = decisionId; SelectionKeys = Guard.View(Guard.Copy(selectionKeys ?? new string[0], nameof(selectionKeys))); Skip = skip;
        }
    }
    public enum DrawEventKind { CardsPlayed = 1, CardsDiscarded, CardsDrawn, Scored, Won, Lost, Ability }
    public sealed class DrawEvent
    {
        public long Revision { get; }
        public int Index { get; }
        public DrawEventKind Kind { get; }
        public IReadOnlyList<CardInstanceId> Cards { get; }
        public ScoreResult Score { get; }
        public AbilityEvent Ability { get; }
        internal DrawEvent(long revision, int index, DrawEventKind kind, IEnumerable<CardInstanceId> cards = null, ScoreResult score = null, AbilityEvent ability = null)
        { Revision = revision; Index = index; Kind = kind; Cards = Guard.View(Guard.Copy(cards ?? new CardInstanceId[0], nameof(cards))); Score = score; Ability = ability; }
    }
    public sealed class DrawTransitionResult
    {
        public bool Accepted => Error == DrawError.None;
        public DrawError Error { get; }
        public DrawState State { get; }
        public IReadOnlyList<DrawEvent> Events { get; }
        internal DrawTransitionResult(DrawState state, DrawError error, IEnumerable<DrawEvent> events = null)
        { State = state; Error = error; Events = Guard.View(Guard.Copy(events ?? new DrawEvent[0], nameof(events))); }
    }
}
