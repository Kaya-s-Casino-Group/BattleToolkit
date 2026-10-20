using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PokerEngine;

internal static partial class PlaytestBridge
{
    // Display adapters only: amounts, targets and lifetimes below are settled engine facts.
    private static object Snapshot(Entry entry)
    {
        var state = entry.Session.State; var duel = state.Duel;
        var catalog = entry.Session.Engine.Duel.Definition.StatusCatalog;
        return D("sessionId", entry.SessionId, "runId", entry.RunId, "revision", Number(state.Revision),
            "seed", entry.Seed, "encounterId", entry.EncounterId, "fingerprint", entry.Fingerprint, "xmlHash", entry.XmlHash,
            "phase", duel.Phase.ToString(), "round", Math.Min(duel.CompletedRounds + 1, entry.Session.Engine.Duel.Definition.RoundLimit),
            "completedRounds", duel.CompletedRounds, "finished", duel.IsFinished,
            "outcome", duel.Phase == ScoredDuelPhase.Won ? "胜利" : duel.Phase == ScoredDuelPhase.Lost ? "失败" : "进行中",
            "actors", duel.Players.Where(player => duel.HasPlayerWindow(player.Id)).Select(player => player.Id.Value).ToArray(),
            "players", duel.Players.Select(player => {
                var unit = Unit(entry, duel, player.Id, catalog, "Players");
                unit["energy"] = player.Energy.Energy; unit["maxEnergy"] = player.Energy.MaxEnergy;
                unit["handLimit"] = player.Limits.HandLimit.Value; unit["discardsRemaining"] = state.RemainingDiscardBudgetFor(player.Id);
                unit["manualDraw"] = player.Rules.Flow.ManualDraw;
                unit["turnFlow"] = player.Rules.Flow.UsesTurnFlow;
                unit["autoDrawToLimit"] = player.Rules.Flow.AutoDrawToLimit;
                unit["window"] = state.FlowFor(player.Id).Window.ToString();
                unit["drawLimit"] = entry.Session.Engine.DrawLimit(state, player.Id);
                unit["drawWarning"] = entry.Session.Engine.RequiresDrawWarning(state, player.Id);
                unit["requiredDiscards"] = entry.Session.Engine.RequiredDiscards(state, player.Id);
                unit["actionsRemaining"] = state.FlowFor(player.Id).ActionsRemaining;
                unit["drawUses"] = state.FlowFor(player.Id).DrawUsesRemaining;
                unit["maxPlayCards"] = entry.Session.Engine.PlayLimit(state, player.Id, false); unit["maxDiscardCards"] = player.Rules.MaxDiscardCards;
                unit["canAct"] = duel.HasPlayerWindow(player.Id); unit["hasEnded"] = player.HasEnded;
                unit["costs"] = D("play", entry.Session.Engine.Duel.QueryActionCost(duel, player.Id, ScoredDuelActionKind.ScoredHand).Cost,
                    "discard", entry.Session.Engine.Duel.QueryActionCost(duel, player.Id, ScoredDuelActionKind.Discard).Cost);
                unit["relics"] = player.Relics.Relics.Select(relic => D("id", relic.InstanceId, "name", relic.DefinitionId,
                    "definitionId", relic.DefinitionId, "displayName", ContentName(entry, "Relics", relic.DefinitionId),
                    "description", RelicDescription(ContentNode(entry, "Relics", relic.DefinitionId)),
                    "level", relic.Level, "charges", relic.RemainingCharges, "cooldown", relic.CooldownRounds, "usesThisRound", relic.UsesThisRound,
                    "activation", (player.Relics.RulesFor(relic.InstanceId).Item?.Activation ?? BattleItemActivation.None).ToString(),
                    "canUse", BattleItemRuntime.CanUse(state, player.Id, relic.InstanceId),
                    "cooldownBattles", Number(relic.Memory.CounterValue(BattleItemRuntime.CooldownBattlesKey)),
                    "rouletteActive", relic.Memory.CounterValue(BattleItemRuntime.RouletteActiveKey) != 0,
                    "rouletteTurn", Number(relic.Memory.CounterValue(BattleItemRuntime.RouletteTurnKey)))).ToArray();
                unit["hand"] = state.Zone(CardZoneKinds.Hand, player.Id).Cards.Select(id => {
                    var card = state.Table.GetCard(id); var face = entry.Session.Engine.Face(card);
                    return D("id", id.Value, "rank", face.Rank.ToString(), "suit", face.Suit.ToString(),
                        "label", SuitLabel(face.Suit) + RankLabel(face.Rank), "disabled", card.IsDisabled, "canPlay", card.CanPlay, "canDiscard", card.CanDiscard, "effects", card.Effects.ToString());
                }).ToArray();
                unit["deckCount"] = state.Zone(CardZoneKinds.Deck, player.Id).Cards.Count;
                unit["discardCount"] = state.Zone(CardZoneKinds.Discard, player.Id).Cards.Count;
                unit["piles"] = D("deck", Pile(entry, state, player.Id, CardZoneKinds.Deck),
                    "discard", Pile(entry, state, player.Id, CardZoneKinds.Discard));
                return unit;
            }).ToArray(),
            "enemies", duel.Enemies.Select(enemy => {
                var unit = Unit(entry, duel, enemy.Id, catalog, "Enemies");
                var intent = entry.Session.Engine.Duel.PreviewEnemyAction(duel, enemy.Id);
                var planned = enemy.BehaviorState.Intent;
                var active = intent != null && intent.Accepted && !intent.IsSkipped;
                var hits = active ? intent.Hits.Where(hit => hit.Effect.Source == enemy.Id &&
                    (hit.Provenance == null || hit.Provenance.Origin == CombatEffectOrigin.Primary)).ToArray() : Array.Empty<CombatEvent>();
                unit["intentSchedule"] = entry.Session.Engine.Duel.PreviewEnemyIntents(duel, enemy.Id).Select((action, offset) => D("round", duel.CompletedRounds + offset + 1, "actionId", action?.Id, "name", action?.Name ?? "等待")).ToArray();
                unit["intentMeta"] = D("actionId", planned?.Id, "name", planned?.Name ?? (active ? "行动" : "等待"),
                    "stageId", enemy.BehaviorState.StageId, "available", intent?.Accepted ?? false, "skipped", intent?.IsSkipped ?? false,
                    "damage", Number(hits.Sum(hit => hit.SettledDamage)), "hitCount", hits.Length,
                    "worldEffects", (active ? planned?.WorldEffects ?? Array.Empty<EnemyWorldEffect>() : Array.Empty<EnemyWorldEffect>())
                        .Select(effect => D("kind", effect.IsRevive ? "Revive" : effect.EnemyRef != null ? "Summon" : "AddCards", "count", effect.Count,
                            "reviveRefs",effect.ReviveRefs,"enemyRef", effect.EnemyRef, "displayName", effect.EnemyRef == null ? null : ContentName(entry, "Enemies", effect.EnemyRef), "zone", effect.Zone, "position", effect.EnemyRef == null ? effect.Position.ToString() : null)).ToArray());
                unit["intent"] = intent == null ? new string[0] : intent.Accepted ? EventLines(intent.RuleEvents, duel, catalog, entry).ToArray() : new[] { "意图暂不可预览：" + Error(intent.Error) };
                if (enemy.Behavior != null && intent != null && intent.Accepted && !intent.IsSkipped)
                {
                    unit["intent"] = new[] { planned?.Name ?? "等待" }.Concat((string[])unit["intent"])
                        .Concat((planned?.WorldEffects ?? Array.Empty<EnemyWorldEffect>()).Select(e => e.IsRevive ? "按顺序复活 " + e.Count + " 个阵亡单位" : e.EnemyRef != null ? "召唤 " + e.EnemyRef + " × " + e.Count : "塞入卡牌 × " + e.Count + " → " + InsertionLabel(e.Zone, e.Position))).ToArray();
                }
                return unit;
            }).ToArray());
    }

