using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PokerEngine
{
    public sealed partial class ConfiguredHandEffectRule
    {
        private sealed class CompatibleDamage
        {
            internal HandEffectEntry Entry;
            internal ConfiguredEffectStep Step;
        }
        private BattleEffectSequence ResolveCompatible(ScoredHandEffectContext context)
        {
            if (context.Hand == null || !context.Hand.UsesCompatibleEffects)
                throw new ArgumentException("Compatible effects require authoritative compatible hand facts.");
            var branches = context.Hand.CompatibleCategories.Select(category => Entries.Single(entry =>
                entry.Category == category && entry.Variant != HandEffectVariant.Royal)).ToArray();
            var damage = branches.SelectMany(entry => entry.Effects.Where(step => step.Kind == CombatEffectKind.Damage)
                .Select(step => new CompatibleDamage { Entry = entry, Step = step })).ToArray();
            var effects = new List<BattleEffectStep>();
            bool components = FullHouseComponentDamage && context.Score.Category == HandCategory.FullHouse;
            if (components)
                foreach (var step in Entries.Single(e => e.Category == HandCategory.FullHouse).Effects.Where(e => e.Kind == CombatEffectKind.Damage))
                    effects.AddRange(step.ResolveAll(context.Score, context.Hand));
            if (!components && damage.Length > 0)
            {
                // Categories are descending: amount and step identity come from the highest matched branch.
                var primary = damage[0].Step;
                bool repeated = damage.Any(d => d.Step.PerSubmittedCard);
                var target = MergeTargets(damage.Select(d => d.Step.Target).ToArray());
                var onHit = damage.Select(d => d.Step.OnHitStatus).Where(s => s != null).ToArray();
                if (onHit.Length > 1 && onHit.Skip(1).Any(s => !StatusBytes(onHit[0]).SequenceEqual(StatusBytes(s))))
                    throw new ArgumentException("Compatible damage steps have conflicting on-hit statuses.");
                var status = onHit.FirstOrDefault();
                long total = primary.Amount.Kind == EffectAmountKind.CardScoreScale
                    ? checked((long)decimal.Floor(primary.Amount.Value * context.Score.Total))
                    : primary.Amount.Resolve(context.Score, context.Hand.FlushBaseTotal);
                var aliases = damage.Select(d => new HandEffectSourceAlias(d.Entry.Category, d.Step.Id)).ToArray();
                if (!repeated)
                    effects.Add(new BattleEffectStep(primary.Id, BattleEffectSpec.Damage(total, status), target, aliases));
                else
                {
                    var amounts = DivideDamage(total, context.Score, context.Hand);
                    for (int i = 0; i < amounts.Length; i++)
                    {
                        string id = i == 0 ? primary.Id : primary.Id + "/card/" + (i + 1);
                        // Base aliases stay on the first hit so AddHits means extra hits once per action.
                        var sources = i == 0 ? aliases : damage.Select(d =>
                            new HandEffectSourceAlias(d.Entry.Category, d.Step.Id + "/card/" + (i + 1))).ToArray();
                        effects.Add(new BattleEffectStep(id, BattleEffectSpec.Damage(amounts[i], status), target, sources));
                    }
                }
            }
            foreach (var branch in branches)
                foreach (var configured in branch.Effects.Where(e => e.Kind != CombatEffectKind.Damage))
                    foreach (var step in configured.ResolveAll(context.Score, context.Hand))
                        effects.Add(new BattleEffectStep("mechanism/" + branch.Category + "/" + step.Id, step.Effect, step.Target,
                            new[] { new HandEffectSourceAlias(branch.Category, step.Id) }));
            if (context.Hand.IsFlush)
                foreach (var configured in FlushBonus)
                    foreach (var step in configured.ResolveAll(context.Score, context.Hand))
                        effects.Add(new BattleEffectStep("flush-bonus/" + step.Id, step.Effect, step.Target,
                            new[] { new HandEffectSourceAlias(HandCategory.Flush, "flush-bonus/" + step.Id) }));
            return new BattleEffectSequence(Version, (components ? "三条＋对子分别计伤：" : "最高牌型计伤：") + context.Score.Category + "；兼容机制：" +
                string.Join("、", context.Hand.CompatibleCategories), effects);
        }
        private static long[] DivideDamage(long total, ScoreResult score, HandAttackInfo hand)
        {
            var weights = hand.SubmittedCards.Select((card, index) =>
            {
                decimal points = score.Steps.Where(s => s.CardId == card.Id.Value && s.Operation == ScoreOperation.AddPoints).Sum(s => s.Amount);
                if (index == 0) points += score.BasePoints + score.Steps.Where(s => s.CardId == null && s.Operation == ScoreOperation.AddPoints).Sum(s => s.Amount);
                return checked(points * score.Multiplier + score.Steps.Where(s => s.CardId == card.Id.Value && s.Operation == ScoreOperation.AddUnmultipliedPoints).Sum(s => s.Amount));
            }).ToArray();
            decimal weight = weights.Sum(), accumulated = 0; long assigned = 0;
            var result = new long[weights.Length];
            if (weight == 0) { result[0] = total; return result; }
            for (int i = 0; i < result.Length; i++)
            {
                accumulated += weights[i];
                long cumulative = i == result.Length - 1 ? total : ExactProportion(total, accumulated, weight);
                result[i] = checked(cumulative - assigned); assigned = cumulative;
            }
            return result;
        }
        private static long ExactProportion(long total, decimal part, decimal whole)
        {
            // Decimal weights are finite rationals; integer arithmetic avoids an overflowing intermediate product.
            Func<decimal, System.Numerics.BigInteger[]> rational = value => {
                var bits = decimal.GetBits(value);
                var numerator = (System.Numerics.BigInteger)(uint)bits[0] + ((System.Numerics.BigInteger)(uint)bits[1] << 32) + ((System.Numerics.BigInteger)(uint)bits[2] << 64);
                if (bits[3] < 0) numerator = -numerator;
                return new[] { numerator, System.Numerics.BigInteger.Pow(10, (bits[3] >> 16) & 255) };
            };
            var a = rational(part); var b = rational(whole);
            var numeratorTotal = total * a[0] * b[1]; var divisor = a[1] * b[0];
            var quotient = System.Numerics.BigInteger.DivRem(numeratorTotal, divisor, out var remainder);
            if (remainder.Sign != 0 && numeratorTotal.Sign != divisor.Sign) quotient--;
            return checked((long)quotient);
        }
        private static EffectTargetSpec MergeTargets(EffectTargetSpec[] targets)
        {
            var primary = targets[0];
            if (targets.Any(t => t.Group != primary.Group)) throw new ArgumentException("Compatible damage targets must use the same target group.");
            if (targets.Any(t => t.Selection == EffectTargetSelection.AllLiving))
                return primary.Group == EffectTargetGroup.Opponents ? EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving) :
                    primary.Group == EffectTargetGroup.Allies ? EffectTargetSpec.Allies(EffectTargetSelection.AllLiving) : EffectTargetSpec.Self();
            if (targets.Any(t => t.Selection != primary.Selection || t.RequestSlot != primary.RequestSlot))
                throw new ArgumentException("Compatible single-target damage selectors conflict.");
            var chains = targets.Select(t => t.RetargetChain).Where(c => c != null).Distinct().ToArray();
            if (chains.Length > 1) throw new ArgumentException("Compatible damage retarget chains conflict.");
            return chains.Length == 1 ? primary.WithRetargetChain(chains[0]) : primary;
        }
        private static byte[] StatusBytes(StatusApplicationSpec status)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                    RelicHandEffectModifierSpec.WriteEffect(writer, BattleEffectSpec.Damage(0, status));
                return stream.ToArray();
            }
        }
    }
}
