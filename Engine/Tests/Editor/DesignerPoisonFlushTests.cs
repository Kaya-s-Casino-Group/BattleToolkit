using System;
using System.Linq;
using System.Xml;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class DesignerPoisonFlushTests
    {
        private static readonly ParticipantId P = new ParticipantId("player"), E = new ParticipantId("enemy");
        private static XmlDocument Document(int[] ranks, bool flush, Action<XmlDocument> edit = null)
        {
            var d = new XmlDocument(); d.LoadXml(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle.xml"));
            d.SelectSingleNode("//Deck[@id='standard-deck']").InnerXml = string.Join("", ranks.Select((r, i) =>
                "<Card id='test-" + i + "' face='standard:" + (flush ? 1 : i % 4 + 1) + ":" + r + "'/>"));
            var enemy = (XmlElement)d.SelectSingleNode("//Enemy[@id='rotator']"); enemy.InnerXml = "";
            enemy.SetAttribute("maxHealth", "10000"); enemy.SetAttribute("damage", "0"); edit?.Invoke(d); return d;
        }
        private static BattleSession Load(int[] ranks, bool flush = false, Action<XmlDocument> edit = null)
        {
            var result = BattleContentCompiler.CompileXml(Document(ranks, flush, edit).OuterXml);
            Assert.IsTrue(result.Success, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
            var build = BattleContentFactory.Create(result.Content, new BattleSetupRequest("standard-single-battle", "designer-feedback", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(build.Success); Act(build.Session, CardDuelAction.Start); Act(build.Session, CardDuelAction.SkipDraw); return build.Session;
        }
        private static void Gear(XmlDocument d, string inner)
        {
            var node = d.CreateElement("Relic"); node.SetAttribute("id", "check"); node.InnerXml = inner;
            d.SelectSingleNode("/BattleContent/Relics").AppendChild(node);
            node = d.CreateElement("Relic"); node.SetAttribute("ref", "check"); node.SetAttribute("instanceId", "check");
            d.SelectSingleNode("//Loadout[@id='baseline']").AppendChild(node);
        }
        private static BattleSessionResult Act(BattleSession s, CardDuelAction action, CardInstanceId[] cards = null)
        { var r = s.Execute(s.State.Revision, P, action, cards); Assert.IsTrue(r.Accepted, action + ": " + r.Error); return r; }
        private static CardInstanceId[] Hand(BattleSession s) => s.State.Zone(CardZoneKinds.Hand, P).Cards.ToArray();
        private static CombatEvent[] Hits(BattleSessionResult r) => r.Steps[0].Transition.Events.Where(e => e.Effect.Kind == CombatEffectKind.Damage).ToArray();
        private static CombatEvent Heal(BattleSessionResult r) => r.Steps[0].Transition.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal);
        private static int Poison(BattleSession s) => s.State.Duel.Combat.Statuses.Single(x => x.DefinitionId.Value == "bleed").Stacks;
        [TestCase(CombatTimingKind.TurnStarted)]
        [TestCase(CombatTimingKind.ActionCompleted)]
        [TestCase(CombatTimingKind.PhaseEnding)]
        public void ConfiguredPoisonTimingChangesTheRealEnemySettlementBoundary(CombatTimingKind timing)
        {
            var s = Load(new[] { 2, 7, 9 }, edit: d => {
                ((XmlElement)d.SelectSingleNode("//Status[@id='bleed']/PeriodicDamage")).SetAttribute("timing", timing.ToString());
                ((XmlElement)d.SelectSingleNode("//Enemy[@id='rotator']")).SetAttribute("damage", "7");
                var other = d.CreateElement("Enemy"); other.SetAttribute("id", "second"); other.SetAttribute("ref", "rotator");
                d.SelectSingleNode("//Encounter[@id='standard-single-battle']").AppendChild(other);
                Gear(d, "<BoundaryTrigger id='seed-poison' timing='BattleStarted'><Effect kind='ApplyStatus' statusRef='bleed' stacks='5' target='Opponents' selection='AllLiving'/></BoundaryTrigger>");
            });
            var end = Act(s, CardDuelAction.EndTurn);
            var events = end.Steps.SelectMany(step => step.Transition.Events).Where(e => e.Effect.Kind == CombatEffectKind.Damage).ToArray();
            string[] expected = timing == CombatTimingKind.TurnStarted
                ? new[] { "poison:enemy", "attack:enemy", "poison:second", "attack:second" }
                : timing == CombatTimingKind.ActionCompleted
                    ? new[] { "attack:enemy", "poison:enemy", "attack:second", "poison:second" }
                    : new[] { "attack:enemy", "attack:second", "poison:enemy", "poison:second" };
            CollectionAssert.AreEqual(expected, events.Select(e => e.Provenance.Origin == CombatEffectOrigin.Periodic
                ? "poison:" + e.Effect.Target.Value : "attack:" + e.Effect.Source.Value));
            Assert.AreEqual(786, s.State.Duel.Combat.Get(P).Health);
            Assert.IsTrue(s.State.Duel.Combat.Statuses.Where(st => st.DefinitionId.Value == "bleed").All(st => st.Stacks == 4));
            Assert.IsTrue(events.Where(e => e.Provenance.Origin == CombatEffectOrigin.Periodic).All(e => e.DamageToHealth == 5));
        }
        [TestCase(CombatTimingKind.TurnStarted, 5, 4)]
        [TestCase(CombatTimingKind.ActionCompleted, 0, 5)]
        [TestCase(CombatTimingKind.PhaseEnding, 5, 4)]
        public void ConfiguredPoisonTimingDefinesWhetherSkippedEnemyActionsConsumeStacks(CombatTimingKind timing, int damage, int stacks)
        {
            var s = Load(new[] { 2, 7, 9 }, edit: d => {
                ((XmlElement)d.SelectSingleNode("//Status[@id='bleed']/PeriodicDamage")).SetAttribute("timing", timing.ToString());
                ((XmlElement)d.SelectSingleNode("//Enemy[@id='rotator']")).SetAttribute("damage", "7");
                Gear(d, "<BoundaryTrigger id='seed-poison' timing='BattleStarted'>" +
                    "<Effect kind='ApplyStatus' statusRef='bleed' stacks='5' target='Opponents'/>" +
                    "<Effect kind='ApplyStatus' statusRef='stun' target='Opponents'/></BoundaryTrigger>");
            });
            var end = Act(s, CardDuelAction.EndTurn);
            Assert.AreEqual(10000 - damage, s.State.Duel.Combat.Get(E).Health);
            Assert.AreEqual(800, s.State.Duel.Combat.Get(P).Health);
            Assert.AreEqual(stacks, Poison(s));
            Assert.AreEqual(1, end.Steps.SelectMany(step => step.Transition.RuleEvents).OfType<CombatActionWindowSkippedEvent>().Count());
        }
        [TestCase(CombatTimingKind.TurnStarted, 795, 795, 791)]
        [TestCase(CombatTimingKind.ActionCompleted, 800, 795, 795)]
        [TestCase(CombatTimingKind.PhaseEnding, 800, 800, 795)]
        public void ConfiguredPoisonTimingAlsoAppliesToThePlayer(CombatTimingKind timing, int startingHealth, int afterPlayHealth, int nextTurnHealth)
        {
            var s = Load(new[] { 2, 7, 9 }, edit: d => {
                ((XmlElement)d.SelectSingleNode("//Status[@id='bleed']/PeriodicDamage")).SetAttribute("timing", timing.ToString());
                Gear(d, "<BoundaryTrigger id='seed-poison' timing='BattleStarted'><Effect kind='ApplyStatus' statusRef='bleed' stacks='5' target='Self'/></BoundaryTrigger>");
            });
            Assert.AreEqual(startingHealth, s.State.Duel.Combat.Get(P).Health);
            Act(s, CardDuelAction.Play, Hand(s).Take(1).ToArray());
            Assert.AreEqual(afterPlayHealth, s.State.Duel.Combat.Get(P).Health);
            Act(s, CardDuelAction.EndTurn);
            Assert.AreEqual(nextTurnHealth, s.State.Duel.Combat.Get(P).Health);
            Assert.AreEqual(timing == CombatTimingKind.TurnStarted ? 3 : 4, Poison(s));
        }
        [TestCase(CombatTimingKind.TurnStarted)]
        [TestCase(CombatTimingKind.ActionCompleted)]
        [TestCase(CombatTimingKind.PhaseEnding)]
        public void ConfiguredApplierPoisonUsesPlayerClockInsteadOfPoisonedEnemyClock(CombatTimingKind timing)
        {
            var s = Load(new[] { 2, 3, 4, 5, 6 }, edit: d => {
                var poison = (XmlElement)d.SelectSingleNode("//Status[@id='bleed']/PeriodicDamage");
                poison.SetAttribute("timing", timing.ToString()); poison.SetAttribute("timingSubject", "Applier");
                ((XmlElement)d.SelectSingleNode("//Enemy[@id='rotator']")).SetAttribute("damage", "7");
            });
            var before = s.State;
            var preview = s.Engine.PreviewPlay(before, before.Revision, P, Hand(s));
            Assert.IsTrue(preview.Accepted); Assert.AreSame(before, s.State);
            var play = Act(s, CardDuelAction.Play, Hand(s));
            Assert.AreEqual(timing == CombatTimingKind.ActionCompleted ? 4 : 5, Poison(s));
            Assert.AreEqual(preview.State.Duel.Combat.Get(E).Health, s.State.Duel.Combat.Get(E).Health);
            Assert.AreEqual(preview.State.Duel.Combat.Statuses.Single().Stacks, Poison(s));
            var end = Act(s, CardDuelAction.EndTurn);
            var events = play.Steps.Concat(end.Steps).SelectMany(step => step.Transition.Events)
                .Where(e => e.Provenance.Origin == CombatEffectOrigin.Periodic ||
                    e.Effect.Kind == CombatEffectKind.Damage && e.Effect.Source == E).ToArray();
            CollectionAssert.AreEqual(timing == CombatTimingKind.TurnStarted ? new[] { "attack", "poison" } : new[] { "poison", "attack" },
                events.Select(e => e.Provenance.Origin == CombatEffectOrigin.Periodic ? "poison" : "attack"));
            Assert.AreEqual(9955, s.State.Duel.Combat.Get(E).Health);
            Assert.AreEqual(4, Poison(s));
        }
        [Test] public void ConfiguredApplierStartPoisonEndsBattleBeforeEnemyActiveEffects()
        {
            var s = Load(new[] { 2, 7, 9 }, edit: d => {
                var poison = (XmlElement)d.SelectSingleNode("//Status[@id='bleed']/PeriodicDamage");
                poison.SetAttribute("timingSubject", "Applier");
                var enemy = (XmlElement)d.SelectSingleNode("//Enemy[@id='rotator']"); enemy.RemoveAttribute("damage");
                enemy.InnerXml = "<Effects><Effect id='poison' kind='ApplyStatus' statusRef='bleed' stacks='800' target='Opponents'/>" +
                    "<Effect id='block' kind='GainBlock' amount='1' target='Self'/></Effects>";
            });
            Act(s, CardDuelAction.EndTurn);
            Assert.AreEqual(800, s.State.Duel.Combat.Get(P).Health);
            Act(s, CardDuelAction.SkipDraw);
            var end = Act(s, CardDuelAction.EndTurn);
            Assert.AreEqual(ScoredDuelPhase.Lost, s.State.Duel.Phase);
            Assert.AreEqual(0, s.State.Duel.Combat.Get(P).Health);
            Assert.IsFalse(end.Steps.SelectMany(step => step.Transition.Events)
                .Any(e => e.Effect.Source == E && e.Provenance.Origin == CombatEffectOrigin.Primary));
        }
        [Test] public void EachSubmittedCardHitsOnceAndAddsOneStackBeforeNextHit()
        {
            var s = Load(new[] { 2, 3, 4, 5, 6 }); var play = Act(s, CardDuelAction.Play, Hand(s));
            Assert.AreEqual(5, Poison(s)); Assert.AreEqual(5, Hits(play).Length);
            CollectionAssert.AreEqual(new long[] { 4, 6, 8, 10, 12 }, Hits(play).Select(h => h.SettledAmount).OrderBy(x => x));
            CollectionAssert.AreEqual(Enumerable.Range(0, 5).SelectMany(_ => new[] { CombatEffectKind.Damage, CombatEffectKind.ApplyStatus }),
                play.Steps[0].Transition.RuleEvents.Where(e => e.Effect != null).Select(e => e.Effect.Kind));
            long before = s.State.Duel.Combat.Get(E).Health; Act(s, CardDuelAction.EndTurn);
            Assert.AreEqual(before - 5, s.State.Duel.Combat.Get(E).Health); Assert.AreEqual(4, Poison(s));
            Act(s, CardDuelAction.SkipDraw); Act(s, CardDuelAction.EndTurn);
            Assert.AreEqual(before - 9, s.State.Duel.Combat.Get(E).Health); Assert.AreEqual(3, Poison(s));
        }
        [Test] public void ShortStraightAndExtraHitUseActualCardsAndRepeatOnHitStatus()
        {
            var s = Load(new[] { 2, 3, 4 }, edit: d => Gear(d, "<RuleValue id='short' key='MinStraightCards' delta='-2'/><HandModifier id='extra' category='Straight' operation='AddHits' stepId='damage/1' hits='1'/>"));
            var r = Act(s, CardDuelAction.Play, Hand(s)); Assert.AreEqual(4, Hits(r).Length); Assert.AreEqual(4, Poison(s));
            Assert.AreEqual(18 + Hits(r)[0].SettledAmount, Hits(r).Sum(h => h.SettledAmount));
        }
        [TestCase(new[] { 2, 3, 4, 5, 6 }, HandCategory.StraightFlush, 160, 16, 5)]
        [TestCase(new[] { 7, 7, 7 }, HandCategory.Flush, 84, 16, 1)]
        [TestCase(new[] { 7, 7, 7, 8 }, HandCategory.Flush, 116, 23, 1)]
        [TestCase(new[] { 2, 7, 9 }, HandCategory.Flush, 72, 14, 1)]
        public void FlushUsesHighestScoreWhileCompatibleMechanismsAndBaseHealingRemainIndependent(int[] ranks, HandCategory category, int damage, int healing, int hits)
        {
            var s = Load(ranks, true); var r = Act(s, CardDuelAction.Play, Hand(s));
            Assert.AreEqual(category, r.Steps[0].Transition.Score.Category); Assert.AreEqual(hits, Hits(r).Length);
            Assert.AreEqual(damage, Hits(r).Sum(h => h.SettledAmount)); Assert.AreEqual(healing, Heal(r).SettledAmount);
            if (category == HandCategory.StraightFlush) Assert.AreEqual(5, Poison(s));
        }
        [Test] public void ShortStraightFlushInheritsShortStraightAndFlushBonus()
        {
            var s = Load(new[] { 2, 3, 4 }, true, d => Gear(d, "<RuleValue id='short' key='MinStraightCards' delta='-2'/>"));
            var r = Act(s, CardDuelAction.Play, Hand(s)); Assert.AreEqual(3, Hits(r).Length); Assert.AreEqual(3, Poison(s)); Assert.AreEqual(7, Heal(r).SettledAmount);
            Assert.AreEqual(HandCategory.Flush, r.Steps[0].Transition.Score.Category);
            Assert.AreEqual(36, Hits(r).Sum(h => h.SettledAmount));
        }
        [Test] public void FlushHealingIgnoresScoringRelicMultipliersAndFlushLevelButRestoresHealth()
        {
            var s = Load(new[] { 2, 3, 4, 5, 6 }, true, d => {
                Gear(d, "<Score operation='MultiplyMultiplier' amount='3'/><BoundaryTrigger id='injury' timing='BattleStarted'><Effect kind='Damage' amount='100' target='Self'/></BoundaryTrigger>");
                var level = d.CreateElement("HandLevel"); level.SetAttribute("category", "Flush"); level.SetAttribute("level", "9"); d.SelectSingleNode("//Loadout[@id='baseline']").AppendChild(level);
            });
            var r = Act(s, CardDuelAction.Play, Hand(s)); Assert.AreEqual(480, Hits(r).Sum(h => h.SettledAmount));
            Assert.AreEqual(16, Heal(r).HealthRestored); Assert.AreEqual(716, s.State.Duel.Combat.Get(P).Health);
        }
        [Test] public void DuplicateSameSuitTriplesHitEveryEnemy()
        {
            var s = Load(new[] { 7, 7, 7 }, true, d => {
                var encounter = d.SelectSingleNode("//Encounter[@id='standard-single-battle']");
                var enemy = d.CreateElement("Enemy"); enemy.SetAttribute("id", "second"); enemy.SetAttribute("ref", "rotator"); encounter.AppendChild(enemy);
            });
            var r = Act(s, CardDuelAction.Play, Hand(s)); Assert.AreEqual(2, Hits(r).Length); Assert.IsTrue(Hits(r).All(h => h.SettledAmount == 84)); Assert.AreEqual(16, Heal(r).SettledAmount);
        }
        [Test] public void RoyalFlushKeepsForcedElimination()
        {
            var s = Load(new[] { 10, 11, 12, 13, 14 }, true); var r = Act(s, CardDuelAction.Play, Hand(s));
            Assert.IsTrue(s.State.Duel.IsFinished); Assert.IsEmpty(Hits(r)); Assert.IsFalse(r.Steps[0].Transition.Events.Any(e => e.Effect.Kind == CombatEffectKind.Heal));
        }
        [Test] public void PreviewDoesNotCommitPoisonHealingOrRandomState()
        {
            var s = Load(new[] { 2, 3, 4, 5, 6 }, true); var before = s.State;
            var preview = s.Engine.PreviewPlay(before, before.Revision, P, Hand(s)); Assert.IsTrue(preview.Accepted); Assert.AreSame(before, s.State); Assert.IsEmpty(s.State.Duel.Combat.Statuses);
            var result = Act(s, CardDuelAction.Play, Hand(s)); Assert.AreEqual(preview.State.Duel.Combat.Get(E).Health, result.State.Duel.Combat.Get(E).Health);
            Assert.AreEqual(preview.State.Duel.Combat.Statuses.Single().Stacks, Poison(s));
        }
        [Test] public void SharedChipBonusesAreCountedOnceAcrossCardHits()
        {
            var s = Load(new[] { 2, 3, 4, 5, 6 }, edit: d => Gear(d, "<Score operation='AddPoints' amount='10'/>"));
            var r = Act(s, CardDuelAction.Play, Hand(s));
            Assert.AreEqual(60, Hits(r).Sum(h => h.SettledAmount)); Assert.AreEqual(5, Poison(s));
        }
        [Test] public void SameSuitFullHouseUsesHighestScoreOnceAndKeepsHealing()
        {
            var s = Load(new[] { 7, 7, 7, 8, 8 }, true); var r = Act(s, CardDuelAction.Play, Hand(s));
            Assert.AreEqual(HandCategory.FullHouse, r.Steps[0].Transition.Score.Category);
            CollectionAssert.AreEqual(new long[] { 148 }, Hits(r).Select(h => h.SettledAmount)); Assert.AreEqual(29, Heal(r).SettledAmount);
        }
        [Test] public void FlushHealingRelicsRecognizeMechanismWhileDamageUsesHighestCategory()
        {
            var s = Load(new[] { 2, 3, 4, 5, 6 }, true, d => Gear(d,
                "<Numeric id='healing' stat='HealingDone' operation='AddRate' value='0.5' origins='Primary' hand='Flush'/>"));
            var r = Act(s, CardDuelAction.Play, Hand(s)); Assert.AreEqual(24, Heal(r).SettledAmount);
            Assert.AreEqual(160, Hits(r).Sum(h => h.SettledAmount)); Assert.AreEqual(5, Poison(s));
        }
        [Test] public void StraightMechanismRelicTriggersOnStraightFlushButStraightScoreRelicDoesNot()
        {
            var s = Load(new[] { 2, 3, 4, 5, 6 }, true, d => {
                Gear(d, "<Score hand='Straight' operation='MultiplyMultiplier' amount='3'/>");
                var relic = d.CreateElement("Relic"); relic.SetAttribute("ref", "straight-dot"); relic.SetAttribute("instanceId", "poison-mechanism");
                d.SelectSingleNode("//Loadout[@id='baseline']").AppendChild(relic);
            });
            var before = s.State;
            var preview = s.Engine.PreviewPlay(before, before.Revision, P, Hand(s));
            Assert.IsTrue(preview.Accepted); Assert.AreSame(before, s.State);
            var result = Act(s, CardDuelAction.Play, Hand(s));
            Assert.AreEqual(HandCategory.StraightFlush, result.Steps[0].Transition.Score.Category);
            Assert.AreEqual(160, Hits(result).Sum(h => h.SettledAmount));
            Assert.AreEqual(10, Poison(s));
            Assert.AreEqual(preview.State.Duel.Combat.Get(E).Health, s.State.Duel.Combat.Get(E).Health);
        }
        [Test] public void PoisonNeverTransfersFromAKillingHitToAnUnhitTarget()
        {
            var s = Load(new[] { 2, 3, 4, 5, 6 }, edit: d => {
                var weak = d.CreateElement("Enemy"); weak.SetAttribute("id", "weak"); weak.SetAttribute("maxHealth", "1"); weak.SetAttribute("damage", "0");
                d.SelectSingleNode("/BattleContent/Enemies").AppendChild(weak);
                ((XmlElement)d.SelectSingleNode("//Encounter[@id='standard-single-battle']/Enemy")).SetAttribute("ref", "weak");
                var extra = d.CreateElement("Enemy"); extra.SetAttribute("id", "second"); extra.SetAttribute("ref", "rotator");
                d.SelectSingleNode("//Encounter[@id='standard-single-battle']").AppendChild(extra);
            });
            var r = Act(s, CardDuelAction.Play, Hand(s)); Assert.AreEqual(E, Hits(r)[0].Effect.Target);
            Assert.IsTrue(Hits(r).Skip(1).All(h => h.Effect.Target.Value == "second"));
            Assert.AreEqual(4, Poison(s)); Assert.AreEqual("second", s.State.Duel.Combat.Statuses.Single().Target.Id);
        }
        [Test] public void PerSubmittedCardStatusAmountRemainsConfigurable()
        {
            var s = Load(new[] { 2, 3, 4, 5, 6 }, edit: d => {
                var status = (XmlElement)d.SelectSingleNode("//Hand[@category='Straight']/Effect/OnHitStatus");
                status.SetAttribute("stacks", "0"); status.SetAttribute("stacksPerSubmittedCard", "1");
            });
            Act(s, CardDuelAction.Play, Hand(s)); Assert.AreEqual(25, Poison(s));
        }
        [Test] public void OnHitStatusRejectsNonDamageAndEnemySubmittedCardDependencies()
        {
            var d = Document(new[] { 2, 3, 4, 5, 6 }, false);
            ((XmlElement)d.SelectSingleNode("//Hand[@category='Straight']/Effect")).SetAttribute("kind", "Heal");
            Assert.IsFalse(BattleContentCompiler.CompileXml(d.OuterXml).Success);
            d = Document(new[] { 2, 3, 4, 5, 6 }, false);
            var enemy = (XmlElement)d.SelectSingleNode("//Enemy[@id='rotator']"); enemy.RemoveAttribute("damage");
            enemy.InnerXml = "<Effects><Effect id='hit' kind='Damage' amount='1'><OnHitStatus statusRef='bleed' stacks='0' stacksPerSubmittedCard='1'/></Effect></Effects>";
            Assert.IsFalse(BattleContentCompiler.CompileXml(d.OuterXml).Success);
        }
    }
}