    private static object Pile(Entry entry, CardDuelState state, ParticipantId owner, string zone)
    {
        var cards = state.Zone(zone, owner).Cards.Select(id => state.Table.GetCard(id)).ToArray();
        // Summarize composition without exposing instance IDs or the shuffled draw order.
        var groups = cards.GroupBy(card => {
            var face = entry.Session.Engine.Face(card);
            return new { face.Rank, face.Suit, card.Effects, card.IsDisabled, card.CanPlay, card.CanDiscard };
        }).OrderBy(group => group.Key.Rank).ThenBy(group => group.Key.Suit).ThenBy(group => group.Key.Effects)
            .ThenBy(group => group.Key.IsDisabled).ThenBy(group => group.Key.CanPlay).ThenBy(group => group.Key.CanDiscard);
        return D("count", cards.Length, "unplayableCount", cards.Count(card => !card.CanPlay),
            "cards", groups.Select(group => D("label", SuitLabel(group.Key.Suit) + RankLabel(group.Key.Rank),
                "rank", group.Key.Rank.ToString(), "suit", group.Key.Suit.ToString(),
                "disabled", group.Key.IsDisabled, "canPlay", group.Key.CanPlay, "canDiscard", group.Key.CanDiscard,
                "effects", group.Key.Effects.ToString(), "count", group.Count())).ToArray());
    }

