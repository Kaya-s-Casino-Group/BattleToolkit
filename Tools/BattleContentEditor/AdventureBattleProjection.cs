using System;
using System.Collections.Generic;
using System.Linq;
using PokerEngine;

// The adventure host reuses exactly the single-battle projection and preview language.
internal static partial class PlaytestBridge
{
    private static Entry AdventureEntry(BattleSession session, string sessionId, string runId, string seed,
        string encounterId, string fingerprint, BattleContentSource source) => new Entry {
            Session = session, SessionId = sessionId, RunId = runId, Seed = seed,
            EncounterId = encounterId, Fingerprint = fingerprint, XmlHash = fingerprint, ContentSource = source
        };

    internal static object AdventureBattle(BattleSession session, string sessionId, string runId, string seed,
        string encounterId, string fingerprint, BattleContentSource source) =>
        session == null ? null : Snapshot(AdventureEntry(session, sessionId, runId, seed, encounterId, fingerprint, source));

    internal static object AdventureSteps(BattleSession session, BattleSessionResult result, BattleContentSource source) =>
        result == null ? new object[0] : Steps(result, session.Engine.Duel.Definition.StatusCatalog,
            AdventureEntry(session, "", "", "", "", "", source));

    internal static object AdventurePreview(BattleSession before, BattleSessionResult result, ParticipantId actor,
        CardDuelAction action, CardInstanceId[] cards, BattleContentSource source)
    {
        var entry = AdventureEntry(before, "", "", "", "", "", source);
        var hand = before.Engine.PreviewHand(before.State, actor, cards);
        object targets = new object[0]; string targetError = null;
        if (action == CardDuelAction.Play && hand.Accepted && before.State.Duel.Combat.Get(actor).IsAlive)
        {
            try { targets = TargetSlots(entry, actor, cards, hand); }
            catch (ArgumentException) { targetError = "无法解析本次效果的选敌要求。"; }
            catch (OverflowException) { targetError = "本次效果数值超限。"; }
        }
        return D("action", action.ToString(), "actor", actor.Value, "selected", cards.Select(c => c.Value).ToArray(),
            "legal", result != null && result.Accepted, "error", result?.Error.ToString() ?? "InvalidPhase",
            "message", result == null ? "当前不能进行战斗操作。" : result.Accepted ? "可以执行" : Error(result.Error),
            "hand", hand.Accepted ? Hand(hand.Score, before.Engine.PreviewHandFacts(before.State, actor, cards)) : null,
            "targets", targets, "targetMessage", targetError,
            "steps", result == null ? new object[0] : Steps(result, before.Engine.Duel.Definition.StatusCatalog, entry, includePresentation: false));
    }
}
