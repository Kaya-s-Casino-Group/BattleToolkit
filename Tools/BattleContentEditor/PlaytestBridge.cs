using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PokerEngine;

// External tool host only. All gameplay and previews use the existing pure C# engine.
internal static partial class PlaytestBridge
{
    private sealed class Entry
    {
        internal string SessionId, RunId, Seed, EncounterId, Fingerprint, XmlHash;
        internal BattleSession Session;
        // Authoring labels are presentation metadata and are intentionally not part of combat state.
        internal BattleContentSource ContentSource;
        internal DateTime Touched;
        internal int Commands;
    }
    private static readonly Dictionary<string, Entry> Sessions = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private static readonly BridgeJson Json = new BridgeJson();
    private const int MaxRequestCharacters = 4 * 1024 * 1024;
    private const int MaxSessions = 16;

    public static int Run()
    {
        Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            object response;
            try
            {
                if (line.Length > MaxRequestCharacters) response = Fail("RequestTooLarge", "请求过大，请缩小内容包。");
                else response = Handle(Json.DeserializeObject(line) as Dictionary<string, object> ?? throw new ArgumentException("需要 JSON 对象。"));
                Console.WriteLine(Json.Serialize(response));
            }
            catch (Exception error)
            {
                // Never echo XML or protocol input to stderr or a browser error.
                Console.Error.WriteLine("Playtest host: " + error.GetType().Name);
                Console.WriteLine(Json.Serialize(Fail("InvalidRequest", "试玩请求无法处理，请检查字段或重新开局。")));
            }
            Console.Out.Flush();
        }
        return 0;
    }

    private static object Handle(Dictionary<string, object> request)
    {
        foreach (var id in Sessions.Where(pair => DateTime.UtcNow - pair.Value.Touched > TimeSpan.FromHours(2)).Select(pair => pair.Key).ToArray())
            Sessions.Remove(id);
        string sessionId = Text(request, "sessionId", 64);
        Guid parsed;
        if (!Guid.TryParseExact(sessionId, "D", out parsed)) return Fail("InvalidSession", "试玩窗口编号无效，请刷新页面。");
        Entry entry; Sessions.TryGetValue(sessionId, out entry);
        string op = Text(request, "op", 16);
        if (op == "start") return Start(request, sessionId, entry);
        if (entry == null) return Fail("SessionNotFound", "试玩对局不存在或已过期，请重新开局。");
        entry.Touched = DateTime.UtcNow;
        if (op == "state") return D("success", true, "battle", Snapshot(entry));
        if (Text(request, "runId", 64) != entry.RunId) return Fail("StaleRun", "对局已重开，请使用当前对局。", entry);
        long revision;
        if (!long.TryParse(Text(request, "revision", 24), NumberStyles.None, CultureInfo.InvariantCulture, out revision))
            return Fail("InvalidRevision", "对局版本无效，请重新读取当前对局。", entry);
        if (revision != entry.Session.State.Revision) return Fail("StaleRevision", "对局已经变化，请重新选择卡牌。", entry);
        if (op == "close") { Sessions.Remove(sessionId); return D("success", true); }
        if (op != "preview" && op != "command") return Fail("InvalidOperation", "未知试玩操作。", entry);
        if (op == "command" && entry.Commands >= 4096) return Fail("CommandLimit", "本次试玩已达到 4096 次操作，请重新开局。", entry);

        var state = entry.Session.State;
        var actor = new ParticipantId(Text(request, "actor", 1024));
        if (!state.Duel.Players.Any(player => player.Id == actor)) return Fail("InvalidActor", "只能操作本局的玩家。", entry);
        CardDuelAction action;
        string actionName = Text(request, "action", 16);
        if (!new[] { "Play", "Discard", "EndTurn", "Draw", "SkipDraw", "DiscardForDraw", "ConfirmOverflow", "FinishTurn" }.Contains(actionName) || !Enum.TryParse(actionName, false, out action))
            return Fail("InvalidAction", "此动作不属于玩家可提交的战斗输入。", entry);
        var cards = Strings(request, "selected", 52, 2048).Select(id => new CardInstanceId(id)).ToArray();
        var bindings = ReadBindings(request);
        // Old damage-only content accepts the default opponent request, but not named effect bindings.
        IEnumerable<ParticipantId> legacyTargets = null;
        if (action == CardDuelAction.Play && state.Duel.Player(actor).Rules.EffectRule == null && bindings != null && bindings.Slots.Count > 0)
        {
            if (bindings.Slots.Count != 1 || !bindings.Slots.ContainsKey(BattleTargetBindings.DefaultSlot))
                return Fail("InvalidTarget", Error(CardDuelError.InvalidTarget), entry);
            legacyTargets = bindings.Slots[BattleTargetBindings.DefaultSlot]; bindings = null;
        }
        var candidate = new BattleSession(entry.Session.Engine, state);
        int drawCount = 0;
        if (request.ContainsKey("drawCount") && (!int.TryParse(Convert.ToString(request["drawCount"], CultureInfo.InvariantCulture), out drawCount) || drawCount < 0 || drawCount > 512))
            return Fail("InvalidSelection", "摸牌张数无效。", entry);
        var result = candidate.Execute(revision, actor, action, cards, legacyTargets, bindings, drawCount);
        if (op == "preview")
        {
            var hand = entry.Session.Engine.PreviewHand(state, actor, cards);
            object targets = new object[0]; string targetError = null;
            if (action == CardDuelAction.Play && hand.Accepted && state.Duel.Combat.Get(actor).IsAlive)
            {
                try { targets = TargetSlots(entry, actor, cards, hand); }
                catch (ArgumentException) { targetError = "无法解析本次效果的选敌要求，请检查牌型与遗物配置。"; }
                catch (OverflowException) { targetError = "本次效果数值超限，请检查配置。"; }
            }
            return D("success", true, "battle", Snapshot(entry), "preview", D(
                "action", action.ToString(), "actor", actor.Value, "selected", cards.Select(card => card.Value).ToArray(),
                "legal", result.Accepted, "error", result.Error.ToString(), "message", result.Accepted ? "可以执行" : Error(result.Error),
                "hand", hand.Accepted ? Hand(hand.Score) : null, "targets", targets, "targetMessage", targetError,
                "steps", Steps(result, entry.Session.Engine.Duel.Definition.StatusCatalog, entry)));
        }
        if (!result.Accepted) return Fail(result.Error.ToString(), Error(result.Error), entry);
        var next = Copy(entry, candidate); next.Commands++;
        var response = D("success", true, "battle", Snapshot(next), "steps", Steps(result, candidate.Engine.Duel.Definition.StatusCatalog, next));
        // Projection and serialization can fail too; commit only a completely deliverable result.
        Json.Serialize(response);
        Sessions[sessionId] = next;
        return response;
    }

    private static object Start(Dictionary<string, object> request, string sessionId, Entry previous)
    {
        string runId = OptionalText(request, "runId", 64);
        if (previous != null && runId != previous.RunId) return Fail("StaleRun", "已有对局，请刷新当前对局后重开。", previous);
        if (previous == null && runId != null) return Fail("SessionNotFound", "原对局已过期，请重新开局。");
        if (previous == null && Sessions.Count >= MaxSessions) return Fail("SessionLimit", "已达到 16 个试玩窗口，请先关闭不用的对局。");
        string xml = Text(request, "xml", MaxRequestCharacters), seed = Text(request, "seed", 256), encounter = Text(request, "encounterId", 1024);
        var read = BattleContentXmlReader.Read(xml, "当前内容包.xml");
        if (!read.Success) return InvalidContent(read.Diagnostics, previous);
        var compiled = BattleContentCompiler.Compile(read.Source);
        if (!compiled.Success) return InvalidContent(compiled.Diagnostics, previous);
        byte[] bytes;
        using (var sha = SHA256.Create()) bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
        // Run IDs are independent freshness tokens; stable battle IDs preserve card IDs on same-seed restarts.
        string battleId = "web-playtest/" + Hash(seed.Length.ToString(CultureInfo.InvariantCulture) + ":" + seed + encounter);
        var build = BattleContentFactory.Create(compiled.Content, new BattleSetupRequest(encounter, battleId, HmacDeckShuffler.FromSeed(bytes)));
        if (!build.Success) return InvalidContent(build.Diagnostics, previous);
        var session = build.Session;
        var opening = session.Execute(session.State.Revision, session.State.Duel.PlayerId, CardDuelAction.Start);
        if (!opening.Accepted) return Fail(opening.Error.ToString(), "开局失败：" + Error(opening.Error), previous);
        var next = new Entry { SessionId = sessionId, RunId = Guid.NewGuid().ToString("D"), Seed = seed, EncounterId = encounter,
            Fingerprint = compiled.Content.Fingerprint, XmlHash = Hash(xml), Session = session, ContentSource = read.Source, Touched = DateTime.UtcNow };
        var response = D("success", true, "battle", Snapshot(next), "steps", Steps(opening, session.Engine.Duel.Definition.StatusCatalog, next));
        Json.Serialize(response);
        Sessions[sessionId] = next;
        return response;
    }

    private static Entry Copy(Entry entry, BattleSession session) => new Entry { SessionId = entry.SessionId, RunId = entry.RunId,
        Seed = entry.Seed, EncounterId = entry.EncounterId, Fingerprint = entry.Fingerprint, XmlHash = entry.XmlHash,
        Session = session, ContentSource = entry.ContentSource, Commands = entry.Commands, Touched = DateTime.UtcNow };

    private static object TargetSlots(Entry entry, ParticipantId actor, CardInstanceId[] selected, CardDuelTransition preview)
    {
        var state = entry.Session.State; var player = state.Duel.Player(actor);
        IEnumerable<EffectTargetSpec> specs;
        if (player.Rules.EffectRule == null) specs = new[] { EffectTargetSpec.Opponents() };
        else
        {
            var ordered = state.Zone(CardZoneKinds.Hand, actor).Cards.Where(selected.Contains).Select(state.Table.GetCard);
            var hand = new HandAttackInfo(preview.Pattern, ordered.Select(card => {
                var face = entry.Session.Engine.Face(card); return new AttackCardSnapshot(card, new EffectiveCard(card.Id, face.Rank, face.Suit));
            }));
            var context = new ScoredHandEffectContext(state.Duel.Combat, actor, preview.Score, hand);
            var sequence = player.Rules.EffectRule.Resolve(context);
            specs = new RelicHandEffectModifier().Modify(new HandEffectModifierContext(player.Relics, context, sequence)).Output.Steps.Select(step => step.Target);
        }
        return specs.Where(spec => spec.RequestSlot != null).GroupBy(spec => spec.RequestSlot, StringComparer.Ordinal).Select(group => {
            var groupSpecs = group.ToArray();
            bool required = groupSpecs.Any(spec => spec.Selection == EffectTargetSelection.Requested);
            var candidates = state.Duel.Combat.Combatants.Where(unit => unit.IsAlive && groupSpecs.All(spec => AcceptsTarget(state.Duel, actor, spec, unit.Id)))
                .Select(unit => D("id", unit.Id.Value, "label", Name(state.Duel, unit.Id))).ToArray();
            return D("slot", group.Key, "label", (groupSpecs.All(spec => spec.Group == EffectTargetGroup.Allies) ? "友方目标" : "效果目标") + " · " + group.Key,
                "required", required, "candidates", candidates);
        }).Where(slot => (bool)slot["required"] || ((object[])slot["candidates"]).Length > 0).ToArray();
    }

    private static bool AcceptsTarget(TeamBattleState state, ParticipantId actor, EffectTargetSpec spec, ParticipantId target)
    {
        try { return BattleTargetResolver.Resolve(state, actor, spec, new[] { target }).Contains(target); }
        catch (ArgumentException) { return false; }
    }

    private static BattleTargetBindings ReadBindings(Dictionary<string, object> request)
    {
        object raw;
        if (!request.TryGetValue("targetBindings", out raw) || raw == null) return null;
        var bindings = raw as Dictionary<string, object>;
        if (bindings == null || bindings.Count > 64) throw new ArgumentException("目标选择无效。");
        var entries = bindings.Select(pair => new KeyValuePair<string, IEnumerable<ParticipantId>>(CheckedText(pair.Key, 1024),
            StringArray(pair.Value, 64, 1024).Select(id => new ParticipantId(id))));
        return new BattleTargetBindings(entries);
    }

    private static Dictionary<string, object> InvalidContent(IEnumerable<BattleDiagnostic> diagnostics, Entry previous)
    {
        var response = Fail("InvalidContent", "配置校验未通过，请修改后重新开局。", previous);
        response["diagnostics"] = diagnostics.Select(item => D("code", item.Code, "severity", item.Severity.ToString(), "message", item.Message,
            "path", item.Location.Path, "line", item.Location.Line, "column", item.Location.Column, "source", item.Location.SourceName,
            "definitionId", item.DefinitionId, "field", item.FieldPath)).ToArray();
        return response;
    }
    private static Dictionary<string, object> Fail(string code, string message, Entry entry = null)
    {
        var response = D("success", false, "error", code, "message", message);
        if (entry != null) response["battle"] = Snapshot(entry);
        return response;
    }
    private static string Hash(string text)
    {
        using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    }
    private static string Text(Dictionary<string, object> request, string name, int max)
    {
        object value; if (!request.TryGetValue(name, out value)) throw new ArgumentException("缺少字段：" + name);
        return CheckedText(value as string, max);
    }
    private static string OptionalText(Dictionary<string, object> request, string name, int max)
    {
        object value; return !request.TryGetValue(name, out value) || value == null ? null : CheckedText(value as string, max);
    }
    private static string CheckedText(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.IndexOf('\0') >= 0) throw new ArgumentException("文本字段无效。");
        return value;
    }
    private static string[] Strings(Dictionary<string, object> request, string name, int max, int textMax)
    {
        object value; return !request.TryGetValue(name, out value) ? new string[0] : StringArray(value, max, textMax);
    }
    private static string[] StringArray(object value, int max, int textMax)
    {
        var array = value as IList;
        if (array == null || array.Count > max) throw new ArgumentException("列表字段无效。");
        return array.Cast<object>().Select(item => CheckedText(item as string, textMax)).ToArray();
    }
    private static Dictionary<string, object> D(params object[] values)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        for (int i = 0; i < values.Length; i += 2) result.Add((string)values[i], values[i + 1]);
        return result;
    }
}
