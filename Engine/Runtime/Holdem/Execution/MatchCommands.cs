using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public abstract class MatchCommand
    {
        public string CommandId { get; }
        public long ExpectedRevision { get; }
        protected MatchCommand(string commandId, long expectedRevision)
        { CommandId = Guard.Text(commandId, nameof(commandId)); ExpectedRevision = expectedRevision; }
    }
    public sealed class BettingCommand : MatchCommand
    {
        public DecisionId DecisionId { get; }
        public BettingAction Action { get; }
        public long RaiseTo { get; }
        public BettingCommand(string commandId, long expectedRevision, DecisionId decisionId, BettingAction action, long raiseTo = 0)
            : base(commandId, expectedRevision) { DecisionId = decisionId; Action = action; RaiseTo = raiseTo; }
    }
    public abstract class SystemMatchCommand : MatchCommand
    { protected SystemMatchCommand(string id, long revision) : base(id, revision) { } }
    public abstract class AbilityCommand : MatchCommand
    { protected AbilityCommand(string id, long revision) : base(id, revision) { } }
    public sealed class ActivateAbilityCommand : AbilityCommand
    {
        public AbilityInstanceId AbilityId { get; }
        public string WindowId { get; }
        public ActivateAbilityCommand(string id, long revision, string windowId, AbilityInstanceId ability) : base(id, revision)
        { WindowId = windowId; AbilityId = ability; }
    }
    public sealed class SelectAbilityTargetsCommand : AbilityCommand
    {
        public DecisionId DecisionId { get; }
        public IReadOnlyList<string> Keys { get; }
        public bool Skip { get; }
        public SelectAbilityTargetsCommand(string id, long revision, DecisionId decision, IEnumerable<string> keys, bool skip = false) : base(id, revision)
        { DecisionId = decision; Keys = Guard.View(Guard.Copy(keys, nameof(keys))); Skip = skip; }
    }
    public sealed class StartHandCommand : SystemMatchCommand
    { public StartHandCommand(string id, long revision) : base(id, revision) { } }
    public sealed class AdvanceWindowCommand : SystemMatchCommand
    {
        public string WindowId { get; }
        public AdvanceWindowCommand(string id, long revision, string windowId) : base(id, revision) { WindowId = windowId; }
    }
    public sealed class DecisionTimeoutCommand : SystemMatchCommand
    {
        public DecisionId DecisionId { get; }
        public DecisionTimeoutCommand(string id, long revision, DecisionId decisionId) : base(id, revision) { DecisionId = decisionId; }
    }

    public enum CommandError { None = 0, StaleRevision, InvalidState, UnsupportedRules, UnsupportedExtensions, MatchEnded,
        WrongPhase, StaleDecision, WrongActor, InvalidAction, InvalidAmount, CannotCheck, NothingToCall, CannotRaise,
        RaiseTooSmall, InsufficientChips, StaleWindow, AmountOverflow, InvalidShuffle, InvalidAbilityOperation }

    public enum MatchEventKind { AbilityWindowOpened = 1, HandStarted, BlindPosted, CardsDealt, PlayerActed, StreetStarted,
        DecisionOpened, CardsRevealed, PotAwarded, UncalledReturned, HandSettled, MatchCompleted, DecisionTimedOut,
        AbilityActivated, SelectionOpened, SelectionResolved }
    public enum EventAudience { Public = 1, Player }
    public sealed class EventCard
    {
        public CardInstanceId Id { get; }
        public CardDefinitionId DefinitionId { get; }
        public EventCard(CardInstance card) { Id = card.Id; DefinitionId = card.DefinitionId; }
    }
    public sealed class MatchEvent
    {
        public long Revision { get; }
        public int Index { get; }
        public MatchEventKind Kind { get; }
        public EventAudience Audience { get; }
        public ParticipantId? Recipient { get; }
        public ParticipantId? Player { get; }
        public Chips Amount { get; }
        public BettingAction? Action { get; }
        public Street? Street { get; }
        public string ReferenceId { get; }
        public IReadOnlyList<EventCard> Cards { get; }
        internal MatchEvent(long revision, int index, MatchEventKind kind, ParticipantId? player, Chips amount,
            BettingAction? action, Street? street, string referenceId, IEnumerable<EventCard> cards, ParticipantId? recipient)
        {
            Revision = revision; Index = index; Kind = kind; Player = player; Amount = amount; Action = action; Street = street;
            ReferenceId = referenceId; Cards = Guard.View(Guard.Copy(cards ?? new EventCard[0], nameof(cards)));
            Recipient = recipient; Audience = recipient.HasValue ? EventAudience.Player : EventAudience.Public;
        }
    }
    public sealed class TransitionResult
    {
        public bool Accepted => Error == CommandError.None;
        public string CommandId { get; }
        public CommandError Error { get; }
        public MatchState State { get; }
        public IReadOnlyList<MatchEvent> Events { get; }
        internal TransitionResult(string commandId, CommandError error, MatchState state, IEnumerable<MatchEvent> events)
        { CommandId = commandId; Error = error; State = state; Events = Guard.View(Guard.Copy(events, nameof(events))); }
        public IReadOnlyList<MatchEvent> EventsFor(ParticipantId viewer)
        {
            if (!State.Participants.Any(p => p.Id == viewer)) throw new ArgumentException("Unknown viewer.");
            return Guard.View(Events.Where(e => e.Audience == EventAudience.Public || e.Recipient == viewer).ToArray());
        }
    }

    public sealed class LegalBettingActions
    {
        public bool CanFold { get; }
        public bool CanCheck { get; }
        public bool CanCall { get; }
        public bool CanRaise { get; }
        public Chips CallCost { get; }
        public Chips? MinimumRaiseTo { get; }
        public Chips MaximumRaiseTo { get; }
        internal LegalBettingActions(bool fold, bool check, bool call, bool raise, Chips cost, Chips? min, Chips max)
        { CanFold = fold; CanCheck = check; CanCall = call; CanRaise = raise; CallCost = cost; MinimumRaiseTo = min; MaximumRaiseTo = max; }
        internal static LegalBettingActions None => new LegalBettingActions(false, false, false, false, Chips.Zero, null, Chips.Zero);
    }
}
