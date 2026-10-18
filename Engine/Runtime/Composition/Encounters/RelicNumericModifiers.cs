using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PokerEngine
{
    public enum BattleNumericStat { DamageDealt = 1, DamageReceived, HealingDone, HealingReceived, BlockGranted, BlockReceived }
    public enum RelicNumericOperation { AddFlat = 1, AddRate = 2 }

    /// <summary>Passive amount configuration. Origins are explicit; derived effects never inherit a primary hand condition.</summary>
    public sealed class RelicNumericModifierSpec
    {
        public string Id { get; }
        public BattleNumericStat Stat { get; }
        public RelicNumericOperation Operation { get; }
        public decimal Value { get; }
        public IReadOnlyList<CombatEffectOrigin> Origins { get; }
        public HandCategory? HandCategory { get; }
        public RelicNumericModifierSpec(string id, BattleNumericStat stat, RelicNumericOperation operation, decimal value,
            IEnumerable<CombatEffectOrigin> origins, HandCategory? handCategory = null)
        {
            Id = Guard.Text(id, nameof(id));
            var allowed = Guard.Copy(origins, nameof(origins));
            if (!Enum.IsDefined(typeof(BattleNumericStat), stat) || !Enum.IsDefined(typeof(RelicNumericOperation), operation) ||
                allowed.Length == 0 || allowed.Distinct().Count() != allowed.Length || allowed.Any(origin => !Enum.IsDefined(typeof(CombatEffectOrigin), origin)) ||
                handCategory.HasValue && (!Enum.IsDefined(typeof(HandCategory), handCategory.Value) ||
                    allowed.Length != 1 || allowed[0] != CombatEffectOrigin.Primary) ||
                operation == RelicNumericOperation.AddFlat && (decimal.Truncate(value) != value || value < long.MinValue || value > long.MaxValue))
                throw new ArgumentException("Invalid relic numeric modifier.");
            Stat = stat; Operation = operation; Value = value;
            Origins = Guard.View(allowed.OrderBy(origin => (int)origin).ToArray()); HandCategory = handCategory;
        }
    }

    /// <summary>Composition-owned battle configuration; Runs and Combat do not depend on this type.</summary>
    public sealed class BattleRelicRules
    {
        public string Version { get; }
        public IReadOnlyList<RelicNumericModifierSpec> Modifiers { get; }
        public IReadOnlyList<RelicHandEffectModifierSpec> HandEffects { get; }
        public IReadOnlyList<RelicRuleValueSpec> RuleValues { get; }
        public IReadOnlyList<RelicRefillRuleSpec> RefillRules { get; }
        public IReadOnlyList<RelicTriggerSpec> Triggers { get; }
        public IReadOnlyList<RelicBoundaryTriggerSpec> BoundaryTriggers { get; }
        public IReadOnlyList<RelicActionTriggerSpec> ActionTriggers { get; }
        public RelicTriggerBudgetSpec TriggerBudget { get; }
        internal bool HasTriggers => Triggers.Count > 0 || BoundaryTriggers.Count > 0 || ActionTriggers.Count > 0;
        internal bool HasBoundaryConfiguration => BoundaryTriggers.Count > 0 || TriggerBudget?.MaxUsesPerBoundary != null ||
            Triggers.Any(trigger => trigger.Occurrence == RelicTriggerOccurrence.PerBoundary);
        public static BattleRelicRules Empty { get; } = new BattleRelicRules("battle-relic.empty.1");
        public BattleRelicRules(string version, IEnumerable<RelicNumericModifierSpec> modifiers = null,
            IEnumerable<RelicHandEffectModifierSpec> handEffects = null, IEnumerable<RelicRuleValueSpec> ruleValues = null,
            IEnumerable<RelicRefillRuleSpec> refillRules = null, IEnumerable<RelicTriggerSpec> triggers = null,
            RelicTriggerBudgetSpec triggerBudget = null, IEnumerable<RelicBoundaryTriggerSpec> boundaryTriggers = null,
            IEnumerable<RelicActionTriggerSpec> actionTriggers = null)
        {
            Version = Guard.Text(version, nameof(version));
            var items = Guard.Copy(modifiers ?? Array.Empty<RelicNumericModifierSpec>(), nameof(modifiers));
            var structural = Guard.Copy(handEffects ?? Array.Empty<RelicHandEffectModifierSpec>(), nameof(handEffects));
            var values = Guard.Copy(ruleValues ?? Array.Empty<RelicRuleValueSpec>(), nameof(ruleValues));
            var refills = Guard.Copy(refillRules ?? Array.Empty<RelicRefillRuleSpec>(), nameof(refillRules));
            var triggered = Guard.Copy(triggers ?? Array.Empty<RelicTriggerSpec>(), nameof(triggers));
            var boundaries = Guard.Copy(boundaryTriggers ?? Array.Empty<RelicBoundaryTriggerSpec>(), nameof(boundaryTriggers));
            var actions = Guard.Copy(actionTriggers ?? Array.Empty<RelicActionTriggerSpec>(), nameof(actionTriggers));
            if (triggered.Length + boundaries.Length + actions.Length == 0 && triggerBudget != null) throw new ArgumentException("A trigger budget requires trigger entries.");
            var ids = items.Select(item => item.Id).Concat(structural.Select(item => item.Id))
                .Concat(values.Select(item => item.Id)).Concat(refills.Select(item => item.Id)).Concat(triggered.Select(item => item.Id))
                .Concat(boundaries.Select(item => item.Id)).Concat(actions.Select(item => item.Id)).ToArray();
            if (ids.Length > 32 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                throw new ArgumentException("A relic supports at most 32 distinctly named battle modifiers.");
            RelicHandEffectValidation.Combinations(structural);
            RelicRuleValidation.Refills(refills);
            Modifiers = Guard.View(items); HandEffects = Guard.View(structural);
            RuleValues = Guard.View(values); RefillRules = Guard.View(refills);
            Triggers = Guard.View(triggered); BoundaryTriggers = Guard.View(boundaries); ActionTriggers = Guard.View(actions);
            TriggerBudget = HasTriggers ? triggerBudget ?? new RelicTriggerBudgetSpec() : null;
        }
        internal void Write(BinaryWriter writer)
        {
            writer.Write(Version); writer.Write(Modifiers.Count);
            foreach (var spec in Modifiers)
            {
                writer.Write(spec.Id); writer.Write((int)spec.Stat); writer.Write((int)spec.Operation); writer.Write(spec.Value);
                writer.Write(spec.Origins.Count); foreach (var origin in spec.Origins) writer.Write((int)origin);
                writer.Write(spec.HandCategory.HasValue); if (spec.HandCategory.HasValue) writer.Write((int)spec.HandCategory.Value);
            }
        }
        internal void WriteHandEffects(BinaryWriter writer)
        {
            writer.Write(Version); writer.Write(HandEffects.Count);
            foreach (var spec in HandEffects) spec.Write(writer);
        }
        internal byte[] Bytes()
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    Write(writer);
                    if (HandEffects.Count != 0) { writer.Write("battle-relic.hand-effects.1"); WriteHandEffects(writer); }
                    if (RuleValues.Count != 0 || RefillRules.Count != 0) { writer.Write("battle-relic.rules.1"); WriteRules(writer); }
                    if (Triggers.Count != 0) { writer.Write("battle-relic.triggers.1"); WriteTriggers(writer); }
                    if (HasBoundaryConfiguration) { writer.Write("battle-relic.boundaries.1"); WriteBoundaries(writer); }
                    if (ActionTriggers.Count != 0) { writer.Write("battle-relic.actions.1"); WriteActions(writer); }
                }
                return stream.ToArray();
            }
        }
        internal void WriteRules(BinaryWriter writer)
        {
            writer.Write(Version); writer.Write(RuleValues.Count);
            foreach (var spec in RuleValues) spec.Write(writer);
            writer.Write(RefillRules.Count);
            foreach (var spec in RefillRules) spec.Write(writer);
        }
        internal void WriteTriggers(BinaryWriter writer)
        {
            writer.Write(Version); writer.Write(Triggers.Count);
            if (Triggers.Count == 0) return;
            TriggerBudget.Write(writer);
            foreach (var spec in Triggers) spec.Write(writer);
        }
        internal void WriteActions(BinaryWriter writer)
        {
            writer.Write(Version); writer.Write(ActionTriggers.Count);
            if (ActionTriggers.Count == 0) return;
            TriggerBudget.Write(writer);
            foreach (var spec in ActionTriggers) spec.Write(writer);
        }
        internal void WriteBoundaries(BinaryWriter writer)
        {
            writer.Write(Version); writer.Write(HasBoundaryConfiguration);
            if (!HasBoundaryConfiguration) return;
            TriggerBudget.Write(writer);
            writer.Write(TriggerBudget.MaxUsesPerBoundary.HasValue);
            if (TriggerBudget.MaxUsesPerBoundary.HasValue) writer.Write(TriggerBudget.MaxUsesPerBoundary.Value);
            writer.Write(BoundaryTriggers.Count); foreach (var spec in BoundaryTriggers) spec.Write(writer);
        }
    }

    public sealed class BattleNumericQuery
    {
        public BattleRelicLoadout Loadout { get; }
        public BattleNumericStat Stat { get; }
        public long BaseAmount { get; }
        public CombatEffectOrigin Origin { get; }
        public HandAttackInfo Hand { get; }
        public BattleNumericQuery(BattleRelicLoadout loadout, BattleNumericStat stat, long baseAmount,
            CombatEffectOrigin origin, HandAttackInfo hand = null)
        {
            Loadout = loadout ?? throw new ArgumentNullException(nameof(loadout));
            if (!Enum.IsDefined(typeof(BattleNumericStat), stat) || baseAmount < 0 || !Enum.IsDefined(typeof(CombatEffectOrigin), origin) ||
                hand != null && origin != CombatEffectOrigin.Primary) throw new ArgumentException("Invalid numeric query.");
            Stat = stat; BaseAmount = baseAmount; Origin = origin; Hand = hand;
        }
    }

    public interface IBattleNumericResolver
    {
        CombatAmountModification Resolve(BattleNumericQuery query);
    }
    public sealed class BattleNumericResolver : IBattleNumericResolver
    {
        public CombatAmountModification Resolve(BattleNumericQuery query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            var contributions = new List<CombatAmountContribution>();
            foreach (var relic in query.Loadout.Relics)
            foreach (var spec in query.Loadout.RulesFor(relic.InstanceId).Modifiers)
            {
                if (spec.Stat != query.Stat || !spec.Origins.Contains(query.Origin) ||
                    spec.HandCategory.HasValue && (query.Hand == null || query.Hand.Pattern.Category != spec.HandCategory.Value)) continue;
                contributions.Add(new CombatAmountContribution(relic.InstanceId, spec.Id,
                    spec.Operation == RelicNumericOperation.AddFlat ? (long)spec.Value : 0,
                    spec.Operation == RelicNumericOperation.AddRate ? spec.Value : 0));
            }
            return new CombatAmountModification(query.BaseAmount, contributions);
        }
    }

    /// <summary>Reads passive configuration only; does not consume charges, cooldowns or runtime counters.</summary>
    public sealed class RelicCombatAmountModifier : ICombatAmountModifier
    {
        private readonly Dictionary<ParticipantId, BattleRelicLoadout> loadouts;
        private readonly HandAttackInfo hand;
        private readonly ParticipantId? handSource;
        private readonly BattleNumericResolver resolver = new BattleNumericResolver();
        public RelicCombatAmountModifier(TeamBattleState state, ParticipantId? handSource = null, HandAttackInfo hand = null)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if ((handSource.HasValue != (hand != null)) || handSource.HasValue && !state.Players.Any(player => player.Id == handSource.Value))
                throw new ArgumentException("Hand facts require their battle player source.");
            loadouts = state.Players.ToDictionary(player => player.Id, player => player.Relics);
            this.hand = hand; this.handSource = handSource;
        }
        public CombatAmountModification Modify(CombatAmountContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            BattleRelicLoadout loadout;
            if (!loadouts.TryGetValue(context.Owner, out loadout)) return new CombatAmountModification(context.Amount);
            bool outgoing = context.Stage == CombatAmountStage.Outgoing;
            BattleNumericStat stat;
            switch (context.Effect.Kind)
            {
                case CombatEffectKind.Damage: stat = outgoing ? BattleNumericStat.DamageDealt : BattleNumericStat.DamageReceived; break;
                case CombatEffectKind.Heal: stat = outgoing ? BattleNumericStat.HealingDone : BattleNumericStat.HealingReceived; break;
                case CombatEffectKind.GainBlock: stat = outgoing ? BattleNumericStat.BlockGranted : BattleNumericStat.BlockReceived; break;
                default: throw new ArgumentException("This effect has no numeric modification stage.");
            }
            var primaryHand = context.Provenance.Origin == CombatEffectOrigin.Primary && context.Effect.Source == handSource ? hand : null;
            return resolver.Resolve(new BattleNumericQuery(loadout, stat, context.Amount, context.Provenance.Origin, primaryHand));
        }
    }
}
