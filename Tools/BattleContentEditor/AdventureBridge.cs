using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PokerEngine;

// A local transport adapter. Gameplay, choices, purchases and saves are owned by the pure C# session.
internal static partial class AdventureBridge
{
    private sealed class Entry
    {
        internal BattleAdventureSession Session;
        internal BattleContentSource Source;
        internal string SessionId, Token, Seed, BattleXml, ConfigXml;
        internal DateTime Touched;
    }
    private static readonly Dictionary<string, Entry> Sessions = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private static readonly BridgeJson Json = new BridgeJson();
    private const int MaxXml = 2 * 1024 * 1024, MaxSave = 6 * 1024 * 1024;

    internal static object Handle(Dictionary<string, object> envelope)
    {
        Entry previous = null;
        try
        {
            var request = Object(envelope, "adventureRequest");
            string sessionId = Text(request, "sessionId", 64);
            if (!Guid.TryParseExact(sessionId, "D", out _)) return Failure("InvalidSession", "窗口编号无效，请刷新页面。");
            foreach (var key in Sessions.Where(p => DateTime.UtcNow - p.Value.Touched > TimeSpan.FromHours(2)).Select(p => p.Key).ToArray())
                Sessions.Remove(key);
            Sessions.TryGetValue(sessionId, out previous);
            string op = Text(request, "op", 32);
            string defaultBattle = Text(envelope, "defaultBattleXml", MaxXml);
            string defaultConfig = Text(envelope, "defaultConfigXml", MaxXml);
            if (op == "catalog")
            {
                var loaded = previous;
                if (loaded == null && Optional(request, "save", MaxSave) is string saved) loaded = ReadSave(saved, sessionId);
                var catalog = Catalog(loaded, Optional(request, "battleXml", MaxXml) ?? loaded?.BattleXml ?? defaultBattle,
                    Optional(request, "configXml", MaxXml) ?? loaded?.ConfigXml ?? defaultConfig);
                catalog["standardConfigXml"] = defaultConfig;
                catalog["standardBattleXml"] = defaultBattle;
                return catalog;
            }
            if (op == "restore")
            {
                if (previous == null && Sessions.Count >= 16) return Failure("SessionLimit", "请先关闭其他冒险窗口。");
                if (previous != null && Optional(request, "runId", 64) != previous.Token)
                    return Failure("StaleRun", "当前旅程已经变化，请先同步再恢复。", previous);
                var restored = ReadSave(Text(request, "save", MaxSave), sessionId);
                return Commit(restored);
            }
            if (op == "start")
            {
                if (previous != null && Optional(request, "runId", 64) != previous.Token)
                    return Failure("StaleRun", "当前旅程已经变化，请先同步。", previous);
                if (previous != null && !Terminal(previous.Session))
                    return Failure("ActiveAdventure", "当前旅程尚未结束。请继续旅程，或先结束本次旅程。", previous);
                if (previous == null && Optional(request, "runId", 64) != null)
                    return Failure("SessionNotFound", "原旅程已不在服务中，请先恢复本地存档。");
                if (previous == null && Sessions.Count >= 16) return Failure("SessionLimit", "请先关闭其他冒险窗口。");
                string battleXml = Optional(request, "battleXml", MaxXml) ?? defaultBattle;
                string configXml = Optional(request, "configXml", MaxXml) ?? defaultConfig;
                var (content, source, definition) = Compile(battleXml, configXml);
                string character = Text(request, "characterId", 256), tarot = Text(request, "tarotId", 256);
                var slot = content.Encounters[definition.StartingEncounterId].Players.Single();
                if (character != slot.TemplateId) return Failure("InvalidCharacter", "请选择当前配置中的角色。", previous);
                string seed = Text(request, "seed", 256);
                var next = new Entry { SessionId = sessionId, Token = Guid.NewGuid().ToString("D"), Seed = seed,
                    BattleXml = battleXml, ConfigXml = configXml, Source = source, Touched = DateTime.UtcNow };
                next.Session = new BattleAdventureSession(definition, content, "adventure/" + next.Token, SeedValue(seed), tarot,
                    previous?.Session.Progression);
                return Commit(next);
            }
            if (previous == null) return Failure("SessionNotFound", "旅程尚未开始或服务已重启。可以恢复本地存档。");
            previous.Touched = DateTime.UtcNow;
            if (op == "state") return Response(previous);
            if (Text(request, "runId", 64) != previous.Token)
                return Failure("StaleRun", "当前旅程已经变化，请重新同步。", previous);
            long revision = Revision(request, "revision");
            if (revision != previous.Session.Revision)
                return Failure("StaleRevision", "旅程状态已经更新，请重新选择。", previous);
            if (op == "close")
            {
                Sessions.Remove(sessionId);
                return D("success", true, "closed", true);
            }
            if (op == "command")
            {
                var next = Copy(previous, previous.Session.Fork());
                var result = next.Session.Execute(revision, Command(request));
                if (!result.Accepted) return Failure(result.Error, Error(result.Error), previous);
                return Commit(next, result.BattleResult);
            }
            if (op == "battle" || op == "preview") return Battle(request, previous, revision, op == "preview");
            return Failure("InvalidOperation", "未知冒险操作。", previous);
        }
        catch (Exception error) when (error is ArgumentException || error is InvalidOperationException ||
            error is OverflowException || error is System.Xml.XmlException || error is SnapshotException)
        {
            Console.Error.WriteLine("Adventure host: " + error.GetType().Name);
            return Failure("InvalidRequest", "配置、存档或操作参数无效：" + error.Message, previous);
        }
    }

