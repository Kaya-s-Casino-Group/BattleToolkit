using System;
using System.Collections.Generic;

namespace PokerEngine
{
    /// <summary>
    /// Connects a rule session to a replaceable frontend. Local playback gates input, while
    /// accepted commands settle completely before their presentation begins.
    /// </summary>
    public sealed class BattleSessionPlayback
    {
        public IBattleSessionCommands Commands { get; }
        public BattleSession Session => Commands.Session;
        public long Revision => Commands.Revision;
        public IBattleSessionPresentation Presentation { get; }

        public BattleSessionPlayback(BattleSession session, IBattleSessionPresentation presentation)
            : this(new BattleSessionCommands(session), presentation, null) { }

        public BattleSessionPlayback(IBattleSessionCommands commands, IBattleSessionPresentation presentation, CardDuelState initialState)
        {
            Commands = commands ?? throw new ArgumentNullException(nameof(commands));
            if (Session == null) throw new ArgumentException("A command source must expose a current battle.", nameof(commands));
            Presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
            Presentation.Initialize(initialState ?? Session.State);
        }

        public BattleSessionResult Start(bool animate = true)
        {
            var result = Submit(Session.State.Duel.PlayerId, CardDuelAction.Start, animate: animate);
            if (!result.Accepted) throw new InvalidOperationException("Opening turn rejected: " + result.Error);
            return result;
        }

        /// <summary>Submits a local input against the current authority revision. Explicit revision callers use Execute.</summary>
        public BattleSessionResult Submit(ParticipantId actor, CardDuelAction action,
            IEnumerable<CardInstanceId> selected = null, IEnumerable<ParticipantId> targets = null, bool animate = true,
            BattleTargetBindings targetBindings = null, int drawCount = 0) =>
            Execute(Revision, actor, action, selected, targets, animate, targetBindings, drawCount);

        public bool CanAct(ParticipantId actor) =>
            !Presentation.IsPresenting && BattleActionQuery.CanAct(Session.State, actor);

        public bool CanPlay(ParticipantId actor, IEnumerable<CardInstanceId> selected) =>
            !Presentation.IsPresenting && BattleActionQuery.CanSelect(Session.Engine.Duel, Session.State, actor, selected, false);

        public bool CanDiscard(ParticipantId actor, IEnumerable<CardInstanceId> selected) =>
            !Presentation.IsPresenting && BattleActionQuery.CanSelect(Session.Engine.Duel, Session.State, actor, selected, true);

        public CardDuelTransition PreviewPlay(ParticipantId actor, IEnumerable<CardInstanceId> selected,
            IEnumerable<ParticipantId> targets = null, BattleTargetBindings targetBindings = null)
        {
            if (!CanAct(actor)) return null;
            var state = Session.State;
            return Session.Engine.PreviewPlay(state, state.Revision, actor, selected, targets, targetBindings);
        }

        public CardDuelTransition PreviewDiscard(ParticipantId actor, IEnumerable<CardInstanceId> selected,
            IEnumerable<ParticipantId> targets = null, BattleTargetBindings targetBindings = null)
        {
            if (!CanAct(actor)) return null;
            var state = Session.State;
            return Session.Engine.PreviewDiscard(state, state.Revision, actor, selected, targets, targetBindings);
        }

        public bool CanUseItem(ParticipantId actor, string instanceId) =>
            !Presentation.IsPresenting && BattleItemRuntime.CanUse(Session.State, actor, instanceId);

        /// <summary>Evaluates a disposable session. The authority and its random cursor remain unchanged.</summary>
        public BattleSessionResult PreviewItem(ParticipantId actor, string instanceId, string targetInstanceId = null)
        {
            if (Presentation.IsPresenting)
                return new BattleSessionResult(Session.State, Session.State, CardDuelError.InvalidPhase, Array.Empty<BattleSessionStep>());
            var preview = new BattleSession(Session.Engine, Session.State);
            return preview.UseItem(preview.State.Revision, actor, instanceId, targetInstanceId);
        }

        /// <summary>Active items follow the same input gate and committed-state recovery as card commands.</summary>
        public BattleSessionResult UseItem(long expectedRevision, ParticipantId actor, string instanceId,
            string targetInstanceId = null, bool animate = true)
        {
            if (Presentation.IsPresenting)
                return new BattleSessionResult(Session.State, Session.State, CardDuelError.InvalidPhase, Array.Empty<BattleSessionStep>());
            var result = Commands.UseItem(expectedRevision, actor, instanceId, targetInstanceId);
            if (!result.Accepted) return result;
            try { Presentation.Present(result, animate); }
            catch { Presentation.Synchronize(Session.State); throw; }
            return result;
        }

        public BattleSessionResult Execute(long expectedRevision, ParticipantId actor, CardDuelAction action,
            IEnumerable<CardInstanceId> selected = null, IEnumerable<ParticipantId> targets = null, bool animate = true,
            BattleTargetBindings targetBindings = null, int drawCount = 0)
        {
            if (Presentation.IsPresenting)
                return new BattleSessionResult(Session.State, Session.State, CardDuelError.InvalidPhase, Array.Empty<BattleSessionStep>());

            var result = Commands.Execute(expectedRevision, actor, action, selected, targets, targetBindings, drawCount);
            if (!result.Accepted) return result;
            try { Presentation.Present(result, animate); }
            catch
            {
                // Presentation failure cannot undo or repeat an already committed command.
                Presentation.Synchronize(Session.State);
                throw;
            }
            return result;
        }

        /// <summary>Administrative correction uses the same committed-state presentation contract
        /// as normal inputs. Playback in progress must finish before the correction can commit.</summary>
        public BattleSessionResult ApplyAdmin(long expectedRevision, BattleAdminPatch patch, bool animate = false)
        {
            if (Presentation.IsPresenting)
                return new BattleSessionResult(Session.State, Session.State, CardDuelError.InvalidPhase, Array.Empty<BattleSessionStep>());
            var result = Commands.ApplyAdmin(expectedRevision, patch);
            if (!result.Accepted) return result;
            try { Presentation.Present(result, animate); }
            catch { Presentation.Synchronize(Session.State); throw; }
            return result;
        }

        public void Synchronize() => Presentation.Synchronize(Session.State);

        /// <summary>Displays an opening already committed by an outer authority. Never executes Start a second time.</summary>
        public void PresentCommitted(BattleSessionResult result, bool animate = true)
        {
            if (result == null || !result.Accepted || !ReferenceEquals(result.State, Session.State))
                throw new ArgumentException("The committed batch must end at the current authority state.", nameof(result));
            if (Presentation.IsPresenting) throw new InvalidOperationException("Finish the current presentation first.");
            try { Presentation.Present(result, animate); }
            catch { Presentation.Synchronize(Session.State); throw; }
        }
    }
}