    private static Dictionary<string, object> Unit(Entry entry, TeamBattleState state, ParticipantId id, CombatStatusCatalog catalog, string section)
    {
        var unit = state.Combat.Get(id);
        return D("id", id.Value, "name", Name(state, id), "health", Number(unit.Health), "maxHealth", Number(unit.MaxHealth),
            "definitionId", unit.DefinitionId, "displayName", ContentName(entry, section, unit.DefinitionId),
            "block", Number(unit.Block), "alive", unit.IsAlive,
            "statuses", state.Combat.Statuses.Where(status => status.Target.Kind == EntityKind.Participant && status.Target.Id == id.Value)
                .Select(status => {
                    var definition = catalog?.Find(status.DefinitionId);
                    var tags = definition?.Tags ?? StatusTags.None;
                    return D("id", status.Id.Value, "definitionId", status.DefinitionId.Value, "name", StatusName(status.DefinitionId, catalog, entry),
                        "displayName", ContentName(entry, "Statuses", status.DefinitionId.Value),
                        "tags", Enum.GetValues(typeof(StatusTags)).Cast<StatusTags>().Where(tag => tag != StatusTags.None && (tags & tag) == tag).Select(tag => tag.ToString()).ToArray(),
                        "behaviorKeys", (definition?.Behaviors ?? Array.Empty<StatusBehaviorSpec>()).Select(behavior => behavior.GetType().Name.Replace("Spec", "")).ToArray(),
                        "isNegative", (tags & StatusTags.Negative) != 0 || (definition?.Behaviors.Any(behavior => behavior is BlockActionWindowSpec) ?? false),
                        "stacks", status.Stacks, "duration", Duration(status.Duration));
                }).ToArray());
    }

    private static BattleContentNode ContentNode(Entry entry, string section, string id) => entry.ContentSource?.Root.Children
        .FirstOrDefault(node => node.Name == section)?.Children.FirstOrDefault(node => Attribute(node, "id") == id);
    private static string Attribute(BattleContentNode node, string key, string fallback = "") =>
        node != null && node.Attributes.TryGetValue(key, out var value) ? value : fallback;
    private static string ContentName(Entry entry, string section, string id) => Attribute(ContentNode(entry, section, id), "name", id);
    private static string RelicDescription(BattleContentNode node)
    {
        if (node == null) return "";
        var labels = new Dictionary<string, string> {
            { "Item", "道具能力" }, { "Program", "条件机制" }, { "Score", "计分修正" }, { "Numeric", "战斗数值修正" }, { "HandModifier", "牌型效果" }, { "RuleValue", "行动规则修正" },
            { "Refill", "补牌效果" }, { "FactTrigger", "战斗事件触发" }, { "BoundaryTrigger", "阶段触发" }, { "ActionTrigger", "行动触发" }
        };
        return string.Join("；", node.Children.Where(child => labels.ContainsKey(child.Name)).Select(child => {
            var effects = Descendants(child).Where(effect => effect.Name == "Effect").Select(effect => EffectLabel(Attribute(effect, "kind"))).Distinct().ToArray();
            return labels[child.Name] + (effects.Length == 0 ? "" : "：" + string.Join("、", effects));
        }).Distinct());
    }
    private static IEnumerable<BattleContentNode> Descendants(BattleContentNode node) => node.Children.SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static string EffectLabel(string kind)
    {
        switch (kind)
        {
            case "Damage": return "伤害";
            case "Heal": return "治疗";
            case "GainBlock": return "护盾";
            case "ApplyStatus": return "施加状态";
            case "DrawCards": return "摸牌";
            case "Cleanse": return "净化";
            case "Eliminate": return "消灭";
            default: return kind;
        }
    }

