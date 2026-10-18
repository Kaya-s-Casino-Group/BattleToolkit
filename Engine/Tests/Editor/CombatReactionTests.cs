using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CombatReactionTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), Enemy = new ParticipantId("enemy"), Ally = new ParticipantId("ally");
        private static readonly StatusDefinitionId Control = new StatusDefinitionId("control"), Immunity = new StatusDefinitionId("immunity");

        private static CombatState Create(int playerHealth = 10, int enemyHealth = 20, IEnumerable<StatusInstance> statuses = null) =>
            new CombatState("reactions", 0, new[] {
                new CombatantState(Player, "hero", "players", 20, playerHealth),
                new CombatantState(Enemy, "enemy", "enemies", 20, enemyHealth),
                new CombatantState(Ally, "ally", "players", 20, 20)
            }, statuses);
        private static CombatEffect Damage(long amount = 1) => new CombatEffect(CombatEffectKind.Damage, Player, Enemy, amount);
        private static CombatEffect Block(long amount = 1) => new CombatEffect(CombatEffectKind.GainBlock, Player, Player, amount);
        private static StatusInstance Status(string id, StatusDefinitionId definition, ParticipantId target, StatusDuration duration = null) =>
            new StatusInstance(new StatusInstanceId(id), definition, EntityRef.Participant(Player), EntityRef.Participant(target), 1,
                duration ?? StatusDuration.UntilRemoved());
        private static CombatStatusCatalog Catalog() => new CombatStatusCatalog("reaction-statuses.1", new[] {
            new CombatStatusDefinition(Control, StatusTags.Control, new StatusBehaviorSpec[] { new BlockActionWindowSpec() }, StatusLifetimeSpec.ForTriggers(1)),
            new CombatStatusDefinition(Immunity, StatusTags.None, new StatusBehaviorSpec[] { new PreventStatusApplicationSpec(StatusTags.Control) }, StatusLifetimeSpec.ForTriggers(1))
        });

        private sealed class Provider : ICombatReactionProvider
        {
            private readonly Func<CombatReactionContext, IEnumerable<CombatReaction>> react;
            public readonly List<CombatReactionContext> Calls = new List<CombatReactionContext>();
            public Provider(Func<CombatReactionContext, IEnumerable<CombatReaction>> react) { this.react = react; }
            public IEnumerable<CombatReaction> React(CombatReactionContext context) { Calls.Add(context); return react(context); }
        }
        private sealed class Incoming : IIncomingDamageModifier
        {
            private readonly Func<DamageModifierContext, long> amount;
            public readonly List<DamageModifierContext> Calls = new List<DamageModifierContext>();
            public Incoming(Func<DamageModifierContext, long> amount) { this.amount = amount; }
            public DamageModification Modify(DamageModifierContext context)
            {
                Calls.Add(context);
                return new DamageModification("reaction-test.1", "read the current settlement state", context.Amount, amount(context));
            }
        }
        private static void Rejected(CombatState initial, CombatTransition result, CombatError? error = null)
        {
            Assert.IsFalse(result.Accepted);
            if (error.HasValue) Assert.AreEqual(error.Value, result.Error);
            Assert.AreSame(initial, result.State); Assert.AreEqual(0, initial.Revision);
            Assert.IsEmpty(result.RuleEvents); Assert.IsEmpty(result.Events);
        }

        [Test] public void ReactionsRunDepthFirstBeforeTheNextPrimaryAndKeepExactCausalFacts()
        {
            var provider = new Provider(context => {
                var provenance = context.Event.Provenance;
                if (provenance.Origin == CombatEffectOrigin.Primary && context.Event.Index == 0)
                    return new[] {
                        new CombatReaction(Damage(2), "first", Player, canTriggerReactions: true, sourceInstanceId: "relic/first"),
                        new CombatReaction(Block(3), "second", Player)
                    };
                if (provenance.RuleId == "first") return new[] { new CombatReaction(Damage(3), "child", Player) };
                return Array.Empty<CombatReaction>();
            });
            var state = Create();
            var result = CombatEngine.Resolve(state, 0, new[] { Damage(), Block(2) }, reactionProvider: provider);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(1, result.State.Revision);
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 3, 2 }, result.Events.Select(item => item.Effect.Amount));
            CollectionAssert.AreEqual(new[] { 0, 0, 0, 0, 1 }, result.RuleEvents.Select(item => item.Index));
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, result.RuleEvents.Select(item => item.SequenceIndex));
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 1, 0 }, result.RuleEvents.Select(item => item.Provenance.Depth));
            CollectionAssert.AreEqual(new int?[] { null, 0, 1, 0, null }, result.RuleEvents.Select(item => item.Provenance.ParentSequenceIndex));
            CollectionAssert.AreEqual(new long?[] { null, 1, 1, 1, null }, result.RuleEvents.Select(item => item.Provenance.ParentRevision));
            CollectionAssert.AreEqual(new[] { 0, 0, 0, 0, 1 }, result.RuleEvents.Select(item => item.Provenance.RootEffectIndex));
            Assert.IsTrue(result.RuleEvents.All(item => item.Revision == 1));
            Assert.AreEqual(CombatEffectOrigin.Primary, result.Events[0].Provenance.Origin);
            Assert.IsNull(result.Events[0].Provenance.RuleId); Assert.IsNull(result.Events[0].Provenance.Owner);
            Assert.AreEqual(Player, result.Events[1].Provenance.Owner);
            Assert.AreEqual("relic/first", result.Events[1].Provenance.SourceInstanceId);
            Assert.AreEqual(CombatEffectOrigin.Triggered, result.Events[1].Provenance.Origin);
            Assert.IsTrue(result.Events[1].Provenance.CanTriggerReactions); Assert.IsFalse(result.Events[2].Provenance.CanTriggerReactions);
            Assert.AreEqual(14, result.State.Get(Enemy).Health); Assert.AreEqual(5, result.State.Get(Player).Block);
            Assert.AreEqual(3, provider.Calls.Count); Assert.AreEqual(20, state.Get(Enemy).Health);
        }

        [Test] public void GeneratedEffectsDoNotTriggerAgainUnlessExplicitlyEnabled()
        {
            var provider = new Provider(context => new[] { new CombatReaction(Block(), "default-leaf", Player) });
            var result = CombatEngine.Resolve(Create(), 0, new[] { Damage() }, reactionProvider: provider);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(2, result.RuleEvents.Count); Assert.AreEqual(1, provider.Calls.Count);
            Assert.AreEqual(1, result.State.Get(Player).Block);
        }

        [Test] public void ExplicitOriginFilteringAllowsAChosenChainWithoutEnablingOtherOrigins()
        {
            var provider = new Provider(context => {
                if (context.Event.Provenance.Origin == CombatEffectOrigin.Primary)
                    return new[] { new CombatReaction(new CombatEffect(CombatEffectKind.Heal, Player, Player, 2), "first-link", Player,
                        CombatEffectOrigin.Lifesteal, canTriggerReactions: true) };
                if (context.Event.Provenance.Origin == CombatEffectOrigin.Lifesteal)
                    return new[] { new CombatReaction(Damage(2), "last-link", Player, CombatEffectOrigin.Overheal) };
                throw new InvalidOperationException("The leaf must not be offered to the provider.");
            });
            var result = CombatEngine.Resolve(Create(), 0, new[] { Damage() }, reactionProvider: provider);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(2, provider.Calls.Count);
            CollectionAssert.AreEqual(new[] { CombatEffectOrigin.Primary, CombatEffectOrigin.Lifesteal, CombatEffectOrigin.Overheal },
                result.RuleEvents.Select(item => item.Provenance.Origin));
            Assert.AreEqual(12, result.State.Get(Player).Health); Assert.AreEqual(17, result.State.Get(Enemy).Health);
        }

        [Test] public void ContextsAreImmutableSettledSnapshotsAndLaterModifiersSeeReactions()
        {
            var provider = new Provider(context => context.Event.Index == 0
                ? new[] { new CombatReaction(Block(4), "grant", Player) } : Array.Empty<CombatReaction>());
            var incoming = new Incoming(context => context.Amount + context.Combat.Get(Player).Block);
            var state = Create();
            var result = CombatEngine.Resolve(state, 0, new[] { Damage(3), Damage() }, incoming, reactionProvider: provider);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(12, result.State.Get(Enemy).Health);
            Assert.AreEqual(17, provider.Calls[0].State.Get(Enemy).Health); Assert.AreEqual(0, provider.Calls[0].State.Get(Player).Block);
            Assert.AreEqual(12, provider.Calls[1].State.Get(Enemy).Health); Assert.AreEqual(4, provider.Calls[1].State.Get(Player).Block);
            Assert.AreEqual(4, incoming.Calls[1].Combat.Get(Player).Block); Assert.AreEqual(5, result.Events[2].SettledDamage);
            Assert.Throws<NotSupportedException>(() => ((IList<CombatantState>)provider.Calls[0].State.Combatants)[0] = null);
            Assert.Throws<NotSupportedException>(() => ((IList<CombatRuleEvent>)result.RuleEvents)[0] = null);
            Assert.AreEqual(20, state.Get(Enemy).Health); Assert.AreEqual(0, state.Get(Player).Block);
        }

        [Test] public void AStatusAppliedByAReactionIsVisibleToTheNextPrimaryDamage()
        {
            var status = Status("mark", new StatusDefinitionId("mark"), Enemy);
            var provider = new Provider(context => context.Event.Index == 0
                ? new[] { new CombatReaction(CombatEffect.ApplyStatus(status), "apply-mark", Player) } : Array.Empty<CombatReaction>());
            var incoming = new Incoming(context => context.Amount + context.Combat.Statuses.Count);
            var result = CombatEngine.Resolve(Create(), 0, new[] { Damage(), Damage() }, incoming, reactionProvider: provider);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(17, result.State.Get(Enemy).Health);
            Assert.AreSame(status, result.State.Statuses.Single()); Assert.IsInstanceOf<CombatStatusEvent>(result.RuleEvents[1]);
            Assert.AreEqual(0, result.RuleEvents[1].Provenance.ParentSequenceIndex);
            Assert.AreEqual(2, result.Events[1].SettledDamage);
        }

        [Test] public void AllFactsOfOneStatusApplicationObserveTheSameFullySettledState()
        {
            var immunity = Status("one-charge", Immunity, Enemy, StatusDuration.ForTriggers(1));
            var control = Status("attempt", Control, Enemy, StatusDuration.ForTriggers(1));
            var provider = new Provider(context => new[] { new CombatReaction(
                new CombatEffect(CombatEffectKind.GainBlock, Enemy, Enemy, context.Event.SequenceIndex + 1),
                "fact-" + context.Event.SequenceIndex, Enemy) });
            var state = Create(statuses: new[] { immunity });
            var result = CombatEngine.Resolve(state, 0, new[] { CombatEffect.ApplyStatus(control) }, statusCatalog: Catalog(), reactionProvider: provider);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(2, provider.Calls.Count);
            Assert.IsTrue(provider.Calls.All(call => call.State.Statuses.Count == 0 && call.State.Get(Enemy).Block == 0));
            Assert.IsInstanceOf<CombatStatusBlockedEvent>(result.RuleEvents[0]);
            Assert.IsInstanceOf<CombatStatusChangedEvent>(result.RuleEvents[1]);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, result.RuleEvents.Select(item => item.SequenceIndex));
            CollectionAssert.AreEqual(new int?[] { null, null, 0, 1 }, result.RuleEvents.Select(item => item.Provenance.ParentSequenceIndex));
            Assert.AreEqual(3, result.State.Get(Enemy).Block); Assert.IsEmpty(result.State.Statuses);
            Assert.AreSame(immunity, state.Statuses.Single());
        }

        [Test] public void ReactionDefeatingTheActorCancelsItsRemainingPrimariesButOtherSourcesContinue()
        {
            var provider = new Provider(context => context.Event.Index == 0
                ? new[] { new CombatReaction(new CombatEffect(CombatEffectKind.Damage, Enemy, Player, 20), "response", Enemy) }
                : Array.Empty<CombatReaction>());
            var result = CombatEngine.Resolve(Create(), 0, new[] { Damage(), Damage(8),
                new CombatEffect(CombatEffectKind.Damage, Ally, Enemy, 5) }, reactionProvider: provider);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(1, result.State.Revision);
            Assert.AreEqual(0, result.State.Get(Player).Health); Assert.AreEqual(14, result.State.Get(Enemy).Health);
            CollectionAssert.AreEqual(new[] { 0, 0, 2 }, result.RuleEvents.Select(item => item.Index));
            Assert.AreEqual(2, provider.Calls.Count);
        }

        [TestCase(false)] [TestCase(true)]
        public void DefeatedReactionSourceRequiresExplicitPermissionAndStillUsesIncomingDamage(bool allowDefeated)
        {
            var provider = new Provider(context => new[] { new CombatReaction(
                new CombatEffect(CombatEffectKind.Damage, Enemy, Player, 3), "post-defeat", Enemy, CombatEffectOrigin.Reflection,
                sourcePolicy: allowDefeated ? CombatReactionSourcePolicy.AllowDefeated : CombatReactionSourcePolicy.RequireLiving) });
            var incoming = new Incoming(context => context.Amount + 2);
            var result = CombatEngine.Resolve(Create(), 0, new[] { CombatEffect.Eliminate(Player, Enemy) }, incoming, reactionProvider: provider);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(allowDefeated ? 5 : 10, result.State.Get(Player).Health);
            Assert.AreEqual(allowDefeated ? 2 : 1, result.RuleEvents.Count); Assert.AreEqual(allowDefeated ? 1 : 0, incoming.Calls.Count);
            if (allowDefeated)
            {
                Assert.IsFalse(incoming.Calls[0].Combat.Get(Enemy).IsAlive);
                Assert.AreEqual(Enemy, result.Events[1].Effect.Source); Assert.AreEqual(Enemy, result.Events[1].Provenance.Owner);
                Assert.AreEqual(CombatReactionSourcePolicy.AllowDefeated, result.Events[1].Provenance.SourcePolicy);
                Assert.AreEqual(5, result.Events[1].SettledDamage);
            }
        }

        [Test] public void AlreadyDefeatedKnownSourcesAreAllowedOnlyForAuthorizedReactions()
        {
            var provider = new Provider(context => new[] { new CombatReaction(
                new CombatEffect(CombatEffectKind.Damage, Enemy, Player, 2), "persistent-source", Enemy, CombatEffectOrigin.Periodic,
                sourcePolicy: CombatReactionSourcePolicy.AllowDefeated) });
            var result = CombatEngine.Resolve(Create(enemyHealth: 0), 0, new[] { Block() }, reactionProvider: provider);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(9, result.State.Get(Player).Health); Assert.AreEqual(0, result.State.Get(Player).Block);
            Assert.AreEqual(Enemy, result.Events[1].Effect.Source);
            var state = Create(enemyHealth: 0); provider.Calls.Clear();
            var rejected = CombatEngine.Resolve(state, 0, new[] { Block(), new CombatEffect(CombatEffectKind.Damage, Enemy, Player, 2) }, reactionProvider: provider);
            Rejected(state, rejected, CombatError.InvalidEffect); Assert.IsEmpty(provider.Calls);
        }

        [TestCase("source")] [TestCase("target")] [TestCase("owner")]
        public void UnknownReactionReferencesAreRejectedEvenWhenTheKnownTargetIsDead(string invalid)
        {
            var missing = new ParticipantId("missing");
            var provider = new Provider(context => new[] { new CombatReaction(
                new CombatEffect(CombatEffectKind.Damage, invalid == "source" ? missing : Player, invalid == "target" ? missing : Enemy, 1),
                "invalid-reference", invalid == "owner" ? missing : Player, sourcePolicy: CombatReactionSourcePolicy.AllowDefeated) });
            var state = Create();
            Rejected(state, CombatEngine.Resolve(state, 0, new[] { CombatEffect.Eliminate(Player, Enemy) }, reactionProvider: provider), CombatError.InvalidReaction);
            Assert.AreEqual(20, state.Get(Enemy).Health);
        }

        [Test] public void DeadTargetsProduceNoReactionFactsOrFurtherCallbacks()
        {
            var provider = new Provider(context => new[] { new CombatReaction(Damage(), "dead-target", Player, canTriggerReactions: true) });
            var result = CombatEngine.Resolve(Create(), 0, new[] { CombatEffect.Eliminate(Player, Enemy) }, reactionProvider: provider);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(1, result.RuleEvents.Count); Assert.AreEqual(1, provider.Calls.Count);
            var skipped = CombatEngine.Resolve(Create(enemyHealth: 0), 0, new[] { Damage() }, reactionProvider: provider);
            Assert.IsTrue(skipped.Accepted); Assert.AreEqual(1, skipped.State.Revision); Assert.IsEmpty(skipped.RuleEvents);
            Assert.AreEqual(1, provider.Calls.Count);
        }

        [TestCase(2, true)] [TestCase(3, false)]
        public void GeneratedEffectBudgetIncludesEffectsThatWillBeSkipped(int count, bool accepted)
        {
            var provider = new Provider(context => Enumerable.Range(0, count).Select(i => new CombatReaction(Damage(), "skipped-" + i, Player)));
            var state = Create();
            var result = CombatEngine.Resolve(state, 0, new[] { CombatEffect.Eliminate(Player, Enemy) },
                reactionProvider: provider, reactionLimits: new CombatReactionLimits(maxEffects: 2));
            if (!accepted) { Rejected(state, result, CombatError.ReactionLimitExceeded); return; }
            Assert.IsTrue(result.Accepted); Assert.AreEqual(1, result.RuleEvents.Count); Assert.AreEqual(0, result.State.Get(Enemy).Health);
        }

        [Test] public void PrimaryEffectsDoNotConsumeTheIndependentReactionBudget()
        {
            var provider = new Provider(context => context.Event.Index == 63
                ? new[] { new CombatReaction(Block(2), "one-reaction", Player) } : Array.Empty<CombatReaction>());
            var result = CombatEngine.Resolve(Create(), 0, Enumerable.Range(0, 64).Select(i => Block()),
                reactionProvider: provider, reactionLimits: new CombatReactionLimits(maxEffects: 1));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(65, result.RuleEvents.Count); Assert.AreEqual(66, result.State.Get(Player).Block);
        }

        [TestCase(1, false)] [TestCase(2, true)]
        public void DepthBudgetAllowsTheBoundaryAndRollsBackExcess(int maxDepth, bool accepted)
        {
            var provider = new Provider(context => context.Event.Provenance.Depth < 2
                ? new[] { new CombatReaction(Block(), "chain", Player, canTriggerReactions: true) } : Array.Empty<CombatReaction>());
            var state = Create();
            var result = CombatEngine.Resolve(state, 0, new[] { Damage() }, reactionProvider: provider,
                reactionLimits: new CombatReactionLimits(maxDepth: maxDepth));
            if (!accepted) { Rejected(state, result, CombatError.ReactionLimitExceeded); return; }
            Assert.IsTrue(result.Accepted); Assert.AreEqual(3, result.RuleEvents.Count); Assert.AreEqual(2, result.State.Get(Player).Block);
        }

        private static IEnumerable<CombatReaction> Unbounded(Action yielded)
        {
            while (true) { yielded(); yield return new CombatReaction(Block(), "unbounded", Player); }
        }
        [Test] public void InfiniteProviderOutputStopsAtTheEffectBudgetWithoutPartialState()
        {
            int count = 0; var provider = new Provider(context => Unbounded(() => count++)); var state = Create();
            var result = CombatEngine.Resolve(state, 0, new[] { Damage() }, reactionProvider: provider,
                reactionLimits: new CombatReactionLimits(maxEffects: 2));
            Rejected(state, result, CombatError.ReactionLimitExceeded); Assert.Greater(count, 0); Assert.LessOrEqual(count, 3);
            Assert.AreEqual(20, state.Get(Enemy).Health); Assert.AreEqual(0, state.Get(Player).Block);
        }

        private static IEnumerable<CombatReaction> ThrowsWhileEnumerating()
        {
            yield return new CombatReaction(Block(), "before-failure", Player);
            throw new InvalidOperationException("Invalid deferred reaction output.");
        }
        [TestCase("null-list")] [TestCase("null-item")] [TestCase("argument")] [TestCase("operation")] [TestCase("enumeration")]
        public void InvalidProviderResultsAndSupportedExceptionsRollbackThePrimary(string failure)
        {
            var provider = new Provider(context => {
                if (failure == "null-list") return null;
                if (failure == "null-item") return new CombatReaction[] { null };
                if (failure == "argument") throw new ArgumentException("Invalid rule input.");
                if (failure == "operation") throw new InvalidOperationException("Invalid rule state.");
                return ThrowsWhileEnumerating();
            });
            var state = Create();
            Rejected(state, CombatEngine.Resolve(state, 0, new[] { Damage() }, reactionProvider: provider), CombatError.InvalidReaction);
            Assert.AreEqual(20, state.Get(Enemy).Health);
        }

        [TestCase(false)] [TestCase(true)]
        public void NumericOverflowInTheProviderOrAReactionRollsBackEveryFact(bool insideProvider)
        {
            var provider = new Provider(context => {
                if (insideProvider) throw new OverflowException("Reaction arithmetic overflow.");
                return new[] { new CombatReaction(Block(long.MaxValue), "overflow", Player) };
            });
            var state = Create();
            Rejected(state, CombatEngine.Resolve(state, 0, new[] { Damage() }, reactionProvider: provider), CombatError.Overflow);
            Assert.AreEqual(20, state.Get(Enemy).Health); Assert.AreEqual(0, state.Get(Player).Block);
        }

        [Test] public void FailedReactionStatusApplicationRollsBackThePrimaryAndEarlierReaction()
        {
            var status = Status("duplicate", new StatusDefinitionId("mark"), Enemy);
            var provider = new Provider(context => new[] {
                new CombatReaction(CombatEffect.ApplyStatus(status), "first-application", Player),
                new CombatReaction(CombatEffect.ApplyStatus(status), "duplicate-application", Player)
            });
            var state = Create();
            Rejected(state, CombatEngine.Resolve(state, 0, new[] { Damage() }, reactionProvider: provider));
            Assert.IsEmpty(state.Statuses); Assert.AreEqual(20, state.Get(Enemy).Health);
        }

        [Test] public void OverhealReportsOnlyTheUnrestoredPartOfAnActualHeal()
        {
            var result = CombatEngine.Resolve(Create(playerHealth: 18), 0, new[] {
                new CombatEffect(CombatEffectKind.Heal, Player, Player, 7),
                new CombatEffect(CombatEffectKind.Heal, Player, Player, long.MaxValue),
                Damage(), CombatEffect.Eliminate(Player, Enemy),
                new CombatEffect(CombatEffectKind.Heal, Player, Enemy, 10)
            });
            Assert.IsTrue(result.Accepted); Assert.AreEqual(4, result.Events.Count);
            Assert.AreEqual(2, result.Events[0].HealthRestored); Assert.AreEqual(5, result.Events[0].Overheal);
            Assert.AreEqual(0, result.Events[1].HealthRestored); Assert.AreEqual(long.MaxValue, result.Events[1].Overheal);
            Assert.AreEqual(0, result.Events[2].Overheal); Assert.AreEqual(0, result.Events[3].Overheal);
            Assert.AreEqual(0, result.Events[3].DamageToHealth); Assert.AreEqual(0, result.State.Get(Enemy).Health);
        }

        [Test] public void NoProviderKeepsPrimaryFactsAndLifecycleFactsHaveNoEffectProvenance()
        {
            var state = Create(statuses: new[] { Status("expired", new StatusDefinitionId("mark"), Enemy, StatusDuration.UntilPhaseEnd("round/1", "enemy")) });
            var result = CombatEngine.Resolve(state, 0, new[] { Damage(), Block(2) });
            Assert.IsTrue(result.Accepted); Assert.AreEqual(2, result.Events.Count);
            Assert.IsTrue(result.RuleEvents.All(item => item.Provenance.Origin == CombatEffectOrigin.Primary && item.Provenance.Depth == 0));
            Assert.IsTrue(result.RuleEvents.All(item => item.Provenance.ParentSequenceIndex == null && item.Provenance.ParentRevision == null));
            var ended = CombatStatusLifecycle.EndPhase(result.State, result.State.Revision, "round/1", "enemy");
            Assert.IsTrue(ended.Accepted); Assert.AreEqual(1, ended.RuleEvents.Count);
            Assert.IsNull(ended.RuleEvents[0].Provenance); Assert.IsNull(ended.RuleEvents[0].Effect); Assert.AreEqual(-1, ended.RuleEvents[0].Index);
        }

        [Test] public void ReactionAndBudgetConstructorsRejectAmbiguousOrUnboundedConfiguration()
        {
            Assert.Catch<ArgumentException>(() => new CombatReaction(null, "rule", Player));
            Assert.Catch<ArgumentException>(() => new CombatReaction(Block(), " ", Player));
            Assert.Catch<ArgumentException>(() => new CombatReaction(Block(), "rule", default(ParticipantId)));
            Assert.Catch<ArgumentException>(() => new CombatReaction(Block(), "rule", Player, CombatEffectOrigin.Primary));
            Assert.Catch<ArgumentException>(() => new CombatReaction(Block(), "rule", Player, (CombatEffectOrigin)999));
            Assert.Catch<ArgumentException>(() => new CombatReaction(Block(), "rule", Player, sourcePolicy: (CombatReactionSourcePolicy)999));
            Assert.Catch<ArgumentException>(() => new CombatReactionLimits(maxEffects: 0));
            Assert.Catch<ArgumentException>(() => new CombatReactionLimits(maxEffects: 4097));
            Assert.Catch<ArgumentException>(() => new CombatReactionLimits(maxDepth: 0));
            Assert.Catch<ArgumentException>(() => new CombatReactionLimits(maxDepth: 65));
            Assert.DoesNotThrow(() => new CombatReactionLimits(4096, 64));
        }
    }
}
