using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public enum EffectAmountKind { Constant = 1, ScoreTotalScale, ScorePointsScale }
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
        public long Resolve(ScoreResult score = null)
        {
            if (Kind != EffectAmountKind.Constant && score == null)
                throw new ArgumentException("The amount source requires an authoritative score.");
            return checked((long)decimal.Floor(checked(Value * (Kind == EffectAmountKind.Constant ? 1 :
                Kind == EffectAmountKind.ScoreTotalScale ? score.Total : score.Points))));
        }
    }

    public sealed class ConfiguredEffectStep
    {
        public string Id { get; }
        public CombatEffectKind Kind { get; }
        public EffectAmountSpec Amount { get; }
        public StatusApplicationSpec Status { get; }
        public EffectTargetSpec Target { get; }
        public ConfiguredEffectStep(string id, CombatEffectKind kind, EffectTargetSpec target,
            EffectAmountSpec amount = null, StatusApplicationSpec status = null)
        {
            Id = Guard.Text(id, nameof(id)); Target = target ?? throw new ArgumentNullException(nameof(target));
            bool numeric = kind == CombatEffectKind.Damage || kind == CombatEffectKind.Heal || kind == CombatEffectKind.GainBlock;
            if (!numeric && kind != CombatEffectKind.ApplyStatus && kind != CombatEffectKind.Eliminate ||
                numeric != (amount != null) || (kind == CombatEffectKind.ApplyStatus) != (status != null))
                throw new ArgumentException("Effect payload does not match its kind.");
            if (status?.Duration != null && status.Duration.Kind != DurationKind.UntilRemoved && status.Duration.Kind != DurationKind.TriggerCount ||
                status?.Lifetime?.Kind == StatusLifetimeKind.ThroughNextPhase &&
                status.Lifetime.PhaseKey != BattleStatusTiming.PlayerPhase && status.Lifetime.PhaseKey != BattleStatusTiming.EnemyPhase)
                throw new ArgumentException("Configured effects require relative battle lifetimes.");
            Kind = kind; Amount = amount; Status = status;
        }
        public BattleEffectStep Resolve(ScoreResult score = null)
        {
            var effect = Kind == CombatEffectKind.Damage ? BattleEffectSpec.Damage(Amount.Resolve(score)) :
                Kind == CombatEffectKind.Heal ? BattleEffectSpec.Heal(Amount.Resolve(score)) :
                Kind == CombatEffectKind.GainBlock ? BattleEffectSpec.GainBlock(Amount.Resolve(score)) :
                Kind == CombatEffectKind.ApplyStatus ? BattleEffectSpec.ApplyStatus(Status) : BattleEffectSpec.Eliminate();
            return new BattleEffectStep(Id, effect, Target);
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
            Category = category; Variant = variant; Effects = Guard.View(copy);
        }
    }

    /// <summary>Complete, unambiguous data-driven hand mapping using the existing settlement pipeline.</summary>
    public sealed class ConfiguredHandEffectRule : IScoredHandEffectRule, IBattleStatusCatalogValidator
    {
        public string Version { get; }
        public IReadOnlyList<HandEffectEntry> Entries { get; }
        public ConfiguredHandEffectRule(string version, IEnumerable<HandEffectEntry> entries)
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
        }
        public BattleEffectSequence Resolve(ScoredHandEffectContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var matches = Entries.Where(item => item.Category == context.Score.Category).ToArray();
            var entry = matches[0];
            if (matches.Length == 2)
            {
                if (context.Hand == null) throw new ArgumentException("Royal flush matching requires authoritative hand facts.");
                bool royal = context.Hand.Pattern.TieBreakRanks[0] == (int)Rank.Ace;
                entry = matches.Single(item => item.Variant == (royal ? HandEffectVariant.Royal : HandEffectVariant.Ordinary));
            }
            return new BattleEffectSequence(Version, "配置牌型效果：" + entry.Category, entry.Effects.Select(item => item.Resolve(context.Score)));
        }
        public void ValidateStatusCatalog(CombatStatusCatalog catalog)
        {
            foreach (var step in Entries.SelectMany(item => item.Effects))
                ConfiguredEffectValidation.Status(step.Status, catalog);
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
        { foreach (var step in Sequence.Steps) ConfiguredEffectValidation.Status(step.Effect.Status, catalog); }
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
