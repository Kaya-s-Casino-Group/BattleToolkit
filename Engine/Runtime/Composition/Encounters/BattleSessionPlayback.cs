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
        public BattleSession Session { get; }
        public IBattleSessionPresentation Presentation { get; }

        public BattleSessionPlayback(BattleSession session, IBattleSessionPresentation presentation)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
            Presentation.Initialize(Session.State);
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
            BattleTargetBindings targetBindings = null) =>
            Execute(Session.State.Revision, actor, action, selected, targets, animate, targetBindings);

        public bool CanAct(ParticipantId actor)
        {
            var state = Session.State;
            return !Presentation.IsPresenting && state.HasStarted && !state.Duel.IsFinished &&
                state.Duel.CanAct(actor) && state.Zone(CardZoneKinds.Played, actor).Cards.Count == 0;
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
            if (discard && !state.HasDiscardBudgetFor(actor) ||
                !Session.Engine.Duel.QueryActionCost(state.Duel, actor,
                    discard ? ScoredDuelActionKind.Discard : ScoredDuelActionKind.ScoredHand).CanAfford) return false;
            int limit = discard ? rules.MaxDiscardCards : rules.MaxPlayCards;
            var hand = new HashSet<CardInstanceId>(state.Zone(CardZoneKinds.Hand, actor).Cards);
            var seen = new HashSet<CardInstanceId>();
            foreach (var id in selected)
            {
                if (!id.IsValid || !seen.Add(id) || seen.Count > limit || !hand.Contains(id)) return false;
                var card = state.Table.GetCard(id);
                if (discard ? !card.CanDiscard : !card.CanPlay) return false;
            }
            return seen.Count > 0;
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

        public BattleSessionResult Execute(long expectedRevision, ParticipantId actor, CardDuelAction action,
            IEnumerable<CardInstanceId> selected = null, IEnumerable<ParticipantId> targets = null, bool animate = true,
            BattleTargetBindings targetBindings = null)
        {
            if (Presentation.IsPresenting)
                return new BattleSessionResult(Session.State, Session.State, CardDuelError.InvalidPhase, Array.Empty<BattleSessionStep>());

            var result = Session.Execute(expectedRevision, actor, action, selected, targets, targetBindings);
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

        public void Synchronize() => Presentation.Synchronize(Session.State);
    }
}
