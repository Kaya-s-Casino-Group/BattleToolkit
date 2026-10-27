using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    internal partial class BattleContentBuildContext
    {
        private static readonly string[] EffectAttributes = { "id", "kind", "amount", "amountKind", "perSubmittedCard", "retargetChain", "target", "selection", "requestSlot", "statusRef", "stacks", "stacksPerSubmittedCard", "lifetime", "count", "phase" };
        private static readonly string[] TargetAttributes = { "target", "selection", "requestSlot", "retargetChain" };

        internal Dictionary<string, CombatStatusDefinition> ReadStatuses(BattleContentNode section)
        {
            return ReadDefinitions(section, "Status", node =>
            {
                Check(node, new[] { "id", "name", "tags", "lifetime", "count", "phase", "reapply" },
                    new[] { "BlockActionWindow", "PreventStatusApplication", "ReflectBlockedDamage", "LifeSteal", "OverhealDamage", "PeriodicDamage", "HitArmor", "DamageImmunity", "IncomingAttackDamagePerStack", "StackDecay" });
                var behaviors = node.Children.Select(child => Capture(child, () => ReadStatusBehavior(child))).Where(item => item != null).ToArray();
                return new CombatStatusDefinition(new StatusDefinitionId(Text(node, "id")), Enum(node, "tags", StatusTags.None), behaviors,
                    ReadLifetime(node, true), Enum(node, "reapply", StatusReapplyPolicy.KeepLonger));
            });
        }

        private StatusBehaviorSpec ReadStatusBehavior(BattleContentNode node)
        {
            switch (node.Name)
            {
                case "DamageImmunity": Check(node, Array.Empty<string>(), Array.Empty<string>()); return new DamageImmunitySpec();
                case "IncomingAttackDamagePerStack":
                    Check(node, new[] { "rate" }, Array.Empty<string>());
                    return new IncomingAttackDamagePerStackSpec(Decimal(node, "rate", 0.5m));
                case "StackDecay":
                    Check(node, new[] { "timing", "timingSubject", "stacks" }, Array.Empty<string>());
                    return new StackDecaySpec(Enum(node, "timing", CombatTimingKind.ActionCompleted), Enum(node, "timingSubject", PeriodicTimingSubject.Applier), Int(node, "stacks", 1));
                case "HitArmor":
                    Check(node, new[] { "multiplier", "reduction", "consumeStacks", "primaryOnly" }, new string[0]);
                    return new HitArmorSpec(Decimal(node, "multiplier", 0.5m), Long(node, "reduction", 0), Int(node, "consumeStacks", 1), Bool(node, "primaryOnly", true));
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
                    Check(node, new[] { "timing", "timingSubject", "amount", "target", "selection", "stackDecay" }, new string[0]);
                    Text(node, "timing"); Text(node, "amount");
                    return new PeriodicDamageSpec(Enum(node, "timing", CombatTimingKind.ActionCompleted), Long(node, "amount", 0),
                        ReadCombatTarget(node, "Self"), Int(node, "stackDecay", 0), Enum(node, "timingSubject", PeriodicTimingSubject.Holder));
                default: throw new ArgumentException("Unsupported status behavior: " + node.Name);
            }
        }

        internal Dictionary<string, ConfiguredHandEffectRule> ReadHandRules(BattleContentNode section)
        {
            return ReadDefinitions(section, "HandRule", node =>
            {
                Check(node, new[] { "id", "name", "version", "flushUsesRankEffects", "combineCompatibleEffects", "flushAsAttribute", "fullHouseComponentDamage", "modernScoring", "flushDamageMultiplier", "wheelAceScoresOne", "pureFlushScoresAllCards", "fullHouseTripleBaseMultiplier", "exclusiveHandEffects" }, new[] { "Hand", "FlushBonus" });
                var entries = node.Children.Where(item => item.Name == "Hand").Select(hand => Capture(hand, () =>
                {
                    Check(hand, new[] { "category", "variant" }, new[] { "Effect" });
                    var steps = hand.Children.Where(item => item.Name == "Effect").Select(effect => Capture(effect, () => ReadConfiguredStep(effect, true))).Where(item => item != null).ToArray();
                    return new HandEffectEntry(ReadRequiredEnum<HandCategory>(hand, "category"), steps, Enum(hand, "variant", HandEffectVariant.Any));
                })).Where(item => item != null).ToArray();
                var bonus = node.Children.Where(c => c.Name == "FlushBonus").ToArray();
                if (bonus.Length > 1) throw new ArgumentException("Only one FlushBonus is supported.");
                if (bonus.Length == 1) Check(bonus[0], Array.Empty<string>(), new[] { "Effect" });
                return new ConfiguredHandEffectRule(Text(node, "version", ContentVersion), entries,
                    bonus.SelectMany(b => b.Children).Select(e => ReadConfiguredStep(e, true, true)), Bool(node, "flushUsesRankEffects", false), Bool(node, "combineCompatibleEffects", false), Bool(node, "flushAsAttribute", false), Bool(node, "fullHouseComponentDamage", false), Bool(node, "modernScoring", false), Decimal(node, "flushDamageMultiplier", 2m),
                    Bool(node, "wheelAceScoresOne", false), Bool(node, "pureFlushScoresAllCards", false),
                    Optional(node, "fullHouseTripleBaseMultiplier") == null ? (decimal?)null : Decimal(node, "fullHouseTripleBaseMultiplier", 0), Bool(node, "exclusiveHandEffects", false));
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

        private ConfiguredEffectStep ReadConfiguredStep(BattleContentNode node, bool allowScore, bool flushBonus = false)
        {
            Check(node, EffectAttributes, new[] { "OnHitStatus" });
            var kind = ReadEffectKind(node);
            bool perCard = Bool(node, "perSubmittedCard", false);
            if (perCard && !allowScore) throw new ArgumentException("Per-card repetition is only available to hand effects.");
            var amountKind = Enum(node, "amountKind", EffectAmountKind.Constant);
            if (amountKind == EffectAmountKind.EnemyAttackScale && allowScore)
                throw new ArgumentException("Enemy attack amounts are only available to enemy damage effects.");
            if (amountKind == EffectAmountKind.FlushBaseTotalScale && !flushBonus)
                throw new ArgumentException("Flush base amounts are only available to FlushBonus effects.");
            if (flushBonus && (amountKind == EffectAmountKind.ThreeOfAKindTotalScale || amountKind == EffectAmountKind.OnePairTotalScale))
                throw new ArgumentException("Flush bonus cannot assume a full-house component score.");
            var onHit = node.Children.Where(c => c.Name == "OnHitStatus").ToArray();
            if (onHit.Length > 1 || onHit.Length > 0 && kind != CombatEffectKind.Damage) throw new ArgumentException("One OnHitStatus is allowed on a damage effect.");
            if (onHit.Length > 0) Check(onHit[0], new[] { "statusRef", "stacks", "stacksKind", "stacksPerSubmittedCard", "lifetime", "count", "phase" }, Array.Empty<string>());
            return new ConfiguredEffectStep(Text(node, "id"), kind, ReadEffectTarget(node),
                IsNumeric(kind) ? ReadAmount(node, allowScore) : null, kind == CombatEffectKind.ApplyStatus ? ReadStatusApplication(node, allowScore) : null,
                onHit.Length == 0 ? null : ReadStatusApplication(onHit[0], allowScore), perCard,
                onHit.Length == 0 ? OnHitStacksKind.Constant : Enum(onHit[0], "stacksKind", OnHitStacksKind.Constant));
        }

        private BattleEffectSpec ReadConstantEffect(BattleContentNode node, bool allowTarget = true, bool allowId = true, bool allowHandCount = false, bool allowSubmittedCards = false)
        {
            Check(node, EffectAttributes.Where(item => item != "perSubmittedCard" && (allowTarget || !TargetAttributes.Contains(item)) && (allowId || item != "id")).Concat(allowHandCount ? new[] { "amountPerHandCard" } : Array.Empty<string>()).ToArray(), new string[0]);
            var kind = ReadEffectKind(node);
            long amount = IsNumeric(kind) ? ReadAmount(node, false).Resolve() : 0;
            switch (kind)
            {
                case CombatEffectKind.Damage: return BattleEffectSpec.Damage(amount);
                case CombatEffectKind.Heal: return BattleEffectSpec.Heal(amount);
                case CombatEffectKind.GainBlock: return BattleEffectSpec.GainBlock(amount);
                case CombatEffectKind.DrawCards: return BattleEffectSpec.DrawCards(amount);
                case CombatEffectKind.ApplyStatus: return BattleEffectSpec.ApplyStatus(ReadStatusApplication(node, allowSubmittedCards));
                case CombatEffectKind.Cleanse: return BattleEffectSpec.Cleanse();
                default: return BattleEffectSpec.Eliminate();
            }
        }
        private CombatEffectKind ReadEffectKind(BattleContentNode node)
        {
            var kind = ReadRequiredEnum<CombatEffectKind>(node, "kind");
            if (!IsNumeric(kind) && kind != CombatEffectKind.ApplyStatus && kind != CombatEffectKind.Eliminate && kind != CombatEffectKind.Cleanse)
                throw new ArgumentException("Only Damage, Heal, GainBlock, ApplyStatus, Cleanse and Eliminate are content effects.");
            RejectAttributes(node, IsNumeric(kind) ? new[] { "statusRef", "stacks", "stacksPerSubmittedCard", "lifetime", "count", "phase" } :
                kind == CombatEffectKind.ApplyStatus ? new[] { "amount", "amountKind" } :
                new[] { "amount", "amountKind", "statusRef", "stacks", "stacksPerSubmittedCard", "lifetime", "count", "phase" });
            return kind;
        }
        private static bool IsNumeric(CombatEffectKind kind) => kind == CombatEffectKind.Damage || kind == CombatEffectKind.Heal || kind == CombatEffectKind.GainBlock || kind == CombatEffectKind.DrawCards;
        private EffectAmountSpec ReadAmount(BattleContentNode node, bool allowScore)
        {
            Text(node, "amount");
            var kind = Enum(node, "amountKind", EffectAmountKind.Constant);
            if (!allowScore && kind != EffectAmountKind.Constant && kind != EffectAmountKind.EnemyAttackScale) throw new ArgumentException("Score amount sources are only available to hand effects.");
            return new EffectAmountSpec(kind, Decimal(node, "amount", 0));
        }
        private StatusApplicationSpec ReadStatusApplication(BattleContentNode node, bool allowSubmittedCards = false)
        {
            var id = new StatusDefinitionId(Text(node, "statusRef"));
            if (!Statuses.ContainsKey(id.Value)) throw new ArgumentException("Unknown status reference: " + id.Value);
            var lifetime = ReadLifetime(node, false);
            int stacks = Int(node, "stacks", 1), perCard = Int(node, "stacksPerSubmittedCard", 0);
            if (perCard != 0 && !allowSubmittedCards) throw new ArgumentException("Submitted-card stacks require a hand effect or hand fact trigger.");
            var application = lifetime == null ? new StatusApplicationSpec(id, stacks, perCard) : StatusApplicationSpec.WithLifetime(id, lifetime, stacks, perCard);
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
                RejectAttributes(node, new[] { "requestSlot", "retargetChain" });
                if (Optional(node, "selection") != null && Text(node, "selection") != "AllLiving")
                    throw new ArgumentException("Self does not support target selection.");
                return EffectTargetSpec.Self();
            }
            var selection = Enum(node, "selection", EffectTargetSelection.FirstLiving);
            string slot = Optional(node, "requestSlot");
            if (group == "Opponents")
            {
                var target = EffectTargetSpec.Opponents(selection, slot);
                return Optional(node, "retargetChain") == null ? target : target.WithRetargetChain(Text(node, "retargetChain"));
            }
            if (group == "Allies") { RejectAttributes(node, new[] { "retargetChain" }); return EffectTargetSpec.Allies(selection, slot); }
            throw new ArgumentException("Primary effects support Self, Opponents and Allies, without event targets.");
        }
        private CombatTargetSpec ReadCombatTarget(BattleContentNode node, string defaultGroup = "Self")
        {
            RejectAttributes(node, new[] { "requestSlot", "retargetChain" });
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
