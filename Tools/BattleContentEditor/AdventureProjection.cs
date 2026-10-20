using System;
using System.Collections.Generic;
using System.Linq;
using PokerEngine;

internal static partial class AdventureBridge
{
    private static Dictionary<string, object> Catalog(Entry entry, string battleXml, string configXml)
    {
        var (content, source, definition) = Compile(battleXml, configXml);
        var slot = content.Encounters[definition.StartingEncounterId].Players.Single();
        var profile = entry?.Session.Progression;
        if (profile != null && profile.RulesFingerprint != definition.ProgressionDefinition.Fingerprint)
            throw new ArgumentException("成长配置已变化，请保留旧存档并使用匹配的局外 XML。");
        profile = profile ?? new ProgressionEngine(definition.ProgressionDefinition).Create(new ParticipantId(slot.Id));
        return D("success", true, "name", definition.Name, "version", definition.Version,
            "characters", new[] { Character(content, source, definition) },
            "tarots", Tarots(definition, profile), "configXml", configXml, "battleXml", battleXml,
            "progression", D("wins", profile.Statistics.Wins, "runsFinished", profile.Statistics.RunsFinished,
                "unlockedTarots", profile.Unlocks.Where(u => u.Kind == UnlockKind.Tarot).Select(u => u.Id).ToArray()));
    }

