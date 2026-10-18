using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BuildAndScoringTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player");
        private static readonly ParticipantId Other = new ParticipantId("other");
        private sealed class M
        {
            public readonly ContentCatalog Catalog;
            public HoldemMatchEngine Engine;
            public MatchState State;
            public M(ContentCatalog catalog, AbilityRuntime runtime, params AbilityInstance[] abilities)
            {
                Catalog = catalog; Engine = new HoldemMatchEngine(catalog, abilityRuntime: runtime);
                State = Engine.Create(new MatchId("ability-test"), AbilityHoldem.Definition(catalog.Version, 2),
                    new TableState(new[] { new SeatId("a"), new SeatId("b") }), new[] {
                        new ParticipantState(Player, new SeatId("a"), ParticipantStatus.Active, new ResourceState(new Chips(1000), 5)),
                        new ParticipantState(Other, new SeatId("b"), ParticipantStatus.Active, new ResourceState(new Chips(1000), 5))
                    }, HmacDeckShuffler.FromSeed(new byte[32]), abilities);
            }
            public void Accept(TransitionResult r) { Assert.That(r.Accepted, Is.True, r.Error.ToString()); State = r.State; StateValidator.Validate(State, Catalog); }
            public void Start() { Accept(Engine.ExecuteSystem(State, new StartHandCommand("start", State.Revision))); Drain(); }
            public void Drain(bool closeSettled = false)
            {
                int n = 0;
                while (State.AbilityWindow != null && State.AbilityWindow.Timing != HoldemTiming.BeforeBettingAction && State.ActiveDecision?.Kind != DecisionKind.SelectTargets)
                {
                    if (!closeSettled && State.AbilityWindow.Timing == HoldemTiming.AfterHandSettled) break;
                    Assert.That(++n, Is.LessThan(30)); Accept(Engine.ExecuteSystem(State, new AdvanceWindowCommand("advance", State.Revision, State.AbilityWindow.Id)));
                }
            }
            public void Bet(BettingAction action, bool drain = true)
            {
                Accept(Engine.Execute(State, new BettingCommand("bet", State.Revision, State.ActiveDecision.Id, action), State.ActiveDecision.ActorId));
                if (drain) Drain();
            }
            public void Activate(string id) => Accept(Engine.Execute(State, new ActivateAbilityCommand("activate", State.Revision, State.AbilityWindow.Id, new AbilityInstanceId(id)), Player));
            public int Energy => State.Participants.First(p => p.Id == Player).Resources.Energy;
        }
        private static AbilityInstance A(string id, AbilityDefinitionId definition, int? charges = null, int order = 0) =>
            new AbilityInstance(new AbilityInstanceId(id), definition, Player, EntityRef.Participant(Player), charges, order: order);
        private static AbilityDefinition D(string id, AbilityMechanism mechanism, string timing = HoldemTiming.BeforeBettingAction) =>
            new AbilityDefinition(new AbilityDefinitionId(id), mechanism, AbilityModes.All, new[] { timing }, new AbilityCondition[0]);
        private static ContentCatalog Catalog(params AbilityDefinition[] definitions) =>
            new ContentCatalog("test.abilities.1", StandardHoldem.Cards(), definitions, new[] { new StatusDefinitionId("status") });
        private sealed class Handler : IActivatedAbility, ITriggeredAbility
        {
            private readonly Func<AbilityContext, IReadOnlyList<EffectIntent>> plan;
            public Handler(Func<AbilityContext, IReadOnlyList<EffectIntent>> plan) { this.plan = plan; }
            public IReadOnlyList<EffectIntent> Plan(AbilityContext c) => plan(c);
        }
        private sealed class CancelResources : IEffectReplacement
        { public EffectReplacement Replace(AbilityContext c, EffectIntent e) => e is ChangeResourceEffect ? EffectReplacement.Cancel() : EffectReplacement.Keep(); }
        private sealed class Discount : IRuleModifier<AbilityCostQuery, AbilityCost>
        { public AbilityCost Apply(AbilityContext c, AbilityCostQuery q, AbilityCost value) => new AbilityCost(value.Chips, Math.Max(0, value.Energy - 2)); }
        private sealed class AllAces : IRuleModifier<CardFaceQuery, CardFace>
        { public CardFace Apply(AbilityContext c, CardFaceQuery q, CardFace value) => new CardFace(Rank.Ace, value.Suit); }

        [Test] public void TriggerRunsOncePerWindowAndMemorySurvivesRoundTrip()
        {
            var content = ExampleRunContent.Create(); var g = new M(content.CardsAndAbilities, ExampleRunContent.Runtime(), A("focus", ExampleRunContent.CallEnergy));
            g.Start(); g.Bet(BettingAction.Call, false);
            Assert.That(g.Energy, Is.EqualTo(6)); Assert.That(g.State.Abilities.Single().Memory.CounterValue("calls"), Is.EqualTo(1));
            g.State = MatchSnapshotCodec.Deserialize(MatchSnapshotCodec.Serialize(g.State, g.Catalog), g.Catalog, g.State.Definition);
            g.Engine = new HoldemMatchEngine(g.Catalog, abilityRuntime: ExampleRunContent.Runtime()); g.Drain();
            Assert.That(g.Energy, Is.EqualTo(6)); Assert.That(g.State.Abilities.Single().UsesThisRound, Is.EqualTo(1));
        }
        [Test] public void PaidSelectionResumesAfterSaveWithoutChargingAgain()
        {
            var content = ExampleRunContent.Create(); var runtime = ExampleRunContent.Runtime();
            var g = new M(content.CardsAndAbilities, runtime, A("swap", ExampleRunContent.SwapCard, 1)); g.Start();
            var originalHole = g.State.CurrentHand.Zones.Single(z => z.Kind == HoldemZoneKinds.HoleCards && z.ControllerId == Player).Cards.ToArray();
            g.Activate("swap"); Assert.That(g.Energy, Is.EqualTo(4)); Assert.That(g.State.ActiveDecision.Kind, Is.EqualTo(DecisionKind.SelectTargets));
            Assert.That(g.State.Abilities.Single().RemainingCharges, Is.Zero);
            g.State = MatchSnapshotCodec.Deserialize(MatchSnapshotCodec.Serialize(g.State, g.Catalog), g.Catalog, g.State.Definition);
            g.Engine = new HoldemMatchEngine(g.Catalog, abilityRuntime: ExampleRunContent.Runtime());
            var pending = g.State; var command = new SelectAbilityTargetsCommand("choose", pending.Revision, pending.ActiveDecision.Id, new[] { "choice-0" });
            g.Accept(g.Engine.Execute(g.State, command, Player));
            Assert.That(g.Energy, Is.EqualTo(4)); Assert.That(g.State.ActiveDecision.Kind, Is.EqualTo(DecisionKind.ModeAction));
            Assert.That(g.State.CurrentHand.Zones.Single(z => z.Kind == CardZoneKinds.Discard).Cards.Single(), Is.EqualTo(originalHole[0]));
            Assert.That(g.State.CurrentHand.Zones.Single(z => z.Kind == HoldemZoneKinds.HoleCards && z.ControllerId == Player).Cards.Contains(originalHole[0]), Is.False);
            Assert.That(g.Engine.Execute(g.State, command, Player).Error, Is.EqualTo(CommandError.StaleRevision));
            g.Bet(BettingAction.Call); g.Bet(BettingAction.Check);
            Assert.That(g.State.CurrentHand.Zones.Single(z => z.Kind == CardZoneKinds.Discard).Cards.Count, Is.EqualTo(1));
        }
        [Test] public void SelectionRejectsWrongActorAndCannotBeSkippedByBettingOrWindowAdvance()
        {
            var content = ExampleRunContent.Create(); var g = new M(content.CardsAndAbilities, ExampleRunContent.Runtime(), A("swap", ExampleRunContent.SwapCard));
            g.Start(); g.Activate("swap"); var s = g.State;
            Assert.That(g.Engine.Execute(s, new SelectAbilityTargetsCommand("bad", s.Revision, s.ActiveDecision.Id, new[] { "choice-0" }), Other).Accepted, Is.False);
            Assert.That(g.Engine.Execute(s, new BettingCommand("bet", s.Revision, s.ActiveDecision.Id, BettingAction.Call), Player).Accepted, Is.False);
            Assert.That(g.Engine.ExecuteSystem(s, new AdvanceWindowCommand("skip", s.Revision, s.AbilityWindow.Id)).Accepted, Is.False);
            g.Accept(g.Engine.ExecuteSystem(s, new DecisionTimeoutCommand("timeout", s.Revision, s.ActiveDecision.Id)));
            Assert.That(g.State.ActiveDecision.Kind, Is.EqualTo(DecisionKind.ModeAction));
        }
        [Test] public void InvalidEffectRollsBackCostEarlierEffectsChargesAndEvents()
        {
            var d = D("bad", AbilityMechanism.Activated); var behavior = new Handler(c => new EffectIntent[] {
                new ChangeResourceEffect(c.Ability.Id, Player, ResourceKind.Energy, 3),
                new MoveCardEffect(c.Ability.Id, new CardInstanceId("missing"), new ZoneId("deck"), new ZoneId("board")) });
            var g = new M(Catalog(d), new AbilityRuntime(new[] { new AbilityBehavior(d.Id, activated: behavior, cost: new AbilityCost(Chips.Zero, 1)) }), A("bad", d.Id, 2));
            g.Start(); var before = g.State;
            var r = g.Engine.Execute(before, new ActivateAbilityCommand("bad", before.Revision, before.AbilityWindow.Id, new AbilityInstanceId("bad")), Player);
            Assert.That(r.Accepted, Is.False); Assert.That(r.State, Is.SameAs(before)); Assert.That(r.Events.Count, Is.Zero); Assert.That(g.Energy, Is.EqualTo(5));
            Assert.That(g.State.Abilities.Single().RemainingCharges, Is.EqualTo(2));
        }
        [Test] public void ReplacementAndCostModifiersParticipateInExecution()
        {
            var gain = D("gain", AbilityMechanism.Activated); var cancel = D("cancel", AbilityMechanism.EffectReplacement); var discount = D("discount", AbilityMechanism.RuleModifier);
            var runtime = new AbilityRuntime(new[] {
                new AbilityBehavior(gain.Id, activated: new Handler(c => new EffectIntent[] { new ChangeResourceEffect(c.Ability.Id, Player, ResourceKind.Energy, 10) }), cost: new AbilityCost(Chips.Zero, 3)),
                new AbilityBehavior(cancel.Id, replacement: new CancelResources()), new AbilityBehavior(discount.Id, costModifier: new Discount()) });
            var g = new M(Catalog(gain, cancel, discount), runtime, A("gain", gain.Id), A("cancel", cancel.Id), A("discount", discount.Id));
            g.Start(); g.Activate("gain"); Assert.That(g.Energy, Is.EqualTo(4));
        }
        [Test] public void EffectiveFacesAreUsedAtShowdownAndSavedForScoring()
        {
            var d = D("aces", AbilityMechanism.RuleModifier, HoldemTiming.BeforeShowdown);
            var g = new M(Catalog(d), new AbilityRuntime(new[] { new AbilityBehavior(d.Id, faceModifier: new AllAces()) }), A("aces", d.Id)); g.Start();
            while (g.State.CurrentHand.Stage != HandStage.Settled)
                g.Bet(g.Engine.GetLegalActions(g.State, g.State.ActiveDecision.ActorId).CanCheck ? BettingAction.Check : BettingAction.Call);
            Assert.That(g.State.CurrentHand.Settlement.Evaluations.All(e => e.Category == HandCategory.FiveOfAKind), Is.True);
            Assert.That(g.State.CurrentHand.Settlement.Evaluations.All(e => e.Faces.Count == 5 && e.Faces.All(f => f.Face.Rank == Rank.Ace)), Is.True);
            Assert.That(g.State.CurrentHand.Cards.Any(c => g.Catalog.Get(c.DefinitionId).Rank != Rank.Ace), Is.True);
        }
        [Test] public void PeekProducesOnlyOwnersKnowledgeAndPrivateAbilityEvents()
        {
            var content = ExampleRunContent.Create(); var runtime = ExampleRunContent.Runtime(); var g = new M(content.CardsAndAbilities, runtime, A("peek", ExampleRunContent.PeekDeck)); g.Start();
            var r = g.Engine.Execute(g.State, new ActivateAbilityCommand("peek", g.State.Revision, g.State.AbilityWindow.Id, new AbilityInstanceId("peek")), Player); g.Accept(r);
            Assert.That(KnowledgeProjector.Project(HoldemAbilityAdapter.ToWorld(g.State), g.Catalog, Player, runtime).Count, Is.EqualTo(1));
            Assert.That(KnowledgeProjector.Project(HoldemAbilityAdapter.ToWorld(g.State), g.Catalog, Other, runtime).Count, Is.Zero); Assert.That(r.EventsFor(Other).Count, Is.Zero);
        }
        [Test] public void CooldownAndHandCountersResetAcrossHands()
        {
            var d = D("cooldown", AbilityMechanism.Activated);
            var runtime = new AbilityRuntime(new[] { new AbilityBehavior(d.Id, activated: new Handler(c => new EffectIntent[0]), cooldownRounds: 1) });
            var g = new M(Catalog(d), runtime, A("cooldown", d.Id)); g.Start(); g.Activate("cooldown");
            g.Bet(BettingAction.Fold); g.Drain(true); g.Start();
            Assert.That(g.State.Abilities.Single().CooldownRounds, Is.EqualTo(1)); Assert.That(g.State.Abilities.Single().UsesThisRound, Is.Zero);
            Assert.That(g.Engine.Execute(g.State, new ActivateAbilityCommand("blocked", g.State.Revision, g.State.AbilityWindow.Id, new AbilityInstanceId("cooldown")), Player).Accepted, Is.False);
            g.Bet(BettingAction.Fold); g.Drain(true); g.Start(); g.Activate("cooldown");
        }
        [Test] public void AbilityWindowHasPersistentExpansionBudget()
        {
            var d = D("budget", AbilityMechanism.Activated);
            var g = new M(Catalog(d), new AbilityRuntime(new[] { new AbilityBehavior(d.Id, activated: new Handler(c => Enumerable.Range(0, 64)
                .Select(i => (EffectIntent)new ChangeResourceEffect(c.Ability.Id, Player, ResourceKind.Energy, 1)).ToArray())) }), A("budget", d.Id));
            g.Start(); for (int i = 0; i < 4; i++) g.Activate("budget");
            var before = g.State;
            Assert.That(before.Resolution.StepsUsed, Is.EqualTo(256));
            var r = g.Engine.Execute(before, new ActivateAbilityCommand("overflow", before.Revision, before.AbilityWindow.Id, new AbilityInstanceId("budget")), Player);
            Assert.That(r.Accepted, Is.False); Assert.That(r.State, Is.SameAs(before));
        }

        private sealed class TriggerChoice : ITriggeredAbility, IAbilitySelectionHandler
        {
            public IReadOnlyList<EffectIntent> Plan(AbilityContext c) => new EffectIntent[] {
                new RequestSelectionEffect(c.Ability.Id, c.Ability.OwnerId,
                    new SelectionSpec(SelectionTargetKind.Options, new[] { new SelectionCandidate("ok") }, 1, 1))
            };
            public IReadOnlyList<EffectIntent> Resume(AbilityContext c, int step, SelectionResult selection) => new EffectIntent[] {
                new ChangeResourceEffect(c.Ability.Id, c.Ability.OwnerId, ResourceKind.Energy, 2)
            };
        }
        [Test] public void TriggerSelectionResumesRemainingTriggersExactlyOnceAfterRestore()
        {
            var first = D("choice", AbilityMechanism.Triggered); var second = D("later", AbilityMechanism.Triggered);
            var handler = new TriggerChoice();
            var runtime = new AbilityRuntime(new[] { new AbilityBehavior(first.Id, triggered: handler, selection: handler),
                new AbilityBehavior(second.Id, triggered: new Handler(c => new EffectIntent[] { new ChangeResourceEffect(c.Ability.Id, Player, ResourceKind.Energy, 3) })) });
            var g = new M(Catalog(first, second), runtime, A("choice", first.Id, order: 0), A("later", second.Id, order: 1)); g.Start();
            Assert.That(g.State.ActiveDecision.Kind, Is.EqualTo(DecisionKind.SelectTargets)); Assert.That(g.Energy, Is.EqualTo(5));
            g.State = MatchSnapshotCodec.Deserialize(MatchSnapshotCodec.Serialize(g.State, g.Catalog), g.Catalog, g.State.Definition);
            g.Accept(g.Engine.Execute(g.State, new SelectAbilityTargetsCommand("choose", g.State.Revision, g.State.ActiveDecision.Id, new[] { "ok" }), Player));
            Assert.That(g.Energy, Is.EqualTo(10)); Assert.That(g.State.Abilities.All(a => a.UsesThisRound == 1), Is.True);
            Assert.That(g.State.ActiveDecision.Kind, Is.EqualTo(DecisionKind.ModeAction));
        }
        [Test] public void ShortDeckExchangeRejectsBeforeChargingOrOpeningChoice()
        {
            var c = ExampleRunContent.Create(); var g = new M(c.CardsAndAbilities, ExampleRunContent.Runtime(), A("swap", ExampleRunContent.SwapCard));
            g.Engine = new HoldemMatchEngine(g.Catalog, abilityRuntime: ExampleRunContent.Runtime(),
                deckEntries: StandardHoldem.Cards().Take(12).Select((card, i) => new DeckEntry("card-" + i, card.Id)));
            g.Start(); var before = g.State;
            var result = g.Engine.Execute(before, new ActivateAbilityCommand("swap", before.Revision, before.AbilityWindow.Id, new AbilityInstanceId("swap")), Player);
            Assert.That(result.Accepted, Is.False); Assert.That(result.State, Is.SameAs(before)); Assert.That(g.Energy, Is.EqualTo(5));
            Assert.That(result.Events.Count, Is.Zero);
        }
        [Test] public void DeckEffectsCannotConsumeReservedRunoutCards()
        {
            var d = D("drain", AbilityMechanism.Activated);
            var g = new M(Catalog(d), new AbilityRuntime(new[] { new AbilityBehavior(d.Id, activated: new Handler(c => new EffectIntent[] {
                new DrawCardsEffect(c.Ability.Id, new ZoneId("deck"), new ZoneId("discard"), 41)
            })) }), A("drain", d.Id)); g.Start(); var before = g.State;
            var result = g.Engine.Execute(before, new ActivateAbilityCommand("drain", before.Revision, before.AbilityWindow.Id, new AbilityInstanceId("drain")), Player);
            Assert.That(result.Accepted, Is.False); Assert.That(result.State, Is.SameAs(before));
        }
        [Test] public void StatusesExpireAtTheirStreetAndExplicitTriggerBudget()
        {
            var add = D("add", AbilityMechanism.Activated); var consume = D("consume", AbilityMechanism.Activated);
            var runtime = new AbilityRuntime(new[] {
                new AbilityBehavior(add.Id, activated: new Handler(c => new EffectIntent[] {
                    new ApplyStatusEffect(c.Ability.Id, new StatusInstance(new StatusInstanceId("future"), new StatusDefinitionId("status"),
                        EntityRef.Ability(c.Ability.Id), EntityRef.Participant(Player), 1, StatusDuration.UntilPhaseEnd(c.State.ScopeId, HoldemAbilityAdapter.Phase(Street.Flop)))),
                    new ApplyStatusEffect(c.Ability.Id, new StatusInstance(new StatusInstanceId("twice"), new StatusDefinitionId("status"),
                        EntityRef.Ability(c.Ability.Id), EntityRef.Participant(Player), 1, StatusDuration.ForTriggers(2)))
                })),
                new AbilityBehavior(consume.Id, activated: new Handler(c => new EffectIntent[] { new ConsumeStatusEffect(c.Ability.Id, new StatusInstanceId("twice")) }))
            });
            var g = new M(Catalog(add, consume), runtime, A("add", add.Id), A("consume", consume.Id)); g.Start(); g.Activate("add");
            g.Activate("consume"); Assert.That(g.State.Statuses.Single(s => s.Id.Value == "twice").Duration.RemainingTriggers, Is.EqualTo(1));
            g.Activate("consume"); Assert.That(g.State.Statuses.Count, Is.EqualTo(1));
            g.Bet(BettingAction.Call); g.Bet(BettingAction.Check); Assert.That(g.State.Statuses.Count, Is.EqualTo(1));
            g.Bet(BettingAction.Check); g.Bet(BettingAction.Check); Assert.That(g.State.Statuses.Count, Is.Zero);
        }

        private static ScoringCard[] Cards() => new[] { 2, 3, 4, 5, 7 }.Select((rank, i) => new ScoringCard("c" + i, new CardFace((Rank)rank, (Suit)(i % 4 + 1)))).ToArray();
        [Test] public void RelicOrderChangesAddThenMultiplyResult()
        {
            var plus = new ScoringRelic("plus", 1, 0, new[] { new ScoreEffect(ScoreTiming.AfterCards, ScoreOperation.AddMultiplier, 4) });
            var times = new ScoringRelic("times", 1, 0, new[] { new ScoreEffect(ScoreTiming.AfterCards, ScoreOperation.MultiplyMultiplier, 2) });
            var a = ScoreEngine.Evaluate("a", ScoreProfile.Default, HandCategory.HighCard, 1, Cards(), new[] { plus, times });
            var b = ScoreEngine.Evaluate("b", ScoreProfile.Default, HandCategory.HighCard, 1, Cards(), new[] { times, plus });
            Assert.That(a.Total, Is.EqualTo(260)); Assert.That(b.Total, Is.EqualTo(156));
            Assert.That(a.Steps.Last().SourceId, Is.EqualTo("times")); Assert.That(a.Points, Is.EqualTo(26));
        }
        [Test] public void CardRetriggersAndRelicGrowthAppearInScoreTrace()
        {
            var cards = Cards(); cards[0] = new ScoringCard("c0", cards[0].Face, bonusPoints: 3, retriggers: 1);
            var relic = new ScoringRelic("growing", 2, 3, new[] { new ScoreEffect(ScoreTiming.AfterCards, ScoreOperation.AddPoints, 5, perLevel: 2, perGrowth: 4) });
            var result = ScoreEngine.Evaluate("score", ScoreProfile.Default, HandCategory.HighCard, 2, cards, new[] { relic });
            Assert.That(result.Points, Is.EqualTo(63)); Assert.That(result.Multiplier, Is.EqualTo(2)); Assert.That(result.Total, Is.EqualTo(126));
            Assert.That(result.Steps.Count(s => s.CardId == "c0"), Is.EqualTo(2));
        }
        [Test] public void ScoreUsesDecimalAndFloorsOnlyFinalTotal()
        {
            var relic = new ScoringRelic("fraction", 1, 0, new[] { new ScoreEffect(ScoreTiming.AfterCards, ScoreOperation.MultiplyMultiplier, 1.5m) });
            var score = ScoreEngine.Evaluate("fraction", ScoreProfile.Default, HandCategory.HighCard, 1, Cards(), new[] { relic });
            Assert.That(score.Total, Is.EqualTo(39)); Assert.That(score.Multiplier, Is.EqualTo(1.5m));
            Assert.Throws<OverflowException>(() => ScoreEngine.Evaluate("overflow", ScoreProfile.Default, HandCategory.HighCard, 1, Cards(),
                new[] { new ScoringRelic("huge", 1, 0, new[] { new ScoreEffect(ScoreTiming.AfterCards, ScoreOperation.MultiplyMultiplier, decimal.MaxValue) }) }));
        }

        private static RunContent RunContent(long target = 1, int hands = 4, string[] starters = null, int capacity = 5, bool growingAbility = false)
        {
            var sample = ExampleRunContent.Create(); var ids = new[] { "steady", "double", "focus", "exchange", "growing" };
            var offers = ids.Select(id => new RunOfferDefinition("buy-" + id, RunOfferKind.Relic, 5, relicId: id)).Concat(new[] {
                new RunOfferDefinition("enhance", RunOfferKind.EnhanceCard, 2, amount: 7, multiplier: 1),
                new RunOfferDefinition("copy", RunOfferKind.DuplicateCard, 2), new RunOfferDefinition("remove", RunOfferKind.RemoveCard, 2),
                new RunOfferDefinition("level", RunOfferKind.UpgradeHand, 2, handCategory: HandCategory.OnePair)
            }).ToArray();
            return new RunContent(sample.CardsAndAbilities, new RunDefinition("run.test.1", ScoreProfile.Default,
                new[] { new RunStageDefinition("one", target, hands), new RunStageDefinition("two", target, hands) },
                offers.Select(o => o.Id), new[] { "enhance" }, starters, capacity, startingGold: 50, offerCount: 10), ids.Select(id => growingAbility && id == "focus" ? new RelicDefinition("focus", sample.Relic(id).ScoreEffects, growthPerScoredRound: 1, abilityId: ExampleRunContent.CallEnergy) : sample.Relic(id)), offers, sample.StartingDeckVersion, sample.StartingDeck);
        }
        private sealed class R
        {
            public readonly RunContent Content; public readonly HoldemRunAdapter Adapter; public RunEngine<HoldemEncounterState, MatchCommand, MatchEvent> Engine; public RunState<HoldemEncounterState> State;
            public R(RunContent content, bool winnersOnly = true) { Content = content; Adapter = new HoldemRunAdapter(ExampleRunContent.Runtime(), new HoldemEncounterSettings(winnersOnly: winnersOnly)); Engine = new RunEngine<HoldemEncounterState, MatchCommand, MatchEvent>(content, Adapter); State = Engine.Create("run", Player, HmacDeckShuffler.FromSeed(new byte[32])); }
            public void Accept(RunTransitionResult<HoldemEncounterState, MatchEvent> result) { Assert.That(result.Accepted, Is.True, result.Error + "/" + result.EncounterError); State = result.State; RunValidator.Validate(State, Content, Adapter); }
            public void Command(RunAction action, string item = null, string target = null, IEnumerable<string> order = null) =>
                Accept(Engine.Execute(State, new RunCommand<MatchCommand>("command", State.Revision, action, item, target, order), Player));
            public void Start() { Accept(Engine.ExecuteSystem(State, new RunCommand<MatchCommand>("start", State.Revision, RunAction.StartEncounter))); Drain(); }
            public void Drain()
            {
                int n = 0;
                while (State.Phase == RunPhase.Encounter && State.Encounter.Match.AbilityWindow != null && State.Encounter.Match.AbilityWindow.Timing != HoldemTiming.BeforeBettingAction && State.Encounter.Match.ActiveDecision?.Kind != DecisionKind.SelectTargets)
                {
                    Assert.That(++n, Is.LessThan(30)); var m = State.Encounter.Match;
                    Accept(Engine.ExecuteSystem(State, new RunCommand<MatchCommand>("advance", State.Revision, RunAction.SystemEncounter,
                        encounterCommand: new AdvanceWindowCommand("advance", m.Revision, m.AbilityWindow.Id))));
                }
            }
            public void Bet(BettingAction action)
            {
                var m = State.Encounter.Match;
                Accept(Engine.Execute(State, new RunCommand<MatchCommand>("bet", State.Revision, RunAction.PlayerEncounter,
                    encounterCommand: new BettingCommand("bet", m.Revision, m.ActiveDecision.Id, action)), m.ActiveDecision.ActorId)); Drain();
            }
            public void WinByFold() { Start(); Bet(BettingAction.Call); Bet(BettingAction.Fold); Assert.That(State.Phase, Is.EqualTo(RunPhase.Reward)); }
            public string Offer(string definition) => State.Offers.Single(o => o.DefinitionId == definition).Id;
            public void Restore() { State = RunSnapshotCodec<HoldemEncounterState>.Deserialize(RunSnapshotCodec<HoldemEncounterState>.Serialize(State, Content, Adapter), Content, Adapter); Engine = new RunEngine<HoldemEncounterState, MatchCommand, MatchEvent>(Content, Adapter); }
        }
        [Test] public void RunCompletesEncounterRewardShopAndNextEncounter()
        {
            var r = new R(RunContent(starters: new[] { "focus" })); r.WinByFold();
            Assert.That(r.State.StageScore, Is.EqualTo(20)); Assert.That(r.State.RoundsScored, Is.EqualTo(1)); Assert.That(r.State.Gold, Is.EqualTo(55));
            Assert.That(r.State.Relics.Single().Memory.CounterValue("calls"), Is.EqualTo(1));
            r.Command(RunAction.ChooseReward, r.State.Offers.Single().Id, r.State.Deck[0].Id);
            Assert.That(r.State.Phase, Is.EqualTo(RunPhase.Shop)); r.Restore(); r.WinByFold();
            r.Command(RunAction.SkipReward); Assert.That(r.State.Phase, Is.EqualTo(RunPhase.Completed)); Assert.That(r.State.Scores.Count, Is.EqualTo(2));
        }
        [Test] public void ShopBuysUpgradesReordersAndSellsWithCapacityChecks()
        {
            var r = new R(RunContent(capacity: 2)); r.WinByFold(); r.Command(RunAction.SkipReward);
            r.Command(RunAction.Buy, r.Offer("buy-steady")); r.Command(RunAction.Buy, r.Offer("buy-double"));
            var before = r.State; var denied = r.Engine.Execute(before, new RunCommand<MatchCommand>("full", before.Revision, RunAction.Buy, r.Offer("buy-focus")), Player);
            Assert.That(denied.Error, Is.EqualTo(RunError.Capacity)); Assert.That(denied.State, Is.SameAs(before));
            var first = r.State.Relics[0].Id; r.Command(RunAction.UpgradeRelic, first); Assert.That(r.State.Relics[0].Level, Is.EqualTo(2));
            r.Command(RunAction.ReorderRelics, order: r.State.Relics.Reverse().Select(j => j.Id).ToArray()); Assert.That(r.State.Relics[1].Id, Is.EqualTo(first));
            r.Command(RunAction.Sell, first); Assert.That(r.State.Relics.Count, Is.EqualTo(1));
        }
        [Test] public void DeckEnhancementCopyRemovalAndHandLevelsReachNextMatch()
        {
            var r = new R(RunContent()); r.WinByFold(); string card = r.State.Deck[0].Id;
            r.Command(RunAction.ChooseReward, r.State.Offers[0].Id, card); r.Command(RunAction.Buy, r.Offer("copy"), card);
            var copy = r.State.Deck.Last(); Assert.That(copy.Id, Is.Not.EqualTo(card)); Assert.That(copy.BonusPoints, Is.EqualTo(7));
            r.Command(RunAction.Buy, r.Offer("remove"), card); r.Command(RunAction.Buy, r.Offer("level")); r.Restore(); r.Start();
            Assert.That(r.State.Encounter.Match.CurrentHand.Cards.Any(c => c.OriginId == copy.Id), Is.True);
            Assert.That(r.State.Encounter.Match.CurrentHand.Cards.Any(c => c.OriginId == card), Is.False);
            Assert.That(r.State.HandLevels.Single(l => l.Category == HandCategory.OnePair).Level, Is.EqualTo(2));
        }
        [Test] public void ShopRerollAndRestoreHaveIdenticalOffersAndRandomState()
        {
            var r = new R(RunContent()); r.WinByFold(); r.Command(RunAction.SkipReward); var before = r.State;
            var restored = RunSnapshotCodec<HoldemEncounterState>.Deserialize(RunSnapshotCodec<HoldemEncounterState>.Serialize(before, r.Content, r.Adapter), r.Content, r.Adapter);
            var command = new RunCommand<MatchCommand>("reroll", before.Revision, RunAction.Reroll);
            var a = r.Engine.Execute(before, command, Player); var b = new RunEngine<HoldemEncounterState, MatchCommand, MatchEvent>(r.Content, r.Adapter).Execute(restored, command, Player);
            Assert.That(a.Accepted && b.Accepted, Is.True);
            Assert.That(RunSnapshotCodec<HoldemEncounterState>.Serialize(a.State, r.Content, r.Adapter), Is.EqualTo(RunSnapshotCodec<HoldemEncounterState>.Serialize(b.State, r.Content, r.Adapter)));
            Assert.That(a.State.Gold, Is.EqualTo(before.Gold - r.Content.Definition.RerollCost));
            Assert.That(r.Engine.Execute(a.State, command, Player).Error, Is.EqualTo(RunError.StaleRevision));
        }
        [Test] public void RunRejectsForgedOwnerSystemCommandsAndStaleOffers()
        {
            var r = new R(RunContent());
            Assert.That(r.Engine.Execute(r.State, new RunCommand<MatchCommand>("start", r.State.Revision, RunAction.StartEncounter), Player).Error, Is.EqualTo(RunError.WrongActor));
            r.WinByFold(); r.Command(RunAction.SkipReward); var before = r.State;
            Assert.That(r.Engine.Execute(before, new RunCommand<MatchCommand>("buy", before.Revision, RunAction.Buy, r.Offer("buy-steady")), Other).Error, Is.EqualTo(RunError.WrongActor));
            string oldOffer = r.Offer("buy-steady"); r.Command(RunAction.Reroll);
            Assert.That(r.Engine.Execute(r.State, new RunCommand<MatchCommand>("old", r.State.Revision, RunAction.Buy, oldOffer), Player).Error, Is.EqualTo(RunError.UnknownOffer));
        }
        [Test] public void FailedEncounterCannotContinueOrCollectReward()
        {
            var r = new R(RunContent(target: 10000, hands: 1)); r.Start(); r.Bet(BettingAction.Fold);
            Assert.That(r.State.Phase, Is.EqualTo(RunPhase.Failed)); Assert.That(r.State.StageScore, Is.Zero);
            Assert.That(r.Engine.ExecuteSystem(r.State, new RunCommand<MatchCommand>("again", r.State.Revision, RunAction.StartEncounter)).Accepted, Is.False);
        }
        [Test] public void RunCanSaveDuringAbilitySelectionAndResumeIntoBetting()
        {
            var r = new R(RunContent(starters: new[] { "exchange" })); r.Start(); var m = r.State.Encounter.Match;
            r.Accept(r.Engine.Execute(r.State, new RunCommand<MatchCommand>("activate", r.State.Revision, RunAction.PlayerEncounter,
                encounterCommand: new ActivateAbilityCommand("activate", m.Revision, m.AbilityWindow.Id, m.Abilities.Single().Id)), Player)); r.Restore();
            m = r.State.Encounter.Match;
            r.Accept(r.Engine.Execute(r.State, new RunCommand<MatchCommand>("choose", r.State.Revision, RunAction.PlayerEncounter,
                encounterCommand: new SelectAbilityTargetsCommand("choose", m.Revision, m.ActiveDecision.Id, new[] { "choice-1" })), Player));
            Assert.That(r.State.Encounter.Match.ActiveDecision.Kind, Is.EqualTo(DecisionKind.ModeAction));
            Assert.That(r.State.Encounter.Match.Participants.First(p => p.Id == Player).Resources.Energy, Is.EqualTo(2));
        }
        [Test] public void RunSnapshotRejectsChangedTotalsAndVersions()
        {
            var r = new R(RunContent()); r.WinByFold(); string json = RunSnapshotCodec<HoldemEncounterState>.Serialize(r.State, r.Content, r.Adapter);
            Assert.Throws<SnapshotException>(() => RunSnapshotCodec<HoldemEncounterState>.Deserialize(json.Replace("\"StageScore\":20", "\"StageScore\":21"), r.Content, r.Adapter));
            Assert.Throws<SnapshotException>(() => RunSnapshotCodec<HoldemEncounterState>.Deserialize(json.Replace("\"EncounterSchemaVersion\":" + MatchSnapshotCodec.SchemaVersion, "\"EncounterSchemaVersion\":3"), r.Content, r.Adapter));
            Assert.Throws<SnapshotException>(() => RunSnapshotCodec<HoldemEncounterState>.Deserialize(json.Replace("run.test.1", "run.test.2"), r.Content, r.Adapter));
        }
        [Test] public void ShowdownScoresGrowRelicsAcrossSavedHandsWithoutCreatingChips()
        {
            var r = new R(RunContent(target: 100000, starters: new[] { "growing", "focus" }, growingAbility: true), winnersOnly: false); r.Start();
            for (int hand = 0; hand < 2; hand++)
            {
                if (hand > 0)
                {
                    r.Restore(); var m = r.State.Encounter.Match;
                    r.Accept(r.Engine.ExecuteSystem(r.State, new RunCommand<MatchCommand>("next", r.State.Revision, RunAction.SystemEncounter, encounterCommand: new StartHandCommand("next", m.Revision)))); r.Drain();
                }
                while (r.State.RoundsScored <= hand)
                {
                    var legal = r.Adapter.GetLegalActions(RunEncounterContext.From(r.State, r.Content), r.State.Encounter, r.State.Encounter.Match.ActiveDecision.ActorId);
                    r.Bet(legal.CanCheck ? BettingAction.Check : BettingAction.Call);
                }
                Assert.That(r.State.Scores[hand].Total, Is.GreaterThan(0));
                var relic = r.State.Relics.Single(j => j.DefinitionId == "growing");
                Assert.That(relic.Growth, Is.EqualTo(hand + 1));
                Assert.That(r.State.Scores[hand].Steps.Single(s => s.SourceId == relic.Id).Amount, Is.EqualTo(10 + hand * 5));
                Assert.That(r.State.Encounter.Match.Participants.Sum(p => p.Resources.Chips.Value), Is.EqualTo(2000));
                Assert.That(r.State.Gold, Is.EqualTo(50));
                var focus = r.State.Relics.Single(j => j.DefinitionId == "focus");
                Assert.That(focus.Growth, Is.EqualTo(hand + 1));
                Assert.That(focus.Memory.Counters.Any(c => c.Key.StartsWith("build.", StringComparison.Ordinal)), Is.False);
                Assert.That(r.State.Encounter.Match.Abilities.Single(a => a.Id.Value == focus.Id).Memory.CounterValue("build.growth"), Is.EqualTo(hand + 1));
            }
            r.Restore(); Assert.That(r.State.StageScore, Is.EqualTo(r.State.Scores.Sum(s => s.Total)));
        }
        [Test] public void MatchSchemaTwoIsExplicitlyRejected()
        {
            var g = new M(ExampleRunContent.Create().CardsAndAbilities, ExampleRunContent.Runtime()); g.Start();
            string json = MatchSnapshotCodec.Serialize(g.State, g.Catalog);
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json.Replace("\"SchemaVersion\":" + MatchSnapshotCodec.SchemaVersion, "\"SchemaVersion\":2"), g.Catalog, g.State.Definition));
        }
    }
}