    private static object Battle(Dictionary<string, object> request, Entry entry, long revision, bool preview)
    {
        if (entry.Session.Phase != AdventurePhase.Battle || entry.Session.Battle == null)
            return Failure("WrongPhase", "当前不在战斗中。", entry);
        var before = entry.Session.Battle;
        if (Revision(request, "battleRevision") != before.State.Revision)
            return Failure("StaleRevision", "战斗已经更新，请重新选牌。", entry);
        var actor = new ParticipantId(Text(request, "actor", 1024));
        if (actor != before.State.Duel.PlayerId) return Failure("InvalidActor", "只能操作当前玩家。", entry);
        string actionName = Text(request, "action", 32);
        if (!new[] { "Play", "Discard", "EndTurn", "Draw", "SkipDraw", "DiscardForDraw", "ConfirmOverflow", "FinishTurn", "UseItem" }.Contains(actionName) ||
            !Enum.TryParse(actionName, out CardDuelAction action))
            return Failure("InvalidAction", "此操作不能由玩家提交。", entry);
        var cards = StringArray(request, "selected", 512, 2048).Select(id => new CardInstanceId(id)).ToArray();
        var targets = Bindings(request);
        int count = request.ContainsKey("drawCount") ? checked((int)Revision(request, "drawCount")) : 0;
        if (count > 512) return Failure("InvalidAmount", "摸牌张数无效。", entry);
        var next = Copy(entry, entry.Session.Fork());
        var result = action == CardDuelAction.UseItem
            ? next.Session.UseBattleItem(revision, Text(request, "itemInstanceId", 2048), Optional(request, "targetItemInstanceId", 2048))
            : next.Session.ExecuteBattle(revision, action, cards, targetBindings: targets, drawCount: count);
        if (preview)
        {
            var response = Response(entry, includeSave: false);
            response["preview"] = PlaytestBridge.AdventurePreview(before, result.BattleResult, actor, action, cards, entry.Source);
            return response;
        }
        if (!result.Accepted) return Failure(result.Error, Error(result.Error), entry);
        return Commit(next, result.BattleResult);
    }

    private static AdventureCommand Command(Dictionary<string, object> request)
    {
        string action = Text(request, "action", 32);
        switch (action)
        {
            case "enter": return new AdventureCommand(AdventureCommandKind.EnterNode, Text(request, "nodeId", 256));
            case "continue": return new AdventureCommand(AdventureCommandKind.Continue);
            case "buy": return new AdventureCommand(AdventureCommandKind.BuyRelic, Text(request, "itemId", 256));
            case "sell": return new AdventureCommand(AdventureCommandKind.SellRelic, Text(request, "itemId", 1024));
            case "rest": return new AdventureCommand(AdventureCommandKind.ChooseRest, Text(request, "optionId", 256));
            case "abandon": return new AdventureCommand(AdventureCommandKind.Abandon);
            case "service":
                var kind = ServiceKind(Text(request, "itemId", 32));
                Suit? suit = null;
                if (kind == AdventureCommandKind.ChangeSuit)
                {
                    string value = Text(request, "suit", 32);
                    if (!Enum.TryParse(value, out Suit parsed) || !Enum.IsDefined(typeof(Suit), parsed)) throw new ArgumentException("花色无效。");
                    suit = parsed;
                }
                return new AdventureCommand(kind, Text(request, "cardId", 1024), suit);
            default: throw new ArgumentException("未知冒险指令。");
        }
    }

