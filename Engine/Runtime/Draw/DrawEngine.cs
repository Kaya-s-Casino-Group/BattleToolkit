using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Independent select/play/discard encounter. No table, betting, pot, or hold'em state is used.</summary>
    public sealed partial class DrawEngine
    {
        private readonly ContentCatalog catalog;
        private readonly DrawDefinition definition;
        private readonly AbilityRuntime runtime;
        private readonly IDeckShuffler shuffler;
        public DrawDefinition Definition => definition;
        public ContentCatalog Catalog => catalog;
        public DrawEngine(ContentCatalog catalog, DrawDefinition definition, AbilityRuntime runtime = null, IDeckShuffler shuffler = null)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog)); this.definition = definition ?? throw new ArgumentNullException(nameof(definition));
            this.runtime = runtime ?? new AbilityRuntime(new AbilityBehavior[0]); this.shuffler = shuffler ?? new HmacDeckShuffler();
        }

        public DrawState Create(string id, ParticipantId player, IEnumerable<DrawCard> deck, RandomState random,
            IEnumerable<AbilityInstance> abilities = null, ResourceState resources = null)
        {
            Guard.Text(id, nameof(id)); var cards = Guard.Copy(deck, nameof(deck));
            if (!player.IsValid || cards.Length < definition.HandSize || cards.Length > 512 || cards.Select(c => c.Card.Id).Distinct().Count() != cards.Length)
                throw new ArgumentException("A unique deck large enough for the starting hand is required.");
            var shuffled = shuffler.Shuffle(cards.Select(c => c.Card.Id).ToArray(), random);
            var zones = new[] {
                new CardZone(Zone(id, CardZoneKinds.Deck), CardZoneKinds.Deck, null, shuffled.Cards),
                new CardZone(Zone(id, CardZoneKinds.Hand), CardZoneKinds.Hand, player, new CardInstanceId[0]),
                new CardZone(Zone(id, CardZoneKinds.Played), CardZoneKinds.Played, player, new CardInstanceId[0]),
                new CardZone(Zone(id, CardZoneKinds.Discard), CardZoneKinds.Discard, null, new CardInstanceId[0]),
                new CardZone(Zone(id, CardZoneKinds.Removed), CardZoneKinds.Removed, null, new CardInstanceId[0]) };
            var world = new AbilityWorld(id, 0, true, MatchMode.Solo, id + "/play/0", DrawPhase.Ready.ToString(), player,
                new[] { new AbilityParticipant(player, true, resources ?? new ResourceState(Chips.Zero, 0)) }, cards.Select(c => c.Card), zones,
                abilities ?? new AbilityInstance[0], new StatusInstance[0], null, ResolutionState.Empty, null, new KnowledgeGrant[0]);
            var state = new DrawState(id, 0, catalog.Version, definition.Version, definition.Scoring.Version, player, DrawPhase.Ready,
                definition.Hands, definition.Discards, 0, world, shuffled.Random, cards, new ScoreResult[0], new CardInstanceId[0]);
            var work = new Work(this, state, 0); work.Refill(); work.Open(DrawTimings.BeforeAction); work.Trigger();
            var result = work.Build(); Validate(result); return result;
        }

        public DrawTransitionResult Execute(DrawState state, DrawCommand command, ParticipantId authenticatedActor)
        {
            if (state == null || command == null) throw new ArgumentNullException("State and command are required.");
            try { Validate(state); } catch (ArgumentException) { return new DrawTransitionResult(state, DrawError.InvalidState); }
            catch (OverflowException) { return new DrawTransitionResult(state, DrawError.InvalidState); }
            catch (InvalidOperationException) { return new DrawTransitionResult(state, DrawError.InvalidState); }
            if (command.ExpectedRevision != state.Revision) return new DrawTransitionResult(state, DrawError.StaleRevision);
            if (authenticatedActor != state.PlayerId) return new DrawTransitionResult(state, DrawError.Unauthorized);
            if (state.Phase == DrawPhase.Won || state.Phase == DrawPhase.Lost || command.Action == DrawAction.Timeout || !Enum.IsDefined(typeof(DrawAction), command.Action))
                return new DrawTransitionResult(state, DrawError.InvalidAction);
            try
            {
                var work = new Work(this, state, checked(state.Revision + 1));
                if (command.Action == DrawAction.ActivateAbility)
                {
                    if (work.Phase != DrawPhase.Ready) return new DrawTransitionResult(state, DrawError.InvalidAction);
                    work.Apply(work.Executor.Activate(work.World, command.WindowId, command.AbilityId, authenticatedActor));
                }
                else if (command.Action == DrawAction.SelectAbilityTargets)
                    work.Apply(work.Executor.Select(work.World, command.DecisionId, authenticatedActor, command.SelectionKeys, command.Skip));
                else if (command.Action == DrawAction.Continue)
                {
                    if (work.Phase != DrawPhase.Scored || work.World.ActiveDecision != null) return new DrawTransitionResult(state, DrawError.InvalidAction);
                    work.NextRound();
                }
                else
                {
                    if (work.Phase != DrawPhase.Ready || work.World.ActiveDecision != null) return new DrawTransitionResult(state, DrawError.InvalidAction);
                    if (command.Cards.Count < 1 || command.Cards.Count > definition.MaxSelection || command.Cards.Distinct().Count() != command.Cards.Count ||
                        command.Cards.Any(card => !work.ZoneCards(CardZoneKinds.Hand).Contains(card))) return new DrawTransitionResult(state, DrawError.InvalidSelection);
                    if (command.Cards.Any(id => {
                        var card = work.World.Cards.Single(c => c.Id == id);
                        return command.Action == DrawAction.Play ? !card.CanPlay : !card.CanDiscard;
                    })) return new DrawTransitionResult(state, DrawError.InvalidSelection);
                    if (command.Action == DrawAction.Play) work.Play(command.Cards);
                    else if (work.DiscardsRemaining == 0) return new DrawTransitionResult(state, DrawError.InvalidAction);
                    else work.Discard(command.Cards);
                }
                work.Progress(); var result = work.Build(); Validate(result);
                return new DrawTransitionResult(result, DrawError.None, work.Events);
            }
            catch (OverflowException) { return new DrawTransitionResult(state, DrawError.Overflow); }
            catch (ArgumentException) { return new DrawTransitionResult(state, command.Action == DrawAction.ActivateAbility || command.Action == DrawAction.SelectAbilityTargets ? DrawError.InvalidAbility : DrawError.InvalidAction); }
            catch (InvalidOperationException) { return new DrawTransitionResult(state, DrawError.InvalidAction); }
        }

        /// <summary>Authority-only timeout for a saved ability choice; it uses the exact same continuation as a player selection.</summary>
        public DrawTransitionResult ResolveTimeout(DrawState state, long expectedRevision, DecisionId decisionId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            try
            {
                Validate(state);
                if (state.Revision != expectedRevision) return new DrawTransitionResult(state, DrawError.StaleRevision);
                var work = new Work(this, state, checked(state.Revision + 1));
                work.Apply(work.Executor.Select(work.World, decisionId, state.PlayerId, new string[0], false, true));
                work.Progress(); var result = work.Build(); Validate(result); return new DrawTransitionResult(result, DrawError.None, work.Events);
            }
            catch (OverflowException) { return new DrawTransitionResult(state, DrawError.Overflow); }
            catch (ArgumentException) { return new DrawTransitionResult(state, DrawError.InvalidAbility); }
            catch (InvalidOperationException) { return new DrawTransitionResult(state, DrawError.InvalidAbility); }
        }

        /// <summary>Composition may synchronize portable ability counters at a complete scoring checkpoint.</summary>
        public DrawState SyncAbilities(DrawState state, IEnumerable<AbilityInstance> abilities)
        {
            Validate(state);
            if (state.Phase != DrawPhase.Scored && state.Phase != DrawPhase.Won && state.Phase != DrawPhase.Lost)
                throw new ArgumentException("Build synchronization requires a settled checkpoint.");
            var work = new Work(this, state, state.Revision); work.CopyWorld(abilities: abilities); var result = work.Build(); Validate(result); return result;
        }

        public static ZoneId Zone(string encounterId, string kind) => new ZoneId(encounterId + "/zone/" + kind);
        public IReadOnlyList<DrawEvent> EventsFor(DrawState state, IEnumerable<DrawEvent> events, ParticipantId viewer) =>
            viewer == state.PlayerId ? Guard.View(Guard.Copy(events, nameof(events))) : Guard.View(new DrawEvent[0]);

        private sealed class Work
        {
            private readonly DrawEngine engine;
            private readonly DrawState source;
            private readonly long revision;
            public DrawPhase Phase;
            public int HandsRemaining, DiscardsRemaining;
            public long Score;
            public AbilityWorld World;
            private CardInstanceId[] pending;
            private PokerHandClassification lastPattern;
            private readonly List<ScoreResult> scores;
            public readonly List<DrawEvent> Events = new List<DrawEvent>();
            public AbilityExecutor Executor => new AbilityExecutor(engine.runtime, engine.catalog);
            public Work(DrawEngine engine, DrawState source, long revision)
            {
                this.engine = engine; this.source = source; this.revision = revision; Phase = source.Phase;
                HandsRemaining = source.HandsRemaining; DiscardsRemaining = source.DiscardsRemaining; Score = source.Score;
                World = source.World; pending = source.PendingCards.ToArray(); lastPattern = source.LastPattern; scores = source.Scores.ToList(); CopyWorld();
            }
            public DrawState Build() => new DrawState(source.Id, revision, source.ContentVersion, source.RulesVersion, source.ScoreVersion, source.PlayerId,
                Phase, HandsRemaining, DiscardsRemaining, Score, World, source.Random, source.Cards, scores, pending, lastPattern);
            public void CopyWorld(IEnumerable<CardZone> zones = null, IEnumerable<AbilityInstance> abilities = null,
                IEnumerable<StatusInstance> statuses = null, IEnumerable<KnowledgeGrant> knowledge = null)
            {
                bool complete = Phase == DrawPhase.Won || Phase == DrawPhase.Lost;
                World = new AbilityWorld(World.Id, revision, !complete, World.Mode, World.ScopeId, Phase.ToString(), complete ? (ParticipantId?)null : source.PlayerId,
                    World.Participants, World.Cards, zones ?? World.Zones, abilities ?? World.Abilities, statuses ?? World.Statuses,
                    World.ActiveDecision, World.Resolution, World.AbilityWindow, knowledge ?? World.Knowledge,
                    World.AllowChipChanges, World.AllowCardChanges, World.AllowKnowledgeProjection);
            }
            public IReadOnlyList<CardInstanceId> ZoneCards(string kind) => World.Zones.Single(z => z.Kind == kind).Cards;
            private void Emit(DrawEventKind kind, IEnumerable<CardInstanceId> cards = null, ScoreResult score = null) => Events.Add(new DrawEvent(revision, Events.Count, kind, cards, score));
            public void Apply(AbilityExecutionResult result)
            {
                World = result.World;
                foreach (var item in result.Events) Events.Add(new DrawEvent(revision, Events.Count, DrawEventKind.Ability, ability: item));
            }
            public void Trigger() => Apply(Executor.Triggers(World));
            public void Open(string timing)
            {
                World = AbilityLifecycle.EndPhase(World, World.PhaseKey);
                var phase = Phase.ToString();
                var window = new AbilityWindowState(source.Id + "/window/" + revision + "/" + timing, timing, World.ScopeId, source.PlayerId, timing);
                World = new AbilityWorld(World.Id, revision, true, World.Mode, World.ScopeId, phase, source.PlayerId, World.Participants, World.Cards,
                    World.Zones, World.Abilities, World.Statuses, null, ResolutionState.Empty, window, World.Knowledge,
                    World.AllowChipChanges, World.AllowCardChanges, World.AllowKnowledgeProjection);
            }
            private void Close()
            {
                World = AbilityLifecycle.EndRound(World);
                bool complete = Phase == DrawPhase.Won || Phase == DrawPhase.Lost;
                World = new AbilityWorld(World.Id, revision, !complete, World.Mode, World.ScopeId, Phase.ToString(), complete ? (ParticipantId?)null : source.PlayerId,
                    World.Participants, World.Cards, World.Zones, World.Abilities, World.Statuses, null, ResolutionState.Empty, null, World.Knowledge,
                    World.AllowChipChanges, World.AllowCardChanges, World.AllowKnowledgeProjection);
            }
            private void Move(IEnumerable<CardInstanceId> cards, string from, string to)
            {
                var source = World.Zones.Single(z => z.Kind == from);
                var target = World.Zones.Single(z => z.Kind == to);
                ApplyTransfer(source.TransferRangeTo(target, cards, target.Cards.Count));
            }
            private void ApplyTransfer(CardZoneTransfer transfer) => CopyWorld(zones: World.Zones.Select(z =>
                z.Id == transfer.Source.Id ? transfer.Source : z.Id == transfer.Target.Id ? transfer.Target : z));
            public void Refill()
            {
                var deck = World.Zones.Single(z => z.Kind == CardZoneKinds.Deck);
                var hand = World.Zones.Single(z => z.Kind == CardZoneKinds.Hand);
                int count = Math.Min(Math.Max(0, engine.definition.HandSize - hand.Cards.Count), deck.Cards.Count);
                var transfer = deck.DrawTo(hand, count, hand.Cards.Count); ApplyTransfer(transfer);
                if (count > 0) Emit(DrawEventKind.CardsDrawn, transfer.Cards);
            }
            public void Play(IReadOnlyList<CardInstanceId> cards)
            {
                pending = cards.ToArray(); Move(pending, CardZoneKinds.Hand, CardZoneKinds.Played); Phase = DrawPhase.BeforeScoring;
                Emit(DrawEventKind.CardsPlayed, pending); Open(DrawTimings.BeforeScoring); Trigger();
            }
            public void Discard(IReadOnlyList<CardInstanceId> cards)
            {
                pending = cards.ToArray(); Move(pending, CardZoneKinds.Hand, CardZoneKinds.Discard); DiscardsRemaining--; Phase = DrawPhase.AfterDiscard;
                Emit(DrawEventKind.CardsDiscarded, pending); Open(DrawTimings.AfterDiscard); Trigger();
            }
            public void Progress()
            {
                if (World.ActiveDecision != null) return;
                if (Phase == DrawPhase.BeforeScoring)
                {
                    if (!new HashSet<CardInstanceId>(ZoneCards(CardZoneKinds.Played)).SetEquals(pending)) throw new ArgumentException("The submitted play must remain in its play zone until scored.");
                    var cards = pending.Select(id => source.Cards.Single(c => c.Card.Id == id).WithFace(engine.runtime.Resolve(World, engine.catalog, id))).ToArray();
                    lastPattern = PokerHandClassifier.Classify(cards.Select(c => new EffectiveCard(new CardInstanceId(c.Id), c.Face.Rank, c.Face.Suit)));
                    var score = ScoreEngine.EvaluatePattern(source.Id + "/score/" + scores.Count, engine.definition.Scoring, lastPattern,
                        engine.definition.Level(lastPattern.Category), cards, engine.definition.Relics.Select(j => j.At(scores.Count)));
                    score = engine.runtime.ModifyOutcome(World, engine.catalog, score); ScoreEngine.Validate(score);
                    if (score.Id != source.Id + "/score/" + scores.Count) throw new ArgumentException("Score modifiers must preserve result identity.");
                    scores.Add(score); Score = checked(Score + score.Total); HandsRemaining--; Emit(DrawEventKind.Scored, pending, score);
                    Phase = DrawPhase.AfterScoring; Open(DrawTimings.AfterScoring); Trigger();
                    if (World.ActiveDecision != null) return;
                }
                if (Phase == DrawPhase.AfterScoring)
                {
                    Move(ZoneCards(CardZoneKinds.Played), CardZoneKinds.Played, CardZoneKinds.Discard); pending = new CardInstanceId[0];
                    Phase = Score >= engine.definition.TargetScore ? DrawPhase.Won : HandsRemaining == 0 || ZoneCards(CardZoneKinds.Hand).Count + ZoneCards(CardZoneKinds.Deck).Count == 0 ? DrawPhase.Lost : DrawPhase.Scored;
                    Close(); if (Phase == DrawPhase.Won || Phase == DrawPhase.Lost) Emit(Phase == DrawPhase.Won ? DrawEventKind.Won : DrawEventKind.Lost);
                }
                else if (Phase == DrawPhase.AfterDiscard)
                {
                    pending = new CardInstanceId[0]; Refill(); Phase = ZoneCards(CardZoneKinds.Hand).Count == 0 ? DrawPhase.Lost : DrawPhase.Ready;
                    if (Phase == DrawPhase.Lost) { Close(); Emit(DrawEventKind.Lost); }
                    else { Open(DrawTimings.BeforeAction); Trigger(); }
                }
            }
            public void NextRound()
            {
                var scope = source.Id + "/play/" + scores.Count;
                World = AbilityLifecycle.BeginRound(World);
                World = new AbilityWorld(World.Id, revision, true, World.Mode, scope, DrawPhase.Ready.ToString(), source.PlayerId, World.Participants,
                    World.Cards, World.Zones, World.Abilities, World.Statuses, null, ResolutionState.Empty, null, World.Knowledge,
                    World.AllowChipChanges, World.AllowCardChanges, World.AllowKnowledgeProjection);
                Phase = DrawPhase.Ready; Refill(); Open(DrawTimings.BeforeAction); Trigger();
            }
        }
    }
}
