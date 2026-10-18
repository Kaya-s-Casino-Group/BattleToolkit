using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    public sealed class CombatPrimitiveTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), Enemy = new ParticipantId("enemy");
        private static CombatState Create(int block = 3, IEnumerable<StatusInstance> statuses = null) => new CombatState("primitives", 0,
            new[] { new CombatantDefinition("hero", 20).Create(Player, "player"), new CombatantDefinition("enemy", 10, block).Create(Enemy, "enemy") }, statuses);
        private static StatusInstance Status(string id, int stacks = 2, StatusDuration duration = null) => new StatusInstance(
            new StatusInstanceId(id), new StatusDefinitionId("mark"), EntityRef.Participant(Player), EntityRef.Participant(Enemy),
            stacks, duration ?? StatusDuration.UntilPhaseEnd("round/1", "enemy"));
        private static CombatEffect Damage(long amount) => new CombatEffect(CombatEffectKind.Damage, Player, Enemy, amount);
        private sealed class RejectNonDamageModifier : IIncomingDamageModifier
        {
            public DamageModification Modify(DamageModifierContext context) => throw new InvalidOperationException("Non-damage effects must not invoke damage modifiers.");
        }
        private sealed class CountCurrentStatuses : IIncomingDamageModifier
        {
            public DamageModification Modify(DamageModifierContext context) => new DamageModification("current-statuses.1", "count current statuses",
                context.Amount, context.Amount + context.Combat.Statuses.Count * 2);
        }
        private sealed class CountIncoming : IIncomingDamageModifier
        {
            public int Calls { get; private set; }
            public DamageModification Modify(DamageModifierContext context)
            {
                Calls++;
                return new DamageModification("count.1", "retain incoming damage", context.Amount, context.Amount);
            }
        }

        [Test] public void NewEffectPayloadsRequireMatchingParticipantReferencesAndNoDamageFields()
        {
            Assert.AreEqual(1, (int)CombatEffectKind.Damage); Assert.AreEqual(2, (int)CombatEffectKind.Heal);
            Assert.AreEqual(3, (int)CombatEffectKind.GainBlock); Assert.AreEqual(4, (int)CombatEffectKind.ClearBlock);
            var status = Status("payload"); var attack = new AttackContext(Player, AttackOrigin.Other, 0);
            var report = new DamageModification("zero.1", "zero", 0, 0);
            var apply = CombatEffect.ApplyStatus(status);
            Assert.AreEqual(CombatEffectKind.ApplyStatus, apply.Kind); Assert.AreSame(status, apply.Status);
            Assert.AreEqual(Player, apply.Source); Assert.AreEqual(Enemy, apply.Target); Assert.AreEqual(0, apply.Amount);
            Assert.Catch<ArgumentException>(() => CombatEffect.ApplyStatus(null));
            Assert.Catch<ArgumentException>(() => new CombatEffect(CombatEffectKind.ApplyStatus, Player, Enemy));
            Assert.Catch<ArgumentException>(() => new CombatEffect(CombatEffectKind.ApplyStatus, Enemy, Enemy, status: status));
            Assert.Catch<ArgumentException>(() => new CombatEffect(CombatEffectKind.ApplyStatus, Player, Player, status: status));
            Assert.Catch<ArgumentException>(() => new CombatEffect(CombatEffectKind.ApplyStatus, Player, Enemy, 1, status: status));
            Assert.Catch<ArgumentException>(() => new CombatEffect(CombatEffectKind.ApplyStatus, Player, Enemy, attack: attack, status: status));
            Assert.Catch<ArgumentException>(() => new CombatEffect(CombatEffectKind.ApplyStatus, Player, Enemy, attackModification: report, status: status));
            Assert.Catch<ArgumentException>(() => new CombatEffect(CombatEffectKind.Damage, Player, Enemy, status: status));
            Assert.Catch<ArgumentException>(() => new CombatEffect(CombatEffectKind.Eliminate, Player, Enemy, status: status));
            Assert.Catch<ArgumentException>(() => new CombatEffect(CombatEffectKind.Eliminate, Player, Enemy, 1));
            Assert.Catch<ArgumentException>(() => new CombatEffect(CombatEffectKind.Eliminate, Player, Enemy, attack: attack));
            Assert.Catch<ArgumentException>(() => new CombatEffect(CombatEffectKind.Eliminate, Player, Enemy, attackModification: report));
            var matchSource = new StatusInstance(status.Id, status.DefinitionId, EntityRef.Match(new MatchId(Player.Value)), status.Target, 1, status.Duration);
            var matchTarget = new StatusInstance(status.Id, status.DefinitionId, status.Source, EntityRef.Match(new MatchId(Enemy.Value)), 1, status.Duration);
            Assert.Catch<ArgumentException>(() => CombatEffect.ApplyStatus(matchSource));
            Assert.Catch<ArgumentException>(() => CombatEffect.ApplyStatus(matchTarget));
            Assert.Catch<ArgumentException>(() => new CombatEffect(CombatEffectKind.ApplyStatus, Player, Enemy, status: matchSource));
            Assert.Catch<ArgumentException>(() => new CombatEffect(CombatEffectKind.ApplyStatus, Player, Enemy, status: matchTarget));
        }

        [Test] public void ApplyingStatusProducesOnlyAStatusEventAndPreservesVitals()
        {
            var state = Create(); var status = Status("new-status"); var effect = CombatEffect.ApplyStatus(status);
            var result = CombatEngine.Resolve(state, state.Revision, new[] { effect }, new RejectNonDamageModifier());
            Assert.IsTrue(result.Accepted); Assert.AreEqual(state.Revision + 1, result.State.Revision);
            Assert.IsEmpty(result.Events); Assert.AreEqual(1, result.RuleEvents.Count);
            var applied = result.RuleEvents.Single() as CombatStatusEvent;
            Assert.IsNotNull(applied); Assert.AreSame(effect, applied.Effect); Assert.AreSame(status, applied.Status);
            Assert.AreEqual(0, applied.Index); Assert.AreEqual(result.State.Revision, applied.Revision);
            Assert.AreSame(status, result.State.Statuses.Single()); Assert.IsEmpty(state.Statuses);
            foreach (var before in state.Combatants)
            {
                Assert.AreEqual(before.Health, result.State.Get(before.Id).Health);
                Assert.AreEqual(before.Block, result.State.Get(before.Id).Block);
            }
            Assert.Throws<NotSupportedException>(() => ((IList<CombatRuleEvent>)result.RuleEvents)[0] = null);
            Assert.Throws<NotSupportedException>(() => ((IList<StatusInstance>)result.State.Statuses)[0] = null);
        }

        [Test] public void MixedEventsKeepTotalOrderAndLaterDamageReadsNewlyAppliedStatuses()
        {
            var state = Create(0); var status = Status("visible-in-batch");
            var effects = new[] { Damage(1), CombatEffect.ApplyStatus(status), Damage(1), new CombatEffect(CombatEffectKind.Heal, Player, Enemy, 1) };
            var result = CombatEngine.Resolve(state, state.Revision, effects, new CountCurrentStatuses());
            Assert.IsTrue(result.Accepted); Assert.AreEqual(4, result.RuleEvents.Count); Assert.AreEqual(3, result.Events.Count);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, result.RuleEvents.Select(item => item.Index));
            CollectionAssert.AreEqual(new[] { 0, 2, 3 }, result.Events.Select(item => item.Index));
            Assert.IsInstanceOf<CombatStatusEvent>(result.RuleEvents[1]);
            CollectionAssert.AreEqual(effects, result.RuleEvents.Select(item => item.Effect));
            Assert.AreSame(result.RuleEvents[0], result.Events[0]); Assert.AreSame(result.RuleEvents[2], result.Events[1]); Assert.AreSame(result.RuleEvents[3], result.Events[2]);
            Assert.AreEqual(1, result.Events[0].DamageToHealth); Assert.AreEqual(3, result.Events[1].DamageToHealth);
            Assert.AreEqual(0, result.Events[2].DamageToHealth); Assert.AreEqual(1, result.Events[2].HealthRestored);
            Assert.IsTrue(result.RuleEvents.All(item => item.Revision == state.Revision + 1));
            Assert.AreEqual(7, result.State.Get(Enemy).Health); Assert.AreEqual(10, state.Get(Enemy).Health); Assert.IsEmpty(state.Statuses);
        }

        [Test] public void DistinctStatusIdsWithTheSameDefinitionDoNotMergeRefreshOrConsumeEachOther()
        {
            var first = Status("first", 2, StatusDuration.ForTriggers(3));
            var second = Status("second", 4, StatusDuration.UntilRemoved()); var state = Create();
            var result = CombatEngine.Resolve(state, state.Revision, new[] { CombatEffect.ApplyStatus(first), CombatEffect.ApplyStatus(second) });
            Assert.IsTrue(result.Accepted); Assert.AreEqual(2, result.State.Statuses.Count); Assert.IsEmpty(result.Events);
            Assert.AreSame(first, result.State.Statuses[0]); Assert.AreSame(second, result.State.Statuses[1]);
            Assert.AreEqual(2, result.State.Statuses[0].Stacks); Assert.AreEqual(4, result.State.Statuses[1].Stacks);
            Assert.AreEqual(3, result.State.Statuses[0].Duration.RemainingTriggers); Assert.AreEqual(DurationKind.UntilRemoved, result.State.Statuses[1].Duration.Kind);
            Assert.AreEqual(2, result.RuleEvents.Count);
        }

        [TestCase(false)] [TestCase(true)]
        public void DuplicateStatusIdsRollbackEarlierDamageAndStatusChanges(bool alreadyApplied)
        {
            var status = Status("duplicate"); var state = Create(0, alreadyApplied ? new[] { status } : Array.Empty<StatusInstance>());
            var effects = new List<CombatEffect> { Damage(1), CombatEffect.ApplyStatus(status) };
            if (!alreadyApplied) effects.Add(CombatEffect.ApplyStatus(status));
            var result = CombatEngine.Resolve(state, state.Revision, effects);
            Assert.AreEqual(CombatError.InvalidEffect, result.Error); Assert.AreSame(state, result.State);
            Assert.IsEmpty(result.RuleEvents); Assert.IsEmpty(result.Events); Assert.AreEqual(10, state.Get(Enemy).Health);
            Assert.AreEqual(alreadyApplied ? 1 : 0, state.Statuses.Count);
        }

        [Test] public void StatusCapacityAllows256ButRejectsTheWholeBatchAt257()
        {
            var state = Create(0, Enumerable.Range(0, 255).Select(i => Status("existing/" + i)));
            var last = CombatEffect.ApplyStatus(Status("number-256"));
            var accepted = CombatEngine.Resolve(state, state.Revision, new[] { last });
            Assert.IsTrue(accepted.Accepted); Assert.AreEqual(256, accepted.State.Statuses.Count); Assert.AreEqual(255, state.Statuses.Count);
            var rejected = CombatEngine.Resolve(state, state.Revision, new[] { Damage(1), last, CombatEffect.ApplyStatus(Status("number-257")) });
            Assert.AreEqual(CombatError.InvalidEffect, rejected.Error); Assert.AreSame(state, rejected.State);
            Assert.IsEmpty(rejected.RuleEvents); Assert.IsEmpty(rejected.Events); Assert.AreEqual(10, state.Get(Enemy).Health);
            Assert.AreEqual(255, state.Statuses.Count);
        }

        [Test] public void StatusApplicationRejectsUnknownUnitsAndStaleRevisionWithoutEffects()
        {
            var state = Create(); var status = Status("known");
            var foreign = new StatusInstance(new StatusInstanceId("foreign"), status.DefinitionId, status.Source,
                EntityRef.Participant(new ParticipantId("missing")), 1, status.Duration);
            var invalid = CombatEngine.Resolve(state, state.Revision, new[] { CombatEffect.ApplyStatus(foreign) });
            Assert.AreEqual(CombatError.InvalidEffect, invalid.Error); Assert.AreSame(state, invalid.State); Assert.IsEmpty(invalid.RuleEvents);
            var stale = CombatEngine.Resolve(state, state.Revision + 1, new[] { CombatEffect.ApplyStatus(status) });
            Assert.AreEqual(CombatError.StaleRevision, stale.Error); Assert.AreSame(state, stale.State); Assert.IsEmpty(stale.RuleEvents); Assert.IsEmpty(stale.Events);
        }

        [Test] public void EliminationBypassesDamageModifiersAndBlockWithoutReportingDamage()
        {
            var state = Create(100); var effect = CombatEffect.Eliminate(Player, Enemy);
            var result = CombatEngine.Resolve(state, state.Revision, new[] { effect }, new RejectNonDamageModifier());
            Assert.IsTrue(result.Accepted); Assert.AreEqual(0, result.State.Get(Enemy).Health); Assert.AreEqual(100, result.State.Get(Enemy).Block);
            Assert.AreEqual(state.Revision + 1, result.State.Revision); Assert.AreEqual(1, result.RuleEvents.Count); Assert.AreEqual(1, result.Events.Count);
            var defeated = result.Events.Single(); Assert.AreSame(defeated, result.RuleEvents.Single()); Assert.AreSame(effect, defeated.Effect);
            Assert.IsTrue(defeated.Died); Assert.AreEqual(CombatDeathCause.Eliminate, defeated.DeathCause);
            Assert.AreEqual(10, defeated.HealthLost); Assert.AreEqual(0, defeated.DamageToHealth); Assert.AreEqual(0, defeated.SettledDamage);
            Assert.AreEqual(0, defeated.BlockAbsorbed); Assert.AreEqual(0, defeated.Overkill); Assert.AreEqual(0, defeated.HealthRestored);
            Assert.IsNull(defeated.IncomingModification); Assert.IsNull(effect.Attack); Assert.IsNull(effect.AttackModification); Assert.IsNull(effect.Status);
            Assert.AreEqual(10, state.Get(Enemy).Health); Assert.AreEqual(100, state.Get(Enemy).Block);
        }

        [TestCase(5, false)] [TestCase(100, true)]
        public void DamageEventsRetainHealthDamageAndIdentifyOnlyActualDamageDeaths(long amount, bool lethal)
        {
            var result = CombatEngine.Resolve(Create(), 0, new[] { Damage(amount) }); var hit = result.Events.Single();
            Assert.IsTrue(result.Accepted); Assert.AreSame(hit, result.RuleEvents.Single());
            Assert.AreEqual(Math.Min(10, amount - 3), hit.DamageToHealth); Assert.AreEqual(hit.HealthLost, hit.DamageToHealth);
            Assert.AreEqual(lethal, hit.Died); Assert.AreEqual(lethal ? CombatDeathCause.Damage : CombatDeathCause.None, hit.DeathCause);
            Assert.AreEqual(3, hit.BlockAbsorbed); Assert.AreEqual(amount, hit.SettledDamage);
        }

        [Test] public void InvalidEffectAfterEliminationRollsBackBothTheDeathAndEarlierStatus()
        {
            var state = Create(); var first = Status("rollback");
            var duplicate = new StatusInstance(first.Id, first.DefinitionId, first.Source, EntityRef.Participant(Player), first.Stacks, first.Duration);
            var result = CombatEngine.Resolve(state, state.Revision, new[] {
                CombatEffect.ApplyStatus(first), CombatEffect.Eliminate(Player, Enemy), CombatEffect.ApplyStatus(duplicate)
            });
            Assert.AreEqual(CombatError.InvalidEffect, result.Error); Assert.AreSame(state, result.State);
            Assert.IsEmpty(result.RuleEvents); Assert.IsEmpty(result.Events); Assert.IsEmpty(state.Statuses); Assert.AreEqual(10, state.Get(Enemy).Health);
        }

        [TestCase(false)] [TestCase(true)]
        public void DeathSkipsEveryLaterPrimitiveOnTheDeadTargetAndContinuesOtherTargets(bool eliminate)
        {
            var state = Create(100); var incoming = new CountIncoming();
            var result = CombatEngine.Resolve(state, state.Revision, new[] {
                eliminate ? CombatEffect.Eliminate(Player, Enemy) : Damage(1000), Damage(1),
                new CombatEffect(CombatEffectKind.Heal, Player, Enemy, 10),
                new CombatEffect(CombatEffectKind.GainBlock, Player, Enemy, long.MaxValue),
                new CombatEffect(CombatEffectKind.ClearBlock, Player, Enemy),
                CombatEffect.ApplyStatus(Status("skipped")), CombatEffect.Eliminate(Player, Enemy),
                new CombatEffect(CombatEffectKind.GainBlock, Player, Player, 2)
            }, incoming);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(1, result.State.Revision);
            Assert.AreEqual(0, result.State.Get(Enemy).Health); Assert.AreEqual(eliminate ? 100 : 0, result.State.Get(Enemy).Block);
            Assert.AreEqual(eliminate ? 0 : 1, incoming.Calls);
            Assert.AreEqual(2, result.State.Get(Player).Block); Assert.IsEmpty(result.State.Statuses);
            CollectionAssert.AreEqual(new[] { 0, 7 }, result.RuleEvents.Select(item => item.Index));
            CollectionAssert.AreEqual(result.RuleEvents, result.Events);
            Assert.AreEqual(1, result.Events.Count(item => item.Died));
            Assert.AreEqual(eliminate ? CombatDeathCause.Eliminate : CombatDeathCause.Damage, result.Events[0].DeathCause);
        }

        [TestCase(0, false)] [TestCase(256, false)] [TestCase(256, true)]
        public void InitiallyDeadTargetsAcceptAnEntirelySkippedBatchEvenAtStatusCapacity(int statusCount, bool duplicateId)
        {
            var state = new CombatState("primitives", 5, new[] { Create().Get(Player),
                new CombatantState(Enemy, "enemy", "enemy", 10, 0, 3) },
                Enumerable.Range(0, statusCount).Select(i => Status("existing/" + i)));
            var result = CombatEngine.Resolve(state, state.Revision, new[] {
                Damage(1), new CombatEffect(CombatEffectKind.Heal, Player, Enemy, 10),
                new CombatEffect(CombatEffectKind.GainBlock, Player, Enemy, long.MaxValue),
                new CombatEffect(CombatEffectKind.ClearBlock, Player, Enemy),
                CombatEffect.ApplyStatus(Status(duplicateId ? "existing/0" : "skipped")), CombatEffect.Eliminate(Player, Enemy)
            }, new RejectNonDamageModifier());
            Assert.IsTrue(result.Accepted); Assert.AreEqual(6, result.State.Revision);
            Assert.IsEmpty(result.RuleEvents); Assert.IsEmpty(result.Events);
            Assert.AreEqual(0, result.State.Get(Enemy).Health); Assert.AreEqual(3, result.State.Get(Enemy).Block);
            Assert.AreEqual(statusCount, result.State.Statuses.Count);
            CollectionAssert.AreEqual(state.Statuses, result.State.Statuses);
            Assert.AreEqual(5, state.Revision);
        }

        [Test] public void SkippedStatusDoesNotReserveItsIdOrConsumeTheLastCapacitySlot()
        {
            var state = Create(0, Enumerable.Range(0, 255).Select(i => Status("existing/" + i)));
            var skipped = Status("reused-after-skip");
            var applied = new StatusInstance(skipped.Id, skipped.DefinitionId, EntityRef.Participant(Player),
                EntityRef.Participant(Player), skipped.Stacks, skipped.Duration);
            var result = CombatEngine.Resolve(state, state.Revision, new[] {
                CombatEffect.Eliminate(Player, Enemy), CombatEffect.ApplyStatus(skipped), CombatEffect.ApplyStatus(applied)
            });
            Assert.IsTrue(result.Accepted); Assert.AreEqual(256, result.State.Statuses.Count);
            Assert.AreSame(applied, result.State.Statuses.Last()); Assert.AreEqual(255, state.Statuses.Count);
            CollectionAssert.AreEqual(new[] { 0, 2 }, result.RuleEvents.Select(item => item.Index));
            Assert.AreEqual(1, result.Events.Count); Assert.IsInstanceOf<CombatStatusEvent>(result.RuleEvents[1]);
        }

        [TestCase("combat.2")] [TestCase("combat.3")] [TestCase("combat.4")] [TestCase("combat.5")] [TestCase("combat.6")] [TestCase("combat.7")]
        public void CombatStateRoundTripPreservesStatusAndEliminationAndRejectsTheOldRulesVersion(string oldVersion)
        {
            var state = Create(); var status = Status("persisted", 3, StatusDuration.ForTriggers(2));
            var result = CombatEngine.Resolve(state, state.Revision, new[] { CombatEffect.ApplyStatus(status), CombatEffect.Eliminate(Player, Enemy) });
            Assert.IsTrue(result.Accepted);
            var serializer = new DataContractJsonSerializer(typeof(CombatState)); string json;
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, result.State); json = Encoding.UTF8.GetString(stream.ToArray()); stream.Position = 0;
                var restored = (CombatState)serializer.ReadObject(stream); CombatEngine.Validate(restored);
                Assert.AreEqual(result.State.Revision, restored.Revision); Assert.AreEqual("combat.8", restored.RulesVersion);
                Assert.AreEqual(0, restored.Get(Enemy).Health); Assert.AreEqual(3, restored.Get(Enemy).Block);
                Assert.AreEqual(status.Id, restored.Statuses.Single().Id); Assert.AreEqual(status.DefinitionId, restored.Statuses[0].DefinitionId);
                Assert.AreEqual(status.Source.Kind, restored.Statuses[0].Source.Kind); Assert.AreEqual(status.Source.Id, restored.Statuses[0].Source.Id);
                Assert.AreEqual(status.Target.Id, restored.Statuses[0].Target.Id); Assert.AreEqual(3, restored.Statuses[0].Stacks);
                Assert.AreEqual(DurationKind.TriggerCount, restored.Statuses[0].Duration.Kind); Assert.AreEqual(2, restored.Statuses[0].Duration.RemainingTriggers);
            }
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json.Replace("combat.8", oldVersion))))
            {
                var old = (CombatState)serializer.ReadObject(stream);
                Assert.Throws<ArgumentException>(() => CombatEngine.Validate(old));
                Assert.AreEqual(CombatError.InvalidState, CombatEngine.Resolve(old, old.Revision,
                    new[] { new CombatEffect(CombatEffectKind.GainBlock, Player, Player, 1) }).Error);
            }
        }
    }
}