    private static (CompiledBattleContent, BattleContentSource, BattleAdventureDefinition) Compile(string battleXml, string configXml)
    {
        var read = BattleContentXmlReader.Read(battleXml, "冒险战斗配置.xml");
        if (!read.Success) throw new ArgumentException(string.Join("；", read.Diagnostics.Take(5).Select(d => d.Message)));
        var compiled = BattleContentCompiler.Compile(read.Source);
        if (!compiled.Success) throw new ArgumentException(string.Join("；", compiled.Diagnostics.Take(5).Select(d => d.Message)));
        return (compiled.Content, read.Source, BattleAdventureXml.Load(configXml, compiled.Content));
    }
    private static Entry ReadSave(string saved, string sessionId)
    {
        var data = Json.DeserializeObject(saved) as Dictionary<string, object> ?? throw new ArgumentException("存档必须是 JSON 对象。");
        if (Revision(data, "schema") != 1) throw new ArgumentException("不支持的存档版本。");
        string battleXml = Text(data, "battleXml", MaxXml), configXml = Text(data, "configXml", MaxXml);
        var (content, source, definition) = Compile(battleXml, configXml);
        var entry = new Entry { SessionId = sessionId, Token = Guid.NewGuid().ToString("D"), Source = source,
            BattleXml = battleXml, ConfigXml = configXml, Seed = Text(data, "seed", 256), Touched = DateTime.UtcNow,
            Session = BattleAdventureSession.Restore(definition, content, Text(data, "state", MaxSave)) };
        if (entry.Session.Seed != SeedValue(entry.Seed)) throw new ArgumentException("存档种子与旅程不一致。");
        return entry;
    }
    private static string Save(Entry entry)
    {
        string result = Json.Serialize(D("schema", 1, "battleXml", entry.BattleXml,
            "configXml", entry.ConfigXml, "seed", entry.Seed, "state", entry.Session.Export()));
        if (result.Length > MaxSave) throw new ArgumentException("存档已达到本机工具大小上限，请导出当前存档。");
        return result;
    }
    private static object Commit(Entry next, BattleSessionResult battleResult = null)
    {
        var response = Response(next, battleResult);
        Json.Serialize(response); // Publish only after a complete, deliverable response exists.
        Sessions[next.SessionId] = next;
        return response;
    }
    private static Entry Copy(Entry before, BattleAdventureSession session) => new Entry {
        Session = session, SessionId = before.SessionId, Token = before.Token, Source = before.Source,
        Seed = before.Seed, BattleXml = before.BattleXml, ConfigXml = before.ConfigXml, Touched = DateTime.UtcNow
    };
    private static Dictionary<string, object> Response(Entry entry, BattleSessionResult result = null, bool includeSave = true)
    {
        var battle = PlaytestBridge.AdventureBattle(entry.Session.Battle, entry.SessionId,
            entry.Token + "/" + (entry.Session.CurrentNode?.Id ?? ""), entry.Seed,
            entry.Session.CurrentNode?.EncounterId ?? "", entry.Session.Content.Fingerprint, entry.Source);
        var response = D("success", true, "adventure", Snapshot(entry, battle), "battle", battle,
            "steps", PlaytestBridge.AdventureSteps(entry.Session.Battle, result, entry.Source));
        if (includeSave) response["save"] = Save(entry);
        return response;
    }
    private static Dictionary<string, object> Failure(string error, string message, Entry entry = null)
    {
        var response = entry == null ? D() : Response(entry, includeSave: false);
        response["success"] = false; response["error"] = error; response["message"] = message;
        return response;
    }
    private static bool Terminal(BattleAdventureSession session) => session.Phase == AdventurePhase.Completed || session.Phase == AdventurePhase.Failed;
    private static ulong SeedValue(string value)
    {
        using (var sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            ulong seed = 0; for (int i = 0; i < 8; i++) seed = (seed << 8) | hash[i]; return seed;
        }
    }
    private static BattleTargetBindings Bindings(Dictionary<string, object> request)
    {
        if (!request.TryGetValue("targetBindings", out var raw) || raw == null) return null;
        if (!(raw is Dictionary<string, object> values) || values.Count > 64) throw new ArgumentException("目标选择无效。");
        return new BattleTargetBindings(values.Select(p => new KeyValuePair<string, IEnumerable<ParticipantId>>(
            Checked(p.Key, 1024), Values(p.Value, 64, 1024).Select(v => new ParticipantId(v)))));
    }
    private static long Revision(Dictionary<string, object> request, string key)
    {
        if (!request.TryGetValue(key, out var raw) || !long.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
            NumberStyles.None, CultureInfo.InvariantCulture, out long value)) throw new ArgumentException("缺少或无效的 " + key);
        return value;
    }
    private static Dictionary<string, object> Object(Dictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var value) && value is Dictionary<string, object> result ? result : throw new ArgumentException("需要 JSON 对象：" + key);
    private static string Text(Dictionary<string, object> data, string key, int max) =>
        data.TryGetValue(key, out var value) ? Checked(value as string, max) : throw new ArgumentException("缺少字段：" + key);
    private static string Optional(Dictionary<string, object> data, string key, int max) =>
        !data.TryGetValue(key, out var value) || value == null ? null : Checked(value as string, max);
    private static string Checked(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.IndexOf('\0') >= 0) throw new ArgumentException("文本字段无效。");
        return value;
    }
    private static string[] StringArray(Dictionary<string, object> data, string key, int count, int max) =>
        data.TryGetValue(key, out var value) ? Values(value, count, max) : Array.Empty<string>();
    private static string[] Values(object value, int count, int max) =>
        value is IList list && list.Count <= count ? list.Cast<object>().Select(v => Checked(v as string, max)).ToArray() : throw new ArgumentException("列表字段无效。");
    private static Dictionary<string, object> D(params object[] pairs)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
        return result;
    }
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static AdventureCommandKind ServiceKind(string id) => id == "delete" ? AdventureCommandKind.DeleteCard :
        id == "duplicate" ? AdventureCommandKind.DuplicateCard : id == "suit" ? AdventureCommandKind.ChangeSuit :
        throw new ArgumentException("未知改牌服务。");
    private static string ServiceId(AdventureCommandKind kind) => kind == AdventureCommandKind.DeleteCard ? "delete" :
        kind == AdventureCommandKind.DuplicateCard ? "duplicate" : "suit";
    private static string Error(string error)
    {
        switch (error)
        {
            case "StaleRevision": return "状态已变化，请重新选择。";
            case "InsufficientGold": case "Insufficient gold.": return "金币不足。";
            case "InvalidNode": case "Node is not available.": return "只能进入当前可达的节点。";
            case "WrongPhase": case "Shop services require a shop node.": return "请进入商店后再购买或改牌。";
            case "AlreadyChosen": case "A rest reward can only be chosen once.": return "本次休整已经完成。";
            case "Complete this node before continuing.": return "请先完成本节点的战斗或休整选择。";
            case "Minimum deck size reached.": return "持有牌组已达到最小张数。";
            case "Maximum deck size reached.": return "持有牌组已达到最大张数。";
            case "Card already has this suit.": return "这张牌已经是所选花色。";
            case "Relic capacity or copy limit reached.": return "遗物栏已满，或同类遗物已达到持有上限。";
            case "Shop item is unavailable.": return "这件商品本次已售出。";
            case "Adventure is already terminal.": return "本次旅程已经结束。";
            case "InvalidPhase": return "当前阶段无法执行此操作。";
            case "InvalidSelection": return "请检查所选手牌和出牌张数。";
            case "InsufficientEnergy": return "当前能量不足。";
            default: return "当前操作未生效：" + error;
        }
    }
}
