using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CombatWrathImmunityTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), Body = new ParticipantId("body"), Hand = new ParticipantId("hand");
        private static CombatStatusCatalog Catalog()
        {
            var xml = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle-2026-10-20.xml"));
            xml.Root.Element("Statuses").Add(XElement.Parse("<Status id='test-shell' lifetime='UntilRemoved'><DamageImmunity/></Status>"));
            xml.Root.Element("Statuses").Add(XElement.Parse("<Status id='test-poison' lifetime='UntilRemoved'><PeriodicDamage timing='TurnStarted' amount='3'/></Status>"));
            var compiled = BattleContentCompiler.CompileXml(xml.ToString());
            Assert.IsTrue(compiled.Success, string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
            return new CombatStatusCatalog("wrath-immunity.test", compiled.Content.Statuses.Values);
        }
        private static StatusInstance Status(string id, string definition = "test-shell", int stacks = 2) => new StatusInstance(
            new StatusInstanceId(id), new StatusDefinitionId(definition), EntityRef.Participant(Player), EntityRef.Participant(Body), stacks, StatusDuration.UntilRemoved());
        private static CombatState State(params StatusInstance[] statuses) => new CombatState("wrath-immunity", 0, new[] {
            new CombatantState(Player, "hero", "players", 100, 100),
            new CombatantState(Body, "wrath", "enemies", 100, 100, 7),
            new CombatantState(Hand, "hand", "enemies", 1, 1)
        }, statuses.Length == 0 ? new[] { Status("shell") } : statuses);
        private static CombatEffect Hit(long amount = 10, ParticipantId? target = null) => new CombatEffect(CombatEffectKind.Damage, Player, target ?? Body, amount);

        [Test] public void TwoSegmentsAreZeroAndOnlyThirdSegmentConsumesBlockAndHealth()
        {
            var state = State();
            var result = CombatEngine.Resolve(state, 0, new[] { Hit(), Hit(), Hit() }, statusCatalog: Catalog());
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            CollectionAssert.AreEqual(new long[] { 0, 0, 10 }, result.Events.Select(e => e.SettledDamage));
            Assert.AreEqual(97, result.State.Get(Body).Health);
            Assert.AreEqual(0, result.State.Get(Body).Block);
            Assert.IsEmpty(result.State.Statuses);
            Assert.AreEqual(2, state.Statuses.Single().Stacks, "Input state remains immutable.");
        }

        [Test] public void ZeroDamageAndDamageToAnotherUnitDoNotConsumeBodyLayers()
        {
            var result = CombatEngine.Resolve(State(), 0, new[] { Hit(0), Hit(1, Hand) }, statusCatalog: Catalog());
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            Assert.AreEqual(2, result.State.Statuses.Single().Stacks);
            Assert.IsFalse(result.State.Get(Hand).IsAlive);
            Assert.AreEqual(100, result.State.Get(Body).Health);
        }

        private sealed class AddIncoming : ICombatAmountModifier
        {
            public CombatAmountModification Modify(CombatAmountContext context) => new CombatAmountModification(context.Amount,
                context.Stage == CombatAmountStage.Incoming ? new[] { new CombatAmountContribution("test", "incoming-add", 7) } : null);
        }

        [Test] public void LaterIncomingAdditionsCannotRestoreImmuneDamageOrAbsorbBlock()
        {
            var result = CombatEngine.Resolve(State(), 0, new[] { Hit() }, new ScaledIncomingDamageModifier(1, 5), Catalog(), amountModifier: new AddIncoming());
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            Assert.AreEqual(0, result.Events.Single().SettledDamage);
            Assert.AreEqual(0, result.Events.Single().DamageToHealth);
            Assert.AreEqual(0, result.Events.Single().BlockAbsorbed);
            Assert.AreEqual(7, result.State.Get(Body).Block);
            Assert.AreEqual(1, result.State.Statuses.Single().Stacks);
        }

        [Test] public void PeriodicPoisonConsumesOneLayerButDoesNoDamage()
        {
            var state = State(Status("shell"), Status("poison", "test-poison", 1));
            var result = CombatEngine.ResolveTiming(state, 0, CombatTimingSignal.TurnStarted("turn/1", new[] { Body }), statusCatalog: Catalog());
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            Assert.AreEqual(0, result.Events.Single().SettledDamage);
            Assert.AreEqual(CombatEffectOrigin.Periodic, result.Events.Single().Provenance.Origin);
            Assert.AreEqual(1, result.State.Statuses.Single(s => s.DefinitionId.Value == "test-shell").Stacks);
        }

        private sealed class Reflection : ICombatReactionProvider
        {
            public IEnumerable<CombatReaction> React(CombatReactionContext context) => new[] {
                new CombatReaction(Hit(3), "test-reflection", Player, CombatEffectOrigin.Reflection)
            };
        }

        [Test] public void ReflectedDamageConsumesOneLayer()
        {
            var result = CombatEngine.Resolve(State(), 0, new[] { new CombatEffect(CombatEffectKind.GainBlock, Player, Player, 1) },
                statusCatalog: Catalog(), reactionProvider: new Reflection());
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            var hit = result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Damage);
            Assert.AreEqual(CombatEffectOrigin.Reflection, hit.Provenance.Origin);
            Assert.AreEqual(0, hit.SettledDamage);
            Assert.AreEqual(1, result.State.Statuses.Single().Stacks);
        }

        [Test] public void ImmunityDoesNotBlockStatusApplicationOrStrongElimination()
        {
            var result = CombatEngine.Resolve(State(), 0, new[] { Hit(), CombatEffect.ApplyStatus(Status("poison", "test-poison", 1)) }, statusCatalog: Catalog());
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            Assert.AreEqual(1, result.State.Statuses.Single(s => s.DefinitionId.Value == "test-poison").Stacks);
            var eliminated = CombatEngine.Resolve(result.State, result.State.Revision, new[] { CombatEffect.Eliminate(Player, Body) }, statusCatalog: Catalog());
            Assert.IsTrue(eliminated.Accepted, eliminated.Error.ToString());
            Assert.IsFalse(eliminated.State.Get(Body).IsAlive);
            Assert.AreEqual(CombatDeathCause.Eliminate, eliminated.Events.Single().DeathCause);
        }

        [Test] public void ReapplyingTwoLayersRefreshesInsteadOfAccumulating()
        {
            var first = CombatEngine.Resolve(State(), 0, new[] { Hit(), CombatEffect.ApplyStatus(Status("refresh")) }, statusCatalog: Catalog());
            Assert.IsTrue(first.Accepted, first.Error.ToString());
            Assert.AreEqual(2, first.State.Statuses.Single().Stacks);
            var second = CombatEngine.Resolve(first.State, first.State.Revision, new[] { CombatEffect.ApplyStatus(Status("refresh-again")) }, statusCatalog: Catalog());
            Assert.IsTrue(second.Accepted, second.Error.ToString());
            Assert.AreEqual(2, second.State.Statuses.Single().Stacks);
        }

        [Test] public void OrdinaryArmorRetainsItsQuarterDamageMultiplier()
        {
            var result = CombatEngine.Resolve(State(Status("armor", "layer-armor", 2)), 0, new[] { Hit(40) }, statusCatalog: Catalog());
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            Assert.AreEqual(10, result.Events.Single().SettledDamage);
        }

        [Test] public void FailedLaterEffectRollsBackConsumedImmunity()
        {
            var state = State();
            var result = CombatEngine.Resolve(state, 0, new[] { Hit(), new CombatEffect(CombatEffectKind.GainBlock, Body, Body, long.MaxValue) }, statusCatalog: Catalog());
            Assert.IsFalse(result.Accepted);
            Assert.AreSame(state, result.State);
            Assert.AreEqual(2, state.Statuses.Single().Stacks);
            Assert.IsEmpty(result.RuleEvents);
        }
    }
}