    private static object Hand(ScoreResult score, HandAttackInfo facts = null)
    {
        var mechanisms = facts?.CompatibleCategories ?? new[] { score.Category };
        return D("category", score.Category.ToString(), "label", HandName(score.Category),
            "scoringCategory", score.Category.ToString(), "scoringLabel", HandName(score.Category),
            "mechanisms", mechanisms.Select(category => D("category", category.ToString(), "label", HandName(category))).ToArray(),
            "mechanismLabel", string.Join("、", mechanisms.Select(HandName)),
            "components",score.Components.Select(c=>D("label",HandName(c.Category),"points",Number(c.Points),"multiplier",c.Multiplier.ToString(CultureInfo.InvariantCulture),"total",Number(c.Total))).ToArray(),
            "points", Number(score.Points), "kickerPoints", Number(score.UnmultipliedPoints),
            "multiplier", score.Multiplier.ToString(CultureInfo.InvariantCulture), "total", Number(score.Total));
    }

    private static object[] Steps(BattleSessionResult result, CombatStatusCatalog catalog, Entry entry) => result.Steps.Select(step => {
        var transition = step.Transition; var lines = new List<string>();
        if (transition.Score != null)
        {
            if(transition.Score.Components.Count>0) lines.Add("分项计伤："+string.Join(" + ",transition.Score.Components.Select(c=>HandName(c.Category)+" "+Number(c.Points)+" × "+c.Multiplier.ToString(CultureInfo.InvariantCulture)+" = "+Number(c.Total))));
            else lines.Add("计伤牌型：" + HandName(transition.Score.Category) + " · 点数 " + Number(transition.Score.Points) +
                " × 倍率 " + transition.Score.Multiplier.ToString(CultureInfo.InvariantCulture) + " = 牌型得分 " + Number(transition.Score.Total));
            var facts = transition.ActionCompletion?.Context.Hand;
            if (facts != null) lines.Add("兼容牌型：" + string.Join("、", facts.CompatibleCategories.Select(HandName)));
        }
        if (transition.ActionCost != null) lines.Add("消耗能量 " + transition.ActionCost.Cost);
        lines.AddRange(EventLines(transition.RuleEvents, step.Before.Duel, catalog, entry));
        lines.AddRange(WorldEffectLines(transition.Timeline, entry));
        foreach (var activation in transition.RelicActivations)
            lines.Add("遗物触发：" + activation.Before.DefinitionId + " / " + activation.TriggerId);
        foreach (var draw in transition.Draws.Where(draw => draw.DrawnCount > 0))
            lines.Add(Name(step.Before.Duel, draw.Plan.PlayerId) + "：补牌 " + draw.DrawnCount);
        foreach (var player in transition.State.Duel.Players)
        {
            var before = step.Before.Duel.Player(player.Id);
            var oldItems = before.Relics.Relics.ToDictionary(r => r.InstanceId);
            var newItems = player.Relics.Relics.ToDictionary(r => r.InstanceId);
            foreach (var old in oldItems.Values.Where(r => !newItems.ContainsKey(r.InstanceId)))
                lines.Add("移除道具：" + ContentName(entry, "Relics", old.DefinitionId));
            foreach (var item in newItems.Values)
            {
                string itemName = ContentName(entry, "Relics", item.DefinitionId);
                if (!oldItems.TryGetValue(item.InstanceId, out var old)) { lines.Add("获得全新道具：" + itemName); continue; }
                if (old.RemainingCharges != item.RemainingCharges) lines.Add(itemName + "：剩余 " + (item.RemainingCharges?.ToString() ?? "不限") + " 次");
                long cooldown = item.Memory.CounterValue(BattleItemRuntime.CooldownBattlesKey);
                if (old.Memory.CounterValue(BattleItemRuntime.CooldownBattlesKey) != cooldown) lines.Add(itemName + "：冷却 " + cooldown + " 场战斗");
                bool wasActive = old.Memory.CounterValue(BattleItemRuntime.RouletteActiveKey) != 0;
                bool active = item.Memory.CounterValue(BattleItemRuntime.RouletteActiveKey) != 0;
                if (active && (!wasActive || old.Memory.CounterValue(BattleItemRuntime.RouletteTurnKey) != item.Memory.CounterValue(BattleItemRuntime.RouletteTurnKey)))
                    lines.Add(itemName + "：状态第 " + item.Memory.CounterValue(BattleItemRuntime.RouletteTurnKey) + " 回合");
                else if (wasActive && !active) lines.Add(itemName + "：状态结束");
            }
            if (before.Energy.Energy != player.Energy.Energy) lines.Add(Name(step.Before.Duel, player.Id) + "：能量 " + before.Energy.Energy + " → " + player.Energy.Energy);
        }
        if (step.Before.Duel.CompletedRounds != transition.State.Duel.CompletedRounds) lines.Add("已完成回合 " + transition.State.Duel.CompletedRounds);
        if (!step.Before.Duel.IsFinished && transition.State.Duel.IsFinished) lines.Add(transition.State.Duel.Phase == ScoredDuelPhase.Won ? "战斗胜利" : "战斗失败");
        if (step.Action == CardDuelAction.CollectPlayed) lines.Add("已打出的牌进入弃牌堆");
        if (lines.Count == 0) lines.Add("结算完成");
        return (object)D("action", step.Action.ToString(), "actor", step.Actor.Value, "title", Name(step.Before.Duel, step.Actor) + " · " + ActionName(step.Action),
            "revision", Number(transition.State.Revision), "lines", lines.ToArray(),
            "cardCreations", transition.Timeline.Where(fact => fact.Kind == BattleTimelineKind.CardsCreated)
                .Select(fact => D("actor", fact.Actor.Value, "owner", fact.CardOwner.Value, "zone", fact.ToZone, "position", fact.InsertionPosition?.ToString(),
                    "count", fact.Cards.Count, "unplayableCount", fact.Cards.Count(id => !fact.State.Table.GetCard(id).CanPlay))).ToArray());
    }).ToArray();

