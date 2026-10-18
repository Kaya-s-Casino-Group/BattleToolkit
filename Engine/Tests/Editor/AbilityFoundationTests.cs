using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Fixture = PokerEngine.Tests.DataFoundationTests.Fixture;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class AbilityFoundationTests
    {
        private static AbilityWorld W(MatchState state) => HoldemAbilityAdapter.ToWorld(state);

        private static AbilityDefinition Definition(Fixture f, AbilityMechanism mechanism = AbilityMechanism.Activated,
            AbilityModes modes = AbilityModes.All, IEnumerable<AbilityCondition> conditions = null,
            string timing = HoldemTiming.BeforeBettingAction, int priority = 0, BettingAction? requiredAction = null) =>
            new AbilityDefinition(f.AbilityDefinition, mechanism, modes, new[] { timing }, conditions ?? new AbilityCondition[0], priority, requiredAction.HasValue ? HoldemActionKeys.Key(requiredAction.Value) : null);

        private static void Configure(Fixture f, params AbilityDefinition[] definitions)
        {
            f.Catalog = new ContentCatalog("content-1", new[] { new CardDefinition(f.CardDefinition, Rank.Ace, Suit.Spades, new string[0]) },
                definitions, new[] { f.StatusDefinition });
            f.Window = new AbilityWindowState("window-1", HoldemTiming.BeforeBettingAction, f.Hand.Value, f.C);
        }

        private static AbilityInstance WithMemory(Fixture f, AbilityMemory memory) =>
            new AbilityInstance(f.Ability, f.AbilityDefinition, f.A, EntityRef.Participant(f.A), memory: memory);

        private static SelectionSpec Cards(Fixture f, bool ordered = false, bool optional = false) =>
            new SelectionSpec(SelectionTargetKind.Cards, f.Cards.Take(2).Select((c, i) => new SelectionCandidate("choice-" + i, EntityRef.Card(c.Id))),
                ordered ? 2 : 1, ordered ? 2 : 1, ordered, optional, optional ? SelectionTimeoutPolicy.Skip : SelectionTimeoutPolicy.FirstCandidates);

        private static KnowledgeGrant Grant(Fixture f, KnowledgeMode mode = KnowledgeMode.Snapshot, bool everyone = false, Street? expires = null) =>
            new KnowledgeGrant(new KnowledgeGrantId("grant-1"), EntityRef.Ability(f.Ability),
                everyone ? KnowledgeAudience.Everyone : KnowledgeAudience.Participant, everyone ? (ParticipantId?)null : f.A,
                mode, f.Hand.Value, f.Cards[2].Id, 40, mode == KnowledgeMode.Snapshot ? new CardFace(Rank.King, Suit.Hearts) : null,
                mode == KnowledgeMode.TrackCardInZone ? (ZoneId?)f.Zones[3].Id : null, expires.HasValue ? HoldemAbilityAdapter.Phase(expires.Value) : null);

        private sealed class FixedFace : ICardFaceResolver
        { public CardFace Resolve(AbilityWorld s, ContentCatalog c, CardInstanceId id) => new CardFace(Rank.Two, Suit.Clubs); }

        [Test] public void EligibilityEnforcesOwnerModeWindowAndMechanism()
        {
            var f = new Fixture(); Configure(f, Definition(f, modes: AbilityModes.Solo));
            Assert.That(AbilityEligibility.Check(W(f.Build()), f.Catalog, f.Ability, AbilityMechanism.Activated, f.A), Is.EqualTo(AbilityDenial.None));
            Assert.That(AbilityEligibility.Check(W(f.Build()), f.Catalog, f.Ability, AbilityMechanism.Activated, f.B), Is.EqualTo(AbilityDenial.WrongOwner));
            Assert.That(AbilityEligibility.Check(W(f.Build()), f.Catalog, f.Ability, AbilityMechanism.Triggered), Is.EqualTo(AbilityDenial.WrongMechanism));
            f.Window = null;
            Assert.That(AbilityEligibility.Check(W(f.Build()), f.Catalog, f.Ability, AbilityMechanism.Activated, f.A), Is.EqualTo(AbilityDenial.WrongWindow));
            f.Definition = new MatchDefinition("holdem", "1", "content-1", 6, new Chips(10), new Chips(20), MatchMode.Pvp);
            Assert.That(AbilityEligibility.Check(W(f.Build()), f.Catalog, f.Ability, AbilityMechanism.Activated, f.A), Is.EqualTo(AbilityDenial.ModeDisabled));
        }

        [Test] public void ConditionsUseEnergyUsageAndTypedMemoryWithoutMutatingIt()
        {
            var f = new Fixture(); Configure(f, Definition(f, conditions: new[] {
                new AbilityCondition(ConditionKind.MinimumEnergy, 2), new AbilityCondition(ConditionKind.UsesThisRoundBelow, 1),
                new AbilityCondition(ConditionKind.CounterAtLeast, 2, "loss-streak"), new AbilityCondition(ConditionKind.HasMark, memoryKey: "marked") }));
            var memory = new AbilityMemory(new[] { new AbilityCounter("loss-streak", 2, MemoryScope.Encounter) },
                new[] { new AbilityMark("marked", EntityRef.Card(f.Cards[0].Id), MemoryScope.Round) });
            f.Abilities = new[] { WithMemory(f, memory) };
            var state = f.Build(); string before = MatchSnapshotCodec.Serialize(state, f.Catalog);
            Assert.That(AbilityEligibility.Check(W(state), f.Catalog, f.Ability, AbilityMechanism.Activated, f.A), Is.EqualTo(AbilityDenial.None));
            Assert.That(MatchSnapshotCodec.Serialize(state, f.Catalog), Is.EqualTo(before));
            f.Abilities = new[] { WithMemory(f, memory.WithCounter("loss-streak", 1, MemoryScope.Encounter)) };
            Assert.That(AbilityEligibility.Check(W(f.Build()), f.Catalog, f.Ability, AbilityMechanism.Activated, f.A), Is.EqualTo(AbilityDenial.ConditionFailed));
        }

        [Test] public void NoChargesAndCooldownPreventAbilityUse()
        {
            var f = new Fixture(); Configure(f, Definition(f));
            f.Abilities = new[] { new AbilityInstance(f.Ability, f.AbilityDefinition, f.A, EntityRef.Match(f.Match), 0) };
            Assert.That(AbilityEligibility.Check(W(f.Build()), f.Catalog, f.Ability, AbilityMechanism.Activated, f.A), Is.EqualTo(AbilityDenial.NoCharges));
            f.Abilities = new[] { new AbilityInstance(f.Ability, f.AbilityDefinition, f.A, EntityRef.Match(f.Match), cooldownRounds: 1) };
            Assert.That(AbilityEligibility.Check(W(f.Build()), f.Catalog, f.Ability, AbilityMechanism.Activated, f.A), Is.EqualTo(AbilityDenial.Cooldown));
        }

        [Test] public void TriggerMatchesBothActionAndEventActor()
        {
            var f = new Fixture(); Configure(f, Definition(f, AbilityMechanism.Triggered, conditions: new[] { new AbilityCondition(ConditionKind.OwnerIsEventActor) },
                timing: HoldemTiming.AfterBettingAction, requiredAction: BettingAction.RaiseTo));
            f.Window = new AbilityWindowState("w", HoldemTiming.AfterBettingAction, f.Hand.Value, f.A, HoldemActionKeys.Key(BettingAction.Call));
            Assert.That(AbilityEligibility.Check(W(f.Build()), f.Catalog, f.Ability, AbilityMechanism.Triggered), Is.EqualTo(AbilityDenial.WrongAction));
            f.Window = new AbilityWindowState("w", HoldemTiming.AfterBettingAction, f.Hand.Value, f.B, HoldemActionKeys.Key(BettingAction.RaiseTo));
            Assert.That(AbilityEligibility.Check(W(f.Build()), f.Catalog, f.Ability, AbilityMechanism.Triggered), Is.EqualTo(AbilityDenial.ConditionFailed));
            f.Window = new AbilityWindowState("w", HoldemTiming.AfterBettingAction, f.Hand.Value, f.A, HoldemActionKeys.Key(BettingAction.RaiseTo));
            Assert.That(AbilityEligibility.Check(W(f.Build()), f.Catalog, f.Ability, AbilityMechanism.Triggered), Is.EqualTo(AbilityDenial.None));
        }

        [Test] public void HandResetKeepsMatchCountersAndDropsTemporaryMarks()
        {
            var f = new Fixture(); var counters = new[] { new AbilityCounter("streak", 2, MemoryScope.Encounter), new AbilityCounter("calls", 3, MemoryScope.Round) };
            var memory = new AbilityMemory(counters, new[] { new AbilityMark("card", EntityRef.Card(f.Cards[0].Id), MemoryScope.Round) });
            counters[0] = new AbilityCounter("streak", 999, MemoryScope.Encounter);
            var reset = memory.ResetRound();
            Assert.That(reset.CounterValue("streak"), Is.EqualTo(2)); Assert.That(reset.CounterValue("calls"), Is.Zero);
            Assert.That(reset.Marks.Count, Is.Zero); Assert.That(memory.CounterValue("calls"), Is.EqualTo(3));
            Assert.Throws<ArgumentException>(() => new AbilityMark("bad", EntityRef.Card(f.Cards[0].Id), MemoryScope.Encounter));
        }

        [Test] public void MemoryRejectsAmbiguousKeysAndUnknownReferences()
        {
            var f = new Fixture();
            Assert.Throws<ArgumentException>(() => new AbilityMemory(new[] { new AbilityCounter("key", 1, MemoryScope.Encounter) },
                new[] { new AbilityMark("key", EntityRef.Participant(f.A), MemoryScope.Encounter) }));
            f.Abilities = new[] { WithMemory(f, new AbilityMemory(new AbilityCounter[0],
                new[] { new AbilityMark("mark", EntityRef.Card(new CardInstanceId("missing")), MemoryScope.Round) })) };
            Assert.That(Assert.Throws<StateValidationException>(() => StateValidator.Validate(f.Build(), f.Catalog)).Code, Is.EqualTo("UnknownReference"));
        }

        [Test] public void SelectionCanBeMadeByTheTargetInsteadOfAbilityOwner()
        {
            var f = new Fixture(); f.Decision = DecisionState.SelectTargets(new DecisionId("d"), f.B, Cards(f), f.Continuation());
            StateValidator.Validate(f.Build(), f.Catalog);
            Assert.That(SelectionResolver.Resolve(f.Decision, f.B, f.Decision.Id, new[] { "choice-0" }).Selected.Single().Entity.Id,
                Is.EqualTo(f.Cards[0].Id.Value));
            Assert.That(Assert.Throws<StateValidationException>(() => SelectionResolver.Resolve(f.Decision, f.A, f.Decision.Id, new[] { "choice-0" })).Code,
                Is.EqualTo("WrongDecisionActor"));
        }

        [Test] public void SelectionRejectsStaleDecisionUnknownAndRepeatedKeys()
        {
            var f = new Fixture(); var d = DecisionState.SelectTargets(new DecisionId("d"), f.A, Cards(f, ordered: true), f.Continuation());
            Assert.That(Assert.Throws<StateValidationException>(() => SelectionResolver.Resolve(d, f.A, new DecisionId("old"), new[] { "choice-0", "choice-1" })).Code,
                Is.EqualTo("StaleDecision"));
            Assert.Throws<StateValidationException>(() => SelectionResolver.Resolve(d, f.A, d.Id, new[] { "choice-0", "choice-0" }));
            Assert.Throws<StateValidationException>(() => SelectionResolver.Resolve(d, f.A, d.Id, new[] { "choice-0", "missing" }));
            Assert.Throws<StateValidationException>(() => SelectionResolver.Resolve(d, f.A, d.Id, new[] { "choice-0" }));
        }

        [Test] public void OrderedSelectionPreservesOrderAndUnorderedSelectionCanonicalizesIt()
        {
            var f = new Fixture(); var ordered = Cards(f, ordered: true);
            var d = DecisionState.SelectTargets(new DecisionId("d"), f.A, ordered, f.Continuation());
            Assert.That(SelectionResolver.Resolve(d, f.A, d.Id, new[] { "choice-1", "choice-0" }).Selected.Select(c => c.Key), Is.EqualTo(new[] { "choice-1", "choice-0" }));
            var unordered = new SelectionSpec(SelectionTargetKind.Cards, ordered.Candidates, 2, 2);
            d = DecisionState.SelectTargets(new DecisionId("d"), f.A, unordered, f.Continuation());
            Assert.That(SelectionResolver.Resolve(d, f.A, d.Id, new[] { "choice-1", "choice-0" }).Selected.Select(c => c.Key), Is.EqualTo(new[] { "choice-0", "choice-1" }));
        }

        [Test] public void SkipAndTimeoutUseExplicitPolicies()
        {
            var f = new Fixture(); var d = DecisionState.SelectTargets(new DecisionId("d"), f.A, Cards(f, optional: true), f.Continuation());
            Assert.That(SelectionResolver.ResolveTimeout(d, d.Id).Skipped, Is.True);
            Assert.Throws<StateValidationException>(() => SelectionResolver.Resolve(d, f.A, d.Id, new[] { "choice-0" }, true));
            d = DecisionState.SelectTargets(new DecisionId("d"), f.A, Cards(f), f.Continuation());
            Assert.That(SelectionResolver.ResolveTimeout(d, d.Id).Selected.Single().Key, Is.EqualTo("choice-0"));
            Assert.Throws<StateValidationException>(() => SelectionResolver.Resolve(d, f.A, d.Id, new string[0], true));
            Assert.Throws<StateValidationException>(() => new SelectionSpec(SelectionTargetKind.Options, new[] { new SelectionCandidate("x") }, 1, 1,
                timeoutPolicy: SelectionTimeoutPolicy.Skip));
        }

        [Test] public void ParticipantsAndOptionsUseTheSameSelectionContract()
        {
            var f = new Fixture(); var spec = new SelectionSpec(SelectionTargetKind.Participants,
                new[] { new SelectionCandidate("opponent", EntityRef.Participant(f.B)) }, 1, 1);
            f.Decision = DecisionState.SelectTargets(new DecisionId("d"), f.A, spec, f.Continuation());
            StateValidator.Validate(f.Build(), f.Catalog);
            Assert.That(SelectionResolver.Resolve(f.Decision, f.A, f.Decision.Id, new[] { "opponent" }).Selected[0].Entity.Kind, Is.EqualTo(EntityKind.Participant));
            spec = new SelectionSpec(SelectionTargetKind.Options, new[] { new SelectionCandidate("hearts"), new SelectionCandidate("spades") }, 1, 1);
            f.Decision = DecisionState.SelectTargets(new DecisionId("d"), f.A, spec, f.Continuation());
            StateValidator.Validate(f.Build(), f.Catalog);
        }

        [Test] public void SelectionCannotAliasOneEntityOrMixCandidateTypes()
        {
            var f = new Fixture();
            Assert.Throws<StateValidationException>(() => new SelectionSpec(SelectionTargetKind.Cards,
                new[] { new SelectionCandidate("a", EntityRef.Card(f.Cards[0].Id)), new SelectionCandidate("b", EntityRef.Card(f.Cards[0].Id)) }, 2, 2));
            Assert.Throws<StateValidationException>(() => new SelectionSpec(SelectionTargetKind.Participants,
                new[] { new SelectionCandidate("a", EntityRef.Card(f.Cards[0].Id)) }, 1, 1));
        }

        [Test] public void SnapshotKnowledgeIsPrivateAndRetainsTheObservedFace()
        {
            var f = new Fixture(); f.Knowledge = new[] { Grant(f) };
            var observed = KnowledgeProjector.Project(W(f.Build()), f.Catalog, f.A, new FixedFace()).Single();
            Assert.That(observed.Face.Rank, Is.EqualTo(Rank.King)); Assert.That(observed.Face.Suit, Is.EqualTo(Suit.Hearts));
            Assert.That(observed.IsHistorical, Is.True); Assert.That(observed.ObservedAtRevision, Is.EqualTo(40));
            Assert.That(KnowledgeProjector.Project(W(f.Build()), f.Catalog, f.B, new FixedFace()).Count, Is.Zero);
            Assert.Throws<ArgumentException>(() => KnowledgeProjector.Project(W(f.Build()), f.Catalog, new ParticipantId("outsider"), new FixedFace()));
        }

        [Test] public void LiveKnowledgeUsesAuthoritativeFaceAndDoesNotRevealReplacementCard()
        {
            var f = new Fixture(); f.Knowledge = new[] { Grant(f, KnowledgeMode.TrackCardInZone) };
            var observed = KnowledgeProjector.Project(W(f.Build()), f.Catalog, f.A, new FixedFace()).Single();
            Assert.That(observed.Face.Rank, Is.EqualTo(Rank.Two)); Assert.That(observed.IsHistorical, Is.False);
            var moved = f.Cards[2].Id; var replacement = f.Cards[6].Id;
            f.Zones[0] = new CardZone(f.Zones[0].Id, CardZoneKinds.Deck, null, new[] { moved, f.Cards[7].Id, f.Cards[8].Id });
            f.Zones[3] = new CardZone(f.Zones[3].Id, HoldemZoneKinds.HoleCards, f.B, new[] { replacement, f.Cards[3].Id });
            Assert.That(KnowledgeProjector.Project(W(f.Build()), f.Catalog, f.A, new FixedFace()).Count, Is.Zero);
        }

        [Test] public void PublicKnowledgeIsVisibleToAllAndStreetExpiryHidesIt()
        {
            var f = new Fixture(); f.Knowledge = new[] { Grant(f, everyone: true, expires: Street.Preflop) };
            foreach (var viewer in new[] { f.A, f.B, f.C }) Assert.That(KnowledgeProjector.Project(W(f.Build()), f.Catalog, viewer, new FixedFace()).Count, Is.EqualTo(1));
            f.Betting = new BettingRoundState(Street.Flop, f.Betting.CurrentBetTo, f.Betting.LastFullRaiseSize, f.Betting.PendingActors, f.Betting.Players);
            Assert.That(KnowledgeProjector.Project(W(f.Build()), f.Catalog, f.A, new FixedFace()).Count, Is.Zero);
        }

        [Test] public void HandEndExpiresKnowledge()
        {
            var f = new Fixture(); f.Knowledge = new[] { Grant(f) }; f.Settled();
            Assert.That(KnowledgeProjector.Project(W(f.Build()), f.Catalog, f.A, new FixedFace()).Count, Is.Zero);
        }

        [Test] public void AbilityWindowMustMatchActualPhaseAndHand()
        {
            var f = new Fixture(); f.Window = new AbilityWindowState("w", HoldemTiming.BeforeShowdown, f.Hand.Value);
            Assert.That(Assert.Throws<StateValidationException>(() => StateValidator.Validate(f.Build(), f.Catalog)).Code, Is.EqualTo("InvalidAbilityWindowPhase"));
            f.Window = new AbilityWindowState("w", HoldemTiming.BeforeBettingAction, "old");
            Assert.That(Assert.Throws<StateValidationException>(() => StateValidator.Validate(f.Build(), f.Catalog)).Code, Is.EqualTo("UnknownRound"));
        }

        [Test] public void PostSettlementChoiceAndContinuationCanBeSaved()
        {
            var f = new Fixture(); f.Settled(); f.Window = new AbilityWindowState("w", HoldemTiming.AfterHandSettled, f.Hand.Value);
            var spec = new SelectionSpec(SelectionTargetKind.Options, new[] { new SelectionCandidate("energy"), new SelectionCandidate("shield") }, 1, 1);
            f.Decision = DecisionState.SelectTargets(new DecisionId("reward"), f.A, spec, f.Continuation());
            f.Resolution = new ResolutionState(new[] { f.Continuation() });
            string json = MatchSnapshotCodec.Serialize(f.Build(), f.Catalog);
            var restored = MatchSnapshotCodec.Deserialize(json, f.Catalog, f.Definition);
            Assert.That(restored.ActiveDecision.Selection.Candidates.Count, Is.EqualTo(2));
        }

        [Test] public void AbilityStateRoundTripsWithKnowledgeMarksAndOrdering()
        {
            var f = new Fixture(); Configure(f, Definition(f));
            f.Abilities = new[] { WithMemory(f, new AbilityMemory(new[] { new AbilityCounter("loss-streak", 2, MemoryScope.Encounter) },
                new[] { new AbilityMark("marked-card", EntityRef.Card(f.Cards[0].Id), MemoryScope.Round) })) };
            f.Decision = DecisionState.SelectTargets(new DecisionId("order"), f.B, Cards(f, ordered: true), f.Continuation());
            f.Knowledge = new[] { Grant(f) }; string json = MatchSnapshotCodec.Serialize(f.Build(), f.Catalog);
            var restored = MatchSnapshotCodec.Deserialize(json, f.Catalog, f.Definition);
            Assert.That(MatchSnapshotCodec.Serialize(restored, f.Catalog), Is.EqualTo(json));
            Assert.That(restored.Abilities[0].Memory.CounterValue("loss-streak"), Is.EqualTo(2));
            Assert.That(restored.ActiveDecision.Selection.OrderMatters, Is.True);
            Assert.That(KnowledgeProjector.Project(W(restored), f.Catalog, f.B, new FixedFace()).Count, Is.Zero);
        }

        [Test] public void SnapshotRejectsMissingMemoryKnowledgeAndOldSchema()
        {
            var f = new Fixture(); string json = MatchSnapshotCodec.Serialize(f.Build(), f.Catalog);
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json.Replace("\"SchemaVersion\":" + MatchSnapshotCodec.SchemaVersion, "\"SchemaVersion\":1"), f.Catalog, f.Definition));
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json.Replace("\"Knowledge\":[]", "\"Knowledge\":null"), f.Catalog, f.Definition));
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json.Replace("\"Version\":1", "\"Version\":99"), f.Catalog, f.Definition));
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json.Replace("\"Memory\":", "\"IgnoredMemory\":"), f.Catalog, f.Definition));
        }

        [Test] public void SnapshotRejectsTamperedChoiceReferencesAndKnowledgeAudience()
        {
            var f = new Fixture(); f.Knowledge = new[] { Grant(f) };
            f.Decision = DecisionState.SelectTargets(new DecisionId("d"), f.A, Cards(f), f.Continuation());
            string json = MatchSnapshotCodec.Serialize(f.Build(), f.Catalog);
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json.Replace("\"ViewerId\":{\"Value\":\"player-a\"}", "\"ViewerId\":{\"Value\":\"unknown\"}"), f.Catalog, f.Definition));
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json.Replace("\"Kind\":3,\"Id\":\"card-1\"", "\"Kind\":3,\"Id\":\"missing-card\""), f.Catalog, f.Definition));
        }

        private sealed class SwapPlan : IActivatedAbility
        {
            private readonly SelectionSpec selection;
            public SwapPlan(SelectionSpec selection) { this.selection = selection; }
            public IReadOnlyList<EffectIntent> Plan(AbilityContext c) => new EffectIntent[] { new RequestSelectionEffect(c.Ability.Id, c.Ability.OwnerId, selection) };
        }
        private sealed class GainEnergy : ITriggeredAbility
        { public IReadOnlyList<EffectIntent> Plan(AbilityContext c) => new EffectIntent[] { new ChangeResourceEffect(c.Ability.Id, c.Ability.OwnerId, ResourceKind.Energy, 1) }; }
        private sealed class ChangeSuit : IRuleModifier<CardFaceQuery, CardFace>
        { public CardFace Apply(AbilityContext c, CardFaceQuery q, CardFace current) => new CardFace(current.Rank, Suit.Hearts); }
        private sealed class NumericModifier : IRuleModifier<string, int>
        {
            private readonly bool multiply;
            public NumericModifier(bool multiply) { this.multiply = multiply; }
            public int Apply(AbilityContext c, string q, int current) => multiply ? current * 2 : current + 1;
        }
        private sealed class Cancel : IEffectReplacement
        { public EffectReplacement Replace(AbilityContext c, EffectIntent e) => EffectReplacement.Cancel(); }
        private sealed class Expand : IEffectReplacement
        { public EffectReplacement Replace(AbilityContext c, EffectIntent e) => EffectReplacement.With(new[] { e, e }); }
        private sealed class Bonus : IOutcomeModifier<int>
        { public int Apply(AbilityContext c, int proposed) => proposed + 10; }
        private sealed class Never : IAbilityCondition
        { public bool IsSatisfied(AbilityContext context) => false; }
        private sealed class MustNotRun : IEffectReplacement
        { public EffectReplacement Replace(AbilityContext c, EffectIntent e) => throw new Exception("Canceled effects must not reach later hooks."); }
        private sealed class PeekPlan : IActivatedAbility
        {
            private readonly KnowledgeGrant grant;
            public PeekPlan(KnowledgeGrant grant) { this.grant = grant; }
            public IReadOnlyList<EffectIntent> Plan(AbilityContext c) => new EffectIntent[] { new GrantKnowledgeEffect(c.Ability.Id, grant) };
        }

        [Test] public void ActiveAbilitiesCannotInterruptAChoiceOrPendingResolution()
        {
            var f = new Fixture(); Configure(f, Definition(f));
            f.Decision = DecisionState.SelectTargets(new DecisionId("pending"), f.B, Cards(f), f.Continuation());
            Assert.That(AbilityEligibility.Check(W(f.Build()), f.Catalog, f.Ability, AbilityMechanism.Activated, f.A), Is.EqualTo(AbilityDenial.ResolutionPending));
            f.Decision = null; f.Resolution = new ResolutionState(new[] { f.Continuation() });
            Assert.That(AbilityEligibility.Check(W(f.Build()), f.Catalog, f.Ability, AbilityMechanism.Activated, f.A), Is.EqualTo(AbilityDenial.ResolutionPending));
        }

        [Test] public void CustomConditionsGateBothActivePlansAndRuleHooks()
        {
            var f = new Fixture(); Configure(f, Definition(f));
            Assert.Throws<InvalidOperationException>(() => AbilityHooks.PlanActivated(new AbilityContext(W(f.Build()), f.Catalog, f.Ability), f.A,
                new SwapPlan(Cards(f)), new[] { new Never() }));
            Configure(f, Definition(f, AbilityMechanism.RuleModifier));
            var hooks = new[] { new AbilityHook<IRuleModifier<string, int>>(f.Ability, new NumericModifier(true), new[] { new Never() }) };
            Assert.That(AbilityHooks.Modify(W(f.Build()), f.Catalog, "cost", 3, hooks), Is.EqualTo(3));
        }

        [Test] public void PeekPlanCarriesAnExplicitPrivateObservation()
        {
            var f = new Fixture(); Configure(f, Definition(f));
            var effect = (GrantKnowledgeEffect)AbilityHooks.PlanActivated(new AbilityContext(W(f.Build()), f.Catalog, f.Ability), f.A, new PeekPlan(Grant(f))).Single();
            Assert.That(effect.Grant.ViewerId, Is.EqualTo(f.A)); Assert.That(effect.Grant.Mode, Is.EqualTo(KnowledgeMode.Snapshot));
            Assert.That(f.Build().Knowledge.Count, Is.Zero);
        }

        [Test] public void LossStreakCanGateANextHandTriggerWithoutLeakingHandMemory()
        {
            var f = new Fixture(); Configure(f, Definition(f, AbilityMechanism.Triggered,
                conditions: new[] { new AbilityCondition(ConditionKind.CounterAtLeast, 2, "losses") }, timing: HoldemTiming.BeforeHand));
            f.Settled(); f.Window = new AbilityWindowState("next-hand", HoldemTiming.BeforeHand);
            var hooks = new[] { new AbilityHook<ITriggeredAbility>(f.Ability, new GainEnergy()) };
            f.Abilities = new[] { WithMemory(f, AbilityMemory.Empty.WithCounter("losses", 1, MemoryScope.Encounter).ResetRound()) };
            Assert.That(AbilityHooks.PlanTriggered(W(f.Build()), f.Catalog, hooks).Count, Is.Zero);
            f.Abilities = new[] { WithMemory(f, AbilityMemory.Empty.WithCounter("losses", 2, MemoryScope.Encounter).ResetRound()) };
            Assert.That(AbilityHooks.PlanTriggered(W(f.Build()), f.Catalog, hooks).Count, Is.EqualTo(1));
        }

        [Test] public void RuleHookTieBreakUsesDealerRelativeSeatThenOrdinalAbilityId()
        {
            var f = new Fixture(); var other = new AbilityInstanceId("z-other"); var otherDefinition = new AbilityDefinitionId("other-def");
            Configure(f, Definition(f, AbilityMechanism.RuleModifier), new AbilityDefinition(otherDefinition, AbilityMechanism.RuleModifier,
                AbilityModes.All, new[] { HoldemTiming.BeforeBettingAction }, new AbilityCondition[0]));
            f.Abilities = f.Abilities.Concat(new[] { new AbilityInstance(other, otherDefinition, f.C, EntityRef.Match(f.Match)) }).ToArray();
            var hooks = new[] { new AbilityHook<IRuleModifier<string, int>>(f.Ability, new NumericModifier(false)),
                new AbilityHook<IRuleModifier<string, int>>(other, new NumericModifier(true)) };
            Assert.That(AbilityHooks.Modify(W(f.Build()), f.Catalog, "test", 2, hooks), Is.EqualTo(5));
            f.Abilities[1] = new AbilityInstance(other, otherDefinition, f.A, EntityRef.Match(f.Match));
            Assert.That(AbilityHooks.Modify(W(f.Build()), f.Catalog, "test", 2, hooks.Reverse()), Is.EqualTo(6));
        }

        [Test] public void CancellationStopsLaterHooksAndExpansionIsBounded()
        {
            var f = new Fixture();
            var ids = Enumerable.Range(0, 7).Select(i => new AbilityInstanceId("a-" + i)).ToArray();
            var defs = Enumerable.Range(0, 7).Select(i => new AbilityDefinition(new AbilityDefinitionId("d-" + i), AbilityMechanism.EffectReplacement,
                AbilityModes.All, new[] { HoldemTiming.BeforeBettingAction }, new AbilityCondition[0], i)).ToArray();
            Configure(f, defs);
            f.Abilities = ids.Select((id, i) => new AbilityInstance(id, defs[i].Id, f.A, EntityRef.Match(f.Match))).ToArray();
            var draw = new DrawCardsEffect(null, f.Zones[0].Id, f.Zones[2].Id, 1);
            var canceled = AbilityHooks.Replace(W(f.Build()), f.Catalog, draw, new[] { new AbilityHook<IEffectReplacement>(ids[0], new Cancel()),
                new AbilityHook<IEffectReplacement>(ids[1], new MustNotRun()) });
            Assert.That(canceled.Count, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => AbilityHooks.Replace(W(f.Build()), f.Catalog, draw,
                ids.Select(id => new AbilityHook<IEffectReplacement>(id, new Expand()))));
        }

        [Test] public void SnapshotRejectsForgedKnowledgeTimeAndMatchMode()
        {
            var f = new Fixture(); f.Knowledge = new[] { Grant(f) }; string json = MatchSnapshotCodec.Serialize(f.Build(), f.Catalog);
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json.Replace("\"GrantedAtRevision\":40", "\"GrantedAtRevision\":43"), f.Catalog, f.Definition));
            var pvp = new MatchDefinition("holdem", "1", "content-1", 6, new Chips(10), new Chips(20), MatchMode.Pvp);
            Assert.Throws<SnapshotException>(() => MatchSnapshotCodec.Deserialize(json, f.Catalog, pvp));
        }

        [Test] public void SwapPlanProducesAChoiceWithoutModifyingCardsOrPayingCosts()
        {
            var f = new Fixture(); Configure(f, Definition(f)); var state = f.Build();
            var result = AbilityHooks.PlanActivated(new AbilityContext(W(state), f.Catalog, f.Ability), f.A, new SwapPlan(Cards(f)));
            Assert.That(result.Single(), Is.TypeOf<RequestSelectionEffect>());
            Assert.That(state.CurrentHand.Zones[2].Cards.Count, Is.EqualTo(2)); Assert.That(state.Participants[0].Resources.Energy, Is.EqualTo(2));
            Assert.Throws<InvalidOperationException>(() => AbilityHooks.PlanActivated(new AbilityContext(W(state), f.Catalog, f.Ability), f.B, new SwapPlan(Cards(f))));
        }

        [Test] public void RaiseTriggerProducesEnergyOnlyForTheMatchingAction()
        {
            var f = new Fixture(); Configure(f, Definition(f, AbilityMechanism.Triggered, timing: HoldemTiming.AfterBettingAction, requiredAction: BettingAction.RaiseTo));
            var hooks = new[] { new AbilityHook<ITriggeredAbility>(f.Ability, new GainEnergy()) };
            f.Window = new AbilityWindowState("w", HoldemTiming.AfterBettingAction, f.Hand.Value, f.A, HoldemActionKeys.Key(BettingAction.Call));
            Assert.That(AbilityHooks.PlanTriggered(W(f.Build()), f.Catalog, hooks).Count, Is.Zero);
            f.Window = new AbilityWindowState("w", HoldemTiming.AfterBettingAction, f.Hand.Value, f.A, HoldemActionKeys.Key(BettingAction.RaiseTo));
            Assert.That(((ChangeResourceEffect)AbilityHooks.PlanTriggered(W(f.Build()), f.Catalog, hooks).Single()).Delta, Is.EqualTo(1));
        }

        [Test] public void CardFaceHookChangesDerivedFaceWithoutRewritingDefinition()
        {
            var f = new Fixture(); Configure(f, Definition(f, AbilityMechanism.RuleModifier));
            var face = AbilityHooks.Modify(W(f.Build()), f.Catalog, new CardFaceQuery(f.Cards[0].Id), new CardFace(Rank.Ace, Suit.Spades),
                new[] { new AbilityHook<IRuleModifier<CardFaceQuery, CardFace>>(f.Ability, new ChangeSuit()) });
            Assert.That(face.Suit, Is.EqualTo(Suit.Hearts)); Assert.That(f.Catalog.Get(f.CardDefinition).Suit, Is.EqualTo(Suit.Spades));
        }

        [Test] public void RuleHookPriorityDoesNotDependOnRegistrationOrder()
        {
            var f = new Fixture(); var other = new AbilityInstanceId("other"); var otherDefinition = new AbilityDefinitionId("other-def");
            Configure(f, Definition(f, AbilityMechanism.RuleModifier, priority: 10),
                new AbilityDefinition(otherDefinition, AbilityMechanism.RuleModifier, AbilityModes.All, new[] { HoldemTiming.BeforeBettingAction }, new AbilityCondition[0], 0));
            f.Abilities = f.Abilities.Concat(new[] { new AbilityInstance(other, otherDefinition, f.B, EntityRef.Participant(f.B)) }).ToArray();
            var hooks = new[] { new AbilityHook<IRuleModifier<string, int>>(f.Ability, new NumericModifier(true)),
                new AbilityHook<IRuleModifier<string, int>>(other, new NumericModifier(false)) };
            Assert.That(AbilityHooks.Modify(W(f.Build()), f.Catalog, "cost", 2, hooks), Is.EqualTo(6));
            Assert.That(AbilityHooks.Modify(W(f.Build()), f.Catalog, "cost", 2, hooks.Reverse()), Is.EqualTo(6));
            Assert.Throws<ArgumentException>(() => AbilityHooks.Modify(W(f.Build()), f.Catalog, "cost", 2, new[] { hooks[0], hooks[0] }));
        }

        [Test] public void ReplacementCanInterceptCoreDrawAndCancellationIsTerminal()
        {
            var f = new Fixture(); Configure(f, Definition(f, AbilityMechanism.EffectReplacement));
            var draw = new DrawCardsEffect(null, f.Zones[0].Id, f.Zones[2].Id, 1);
            Assert.That(AbilityHooks.Replace(W(f.Build()), f.Catalog, draw, new[] { new AbilityHook<IEffectReplacement>(f.Ability, new Cancel()) }).Count, Is.Zero);
            Assert.That(AbilityHooks.Replace(W(f.Build()), f.Catalog, draw, new[] { new AbilityHook<IEffectReplacement>(f.Ability, new Expand()) }).Count, Is.EqualTo(2));
        }

        [Test] public void OutcomeHooksDoNotModifyCommittedSettlement()
        {
            var f = new Fixture(); Configure(f, Definition(f, AbilityMechanism.OutcomeModifier, timing: HoldemTiming.AfterHandSettled)); f.Settled();
            f.Window = new AbilityWindowState("w", HoldemTiming.AfterHandSettled, f.Hand.Value);
            var state = f.Build(); var bonus = AbilityHooks.ModifyOutcome(W(state), f.Catalog, 100, new[] { new AbilityHook<IOutcomeModifier<int>>(f.Ability, new Bonus()) });
            Assert.That(bonus, Is.EqualTo(110)); Assert.That(state.CurrentHand.Settlement.Awards[0].Amount.Value, Is.EqualTo(300));
        }
    }
}
