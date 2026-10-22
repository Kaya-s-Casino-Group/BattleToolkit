using System;
using System.Collections.Generic;
using System.Linq;
using PokerEngine;

internal static partial class PlaytestBridge
{
    // Replay committed phase-entry checkpoints. Never infer elapsed rounds from a final snapshot.
    private static object[] WrathPlayback(IEnumerable<BattleTimelineEntry> timeline, Entry entry) => timeline
        .Where(fact => fact.Kind == BattleTimelineKind.PhaseStarted && fact.PhaseKey == "enemy")
        .Select(fact => new { Fact = fact, View = WrathBossQuery.Read(fact.State.Duel) })
        .Where(item => item.View != null)
        .Select(item => (object)D("id", item.Fact.ActionId + "/" + item.Fact.Sequence + "/wrath-enemy-phase",
            "kind", "EnemyPhase", "round", item.Fact.State.Duel.CompletedRounds + 1,
            "battle", Snapshot(entry, item.Fact.State),
            "handFloats", item.View.Hands.Where(hand => !hand.IsAlive && hand.ReviveRemaining > 0)
                .Select(hand => D("id", hand.Id.Value, "remaining", hand.ReviveRemaining)).ToArray()))
        .ToArray();
}
