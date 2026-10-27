using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    // Preserve old numeric identities; final flush types also have explicit queries.
    public enum HandGuideType { Any, HighCard, OnePair, TwoPair, ThreeOfAKind, Straight, FullHouse, FourOfAKind, FiveOfAKind, RoyalFlush, Flush, StraightFlush, FlushHouse, FlushFive }
    public enum HandGuideAvailability { Complete, Completable, Impossible, SearchLimited }

    public sealed class HandGuideRequest
    {
        public HandGuideType Type { get; }
        public bool RequireFlush { get; }
        public int MaxCandidates { get; }
        public int NodeBudget { get; }
        public HandGuideRequest(HandGuideType type, bool requireFlush = false, int maxCandidates = 24, int nodeBudget = 200000)
        {
            if (!Enum.IsDefined(typeof(HandGuideType), type) || maxCandidates < 1 || maxCandidates > 256 || nodeBudget < 1 || nodeBudget > 2000000)
                throw new ArgumentOutOfRangeException(nameof(type));
            Type = type; RequireFlush = requireFlush || type == HandGuideType.Flush || type == HandGuideType.StraightFlush || type == HandGuideType.FlushHouse || type == HandGuideType.FlushFive; MaxCandidates = maxCandidates; NodeBudget = nodeBudget;
        }
    }

    public sealed class HandGuideCard
    {
        public CardInstance Card { get; }
        public CardDefinition Face { get; }
        public string ZoneKind { get; }
        public bool IsMissing => ZoneKind != CardZoneKinds.Hand;
        internal HandGuideCard(CardInstance card, CardDefinition face, string zoneKind) { Card = card; Face = face; ZoneKind = zoneKind; }
    }

    public sealed class HandGuideCandidate
    {
        public string Id { get; }
        public long Revision { get; }
        public ParticipantId Actor { get; }
        public IReadOnlyList<HandGuideCard> Cards { get; }
        public int MissingCount => Cards.Count(c => c.IsMissing);
        public HandCategory Category { get; }
        public bool IsFlush { get; }
        public BattleHandType FinalType { get; }
        // Binds the preview to the exact immutable source, including encounter and hidden rule state.
        internal CardDuelState Snapshot { get; }
        internal HandGuideCandidate(CardDuelState state, ParticipantId actor, IEnumerable<HandGuideCard> cards, HandCategory category, bool flush, int highRank = 0)
        {
            Snapshot = state; Revision = state.Revision; Actor = actor; Cards = Array.AsReadOnly(cards.ToArray()); Category = category; IsFlush = flush;
            FinalType = HandAttackInfo.ResolveFinalType(category, flush, highRank);
            Id = string.Join("|", Cards.Select(c => c.Card.Id.Value).OrderBy(x => x, StringComparer.Ordinal).Select(x => x.Length + ":" + x));
        }
    }

    public sealed class HandGuideResult
    {
        public long Revision { get; }
        public HandGuideAvailability Availability { get; }
        public IReadOnlyList<HandGuideCandidate> Candidates { get; }
        public bool Truncated { get; }
        public int VisitedNodes { get; }
        public CardDuelError Error { get; }
        internal HandGuideResult(long revision, HandGuideCandidate[] candidates, bool truncated, int visited, CardDuelError error = CardDuelError.None)
        {
            Revision = revision; Candidates = Array.AsReadOnly(candidates); Truncated = truncated; VisitedNodes = visited; Error = error;
            Availability = candidates.Length != 0 ? candidates[0].MissingCount == 0 ? HandGuideAvailability.Complete : HandGuideAvailability.Completable :
                truncated || error != CardDuelError.None ? HandGuideAvailability.SearchLimited : HandGuideAvailability.Impossible;
        }
    }

    /// <summary>Rule evidence only. No simulated state or command is exposed for accidental commit.</summary>
    public sealed class HandGuidePreview
    {
        public HandGuideCandidate Candidate { get; }
        public CardDuelError Error { get; }
        public ScoreResult Score { get; }
        public HandAttackInfo Hand { get; }
        /// <summary>Original configured effects, before structural/relic programs. Retains exact base healing.</summary>
        public BattleEffectSequence RuleEffects { get; }
        /// <summary>After hand/relic programs, before target mitigation, outgoing/incoming modifiers and health caps.</summary>
        public BattleEffectSequence BaseEffects { get; }
        public bool SettlementRequested { get; }
        public CardDuelError SettlementError { get; }
        public IReadOnlyList<CombatEvent> Events { get; }
        internal HandGuidePreview(HandGuideCandidate candidate, CardDuelError error, ScoreResult score = null, HandAttackInfo hand = null,
            BattleEffectSequence effects = null, bool settlementRequested = false, CardDuelTransition settlement = null, BattleEffectSequence ruleEffects = null)
        {
            Candidate = candidate; Error = error; Score = score; Hand = hand; BaseEffects = effects; RuleEffects = ruleEffects; SettlementRequested = settlementRequested;
            SettlementError = settlement?.Error ?? CardDuelError.None;
            Events = Array.AsReadOnly((settlement?.Events ?? Array.Empty<CombatEvent>()).ToArray());
        }
    }
}
