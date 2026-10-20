using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PokerEngine;

// Explicit local administrator transport. Ordinary battle projections never call this adapter.
internal static class AdminBridge
{
    internal static BattleAdminPatch Read(Dictionary<string, object> request)
    {
        var p = Object(request, "patch");
        Only(p, "reason", "vitals", "zones", "relics", "gold");
        return new BattleAdminPatch(
            Rows(p, "vitals", 128).Select(v => { Only(v, "unit", "health", "maxHealth", "block"); return new BattleAdminVitals(Id(v, "unit"), Long(v, "health"), Long(v, "maxHealth"), Long(v, "block")); }),
            Rows(p, "zones", 64).Select(z => { Only(z, "owner", "kind", "cards"); return new BattleAdminZone(Id(z, "owner"), Text(z, "kind"), Rows(z, "cards", 512, true).Select(c => {
                Only(c, "instanceId", "definitionId", "effects", "isDisabled");
                CardEffects? effects = null;
                if (Optional(c, "effects") is string value)
                { if (!Enum.TryParse(value, out CardEffects parsed)) throw new ArgumentException("无效卡牌效果。"); effects = parsed; }
                bool? disabled = null;
                if (c.TryGetValue("isDisabled", out var raw) && raw != null)
                { if (!(raw is bool flag)) throw new ArgumentException("isDisabled 必须为布尔值。"); disabled = flag; }
                return new BattleAdminCard(Optional(c, "instanceId"), Optional(c, "definitionId"), effects, disabled);
            })); }),
            Rows(p, "relics", 32).Select(r => { Only(r, "owner", "items"); return new BattleAdminRelics(Id(r, "owner"), Rows(r, "items", 32, true).Select(i => {
                Only(i, "definitionId", "instanceId", "level", "charges", "growth", "cooldownRounds", "usesThisRound");
                return new BattleAdminRelic(Text(i, "definitionId"), Optional(i, "instanceId"), Int(i, "level"), Int(i, "charges"), Long(i, "growth"), Int(i, "cooldownRounds"), Int(i, "usesThisRound"));
            })); }),
            Rows(p, "gold", 32).Select(g => { Only(g, "owner", "amount"); return new BattleAdminGold(Id(g, "owner"), Long(g, "amount") ?? throw new ArgumentException("缺少金币数。")); }),
            Optional(p, "reason"));
    }

