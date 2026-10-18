using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public sealed class RankPointRule
    {
        public Rank Rank { get; }
        public long Points { get; }
        public RankPointRule(Rank rank, long points)
        {
            if (!Enum.IsDefined(typeof(Rank), rank) || points < 0) throw new ArgumentException("Invalid rank chips.");
            Rank = rank; Points = points;
        }
    }
    public sealed class FacePointRule
    {
        public CardFace Face { get; }
        public long Points { get; }
        public FacePointRule(CardFace face, long points)
        {
            Face = face ?? throw new ArgumentNullException(nameof(face));
            if (points < 0) throw new ArgumentOutOfRangeException(nameof(points));
            Points = points;
        }
    }
    /// <summary>Immutable card-chip lookup. Face overrides allow any of the 52 cards to differ; instance growth stays outside this table.</summary>
    public sealed class CardPointProfile
    {
        private readonly Dictionary<Rank, long> ranks;
        private readonly Dictionary<int, long> faces;
        public CardPointProfile(IEnumerable<RankPointRule> ranks, IEnumerable<FacePointRule> overrides = null)
        {
            this.ranks = Guard.Copy(ranks, nameof(ranks)).ToDictionary(x => x.Rank, x => x.Points);
            if (Enum.GetValues(typeof(Rank)).Cast<Rank>().Any(rank => !this.ranks.ContainsKey(rank)))
                throw new ArgumentException("Chips are required for every rank.");
            faces = Guard.Copy(overrides ?? new FacePointRule[0], nameof(overrides)).ToDictionary(x => Key(x.Face), x => x.Points);
        }
        private static int Key(CardFace face) => (int)face.Suit * 100 + (int)face.Rank;
        public long GetBasePoints(Rank rank) => ranks[rank];
        public long GetBasePoints(CardFace face)
        {
            if (face == null) throw new ArgumentNullException(nameof(face));
            return faces.TryGetValue(Key(face), out var points) ? points : ranks[face.Rank];
        }
        public static CardPointProfile Default { get; } = new CardPointProfile(Enum.GetValues(typeof(Rank)).Cast<Rank>()
            .Select(rank => new RankPointRule(rank, rank == Rank.Ace ? 11 : Math.Min(10, (int)rank))));
    }
}
