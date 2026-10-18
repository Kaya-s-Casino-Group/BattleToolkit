using System;
using System.Collections.Generic;

namespace PokerEngine
{
    /// <summary>A resolved rule step. A frontend may display these snapshots at its own pace.</summary>
    public sealed class BattleSessionStep
    {
        public CardDuelAction Action { get; }
        public ParticipantId Actor { get; }
        public CardDuelState Before { get; }
        public CardDuelTransition Transition { get; }
        public IReadOnlyList<CardInstanceId> Selected { get; }
        internal BattleSessionStep(CardDuelAction action, ParticipantId actor, CardDuelState before, CardDuelTransition transition,
            IEnumerable<CardInstanceId> selected = null)
        {
            Action = action; Actor = actor; Before = before; Transition = transition;
            Selected = Array.AsReadOnly(new List<CardInstanceId>(selected ?? Array.Empty<CardInstanceId>()).ToArray());
        }
    }

    /// <summary>One player command and all consequent automatic rule steps, committed together.</summary>
    public sealed class BattleSessionResult
    {
        public bool Accepted => Error == CardDuelError.None;
        public CardDuelError Error { get; }
        public CardDuelState Before { get; }
        public CardDuelState State { get; }
        public IReadOnlyList<BattleSessionStep> Steps { get; }
        internal BattleSessionResult(CardDuelState before, CardDuelState state, CardDuelError error,
            IEnumerable<BattleSessionStep> steps)
        {
            Before = before; State = state; Error = error;
            Steps = Array.AsReadOnly(new List<BattleSessionStep>(steps).ToArray());
        }
    }

    /// <summary>
    /// Owns a card battle independently of scene objects, input devices and animation clocks.
    /// Commands resolve through the next player decision or terminal state before returning.
    /// Injected engine policies must be pure, as required by CardDuelEngine previews and execution.
    /// </summary>
    public sealed class BattleSession
    {
        public CardDuelEngine Engine { get; }
        public CardDuelState State { get; private set; }

        public BattleSession(CardDuelEngine engine, CardDuelState initialState)
        {
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
            State = initialState ?? throw new ArgumentNullException(nameof(initialState));
            if (!ReferenceEquals(initialState.Duel.Rules, engine.Duel.Definition))
                throw new ArgumentException("The initial state must belong to this engine's rules.", nameof(initialState));
        }

        public BattleSessionResult Execute(long expectedRevision, ParticipantId actor, CardDuelAction action,
            IEnumerable<CardInstanceId> selected = null, IEnumerable<ParticipantId> targets = null, BattleTargetBindings targetBindings = null)
        {
            var before = State;
            if (expectedRevision != before.Revision) return Reject(before, CardDuelError.StaleRevision);
            if (action != CardDuelAction.Start && action != CardDuelAction.Play &&
                action != CardDuelAction.Discard && action != CardDuelAction.EndTurn)
                return Reject(before, CardDuelError.InvalidPhase);

            var steps = new List<BattleSessionStep>();
            var cards = new List<CardInstanceId>(selected ?? Array.Empty<CardInstanceId>()).ToArray();
            var result = Engine.Execute(before, expectedRevision, actor, action, cards, targets, targetBindings);
            if (!result.Accepted) return Reject(before, result.Error);
            steps.Add(new BattleSessionStep(action, actor, before, result, cards));
            var next = result.State;

            // Each engine step completes its own draw/collection boundary before another automatic turn.
            // In particular, a newly started player phase draws before a blocked player is skipped.
            while (!next.Duel.IsFinished)
            {
                ParticipantId automaticActor;
                CardDuelAction automaticAction;
                if (next.Duel.Phase == ScoredDuelPhase.AwaitingEnemyAction)
                {
                    automaticActor = next.Duel.NextEnemy;
                    automaticAction = CardDuelAction.EnemyAction;
                }
                else if (next.Duel.Phase == ScoredDuelPhase.AwaitingPlayerHand && next.Duel.NextBlockedPlayer.IsValid)
                {
                    automaticActor = next.Duel.NextBlockedPlayer;
                    automaticAction = CardDuelAction.SkipPlayerTurn;
                }
                else break;

                result = Engine.Execute(next, next.Revision, automaticActor, automaticAction);
                if (!result.Accepted) return Reject(before, result.Error);
                steps.Add(new BattleSessionStep(automaticAction, automaticActor, next, result));
                next = result.State;
            }

            // Play already performed its configured refill while the submitted cards were in Played.
            // Preserve that shuffle boundary. EnemyAction collects every player's Played itself;
            // a terminal player action intentionally leaves its cards in Played, as before.
            if (next.Duel.Phase == ScoredDuelPhase.AwaitingPlayerHand &&
                next.Zone(CardZoneKinds.Played, actor).Cards.Count > 0)
            {
                result = Engine.Execute(next, next.Revision, actor, CardDuelAction.CollectPlayed);
                if (!result.Accepted) return Reject(before, result.Error);
                steps.Add(new BattleSessionStep(CardDuelAction.CollectPlayed, actor, next, result));
                next = result.State;
            }

            var committed = new BattleSessionResult(before, next, CardDuelError.None, steps);
            State = next;
            return committed;
        }

        private static BattleSessionResult Reject(CardDuelState before, CardDuelError error) =>
            new BattleSessionResult(before, before, error, Array.Empty<BattleSessionStep>());
    }
}
