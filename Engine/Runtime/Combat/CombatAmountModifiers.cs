using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public enum CombatAmountStage { Outgoing = 1, Incoming = 2 }

    /// <summary>Pure amount-only adjustment. Called once per stage of each surviving damage/heal/block effect.</summary>
    public interface ICombatAmountModifier
    {
        CombatAmountModification Modify(CombatAmountContext context);
    }

    public sealed class CombatAmountContext
    {
        public CombatState Combat { get; }
        public CombatEffect Effect { get; }
        public CombatEffectProvenance Provenance { get; }
        public CombatAmountStage Stage { get; }
        public long Amount { get; }
        public ParticipantId Owner => Stage == CombatAmountStage.Outgoing ? Effect.Source : Effect.Target;
        internal CombatAmountContext(CombatState combat, CombatEffect effect, CombatEffectProvenance provenance,
            CombatAmountStage stage, long amount)
        { Combat = combat; Effect = effect; Provenance = provenance; Stage = stage; Amount = amount; }
    }

    /// <summary>One immutable numeric contribution. Identities are evidence, not references to a gameplay module.</summary>
    public sealed class CombatAmountContribution
    {
        public string SourceInstanceId { get; }
        public string RuleId { get; }
        public long FlatAdjustment { get; }
        public decimal RateAdjustment { get; }
        public CombatAmountContribution(string sourceInstanceId, string ruleId, long flatAdjustment = 0, decimal rateAdjustment = 0)
        {
            SourceInstanceId = Guard.Text(sourceInstanceId, nameof(sourceInstanceId)); RuleId = Guard.Text(ruleId, nameof(ruleId));
            FlatAdjustment = flatAdjustment; RateAdjustment = rateAdjustment;
        }
    }

    /// <summary>Numeric stage. Sequential mode applies each contribution to the previous result in supplied order.</summary>
    public sealed class CombatAmountModification
    {
        public long InputAmount { get; }
        public long OutputAmount { get; }
        public decimal FlatAdjustment { get; }
        public decimal RateAdjustment { get; }
        public IReadOnlyList<CombatAmountContribution> Contributions { get; }
        public bool Sequential { get; }
        public bool IsIdentity => Contributions.Count == 0;

        public CombatAmountModification(long inputAmount, IEnumerable<CombatAmountContribution> contributions = null, bool sequential = false)
        {
            if (inputAmount < 0) throw new ArgumentOutOfRangeException(nameof(inputAmount));
            var items = Guard.Copy(contributions ?? Array.Empty<CombatAmountContribution>(), nameof(contributions));
            if (items.Length > 1024 || items.Select(item => Tuple.Create(item.SourceInstanceId, item.RuleId)).Distinct().Count() != items.Length)
                throw new ArgumentException("Numeric contributions require distinct identities and at most 1024 entries.", nameof(contributions));
            // Legacy aggregate callers retain canonical reduction; ordered callers preserve acquisition order.
            if (!sequential) items = items.OrderBy(item => item.SourceInstanceId, StringComparer.Ordinal).ThenBy(item => item.RuleId, StringComparer.Ordinal).ToArray();
            decimal flat = 0, rate = 0;
            foreach (var item in items) { flat = checked(flat + item.FlatAdjustment); rate = checked(rate + item.RateAdjustment); }
            decimal adjusted = inputAmount;
            if (sequential)
                foreach (var item in items)
                    adjusted = checked((long)decimal.Floor(Math.Max(0m, checked(adjusted * Math.Max(0m, checked(1m + item.RateAdjustment)) + item.FlatAdjustment))));
            else adjusted = checked(inputAmount * Math.Max(0m, checked(1m + rate)) + flat);
            Sequential = sequential;
            InputAmount = inputAmount; OutputAmount = checked((long)decimal.Floor(Math.Max(0m, adjusted)));
            FlatAdjustment = flat; RateAdjustment = rate; Contributions = Guard.View(items);
        }

        internal static CombatAmountModification Validate(CombatAmountModification result, long inputAmount)
        {
            if (result == null || result.InputAmount != inputAmount)
                throw new ArgumentException("An amount modifier must report the supplied input.");
            return result;
        }
    }
}
