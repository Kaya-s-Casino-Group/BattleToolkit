using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class PeriodicTimingSubjectTests
    {
        private static readonly ParticipantId P = new ParticipantId("player"), A = new ParticipantId("ally"), E = new ParticipantId("enemy");
        private static readonly StatusDefinitionId Poison = new StatusDefinitionId("poison");
        private static CombatState State(StatusInstance[] statuses = null, int playerHealth = 100, int enemyHealth = 100) =>
            new CombatState("poison-subject", 0, new[] {
                new CombatantState(P, "hero", "players", 100, playerHealth),
                new CombatantState(A, "ally", "players", 100, 100),
                new CombatantState(E, "enemy", "enemies", 100, enemyHealth)
            }, statuses);
        private static StatusInstance Status(string id, ParticipantId source, int stacks = 5, ParticipantId? holder = null) =>
            new StatusInstance(new StatusInstanceId(id), Poison, EntityRef.Participant(source), EntityRef.Participant(holder ?? E),
                stacks, StatusDuration.UntilRemoved());
        private static CombatStatusCatalog Catalog(CombatTimingKind timing, PeriodicTimingSubject subject,
            StatusReapplyPolicy policy = StatusReapplyPolicy.AddStacks) =>
            new CombatStatusCatalog("poison-subject.1", new[] { new CombatStatusDefinition(Poison, StatusTags.Negative,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(timing, 1, stackDecay: 1, timingSubject: subject) },
                StatusLifetimeSpec.UntilRemoved(), policy) });
        private static CombatTimingSignal Clock(CombatTimingKind timing, ParticipantId participant) =>
            timing == CombatTimingKind.TurnStarted ? CombatTimingSignal.TurnStarted("turn", new[] { participant }) :
            timing == CombatTimingKind.PhaseEnding ? CombatTimingSignal.PhaseEnding("phase", new[] { participant }, "team") :
            CombatTimingSignal.ActionCompleted("action", participant, "play");

        [TestCase(CombatTimingKind.TurnStarted, PeriodicTimingSubject.Holder)]
        [TestCase(CombatTimingKind.ActionCompleted, PeriodicTimingSubject.Holder)]
        [TestCase(CombatTimingKind.PhaseEnding, PeriodicTimingSubject.Holder)]
        [TestCase(CombatTimingKind.TurnStarted, PeriodicTimingSubject.Applier)]
        [TestCase(CombatTimingKind.ActionCompleted, PeriodicTimingSubject.Applier)]
        [TestCase(CombatTimingKind.PhaseEnding, PeriodicTimingSubject.Applier)]
        public void OnlyConfiguredSubjectsClockTicksWithoutChangingAttributionOrTarget(CombatTimingKind timing, PeriodicTimingSubject subject)
        {
            var state = State(new[] { Status("first", P) }); var catalog = Catalog(timing, subject);
            var selected = subject == PeriodicTimingSubject.Holder ? E : P;
            var unselected = selected == E ? P : E;
            var skipped = CombatEngine.ResolveTiming(state, 0, Clock(timing, unselected), statusCatalog: catalog);
            Assert.IsTrue(skipped.Accepted); Assert.IsEmpty(skipped.Events); Assert.AreEqual(5, skipped.State.Statuses.Single().Stacks);
            var tick = CombatEngine.ResolveTiming(state, 0, Clock(timing, selected), statusCatalog: catalog);
            Assert.IsTrue(tick.Accepted); Assert.AreEqual(95, tick.State.Get(E).Health); Assert.AreEqual(4, tick.State.Statuses.Single().Stacks);
            Assert.AreEqual(P, tick.Events.Single().Effect.Source); Assert.AreEqual(E, tick.Events.Single().Effect.Target);
        }

        [TestCase(StatusReapplyPolicy.AddStacks, 5)]
        [TestCase(StatusReapplyPolicy.KeepLonger, 3)]
        public void ApplierReapplicationPreservesIndependentSourceBuckets(StatusReapplyPolicy policy, int playerStacks)
        {
            var catalog = Catalog(CombatTimingKind.TurnStarted, PeriodicTimingSubject.Applier, policy);
            var applied = CombatEngine.Resolve(State(), 0, new[] {
                CombatEffect.ApplyStatus(Status("p-first", P, 2)), CombatEffect.ApplyStatus(Status("a-first", A, 4)),
                CombatEffect.ApplyStatus(Status("p-second", P, 3)) }, statusCatalog: catalog);
            Assert.IsTrue(applied.Accepted, applied.Error.ToString()); Assert.AreEqual(2, applied.State.Statuses.Count);
            Assert.AreEqual(playerStacks, applied.State.Statuses.Single(st => st.Source.Id == P.Value).Stacks);
            var tick = CombatEngine.ResolveTiming(applied.State, applied.State.Revision, Clock(CombatTimingKind.TurnStarted, P), statusCatalog: catalog);
            Assert.IsTrue(tick.Accepted, tick.Error.ToString()); Assert.AreEqual(100 - playerStacks, tick.State.Get(E).Health);
            Assert.AreEqual(playerStacks - 1, tick.State.Statuses.Single(st => st.Source.Id == P.Value).Stacks);
            Assert.AreEqual(4, tick.State.Statuses.Single(st => st.Source.Id == A.Value).Stacks);
        }

        [TestCase(StatusReapplyPolicy.AddStacks, 9)]
        [TestCase(StatusReapplyPolicy.KeepLonger, 4)]
        public void HolderReapplicationKeepsExistingCrossSourceMergeSemantics(StatusReapplyPolicy policy, int stacks)
        {
            var applied = CombatEngine.Resolve(State(), 0, new[] {
                CombatEffect.ApplyStatus(Status("p-first", P, 2)), CombatEffect.ApplyStatus(Status("a-first", A, 4)),
                CombatEffect.ApplyStatus(Status("p-second", P, 3)) },
                statusCatalog: Catalog(CombatTimingKind.TurnStarted, PeriodicTimingSubject.Holder, policy));
            Assert.IsTrue(applied.Accepted); Assert.AreEqual(stacks, applied.State.Statuses.Single().Stacks);
            Assert.AreEqual(P.Value, applied.State.Statuses.Single().Source.Id);
        }

        [TestCase(PeriodicTimingSubject.Applier, StatusReapplyPolicy.AddStacks, false, true)]
        [TestCase(PeriodicTimingSubject.Applier, StatusReapplyPolicy.AddStacks, true, false)]
        [TestCase(PeriodicTimingSubject.Applier, StatusReapplyPolicy.KeepLonger, false, true)]
        [TestCase(PeriodicTimingSubject.Applier, StatusReapplyPolicy.KeepLonger, true, false)]
        [TestCase(PeriodicTimingSubject.Holder, StatusReapplyPolicy.AddStacks, false, false)]
        [TestCase(PeriodicTimingSubject.Applier, StatusReapplyPolicy.IndependentInstances, true, true)]
        public void SnapshotDuplicateValidationUsesTheSameSourceScopeAsReapplication(PeriodicTimingSubject subject,
            StatusReapplyPolicy policy, bool sameSource, bool accepted)
        {
            var state = State(new[] { Status("first", P), Status("second", sameSource ? P : A) });
            var result = CombatEngine.ResolveTiming(state, 0, Clock(CombatTimingKind.TurnStarted, E),
                statusCatalog: Catalog(CombatTimingKind.TurnStarted, subject, policy));
            Assert.AreEqual(accepted, result.Accepted);
            if (!accepted) { Assert.AreEqual(CombatError.InvalidState, result.Error); Assert.AreSame(state, result.State); Assert.IsEmpty(result.RuleEvents); }
        }

        [TestCase(CombatTimingKind.TurnStarted, PeriodicTimingSubject.Holder, true)]
        [TestCase(CombatTimingKind.ActionCompleted, PeriodicTimingSubject.Holder, true)]
        [TestCase(CombatTimingKind.PhaseEnding, PeriodicTimingSubject.Holder, true)]
        [TestCase(CombatTimingKind.TurnStarted, PeriodicTimingSubject.Applier, false)]
        [TestCase(CombatTimingKind.ActionCompleted, PeriodicTimingSubject.Applier, false)]
        [TestCase(CombatTimingKind.PhaseEnding, PeriodicTimingSubject.Applier, true)]
        public void DefeatedApplierOnlyKeepsItsTeamEndingClockWhileHolderClocksStayUnchanged(CombatTimingKind timing,
            PeriodicTimingSubject subject, bool ticks)
        {
            var state = State(new[] { Status("first", P) }, playerHealth: 0);
            var result = CombatEngine.ResolveTiming(state, 0, Clock(timing, subject == PeriodicTimingSubject.Holder ? E : P),
                statusCatalog: Catalog(timing, subject));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(ticks ? 95 : 100, result.State.Get(E).Health);
            Assert.AreEqual(ticks ? 4 : 5, result.State.Statuses.Single().Stacks);
        }

        [TestCase(CombatTimingKind.TurnStarted, false)]
        [TestCase(CombatTimingKind.PhaseEnding, true)]
        public void EarlierPeriodicDeathCancelsLaterIndividualApplierClockButNotTeamEnding(CombatTimingKind timing, bool laterTicks)
        {
            var state = State(new[] { Status("kill-applier", A, holder: P), Status("later", P) }, playerHealth: 5);
            var signal = timing == CombatTimingKind.TurnStarted
                ? CombatTimingSignal.TurnStarted("both", new[] { A, P })
                : CombatTimingSignal.PhaseEnding("both", new[] { A, P }, "players");
            var result = CombatEngine.ResolveTiming(state, 0, signal, statusCatalog: Catalog(timing, PeriodicTimingSubject.Applier));
            Assert.IsTrue(result.Accepted); Assert.AreEqual(0, result.State.Get(P).Health);
            Assert.AreEqual(laterTicks ? 95 : 100, result.State.Get(E).Health);
            Assert.AreEqual(laterTicks ? 4 : 5, result.State.Statuses.Single(st => st.Id.Value == "later").Stacks);
        }

        [Test] public void InvalidTimingSubjectIsRejectedAndXmlFingerprintsIncludeIt()
        {
            Assert.Throws<ArgumentException>(() => new PeriodicDamageSpec(CombatTimingKind.TurnStarted, 1, timingSubject: (PeriodicTimingSubject)999));
            Func<string, BattleContentBuildResult> compile = subject => BattleContentCompiler.CompileXml(
                "<BattleContent schemaVersion='1' id='timing' version='1'><Statuses><Status id='poison' reapply='AddStacks'>" +
                "<PeriodicDamage timing='TurnStarted' amount='1'" + subject + "/></Status></Statuses></BattleContent>");
            var defaultSubject = compile(""); var holder = compile(" timingSubject='Holder'"); var applier = compile(" timingSubject='Applier'");
            Assert.IsTrue(defaultSubject.Success); Assert.IsTrue(holder.Success); Assert.IsTrue(applier.Success);
            Assert.AreEqual(PeriodicTimingSubject.Holder, ((PeriodicDamageSpec)defaultSubject.Content.Statuses["poison"].Behaviors.Single()).TimingSubject);
            Assert.AreEqual(PeriodicTimingSubject.Applier, ((PeriodicDamageSpec)applier.Content.Statuses["poison"].Behaviors.Single()).TimingSubject);
            Assert.AreNotEqual(holder.Content.Fingerprint, applier.Content.Fingerprint);
            Assert.IsFalse(compile(" timingSubject='Opponent'").Success);
        }
    }
}
