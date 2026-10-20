using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public enum EffectAmountKind { Constant = 1, ScoreTotalScale, ScorePointsScale, ThreeOfAKindTotalScale, OnePairTotalScale, FlushBaseTotalScale, CardScoreScale }
    public enum HandEffectVariant { Any = 1, Ordinary, Royal }

    /// <summary>Restricted content expression evaluated from authoritative score facts.</summary>
    public sealed class EffectAmountSpec
    {
        public EffectAmountKind Kind { get; }
        public decimal Value { get; }
        public EffectAmountSpec(EffectAmountKind kind, decimal value)
        {
            if (!Enum.IsDefined(typeof(EffectAmountKind), kind) || value < 0 ||
                kind == EffectAmountKind.Constant && (decimal.Truncate(value) != value || value > long.MaxValue))
                throw new ArgumentException("Invalid effect amount or unsupported amount source.");
            Kind = kind; Value = value;
        }
        public long Resolve(ScoreResult score = null, long? flushBaseTotal = null, string cardId = null, bool includeSharedPoints = false)
        {
            if (Kind == EffectAmountKind.CardScoreScale)
            {
                if (score == null || cardId == null) throw new ArgumentException("Per-card amounts require a repeated hand effect.");
                decimal points = score.Steps.Where(s => s.CardId == cardId && s.Operation == ScoreOperation.AddPoints).Sum(s => s.Amount);
                if (includeSharedPoints) points += score.BasePoints + score.Steps.Where(s => s.CardId == null && s.Operation == ScoreOperation.AddPoints).Sum(s => s.Amount);
                decimal extra = score.Steps.Where(s => s.CardId == cardId && s.Operation == ScoreOperation.AddUnmultipliedPoints).Sum(s => s.Amount);
                return checked((long)decimal.Floor(Value * (points * score.Multiplier + extra)));
            }
            if (Kind == EffectAmountKind.FlushBaseTotalScale)
            {
                if (!flushBaseTotal.HasValue) throw new ArgumentException("Flush base amount requires flush scoring facts.");
                return checked((long)decimal.Floor(Value * flushBaseTotal.Value));
            }
            if (Kind != EffectAmountKind.Constant && score == null)
                throw new ArgumentException("The amount source requires an authoritative score.");
            return checked((long)decimal.Floor(checked(Value * (Kind == EffectAmountKind.Constant ? 1 :
                Kind == EffectAmountKind.ScoreTotalScale ? score.Total : Kind == EffectAmountKind.ScorePointsScale ? score.AllPoints :
                score.Components.Single(c => c.Category == (Kind == EffectAmountKind.ThreeOfAKindTotalScale ? HandCategory.ThreeOfAKind : HandCategory.OnePair)).Total))));
        }
    }

    public sealed class ConfiguredEffectStep
    {
        public string Id { get; }
        public CombatEffectKind Kind { get; }
        public EffectAmountSpec Amount { get; }
        public StatusApplicationSpec Status { get; }
        public StatusApplicationSpec OnHitStatus { get; }
        public EffectTargetSpec Target { get; }
        public bool PerSubmittedCard { get; }
        public ConfiguredEffectStep(string id, CombatEffectKind kind, EffectTargetSpec target,
            EffectAmountSpec amount = null, StatusApplicationSpec status = null, StatusApplicationSpec onHitStatus = null, bool perSubmittedCard = false)
        {
            Id = Guard.Text(id, nameof(id)); Target = target ?? throw new ArgumentNullException(nameof(target));
            bool numeric = kind == CombatEffectKind.Damage || kind == CombatEffectKind.Heal || kind == CombatEffectKind.GainBlock || kind == CombatEffectKind.DrawCards;
            if (!numeric && kind != CombatEffectKind.ApplyStatus && kind != CombatEffectKind.Eliminate && kind != CombatEffectKind.Cleanse ||
                numeric != (amount != null) || (kind == CombatEffectKind.ApplyStatus) != (status != null))
                throw new ArgumentException("Effect payload does not match its kind.");
            if (status?.Duration != null && status.Duration.Kind != DurationKind.UntilRemoved && status.Duration.Kind != DurationKind.TriggerCount ||
                status?.Lifetime?.Kind == StatusLifetimeKind.ThroughNextPhase &&
                status.Lifetime.PhaseKey != BattleStatusTiming.PlayerPhase && status.Lifetime.PhaseKey != BattleStatusTiming.EnemyPhase)
                throw new ArgumentException("Configured effects require relative battle lifetimes.");
            if (onHitStatus != null && kind != CombatEffectKind.Damage) throw new ArgumentException("On-hit status requires damage.");
            if (amount?.Kind == EffectAmountKind.CardScoreScale && !perSubmittedCard) throw new ArgumentException("CardScoreScale requires perSubmittedCard.");
            Kind = kind; Amount = amount; Status = status; OnHitStatus = onHitStatus; PerSubmittedCard = perSubmittedCard;
        }
        public BattleEffectStep Resolve(ScoreResult score = null, long? flushBaseTotal = null, string cardId = null, bool includeSharedPoints = false)
        {
            var effect = Kind == CombatEffectKind.Damage ? BattleEffectSpec.Damage(Amount.Resolve(score, flushBaseTotal, cardId, includeSharedPoints), OnHitStatus) :
                Kind == CombatEffectKind.Heal ? BattleEffectSpec.Heal(Amount.Resolve(score, flushBaseTotal, cardId, includeSharedPoints)) :
                Kind == CombatEffectKind.GainBlock ? BattleEffectSpec.GainBlock(Amount.Resolve(score, flushBaseTotal, cardId, includeSharedPoints)) :
                Kind == CombatEffectKind.DrawCards ? BattleEffectSpec.DrawCards(Amount.Resolve(score, flushBaseTotal, cardId, includeSharedPoints)) :
                Kind == CombatEffectKind.ApplyStatus ? BattleEffectSpec.ApplyStatus(Status) :
                Kind == CombatEffectKind.Cleanse ? BattleEffectSpec.Cleanse() : BattleEffectSpec.Eliminate();
            return new BattleEffectStep(Id, effect, Target);
        }
        public IEnumerable<BattleEffectStep> ResolveAll(ScoreResult score, HandAttackInfo hand)
        {
            if (hand?.ModernScoring == true && Kind == CombatEffectKind.Heal && Amount?.Kind == EffectAmountKind.FlushBaseTotalScale)
            {
                yield return new BattleEffectStep(Id, BattleEffectSpec.HealExact(checked(hand.FlushBaseTotal.Value * Amount.Value)), Target);
                yield break;
            }
            if (!PerSubmittedCard) { yield return Resolve(score, hand?.FlushBaseTotal); yield break; }
            if (hand == null) throw new ArgumentException("Repeated effects require submitted hand facts.");
            var cards = hand.ModernScoring ? hand.AttackCards : hand.SubmittedCards;
            for (int i = 0; i < cards.Count; i++)
            {
                var step = Resolve(score, hand.FlushBaseTotal, cards[i].Id.Value, i == 0);
                yield return i == 0 ? step : new BattleEffectStep(Id + "/card/" + (i + 1), step.Effect, step.Target);
            }
        }
    }

    public sealed class HandEffectEntry
    {
        public HandCategory Category { get; }
        public HandEffectVariant Variant { get; }
        public IReadOnlyList<ConfiguredEffectStep> Effects { get; }
        public HandEffectEntry(HandCategory category, IEnumerable<ConfiguredEffectStep> effects, HandEffectVariant variant = HandEffectVariant.Any)
        {
            var copy = Guard.Copy(effects, nameof(effects));
            if (!Enum.IsDefined(typeof(HandCategory), category) || !Enum.IsDefined(typeof(HandEffectVariant), variant) ||
                category != HandCategory.StraightFlush && variant != HandEffectVariant.Any || copy.Length < 1 || copy.Length > 64 ||
                copy.Any(item => item == null) || copy.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != copy.Length)
                throw new ArgumentException("Invalid hand match or duplicate effect step IDs.");
            if (category != HandCategory.FullHouse && copy.Any(step => step.Amount != null &&
                (step.Amount.Kind == EffectAmountKind.ThreeOfAKindTotalScale || step.Amount.Kind == EffectAmountKind.OnePairTotalScale)))
                throw new ArgumentException("Component score amounts require a full house.");
            Category = category; Variant = variant; Effects = Guard.View(copy);
        }
    }

    /// <summary>Complete, unambiguous data-driven hand mapping using the existing settlement pipeline.</summary>
    public sealed partial class ConfiguredHandEffectRule : IScoredHandEffectRule, IBattleStatusCatalogValidator
    {
        public string Version { get; }
        public IReadOnlyList<HandEffectEntry> Entries { get; }
        public IReadOnlyList<ConfiguredEffectStep> FlushBonus { get; }
        public bool FlushUsesRankEffects { get; }
        public bool CombineCompatibleEffects { get; }
        public bool FlushAsAttribute { get; }
        public bool FullHouseComponentDamage { get; }
        public bool ModernScoring { get; }
        public decimal FlushDamageMultiplier { get; }
        public ConfiguredHandEffectRule(string version, IEnumerable<HandEffectEntry> entries, IEnumerable<ConfiguredEffectStep> flushBonus = null, bool flushUsesRankEffects = false, bool combineCompatibleEffects = false, bool flushAsAttribute = false, bool fullHouseComponentDamage = false, bool modernScoring = false, decimal flushDamageMultiplier = 2m)
        {
            Version = Guard.Text(version, nameof(version));
            var copy = Guard.Copy(entries, nameof(entries));
            if (copy.Any(item => item == null)) throw new ArgumentException("Null hand entries are not supported.");
            foreach (HandCategory category in Enum.GetValues(typeof(HandCategory)))
            {
                var matches = copy.Where(item => item.Category == category).ToArray();
                bool complete = category == HandCategory.StraightFlush ?
                    matches.Length == 1 && matches[0].Variant == HandEffectVariant.Any ||
                    matches.Length == 2 && matches.Any(item => item.Variant == HandEffectVariant.Ordinary) && matches.Any(item => item.Variant == HandEffectVariant.Royal) :
                    matches.Length == 1;
                if (!complete) throw new ArgumentException("Hand mappings must cover every category exactly once, with either one common or separate ordinary/royal straight flush branches: " + category);
            }
            Entries = Guard.View(copy);
            FlushBonus = Guard.View(Guard.Copy(flushBonus ?? Array.Empty<ConfiguredEffectStep>(), nameof(flushBonus)));
            if (FlushBonus.Any(e => e == null) || FlushBonus.Count > 64 || FlushBonus.Select(e => e.Id).Distinct().Count() != FlushBonus.Count)
                throw new ArgumentException("Invalid flush bonus effects.");
            FlushUsesRankEffects = flushUsesRankEffects;
            CombineCompatibleEffects = combineCompatibleEffects; FlushAsAttribute = flushAsAttribute; FullHouseComponentDamage = fullHouseComponentDamage;
            ModernScoring = modernScoring;
            if (flushDamageMultiplier < 0) throw new ArgumentOutOfRangeException(nameof(flushDamageMultiplier));
            FlushDamageMultiplier = flushDamageMultiplier;
            if (modernScoring && (!combineCompatibleEffects || !flushAsAttribute))
                throw new ArgumentException("Modern scoring requires compatible mechanisms and flush as an attribute.");
            if ((flushAsAttribute || fullHouseComponentDamage) && !combineCompatibleEffects) throw new ArgumentException("Composition options require compatible effects.");
            if (flushUsesRankEffects && combineCompatibleEffects) throw new ArgumentException("Choose one hand composition mode.");
            if (combineCompatibleEffects && (copy.Any(entry => !(fullHouseComponentDamage && entry.Category == HandCategory.FullHouse) && entry.Effects.Count(e => e.Kind == CombatEffectKind.Damage) > 1) ||
                copy.Where(entry => !(fullHouseComponentDamage && entry.Category == HandCategory.FullHouse)).SelectMany(entry => entry.Effects).Any(e => e.Amount?.Kind == EffectAmountKind.ThreeOfAKindTotalScale || e.Amount?.Kind == EffectAmountKind.OnePairTotalScale) ||
                FlushBonus.Any(e => e.Kind == CombatEffectKind.Damage)))
                throw new ArgumentException("Compatible mechanisms require one primary damage step per branch, highest-hand score amounts, and non-damage flush bonuses.");
        }
        public BattleEffectSequence Resolve(ScoredHandEffectContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (CombineCompatibleEffects && !(context.Hand?.Pattern.Category == HandCategory.StraightFlush && context.Hand.Pattern.TieBreakRanks[0] == (int)Rank.Ace))
                return ResolveCompatible(context);
            var matches = Entries.Where(item => item.Category == context.Score.Category).ToArray();
            var entry = matches[0];
            if (matches.Length == 2)
            {
                if (context.Hand == null) throw new ArgumentException("Royal flush matching requires authoritative hand facts.");
                bool royal = context.Hand.Pattern.TieBreakRanks[0] == (int)Rank.Ace;
                entry = matches.Single(item => item.Variant == (royal ? HandEffectVariant.Royal : HandEffectVariant.Ordinary));
            }
            bool flush = context.Hand?.IsFlush == true && (ModernScoring || !(context.Score.Category == HandCategory.StraightFlush && context.Hand.Pattern.TieBreakRanks[0] == (int)Rank.Ace));
            if (flush && FlushUsesRankEffects)
                entry = Entries.Single(item => item.Category == context.Hand.RankPattern.Category);
            var effects = entry.Effects.SelectMany(item => item.ResolveAll(context.Score, context.Hand)).ToList();
            if (flush) effects.AddRange(FlushBonus.SelectMany(item => item.ResolveAll(context.Score, context.Hand)).Select(resolved =>
                new BattleEffectStep("flush-bonus/" + resolved.Id, resolved.Effect, resolved.Target)));
            return new BattleEffectSequence(Version, "配置牌型效果：" + entry.Category + (flush ? "＋同花加成" : ""), effects);
        }
        public void ValidateStatusCatalog(CombatStatusCatalog catalog)
        {
            foreach (var step in Entries.SelectMany(item => item.Effects).Concat(FlushBonus))
            { ConfiguredEffectValidation.Status(step.Status, catalog); ConfiguredEffectValidation.Status(step.OnHitStatus, catalog); }
        }
    }

    public sealed class ConfiguredEnemyEffectRule : IEnemyBattleEffectRule, IBattleStatusCatalogValidator
    {
        public BattleEffectSequence Sequence { get; }
        public string Version => Sequence.RuleVersion;
        public ConfiguredEnemyEffectRule(BattleEffectSequence sequence)
        {
            Sequence = sequence ?? throw new ArgumentNullException(nameof(sequence));
            if (Sequence.Steps.Any(step => step.Target.Selection == EffectTargetSelection.Requested))
                throw new ArgumentException("Automatic enemy actions cannot provide requested target bindings.");
        }
        public BattleEffectSequence Resolve(EnemyBattleEffectContext context)
        { if (context == null) throw new ArgumentNullException(nameof(context)); return Sequence; }
        public void ValidateStatusCatalog(CombatStatusCatalog catalog)
        { foreach (var step in Sequence.Steps) { ConfiguredEffectValidation.Status(step.Effect.Status, catalog); ConfiguredEffectValidation.Status(step.Effect.OnHitStatus, catalog); } }
    }

    internal static class ConfiguredEffectValidation
    {
        internal static void Status(StatusApplicationSpec application, CombatStatusCatalog catalog)
        {
            if (application == null) return;
            if (catalog == null) throw new ArgumentException("Status effects require a status catalog.");
            var definition = catalog.Get(application.DefinitionId);
            bool triggerBudget = application.Duration != null ? application.Duration.Kind == DurationKind.TriggerCount :
                (application.Lifetime ?? definition.DefaultLifetime).Kind == StatusLifetimeKind.TriggerCount;
            if (triggerBudget && definition.Behaviors.Count > 1)
                throw new ArgumentException("A trigger budget cannot be shared by different status behaviors.");
        }
    }
}
