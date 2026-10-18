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
            "actors", duel.Players.Where(player => duel.CanAct(player.Id)).Select(player => player.Id.Value).ToArray(),
            "players", duel.Players.Select(player => {
                var unit = Unit(duel, player.Id, catalog);
                unit["energy"] = player.Energy.Energy; unit["maxEnergy"] = player.Energy.MaxEnergy;
                unit["handLimit"] = player.Limits.HandLimit.Value; unit["discardsRemaining"] = state.RemainingDiscardBudgetFor(player.Id);
                unit["maxPlayCards"] = player.Rules.MaxPlayCards; unit["maxDiscardCards"] = player.Rules.MaxDiscardCards;
                unit["canAct"] = duel.CanAct(player.Id); unit["hasEnded"] = player.HasEnded;
                unit["costs"] = D("play", entry.Session.Engine.Duel.QueryActionCost(duel, player.Id, ScoredDuelActionKind.ScoredHand).Cost,
                    "discard", entry.Session.Engine.Duel.QueryActionCost(duel, player.Id, ScoredDuelActionKind.Discard).Cost);
                unit["relics"] = player.Relics.Relics.Select(relic => D("id", relic.InstanceId, "name", relic.DefinitionId,
                    "level", relic.Level, "charges", relic.RemainingCharges, "cooldown", relic.CooldownRounds, "usesThisRound", relic.UsesThisRound)).ToArray();
                unit["hand"] = state.Zone(CardZoneKinds.Hand, player.Id).Cards.Select(id => {
                    var card = state.Table.GetCard(id); var face = entry.Session.Engine.Face(card);
                    return D("id", id.Value, "rank", face.Rank.ToString(), "suit", face.Suit.ToString(),
                        "label", SuitLabel(face.Suit) + RankLabel(face.Rank), "disabled", card.IsDisabled, "effects", card.Effects.ToString());
                }).ToArray();
                unit["deckCount"] = state.Zone(CardZoneKinds.Deck, player.Id).Cards.Count;
                unit["discardCount"] = state.Zone(CardZoneKinds.Discard, player.Id).Cards.Count;
                return unit;
            }).ToArray(),
            "enemies", duel.Enemies.Select(enemy => {
                var unit = Unit(duel, enemy.Id, catalog);
                var intent = entry.Session.Engine.Duel.PreviewEnemyAction(duel, enemy.Id);
                unit["intent"] = intent == null ? new string[0] : intent.Accepted ? EventLines(intent.RuleEvents, duel, catalog).ToArray() : new[] { "意图暂不可预览：" + Error(intent.Error) };
                return unit;
            }).ToArray());
    }

    private static Dictionary<string, object> Unit(TeamBattleState state, ParticipantId id, CombatStatusCatalog catalog)
    {
        var unit = state.Combat.Get(id);
        return D("id", id.Value, "name", Name(state, id), "health", Number(unit.Health), "maxHealth", Number(unit.MaxHealth),
            "block", Number(unit.Block), "alive", unit.IsAlive,
            "statuses", state.Combat.Statuses.Where(status => status.Target.Kind == EntityKind.Participant && status.Target.Id == id.Value)
                .Select(status => D("id", status.Id.Value, "definitionId", status.DefinitionId.Value, "name", StatusName(status.DefinitionId, catalog),
                    "stacks", status.Stacks, "duration", Duration(status.Duration))).ToArray());
    }

    private static object Hand(ScoreResult score) => D("category", score.Category.ToString(), "label", HandName(score.Category),
        "points", Number(score.Points), "multiplier", score.Multiplier.ToString(CultureInfo.InvariantCulture), "total", Number(score.Total));

    private static object[] Steps(BattleSessionResult result, CombatStatusCatalog catalog) => result.Steps.Select(step => {
        var transition = step.Transition; var lines = new List<string>();
        if (transition.Score != null) lines.Add(HandName(transition.Score.Category) + " · 点数 " + Number(transition.Score.Points) +
            " × 倍率 " + transition.Score.Multiplier.ToString(CultureInfo.InvariantCulture) + " = 牌型得分 " + Number(transition.Score.Total));
        if (transition.ActionCost != null) lines.Add("消耗能量 " + transition.ActionCost.Cost);
        lines.AddRange(EventLines(transition.RuleEvents, step.Before.Duel, catalog));
        foreach (var activation in transition.RelicActivations)
            lines.Add("遗物触发：" + activation.Before.DefinitionId + " / " + activation.TriggerId);
        foreach (var draw in transition.Draws.Where(draw => draw.DrawnCount > 0))
            lines.Add(Name(step.Before.Duel, draw.Plan.PlayerId) + "：补牌 " + draw.DrawnCount);
        foreach (var player in transition.State.Duel.Players)
        {
            var before = step.Before.Duel.Player(player.Id);
            if (before.Energy.Energy != player.Energy.Energy) lines.Add(Name(step.Before.Duel, player.Id) + "：能量 " + before.Energy.Energy + " → " + player.Energy.Energy);
        }
        if (step.Before.Duel.CompletedRounds != transition.State.Duel.CompletedRounds) lines.Add("已完成回合 " + transition.State.Duel.CompletedRounds);
        if (!step.Before.Duel.IsFinished && transition.State.Duel.IsFinished) lines.Add(transition.State.Duel.Phase == ScoredDuelPhase.Won ? "战斗胜利" : "战斗失败");
        if (step.Action == CardDuelAction.CollectPlayed) lines.Add("已打出的牌进入弃牌堆");
        if (lines.Count == 0) lines.Add("结算完成");
        return (object)D("action", step.Action.ToString(), "actor", step.Actor.Value, "title", Name(step.Before.Duel, step.Actor) + " · " + ActionName(step.Action),
            "revision", Number(transition.State.Revision), "lines", lines.ToArray());
    }).ToArray();

    private static IEnumerable<string> EventLines(IEnumerable<CombatRuleEvent> events, TeamBattleState names, CombatStatusCatalog catalog)
    {
        foreach (var item in events)
        {
            var skipped = item as CombatActionWindowSkippedEvent;
            if (skipped != null) { yield return Name(names, skipped.Actor) + "：受控制，跳过行动"; continue; }
            var changed = item as CombatStatusChangedEvent;
            if (changed != null)
            {
                var status = changed.StatusAfter ?? changed.StatusBefore;
                string reason = changed.Reason == CombatStatusChangeReason.Expired ? "到期" : changed.Reason == CombatStatusChangeReason.Refreshed ? "刷新" : changed.StatusAfter == null ? "耗尽" : "消耗";
                string owner = status.Target.Kind == EntityKind.Participant ? Name(names, new ParticipantId(status.Target.Id)) : status.Target.Id;
                yield return owner + "：" + StatusName(status.DefinitionId, catalog) + " " + reason + (changed.StatusAfter == null ? "" : "（" + Duration(changed.StatusAfter.Duration) + "）");
                continue;
            }
            var timing = item as CombatTimingEvent;
            if (timing != null)
            {
                if (timing.Signal.Kind == CombatTimingKind.BattleStarted) yield return "战斗开始";
                if (timing.Signal.Kind == CombatTimingKind.PhaseStarted) yield return PhaseName(timing.Signal.PhaseKey) + "开始";
                if (timing.Signal.Kind == CombatTimingKind.PhaseEnding) yield return PhaseName(timing.Signal.PhaseKey) + "结束";
                continue;
            }
            var effect = item.Effect;
            if (effect == null) continue;
            string target = Name(names, effect.Target) + "：";
            if (item is CombatStatusBlockedEvent) { yield return target + "免疫 " + StatusName(effect.Status.DefinitionId, catalog); continue; }
            if (item is CombatStatusEvent) { yield return target + "获得 " + StatusName(effect.Status.DefinitionId, catalog) + " ×" + effect.Status.Stacks + "（" + Duration(effect.Status.Duration) + "）"; continue; }
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
    private static string StatusName(StatusDefinitionId id, CombatStatusCatalog catalog)
    {
        var definition = catalog == null ? null : catalog.Find(id);
        return definition == null || definition.Behaviors.Count == 0 ? id.Value : id.Value + " · " + string.Join(" + ", definition.Behaviors.Select(BehaviorName));
    }
    private static string BehaviorName(StatusBehaviorSpec behavior)
    {
        if (behavior is BlockActionWindowSpec) return "眩晕";
        if (behavior is PreventStatusApplicationSpec) return "控制免疫";
        if (behavior is ReflectBlockedDamageSpec) return "盾反 " + Ratio(((ReflectBlockedDamageSpec)behavior).Ratio);
        if (behavior is LifeStealSpec) return "吸血 " + Ratio(((LifeStealSpec)behavior).Ratio);
        if (behavior is OverhealDamageSpec) return "溢出治疗转伤 " + Ratio(((OverhealDamageSpec)behavior).Ratio);
        var periodic = behavior as PeriodicDamageSpec;
        if (periodic != null) return (periodic.Timing == CombatTimingKind.ActionCompleted ? "行动后" : "阶段结束时") + Number(periodic.DamagePerStack) + " 伤害/层";
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
