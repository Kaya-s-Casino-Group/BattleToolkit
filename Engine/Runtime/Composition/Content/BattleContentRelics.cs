using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    internal partial class BattleContentBuildContext
    {
        internal Dictionary<string, BattleRelicContentDefinition> ReadRelics(BattleContentNode section)
        {
            return ReadDefinitions(section, "Relic", node =>
            {
                Check(node, new[] { "id", "name", "version", "charges", "maxCopies", "maxLevel", "slots", "sellPrice", "upgradeCost", "growthPerScoredRound" },
                    new[] { "Budget", "Numeric", "HandModifier", "RuleValue", "Refill", "FactTrigger", "BoundaryTrigger", "ActionTrigger" });
                var definition = new RelicDefinition(Text(node, "id"), Array.Empty<ScoreEffect>(),
                    Long(node, "sellPrice", 1), Long(node, "upgradeCost", 5), Int(node, "slots", 1), Int(node, "maxCopies", 1),
                    Int(node, "maxLevel", 1), Long(node, "growthPerScoredRound", 0), charges: OptionalInt(node, "charges"));
                var budgets = node.Children.Where(item => item.Name == "Budget").ToArray();
                if (budgets.Length > 1) throw new ArgumentException("A relic has one shared trigger budget.");
                var budget = budgets.Length == 0 ? null : Capture(budgets[0], () => ReadBudget(budgets[0]));
                var rules = new BattleRelicRules(Text(node, "version", ContentVersion),
                    ReadRelicEntries(node, "Numeric", ReadNumeric), ReadRelicEntries(node, "HandModifier", ReadHandModifier),
                    ReadRelicEntries(node, "RuleValue", ReadRuleValue), ReadRelicEntries(node, "Refill", ReadRefill),
                    ReadRelicEntries(node, "FactTrigger", ReadFactTrigger), budget,
                    ReadRelicEntries(node, "BoundaryTrigger", ReadBoundaryTrigger), ReadRelicEntries(node, "ActionTrigger", ReadActionTrigger));
                return new BattleRelicContentDefinition(definition, rules);
            });
        }
        private T[] ReadRelicEntries<T>(BattleContentNode node, string name, Func<BattleContentNode, T> read) where T : class =>
            node.Children.Where(item => item.Name == name).Select(item => Capture(item, () => read(item))).Where(item => item != null).ToArray();

        private RelicTriggerBudgetSpec ReadBudget(BattleContentNode node)
        {
            Check(node, new[] { "maxUsesPerAction", "maxUsesPerRound", "chargeCost", "cooldownRounds", "maxUsesPerBoundary" }, new string[0]);
            return new RelicTriggerBudgetSpec(OptionalInt(node, "maxUsesPerAction"), OptionalInt(node, "maxUsesPerRound"),
                Int(node, "chargeCost", 1), Int(node, "cooldownRounds", 0), OptionalInt(node, "maxUsesPerBoundary"));
        }
        private RelicNumericModifierSpec ReadNumeric(BattleContentNode node)
        {
            Check(node, new[] { "id", "stat", "operation", "value", "origins", "hand" }, new string[0]);
            Text(node, "value");
            return new RelicNumericModifierSpec(Text(node, "id"), ReadRequiredEnum<BattleNumericStat>(node, "stat"),
                ReadRequiredEnum<RelicNumericOperation>(node, "operation"), Decimal(node, "value", 0), ReadOrigins(node),
                Optional(node, "hand") == null ? (HandCategory?)null : ReadRequiredEnum<HandCategory>(node, "hand"));
        }
        private CombatEffectOrigin[] ReadOrigins(BattleContentNode node)
        {
            return Text(node, "origins", "Primary").Split(',').Select(value =>
            {
                CombatEffectOrigin parsed;
                // Numeric enum spellings would make content difficult to review and are not supported.
                string name = value.Trim();
                if (!System.Enum.GetNames(typeof(CombatEffectOrigin)).Contains(name) || !System.Enum.TryParse(name, out parsed))
                    throw new ArgumentException("Unknown effect origin: " + value);
                return parsed;
            }).ToArray();
        }
        private RelicHandEffectModifierSpec ReadHandModifier(BattleContentNode node)
        {
            Check(node, new[] { "id", "category", "operation", "stepId", "hits", "target", "selection", "requestSlot" }, new[] { "Effect" });
            string id = Text(node, "id"); var category = ReadRequiredEnum<HandCategory>(node, "category");
            var operation = ReadRequiredEnum<RelicHandEffectOperation>(node, "operation");
            var effects = node.Children.Where(item => item.Name == "Effect").ToArray();
            bool hasEffect = operation == RelicHandEffectOperation.AppendEffect || operation == RelicHandEffectOperation.ReplaceEffect;
            if (effects.Length != (hasEffect ? 1 : 0)) throw new ArgumentException("This hand modifier has an invalid number of effect payloads.");
            RejectAttributes(node, operation == RelicHandEffectOperation.ChangeTarget ? new[] { "hits" } : TargetAttributes);
            if (operation != RelicHandEffectOperation.AddHits) RejectAttributes(node, new[] { "hits" });
            switch (operation)
            {
                case RelicHandEffectOperation.AppendEffect:
                    RejectAttributes(node, new[] { "stepId" });
                    return RelicHandEffectModifierSpec.Append(id, category, ReadConstantEffect(effects[0], true, false), ReadEffectTarget(effects[0]));
                case RelicHandEffectOperation.ReplaceEffect:
                    return RelicHandEffectModifierSpec.Replace(id, category, Text(node, "stepId"), ReadConstantEffect(effects[0], false, false));
                case RelicHandEffectOperation.AddHits:
                    return RelicHandEffectModifierSpec.AddHits(id, category, Text(node, "stepId"), Int(node, "hits", 1));
                case RelicHandEffectOperation.ChangeTarget:
                    return RelicHandEffectModifierSpec.Retarget(id, category, Text(node, "stepId"), ReadEffectTarget(node));
                default: throw new ArgumentException("Unknown hand modifier operation.");
            }
        }
        private RelicRuleValueSpec ReadRuleValue(BattleContentNode node)
        {
            Check(node, new[] { "id", "key", "delta" }, new string[0]);
            Text(node, "delta");
            return new RelicRuleValueSpec(Text(node, "id"), ReadRequiredEnum<BattleRuleValueKey>(node, "key"), Int(node, "delta", 0));
        }
        private RelicRefillRuleSpec ReadRefill(BattleContentNode node)
        {
            Check(node, new[] { "id", "timing", "mode", "fixedCount", "delta" }, new string[0]);
            return new RelicRefillRuleSpec(Text(node, "id"), ReadRequiredEnum<CardDuelDealTrigger>(node, "timing"),
                Optional(node, "mode") == null ? (BattleRefillMode?)null : ReadRequiredEnum<BattleRefillMode>(node, "mode"),
                OptionalInt(node, "fixedCount"), Int(node, "delta", 0));
        }
        private RelicTriggerSpec ReadFactTrigger(BattleContentNode node)
        {
            Check(node, new[] { "id", "timing", "relation", "occurrence", "origins", "priority", "chain", "sourcePolicy" }, new[] { "Conditions", "Effect" });
            var trigger = new RelicTriggerSpec(Text(node, "id"), ReadRequiredEnum<RelicTriggerTiming>(node, "timing"),
                Enum(node, "relation", RelicEventRelation.OwnerIsSource), ReadTriggeredEffects(node), ReadConditions(node),
                Enum(node, "occurrence", RelicTriggerOccurrence.PerEvent), ReadOrigins(node), Int(node, "priority", 0), Bool(node, "chain", false),
                Enum(node, "sourcePolicy", CombatReactionSourcePolicy.RequireLiving));
            foreach (var condition in trigger.Conditions)
            {
                if (condition.Kind == RelicTriggerConditionKind.FactAtLeast)
                {
                    bool damage = trigger.Timing == RelicTriggerTiming.DamageSettled || trigger.Timing == RelicTriggerTiming.UnitDied;
                    bool available = damage && (condition.Fact == RelicFactAmount.DamageToHealth || condition.Fact == RelicFactAmount.BlockAbsorbed) ||
                        trigger.Timing == RelicTriggerTiming.HealingSettled && (condition.Fact == RelicFactAmount.HealthRestored || condition.Fact == RelicFactAmount.Overheal) ||
                        trigger.Timing == RelicTriggerTiming.BlockGained && condition.Fact == RelicFactAmount.BlockGained;
                    if (!available) throw new ArgumentException("This fact timing does not provide the requested amount: " + condition.Fact);
                }
                if (condition.Kind == RelicTriggerConditionKind.EventStatus &&
                    trigger.Timing != RelicTriggerTiming.StatusApplied && trigger.Timing != RelicTriggerTiming.StatusBlocked)
                    throw new ArgumentException("EventStatus requires a status application fact.");
                bool actionFact = condition.Kind == RelicTriggerConditionKind.HandCategory || condition.Kind == RelicTriggerConditionKind.SubmittedCardsAtLeast ||
                    condition.Kind == RelicTriggerConditionKind.DiscardedCardsAtLeast;
                if (actionFact && (!trigger.Origins.Contains(CombatEffectOrigin.Primary) || trigger.Occurrence == RelicTriggerOccurrence.PerBoundary))
                    throw new ArgumentException("Hand and discard facts require a primary action event.");
            }
            bool handCondition = trigger.Conditions.Any(item => item.Kind == RelicTriggerConditionKind.HandCategory || item.Kind == RelicTriggerConditionKind.SubmittedCardsAtLeast);
            if (handCondition && trigger.Conditions.Any(item => item.Kind == RelicTriggerConditionKind.DiscardedCardsAtLeast))
                throw new ArgumentException("One primary operation cannot be both play and discard.");
            return trigger;
        }
        private RelicBoundaryTriggerSpec ReadBoundaryTrigger(BattleContentNode node)
        {
            Check(node, new[] { "id", "timing", "phase", "priority", "chain", "sourcePolicy" }, new[] { "Conditions", "Effect" });
            return new RelicBoundaryTriggerSpec(Text(node, "id"), ReadRequiredEnum<RelicBoundaryTiming>(node, "timing"), ReadTriggeredEffects(node),
                Optional(node, "phase") == null ? (RelicPhaseRelation?)null : ReadRequiredEnum<RelicPhaseRelation>(node, "phase"), ReadConditions(node),
                Int(node, "priority", 0), Bool(node, "chain", false), Enum(node, "sourcePolicy", CombatReactionSourcePolicy.RequireLiving));
        }
        private RelicActionTriggerSpec ReadActionTrigger(BattleContentNode node)
        {
            Check(node, new[] { "id", "timing", "relation", "priority", "chain", "sourcePolicy" }, new[] { "Conditions", "Effect" });
            return new RelicActionTriggerSpec(Text(node, "id"), ReadRequiredEnum<RelicActionTiming>(node, "timing"), ReadTriggeredEffects(node),
                Enum(node, "relation", RelicActionRelation.OwnerIsActor), ReadConditions(node), Int(node, "priority", 0), Bool(node, "chain", false),
                Enum(node, "sourcePolicy", CombatReactionSourcePolicy.RequireLiving));
        }
        private RelicTriggeredEffectSpec[] ReadTriggeredEffects(BattleContentNode node) =>
            node.Children.Where(item => item.Name == "Effect").Select(item => Capture(item, () =>
                new RelicTriggeredEffectSpec(ReadConstantEffect(item, true, false), ReadCombatTarget(item)))).Where(item => item != null).ToArray();

        private RelicTriggerConditionSpec[] ReadConditions(BattleContentNode node)
        {
            var groups = node.Children.Where(item => item.Name == "Conditions").ToArray();
            if (groups.Length > 1) throw new ArgumentException("A trigger accepts one AND condition list.");
            if (groups.Length == 0) return Array.Empty<RelicTriggerConditionSpec>();
            Check(groups[0], new string[0], new[] { "FactAtLeast", "OwnerHealthAtMost", "OwnerHasStatus", "EventStatus", "HandIs", "SubmittedCardsAtLeast", "DiscardedCardsAtLeast" });
            return groups[0].Children.Select(item => Capture(item, () => ReadCondition(item))).Where(item => item != null).ToArray();
        }
        private RelicTriggerConditionSpec ReadCondition(BattleContentNode node)
        {
            switch (node.Name)
            {
                case "FactAtLeast":
                    Check(node, new[] { "fact", "amount" }, new string[0]);
                    Text(node, "amount");
                    return RelicTriggerConditionSpec.FactAtLeast(ReadRequiredEnum<RelicFactAmount>(node, "fact"), Long(node, "amount", 0));
                case "OwnerHealthAtMost":
                    Check(node, new[] { "ratio" }, new string[0]);
                    Text(node, "ratio");
                    return RelicTriggerConditionSpec.OwnerHealthAtMost(Decimal(node, "ratio", 1));
                case "OwnerHasStatus":
                case "EventStatus":
                    Check(node, new[] { "statusRef" }, new string[0]);
                    var id = new StatusDefinitionId(Text(node, "statusRef"));
                    if (!Statuses.ContainsKey(id.Value)) throw new ArgumentException("Unknown status reference: " + id.Value);
                    return node.Name == "OwnerHasStatus" ? RelicTriggerConditionSpec.OwnerHasStatus(id) : RelicTriggerConditionSpec.EventStatus(id);
                case "HandIs":
                    Check(node, new[] { "category" }, new string[0]);
                    return RelicTriggerConditionSpec.HandIs(ReadRequiredEnum<HandCategory>(node, "category"));
                case "SubmittedCardsAtLeast":
                    Check(node, new[] { "count" }, new string[0]);
                    return RelicTriggerConditionSpec.SubmittedCardsAtLeast(Int(node, "count", 1));
                case "DiscardedCardsAtLeast":
                    Check(node, new[] { "count" }, new string[0]);
                    return RelicTriggerConditionSpec.DiscardedCardsAtLeast(Int(node, "count", 1));
                default: throw new ArgumentException("Unknown trigger condition: " + node.Name);
            }
        }
    }
}
