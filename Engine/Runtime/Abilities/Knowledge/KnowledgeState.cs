using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public enum KnowledgeMode { Snapshot = 1, TrackCardInZone }
    public enum KnowledgeAudience { Participant = 1, Everyone }

    /// <summary>Round-scoped authorization. Snapshot preserves observed facts; tracking stops outside its bound zone.</summary>
    [DataContract]
    public sealed class KnowledgeGrant
    {
        [DataMember(Order = 1, IsRequired = true)] public KnowledgeGrantId Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public EntityRef Source { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public KnowledgeAudience Audience { get; private set; }
        [DataMember(Order = 4)] public ParticipantId? ViewerId { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public KnowledgeMode Mode { get; private set; }
        [DataMember(Order = 6, IsRequired = true)] public string ScopeId { get; private set; }
        [DataMember(Order = 7, IsRequired = true)] public CardInstanceId CardId { get; private set; }
        [DataMember(Order = 8, IsRequired = true)] public long GrantedAtRevision { get; private set; }
        [DataMember(Order = 9)] public string UntilPhaseEnd { get; private set; }
        [DataMember(Order = 10)] public CardFace ObservedFace { get; private set; }
        [DataMember(Order = 11)] public ZoneId? BoundZoneId { get; private set; }
        public KnowledgeGrant(KnowledgeGrantId id, EntityRef source, KnowledgeAudience audience, ParticipantId? viewerId,
            KnowledgeMode mode, string scopeId, CardInstanceId cardId, long grantedAtRevision,
            CardFace observedFace = null, ZoneId? boundZoneId = null, string untilPhaseEnd = null)
        {
            if (!id.IsValid || string.IsNullOrWhiteSpace(scopeId) || !cardId.IsValid || grantedAtRevision < 0 ||
                !Enum.IsDefined(typeof(KnowledgeAudience), audience) || !Enum.IsDefined(typeof(KnowledgeMode), mode) ||
                (audience == KnowledgeAudience.Participant) != viewerId.HasValue || (viewerId.HasValue && !viewerId.Value.IsValid) ||
                (mode == KnowledgeMode.Snapshot) != (observedFace != null) || (mode == KnowledgeMode.TrackCardInZone) != boundZoneId.HasValue ||
                (boundZoneId.HasValue && !boundZoneId.Value.IsValid) || (untilPhaseEnd != null && string.IsNullOrWhiteSpace(untilPhaseEnd)))
                throw new ArgumentException("Invalid knowledge grant.");
            Id = id; Source = source ?? throw new ArgumentNullException(nameof(source)); Audience = audience; ViewerId = viewerId; Mode = mode;
            ScopeId = scopeId; CardId = cardId; GrantedAtRevision = grantedAtRevision; ObservedFace = observedFace; BoundZoneId = boundZoneId; UntilPhaseEnd = untilPhaseEnd;
        }
    }

    public interface ICardFaceResolver
    {
        CardFace Resolve(AbilityWorld state, ContentCatalog catalog, CardInstanceId cardId);
    }

    /// <summary>Suitable only for rules with no face modifiers. Modified games supply their authoritative resolver.</summary>
    public sealed class BaseCardFaceResolver : ICardFaceResolver
    {
        public CardFace Resolve(AbilityWorld state, ContentCatalog catalog, CardInstanceId cardId)
        {
            var card = state.Cards.First(c => c.Id == cardId); var definition = catalog.Get(card.DefinitionId);
            return new CardFace(definition.Rank, definition.Suit);
        }
    }

    public sealed class KnownCardObservation
    {
        public CardInstanceId CardId { get; }
        public CardFace Face { get; }
        public long ObservedAtRevision { get; }
        public bool IsHistorical { get; }
        internal KnownCardObservation(CardInstanceId cardId, CardFace face, long revision, bool historical)
        { CardId = cardId; Face = face; ObservedAtRevision = revision; IsHistorical = historical; }
    }

    /// <summary>Additional observations only, not the complete PlayerView. Does not expose grants, sources, zones or random state.</summary>
    public static class KnowledgeProjector
    {
        public static IReadOnlyList<KnownCardObservation> Project(AbilityWorld state, ContentCatalog catalog, ParticipantId viewer,
            ICardFaceResolver faceResolver)
        {
            if (state == null || catalog == null || faceResolver == null) throw new ArgumentNullException();
            AbilityWorldValidator.Validate(state, catalog);
            if (!state.Participants.Any(p => p.Id == viewer)) throw new ArgumentException("Viewer is not a participant.");
            var observations = new List<KnownCardObservation>();
            if (state.ScopeId == null || !state.AllowKnowledgeProjection || !state.IsRunning) return observations.AsReadOnly();
            foreach (var grant in state.Knowledge.OrderBy(g => g.GrantedAtRevision).ThenBy(g => g.Id.Value, StringComparer.Ordinal))
            {
                if (grant.Audience != KnowledgeAudience.Everyone && grant.ViewerId != viewer) continue;
                if (grant.ScopeId != state.ScopeId || (grant.UntilPhaseEnd != null && grant.UntilPhaseEnd != state.PhaseKey)) continue;
                if (grant.Mode == KnowledgeMode.Snapshot)
                    observations.Add(new KnownCardObservation(grant.CardId, grant.ObservedFace, grant.GrantedAtRevision, true));
                else if (state.Zones.Any(z => z.Id == grant.BoundZoneId && z.Cards.Contains(grant.CardId)))
                {
                    var face = faceResolver.Resolve(state, catalog, grant.CardId);
                    if (face == null) throw new InvalidOperationException("Face resolver returned no face.");
                    observations.Add(new KnownCardObservation(grant.CardId, face, state.Revision, false));
                }
            }
            return observations.AsReadOnly();
        }
    }
}