    private static IEnumerable<string> WorldEffectLines(IEnumerable<BattleTimelineEntry> timeline, Entry entry)
    {
        // Read committed facts, not the intended effect: living limits and card capacity can reduce counts.
        var facts = timeline.Where(fact => fact.Kind == BattleTimelineKind.EnemySummoned || fact.Kind == BattleTimelineKind.CardsCreated).ToArray();
        for (int index = 0; index < facts.Length; index++)
        {
            var fact = facts[index];
            string owner = DisplayName(entry, fact.State.Duel, fact.Actor);
            if (fact.Kind == BattleTimelineKind.EnemySummoned)
            {
                string definition = fact.State.Duel.Combat.Get(new ParticipantId(fact.Reason)).DefinitionId;
                int count = 1;
                while (index + 1 < facts.Length && facts[index + 1].Kind == BattleTimelineKind.EnemySummoned &&
                    facts[index + 1].Actor == fact.Actor &&
                    facts[index + 1].State.Duel.Combat.Get(new ParticipantId(facts[index + 1].Reason)).DefinitionId == definition)
                { count++; index++; }
                yield return owner + "：召唤 " + ContentName(entry, "Enemies", definition) + " × " + count + "（下个敌方回合行动）";
            }
            else
            {
                bool unplayable = fact.Cards.All(id => (fact.State.Table.GetCard(id).Effects & CardEffects.Unplayable) != 0);
                string zone = InsertionLabel(fact.ToZone, fact.InsertionPosition);
                yield return owner + "：生成" + (unplayable ? "废牌" : "卡牌") + " × " + fact.Cards.Count + "，塞入 " +
                    DisplayName(entry, fact.State.Duel, fact.CardOwner) + " 的" + zone;
            }
        }
    }

    private static string InsertionLabel(string zone, CardInsertionPosition? position)
    {
        if (zone == CardZoneKinds.Hand) return "手牌";
        if (zone == CardZoneKinds.Discard) return "弃牌堆";
        if (zone == CardZoneKinds.Deck) return position == CardInsertionPosition.Top ? "抽牌堆牌顶" :
            position == CardInsertionPosition.Bottom ? "抽牌堆牌底" : "抽牌堆（随机洗入）";
        return zone;
    }

