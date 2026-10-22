using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public sealed partial class CardDuelEngine
    {
        /// <summary>Searches held cards first, then the fewest missing cards from this owner's deck/discard.
        /// Does not inspect shuffle order, consume RNG, spend resources, or enumerate stronger categories as weaker ones.
        /// Bounded searches explicitly report truncation; an exhausted budget never means impossible.</summary>
        public HandGuideResult QueryHandGuide(CardDuelState state, ParticipantId actor, HandGuideRequest request)
        {
            if (state == null || request == null) throw new ArgumentNullException();
            if (!ReferenceEquals(state.Duel.Rules, Duel.Definition) || !state.HasStarted || !state.Duel.Players.Any(p => p.Id == actor))
                return new HandGuideResult(state.Revision, Array.Empty<HandGuideCandidate>(), false, 0, CardDuelError.InvalidState);
            var pool = state.Table.Zones.Where(z => z.ControllerId == actor &&
                    (z.Kind == CardZoneKinds.Hand || z.Kind == CardZoneKinds.Deck || z.Kind == CardZoneKinds.Discard))
                .SelectMany(z => z.Cards.Select(id => new HandGuideCard(state.Table.GetCard(id), Face(state.Table.GetCard(id)), z.Kind)))
                .Where(c => c.Card.CanPlay).OrderBy(c => c.IsMissing).ThenBy(c => c.Card.Id.Value, StringComparer.Ordinal).ToArray();
            var player = state.Duel.Player(actor);
            int flush = BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.MinFlushCards).Value;
            int straight = BattleRuleResolver.Value(player.Rules, player.Relics, BattleRuleValueKey.MinStraightCards).Value;
            int min = Math.Max(PlayLimit(state, actor, true), MinimumGuideCards(request.Type, straight));
            if (request.Type == HandGuideType.RoyalFlush) min = Math.Max(PlayLimit(state, actor, true), Math.Max(flush, straight));
            if (request.RequireFlush) min = Math.Max(min, flush);
            int max = Math.Min(pool.Length, PlayLimit(state, actor, false));
            if (!GuideRanksAvailable(pool, request.Type, straight) ||
                request.RequireFlush && !pool.GroupBy(c => c.Face.Suit).Any(g => g.Count() >= flush &&
                    (max != flush || GuideRanksAvailable(g.ToArray(), request.Type, straight))) ||
                request.Type == HandGuideType.RoyalFlush && !pool.GroupBy(c => c.Face.Suit).Any(g =>
                    g.Count() >= flush && GuideRanksAvailable(g.ToArray(), request.Type, straight)))
                return new HandGuideResult(state.Revision, Array.Empty<HandGuideCandidate>(), false, 0);
            var found = new List<HandGuideCandidate>(); var chosen = new List<HandGuideCard>();
            int nodes = 0; bool stopped = false;
            // Minimal witnesses avoid padding a pair with irrelevant kickers. Larger legal selections remain
            // searchable when a rule raises MinPlayCards or a flush overlay requires extra cards.
            for (int missing = 0; missing <= max && !stopped; missing++)
            {
                for (int count = min; count <= max && !stopped; count++) Search(0, count, missing);
                if (found.Count != 0) break;
            }
            return new HandGuideResult(state.Revision, found.ToArray(), stopped, nodes);

            void Search(int start, int remaining, int missing)
            {
                if (stopped || missing < 0 || missing > remaining || pool.Length - start < remaining) return;
                if (nodes >= request.NodeBudget) { stopped = true; return; }
                nodes++;
                if (remaining == 0)
                {
                    var cards = chosen.Select(c => c.Card).ToArray();
                    var pattern = ClassifyHand(player, cards);
                    bool isFlush = player.Rules.EffectRule is ConfiguredHandEffectRule configured && configured.CombineCompatibleEffects ?
                        chosen.GroupBy(c => c.Face.Suit).Any(g => g.Count() >= flush) : cards.Length >= flush && chosen.All(c => c.Face.Suit == chosen[0].Face.Suit);
                    if (!MatchesGuide(request, pattern, isFlush)) return;
                    // Ignore a superset of an already found witness at this same missing distance.
                    if (found.Any(c => c.Cards.All(k => chosen.Any(x => x.Card.Id == k.Card.Id)))) return;
                    found.Add(new HandGuideCandidate(state, actor, chosen, pattern.Category, isFlush));
                    if (found.Count >= request.MaxCandidates) stopped = true;
                    return;
                }
                for (int i = start; i <= pool.Length - remaining && !stopped; i++)
                {
                    if (nodes >= request.NodeBudget) { stopped = true; return; }
                    nodes++; // Count rejected prefixes too, so pruning cannot bypass the work budget.
                    var next = pool[i];
                    if (next.IsMissing && missing == 0) break;
                    if (!next.IsMissing && remaining == missing) continue;
                    chosen.Add(next);
                    if (GuidePrefixPossible(chosen, request, chosen.Count + remaining - 1, flush, straight))
                        Search(i + 1, remaining - 1, missing - (next.IsMissing ? 1 : 0));
                    chosen.RemoveAt(chosen.Count - 1);
                }
            }
        }

        public HandGuidePreview PreviewHandGuide(CardDuelState state, HandGuideCandidate candidate, bool settle = false,
            IEnumerable<ParticipantId> targets = null, BattleTargetBindings targetBindings = null)
        {
            if (state == null || candidate == null) throw new ArgumentNullException();
            if (!ReferenceEquals(candidate.Snapshot, state)) return new HandGuidePreview(candidate, CardDuelError.StaleRevision);
            // Only private hypothetical relocation: no draw hooks, deck ordering or RNG are involved.
            var missing = new HashSet<CardInstanceId>(candidate.Cards.Where(c => c.IsMissing).Select(c => c.Card.Id));
            var zones = state.Table.Zones.Select(z => new CardZone(z.Id, z.Kind, z.ControllerId,
                z.Kind == CardZoneKinds.Hand && z.ControllerId == candidate.Actor ?
                    z.Cards.Concat(missing.OrderBy(id => id.Value, StringComparer.Ordinal)) : z.Cards.Where(id => !missing.Contains(id))));
            var snapshot = new CardDuelState(new CardTableState(state.Table.MatchId, state.Table.Cards, zones, state.Revision),
                state.Duel, state.Random, state.Duel.Players.ToDictionary(p => p.Id, p => state.DiscardsFor(p.Id)),
                state.HasStarted, state.CopyFlows(), state.CopyGold());
            var ids = candidate.Cards.Select(c => c.Card.Id).ToArray();
            var scored = PreviewHand(snapshot, candidate.Actor, ids);
            if (!scored.Accepted) return new HandGuidePreview(candidate, scored.Error);
            var hand = PreviewHandFacts(snapshot, candidate.Actor, ids);
            BattleEffectSequence sequence = null, ruleEffects = null;
            try
            {
                var player = state.Duel.Player(candidate.Actor);
                if (player.Rules.EffectRule != null && state.Duel.Combat.Get(candidate.Actor).IsAlive)
                {
                    var context = new ScoredHandEffectContext(state.Duel.Combat, candidate.Actor,
                        ScoreEngine.RemoveFinalMultiplier(scored.Score, "item/roulette"), hand);
                    sequence = player.Rules.EffectRule.Resolve(context);
                    ruleEffects = sequence;
                    if (sequence == null || sequence.RuleVersion != player.Rules.EffectRule.Version)
                        return new HandGuidePreview(candidate, CardDuelError.InvalidEffectPlan);
                    sequence = new RelicHandEffectModifier().Modify(new HandEffectModifierContext(player.Relics, context, sequence)).Output;
                    sequence = RelicPrograms.Sequence(player.Relics, context, sequence);
                }
                var settlement = settle ? PreviewPlay(snapshot, snapshot.Revision, candidate.Actor, ids, targets, targetBindings) : null;
                return new HandGuidePreview(candidate, CardDuelError.None, scored.Score, hand, sequence, settle, settlement, ruleEffects);
            }
            catch (ArgumentException) { return new HandGuidePreview(candidate, CardDuelError.InvalidEffectPlan); }
            catch (OverflowException) { return new HandGuidePreview(candidate, CardDuelError.Overflow); }
        }

        private static int MinimumGuideCards(HandGuideType type, int straight)
        {
            switch (type)
            {
                case HandGuideType.OnePair: return 2;
                case HandGuideType.ThreeOfAKind: return 3;
                case HandGuideType.TwoPair: case HandGuideType.FourOfAKind: return 4;
                case HandGuideType.Straight: return straight;
                case HandGuideType.FullHouse: case HandGuideType.FiveOfAKind: case HandGuideType.RoyalFlush: return 5;
                default: return 1;
            }
        }
        // Necessary conditions only. In particular, a normal 52-card deck cannot make a five-card
        // pair + flush: each suit has only one card per rank. Prove such absences before enumerating.
        private static bool GuideRanksAvailable(HandGuideCard[] cards, HandGuideType type, int straight)
        {
            var counts = cards.GroupBy(c => (int)c.Face.Rank).ToDictionary(g => g.Key, g => g.Count());
            if (counts.Count == 0) return false;
            switch (type)
            {
                case HandGuideType.OnePair: return counts.Values.Any(n => n >= 2);
                case HandGuideType.TwoPair: return counts.Values.Count(n => n >= 2) >= 2;
                case HandGuideType.ThreeOfAKind: return counts.Values.Any(n => n >= 3);
                case HandGuideType.FourOfAKind: return counts.Values.Any(n => n >= 4);
                case HandGuideType.FiveOfAKind: return counts.Values.Any(n => n >= 5);
                case HandGuideType.FullHouse: return counts.Any(a => a.Value >= 3 && counts.Any(b => b.Key != a.Key && b.Value >= 2));
                case HandGuideType.RoyalFlush: return Enumerable.Range(15 - straight, straight).All(counts.ContainsKey);
                case HandGuideType.Straight: return Enumerable.Range(1, 15 - straight).Any(low =>
                    Enumerable.Range(low, straight).All(rank => counts.ContainsKey(rank == 1 ? 14 : rank)));
                default: return true;
            }
        }
        private static bool MatchesGuide(HandGuideRequest request, PokerHandClassification pattern, bool flush)
        {
            if (request.RequireFlush && !flush) return false;
            if (request.Type == HandGuideType.Any) return true;
            if (request.Type == HandGuideType.RoyalFlush)
                return pattern.Category == HandCategory.StraightFlush && pattern.TieBreakRanks[0] == (int)Rank.Ace;
            return pattern.Category.ToString() == request.Type.ToString();
        }
        private static bool GuidePrefixPossible(List<HandGuideCard> cards, HandGuideRequest request, int size, int flush, int straight)
        {
            // Conservative pruning only for full <=5-card witnesses; larger selections may contain kickers.
            if (size > 5) return true;
            if ((request.RequireFlush && size == flush || request.Type == HandGuideType.RoyalFlush) && cards.Any(c => c.Face.Suit != cards[0].Face.Suit)) return false;
            var ranks = cards.GroupBy(c => (int)c.Face.Rank).Select(g => g.Count()).ToArray();
            switch (request.Type)
            {
                case HandGuideType.HighCard: return ranks.All(n => n == 1);
                case HandGuideType.OnePair: return ranks.Max() <= 2 && ranks.Count(n => n > 1) <= 1 && (size != 2 || ranks.Length == 1);
                case HandGuideType.TwoPair: return ranks.Max() <= 2 && (size != 4 || ranks.Length <= 2);
                case HandGuideType.ThreeOfAKind: return ranks.Max() <= 3 && ranks.Count(n => n > 1) <= 1 && (size != 3 || ranks.Length == 1);
                case HandGuideType.FourOfAKind: return ranks.Max() <= 4 && ranks.Count(n => n > 1) <= 1 && (size != 4 || ranks.Length == 1);
                case HandGuideType.FiveOfAKind: return ranks.Length == 1;
                case HandGuideType.FullHouse: return ranks.Length <= 2 && ranks.Max() <= 3 && ranks.Count(n => n == 3) <= 1;
                case HandGuideType.RoyalFlush: return ranks.All(n => n == 1) && cards.All(c => (int)c.Face.Rank >= 10);
                case HandGuideType.Straight:
                    if (size != straight) return true;
                    if (ranks.Any(n => n > 1)) return false;
                    return Enumerable.Range(1, 15 - straight).Any(low => cards.All(c =>
                    { int rank = (int)c.Face.Rank; if (low == 1 && rank == 14) rank = 1; return rank >= low && rank < low + straight; }));
                default: return true;
            }
        }
    }
}
