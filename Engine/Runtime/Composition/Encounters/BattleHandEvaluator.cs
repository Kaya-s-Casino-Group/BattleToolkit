using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Read-only hand classification, scoring and authoritative hand facts, shared by
    /// preview, play and hand-guide queries. Owns no battle state and never commits a transition.</summary>
    internal sealed class BattleHandEvaluator
    {
        private readonly TeamBattleEngine duel;
        private readonly IReadOnlyDictionary<CardDefinitionId, CardDefinition> faces;
        private readonly ScoreProfile Scoring;
        internal BattleHandEvaluator(TeamBattleEngine duel, ScoreProfile scoring, IReadOnlyDictionary<CardDefinitionId, CardDefinition> faces)
        { this.duel = duel; Scoring = scoring; this.faces = faces; }
        private CardDefinition Face(CardInstance card) => faces[card.DefinitionId];

        internal CardDuelTransition Evaluate(CardDuelState state, ParticipantId actor, IEnumerable<CardInstanceId> selected, out HandAttackInfo facts)
        {
            facts = null;
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (!ReferenceEquals(state.Duel.Rules, duel.Definition)) return new CardDuelTransition(state, CardDuelError.InvalidState);
            if (!state.HasStarted || !state.Duel.Players.Any(player => player.Id == actor)) return new CardDuelTransition(state, CardDuelError.InvalidPhase);
            var ids = (selected ?? new CardInstanceId[0]).ToArray();
            var hand = state.Zone(CardZoneKinds.Hand, actor);
            if (ids.Length < BattleActionQuery.PlayLimit(state, actor, true) || ids.Length > BattleActionQuery.PlayLimit(state, actor, false) ||
                !BattleActionQuery.HasSelectableCards(state, actor, ids, false))
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
                var score = ScoreEngine.EvaluateBattle(duel.NextScoreId(state.Duel), Scoring, pattern, level,
                    ordered.Select(card => new ScoringCard(card.Id.Value, new CardFace(Face(card).Rank, Face(card).Suit), effects: card.Effects)),
                    flow.UsesTurnFlow || flow.LayeredScoreRelics ? relics : Array.Empty<ScoringRelic>(), kicker, flow.LayeredScoreRelics, player.Rules.HandLevels.LevelFor,
                    includeLegacyComponents: !compatible || (player.Rules.EffectRule as ConfiguredHandEffectRule)?.FullHouseComponentDamage == true);
                if (handRule?.ModernScoring == true && facts.IsFlush && !royal)
                    score = ScoreEngine.ScaleMultiplier(score, handRule.FlushDamageMultiplier, "hand/flush");
                var itemMultiplier = BattleItemRuntime.HandMultiplier(player.Relics);
                if (itemMultiplier != 1) score = ScoreEngine.ScaleMultiplier(score, itemMultiplier, "item/roulette");
                return new CardDuelTransition(state, score: score, pattern: pattern);
            }
            catch (OverflowException) { return new CardDuelTransition(state, CardDuelError.Overflow); }
        }

        internal PokerHandClassification ClassifyHand(BattlePlayerTurn player, CardInstance[] ordered)
        {
            var rule = player.Rules.EffectRule as ConfiguredHandEffectRule;
            int flush = BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.MinFlushCards).Value;
            int straight = BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.MinStraightCards).Value;
            var cards = ordered.Select(card => new EffectiveCard(card.Id, Face(card).Rank, Face(card).Suit)).ToArray();
            var pattern = rule?.CombineCompatibleEffects == true ?
                PokerHandCompatibility.Classify(cards, flush, straight, flushAsAttribute: rule.FlushAsAttribute, modernScoring: rule.ModernScoring).Highest :
                PokerHandClassifier.ClassifyBest(cards, flush, straight);
            bool royal = pattern.Category == HandCategory.StraightFlush && pattern.TieBreakRanks[0] == (int)Rank.Ace;
            return !royal && rule?.FlushUsesRankEffects == true ? PokerHandClassifier.ClassifyBest(cards, flush, straight, true) : pattern;
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
                    // A low Ace changes straight ordering, never the configured scoring/healing points.
                    flushBase = ordered.Where(card => flushIds.Contains(card.Id) && CardEffectRules.CanScore(card.Effects)).Aggregate(0L, (sum, card) =>
                        checked(sum + Scoring.CardPoints.GetBasePoints(new CardFace(Face(card).Rank, Face(card).Suit))));
                }
                else flushBase = ScoreEngine.Evaluate(duel.NextScoreId(state.Duel) + "/flush-base", Scoring, HandCategory.Flush, 1,
                    ordered.Select(c => new ScoringCard(c.Id.Value, new CardFace(Face(c).Rank, Face(c).Suit), effects: c.Effects)),
                    Array.Empty<ScoringRelic>(), true).Total;
            }
            return new HandAttackInfo(pattern, ordered.Select(card => new AttackCardSnapshot(card,
                new EffectiveCard(card.Id, Face(card).Rank, Face(card).Suit))),
                BattleRuleResolver.Value(state.Duel.Player(actor).Rules, state.Duel.Player(actor).Relics, BattleRuleValueKey.MinFlushCards).Value,
                straightMin, flushBase, rule?.FlushUsesRankEffects == true, rule?.CombineCompatibleEffects == true, rule?.FlushAsAttribute == true, rule?.ModernScoring == true);
        }

    }
}
