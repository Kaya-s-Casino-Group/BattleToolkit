using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    /// <summary>Only Core/Abilities types: the same behavior and executor work without either mode engine.</summary>
    [TestFixture]
    public sealed class AbilityModuleTests
    {
        private static readonly ParticipantId A = new ParticipantId("a");
        private static readonly ParticipantId B = new ParticipantId("b");
        private static readonly AbilityDefinitionId Definition = new AbilityDefinitionId("shared");
        private static readonly StatusDefinitionId Status = new StatusDefinitionId("test.status");
        private static readonly CardDefinitionId CardDefinition = new CardDefinitionId("test.card");
        private static readonly CardInstanceId Card = new CardInstanceId("card");
        private static readonly CardInstanceId DeckCard = new CardInstanceId("deck-card");
        private static readonly ZoneId Hand = new ZoneId("hand");
        private static readonly ZoneId Deck = new ZoneId("deck");
        private static readonly ZoneId Discard = new ZoneId("discard");
        private static readonly AbilityInstanceId Ability = new AbilityInstanceId("ability");

        private static ContentCatalog Catalog(AbilityMechanism mechanism) => new ContentCatalog("shared.test.1",
            new[] { new CardDefinition(CardDefinition, Rank.Ace, Suit.Hearts, new string[0]) },
            new[] { new AbilityDefinition(Definition, mechanism, AbilityModes.All,
                new[] { "mode.one.action", "mode.two.action" }, new AbilityCondition[0]) }, new[] { Status });
        private static AbilityWorld World(string timing = "mode.one.action", IEnumerable<AbilityInstance> abilities = null,
            IEnumerable<StatusInstance> statuses = null, IEnumerable<KnowledgeGrant> knowledge = null, bool chips = true,
            bool cards = true, bool reverseOwners = false)
        {
            var players = new[] { new AbilityParticipant(A, true, new ResourceState(new Chips(50), 5)),
                new AbilityParticipant(B, true, new ResourceState(new Chips(50), 5)) };
            return new AbilityWorld("encounter", 4, true, MatchMode.Solo, "round", "first", A,
                reverseOwners ? players.Reverse() : players,
                new[] { new CardInstance(Card, CardDefinition), new CardInstance(DeckCard, CardDefinition) },
                new[] { new CardZone(Hand, CardZoneKinds.Hand, A, new[] { Card }), new CardZone(Deck, CardZoneKinds.Deck, null, new[] { DeckCard }),
                    new CardZone(Discard, CardZoneKinds.Discard, null, new CardInstanceId[0]) },
                abilities ?? new[] { new AbilityInstance(Ability, Definition, A, EntityRef.Participant(A), remainingCharges: 1) },
                statuses ?? new StatusInstance[0], DecisionState.ModeAction(new DecisionId("action"), A), ResolutionState.Empty,
                new AbilityWindowState("window", timing, "round", A), knowledge ?? new KnowledgeGrant[0], chips, cards);
        }
        private static AbilityWorld Restore(AbilityWorld world)
        {
            var serializer = new DataContractJsonSerializer(typeof(AbilityWorld));
            using (var stream = new MemoryStream())
            { serializer.WriteObject(stream, world); stream.Position = 0; return (AbilityWorld)serializer.ReadObject(stream); }
        }
        private sealed class Energy : ITriggeredAbility, IActivatedAbility
        {
            public IReadOnlyList<EffectIntent> Plan(AbilityContext context) =>
                new EffectIntent[] { new ChangeResourceEffect(context.Ability.Id, context.Ability.OwnerId, ResourceKind.Energy, 1) };
        }
        private sealed class Choose : IActivatedAbility, IAbilitySelectionHandler
        {
            public IReadOnlyList<EffectIntent> Plan(AbilityContext context) => new EffectIntent[] {
                new RequestSelectionEffect(context.Ability.Id, context.Ability.OwnerId, new SelectionSpec(SelectionTargetKind.Cards,
                    new[] { new SelectionCandidate("selected", EntityRef.Card(Card)) }, 1, 1)) };
            public IReadOnlyList<EffectIntent> Resume(AbilityContext context, int step, SelectionResult selection)
            {
                if (step != 1) throw new ArgumentException("Wrong continuation step.");
                return new EffectIntent[] { new MoveCardEffect(context.Ability.Id,
                    new CardInstanceId(selection.Selected.Single().Entity.Id), Hand, Discard) };
            }
        }
        private sealed class InvalidPlan : IActivatedAbility
        {
            public IReadOnlyList<EffectIntent> Plan(AbilityContext context) => new EffectIntent[] {
                new ChangeResourceEffect(context.Ability.Id, A, ResourceKind.Energy, 3),
                new MoveCardEffect(context.Ability.Id, Card, Deck, Discard) };
        }
        private sealed class Multiply : IOutcomeModifier<int>
        { public int Apply(AbilityContext context, int outcome) => checked(outcome * 2); }

        private sealed class ReorderDrawMove : IActivatedAbility
        {
            public IReadOnlyList<EffectIntent> Plan(AbilityContext context) => new EffectIntent[] {
                new ReorderCardsEffect(context.Ability.Id, Deck, context.State.Zones.Single(z => z.Id == Deck).Cards.Reverse()),
                new DrawCardsEffect(context.Ability.Id, Deck, Hand, 2),
                new MoveCardEffect(context.Ability.Id, Card, Hand, Discard) };
        }

        [Test] public void SharedZoneOperationsComposeAcrossAbilityEffectsAndSnapshotRestore()
        {
            var original = World();
            var extra = new[] { new CardInstanceId("extra-1"), new CardInstanceId("extra-2") };
            var initial = new AbilityWorld(original.Id, original.Revision, original.IsRunning, original.Mode, original.ScopeId, original.PhaseKey,
                original.CurrentActorId, original.Participants, original.Cards.Concat(extra.Select(c => new CardInstance(c, CardDefinition))),
                original.Zones.Select(z => z.Id == Deck ? z.InsertRange(z.Cards.Count, extra) : z), original.Abilities, original.Statuses,
                original.ActiveDecision, original.Resolution, original.AbilityWindow, original.Knowledge);
            var catalog = Catalog(AbilityMechanism.Activated);
            var executor = new AbilityExecutor(new AbilityRuntime(new[] { new AbilityBehavior(Definition, activated: new ReorderDrawMove()) }), catalog);
            var result = executor.Activate(initial, "window", Ability, A);
            var restored = Restore(result.World); AbilityWorldValidator.Validate(restored, catalog);
            CollectionAssert.AreEqual(new[] { DeckCard, extra[0] }, restored.Zones.Single(z => z.Id == Hand).Cards);
            CollectionAssert.AreEqual(new[] { extra[1] }, restored.Zones.Single(z => z.Id == Deck).Cards);
            CollectionAssert.AreEqual(new[] { Card }, restored.Zones.Single(z => z.Id == Discard).Cards);
            CollectionAssert.AreEqual(new[] { DeckCard, extra[0], extra[1] }, initial.Zones.Single(z => z.Id == Deck).Cards);
            CollectionAssert.AreEqual(new[] { Card }, initial.Zones.Single(z => z.Id == Hand).Cards);
        }

        [Test]
        public void SameRegistrationRunsAcrossModeEventKeysWithExplicitOwnerAndSlotOrder()
        {
            var catalog = Catalog(AbilityMechanism.Triggered);
            var runtime = new AbilityRuntime(new[] { new AbilityBehavior(Definition, triggered: new Energy()) });
            var executor = new AbilityExecutor(runtime, catalog);
            var abilities = new[] { new AbilityInstance(new AbilityInstanceId("a-second"), Definition, A, EntityRef.Participant(A), order: 2),
                new AbilityInstance(new AbilityInstanceId("a-first"), Definition, A, EntityRef.Participant(A), order: 1),
                new AbilityInstance(new AbilityInstanceId("b"), Definition, B, EntityRef.Participant(B)) };
            foreach (var key in new[] { "mode.one.action", "mode.two.action" })
            {
                var initial = World(key, abilities, reverseOwners: true); var result = executor.Triggers(initial);
                CollectionAssert.AreEqual(new[] { "b", "a-first", "a-second" }, result.Events.Select(e => e.AbilityId.Value));
                Assert.AreEqual(7, result.World.Participants.Single(p => p.Id == A).Resources.Energy);
                Assert.AreEqual(5, initial.Participants.Single(p => p.Id == A).Resources.Energy);
                Assert.AreEqual(0, executor.Triggers(Restore(result.World)).Events.Count);
            }
        }
        [Test]
        public void SavedSelectionResumesWithTimeoutAndChargesCostOnlyOnce()
        {
            var handler = new Choose(); var catalog = Catalog(AbilityMechanism.Activated);
            var runtime = new AbilityRuntime(new[] { new AbilityBehavior(Definition, activated: handler, selection: handler,
                cost: new AbilityCost(Chips.Zero, 2), cooldownRounds: 1) });
            var executor = new AbilityExecutor(runtime, catalog); var initial = World();
            var selected = executor.Activate(initial, "window", Ability, A); var saved = Restore(selected.World);
            Assert.AreEqual(3, saved.Participants[0].Resources.Energy); Assert.AreEqual(0, saved.Abilities[0].RemainingCharges);
            Assert.AreEqual(2, saved.Abilities[0].CooldownRounds); Assert.AreEqual(DecisionKind.SelectTargets, saved.ActiveDecision.Kind);
            Assert.Throws<StateValidationException>(() => executor.Select(saved, saved.ActiveDecision.Id, B, new[] { "selected" }));
            var resumed = executor.Select(saved, saved.ActiveDecision.Id, A, new string[0], timeout: true);
            Assert.AreEqual(3, resumed.World.Participants[0].Resources.Energy); Assert.AreEqual(1, resumed.World.Abilities[0].UsesThisRound);
            Assert.AreEqual(DecisionKind.ModeAction, resumed.World.ActiveDecision.Kind);
            CollectionAssert.AreEqual(new[] { Card }, resumed.World.Zones.Single(z => z.Id == Discard).Cards);
            Assert.AreEqual(1, resumed.Events.Count); Assert.AreEqual(AbilityEventKind.SelectionResolved, resumed.Events[0].Kind);
            Assert.Throws<StateValidationException>(() => executor.Select(resumed.World, saved.ActiveDecision.Id, A, new[] { "selected" }));
        }
        [Test]
        public void FailedSharedEffectLeavesResourcesCardsChargesAndResolutionUntouched()
        {
            var executor = new AbilityExecutor(new AbilityRuntime(new[] { new AbilityBehavior(Definition, activated: new InvalidPlan(),
                cost: new AbilityCost(Chips.Zero, 2)) }), Catalog(AbilityMechanism.Activated));
            var initial = World(); Assert.Throws<ArgumentException>(() => executor.Activate(initial, "window", Ability, A));
            Assert.AreEqual(5, initial.Participants[0].Resources.Energy); Assert.AreEqual(1, initial.Abilities[0].RemainingCharges);
            Assert.AreEqual(0, initial.Abilities[0].UsesThisRound); Assert.IsNull(initial.Resolution.WindowId);
            CollectionAssert.AreEqual(new[] { Card }, initial.Zones.Single(z => z.Id == Hand).Cards);
        }
        [Test]
        public void LifecycleExpiresExactPhaseAndRoundWhilePreservingPersistentMemoryAndFuturePhase()
        {
            var source = EntityRef.Participant(A);
            var statuses = new[] { new StatusInstance(new StatusInstanceId("first"), Status, source, source, 1, StatusDuration.UntilPhaseEnd("round", "first")),
                new StatusInstance(new StatusInstanceId("second"), Status, source, source, 1, StatusDuration.UntilPhaseEnd("round", "second")),
                new StatusInstance(new StatusInstanceId("round"), Status, source, source, 1, StatusDuration.UntilRoundEnd("round")),
                new StatusInstance(new StatusInstanceId("lasting"), Status, source, source, 1, StatusDuration.UntilRemoved()) };
            var memory = AbilityMemory.Empty.WithCounter("round-value", 2, MemoryScope.Round).WithCounter("persistent", 3, MemoryScope.Encounter);
            var ability = new AbilityInstance(Ability, Definition, A, source, usesThisRound: 2, cooldownRounds: 2, memory: memory);
            var grant = new KnowledgeGrant(new KnowledgeGrantId("knowledge"), source, KnowledgeAudience.Participant, A,
                KnowledgeMode.Snapshot, "round", Card, 4, new CardFace(Rank.Two, Suit.Spades), untilPhaseEnd: "first");
            var initial = World(abilities: new[] { ability }, statuses: statuses, knowledge: new[] { grant });
            var phase = AbilityLifecycle.EndPhase(initial, "first");
            CollectionAssert.AreEquivalent(new[] { "second", "round", "lasting" }, phase.Statuses.Select(s => s.Id.Value));
            Assert.AreEqual(0, phase.Knowledge.Count); Assert.AreEqual(2, phase.Abilities[0].UsesThisRound);
            var end = AbilityLifecycle.EndRound(phase); Assert.AreEqual("lasting", end.Statuses.Single().Id.Value);
            Assert.AreEqual(2, end.Abilities[0].CooldownRounds);
            var next = AbilityLifecycle.BeginRound(end); AbilityWorldValidator.Validate(next, Catalog(AbilityMechanism.Activated));
            Assert.AreEqual(0, next.Abilities[0].UsesThisRound); Assert.AreEqual(1, next.Abilities[0].CooldownRounds);
            Assert.AreEqual(0, next.Abilities[0].Memory.CounterValue("round-value")); Assert.AreEqual(3, next.Abilities[0].Memory.CounterValue("persistent"));
        }
        [Test]
        public void KnowledgeProjectionRespectsAudienceSnapshotAndTrackedLocation()
        {
            var grants = new[] { new KnowledgeGrant(new KnowledgeGrantId("snapshot"), EntityRef.Participant(A), KnowledgeAudience.Participant, A,
                KnowledgeMode.Snapshot, "round", Card, 3, new CardFace(Rank.Two, Suit.Spades)),
                new KnowledgeGrant(new KnowledgeGrantId("tracking"), EntityRef.Participant(A), KnowledgeAudience.Everyone, null,
                    KnowledgeMode.TrackCardInZone, "round", Card, 4, boundZoneId: Hand) };
            var handler = new Choose(); var catalog = Catalog(AbilityMechanism.Activated);
            var runtime = new AbilityRuntime(new[] { new AbilityBehavior(Definition, activated: handler, selection: handler) });
            var initial = World(knowledge: grants, cards: false); var before = KnowledgeProjector.Project(initial, catalog, A, runtime);
            Assert.AreEqual(2, before.Count); Assert.AreEqual(Rank.Two, before[0].Face.Rank); Assert.IsTrue(before[0].IsHistorical);
            Assert.AreEqual(Rank.Ace, before[1].Face.Rank); Assert.AreEqual(1, KnowledgeProjector.Project(initial, catalog, B, runtime).Count);
            var executor = new AbilityExecutor(runtime, catalog); var selection = executor.Activate(World(knowledge: grants), "window", Ability, A).World;
            var after = executor.Select(selection, selection.ActiveDecision.Id, A, new[] { "selected" }).World;
            Assert.AreEqual(1, KnowledgeProjector.Project(after, catalog, A, runtime).Count);
            Assert.AreEqual(0, KnowledgeProjector.Project(after, catalog, B, runtime).Count);
        }
        [Test]
        public void OutcomeRegistrationIsTypedWithoutDependingOnModeOutcomeModels()
        {
            var catalog = Catalog(AbilityMechanism.OutcomeModifier);
            var runtime = new AbilityRuntime(new[] { new AbilityBehavior(Definition, outcomeModifier: AbilityOutcomeHandler.For<int>(new Multiply())) });
            Assert.AreEqual(14, runtime.ModifyOutcome(World(), catalog, 7));
            Assert.AreEqual("untouched", runtime.ModifyOutcome(World(), catalog, "untouched"));
        }
        [Test]
        public void ModeResourcePolicySkipsUnaffordableTriggerAndRejectsActivatedCost()
        {
            var trigger = new AbilityExecutor(new AbilityRuntime(new[] { new AbilityBehavior(Definition, triggered: new Energy(),
                cost: new AbilityCost(new Chips(1), 0)) }), Catalog(AbilityMechanism.Triggered));
            Assert.AreEqual(0, trigger.Triggers(World(chips: false)).Events.Count);
            var activation = new AbilityExecutor(new AbilityRuntime(new[] { new AbilityBehavior(Definition, activated: new Energy(),
                cost: new AbilityCost(new Chips(1), 0)) }), Catalog(AbilityMechanism.Activated));
            Assert.Throws<ArgumentException>(() => activation.Activate(World(chips: false), "window", Ability, A));
        }
    }
}