    private static object Snapshot(Entry entry, object battle)
    {
        var session = entry.Session; var definition = session.Definition;
        var available = new HashSet<string>(session.AvailableNodes.Select(n => n.Id), StringComparer.Ordinal);
        var visited = new HashSet<string>(session.VisitedNodeIds, StringComparer.Ordinal);
        var faces = StandardDeck.Cards().ToDictionary(c => c.Id);
        var tarot = definition.Tarots.Single(t => t.Id == session.TarotId);
        var phase = session.Phase;
        string status = phase == AdventurePhase.Completed ? "Victory" : phase == AdventurePhase.Failed ? "Defeat" : phase.ToString();
        var currentResult = session.Progression.Settlements.FirstOrDefault(s => s.RunId == session.RunId);
        if (currentResult?.Outcome == RunOutcome.Abandoned) status = "Abandoned";
        var shopItems = definition.ShopItems.Select(item => {
            bool purchased = session.BoughtShopItemIds.Contains(item.Id);
            bool canBuy = phase == AdventurePhase.Shop && session.Execute(session.Revision,
                new AdventureCommand(AdventureCommandKind.BuyRelic, item.Id), preview: true).Accepted;
            return D("id", item.Id, "name", item.Name, "description", item.Description, "price", Number(item.Price),
                "sellPrice", Number(item.SellPrice), "purchased", purchased, "canBuy", canBuy);
        }).ToArray();
        var services = definition.ServicePrices.Select(service => {
            string id = ServiceId(service.Kind);
            bool room = service.Kind != AdventureCommandKind.DeleteCard || session.Deck.Count > definition.MinimumDeckSize;
            room &= service.Kind != AdventureCommandKind.DuplicateCard || session.Deck.Count < definition.MaximumDeckSize;
            return D("id", id, "name", id == "delete" ? "撕毁" : id == "duplicate" ? "印卡" : "染色",
                "description", id == "delete" ? "永久移除一张持有牌。" : id == "duplicate" ? "复制一张持有牌。" : "改变一张持有牌的花色，保留点数。",
                "price", Number(session.ServicePrice(service.Kind)), "increase", Number(service.Increase),
                "uses", session.ServiceUses(service.Kind), "requiresCard", true, "requiresSuit", id == "suit",
                "canBuy", phase == AdventurePhase.Shop && room && session.Gold >= session.ServicePrice(service.Kind));
        }).ToArray();
        var rest = definition.RestChoices.Select(choice => D("id", choice.Id, "name", choice.Name, "description", choice.Description,
            "kind", choice.Kind.ToString(), "selected", session.RestChoiceId == choice.Id,
            "enabled", phase == AdventurePhase.Rest && session.Execute(session.Revision,
                new AdventureCommand(AdventureCommandKind.ChooseRest, choice.Id), preview: true).Accepted)).ToArray();
        var owned = session.Relics.Select(relic => {
            var offer = definition.ShopItems.FirstOrDefault(i => i.RelicId == relic.DefinitionId);
            string name = offer?.Name ?? ContentName(entry.Source, "Relics", relic.DefinitionId);
            return D("id", relic.Id, "definitionId", relic.DefinitionId, "name", name,
                "description", offer?.Description ?? "", "level", relic.Level, "charges", relic.Charges,
                "sellPrice", Number(session.SellPrice(relic.Id)), "canSell", phase == AdventurePhase.Shop);
        }).ToArray();
        return D("sessionId", entry.SessionId, "runId", entry.Token, "revision", Number(session.Revision), "seed", entry.Seed,
            "phase", phase.ToString(), "status", status, "name", definition.Name, "version", definition.Version,
            "chapter", session.ChapterIndex + 1, "chapterCount", definition.TotalChapters, "completedChapters", session.CompletedChapters,
            "character", Character(session.Content, entry.Source, definition),
            "tarot", D("id", tarot.Id, "name", tarot.Name, "description", tarot.Description),
            "tarots", Tarots(definition, session.Progression),
            "player", D("health", Number(session.Health), "maxHealth", Number(session.MaxHealth), "gold", Number(session.Gold)),
            "currentNodeId", session.CurrentNode?.Id,
            "nodes", definition.Nodes.Select(n => D("id", n.Id, "name", NodeName(n.Kind), "type", n.Kind.ToString(),
                "chapter", n.Chapter, "row", n.Row, "column", n.Column, "nextIds", n.NextIds.ToArray(),
                "state", visited.Contains(n.Id) ? "completed" : available.Contains(n.Id) ? "available" :
                    session.CurrentNode?.Id == n.Id ? "current" : "locked")).ToArray(),
            "deck", session.Deck.Select(card => {
                var face = faces[card.DefinitionId];
                return D("id", card.Id, "face", Face(face), "label", Face(face), "rank", face.Rank.ToString(),
                    "suit", face.Suit.ToString(), "effects", card.Effects.ToString());
            }).ToArray(),
            "relics", owned, "shop", D("items", shopItems, "services", services),
            "rest", D("options", rest, "selectedOptionId", session.RestChoiceId),
            "reward", D("gold", Number(session.LastRewardGold), "options", Array.Empty<object>(),
                "title", "战斗胜利", "description", "本场奖励已结算，血量与构筑将保留到下一场。"),
            "battle", battle, "canContinue", session.CanContinue,
            "progression", D("wins", session.Progression.Statistics.Wins, "runsFinished", session.Progression.Statistics.RunsFinished),
            "message", "", "log", Array.Empty<object>());
    }
    private static object Character(CompiledBattleContent content, BattleContentSource source, BattleAdventureDefinition definition)
    {
        var slot = content.Encounters[definition.StartingEncounterId].Players.Single();
        return D("id", slot.TemplateId, "name", ContentName(source, "Players", slot.TemplateId),
            "description", "携带持有牌组与天赋，踏上" + definition.TotalChapters + "章旅程。", "art", "hero",
            "maxHealth", Number(content.Players[slot.TemplateId].Definition.MaxHealth));
    }
    private static object[] Tarots(BattleAdventureDefinition definition, PlayerProgressionState profile) =>
        definition.Tarots.Select(t => (object)D("id", t.Id, "name", t.Name, "description", t.Description,
            "unlocked", profile.IsUnlocked(new ProgressionUnlock(UnlockKind.Tarot, t.Id)),
            "unlockDescription", t.UnlockOnVictory ? "完成一次冒险后解锁" : "尚未解锁")).ToArray();
    private static string ContentName(BattleContentSource source, string section, string id)
    {
        var node = source.Root.Children.FirstOrDefault(n => n.Name == section)?.Children
            .FirstOrDefault(n => n.Attributes.TryGetValue("id", out var value) && value == id);
        return node != null && node.Attributes.TryGetValue("name", out var name) ? name : id;
    }
    private static string NodeName(AdventureNodeKind kind) => kind == AdventureNodeKind.Battle ? "遭遇战" :
        kind == AdventureNodeKind.Elite ? "精英战" : kind == AdventureNodeKind.Boss ? "首领战" :
        kind == AdventureNodeKind.Shop ? "吟游诗人商店" : "休息营地";
    private static string Face(CardDefinition card)
    {
        string suit = card.Suit == Suit.Spades ? "♠" : card.Suit == Suit.Hearts ? "♥" : card.Suit == Suit.Clubs ? "♣" : "♦";
        string rank = card.Rank == Rank.Ace ? "A" : card.Rank == Rank.King ? "K" : card.Rank == Rank.Queen ? "Q" :
            card.Rank == Rank.Jack ? "J" : ((int)card.Rank).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return suit + rank;
    }
}
