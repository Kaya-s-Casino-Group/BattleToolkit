using System;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CompatibleHandMechanismTests
    {
        private static XDocument Document(int[] ranks, bool flush = false, int enemies = 1, Action<XDocument> edit = null)
        {
            var d = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle.xml"));
            d.Root.Element("Decks").Elements().First().ReplaceNodes(ranks.Select((rank, i) => new XElement("Card",
                new XAttribute("id", "c" + i), new XAttribute("face", "standard:" + (flush ? 1 : i % 4 + 1) + ":" + rank))));
            var enemy = d.Root.Element("Enemies").Elements().First(e => (string)e.Attribute("id") == "rotator");
            enemy.RemoveNodes(); enemy.SetAttributeValue("maxHealth", 10000); enemy.SetAttributeValue("damage", 0);
            var encounter = d.Root.Element("Encounters").Elements().First();
            for (int i = 1; i < enemies; i++) encounter.Add(new XElement("Enemy", new XAttribute("id", "enemy-" + i), new XAttribute("ref", "rotator")));
            edit?.Invoke(d); return d;
        }
        private static XElement Hand(XDocument d, string category, string variant = null) => d.Root.Element("HandRules").Elements().First().Elements("Hand")
            .Single(e => (string)e.Attribute("category") == category && (string)e.Attribute("variant") == variant);
        private static void Gear(XDocument d, string effects)
        {
            var relic = XElement.Parse("<Relic id='test-gear'>" + effects + "</Relic>");
            d.Root.Element("Relics").Add(relic);
            d.Root.Element("Loadouts").Elements().First().Add(new XElement("Relic", new XAttribute("ref", "test-gear"), new XAttribute("instanceId", "test-gear")));
        }
        private static BattleSession Session(int[] ranks, bool flush = false, int enemies = 1, Action<XDocument> edit = null)
        {
            var compiled = BattleContentCompiler.CompileXml(Document(ranks, flush, enemies, edit).ToString());
            Assert.IsTrue(compiled.Success, string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
            var built = BattleContentFactory.Create(compiled.Content, new BattleSetupRequest("standard-single-battle", "compatible", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(built.Success, string.Join("; ", built.Diagnostics.Select(d => d.Message)));
            var session = built.Session; Act(session, CardDuelAction.Start); Act(session, CardDuelAction.SkipDraw); return session;
        }
        private static BattleSessionResult Act(BattleSession session, CardDuelAction action, CardInstanceId[] cards = null)
        {
            var result = session.Execute(session.State.Revision, session.State.Duel.PlayerId, action, cards);
            Assert.IsTrue(result.Accepted, action + ": " + result.Error); return result;
        }
        private static CardInstanceId[] Cards(BattleSession session) => session.State.Zone(CardZoneKinds.Hand).Cards.ToArray();
        private static CombatEvent[] Hits(BattleSessionResult result) => result.Steps[0].Transition.Events.Where(e => e.Effect.Kind == CombatEffectKind.Damage).ToArray();
        private static int Poison(BattleSession session, string enemy = "enemy") =>
            session.State.Duel.Combat.Statuses.Single(s => s.DefinitionId.Value == "bleed" && s.Target.Kind == EntityKind.Participant && s.Target.Id == enemy).Stacks;

        [Test] public void StraightFlushUsesHighestBudgetWithFiveHitsPoisonAndIndependentHealing()
        {
            var s = Session(new[] { 2, 3, 4, 5, 6 }, true);
            var facts = s.Engine.PreviewHandFacts(s.State, s.State.Duel.PlayerId, Cards(s));
            CollectionAssert.AreEquivalent(new[] { HandCategory.StraightFlush, HandCategory.Flush, HandCategory.Straight }, facts.CompatibleCategories);
            Assert.IsFalse(facts.MatchesCategory(HandCategory.HighCard));
            var result = Act(s, CardDuelAction.Play, Cards(s));
            Assert.AreEqual(HandCategory.StraightFlush, result.Steps[0].Transition.Score.Category);
            Assert.AreEqual(160, result.Steps[0].Transition.Score.Total);
            Assert.AreEqual(5, Hits(result).Length); Assert.AreEqual(160, Hits(result).Sum(h => h.SettledAmount));
            CollectionAssert.AreEquivalent(new long[] { 16, 24, 32, 40, 48 }, Hits(result).Select(h => h.SettledAmount));
            Assert.AreEqual(5, Poison(s)); Assert.AreEqual(16, result.Steps[0].Transition.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).SettledAmount);
        }
        [Test] public void FullHouseUsesOneHighestDamageForEachEnemyAndControlMechanism()
        {
            var s = Session(new[] { 7, 7, 7, 8, 8 }, enemies: 2, edit: d => Gear(d,
                "<BoundaryTrigger id='initial-poison' timing='BattleStarted'><Effect kind='ApplyStatus' statusRef='bleed' stacks='2' target='Self'/></BoundaryTrigger>"));
            var facts = s.Engine.PreviewHandFacts(s.State, s.State.Duel.PlayerId, Cards(s));
            CollectionAssert.AreEquivalent(new[] { HandCategory.FullHouse, HandCategory.ThreeOfAKind, HandCategory.TwoPair, HandCategory.OnePair }, facts.CompatibleCategories);
            var result = Act(s, CardDuelAction.Play, Cards(s));
            Assert.AreEqual(148, result.Steps[0].Transition.Score.Total); Assert.AreEqual(2, Hits(result).Length);
            Assert.IsEmpty(result.Steps[0].Transition.Score.Components);
            Assert.IsTrue(Hits(result).All(h => h.SettledAmount == 148));
            Assert.AreEqual(1, result.Steps[0].Transition.RuleEvents.OfType<CombatStatusChangedEvent>()
                .Count(e => e.Reason == CombatStatusChangeReason.Cleansed));
            Assert.IsFalse(s.State.Duel.Combat.Statuses.Any(st => st.DefinitionId.Value == "bleed" && st.Target.Id == "player"));
            Assert.IsTrue(s.State.Duel.Combat.Statuses.Any(st => st.DefinitionId.Value == "stun"));
            Assert.IsTrue(s.State.Duel.Combat.Statuses.Any(st => st.DefinitionId.Value == "control-immunity"));
        }
        [Test] public void FourOfAKindAndThreeOfAKindAreaMechanismsDoNotDoubleBaseDamage()
        {
            var s = Session(new[] { 7, 7, 7, 7, 8 }, true, 2);
            var facts = s.Engine.PreviewHandFacts(s.State, s.State.Duel.PlayerId, Cards(s));
            Assert.IsTrue(facts.MatchesCategory(HandCategory.FourOfAKind)); Assert.IsTrue(facts.MatchesCategory(HandCategory.ThreeOfAKind));
            Assert.IsTrue(facts.MatchesCategory(HandCategory.Flush)); Assert.IsFalse(facts.MatchesCategory(HandCategory.HighCard));
            var result = Act(s, CardDuelAction.Play, Cards(s));
            Assert.AreEqual(112, result.Steps[0].Transition.Score.Total); Assert.AreEqual(2, Hits(result).Length);
            Assert.IsTrue(Hits(result).All(h => h.SettledAmount == 112)); Assert.AreEqual(1, result.Steps[0].Transition.Events.Count(e => e.Effect.Kind == CombatEffectKind.Heal));
        }
        [Test] public void UnusedLowerCategoryScoresCannotOverflowHighestFullHouseScoring()
        {
            var s = Session(new[] { 7, 7, 7, 8, 8 }, edit: d => Gear(d,
                "<Score hand='ThreeOfAKind' operation='MultiplyMultiplier' amount='1000000000000000000'/>"));
            var r = Act(s, CardDuelAction.Play, Cards(s));
            Assert.AreEqual(148, r.Steps[0].Transition.Score.Total); Assert.IsEmpty(r.Steps[0].Transition.Score.Components);
        }
        [Test] public void ShortStraightAndTripleCombineIntoPerCardAreaAttacksWithoutExtraDamageBudget()
        {
            var s = Session(new[] { 2, 2, 2, 3, 4 }, enemies: 2,
                edit: d => Gear(d, "<RuleValue id='short' key='MinStraightCards' delta='-2'/>"));
            var facts = s.Engine.PreviewHandFacts(s.State, s.State.Duel.PlayerId, Cards(s));
            Assert.AreEqual(HandCategory.Straight, facts.Pattern.Category); Assert.IsTrue(facts.MatchesCategory(HandCategory.ThreeOfAKind));
            var result = Act(s, CardDuelAction.Play, Cards(s));
            Assert.AreEqual(18, result.Steps[0].Transition.Score.Total); Assert.AreEqual(10, Hits(result).Length);
            Assert.IsTrue(Hits(result).GroupBy(h => h.Effect.Target).All(g => g.Sum(h => h.SettledAmount) == 18));
            Assert.AreEqual(5, Poison(s)); Assert.AreEqual(5, Poison(s, "enemy-1"));
        }
        [Test] public void ShortStraightFlushStillScoresFlushAndHighCardOnlyMatchesPlainHighCard()
        {
            var s = Session(new[] { 2, 3, 4 }, true, edit: d => Gear(d, "<RuleValue id='short' key='MinStraightCards' delta='-2'/>"));
            var facts = s.Engine.PreviewHandFacts(s.State, s.State.Duel.PlayerId, Cards(s));
            Assert.AreEqual(HandCategory.Flush, facts.Pattern.Category); Assert.IsFalse(facts.MatchesCategory(HandCategory.StraightFlush));
            var r = Act(s, CardDuelAction.Play, Cards(s)); Assert.AreEqual(36, Hits(r).Sum(h => h.SettledAmount)); Assert.AreEqual(3, Poison(s));
            var high = Session(new[] { 2, 7, 9 }); var highFacts = high.Engine.PreviewHandFacts(high.State, high.State.Duel.PlayerId, Cards(high));
            CollectionAssert.AreEqual(new[] { HandCategory.HighCard }, highFacts.CompatibleCategories);
        }
        [TestCase(new[] { 2, 3, 4, 5, 6 }, HandCategory.StraightFlush)]
        [TestCase(new[] { 7, 7, 7, 8, 8 }, HandCategory.FullHouse)]
        public void FiveCardFlushMechanismIsNotHiddenByHigherPattern(int[] ranks, HandCategory expected)
        {
            var s = Session(ranks, true, edit: d => d.Root.Element("Players").Elements().First().SetAttributeValue("minFlushCards", 5));
            var facts = s.Engine.PreviewHandFacts(s.State, s.State.Duel.PlayerId, Cards(s));
            Assert.AreEqual(expected, facts.Pattern.Category); Assert.IsTrue(facts.MatchesCategory(HandCategory.Flush));
            var r = Act(s, CardDuelAction.Play, Cards(s));
            Assert.AreEqual(1, r.Steps[0].Transition.Events.Count(e => e.Effect.Kind == CombatEffectKind.Heal));
        }
        [Test] public void StructuralAliasesMatchLowerShapeIdsAndExtraHitsOnlyApplyToFirstCardStep()
        {
            var s = Session(new[] { 2, 3, 4, 5, 6 }, true, edit: d =>
            {
                Hand(d, "Straight").Elements("Effect").First().SetAttributeValue("id", "poison-strike");
                Hand(d, "StraightFlush", "Ordinary").Elements("Effect").First().SetAttributeValue("id", "sf-strike");
                Gear(d, "<HandModifier id='more' category='Straight' operation='AddHits' stepId='poison-strike' hits='2'/>");
            });
            var r = Act(s, CardDuelAction.Play, Cards(s)); Assert.AreEqual(7, Hits(r).Length); Assert.AreEqual(7, Poison(s));
            Assert.AreEqual(160 + Hits(r)[0].SettledAmount * 2, Hits(r).Sum(h => h.SettledAmount));
        }
        [Test] public void NumericAndFactConditionsMatchCompatibleShapesWhileScoreRemainsHighest()
        {
            var s = Session(new[] { 2, 3, 4, 5, 6 }, true, edit: d => Gear(d,
                "<Score hand='Straight' operation='MultiplyMultiplier' amount='10'/>" +
                "<Numeric id='healing' stat='HealingDone' operation='AddRate' value='0.5' origins='Primary' hand='Flush'/>" +
                "<FactTrigger id='poison' timing='DamageSettled' relation='OwnerIsSource' occurrence='PerEvent' origins='Primary'>" +
                "<Conditions><HandIs category='Straight'/></Conditions><Effect kind='ApplyStatus' statusRef='bleed' target='EventTarget' stacks='1'/></FactTrigger>"));
            var r = Act(s, CardDuelAction.Play, Cards(s)); Assert.AreEqual(160, r.Steps[0].Transition.Score.Total);
            Assert.AreEqual(10, Poison(s)); Assert.AreEqual(24, r.Steps[0].Transition.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).SettledAmount);
        }
        [TestCase("ReplaceEffect", "<Effect kind='Damage' amount='10'/>", "<Effect kind='Damage' amount='11'/>")]
        [TestCase("ChangeTarget", "", "")]
        public void ConflictingCrossCategoryStructuralEditsRejectEntireAction(string operation, string first, string second)
        {
            var s = Session(new[] { 2, 3, 4, 5, 6 }, true, edit: d => Gear(d,
                "<HandModifier id='a' category='Straight' operation='" + operation + "' stepId='damage/1'" +
                (operation == "ChangeTarget" ? " target='Opponents' selection='LowestHealth'" : "") + ">" + first + "</HandModifier>" +
                "<HandModifier id='b' category='StraightFlush' operation='" + operation + "' stepId='damage/1'" +
                (operation == "ChangeTarget" ? " target='Opponents' selection='HighestHealth'" : "") + ">" + second + "</HandModifier>"));
            var before = s.State; var result = s.Execute(before.Revision, before.Duel.PlayerId, CardDuelAction.Play, Cards(s));
            Assert.IsFalse(result.Accepted); Assert.AreSame(before, s.State);
        }
        [Test] public void ConflictingOnHitStatusesRejectEntireAction()
        {
            var s = Session(new[] { 2, 3, 4, 5, 6 }, true, edit: d => Hand(d, "Flush").Elements("Effect").First().Add(
                new XElement("OnHitStatus", new XAttribute("statusRef", "stun"))));
            var before = s.State; var result = s.Execute(before.Revision, before.Duel.PlayerId, CardDuelAction.Play, Cards(s));
            Assert.IsFalse(result.Accepted); Assert.AreSame(before, s.State);
        }
        [Test] public void FractionalPerCardDamageKeepsExactlyOneHighestTotalIncludingSharedPoints()
        {
            var s = Session(new[] { 2, 3, 4, 5, 6 }, true, edit: d =>
            {
                var rule = d.Root.Element("ScoringProfiles").Descendants("Hand").Single(h => (string)h.Attribute("category") == "StraightFlush");
                rule.SetAttributeValue("multiplier", "1.17"); rule.SetAttributeValue("points", 3);
            });
            var r = Act(s, CardDuelAction.Play, Cards(s)); Assert.AreEqual(5, Hits(r).Length);
            Assert.AreEqual(r.Steps[0].Transition.Score.Total, Hits(r).Sum(h => h.SettledAmount));
        }
        [Test] public void RoyalFlushPreservesExclusiveEliminationWithoutFlushOrStraightMechanisms()
        {
            var s = Session(new[] { 10, 11, 12, 13, 14 }, true, 2);
            var facts = s.Engine.PreviewHandFacts(s.State, s.State.Duel.PlayerId, Cards(s));
            CollectionAssert.AreEqual(new[] { HandCategory.StraightFlush }, facts.CompatibleCategories);
            var r = Act(s, CardDuelAction.Play, Cards(s));
            Assert.AreEqual(ScoredDuelPhase.Won, s.State.Duel.Phase); Assert.IsEmpty(Hits(r));
            Assert.AreEqual(2, r.Steps[0].Transition.Events.Count(e => e.Effect.Kind == CombatEffectKind.Eliminate));
            Assert.IsFalse(r.Steps[0].Transition.Events.Any(e => e.Effect.Kind == CombatEffectKind.Heal));
        }
        [Test] public void CombinedModeRejectsLegacyModeAndMultipleBaseDamageBranches()
        {
            var mixed = Document(new[] { 2, 3, 4, 5, 6 }, true);
            mixed.Root.Element("HandRules").Elements().First().SetAttributeValue("flushUsesRankEffects", true);
            Assert.IsFalse(BattleContentCompiler.CompileXml(mixed.ToString()).Success);
            var duplicate = Document(new[] { 2, 3, 4, 5, 6 }, true);
            Hand(duplicate, "FullHouse").Add(new XElement("Effect", new XAttribute("id", "second"), new XAttribute("kind", "Damage"), new XAttribute("amount", 1)));
            Assert.IsFalse(BattleContentCompiler.CompileXml(duplicate.ToString()).Success);
        }
    }
}
