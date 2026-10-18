using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PokerEngine
{
    public enum RelicHandEffectOperation { AppendEffect = 1, AddHits, ReplaceEffect, ChangeTarget }

    /// <summary>Passive declaration. Selectors match only named steps in the original hand sequence.</summary>
    public sealed class RelicHandEffectModifierSpec
    {
        public string Id { get; }
        public HandCategory HandCategory { get; }
        public RelicHandEffectOperation Operation { get; }
        public string StepId { get; }
        public BattleEffectSpec Effect { get; }
        public EffectTargetSpec Target { get; }
        public int AdditionalHits { get; }

        private RelicHandEffectModifierSpec(string id, HandCategory category, RelicHandEffectOperation operation,
            string stepId = null, BattleEffectSpec effect = null, EffectTargetSpec target = null, int additionalHits = 0)
        {
            Id = Guard.Text(id, nameof(id));
            if (!Enum.IsDefined(typeof(HandCategory), category)) throw new ArgumentException("Unknown hand category.");
            HandCategory = category; Operation = operation;
            StepId = operation == RelicHandEffectOperation.AppendEffect ? null : Guard.Text(stepId, nameof(stepId));
            if (operation == RelicHandEffectOperation.AppendEffect || operation == RelicHandEffectOperation.ReplaceEffect)
                Effect = effect ?? throw new ArgumentNullException(nameof(effect));
            if (operation == RelicHandEffectOperation.AppendEffect || operation == RelicHandEffectOperation.ChangeTarget)
                Target = target ?? throw new ArgumentNullException(nameof(target));
            if (operation == RelicHandEffectOperation.AddHits && (additionalHits < 1 || additionalHits > 63))
                throw new ArgumentOutOfRangeException(nameof(additionalHits));
            AdditionalHits = additionalHits;
            var status = Effect?.Status;
            // Content stores relative lifetimes, never a scope captured from another action/battle.
            if (status?.Duration != null && status.Duration.Kind != DurationKind.UntilRemoved && status.Duration.Kind != DurationKind.TriggerCount ||
                status?.Lifetime?.Kind == StatusLifetimeKind.ThroughNextPhase &&
                status.Lifetime.PhaseKey != BattleStatusTiming.PlayerPhase && status.Lifetime.PhaseKey != BattleStatusTiming.EnemyPhase)
                throw new ArgumentException("Relic status effects require a relative battle lifetime.");
        }

        public static RelicHandEffectModifierSpec Append(string id, HandCategory category, BattleEffectSpec effect, EffectTargetSpec target) =>
            new RelicHandEffectModifierSpec(id, category, RelicHandEffectOperation.AppendEffect, effect: effect, target: target);
        public static RelicHandEffectModifierSpec AddHits(string id, HandCategory category, string stepId, int count) =>
            new RelicHandEffectModifierSpec(id, category, RelicHandEffectOperation.AddHits, stepId, additionalHits: count);
        public static RelicHandEffectModifierSpec Replace(string id, HandCategory category, string stepId, BattleEffectSpec effect) =>
            new RelicHandEffectModifierSpec(id, category, RelicHandEffectOperation.ReplaceEffect, stepId, effect);
        public static RelicHandEffectModifierSpec Retarget(string id, HandCategory category, string stepId, EffectTargetSpec target) =>
            new RelicHandEffectModifierSpec(id, category, RelicHandEffectOperation.ChangeTarget, stepId, target: target);

        internal void Write(BinaryWriter writer)
        {
            writer.Write(Id); writer.Write((int)HandCategory); writer.Write((int)Operation);
            WriteText(writer, StepId); writer.Write(AdditionalHits); WriteEffect(writer, Effect); WriteTarget(writer, Target);
        }
        internal static void WriteEffect(BinaryWriter writer, BattleEffectSpec effect)
        {
            writer.Write(effect != null); if (effect == null) return;
            writer.Write((int)effect.Kind); writer.Write(effect.Amount);
            var status = effect.Status; writer.Write(status != null); if (status == null) return;
            writer.Write(status.DefinitionId.Value); writer.Write(status.Stacks);
            writer.Write(status.Duration != null);
            if (status.Duration != null)
            {
                writer.Write((int)status.Duration.Kind); WriteText(writer, status.Duration.ScopeId); WriteText(writer, status.Duration.PhaseKey);
                writer.Write(status.Duration.RemainingTriggers.HasValue);
                if (status.Duration.RemainingTriggers.HasValue) writer.Write(status.Duration.RemainingTriggers.Value);
            }
            writer.Write(status.Lifetime != null);
            if (status.Lifetime != null)
            {
                writer.Write((int)status.Lifetime.Kind); WriteText(writer, status.Lifetime.PhaseKey);
                writer.Write(status.Lifetime.Count.HasValue); if (status.Lifetime.Count.HasValue) writer.Write(status.Lifetime.Count.Value);
            }
        }
        internal static void WriteTarget(BinaryWriter writer, EffectTargetSpec target)
        {
            writer.Write(target != null); if (target == null) return;
            writer.Write((int)target.Group); writer.Write((int)target.Selection); WriteText(writer, target.RequestSlot);
        }
        private static void WriteText(BinaryWriter writer, string value)
        { writer.Write(value != null); if (value != null) writer.Write(value); }
        internal byte[] PayloadBytes()
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                { WriteEffect(writer, Effect); WriteTarget(writer, Target); }
                return stream.ToArray();
            }
        }
    }

    public sealed class HandEffectModifierContext
    {
        public BattleRelicLoadout Loadout { get; }
        public ScoredHandEffectContext Action { get; }
        public BattleEffectSequence Sequence { get; }
        public HandEffectModifierContext(BattleRelicLoadout loadout, ScoredHandEffectContext action, BattleEffectSequence sequence)
        {
            Loadout = loadout ?? throw new ArgumentNullException(nameof(loadout));
            Action = action ?? throw new ArgumentNullException(nameof(action)); Sequence = sequence ?? throw new ArgumentNullException(nameof(sequence));
            if (loadout.OwnerId != action.Source) throw new ArgumentException("Only the acting player's relics modify their hand sequence.");
        }
    }
    public interface IHandEffectModifier
    {
        HandEffectModification Modify(HandEffectModifierContext context);
    }
    public sealed class HandEffectChange
    {
        public string SourceInstanceId { get; }
        public string RuleId { get; }
        public RelicHandEffectOperation Operation { get; }
        public string BaseStepId { get; }
        public IReadOnlyList<string> ResultStepIds { get; }
        internal HandEffectChange(string source, RelicHandEffectModifierSpec spec, IEnumerable<string> results)
        {
            SourceInstanceId = source; RuleId = spec.Id; Operation = spec.Operation; BaseStepId = spec.StepId;
            ResultStepIds = Guard.View(results.ToArray());
        }
    }
    public sealed class HandEffectModification
    {
        public BattleEffectSequence Input { get; }
        public BattleEffectSequence Output { get; }
        public IReadOnlyList<HandEffectChange> Changes { get; }
        public bool IsIdentity => Changes.Count == 0;
        internal HandEffectModification(BattleEffectSequence input, BattleEffectSequence output, IEnumerable<HandEffectChange> changes)
        { Input = input; Output = output; Changes = Guard.View(changes.ToArray()); }
    }

    /// <summary>Pure structural pass. Replacement/target edits precede additive copies; new steps are never rematched.</summary>
    public sealed class RelicHandEffectModifier : IHandEffectModifier
    {
        private sealed class Entry
        {
            internal string Source;
            internal RelicHandEffectModifierSpec Spec;
            internal string GeneratedId(int index) => "relic/" + Part(Source) + Part(Spec.Id) + index.ToString(CultureInfo.InvariantCulture);
            private static string Part(string value) => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value + "/";
        }
        public HandEffectModification Modify(HandEffectModifierContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var entries = context.Loadout.Relics.SelectMany(relic => context.Loadout.RulesFor(relic.InstanceId).HandEffects
                .Where(spec => spec.HandCategory == context.Action.Score.Category).Select(spec => new Entry { Source = relic.InstanceId, Spec = spec }))
                .OrderBy(entry => entry.Source, StringComparer.Ordinal).ThenBy(entry => entry.Spec.Id, StringComparer.Ordinal).ToArray();
            var output = new List<BattleEffectStep>(); var changes = new List<HandEffectChange>();
            foreach (var step in context.Sequence.Steps)
            {
                var edits = entries.Where(entry => entry.Spec.StepId != null && entry.Spec.StepId == step.Id).ToArray();
                var effect = edits.FirstOrDefault(entry => entry.Spec.Operation == RelicHandEffectOperation.ReplaceEffect)?.Spec.Effect ?? step.Effect;
                var target = edits.FirstOrDefault(entry => entry.Spec.Operation == RelicHandEffectOperation.ChangeTarget)?.Spec.Target ?? step.Target;
                output.Add(edits.Length == 0 ? step : new BattleEffectStep(step.Id, effect, target));
                foreach (var entry in edits)
                {
                    var ids = new List<string>();
                    if (entry.Spec.Operation == RelicHandEffectOperation.AddHits)
                    {
                        if (step.Effect.Kind != CombatEffectKind.Damage || effect.Kind != CombatEffectKind.Damage)
                            throw new ArgumentException("Additional hits require a base damage step that remains damage.");
                        if (output.Count + entry.Spec.AdditionalHits > 64) throw new ArgumentException("Modified hand sequence exceeds 64 steps.");
                        for (int hit = 0; hit < entry.Spec.AdditionalHits; hit++)
                        {
                            string id = entry.GeneratedId(hit); ids.Add(id);
                            output.Add(new BattleEffectStep(id, effect, target));
                        }
                    }
                    else ids.Add(step.Id);
                    changes.Add(new HandEffectChange(entry.Source, entry.Spec, ids));
                }
                if (output.Count > 64) throw new ArgumentException("Modified hand sequence exceeds 64 steps.");
            }
            foreach (var entry in entries.Where(item => item.Spec.Operation == RelicHandEffectOperation.AppendEffect))
            {
                string id = entry.GeneratedId(0);
                output.Add(new BattleEffectStep(id, entry.Spec.Effect, entry.Spec.Target));
                changes.Add(new HandEffectChange(entry.Source, entry.Spec, new[] { id }));
                if (output.Count > 64) throw new ArgumentException("Modified hand sequence exceeds 64 steps.");
            }
            // No match is normal for alternate shapes, e.g. royal flush has no damage step.
            return new HandEffectModification(context.Sequence, changes.Count == 0 ? context.Sequence :
                new BattleEffectSequence(context.Sequence.RuleVersion, context.Sequence.Explanation, output), changes);
        }
    }

    internal static class RelicHandEffectValidation
    {
        internal static void Combinations(IEnumerable<RelicHandEffectModifierSpec> specs)
        {
            foreach (var group in specs.Where(spec => spec.StepId != null).GroupBy(spec => Tuple.Create(spec.HandCategory, spec.StepId)))
            {
                foreach (var kind in new[] { RelicHandEffectOperation.ReplaceEffect, RelicHandEffectOperation.ChangeTarget })
                {
                    var edits = group.Where(spec => spec.Operation == kind).ToArray();
                    if (edits.Length > 1 && edits.Skip(1).Any(spec => !edits[0].PayloadBytes().SequenceEqual(spec.PayloadBytes())))
                        throw new ArgumentException("Conflicting relic edits for the same hand step.");
                }
                if (group.Any(spec => spec.Operation == RelicHandEffectOperation.AddHits) &&
                    group.Any(spec => spec.Operation == RelicHandEffectOperation.ReplaceEffect && spec.Effect.Kind != CombatEffectKind.Damage))
                    throw new ArgumentException("Cannot add hits to a step replaced with a non-damage effect.");
            }
        }
        internal static void Catalog(BattleRelicLoadout loadout, BattlePlayerRules rules, CombatStatusCatalog catalog)
        {
            var specs = loadout.Relics.SelectMany(relic => loadout.RulesFor(relic.InstanceId).HandEffects).ToArray();
            if (specs.Length > 0 && rules.EffectRule == null)
                throw new ArgumentException("Structural relics require a scored-hand effect rule.");
            foreach (var status in specs.Where(spec => spec.Effect?.Status != null).Select(spec => spec.Effect.Status))
            {
                var definition = catalog?.Get(status.DefinitionId) ?? throw new ArgumentException("Relic status effects require a status catalog.");
                bool triggers = status.Duration != null ? status.Duration.Kind == DurationKind.TriggerCount :
                    (status.Lifetime ?? definition.DefaultLifetime).Kind == StatusLifetimeKind.TriggerCount;
                if (triggers && definition.Behaviors.Count > 1)
                    throw new ArgumentException("A trigger budget cannot be shared by different status behaviors.");
            }
        }
    }
}
