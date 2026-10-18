using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Standard no-limit Hold'em authority. Commands are atomic; ability windows are explicit resumable checkpoints.</summary>
    public sealed class HoldemMatchEngine
    {
        private readonly ContentCatalog catalog;
        private readonly IDeckShuffler shuffler;
        private readonly AbilityRuntime abilityRuntime;
        private readonly DeckEntry[] deckEntries;
        public HoldemMatchEngine(ContentCatalog catalog = null, IDeckShuffler shuffler = null, AbilityRuntime abilityRuntime = null,
            IEnumerable<DeckEntry> deckEntries = null)
        {
            this.catalog = catalog ?? StandardHoldem.Catalog(); this.shuffler = shuffler ?? new HmacDeckShuffler(); this.abilityRuntime = abilityRuntime;
            this.deckEntries = deckEntries == null ? null : Guard.Copy(deckEntries, nameof(deckEntries));
            if (this.deckEntries != null && (this.deckEntries.Length > 512 || this.deckEntries.Select(e => e.Id).Distinct().Count() != this.deckEntries.Length ||
                this.deckEntries.Any(e => !this.catalog.Contains(e.DefinitionId)))) throw new ArgumentException("Invalid match deck.");
        }

        public MatchState Create(MatchId id, MatchDefinition definition, TableState table, IEnumerable<ParticipantState> participants, RandomState random = null,
            IEnumerable<AbilityInstance> abilities = null)
        {
            var state = new MatchState(id, 0, definition, MatchStatus.Ready, table, participants, abilities ?? new AbilityInstance[0], new StatusInstance[0],
                null, null, ResolutionState.Empty, random ?? HmacDeckShuffler.CreateRandomState());
            StateValidator.Validate(state, catalog);
            if (Supported(state) != CommandError.None || state.Participants.Any(p => p.Status != ParticipantStatus.Active || p.Resources.Chips.Value <= 0))
                throw new ArgumentException("Initial match must use standard rules and at least two funded active players.");
            return state;
        }

        /// <summary>Caller supplies authenticated identity. The command itself does not nominate an actor.</summary>
        public TransitionResult Execute(MatchState state, BettingCommand command, ParticipantId authenticatedActor) =>
            Run(state, command, work => work.Bet(command, authenticatedActor));

        /// <summary>Authority-only API. Never expose system commands through the player command endpoint.</summary>
        public TransitionResult ExecuteSystem(MatchState state, SystemMatchCommand command)
        {
            if (command is DecisionTimeoutCommand && state?.ActiveDecision?.Kind == DecisionKind.SelectTargets)
                return RunAbility(state, command, state.ActiveDecision.ActorId, true);
            return Run(state, command, work => {
                if (command is StartHandCommand) work.Start();
                else if (command is AdvanceWindowCommand) work.Advance((AdvanceWindowCommand)command);
                else if (command is DecisionTimeoutCommand) work.Timeout((DecisionTimeoutCommand)command);
                else throw new Rejection(CommandError.InvalidAction);
            });
        }

        public TransitionResult Execute(MatchState state, AbilityCommand command, ParticipantId authenticatedActor) =>
            RunAbility(state, command, authenticatedActor, false);

        private TransitionResult RunAbility(MatchState state, MatchCommand command, ParticipantId actor, bool timeout)
        {
            if (state == null || command == null) throw new ArgumentNullException();
            try
            {
                if (command.ExpectedRevision != state.Revision) return Rejected(state, command, CommandError.StaleRevision);
                StateValidator.Validate(state, catalog);
                var supported = Supported(state);
                if (supported != CommandError.None) return Rejected(state, command, supported);
                if (abilityRuntime == null || state.Definition.RulesetId != AbilityHoldem.RulesetId) return Rejected(state, command, CommandError.UnsupportedExtensions);
                if (state.Status != MatchStatus.Running) return Rejected(state, command, CommandError.WrongPhase);
                var next = HoldemAbilityAdapter.WithRevision(state, checked(state.Revision + 1)); var events = new List<MatchEvent>();
                var executor = new AbilityExecutor(abilityRuntime, catalog);
                var world = HoldemAbilityAdapter.ToWorld(next); AbilityExecutionResult execution;
                if (timeout) execution = executor.Select(world, ((DecisionTimeoutCommand)command).DecisionId, actor, new string[0], false, true);
                else if (command is ActivateAbilityCommand)
                { var c = (ActivateAbilityCommand)command; execution = executor.Activate(world, c.WindowId, c.AbilityId, actor); }
                else if (command is SelectAbilityTargetsCommand)
                { var c = (SelectAbilityTargetsCommand)command; execution = executor.Select(world, c.DecisionId, actor, c.Keys, c.Skip); }
                else return Rejected(state, command, CommandError.InvalidAction);
                next = HoldemAbilityAdapter.ApplyResult(next, execution, events);
                StateValidator.Validate(next, catalog); supported = Supported(next);
                return supported == CommandError.None ? new TransitionResult(command.CommandId, CommandError.None, next, events) : Rejected(state, command, supported);
            }
            catch (OverflowException) { return Rejected(state, command, CommandError.AmountOverflow); }
            catch (StateValidationException) { return Rejected(state, command, CommandError.InvalidAbilityOperation); }
            catch (ArgumentException) { return Rejected(state, command, CommandError.InvalidAbilityOperation); }
            catch (InvalidOperationException) { return Rejected(state, command, CommandError.InvalidAbilityOperation); }
        }

        public LegalBettingActions GetLegalActions(MatchState state, ParticipantId actor)
        {
            StateValidator.Validate(state, catalog);
            if (Supported(state) != CommandError.None || state.Status != MatchStatus.Running ||
                state.AbilityWindow?.Timing != HoldemTiming.BeforeBettingAction || state.ActiveDecision?.Kind != DecisionKind.ModeAction || state.ActiveDecision.ActorId != actor)
                return LegalBettingActions.None;
            return new Work(this, state, state.Revision).Legal(actor);
        }

        private TransitionResult Run(MatchState state, MatchCommand command, Action<Work> action)
        {
            if (state == null || command == null) throw new ArgumentNullException();
            try
            {
                if (command.ExpectedRevision != state.Revision) throw new Rejection(CommandError.StaleRevision);
                StateValidator.Validate(state, catalog);
                var supported = Supported(state);
                if (supported != CommandError.None) throw new Rejection(supported);
                if (state.Status == MatchStatus.Completed || state.Status == MatchStatus.Aborted) throw new Rejection(CommandError.MatchEnded);
                var work = new Work(this, state, checked(state.Revision + 1));
                action(work);
                var next = work.Build(); StateValidator.Validate(next, catalog);
                if (next.Definition.RulesetId == AbilityHoldem.RulesetId)
                    next = HoldemAbilityAdapter.ApplyResult(next, new AbilityExecutor(abilityRuntime, catalog).Triggers(HoldemAbilityAdapter.ToWorld(next)), work.Events);
                StateValidator.Validate(next, catalog);
                supported = Supported(next); if (supported != CommandError.None) throw new Rejection(supported);
                return new TransitionResult(command.CommandId, CommandError.None, next, work.Events);
            }
            catch (Rejection e) { return Rejected(state, command, e.Error); }
            catch (StateValidationException) { return Rejected(state, command, CommandError.InvalidState); }
            catch (OverflowException) { return Rejected(state, command, CommandError.AmountOverflow); }
            catch (ArgumentException) { return Rejected(state, command, CommandError.InvalidAbilityOperation); }
            catch (InvalidOperationException) { return Rejected(state, command, CommandError.InvalidAbilityOperation); }
        }

        private static TransitionResult Rejected(MatchState state, MatchCommand command, CommandError error) =>
            new TransitionResult(command.CommandId, error, state, new MatchEvent[0]);

        private CommandError Supported(MatchState state)
        {
            var d = state.Definition;
            bool extended = d.RulesetId == AbilityHoldem.RulesetId && d.RulesetVersion == AbilityHoldem.RulesetVersion && abilityRuntime != null;
            if (deckEntries != null && (!extended || deckEntries.Length < state.Participants.Count * 2 + 8)) return CommandError.UnsupportedRules;
            if ((!extended && (d.RulesetId != StandardHoldem.RulesetId || d.RulesetVersion != StandardHoldem.RulesetVersion ||
                d.ContentVersion != StandardHoldem.ContentVersion)) || d.MaxSeats > 10) return CommandError.UnsupportedRules;
            foreach (var card in StandardHoldem.Cards())
                if (!catalog.Contains(card.Id) || catalog.Get(card.Id).Rank != card.Rank || catalog.Get(card.Id).Suit != card.Suit) return CommandError.UnsupportedRules;
            if (state.Resolution.PendingEffects.Count > 0 ||
                (!extended && (state.Abilities.Count > 0 || state.Statuses.Count > 0 || state.Knowledge.Count > 0 || state.ActiveDecision != null && state.ActiveDecision.Kind != DecisionKind.ModeAction)) ||
                (extended && (!abilityRuntime.Supports(HoldemAbilityAdapter.ToWorld(state), catalog) || state.ActiveDecision != null &&
                    state.ActiveDecision.Kind != DecisionKind.ModeAction && state.ActiveDecision.Kind != DecisionKind.SelectTargets))) return CommandError.UnsupportedExtensions;
            long total = 0;
            foreach (var p in state.Participants) total = checked(total + p.Resources.Chips.Value);
            var h = state.CurrentHand;
            if (h != null)
            {
                var definitions = new HashSet<CardDefinitionId>(StandardHoldem.Cards().Select(c => c.Id));
                bool validDeck = deckEntries == null ? h.Cards.Count == 52 && definitions.SetEquals(h.Cards.Select(c => c.DefinitionId)) :
                    h.Cards.Count == deckEntries.Length && h.Cards.All(c => deckEntries.Any(e => e.Id == c.OriginId && e.DefinitionId == c.DefinitionId));
                if (!validDeck ||
                    h.Zones.Where(z => z.Kind == HoldemZoneKinds.HoleCards).Any(z => z.Cards.Count != 2) ||
                    !extended && h.Zones.Any(z => (z.Kind == CardZoneKinds.Discard || z.Kind == CardZoneKinds.Removed) && z.Cards.Count > 0)) return CommandError.UnsupportedRules;
                int board = h.Zones.Single(z => z.Kind == HoldemZoneKinds.Board).Cards.Count;
                if (h.Stage == HandStage.Betting && (board != BoardCount(h.Betting.Street) || h.Betting.LastFullRaiseSize < d.BigBlind)) return CommandError.InvalidState;
                if (h.Stage == HandStage.Betting && h.Zones.Single(z => z.Kind == CardZoneKinds.Deck).Cards.Count < AbilityHoldem.RunoutCards(h.Betting.Street))
                    return CommandError.InvalidState;
                if (h.Stage == HandStage.Showdown && board != 5) return CommandError.InvalidState;
                if (h.Stage != HandStage.Settled) foreach (var p in h.Participants) total = checked(total + p.HandCommitted.Value);
            }
            return CommandError.None;
        }

        private static int BoardCount(Street street) => street == Street.Preflop ? 0 : street == Street.Flop ? 3 : street == Street.Turn ? 4 : 5;
        private sealed class Rejection : Exception
        { public CommandError Error { get; } public Rejection(CommandError error) { Error = error; } }

        private sealed class HandPlayer
        {
            public ParticipantId Id;
            public bool Folded, Acted;
            public long Total, Street, LastActionBet;
            public long? ReopenAt;
        }

        /// <summary>Per-command scratch state. No mutable collection is shared with the input snapshot.</summary>
        private sealed class Work
        {
            private readonly HoldemMatchEngine engine;
            private readonly MatchState original;
            private readonly long revision;
            private readonly List<ParticipantState> players;
            private List<AbilityInstance> abilities;
            private List<StatusInstance> statuses;
            private List<KnowledgeGrant> knowledge;
            private ResolutionState resolution;
            private MatchStatus status;
            private RandomState random;
            private HandId? handId;
            private HandStage stage;
            private SeatId dealer;
            private ParticipantId smallBlind, bigBlind;
            private Street street;
            private long betTo, lastFullRaise;
            private List<HandPlayer> handPlayers = new List<HandPlayer>();
            private List<ParticipantId> pending = new List<ParticipantId>();
            private List<CardInstance> cards = new List<CardInstance>();
            private List<CardZone> extraZones = new List<CardZone>();
            private CardZone deck, board, burn;
            private Dictionary<ParticipantId, CardZone> holes = new Dictionary<ParticipantId, CardZone>();
            private DecisionState decision;
            private AbilityWindowState window;
            private PotSettlement settlement;
            public readonly List<MatchEvent> Events = new List<MatchEvent>();

            public Work(HoldemMatchEngine engine, MatchState state, long revision)
            {
                this.engine = engine; original = state; this.revision = revision; players = state.Participants.ToList();
                abilities = state.Abilities.ToList(); statuses = state.Statuses.ToList(); knowledge = state.Knowledge.ToList(); resolution = state.Resolution;
                status = state.Status; random = state.Random; decision = state.ActiveDecision; window = state.AbilityWindow;
                var h = state.CurrentHand;
                if (h == null) return;
                handId = h.Id; stage = h.Stage; dealer = h.DealerSeatId; smallBlind = h.SmallBlindId; bigBlind = h.BigBlindId; settlement = h.Settlement;
                cards = h.Cards.ToList();
                deck = h.Zones.Single(z => z.Kind == CardZoneKinds.Deck); board = h.Zones.Single(z => z.Kind == HoldemZoneKinds.Board);
                burn = h.Zones.FirstOrDefault(z => z.Kind == HoldemZoneKinds.Burn) ??
                    new CardZone(new ZoneId("burn"), HoldemZoneKinds.Burn, null, new CardInstanceId[0]);
                holes = h.Zones.Where(z => z.Kind == HoldemZoneKinds.HoleCards).ToDictionary(z => z.ControllerId.Value);
                extraZones = h.Zones.Where(z => z.Kind == CardZoneKinds.Discard || z.Kind == CardZoneKinds.Removed).ToList();
                foreach (var p in h.Participants)
                {
                    var b = h.Betting?.Players.First(x => x.ParticipantId == p.ParticipantId);
                    handPlayers.Add(new HandPlayer { Id = p.ParticipantId, Folded = p.Participation == Participation.Folded,
                        Total = p.HandCommitted.Value, Street = p.StreetCommitted.Value, Acted = b?.HasActed ?? false,
                        LastActionBet = b?.BetToAtLastAction.Value ?? 0, ReopenAt = b?.ReopenAtBetTo?.Value });
                }
                if (h.Betting != null)
                { street = h.Betting.Street; betTo = h.Betting.CurrentBetTo.Value; lastFullRaise = h.Betting.LastFullRaiseSize.Value; pending = h.Betting.PendingActors.ToList(); }
            }

            private ParticipantState Player(ParticipantId id) => players.First(p => p.Id == id);
            private HandPlayer HandPlayer(ParticipantId id) => handPlayers.First(p => p.Id == id);
            private long Stack(ParticipantId id) => Player(id).Resources.Chips.Value;
            private bool CanAct(ParticipantId id) => !HandPlayer(id).Folded && Stack(id) > 0;
            private IEnumerable<ParticipantId> After(SeatId seat, IEnumerable<ParticipantId> ids)
            {
                var remaining = new HashSet<ParticipantId>(ids); var seats = original.Table.Seats; int start = seats.ToList().IndexOf(seat);
                for (int i = 1; i <= seats.Count; i++)
                {
                    var p = players.FirstOrDefault(x => x.SeatId == seats[(start + i) % seats.Count]);
                    if (p != null && remaining.Contains(p.Id)) yield return p.Id;
                }
            }
            private void Balance(ParticipantId id, long amount)
            {
                var p = Player(id);
                players[players.IndexOf(p)] = new ParticipantState(p.Id, p.SeatId, p.Status,
                    new ResourceState(new Chips(amount), p.Resources.Energy, p.Resources.Health));
            }
            private void Pay(ParticipantId id, long amount)
            {
                Balance(id, checked(Stack(id) - amount)); var p = HandPlayer(id);
                p.Total = checked(p.Total + amount); p.Street = checked(p.Street + amount);
            }
            private void Emit(MatchEventKind kind, ParticipantId? player = null, long amount = 0, BettingAction? action = null,
                Street? atStreet = null, string reference = null, IEnumerable<CardInstanceId> dealt = null, ParticipantId? recipient = null)
            {
                var eventCards = dealt?.Select(id => new EventCard(cards.First(c => c.Id == id)));
                Events.Add(new MatchEvent(revision, Events.Count, kind, player, new Chips(amount), action, atStreet, reference, eventCards, recipient));
            }
            private string NewId(string kind) => original.Id.Value + "/" + kind + "/" + revision.ToString(CultureInfo.InvariantCulture);
            private void OpenWindow(string timing, ParticipantId? actor = null, BettingAction? action = null)
            {
                resolution = ResolutionState.Empty;
                window = new AbilityWindowState(NewId("window"), timing, timing == HoldemTiming.BeforeHand ? null : handId?.Value, actor, action.HasValue ? HoldemActionKeys.Key(action.Value) : null);
                Emit(MatchEventKind.AbilityWindowOpened, actor, reference: window.Id);
            }

            public void Start()
            {
                if (window != null || decision != null || (handId.HasValue && stage != HandStage.Settled)) throw new Rejection(CommandError.WrongPhase);
                if (players.Count(p => p.Status == ParticipantStatus.Active && p.Resources.Chips.Value > 0) < 2) throw new Rejection(CommandError.WrongPhase);
                if (handId.HasValue)
                    abilities = abilities.Select(a => new AbilityInstance(a.Id, a.DefinitionId, a.OwnerId, a.Source, a.RemainingCharges, 0,
                        Math.Max(0, a.CooldownRounds - 1), a.Memory.ResetRound(), a.Order)).ToList();
                knowledge.Clear(); status = MatchStatus.Running; OpenWindow(HoldemTiming.BeforeHand);
            }

            public void Advance(AdvanceWindowCommand command)
            {
                if (decision != null && decision.Kind != DecisionKind.ModeAction) throw new Rejection(CommandError.WrongPhase);
                if (window == null || window.Id != command.WindowId) throw new Rejection(CommandError.StaleWindow);
                var timing = window.Timing;
                if (timing == HoldemTiming.BeforeBettingAction) throw new Rejection(CommandError.WrongPhase);
                window = null; resolution = ResolutionState.Empty;
                switch (timing)
                {
                    case HoldemTiming.BeforeHand: DealHand(); break;
                    case HoldemTiming.AfterBettingAction:
                    case HoldemTiming.AfterBoardDealt: Progress(); break;
                    case HoldemTiming.BeforeShowdown: window = original.AbilityWindow; resolution = original.Resolution; Settle(); break;
                    case HoldemTiming.AfterHandSettled: FinishHand(); break;
                    default: throw new Rejection(CommandError.WrongPhase);
                }
            }

            private void DealHand()
            {
                var active = players.Where(p => p.Status == ParticipantStatus.Active && p.Resources.Chips.Value > 0).Select(p => p.Id).ToArray();
                if (active.Length < 2) throw new Rejection(CommandError.WrongPhase);
                var nextDealer = handId.HasValue ? After(dealer, active).First() : players.Where(p => active.Contains(p.Id))
                    .OrderBy(p => original.Table.Seats.ToList().IndexOf(p.SeatId)).First().Id;
                dealer = Player(nextDealer).SeatId;
                var order = After(dealer, active).ToArray(); smallBlind = active.Length == 2 ? nextDealer : order[0];
                bigBlind = After(Player(smallBlind).SeatId, active).First();
                handId = new HandId(NewId("hand")); stage = HandStage.Betting; street = Street.Preflop; settlement = null;
                statuses.RemoveAll(s => s.Duration.Kind == DurationKind.UntilRoundEnd || s.Duration.Kind == DurationKind.UntilPhaseEnd ||
                    s.Source.Kind == EntityKind.Card || s.Target.Kind == EntityKind.Card);
                var expiredAbilities = new HashSet<AbilityInstanceId>(abilities.Where(a => a.Source.Kind == EntityKind.Card).Select(a => a.Id));
                bool expanded;
                do
                {
                    expanded = false;
                    foreach (var a in abilities.Where(a => a.Source.Kind == EntityKind.Ability))
                        if (expiredAbilities.Contains(new AbilityInstanceId(a.Source.Id))) expanded |= expiredAbilities.Add(a.Id);
                } while (expanded);
                abilities.RemoveAll(a => expiredAbilities.Contains(a.Id));
                abilities = abilities.Select(a => new AbilityInstance(a.Id, a.DefinitionId, a.OwnerId, a.Source, a.RemainingCharges,
                    a.UsesThisRound, a.CooldownRounds, new AbilityMemory(a.Memory.Counters, a.Memory.Marks.Where(m =>
                        m.Target.Kind != EntityKind.Ability || !expiredAbilities.Contains(new AbilityInstanceId(m.Target.Id)))), a.Order)).ToList();
                statuses.RemoveAll(s => s.Source.Kind == EntityKind.Ability && expiredAbilities.Any(id => id.Value == s.Source.Id) ||
                    s.Target.Kind == EntityKind.Ability && expiredAbilities.Any(id => id.Value == s.Target.Id));
                handPlayers = active.Select(id => new HandPlayer { Id = id }).ToList();
                var entries = engine.deckEntries ?? StandardHoldem.Cards().Select((c, i) => new DeckEntry("standard-" + i, c.Id)).ToArray();
                cards = entries.Select((c, i) => new CardInstance(new CardInstanceId(handId.Value.Value + "/card/" + i), c.DefinitionId,
                    engine.deckEntries == null ? null : c.Id, c.Effects, c.IsDisabled)).ToList();
                ShuffleResult shuffled;
                try { shuffled = engine.shuffler.Shuffle(cards.Select(c => c.Id).ToArray(), random); }
                catch (ArgumentException) { throw new Rejection(CommandError.InvalidShuffle); }
                if (shuffled == null || shuffled.Cards.Count != cards.Count || shuffled.Cards.Distinct().Count() != cards.Count ||
                    !new HashSet<CardInstanceId>(cards.Select(c => c.Id)).SetEquals(shuffled.Cards)) throw new Rejection(CommandError.InvalidShuffle);
                // Assign final identities after shuffling: an instance ID must not encode its card definition.
                var shuffledCards = shuffled.Cards.Select(id => cards.First(c => c.Id == id)).ToArray();
                cards = shuffledCards.Select((card, i) => new CardInstance(
                    new CardInstanceId(handId.Value.Value + "/card/" + i.ToString(CultureInfo.InvariantCulture)), card.DefinitionId, card.OriginId, card.Effects, card.IsDisabled)).ToList();
                extraZones = new List<CardZone> { new CardZone(new ZoneId("discard"), CardZoneKinds.Discard, null, new CardInstanceId[0]),
                    new CardZone(new ZoneId("removed"), CardZoneKinds.Removed, null, new CardInstanceId[0]) };
                random = shuffled.Random;
                deck = new CardZone(new ZoneId("deck"), CardZoneKinds.Deck, null, cards.Select(c => c.Id));
                board = new CardZone(new ZoneId("board"), HoldemZoneKinds.Board, null, new CardInstanceId[0]);
                burn = new CardZone(new ZoneId("burn"), HoldemZoneKinds.Burn, null, new CardInstanceId[0]);
                holes = active.ToDictionary(id => id, id => new CardZone(new ZoneId("hole:" + id.Value), HoldemZoneKinds.HoleCards, id, new CardInstanceId[0]));
                Emit(MatchEventKind.HandStarted, nextDealer, reference: handId.Value.Value);
                Pay(smallBlind, Math.Min(Stack(smallBlind), original.Definition.SmallBlind.Value));
                Emit(MatchEventKind.BlindPosted, smallBlind, HandPlayer(smallBlind).Street);
                Pay(bigBlind, Math.Min(Stack(bigBlind), original.Definition.BigBlind.Value));
                Emit(MatchEventKind.BlindPosted, bigBlind, HandPlayer(bigBlind).Street);
                for (int round = 0; round < 2; round++) foreach (var id in order) holes[id] = DealTo(holes[id], 1).Target;
                foreach (var id in order) Emit(MatchEventKind.CardsDealt, id, dealt: holes[id].Cards, recipient: id);
                betTo = original.Definition.BigBlind.Value; lastFullRaise = betTo;
                pending = After(Player(bigBlind).SeatId, active).Where(CanAct).ToList(); Progress();
            }

            private CardZoneTransfer DealTo(CardZone target, int count)
            {
                if (deck.Cards.Count < count) throw new Rejection(CommandError.InvalidState);
                var transfer = deck.DrawTo(target, count, target.Cards.Count);
                deck = transfer.Source;
                return transfer;
            }

            public LegalBettingActions Legal(ParticipantId actor)
            {
                var p = HandPlayer(actor); long stack = Stack(actor), call = Math.Max(0, betTo - p.Street), max = checked(stack + p.Street);
                bool reopened = !p.Acted || (p.ReopenAt.HasValue && betTo >= p.ReopenAt.Value);
                bool opponent = handPlayers.Any(x => x.Id != actor && !x.Folded && Stack(x.Id) > 0);
                Chips? min = betTo <= long.MaxValue - lastFullRaise ? (Chips?)new Chips(betTo + lastFullRaise) : null;
                return new LegalBettingActions(true, call == 0, call > 0, reopened && opponent && max > betTo,
                    new Chips(Math.Min(call, stack)), min, new Chips(max));
            }

            public void Bet(BettingCommand command, ParticipantId actor)
            {
                if (status != MatchStatus.Running || stage != HandStage.Betting || window?.Timing != HoldemTiming.BeforeBettingAction || decision?.Kind != DecisionKind.ModeAction)
                    throw new Rejection(CommandError.WrongPhase);
                if (command.DecisionId != decision.Id) throw new Rejection(CommandError.StaleDecision);
                if (decision.ActorId != actor) throw new Rejection(CommandError.WrongActor);
                if (!Enum.IsDefined(typeof(BettingAction), command.Action)) throw new Rejection(CommandError.InvalidAction);
                if (command.Action != BettingAction.RaiseTo && command.RaiseTo != 0) throw new Rejection(CommandError.InvalidAmount);
                var legal = Legal(actor); var p = HandPlayer(actor); long paid = 0;
                switch (command.Action)
                {
                    case BettingAction.Fold: p.Folded = true; break;
                    case BettingAction.Check:
                        if (!legal.CanCheck) throw new Rejection(CommandError.CannotCheck); break;
                    case BettingAction.Call:
                        if (!legal.CanCall) throw new Rejection(CommandError.NothingToCall);
                        paid = legal.CallCost.Value; Pay(actor, paid); break;
                    case BettingAction.RaiseTo:
                        if (!legal.CanRaise) throw new Rejection(CommandError.CannotRaise);
                        if (command.RaiseTo <= betTo) throw new Rejection(CommandError.InvalidAmount);
                        if (command.RaiseTo > legal.MaximumRaiseTo.Value) throw new Rejection(CommandError.InsufficientChips);
                        if ((!legal.MinimumRaiseTo.HasValue || command.RaiseTo < legal.MinimumRaiseTo.Value.Value) && command.RaiseTo != legal.MaximumRaiseTo.Value)
                            throw new Rejection(CommandError.RaiseTooSmall);
                        long raise = command.RaiseTo - betTo;
                        if (raise >= lastFullRaise) lastFullRaise = raise;
                        paid = command.RaiseTo - p.Street; Pay(actor, paid); betTo = command.RaiseTo; break;
                }
                p.Acted = true; p.LastActionBet = betTo;
                p.ReopenAt = betTo <= long.MaxValue - lastFullRaise ? (long?)(betTo + lastFullRaise) : null;
                if (command.Action == BettingAction.RaiseTo)
                    pending = After(Player(actor).SeatId, handPlayers.Select(x => x.Id)).Where(id => id != actor && CanAct(id) && (!HandPlayer(id).Acted || HandPlayer(id).Street < betTo)).ToList();
                else pending.Remove(actor);
                decision = null; Emit(MatchEventKind.PlayerActed, actor, paid, command.Action, street);
                OpenWindow(HoldemTiming.AfterBettingAction, actor, command.Action);
            }

            public void Timeout(DecisionTimeoutCommand command)
            {
                if (decision == null || command.DecisionId != decision.Id) throw new Rejection(CommandError.StaleDecision);
                var actor = decision.ActorId; var action = Legal(actor).CanCheck ? BettingAction.Check : BettingAction.Fold;
                Emit(MatchEventKind.DecisionTimedOut, actor, reference: decision.Id.Value);
                Bet(new BettingCommand(command.CommandId, command.ExpectedRevision, command.DecisionId, action), actor);
            }

            private void Progress()
            {
                var live = handPlayers.Where(p => !p.Folded).ToArray();
                if (live.Length == 1) { statuses.RemoveAll(s => s.Duration.Kind == DurationKind.UntilPhaseEnd); Settle(); return; }
                pending = pending.Where(CanAct).ToList();
                var funded = live.Where(p => Stack(p.Id) > 0).ToArray();
                if (funded.Length <= 1)
                {
                    pending.Clear();
                    if (funded.Length == 1)
                    {
                        long facing = live.Where(p => p.Id != funded[0].Id).Max(p => p.Street);
                        if (funded[0].Street < facing) { betTo = Math.Max(facing, handPlayers.Max(p => p.Street)); pending.Add(funded[0].Id); }
                    }
                }
                if (pending.Count > 0)
                {
                    decision = DecisionState.ModeAction(new DecisionId(NewId("decision")), pending[0]);
                    OpenWindow(HoldemTiming.BeforeBettingAction, pending[0]); Emit(MatchEventKind.DecisionOpened, pending[0], reference: decision.Id.Value); return;
                }
                decision = null;
                statuses.RemoveAll(s => s.Duration.Kind == DurationKind.UntilPhaseEnd && s.Duration.PhaseKey == HoldemAbilityAdapter.Phase(street));
                knowledge.RemoveAll(g => g.UntilPhaseEnd != null);
                if (street == Street.River)
                { stage = HandStage.Showdown; OpenWindow(HoldemTiming.BeforeShowdown); return; }
                street = (Street)((int)street + 1); burn = DealTo(burn, 1).Target;
                var dealt = DealTo(board, street == Street.Flop ? 3 : 1); board = dealt.Target;
                foreach (var p in handPlayers) { p.Street = 0; p.Acted = false; p.LastActionBet = 0; p.ReopenAt = null; }
                betTo = 0; lastFullRaise = original.Definition.BigBlind.Value;
                pending = After(dealer, handPlayers.Select(p => p.Id)).Where(CanAct).ToList();
                Emit(MatchEventKind.StreetStarted, atStreet: street); Emit(MatchEventKind.CardsDealt, atStreet: street, dealt: dealt.Cards);
                OpenWindow(HoldemTiming.AfterBoardDealt);
            }

            private void Settle()
            {
                var live = handPlayers.Where(p => !p.Folded).ToArray(); var evaluations = new List<HandEvaluation>();
                if (live.Length > 1)
                {
                    if (board.Cards.Count != 5) throw new Rejection(CommandError.InvalidState);
                    foreach (var p in live)
                    {
                        var input = holes[p.Id].Cards.Concat(board.Cards).Select(id => {
                            var card = cards.First(c => c.Id == id); var definition = engine.catalog.Get(card.DefinitionId);
                            var face = engine.abilityRuntime == null ? new CardFace(definition.Rank, definition.Suit) : engine.abilityRuntime.Resolve(HoldemAbilityAdapter.ToWorld(Build()), engine.catalog, id);
                            return new EffectiveCard(id, face.Rank, face.Suit);
                        });
                        var evaluation = StandardHandEvaluator.Evaluate(p.Id, input);
                        evaluation = engine.abilityRuntime == null ? evaluation : engine.abilityRuntime.ModifyOutcome(HoldemAbilityAdapter.ToWorld(Build()), engine.catalog, evaluation);
                        if (evaluation == null || evaluation.ParticipantId != p.Id) throw new Rejection(CommandError.InvalidAbilityOperation);
                        var faces = evaluation.SelectedCards.Select(id => new EvaluatedCardFace(id, engine.abilityRuntime == null ?
                            new BaseCardFaceResolver().Resolve(HoldemAbilityAdapter.ToWorld(Build()), engine.catalog, id) : engine.abilityRuntime.Resolve(HoldemAbilityAdapter.ToWorld(Build()), engine.catalog, id)));
                        evaluations.Add(new HandEvaluation(evaluation.ParticipantId, evaluation.Category, evaluation.TieBreakRanks,
                            evaluation.SelectedCards, evaluation.AppliedModifiers, faces));
                        Emit(MatchEventKind.CardsRevealed, p.Id, dealt: holes[p.Id].Cards);
                    }
                }
                var breakdown = PotCalculator.Calculate(Contributions()); var awards = new List<PotAward>();
                for (int index = 0; index < breakdown.Pots.Count; index++)
                {
                    var pot = breakdown.Pots[index]; var eligible = pot.EligibleWinners.ToArray();
                    if (eligible.Length == 0) throw new Rejection(CommandError.InvalidState);
                    ParticipantId[] winners;
                    if (eligible.Length == 1) winners = eligible;
                    else
                    {
                        var ranked = evaluations.Where(e => eligible.Contains(e.ParticipantId)).ToArray();
                        var best = ranked.Aggregate((a, b) => StandardHandEvaluator.Compare(a, b) >= 0 ? a : b);
                        winners = ranked.Where(e => StandardHandEvaluator.Compare(e, best) == 0).Select(e => e.ParticipantId).ToArray();
                    }
                    long each = pot.Amount.Value / winners.Length, remainder = pot.Amount.Value % winners.Length;
                    foreach (var winner in After(dealer, winners))
                    {
                        long amount = each + (remainder-- > 0 ? 1 : 0);
                        if (amount == 0) continue;
                        Balance(winner, checked(Stack(winner) + amount)); awards.Add(new PotAward(index, winner, new Chips(amount)));
                        Emit(MatchEventKind.PotAwarded, winner, amount, reference: index.ToString(CultureInfo.InvariantCulture));
                    }
                }
                foreach (var refund in breakdown.Refunds)
                { Balance(refund.ParticipantId, checked(Stack(refund.ParticipantId) + refund.Amount.Value)); Emit(MatchEventKind.UncalledReturned, refund.ParticipantId, refund.Amount.Value); }
                settlement = new PotSettlement(handId.Value.Value + "/settlement", evaluations, awards, breakdown.Refunds);
                stage = HandStage.Settled; pending.Clear(); decision = null;
                Emit(MatchEventKind.HandSettled, reference: settlement.SettlementId); OpenWindow(HoldemTiming.AfterHandSettled);
            }

            private void FinishHand()
            {
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p.Status == ParticipantStatus.Active && p.Resources.Chips == Chips.Zero)
                        players[i] = new ParticipantState(p.Id, p.SeatId, ParticipantStatus.Eliminated, p.Resources);
                }
                var active = players.Where(p => p.Status == ParticipantStatus.Active && p.Resources.Chips.Value > 0).ToArray();
                if (active.Length < 2) { status = MatchStatus.Completed; Emit(MatchEventKind.MatchCompleted, active.Length == 1 ? (ParticipantId?)active[0].Id : null); }
            }

            private IEnumerable<HandParticipantState> Contributions() => handPlayers.Select(p => new HandParticipantState(p.Id,
                p.Folded ? Participation.Folded : Participation.InHand, new Chips(p.Total), new Chips(p.Street)));

            public MatchState Build()
            {
                HandState hand = null;
                if (handId.HasValue)
                {
                    var zones = new List<CardZone> { deck, board, burn };
                    zones.AddRange(handPlayers.Select(p => holes[p.Id]));
                    zones.AddRange(extraZones);
                    var betting = stage != HandStage.Betting ? null : new BettingRoundState(street, new Chips(betTo), new Chips(lastFullRaise), pending,
                        handPlayers.Select(p => new BettingPlayerState(p.Id, p.Acted, new Chips(p.LastActionBet), p.ReopenAt.HasValue ? (Chips?)new Chips(p.ReopenAt.Value) : null)));
                    hand = new HandState(handId.Value, stage, dealer, smallBlind, bigBlind, Contributions(), cards, zones, betting, settlement);
                }
                return new MatchState(original.Id, revision, original.Definition, status, original.Table, players, abilities, statuses,
                    hand, decision, resolution, random, window, knowledge);
            }
        }
    }
}
