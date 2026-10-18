using System;
using System.Collections.Generic;

namespace PokerEngine
{
    /// <summary>Stable base-step names, independent of sequence positions and configured hit count.</summary>
    public static class PokerHandEffectSteps
    {
        public const string Heal = "heal", Stun = "stun", ControlImmunity = "control-immunity", Eliminate = "eliminate";
        public static string DamageHit(int number)
        {
            if (number < 1 || number > 64) throw new ArgumentOutOfRangeException(nameof(number));
            return "damage/" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Documented hand effects. Target expansion, modifiers and state mutation remain in the shared pipeline.</summary>
    public sealed class PokerHandEffectRule : IScoredHandEffectRule, IBattleStatusCatalogValidator
    {
        public PokerHandEffectProfile Profile { get; }
        public EffectTargetSpec SingleTarget { get; }
        public string Version => Profile.Version;

        public PokerHandEffectRule(PokerHandEffectProfile profile, EffectTargetSpec singleTarget = null)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            SingleTarget = singleTarget ?? EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving);
            if (SingleTarget.Group != EffectTargetGroup.Opponents ||
                SingleTarget.Selection != EffectTargetSelection.Requested && SingleTarget.Selection != EffectTargetSelection.FirstLiving &&
                SingleTarget.Selection != EffectTargetSelection.LowestHealth && SingleTarget.Selection != EffectTargetSelection.HighestHealth)
                throw new ArgumentException("Single-target hand effects require one requested or automatically selected opponent.", nameof(singleTarget));
        }

        public void ValidateStatusCatalog(CombatStatusCatalog catalog)
        {
            if (catalog == null) throw new ArgumentException("Poker hand effects require their configured status catalog.", nameof(catalog));
            var stun = catalog.Get(Profile.StunStatusId);
            var immunity = catalog.Get(Profile.ControlImmunityStatusId);
            if (stun.Tags != StatusTags.Control || stun.ReapplyPolicy != StatusReapplyPolicy.KeepLonger ||
                stun.Behaviors.Count != 1 || !(stun.Behaviors[0] is BlockActionWindowSpec) ||
                immunity.Tags != StatusTags.None || immunity.ReapplyPolicy != StatusReapplyPolicy.KeepLonger ||
                immunity.Behaviors.Count != 1 || !(immunity.Behaviors[0] is PreventStatusApplicationSpec) ||
                ((PreventStatusApplicationSpec)immunity.Behaviors[0]).BlockedTags != StatusTags.Control)
                throw new ArgumentException("Poker hand status IDs must resolve to the required stun and control-immunity behaviors.", nameof(catalog));
        }

        public BattleEffectSequence Resolve(ScoredHandEffectContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var category = context.Score.Category;
            if (category == HandCategory.StraightFlush)
            {
                if (context.Hand == null)
                    throw new ArgumentException("Straight flush effects require authoritative hand facts to distinguish a royal flush.", nameof(context));
                if (context.Hand.Pattern.TieBreakRanks[0] == (int)Rank.Ace)
                    return new BattleEffectSequence(Version, "皇家同花顺：消灭全部存活敌人", new[] {
                        new BattleEffectStep(PokerHandEffectSteps.Eliminate, BattleEffectSpec.Eliminate(), EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving))
                    });
            }
            if (category == HandCategory.FullHouse)
                return new BattleEffectSequence(Version, "葫芦：单体眩晕与自身提前免控", new[] {
                    new BattleEffectStep(PokerHandEffectSteps.Stun, BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(Profile.StunStatusId, 1,
                        StatusDuration.ForTriggers(Profile.StunActionWindows))), SingleTarget),
                    new BattleEffectStep(PokerHandEffectSteps.ControlImmunity, BattleEffectSpec.ApplyStatus(StatusApplicationSpec.WithLifetime(Profile.ControlImmunityStatusId,
                        Profile.ControlImmunityLifetime)), EffectTargetSpec.Self())
                });

            var settings = Profile.DamageByHand[category];
            long damage = Scale(context.Score.Total, settings.ScoreFactorPerHit);
            var target = category == HandCategory.ThreeOfAKind || category == HandCategory.FourOfAKind
                ? EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving) : SingleTarget;
            var steps = new List<BattleEffectStep>();
            for (int hit = 0; hit < settings.HitCount; hit++)
                steps.Add(new BattleEffectStep(PokerHandEffectSteps.DamageHit(hit + 1), BattleEffectSpec.Damage(damage), target));
            if (category == HandCategory.Flush)
                steps.Add(new BattleEffectStep(PokerHandEffectSteps.Heal, BattleEffectSpec.Heal(Scale(context.Score.Points, Profile.FlushHealPerChip)), EffectTargetSpec.Self()));
            // Ordinary straight flush and five of a kind have no documented skill; retain explicit single-hit damage fallbacks.
            return new BattleEffectSequence(Version, category == HandCategory.Flush ? "同花：伤害与按筹码回血" :
                category == HandCategory.Straight ? "顺子：按配置逐段伤害" :
                category == HandCategory.StraightFlush || category == HandCategory.FiveOfAKind ? "文档未定义牌型：单体伤害兼容规则" : "牌型伤害：每个目标独立结算", steps);
        }

        private static long Scale(long value, decimal factor) => checked((long)decimal.Floor(checked(value * factor)));
    }
}
