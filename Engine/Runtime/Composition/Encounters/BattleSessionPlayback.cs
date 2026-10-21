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

        public bool CanAct(ParticipantId actor)
        {
            var state = Session.State;
            return !Presentation.IsPresenting && state.HasStarted && !state.Duel.IsFinished &&
                state.Duel.HasPlayerWindow(actor) && (state.Duel.CanAct(actor) || state.Duel.Player(actor).Rules.Flow.UsesTurnFlow &&
                    (state.FlowFor(actor).Window == BattlePlayerWindow.Draw || state.FlowFor(actor).Window == BattlePlayerWindow.ForcedDiscard || state.FlowFor(actor).Window == BattlePlayerWindow.Ending)) && state.Zone(CardZoneKinds.Played, actor).Cards.Count == 0;
        }

        public bool CanPlay(ParticipantId actor, IEnumerable<CardInstanceId> selected) => CanSelect(actor, selected, false);
        public bool CanDiscard(ParticipantId actor, IEnumerable<CardInstanceId> selected) => CanSelect(actor, selected, true);

        // Button availability checks input budgets and membership only. Custom rules are evaluated by previews/execution.
        private bool CanSelect(ParticipantId actor, IEnumerable<CardInstanceId> selected, bool discard)
        {
            if (!CanAct(actor) || selected == null) return false;
            var state = Session.State;
            var player = state.Duel.Player(actor);
            var rules = player.Rules;
            if (!state.Duel.CanAct(actor)) return false;
            bool freeDefense = discard && rules.Flow.UsesTurnFlow && (!rules.Flow.DiscardCostsAction || state.FlowFor(actor).Window == BattlePlayerWindow.Ending);
            if (discard && !state.HasDiscardBudgetFor(actor) || !freeDefense &&
                !Session.Engine.Duel.QueryActionCost(state.Duel, actor,
                    discard ? ScoredDuelActionKind.Discard : ScoredDuelActionKind.ScoredHand).CanAfford) return false;
            if (rules.Flow.UsesTurnFlow && (discard ? state.FlowFor(actor).Window != BattlePlayerWindow.Action && state.FlowFor(actor).Window != BattlePlayerWindow.Ending :
                state.FlowFor(actor).Window != BattlePlayerWindow.Action)) return false;
            int limit = discard ? rules.MaxDiscardCards : Session.Engine.PlayLimit(state, actor, false);
            var hand = new HashSet<CardInstanceId>(state.Zone(CardZoneKinds.Hand, actor).Cards);
            var seen = new HashSet<CardInstanceId>();
            foreach (var id in selected)
            {
                if (!id.IsValid || !seen.Add(id) || seen.Count > limit || !hand.Contains(id)) return false;
                var card = state.Table.GetCard(id);
                if (discard ? !card.CanDiscard : !card.CanPlay) return false;
            }
            if (rules.Flow.UsesTurnFlow && (discard && (state.FlowFor(actor).DefenseUsed && BattleRuleResolver.Value(rules, state.Duel.Player(actor).Relics, BattleRuleValueKey.DiscardEndsTurn).Value > 0 || state.FlowFor(actor).Window == BattlePlayerWindow.Ending && !rules.Flow.EndDefense) ||
                !discard && state.FlowFor(actor).ActionsRemaining == 0)) return false;
            return seen.Count >= (discard ? 1 : Session.Engine.PlayLimit(state, actor, true));
        }

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
