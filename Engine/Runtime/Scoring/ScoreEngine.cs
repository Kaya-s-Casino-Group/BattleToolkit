using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public enum ScoreTiming { BeforeCards = 1, PerCard, AfterCards }
    public enum ScoreOperation { AddPoints = 1, AddMultiplier, MultiplyMultiplier }

    public sealed class ScoreEffect
    {
        public ScoreTiming Timing { get; }
        public ScoreOperation Operation { get; }
        public decimal Amount { get; }
        public decimal PerLevel { get; }
        public decimal PerGrowth { get; }
        public HandCategory? Category { get; }
        public Suit? Suit { get; }
        public Rank? Rank { get; }
        public ScoreEffect(ScoreTiming timing, ScoreOperation operation, decimal amount, decimal perLevel = 0, decimal perGrowth = 0,
            HandCategory? category = null, Suit? suit = null, Rank? rank = null)
        {
            if (!Enum.IsDefined(typeof(ScoreTiming), timing) || !Enum.IsDefined(typeof(ScoreOperation), operation) || amount < 0 || perLevel < 0 || perGrowth < 0 ||
                category.HasValue && !Enum.IsDefined(typeof(HandCategory), category.Value) || suit.HasValue && !Enum.IsDefined(typeof(Suit), suit.Value) ||
                rank.HasValue && !Enum.IsDefined(typeof(Rank), rank.Value) || timing != ScoreTiming.PerCard && (suit.HasValue || rank.HasValue) ||
                operation == ScoreOperation.AddPoints && (amount != decimal.Truncate(amount) || perLevel != decimal.Truncate(perLevel) || perGrowth != decimal.Truncate(perGrowth)))
                throw new ArgumentException("Invalid score effect.");
            Timing = timing; Operation = operation; Amount = amount; PerLevel = perLevel; PerGrowth = perGrowth; Category = category; Suit = suit; Rank = rank;
        }
    }

    [DataContract]
    public sealed class ScoreStep
    {
        [DataMember(Order = 1, IsRequired = true)] public string SourceId { get; private set; }
        [DataMember(Order = 2)] public string CardId { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public ScoreOperation Operation { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public decimal Amount { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public long PointsAfter { get; private set; }
        [DataMember(Order = 6, IsRequired = true)] public decimal MultiplierAfter { get; private set; }
        public ScoreStep(string source, string card, ScoreOperation operation, decimal amount, long points, decimal multiplier)
        { SourceId = source; CardId = card; Operation = operation; Amount = amount; PointsAfter = points; MultiplierAfter = multiplier; }
    }
    [DataContract]
    public sealed class ScoreResult
    {
        [DataMember(Order = 1, IsRequired = true)] public string Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public HandCategory Category { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public long BasePoints { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public decimal BaseMultiplier { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public long Points { get; private set; }
        [DataMember(Order = 6, IsRequired = true)] public decimal Multiplier { get; private set; }
        [DataMember(Order = 7, IsRequired = true)] public long Total { get; private set; }
        [DataMember(Name = "Steps", Order = 8, IsRequired = true)] private ScoreStep[] steps;
        public IReadOnlyList<ScoreStep> Steps => Guard.View(steps);
        internal ScoreResult(string id, HandCategory category, long basePoints, decimal baseMultiplier, long points, decimal multiplier, IEnumerable<ScoreStep> steps)
        {
            Id = Guard.Text(id, nameof(id)); Category = category; BasePoints = basePoints; BaseMultiplier = baseMultiplier; Points = points; Multiplier = multiplier;
            Total = checked((long)decimal.Floor(checked(points * multiplier))); this.steps = Guard.Copy(steps, nameof(steps));
        }
    }
    public sealed class ScoringCard
    {
        public string Id { get; }
        public CardFace Face { get; }
        /// <summary>Default-profile value. Use GetBasePoints with the active profile for gameplay/UI queries.</summary>
        public long BasePoints => GetBasePoints(CardPointProfile.Default);
        public long GetBasePoints(CardPointProfile profile) => (profile ?? throw new ArgumentNullException(nameof(profile))).GetBasePoints(Face);
        public long GetPointsPerTrigger(CardPointProfile profile) => checked(GetBasePoints(profile) + BonusPoints);
        public long BonusPoints { get; }
        /// <summary>Nominal points for one trigger: base + bonus. Excludes multipliers, retriggers, relics and hand points.
        /// Check CanScore and the hand's scoring subset before applying; ineffective cards still expose their original value.</summary>
        public long PointsPerTrigger => checked(BasePoints + BonusPoints);
        public decimal BonusMultiplier { get; }
        public decimal MultiplierFactor { get; }
        public int Retriggers { get; }
        public CardEffects Effects { get; }
        public bool CanScore => CardEffectRules.CanScore(Effects);
        public ScoringCard(string id, CardFace face, long bonusPoints = 0, decimal bonusMultiplier = 0, decimal multiplierFactor = 1, int retriggers = 0,
            CardEffects effects = CardEffects.None)
        {
            Id = Guard.Text(id, nameof(id)); Face = face ?? throw new ArgumentNullException(nameof(face));
            CardEffectRules.Validate(effects); Effects = effects;
            if (bonusPoints < 0 || bonusMultiplier < 0 || multiplierFactor < 0 || retriggers < 0 || retriggers > 8) throw new ArgumentException("Invalid card enhancement.");
            BonusPoints = bonusPoints; BonusMultiplier = bonusMultiplier; MultiplierFactor = multiplierFactor; Retriggers = retriggers;
        }
    }
    public sealed class ScoringRelic
    {
        public string Id { get; }
        public int Level { get; }
        public long Growth { get; }
        public IReadOnlyList<ScoreEffect> Effects { get; }
        public ScoringRelic(string id, int level, long growth, IEnumerable<ScoreEffect> effects)
        {
            Id = Guard.Text(id, nameof(id)); if (level < 1 || growth < 0) throw new ArgumentException("Invalid relic score state.");
            Level = level; Growth = growth; Effects = Guard.View(Guard.Copy(effects, nameof(effects)));
        }
    }

    /// <summary>Pure, ordered score calculation. Score points never mutate betting chips.</summary>
    public static class ScoreEngine
    {
        public const int MaxSteps = 1024;
        public static ScoreResult Evaluate(string id, ScoreProfile profile, HandCategory category, int handLevel,
            IEnumerable<ScoringCard> scoringCards, IEnumerable<ScoringRelic> orderedRelics)
        {
            if (profile == null || handLevel < 1) throw new ArgumentException("Invalid score input.");
            var cards = Guard.Copy(scoringCards, nameof(scoringCards)); var relics = Guard.Copy(orderedRelics, nameof(orderedRelics));
            if (!Enum.IsDefined(typeof(HandCategory), category) || cards.Length > 512 || cards.Select(c => c.Id).Distinct().Count() != cards.Length || relics.Length > 32 || relics.Select(j => j.Id).Distinct().Count() != relics.Length)
                throw new ArgumentException("Provide the explicit scoring subset and unique relic instances.");
            var rule = profile.Rule(category);
            long initialPoints = checked(rule.BasePoints + checked(rule.PointsPerLevel * (handLevel - 1)));
            decimal initialMultiplier = checked(rule.BaseMultiplier + rule.MultiplierPerLevel * (handLevel - 1));
            long points = initialPoints; decimal multiplier = initialMultiplier; var steps = new List<ScoreStep>();
            Action<string, string, ScoreOperation, decimal> apply = (source, card, op, amount) => {
                if (steps.Count >= MaxSteps) throw new ArgumentException("Score expansion budget exceeded.");
                if (op == ScoreOperation.AddPoints) points = checked(points + checked((long)amount));
                else if (op == ScoreOperation.AddMultiplier) multiplier = checked(multiplier + amount);
                else multiplier = checked(multiplier * amount);
                steps.Add(new ScoreStep(source, card, op, amount, points, multiplier));
            };
            Action<ScoreTiming, ScoringCard> modifiers = (timing, card) => {
                foreach (var relic in relics)
                foreach (var effect in relic.Effects.Where(e => e.Timing == timing && (!e.Category.HasValue || e.Category == category) &&
                    (!e.Suit.HasValue || e.Suit == card?.Face.Suit) && (!e.Rank.HasValue || e.Rank == card?.Face.Rank)))
                    apply(relic.Id, card?.Id, effect.Operation, checked(effect.Amount + effect.PerLevel * (relic.Level - 1) + effect.PerGrowth * relic.Growth));
            };
            modifiers(ScoreTiming.BeforeCards, null);
            // Ineffective cards still participate in classification and the hand's base score.
            // Skip their entire per-card contribution, including enhancements, retriggers and per-card relic effects.
            foreach (var card in cards.Where(c => c.CanScore))
            {
                long pointsPerTrigger = card.GetPointsPerTrigger(profile.CardPoints);
                for (int repeat = 0; repeat <= card.Retriggers; repeat++)
                {
                    apply(card.Id, card.Id, ScoreOperation.AddPoints, pointsPerTrigger);
                    if (card.BonusMultiplier != 0) apply(card.Id, card.Id, ScoreOperation.AddMultiplier, card.BonusMultiplier);
                    if (card.MultiplierFactor != 1) apply(card.Id, card.Id, ScoreOperation.MultiplyMultiplier, card.MultiplierFactor);
                    modifiers(ScoreTiming.PerCard, card);
                }
            }
            modifiers(ScoreTiming.AfterCards, null);
            return new ScoreResult(id, category, initialPoints, initialMultiplier, points, multiplier, steps);
        }
        /// <summary>Pattern-only scoring. Unscored kickers remain submitted cards but produce no card effects.</summary>
        public static ScoreResult EvaluatePattern(string id, ScoreProfile profile, PokerHandClassification pattern, int handLevel,
            IEnumerable<ScoringCard> submittedCards, IEnumerable<ScoringRelic> orderedRelics)
        {
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            var cards = Guard.Copy(submittedCards, nameof(submittedCards));
            if (cards.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != cards.Length ||
                !new HashSet<string>(cards.Select(c => c.Id), StringComparer.Ordinal).SetEquals(pattern.SubmittedCards.Select(c => c.Value)))
                throw new ArgumentException("Scoring input must contain exactly the submitted instances.");
            var byId = cards.ToDictionary(c => c.Id, StringComparer.Ordinal);
            return Evaluate(id, profile, pattern.Category, handLevel, pattern.ScoringCards.Select(c => byId[c.Value]), orderedRelics);
        }
        public static ScoreResult Fixed(string id, long points)
        {
            if (points < 0) throw new ArgumentOutOfRangeException(nameof(points));
            return new ScoreResult(id, HandCategory.HighCard, points, 1, points, 1, new ScoreStep[0]);
        }

        /// <summary>Verify an authoritative persisted arithmetic trace, independently of the mode that produced it.</summary>
        public static void Validate(ScoreResult score)
        {
            if (score == null || string.IsNullOrWhiteSpace(score.Id) || !Enum.IsDefined(typeof(HandCategory), score.Category) ||
                score.BasePoints < 0 || score.BaseMultiplier < 0 || score.Steps.Count > MaxSteps) throw new ArgumentException("Invalid score trace.");
            long points = score.BasePoints; decimal multiplier = score.BaseMultiplier;
            foreach (var step in score.Steps)
            {
                if (step == null || string.IsNullOrWhiteSpace(step.SourceId) || step.CardId != null && string.IsNullOrWhiteSpace(step.CardId) ||
                    step.Amount < 0 || !Enum.IsDefined(typeof(ScoreOperation), step.Operation)) throw new ArgumentException("Invalid score step.");
                if (step.Operation == ScoreOperation.AddPoints)
                {
                    if (step.Amount != decimal.Truncate(step.Amount)) throw new ArgumentException("Card points must be integral.");
                    points = checked(points + checked((long)step.Amount));
                }
                else if (step.Operation == ScoreOperation.AddMultiplier) multiplier = checked(multiplier + step.Amount);
                else multiplier = checked(multiplier * step.Amount);
                if (step.PointsAfter != points || step.MultiplierAfter != multiplier) throw new ArgumentException("Score trace arithmetic disagrees.");
            }
            if (score.Points != points || score.Multiplier != multiplier || score.Total != checked((long)decimal.Floor(checked(points * multiplier))))
                throw new ArgumentException("Score total disagrees with its trace.");
        }
    }
}
