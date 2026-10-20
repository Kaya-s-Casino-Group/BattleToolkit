using System;
using System.Linq;
namespace PokerEngine
{
    internal partial class BattleContentBuildContext
    {
        private EnemyCondition ReadEnemyCondition(BattleContentNode node, int depth = 0)
        {
            if (depth > 12) throw new ArgumentException("Enemy conditions are nested too deeply.");
            Check(node, new[] { "kind", "value", "statusRef", "enemyRefs" }, new[] { "When" });
            string status = Optional(node, "statusRef");
            if (status != null && !Statuses.ContainsKey(status)) throw new ArgumentException("Unknown condition status: " + status);
            var kind = Enum(node, "kind", EnemyConditionKind.Always);
            if (kind != EnemyConditionKind.HasStatus) RejectAttributes(node, new[] { "statusRef" });
            if (kind != EnemyConditionKind.HealthAtMost && kind != EnemyConditionKind.RoundAtLeast && kind != EnemyConditionKind.AlliesBelow && kind != EnemyConditionKind.UnitsAliveAtLeast && kind != EnemyConditionKind.UnitHealthAtMost && kind != EnemyConditionKind.RoundModulo && kind != EnemyConditionKind.HealthBelow && kind != EnemyConditionKind.UnitHealthBelow) RejectAttributes(node, new[] { "value" });
            return new EnemyCondition(kind, Decimal(node, "value", 0), status,
                node.Children.Select(c => ReadEnemyCondition(c, depth + 1)), RefList(node, "enemyRefs"));
        }
        private string[] RefList(BattleContentNode node, string key) => (Optional(node, key) ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        private EnemyCondition EnemyWhen(BattleContentNode node)
        {
            var when = node.Children.Where(c => c.Name == "When").ToArray();
            if (when.Length > 1) throw new ArgumentException("Use All/Any to combine conditions.");
            return when.Length == 0 ? null : ReadEnemyCondition(when[0]);
        }
        private System.Collections.Generic.IEnumerable<string> ConditionRefs(EnemyCondition c) => c.EnemyRefs.Concat(c.Children.SelectMany(ConditionRefs));
        private EnemyWorldEffect ReadRevive(BattleContentNode node)
        {
            Check(node, new[] { "enemyRefs", "count" }, Array.Empty<string>());
            var refs = RefList(node, "enemyRefs"); if (refs.Length == 0) throw new ArgumentException("Revive requires enemyRefs.");
            return EnemyWorldEffect.Revive(refs, Int(node, "count", 1));
        }
        private EnemyLinkRule ReadLink(BattleContentNode node)
        {
            Check(node, new[] { "id", "kind", "targets", "countRefs", "rate", "snapshot", "healTarget" }, new[] { "When" });
            return new EnemyLinkRule(Text(node, "id"), ReadRequiredEnum<EnemyLinkKind>(node, "kind"), RefList(node, "targets"),
                Decimal(node, "rate", 0), RefList(node, "countRefs"), Bool(node, "snapshot", false), EnemyWhen(node), Optional(node, "healTarget"));
        }
        private EnemyBehaviorDefinition ReadEnemyBehavior(BattleContentNode node)
        {
            Check(node, new[] { "seed", "intentPreviewTurns", "refreshOnHealthThreshold", "consumeFailedSummon", "recheckIntentOnAction" }, new[] { "Stage", "OpeningEffects", "Link" });
            var opening = node.Children.Where(c => c.Name == "OpeningEffects").ToArray();
            if (opening.Length > 1) throw new ArgumentException("Only one OpeningEffects is allowed.");
            var initial = opening.Length == 0 ? null : ReadEnemySequence(opening[0]);
            if (initial != null && initial.Steps.Any(s => s.Target.Group != EffectTargetGroup.Self || s.Effect.Kind != CombatEffectKind.ApplyStatus))
                throw new ArgumentException("OpeningEffects only applies statuses to Self.");
            var stages = node.Children.Where(c => c.Name == "Stage").Select(stage => {
                Check(stage, new[] { "id", "mode" }, new[] { "When", "Action", "OnEnter" });
                var actions = stage.Children.Where(c => c.Name == "Action").Select(action => {
                    Check(action, new[] { "id", "name", "weight", "cooldownRounds", "maxUses", "deadTarget" }, new[] { "When", "Effects", "Summon", "AddCards", "Revive" });
                    var effects = action.Children.Where(c => c.Name == "Effects").ToArray();
                    if (effects.Length > 1 || action.Children.SkipWhile(c => c.Name != "Summon" && c.Name != "AddCards" && c.Name != "Revive").Any(c => c.Name == "Effects"))
                        throw new ArgumentException("One combat Effects batch must precede world effects.");
                    var world = action.Children.Where(c => c.Name == "Summon" || c.Name == "AddCards" || c.Name == "Revive").Select(effect => {
                        if (effect.Name == "Revive") return ReadRevive(effect);
                        if (effect.Name == "Summon") {
                            Check(effect, new[] { "enemyRef", "count", "livingLimit", "limitScope" }, new string[0]);
                            return EnemyWorldEffect.Summon(Text(effect, "enemyRef"), Int(effect, "count", 1), Int(effect, "livingLimit", 4), Enum(effect, "limitScope", SummonLimitScope.Battlefield));
                        }
                        Check(effect, new[] { "face", "count", "zone", "position", "disabled", "effects" }, new string[0]);
                        return EnemyWorldEffect.AddCards(Text(effect, "face"), Int(effect, "count", 1), Text(effect, "zone", CardZoneKinds.Discard), Bool(effect, "disabled", false), Enum(effect, "effects", CardEffects.Ineffective | CardEffects.Unplayable), Enum(effect, "position", CardInsertionPosition.Default));
                    });
                    return new EnemyBehaviorAction(Text(action, "id"), Text(action, "name", Text(action, "id")), effects.Length == 0 ? null : ReadEnemySequence(effects[0]),
                        world, EnemyWhen(action), Int(action, "weight", 1), Int(action, "cooldownRounds", 0), Int(action, "maxUses", 0), Enum(action, "deadTarget", EnemyDeadTargetPolicy.Retarget));
                });
                return new EnemyBehaviorStage(Text(stage, "id"), Enum(stage, "mode", EnemySelectionMode.Sequence), actions, EnemyWhen(stage), stage.Children.Where(c => c.Name == "OnEnter").SelectMany(entry => {
                    Check(entry, Array.Empty<string>(), new[] { "Revive" }); return entry.Children.Select(ReadRevive); }));
            });
            return new EnemyBehaviorDefinition(stages, initial, Int(node, "seed", 1), Int(node, "intentPreviewTurns", 1), Bool(node, "refreshOnHealthThreshold", false), Bool(node, "consumeFailedSummon", true), node.Children.Where(c => c.Name == "Link").Select(ReadLink), Bool(node, "recheckIntentOnAction", false));
        }
    }
}
