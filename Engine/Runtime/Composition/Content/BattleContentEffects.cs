using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    internal partial class BattleContentBuildContext
    {
        private static readonly string[] EffectAttributes = { "id", "kind", "amount", "amountKind", "target", "selection", "requestSlot", "statusRef", "stacks", "lifetime", "count", "phase" };
        private static readonly string[] TargetAttributes = { "target", "selection", "requestSlot" };

        internal Dictionary<string, CombatStatusDefinition> ReadStatuses(BattleContentNode section)
        {
            return ReadDefinitions(section, "Status", node =>
            {
                Check(node, new[] { "id", "name", "tags", "lifetime", "count", "phase", "reapply" },
                    new[] { "BlockActionWindow", "PreventStatusApplication", "ReflectBlockedDamage", "LifeSteal", "OverhealDamage", "PeriodicDamage" });
                var behaviors = node.Children.Select(child => Capture(child, () => ReadStatusBehavior(child))).Where(item => item != null).ToArray();
                return new CombatStatusDefinition(new StatusDefinitionId(Text(node, "id")), Enum(node, "tags", StatusTags.None), behaviors,
                    ReadLifetime(node, true), Enum(node, "reapply", StatusReapplyPolicy.KeepLonger));
            });
        }

        private StatusBehaviorSpec ReadStatusBehavior(BattleContentNode node)
        {
            switch (node.Name)
            {
                case "BlockActionWindow": Check(node, new string[0], new string[0]); return new BlockActionWindowSpec();
                case "PreventStatusApplication":
                    Check(node, new[] { "tags" }, new string[0]);
                    return new PreventStatusApplicationSpec(Enum(node, "tags", StatusTags.Control));
                case "ReflectBlockedDamage":
                    Check(node, new[] { "ratio", "target", "selection", "sourcePolicy" }, new string[0]);
                    Text(node, "ratio");
                    return new ReflectBlockedDamageSpec(Decimal(node, "ratio", 1), ReadCombatTarget(node, "EventSource"),
                        Enum(node, "sourcePolicy", CombatReactionSourcePolicy.RequireLiving));
                case "LifeSteal":
                    Check(node, new[] { "ratio", "target", "selection" }, new string[0]);
                    Text(node, "ratio");
                    return new LifeStealSpec(Decimal(node, "ratio", 1), ReadCombatTarget(node, "Self"));
                case "OverhealDamage":
                    Check(node, new[] { "ratio", "target", "selection" }, new string[0]);
                    Text(node, "ratio");
                    return new OverhealDamageSpec(Decimal(node, "ratio", 1), ReadCombatTarget(node, "Opponents"));
                case "PeriodicDamage":
                    Check(node, new[] { "timing", "amount", "target", "selection" }, new string[0]);
                    Text(node, "timing"); Text(node, "amount");
                    return new PeriodicDamageSpec(Enum(node, "timing", CombatTimingKind.ActionCompleted), Long(node, "amount", 0), ReadCombatTarget(node, "Self"));
                default: throw new ArgumentException("Unsupported status behavior: " + node.Name);
            }
        }

        internal Dictionary<string, ConfiguredHandEffectRule> ReadHandRules(BattleContentNode section)
        {
            return ReadDefinitions(section, "HandRule", node =>
            {
                Check(node, new[] { "id", "name", "version" }, new[] { "Hand" });
                var entries = node.Children.Where(item => item.Name == "Hand").Select(hand => Capture(hand, () =>
                {
                    Check(hand, new[] { "category", "variant" }, new[] { "Effect" });
                    var steps = hand.Children.Where(item => item.Name == "Effect").Select(effect => Capture(effect, () => ReadConfiguredStep(effect, true))).Where(item => item != null).ToArray();
                    return new HandEffectEntry(ReadRequiredEnum<HandCategory>(hand, "category"), steps, Enum(hand, "variant", HandEffectVariant.Any));
                })).Where(item => item != null).ToArray();
                return new ConfiguredHandEffectRule(Text(node, "version", ContentVersion), entries);
            });
        }

        internal BattleEffectSequence ReadEnemySequence(BattleContentNode node)
        {
            Check(node, new[] { "version", "explanation" }, new[] { "Effect" });
            var steps = node.Children.Where(item => item.Name == "Effect").Select(effect =>
            {
                var step = ReadConfiguredStep(effect, false);
                if (step.Target.Selection == EffectTargetSelection.Requested)
                    throw new ArgumentException("Automatic enemy actions cannot provide requested target bindings.");
                return step.Resolve();
            }).ToArray();
            return new BattleEffectSequence(Text(node, "version", ContentVersion), Text(node, "explanation", "配置敌人效果"),
                steps);
        }

        private ConfiguredEffectStep ReadConfiguredStep(BattleContentNode node, bool allowScore)
        {
            Check(node, EffectAttributes, new string[0]);
            var kind = ReadEffectKind(node);
            return new ConfiguredEffectStep(Text(node, "id"), kind, ReadEffectTarget(node),
                IsNumeric(kind) ? ReadAmount(node, allowScore) : null, kind == CombatEffectKind.ApplyStatus ? ReadStatusApplication(node) : null);
        }

        private BattleEffectSpec ReadConstantEffect(BattleContentNode node, bool allowTarget = true, bool allowId = true)
        {
            Check(node, EffectAttributes.Where(item => (allowTarget || !TargetAttributes.Contains(item)) && (allowId || item != "id")).ToArray(), new string[0]);
            var kind = ReadEffectKind(node);
            long amount = IsNumeric(kind) ? ReadAmount(node, false).Resolve() : 0;
            switch (kind)
            {
                case CombatEffectKind.Damage: return BattleEffectSpec.Damage(amount);
                case CombatEffectKind.Heal: return BattleEffectSpec.Heal(amount);
                case CombatEffectKind.GainBlock: return BattleEffectSpec.GainBlock(amount);
                case CombatEffectKind.ApplyStatus: return BattleEffectSpec.ApplyStatus(ReadStatusApplication(node));
                default: return BattleEffectSpec.Eliminate();
            }
        }
        private CombatEffectKind ReadEffectKind(BattleContentNode node)
        {
            var kind = ReadRequiredEnum<CombatEffectKind>(node, "kind");
            if (!IsNumeric(kind) && kind != CombatEffectKind.ApplyStatus && kind != CombatEffectKind.Eliminate)
                throw new ArgumentException("Only Damage, Heal, GainBlock, ApplyStatus and Eliminate are content effects.");
            RejectAttributes(node, IsNumeric(kind) ? new[] { "statusRef", "stacks", "lifetime", "count", "phase" } :
                kind == CombatEffectKind.ApplyStatus ? new[] { "amount", "amountKind" } :
                new[] { "amount", "amountKind", "statusRef", "stacks", "lifetime", "count", "phase" });
            return kind;
        }
        private static bool IsNumeric(CombatEffectKind kind) => kind == CombatEffectKind.Damage || kind == CombatEffectKind.Heal || kind == CombatEffectKind.GainBlock;
        private EffectAmountSpec ReadAmount(BattleContentNode node, bool allowScore)
        {
            Text(node, "amount");
            var kind = Enum(node, "amountKind", EffectAmountKind.Constant);
            if (!allowScore && kind != EffectAmountKind.Constant) throw new ArgumentException("Score amount sources are only available to hand effects.");
            return new EffectAmountSpec(kind, Decimal(node, "amount", 0));
        }
        private StatusApplicationSpec ReadStatusApplication(BattleContentNode node)
        {
            var id = new StatusDefinitionId(Text(node, "statusRef"));
            if (!Statuses.ContainsKey(id.Value)) throw new ArgumentException("Unknown status reference: " + id.Value);
            var lifetime = ReadLifetime(node, false);
            int stacks = Int(node, "stacks", 1);
            var application = lifetime == null ? new StatusApplicationSpec(id, stacks) : StatusApplicationSpec.WithLifetime(id, lifetime, stacks);
            var definition = Statuses[id.Value];
            if ((lifetime ?? definition.DefaultLifetime).Kind == StatusLifetimeKind.TriggerCount && definition.Behaviors.Count > 1)
                throw new ArgumentException("A trigger budget cannot be shared by different status behaviors.");
            return application;
        }
        private StatusLifetimeSpec ReadLifetime(BattleContentNode node, bool defaultLifetime)
        {
            if (Optional(node, "lifetime") == null && !defaultLifetime)
            { RejectAttributes(node, new[] { "count", "phase" }); return null; }
            var kind = Enum(node, "lifetime", StatusLifetimeKind.UntilRemoved);
            RejectAttributes(node, kind == StatusLifetimeKind.TriggerCount ? new[] { "phase" } :
                kind == StatusLifetimeKind.ThroughNextPhase ? new[] { "count" } : new[] { "count", "phase" });
            switch (kind)
            {
                case StatusLifetimeKind.UntilRemoved: return StatusLifetimeSpec.UntilRemoved();
                case StatusLifetimeKind.TriggerCount: return StatusLifetimeSpec.ForTriggers(Int(node, "count", 1));
                case StatusLifetimeKind.ThroughCurrentRound: return StatusLifetimeSpec.ThroughCurrentRound();
                default:
                    string phase = Text(node, "phase");
                    if (phase != BattleStatusTiming.PlayerPhase && phase != BattleStatusTiming.EnemyPhase)
                        throw new ArgumentException("Status phase must be player or enemy.");
                    return StatusLifetimeSpec.ThroughNextPhase(phase);
            }
        }
        private EffectTargetSpec ReadEffectTarget(BattleContentNode node)
        {
            string group = Text(node, "target", "Opponents");
            if (group == "Self")
            {
                RejectAttributes(node, new[] { "requestSlot" });
                if (Optional(node, "selection") != null && Text(node, "selection") != "AllLiving")
                    throw new ArgumentException("Self does not support target selection.");
                return EffectTargetSpec.Self();
            }
            var selection = Enum(node, "selection", EffectTargetSelection.FirstLiving);
            string slot = Optional(node, "requestSlot");
            if (group == "Opponents") return EffectTargetSpec.Opponents(selection, slot);
            if (group == "Allies") return EffectTargetSpec.Allies(selection, slot);
            throw new ArgumentException("Primary effects support Self, Opponents and Allies, without event targets.");
        }
        private CombatTargetSpec ReadCombatTarget(BattleContentNode node, string defaultGroup = "Self")
        {
            RejectAttributes(node, new[] { "requestSlot" });
            string group = Text(node, "target", defaultGroup);
            var selection = Enum(node, "selection", CombatTargetSelection.FirstLiving);
            if (group == "Opponents") return CombatTargetSpec.Opponents(selection);
            if (group == "Allies") return CombatTargetSpec.Allies(selection);
            if (Optional(node, "selection") != null && selection != CombatTargetSelection.FirstLiving)
                throw new ArgumentException("Fixed event or self targets do not support target selection.");
            if (group == "Self") return CombatTargetSpec.Self();
            if (group == "EventSource") return CombatTargetSpec.EventSource();
            if (group == "EventTarget") return CombatTargetSpec.EventTarget();
            if (group == "EventActor") return CombatTargetSpec.EventActor();
            throw new ArgumentException("Unknown automatic combat target: " + group);
        }
        private void RejectAttributes(BattleContentNode node, IEnumerable<string> attributes)
        {
            foreach (string attribute in attributes)
                if (Optional(node, attribute) != null) AddError(node, "NotApplicable", "Field is unavailable for this configuration: " + attribute, attribute);
        }
        private T ReadRequiredEnum<T>(BattleContentNode node, string attribute) where T : struct
        { Text(node, attribute); return Enum(node, attribute, default(T)); }
        private Dictionary<string, T> ReadDefinitions<T>(BattleContentNode section, string childName, Func<BattleContentNode, T> read) where T : class
        {
            var result = new Dictionary<string, T>(StringComparer.Ordinal);
            if (section == null) return result;
            Check(section, new string[0], new[] { childName });
            foreach (var node in section.Children.Where(item => item.Name == childName))
            {
                var item = Capture(node, () =>
                {
                    string id = Text(node, "id");
                    if (result.ContainsKey(id)) throw new ArgumentException("Duplicate definition ID: " + id);
                    var value = read(node); result.Add(id, value); return value;
                });
                if (item == null) continue;
            }
            return result;
        }
    }
}
