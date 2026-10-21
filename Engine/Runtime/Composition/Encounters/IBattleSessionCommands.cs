using System.Collections.Generic;

namespace PokerEngine
{
    /// <summary>Authoritative command boundary. Revision belongs to the owning session, which may be a whole adventure.</summary>
    public interface IBattleSessionCommands
    {
        /// <summary>The current battle view. An adventure may return a new read/preview wrapper after each commit.</summary>
        BattleSession Session { get; }
        long Revision { get; }
        BattleSessionResult Execute(long expectedRevision, ParticipantId actor, CardDuelAction action,
            IEnumerable<CardInstanceId> selected = null, IEnumerable<ParticipantId> targets = null,
            BattleTargetBindings targetBindings = null, int drawCount = 0);
        BattleSessionResult UseItem(long expectedRevision, ParticipantId actor, string instanceId, string targetInstanceId = null);
        BattleSessionResult ApplyAdmin(long expectedRevision, BattleAdminPatch patch);
    }

    /// <summary>Standalone battles retain their existing session and revision semantics.</summary>
    public sealed class BattleSessionCommands : IBattleSessionCommands
    {
        public BattleSession Session { get; }
        public long Revision => Session.State.Revision;
        public BattleSessionCommands(BattleSession session) =>
            Session = session ?? throw new System.ArgumentNullException(nameof(session));
        public BattleSessionResult Execute(long expectedRevision, ParticipantId actor, CardDuelAction action,
            IEnumerable<CardInstanceId> selected = null, IEnumerable<ParticipantId> targets = null,
            BattleTargetBindings targetBindings = null, int drawCount = 0) =>
            Session.Execute(expectedRevision, actor, action, selected, targets, targetBindings, drawCount);
        public BattleSessionResult UseItem(long expectedRevision, ParticipantId actor, string instanceId, string targetInstanceId = null) =>
            Session.UseItem(expectedRevision, actor, instanceId, targetInstanceId);
        public BattleSessionResult ApplyAdmin(long expectedRevision, BattleAdminPatch patch) => Session.ApplyAdmin(expectedRevision, patch);
    }
}