    internal static object Battle(BattleSession session, CompiledBattleContent content, BattleContentSource source, long? revision = null)
    {
        var state = session.State; var duel = state.Duel;
        return D("scope", "battle", "editable", CardDuelEngine.CanApplyAdmin(state),
            "revision", N(revision ?? state.Revision), "battleRevision", N(state.Revision), "deckOrder", "bottom-to-top",
            "players", duel.Players.Select(p => {
                var unit = duel.Combat.Get(p.Id);
                return D("id", p.Id.Value, "name", Name(source, "Players", unit.DefinitionId),
                    "health", N(unit.Health), "maxHealth", N(unit.MaxHealth), "block", N(unit.Block), "gold", N(state.GoldFor(p.Id)),
                    "zones", new[] { CardZoneKinds.Hand, CardZoneKinds.Deck, CardZoneKinds.Discard }.Select(kind => D("kind", kind,
                        "cards", state.Zone(kind, p.Id).Cards.Select(id => {
                            var card = state.Table.GetCard(id); var face = session.Engine.Face(card);
                            return D("instanceId", id.Value, "definitionId", card.DefinitionId.Value, "label", Face(face),
                                "rank", face.Rank.ToString(), "suit", face.Suit.ToString(), "effects", card.Effects.ToString(), "isDisabled", card.IsDisabled);
                        }).ToArray())).ToArray(),
                    "relics", p.Relics.Relics.Select(r => Relic(source, r.InstanceId, r.DefinitionId, r.Level, r.RemainingCharges, r.Growth, r.CooldownRounds, r.UsesThisRound)).ToArray());
            }).ToArray(),
            "enemies", duel.Enemies.Select(e => { var unit = duel.Combat.Get(e.Id); return D("id", e.Id.Value,
                "name", Name(source, "Enemies", unit.DefinitionId), "health", N(unit.Health), "maxHealth", N(unit.MaxHealth), "block", N(unit.Block)); }).ToArray(),
            "catalog", Catalog(content, source));
    }
    internal static object Adventure(BattleAdventureSession session, BattleContentSource source)
    {
        if (session.Phase == AdventurePhase.Battle && session.Battle != null) return Battle(session.Battle, session.Content, source, session.Revision);
        return D("scope", "adventure", "editable", session.Phase == AdventurePhase.Map || session.Phase == AdventurePhase.Shop || session.Phase == AdventurePhase.Rest,
            "revision", N(session.Revision), "deckOrder", "bottom-to-top", "players", new[] { D("id", session.PlayerId.Value,
                "name", session.PlayerId.Value, "health", N(session.Health), "maxHealth", N(session.MaxHealth), "block", null, "gold", N(session.Gold),
                "zones", Array.Empty<object>(), "relics", session.Relics.Select(r => Relic(source, r.Id, r.DefinitionId, r.Level, r.Charges, r.Growth, r.CooldownRounds, 0)).ToArray()) },
            "enemies", Array.Empty<object>(), "catalog", Catalog(session.Content, source));
    }
    private static object Relic(BattleContentSource source, string id, string definition, int level, int? charges, long growth, int cooldown, int uses) =>
        D("instanceId", id, "definitionId", definition, "name", Name(source, "Relics", definition), "level", level, "charges", charges,
            "growth", N(growth), "cooldownRounds", cooldown, "usesThisRound", uses);
    private static object Catalog(CompiledBattleContent content, BattleContentSource source) => D(
        "cards", StandardDeck.Cards().Select(c => D("definitionId", c.Id.Value, "label", Face(c), "rank", c.Rank.ToString(), "suit", c.Suit.ToString())).ToArray(),
        "relics", content.Relics.Values.Select(r => D("definitionId", r.Definition.Id, "name", Name(source, "Relics", r.Definition.Id),
            "maxLevel", r.Definition.MaxLevel, "maxCopies", r.Definition.MaxCopies, "charges", r.Definition.Charges)).ToArray());
    private static string Name(BattleContentSource source, string section, string id)
    {
        var node = source?.Root.Children.FirstOrDefault(n => n.Name == section)?.Children.FirstOrDefault(n => n.Attributes.TryGetValue("id", out var value) && value == id);
        return node != null && node.Attributes.TryGetValue("name", out var name) ? name : id;
    }
    private static string Face(CardDefinition c) => (c.Suit == Suit.Spades ? "♠" : c.Suit == Suit.Hearts ? "♥" : c.Suit == Suit.Clubs ? "♣" : "♦") +
        (c.Rank == Rank.Ace ? "A" : c.Rank == Rank.King ? "K" : c.Rank == Rank.Queen ? "Q" : c.Rank == Rank.Jack ? "J" : ((int)c.Rank).ToString(CultureInfo.InvariantCulture));
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static ParticipantId Id(Dictionary<string, object> d, string key) => new ParticipantId(Text(d, key));
    private static string Text(Dictionary<string, object> d, string key) => Optional(d, key) ?? throw new ArgumentException("缺少字段：" + key);
    private static string Optional(Dictionary<string, object> d, string key)
    {
        if (!d.TryGetValue(key, out var raw) || raw == null) return null;
        if (!(raw is string value) || string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.IndexOf('\0') >= 0) throw new ArgumentException("文本字段无效：" + key);
        return value;
    }
    private static long? Long(Dictionary<string, object> d, string key)
    {
        if (!d.TryGetValue(key, out var raw) || raw == null) return null;
        // Decimal strings avoid JavaScript double rounding before the 64-bit authority sees the value.
        if (!(raw is string value) || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number))
            throw new ArgumentException("64 位数值请使用非负整数字符串：" + key);
        return number;
    }
    private static int? Int(Dictionary<string, object> d, string key)
    {
        if (!d.TryGetValue(key, out var raw) || raw == null) return null;
        if (!int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out int value)) throw new ArgumentException("整数字段无效：" + key);
        return value;
    }
    private static Dictionary<string, object> Object(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var raw) && raw is Dictionary<string, object> value ? value : throw new ArgumentException("需要对象：" + key);
    private static IEnumerable<Dictionary<string, object>> Rows(Dictionary<string, object> d, string key, int limit, bool required = false)
    {
        if (!d.TryGetValue(key, out var raw)) { if (required) throw new ArgumentException("缺少列表：" + key); return Array.Empty<Dictionary<string, object>>(); }
        if (!(raw is IList list) || list.Count > limit || list.Cast<object>().Any(v => !(v is Dictionary<string, object>))) throw new ArgumentException("列表无效：" + key);
        return list.Cast<Dictionary<string, object>>().ToArray();
    }
    private static void Only(Dictionary<string, object> d, params string[] keys)
    { if (d.Keys.Any(k => !keys.Contains(k))) throw new ArgumentException("管理员字段不受支持。"); }
    private static Dictionary<string, object> D(params object[] pairs)
    { var d = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) d.Add((string)pairs[i], pairs[i + 1]); return d; }
}
