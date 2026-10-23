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
            HandGuideResult result = null;
            foreach (var step in QueryHandGuideIncremental(state, actor, request)) if (step != null) result = step;
            return result;
        }

        /// <summary>Yields null after each bounded batch of nodes, then one final result. Dispose to cancel.
        /// The caller can advance this on the main thread within a frame budget; no rule callbacks run on worker threads.</summary>
        public IEnumerable<HandGuideResult> QueryHandGuideIncremental(CardDuelState state, ParticipantId actor,
            HandGuideRequest request, int nodesPerStep = 32)
        {
            if (state == null || request == null) throw new ArgumentNullException();
            if (nodesPerStep < 1) throw new ArgumentOutOfRangeException(nameof(nodesPerStep));
            if (!ReferenceEquals(state.Duel.Rules, Duel.Definition) || !state.HasStarted || !state.Duel.Players.Any(p => p.Id == actor))
            {
                yield return new HandGuideResult(state.Revision, Array.Empty<HandGuideCandidate>(), false, 0, CardDuelError.InvalidState);
                yield break;
            }
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
            {
                yield return new HandGuideResult(state.Revision, Array.Empty<HandGuideCandidate>(), false, 0);
                yield break;
            }
            var found = new List<HandGuideCandidate>(); var chosen = new List<HandGuideCard>();
            int nodes = 0, nextYield = nodesPerStep; bool stopped = false;
            var nextIndices = new int[max + 1]; var missingAtDepth = new int[max + 1];
            int heldCount = pool.Count(c => !c.IsMissing);
            bool combined = player.Rules.EffectRule is ConfiguredHandEffectRule configured && configured.CombineCompatibleEffects;
            // Minimal witnesses avoid padding a pair with irrelevant kickers. Larger legal selections remain
            // searchable when a rule raises MinPlayCards or a flush overlay requires extra cards.
            for (int missing = 0; missing <= max && !stopped; missing++)
            {
                for (int count = min; count <= max && !stopped; count++)
                {
                    int depth = 0; bool entering = true;
                    chosen.Clear(); nextIndices[0] = 0; missingAtDepth[0] = missing;
                    // Explicit DFS stack avoids allocating a recursive iterator or a rank grouping per prefix.
                    while (depth >= 0 && !stopped)
                    {
                        int remaining = count - depth, needed = missingAtDepth[depth];
                        if (entering)
                        {
                            int start = nextIndices[depth];
                            if (needed < 0 || needed > remaining || pool.Length - start < remaining ||
                                remaining - needed > Math.Max(0, heldCount - start) ||
                                needed > pool.Length - Math.Max(start, heldCount))
                            { Backtrack(); continue; }
                            if (nodes >= request.NodeBudget) { stopped = true; break; }
                            nodes++;
                            if (nodes >= nextYield) { nextYield = nodes + nodesPerStep; yield return null; }
                            if (remaining == 0)
                            {
                                var cards = chosen.Select(c => c.Card).ToArray();
                                var pattern = ClassifyHand(player, cards);
                                bool isFlush = combined ? chosen.GroupBy(c => c.Face.Suit).Any(g => g.Count() >= flush) :
                                    cards.Length >= flush && chosen.All(c => c.Face.Suit == chosen[0].Face.Suit);
                                if (MatchesGuide(request, pattern, isFlush))
                                {
                                    found.Add(new HandGuideCandidate(state, actor, chosen, pattern.Category, isFlush));
                                    if (found.Count >= request.MaxCandidates) stopped = true;
                                }
                                Backtrack(); continue;
                            }
                            entering = false;
                        }
                        int index = nextIndices[depth]++;
                        if (index > pool.Length - remaining) { Backtrack(); continue; }
                        if (nodes >= request.NodeBudget) { stopped = true; break; }
                        nodes++; // Rejected prefixes also consume time and the total work budget.
                        if (nodes >= nextYield) { nextYield = nodes + nodesPerStep; yield return null; }
                        var next = pool[index];
                        if (next.IsMissing && needed == 0) { Backtrack(); continue; }
                        if (!next.IsMissing && remaining == needed) continue;
                        chosen.Add(next);
                        if (GuideContainsWitness(chosen, found) || !GuidePrefixPossible(chosen, request, count, flush, straight))
                        { chosen.RemoveAt(depth); continue; }
                        depth++; nextIndices[depth] = index + 1;
                        missingAtDepth[depth] = needed - (next.IsMissing ? 1 : 0); entering = true;
                    }

                    void Backtrack()
                    {
                        if (depth > 0) chosen.RemoveAt(depth - 1);
                        depth--; entering = false;
                    }
                }
                if (found.Count != 0) break;
            }
            yield return new HandGuideResult(state.Revision, found.ToArray(), stopped, nodes);
        }

        public HandGuidePreview PreviewHandGuide(CardDuelState state, HandGuideCandidate candidate, bool settle = false,
            IEnumerable<ParticipantId> targets = null, BattleTargetBindings targetBindings = null)
        {
            if (state == null || candidate == null) throw new ArgumentNullException();
            if (!ReferenceEquals(candidate.Snapshot, state)) return new HandGuidePreview(candidate, CardDuelError.StaleRevision);
            // Only private hypothetical relocation: no draw hooks, deck ordering or RNG are involved.
            var missing = new HashSet<CardInstanceId>(candidate.Cards.Where(c => c.IsMissing).Select(c => c.Card.Id));
            var snapshot = state;
            if (missing.Count != 0)
            {
                var zones = state.Table.Zones.Select(z => new CardZone(z.Id, z.Kind, z.ControllerId,
                    z.Kind == CardZoneKinds.Hand && z.ControllerId == candidate.Actor ?
                        z.Cards.Concat(missing.OrderBy(id => id.Value, StringComparer.Ordinal)) : z.Cards.Where(id => !missing.Contains(id))));
                snapshot = new CardDuelState(new CardTableState(state.Table.MatchId, state.Table.Cards, zones, state.Revision),
                    state.Duel, state.Random, state.Duel.Players.ToDictionary(p => p.Id, p => state.DiscardsFor(p.Id)),
                    state.HasStarted, state.CopyFlows(), state.CopyGold());
            }
            var ids = candidate.Cards.Select(c => c.Card.Id).ToArray();
            var scored = PreviewHand(snapshot, candidate.Actor, ids);
            if (!scored.Accepted) return new HandGuidePreview(candidate, scored.Error);
            // PreviewHand already classified and scored this exact selection; reuse that evidence.
            var hand = BuildHandFacts(snapshot, candidate.Actor,
                snapshot.Zone(CardZoneKinds.Hand, candidate.Actor).Cards.Where(ids.Contains).Select(snapshot.Table.GetCard).ToArray(), scored.Pattern);
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
            switch (request.Type)
            {
                case HandGuideType.HighCard: return pattern.Category == HandCategory.HighCard;
                case HandGuideType.OnePair: return pattern.Category == HandCategory.OnePair;
                case HandGuideType.TwoPair: return pattern.Category == HandCategory.TwoPair;
                case HandGuideType.ThreeOfAKind: return pattern.Category == HandCategory.ThreeOfAKind;
                case HandGuideType.Straight: return pattern.Category == HandCategory.Straight;
                case HandGuideType.FullHouse: return pattern.Category == HandCategory.FullHouse;
                case HandGuideType.FourOfAKind: return pattern.Category == HandCategory.FourOfAKind;
                case HandGuideType.FiveOfAKind: return pattern.Category == HandCategory.FiveOfAKind;
                default: return false;
            }
        }
        private static bool GuidePrefixPossible(List<HandGuideCard> cards, HandGuideRequest request, int size, int flush, int straight)
        {
            // Conservative pruning only for full <=5-card witnesses; larger selections may contain kickers.
            if (size > 5) return true;
            int distinct = 0, duplicates = 0, triples = 0, largest = 0;
            for (int i = 0; i < cards.Count; i++)
            {
                if ((request.RequireFlush && size == flush || request.Type == HandGuideType.RoyalFlush) && cards[i].Face.Suit != cards[0].Face.Suit) return false;
                bool seen = false;
                for (int j = 0; j < i; j++) if (cards[j].Face.Rank == cards[i].Face.Rank) { seen = true; break; }
                if (seen) continue;
                int amount = 1;
                for (int j = i + 1; j < cards.Count; j++) if (cards[j].Face.Rank == cards[i].Face.Rank) amount++;
                distinct++; if (amount > 1) duplicates++; if (amount == 3) triples++; largest = Math.Max(largest, amount);
            }
            switch (request.Type)
            {
                case HandGuideType.HighCard: return largest == 1;
                case HandGuideType.OnePair: return largest <= 2 && duplicates <= 1 && (size != 2 || distinct == 1);
                case HandGuideType.TwoPair: return largest <= 2 && (size != 4 || distinct <= 2);
                case HandGuideType.ThreeOfAKind: return largest <= 3 && duplicates <= 1 && (size != 3 || distinct == 1);
                case HandGuideType.FourOfAKind: return largest <= 4 && duplicates <= 1 && (size != 4 || distinct == 1);
                case HandGuideType.FiveOfAKind: return distinct == 1;
                case HandGuideType.FullHouse: return distinct <= 2 && largest <= 3 && triples <= 1;
                case HandGuideType.RoyalFlush:
                    if (largest > 1) return false;
                    for (int i = 0; i < cards.Count; i++) if ((int)cards[i].Face.Rank < 10) return false;
                    return true;
                case HandGuideType.Straight:
                    if (size != straight) return true;
                    if (largest > 1) return false;
                    for (int low = 1; low <= 15 - straight; low++)
                    {
                        bool fits = true;
                        for (int i = 0; i < cards.Count; i++)
                        {
                            int rank = (int)cards[i].Face.Rank; if (low == 1 && rank == 14) rank = 1;
                            if (rank < low || rank >= low + straight) { fits = false; break; }
                        }
                        if (fits) return true;
                    }
                    return false;
                default: return true;
            }
        }

        private static bool GuideContainsWitness(List<HandGuideCard> cards, List<HandGuideCandidate> found)
        {
            // Once a minimal combination is present, adding kickers cannot produce a new minimal witness.
            foreach (var candidate in found)
            {
                if (candidate.Cards.Count > cards.Count) continue;
                bool contains = true;
                foreach (var member in candidate.Cards)
                {
                    bool present = false;
                    for (int i = 0; i < cards.Count; i++) if (cards[i].Card.Id == member.Card.Id) { present = true; break; }
                    if (!present) { contains = false; break; }
                }
                if (contains) return true;
            }
            return false;
        }
    }
}
