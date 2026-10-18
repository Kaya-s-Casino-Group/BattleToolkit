using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public static class StandardHoldem
    {
        public const string RulesetId = "holdem.standard";
        public const string RulesetVersion = "1";
        public const string ContentVersion = "holdem.standard.cards.1";
        public static CardDefinitionId CardId(Suit suit, Rank rank) => StandardDeck.CardId(suit, rank);
        public static IReadOnlyList<CardDefinition> Cards() => StandardDeck.Cards();
        public static ContentCatalog Catalog() => new ContentCatalog(ContentVersion, Cards(), new AbilityDefinition[0], new StatusDefinitionId[0]);
        public static MatchDefinition Definition(int maxSeats = 6, long smallBlind = 10, long bigBlind = 20, MatchMode mode = MatchMode.Solo)
        {
            if (maxSeats < 2 || maxSeats > 10) throw new ArgumentOutOfRangeException(nameof(maxSeats));
            return new MatchDefinition(RulesetId, RulesetVersion, ContentVersion, maxSeats, new Chips(smallBlind), new Chips(bigBlind), mode);
        }
    }

    /// <summary>Five-of-seven standard high poker. No Unity dependency and no ability policy hidden inside the evaluator.</summary>
    public static class StandardHandEvaluator
    {
        public static HandEvaluation Evaluate(ParticipantId player, IEnumerable<EffectiveCard> source)
        {
            var cards = Guard.Copy(source, nameof(source)).OrderBy(c => c.Id.Value, StringComparer.Ordinal).ToArray();
            if (!player.IsValid || cards.Length < 5 || cards.Length > 7 || cards.Select(c => c.Id).Distinct().Count() != cards.Length)
                throw new ArgumentException("Evaluation requires 5 to 7 distinct card instances and a player.");
            HandEvaluation best = null;
            for (int a = 0; a < cards.Length - 4; a++)
            for (int b = a + 1; b < cards.Length - 3; b++)
            for (int c = b + 1; c < cards.Length - 2; c++)
            for (int d = c + 1; d < cards.Length - 1; d++)
            for (int e = d + 1; e < cards.Length; e++)
            {
                var candidate = Five(player, new[] { cards[a], cards[b], cards[c], cards[d], cards[e] });
                if (best == null || Compare(candidate, best) > 0) best = candidate;
            }
            return best;
        }

        public static int Compare(HandEvaluation left, HandEvaluation right)
        {
            if (left == null || right == null) throw new ArgumentNullException("Hands are required.");
            return PokerHandClassifier.Compare(left.Category, left.TieBreakRanks, right.Category, right.TieBreakRanks);
        }

        private static HandEvaluation Five(ParticipantId player, EffectiveCard[] cards)
        {
            var pattern = PokerHandClassifier.Classify(cards);
            // Hold'em compares all five selected cards, including kickers. Pattern scoring is a different policy.
            return new HandEvaluation(player, pattern.Category, pattern.TieBreakRanks, pattern.SubmittedCards, new StatusInstanceId[0]);
        }
    }
}
