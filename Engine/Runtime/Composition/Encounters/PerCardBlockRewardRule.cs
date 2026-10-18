using System;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Each selected living ally gains block from the actual discarded card count. Default targeting is self.</summary>
    public sealed class PerCardBlockRewardRule : IDiscardRewardRule
    {
        public string Version { get; }
        public int BlockPerCard { get; }
        public PerCardBlockRewardRule(int blockPerCard, string version = "discard.block-per-card.1")
        {
            if (blockPerCard < 0) throw new ArgumentOutOfRangeException(nameof(blockPerCard));
            Version = Guard.Text(version, nameof(version)); BlockPerCard = blockPerCard;
        }
        public DiscardRewardPlan Resolve(DiscardRewardContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            long amount = checked((long)BlockPerCard * context.Cards.Count);
            return new DiscardRewardPlan(Version, "每个选定友方获得 " + context.Cards.Count + " × " + BlockPerCard + " = " + amount + " 格挡。",
                amount == 0 ? Array.Empty<CombatEffect>() : context.Targets.Select(target =>
                    new CombatEffect(CombatEffectKind.GainBlock, context.Source, target, amount)).ToArray());
        }
    }
}
