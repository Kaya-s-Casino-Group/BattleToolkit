using System;
using System.Linq;
namespace PokerEngine
{
    public sealed class ChipBlockRewardRule : IDiscardRewardRule
    {
        public string Version => "discard.chips.1";
        public decimal Ratio { get; }
        private readonly CardPointProfile points;
        public ChipBlockRewardRule(decimal ratio, CardPointProfile points = null)
        { if (ratio < 0) throw new ArgumentOutOfRangeException(nameof(ratio)); Ratio = ratio; this.points = points ?? CardPointProfile.Default; }
        public DiscardRewardPlan Resolve(DiscardRewardContext context)
        {
            long chips = 0;
            foreach (var card in context.Cards) chips = checked(chips + points.GetBasePoints(new CardFace(card.Face.Rank, card.Face.Suit)));
            long amount = checked((long)decimal.Floor(chips * Ratio));
            return new DiscardRewardPlan(Version, "弃牌筹码 " + chips + " × " + Ratio,
                context.Targets.Select(target => new CombatEffect(CombatEffectKind.GainBlock, context.Source, target, amount)));
        }
    }
}