    private static string DisplayName(Entry entry, TeamBattleState state, ParticipantId id) => ContentName(entry,
        state.Players.Any(player => player.Id == id) ? "Players" : "Enemies", state.Combat.Get(id).DefinitionId);

    private static IEnumerable<string> EventLines(IEnumerable<CombatRuleEvent> events, TeamBattleState names, CombatStatusCatalog catalog, Entry entry)
    {
        foreach (var item in events)
        {
            var skipped = item as CombatActionWindowSkippedEvent;
            if (skipped != null) { yield return Name(names, skipped.Actor) + "：受控制，跳过行动"; continue; }
            var changed = item as CombatStatusChangedEvent;
            if (changed != null)
            {
                var status = changed.StatusAfter ?? changed.StatusBefore;
                string reason = changed.Reason == CombatStatusChangeReason.Cleansed ? "净化" : changed.Reason == CombatStatusChangeReason.Expired ? "到期" : changed.Reason == CombatStatusChangeReason.Refreshed ? (changed.StatusAfter.Stacks > changed.StatusBefore.Stacks ? "叠加" : "刷新") : changed.StatusAfter == null ? "耗尽" : "消耗";
                string owner = status.Target.Kind == EntityKind.Participant ? Name(names, new ParticipantId(status.Target.Id)) : status.Target.Id;
                yield return owner + "：" + StatusName(status.DefinitionId, catalog, entry) + " " + reason + (changed.StatusAfter == null ? "" : "（" + changed.StatusAfter.Stacks + " 层，" + Duration(changed.StatusAfter.Duration) + "）");
                continue;
            }
            var timing = item as CombatTimingEvent;
            if (timing != null)
            {
                if (timing.Signal.Kind == CombatTimingKind.BattleStarted) yield return "战斗开始";
                if (timing.Signal.Kind == CombatTimingKind.BattleEnded) yield return "战斗结束事件";
                if (timing.Signal.Kind == CombatTimingKind.TurnStarted) yield return "回合开始：" + string.Join("、", timing.Signal.Participants.Select(id => Name(names, id)));
                if (timing.Signal.Kind == CombatTimingKind.BeforeDraw) yield return "摸牌前事件";
                if (timing.Signal.Kind == CombatTimingKind.Draw) yield return "摸牌后事件：" + timing.Signal.Cards.Count + " 张";
                if (timing.Signal.Kind == CombatTimingKind.Reshuffle) yield return "洗回抽牌堆事件：" + timing.Signal.Cards.Count + " 张";
                if (timing.Signal.Kind == CombatTimingKind.PlayPhaseEnd) yield return "出牌阶段结束：保留 " + timing.Signal.HandCount + " 张手牌";
                if (timing.Signal.Kind == CombatTimingKind.PhaseStarted) yield return PhaseName(timing.Signal.PhaseKey) + "开始";
                if (timing.Signal.Kind == CombatTimingKind.PhaseEnding) yield return PhaseName(timing.Signal.PhaseKey) + "结束";
                continue;
            }
            var effect = item.Effect;
            if (effect == null) continue;
            string target = Name(names, effect.Target) + "：";
            if (item is CombatStatusBlockedEvent) { yield return target + "免疫 " + StatusName(effect.Status.DefinitionId, catalog, entry); continue; }
            if (item is CombatStatusEvent) { yield return target + "获得 " + StatusName(effect.Status.DefinitionId, catalog, entry) + " ×" + effect.Status.Stacks + "（" + Duration(effect.Status.Duration) + "）"; continue; }
            var fact = item as CombatEvent;
            if (fact == null) continue;
            string origin = Origin(item.Provenance == null ? CombatEffectOrigin.Primary : item.Provenance.Origin);
            switch (effect.Kind)
            {
                case CombatEffectKind.Damage:
                    yield return target + origin + "伤害 " + Number(fact.SettledDamage) + "（扣血 " + Number(fact.DamageToHealth) + " · 护盾吸收 " + Number(fact.BlockAbsorbed) + "）" + (fact.Died ? " · 击败" : ""); break;
                case CombatEffectKind.Heal: yield return target + origin + "治疗 +" + Number(fact.HealthRestored) + (fact.Overheal > 0 ? "（溢出 " + Number(fact.Overheal) + "）" : ""); break;
                case CombatEffectKind.GainBlock: yield return target + origin + "护盾 +" + Number(fact.After.Block - fact.Before.Block); break;
                case CombatEffectKind.ClearBlock: yield return target + "护盾清空"; break;
                case CombatEffectKind.Revive: yield return target + "复活，生命 " + fact.After.Health; break;
                case CombatEffectKind.LoseHealth: yield return target + "失去生命 " + (fact.Before.Health-fact.After.Health); break;
                case CombatEffectKind.Eliminate: yield return target + "消灭"; break;
            }
        }
    }

