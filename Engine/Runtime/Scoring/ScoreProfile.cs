using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public sealed class HandScoreRule
    {
        public HandCategory Category { get; }
        public long BasePoints { get; }
        public decimal BaseMultiplier { get; }
        public long PointsPerLevel { get; }
        public decimal MultiplierPerLevel { get; }
        public HandScoreRule(HandCategory category, long points, decimal multiplier, long pointsPerLevel = 10, decimal multiplierPerLevel = 1)
        {
            if (!Enum.IsDefined(typeof(HandCategory), category) || points < 0 || multiplier < 0 || pointsPerLevel < 0 || multiplierPerLevel < 0)
                throw new ArgumentException("Invalid hand score rule.");
            Category = category; BasePoints = points; BaseMultiplier = multiplier; PointsPerLevel = pointsPerLevel; MultiplierPerLevel = multiplierPerLevel;
        }
    }
    public sealed class ScoreProfile
    {
        private readonly Dictionary<HandCategory, HandScoreRule> rules;
        public string Version { get; }
        public CardPointProfile CardPoints { get; }
        public ScoreProfile(string version, IEnumerable<HandScoreRule> rules, CardPointProfile cardPoints = null)
        {
            CardPoints = cardPoints ?? CardPointProfile.Default;
            Version = Guard.Text(version, nameof(version)); this.rules = Guard.Copy(rules, nameof(rules)).ToDictionary(r => r.Category);
            if (Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>().Any(c => !this.rules.ContainsKey(c)))
                throw new ArgumentException("A rule is required for every hand category.");
        }
        public HandScoreRule Rule(HandCategory category) => rules[category];
        // Balatro level-one values and per-level growth. FiveOfAKind remains a compatibility extension.
        public static ScoreProfile Default => new ScoreProfile("score.balatro.1", new[] {
            new HandScoreRule(HandCategory.HighCard, 5, 1, 10, 1),
            new HandScoreRule(HandCategory.OnePair, 10, 2, 15, 1),
            new HandScoreRule(HandCategory.TwoPair, 20, 2, 20, 1),
            new HandScoreRule(HandCategory.ThreeOfAKind, 30, 3, 20, 2),
            new HandScoreRule(HandCategory.Straight, 30, 4, 30, 3),
            new HandScoreRule(HandCategory.Flush, 35, 4, 15, 2),
            new HandScoreRule(HandCategory.FullHouse, 40, 4, 25, 2),
            new HandScoreRule(HandCategory.FourOfAKind, 60, 7, 30, 3),
            new HandScoreRule(HandCategory.StraightFlush, 100, 8, 40, 4),
            new HandScoreRule(HandCategory.FiveOfAKind, 120, 12, 35, 3)
        });
    }
}