    private static string Name(TeamBattleState state, ParticipantId id)
    {
        int index = state.Players.ToList().FindIndex(player => player.Id == id);
        if (index >= 0) return "玩家 " + (index + 1) + " · " + id.Value;
        index = state.Enemies.ToList().FindIndex(enemy => enemy.Id == id);
        return index >= 0 ? "敌人 " + (index + 1) + " · " + id.Value : id.Value;
    }
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string StatusName(StatusDefinitionId id, CombatStatusCatalog catalog, Entry entry)
    {
        var definition = catalog == null ? null : catalog.Find(id);
        string name = ContentName(entry, "Statuses", id.Value);
        return definition == null || definition.Behaviors.Count == 0 ? name : name + " · " + string.Join(" + ", definition.Behaviors.Select(BehaviorName));
    }
    private static string BehaviorName(StatusBehaviorSpec behavior)
    {
        if (behavior is BlockActionWindowSpec) return "眩晕";
        if (behavior is PreventStatusApplicationSpec) return "控制免疫";
        if (behavior is ReflectBlockedDamageSpec) return "盾反 " + Ratio(((ReflectBlockedDamageSpec)behavior).Ratio);
        if (behavior is LifeStealSpec) return "吸血 " + Ratio(((LifeStealSpec)behavior).Ratio);
        if (behavior is OverhealDamageSpec) return "溢出治疗转伤 " + Ratio(((OverhealDamageSpec)behavior).Ratio);
        if (behavior is HitArmorSpec armor) return "护甲 · 承伤 " + Ratio(armor.Multiplier) + " · 每击消耗 " + armor.ConsumeStacks + " 层";
        var periodic = behavior as PeriodicDamageSpec;
        if (periodic != null)
        {
            string subject = periodic.TimingSubject == PeriodicTimingSubject.Applier ? "施加者" : "中毒者";
            string timing = periodic.Timing == CombatTimingKind.TurnStarted ? "回合开始" :
                periodic.Timing == CombatTimingKind.ActionCompleted ? "每次有效行动后" : "所属阵营阶段结束";
            return subject + timing + "：" + Number(periodic.DamagePerStack) + " 伤害/层" +
                (periodic.StackDecay > 0 ? "，随后减 " + periodic.StackDecay + " 层" : "");
        }
        return "状态效果";
    }
    private static string Ratio(decimal ratio) => ratio <= decimal.MaxValue / 100
        ? (ratio * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%" : ratio.ToString(CultureInfo.InvariantCulture) + " 倍";
    private static string PhaseName(string key) => key == BattleStatusTiming.PlayerPhase ? "玩家阶段" : key == BattleStatusTiming.EnemyPhase ? "敌人阶段" : key;
    private static string Duration(StatusDuration duration)
    {
        switch (duration.Kind)
        {
            case DurationKind.TriggerCount: return "剩余 " + duration.RemainingTriggers + " 次";
            case DurationKind.UntilRoundEnd: return "至对应回合结束";
            case DurationKind.UntilPhaseEnd: return "至对应" + PhaseName(duration.PhaseKey) + "结束";
            default: return "持续生效";
        }
    }
    private static string Origin(CombatEffectOrigin origin)
    {
        switch (origin)
        {
            case CombatEffectOrigin.Reflection: return "盾反 · ";
            case CombatEffectOrigin.Lifesteal: return "吸血 · ";
            case CombatEffectOrigin.Overheal: return "溢出转伤 · ";
            case CombatEffectOrigin.Periodic: return "持续效果 · ";
            case CombatEffectOrigin.Triggered: return "触发 · ";
            default: return "";
        }
    }
    private static string ActionName(CardDuelAction action)
    {
        switch (action)
        {
            case CardDuelAction.Draw: return "主动摸牌";
            case CardDuelAction.SkipDraw: return "跳过摸牌";
            case CardDuelAction.DiscardForDraw: return "摸牌前弃牌";
            case CardDuelAction.ConfirmOverflow: return "强制弃牌";
            case CardDuelAction.FinishTurn: return "确认结束回合";
            case CardDuelAction.UseItem: return "使用道具";
            case CardDuelAction.Play: return "出牌";
            case CardDuelAction.Discard: return "弃牌";
            case CardDuelAction.EndTurn: return "结束回合";
            case CardDuelAction.EnemyAction: return "敌人行动";
            case CardDuelAction.Start: return "开局";
            case CardDuelAction.SkipPlayerTurn: return "跳过行动";
            case CardDuelAction.CollectPlayed: return "收牌";
            default: return action.ToString();
        }
    }
    private static string HandName(HandCategory category)
    {
        switch (category)
        {
            case HandCategory.HighCard: return "高牌";
            case HandCategory.OnePair: return "对子";
            case HandCategory.TwoPair: return "两对";
            case HandCategory.ThreeOfAKind: return "三条";
            case HandCategory.Straight: return "顺子";
            case HandCategory.Flush: return "同花";
            case HandCategory.FullHouse: return "葫芦";
            case HandCategory.FourOfAKind: return "四条";
            case HandCategory.StraightFlush: return "同花顺";
            case HandCategory.FiveOfAKind: return "五条";
            default: return category.ToString();
        }
    }
    private static string SuitLabel(Suit suit)
    {
        switch (suit)
        {
            case Suit.Clubs: return "♣";
            case Suit.Diamonds: return "♦";
            case Suit.Hearts: return "♥";
            case Suit.Spades: return "♠";
            default: return "";
        }
    }
    private static string RankLabel(Rank rank)
    {
        switch (rank)
        {
            case Rank.Ace: return "A";
            case Rank.King: return "K";
            case Rank.Queen: return "Q";
            case Rank.Jack: return "J";
            default: return ((int)rank).ToString(CultureInfo.InvariantCulture);
        }
    }
    private static string Error(CardDuelError error)
    {
        switch (error)
        {
            case CardDuelError.None: return "可以执行";
            case CardDuelError.StaleRevision: return "对局已经变化，请重新选择卡牌";
            case CardDuelError.InvalidPhase: return "当前阶段或该玩家不可行动";
            case CardDuelError.InvalidSelection: return "选牌数量、卡牌归属或卡牌状态不符合要求";
            case CardDuelError.NoDiscards: return "整场弃牌次数已用尽";
            case CardDuelError.InsufficientEnergy: return "能量不足";
            case CardDuelError.Overflow: return "结算数值超限";
            case CardDuelError.InvalidTarget: return "请选择符合效果要求的目标";
            case CardDuelError.InvalidState: return "对局状态无效，请重新开局";
            case CardDuelError.InvalidDamagePlan: return "伤害配置无法结算";
            case CardDuelError.InvalidDamageModifier: return "伤害修正配置无法结算";
            case CardDuelError.InvalidDiscardReward: return "弃牌收益配置无法结算";
            case CardDuelError.InvalidEffectPlan: return "效果配置无法结算";
            case CardDuelError.ActionBlocked: return "当前玩家受控制，无法行动";
            case CardDuelError.InvalidReaction: return "触发效果配置无法结算";
            case CardDuelError.ReactionLimitExceeded: return "触发链超出上限，请检查循环触发";
            case CardDuelError.InvalidRelicState: return "遗物运行状态无效";
            case CardDuelError.InvalidAmountModifier: return "数值修正配置无法结算";
            case CardDuelError.InvalidHandEffectModifier: return "遗物对牌型的效果修正无法结算";
            case CardDuelError.InvalidRuleModifier: return "规则修正配置无法结算";
            case CardDuelError.InvalidDealPlan: return "补牌配置无法结算";
            default: return "规则拒绝了本次操作";
        }
    }
}
