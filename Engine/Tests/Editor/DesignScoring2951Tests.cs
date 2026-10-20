using System;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class DesignScoring2951Tests
    {
        private static readonly ParticipantId Player = new ParticipantId("player");
        private static readonly ParticipantId Enemy = new ParticipantId("enemy");

        private static BattleSession Session(int[] ranks, bool flush = false, string[] relics = null,
            int enemies = 1, Action<XDocument> edit = null)
        {
            var xml = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle-2026-10-21.xml"));
            xml.Root.Element("Decks").Elements().First().ReplaceNodes(ranks.Select((rank, index) =>
                new XElement("Card", new XAttribute("id", "c" + index),
                    new XAttribute("face", "standard:" + (flush ? 1 : index % 4 + 1) + ":" + rank))));
            var enemy = xml.Root.Element("Enemies").Elements().First();
            enemy.RemoveNodes(); enemy.SetAttributeValue("maxHealth", 100000); enemy.SetAttributeValue("damage", 0);
            var encounter = xml.Root.Element("Encounters").Elements().First();
            for (int index = 1; index < enemies; index++)
                encounter.Add(new XElement("Enemy", new XAttribute("id", "enemy-" + index), new XAttribute("ref", "rotator")));
            var loadout = xml.Root.Element("Loadouts").Elements().First();
            foreach (string relic in relics ?? Array.Empty<string>())
                loadout.Add(new XElement("Relic", new XAttribute("ref", relic), new XAttribute("instanceId", relic)));
            xml.Root.Element("Relics").Add(XElement.Parse(
                "<Relic id='test-injury' version='test'><BoundaryTrigger id='injure' timing='BattleStarted'>" +
                "<Effect kind='Damage' amount='100' target='Self'/></BoundaryTrigger></Relic>"));
            loadout.Add(XElement.Parse("<Relic ref='test-injury' instanceId='test-injury'/>"));
            edit?.Invoke(xml);
            var compiled = BattleContentCompiler.CompileXml(xml.ToString());
            Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics.Select(d => d.Message)));
            var built = BattleContentFactory.Create(compiled.Content,
                new BattleSetupRequest("standard-single-battle", "scoring2951", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(built.Success, string.Join("\n", built.Diagnostics.Select(d => d.Message)));
            var start = built.Session.Execute(0, Player, CardDuelAction.Start);
            Assert.IsTrue(start.Accepted, start.Error.ToString());
            return built.Session;
        }

        private static CardInstanceId[] Cards(BattleSession session) => session.State.Zone(CardZoneKinds.Hand, Player).Cards.ToArray();
        private static CardDuelTransition Play(BattleSession session, bool chooseTarget = false)
        {
            var result = session.Execute(session.State.Revision, Player, CardDuelAction.Play, Cards(session),
                chooseTarget ? new[] { Enemy } : null);
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            return result.Steps[0].Transition;
        }
        private static long[] Hits(CardDuelTransition transition) => transition.Events
            .Where(e => e.Effect.Kind == CombatEffectKind.Damage && e.Effect.Source == Player && e.Effect.Target != Player)
            .Select(e => e.SettledDamage).ToArray();

        [TestCase(null, 80)]
        [TestCase("10001", 160)]
        [TestCase("13003", 280)]
        public void FlushStraightScoresRankThenFlushAndKeepsHealingIndependent(string relic, long expected)
        {
            var session = Session(new[] { 6, 2, 5, 3, 4 }, true, relic == null ? null : new[] { relic });
            var before = session.State;
            var preview = session.Engine.PreviewPlay(before, before.Revision, Player, Cards(session));
            Assert.IsTrue(preview.Accepted, preview.Error.ToString());
            Assert.AreSame(before, session.State);
            var played = Play(session);
            Assert.AreEqual(HandCategory.Straight, played.Score.Category);
            Assert.AreEqual(expected, Hits(played).Sum());
            Assert.AreEqual(4, played.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).SettledAmount);
            Assert.AreEqual(5, session.State.Duel.Combat.Statuses.Single(s => s.DefinitionId.Value == "bleed").Stacks);
            Assert.AreEqual(preview.State.Duel.Combat.Get(Enemy).Health, session.State.Duel.Combat.Get(Enemy).Health);
        }

        [Test]
        public void StraightAttacksAscendingAndHighAceScoresFourteen()
        {
            var session = Session(new[] { 14, 13, 10, 12, 11 });
            CollectionAssert.AreEqual(new long[] { 20, 22, 24, 26, 28 }, Hits(Play(session)));
            session = Session(new[] { 5, 3, 14, 4, 2 });
            CollectionAssert.AreEqual(new long[] { 2, 4, 6, 8, 10 }, Hits(Play(session)));
            session = Session(new[] { 14 });
            Assert.AreEqual(1, Play(session).Score.Points);
        }

        [Test]
        public void EachStraightHitRoundsDownWithoutRedistributingRemainders()
        {
            var session = Session(new[] { 6, 2, 4, 3, 5 }, edit: xml =>
                xml.Root.Element("ScoringProfiles").Descendants("Hand").Single(e => (string)e.Attribute("category") == "Straight")
                    .SetAttributeValue("multiplier", "2.5"));
            CollectionAssert.AreEqual(new long[] { 5, 7, 10, 12, 15 }, Hits(Play(session)));
        }

        [Test]
        public void FlushHealingCombinesRatiosBeforeRounding()
        {
            var session = Session(new[] { 14, 2, 3, 5, 8 }, true, new[] { "11002" });
            var result = Play(session);
            Assert.AreEqual(9, result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).SettledAmount);
            Assert.AreEqual(2, Hits(result).Single());
        }

        [Test]
        public void FlushDamageAndHealingCoefficientsRemainConfigurable()
        {
            var session = Session(new[] { 2, 3, 4, 5, 6 }, true, edit: xml => {
                var handRule = xml.Root.Element("HandRules").Elements().First();
                handRule.SetAttributeValue("flushDamageMultiplier", "3");
                handRule.Element("FlushBonus").Element("Effect").SetAttributeValue("amount", "0.3");
            });
            var result = Play(session);
            Assert.AreEqual(120, Hits(result).Sum());
            Assert.AreEqual(6, result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).SettledAmount);
        }

        [TestCase(3)]
        [TestCase(4)]
        public void ShortSameSuitHandHasNoFlushBonus(int count)
        {
            var session = Session(new[] { 2, 4, 6, 8 }.Take(count).ToArray(), true);
            var result = Play(session);
            Assert.IsFalse(result.Events.Any(e => e.Effect.Kind == CombatEffectKind.Heal));
            Assert.AreEqual(count == 3 ? 6 : 8, Hits(result).Single());
        }

        [TestCase(null, 36, 16)]
        [TestCase("13003", 126, 56)]
        public void FullHouseComponentsHaveIndependentRankMultipliersAndStunAll(string relic, long triple, long pair)
        {
            var session = Session(new[] { 3, 3, 3, 2, 2 }, relics: relic == null ? null : new[] { relic }, enemies: 2);
            var missingTarget = session.Execute(session.State.Revision, Player, CardDuelAction.Play, Cards(session));
            Assert.IsFalse(missingTarget.Accepted);
            var result = Play(session, true);
            CollectionAssert.AreEqual(new[] { triple, triple, pair }, Hits(result));
            CollectionAssert.AreEquivalent(new[] { "enemy", "enemy-1" }, session.State.Duel.Combat.Statuses
                .Where(s => s.DefinitionId.Value == "stun").Select(s => s.Target.Id));
        }

        [TestCase(false, 4)]
        [TestCase(true, 12)]
        public void LastEnemyDeathStillCompletesFlushHealing(bool royal, long healing)
        {
            var session = Session(royal ? new[] { 10, 11, 12, 13, 14 } : new[] { 2, 3, 4, 5, 6 }, true,
                edit: xml => xml.Root.Element("Enemies").Elements().First().SetAttributeValue("maxHealth", 1));
            var result = Play(session);
            Assert.AreEqual(ScoredDuelPhase.Won, session.State.Duel.Phase);
            Assert.AreEqual(700 + healing, session.State.Duel.Combat.Get(Player).Health);
            if (royal)
            {
                Assert.IsEmpty(Hits(result));
                Assert.AreEqual(1, result.Events.Count(e => e.Effect.Kind == CombatEffectKind.Eliminate));
            }
        }

        [Test]
        public void SortedStraightRetargetsLowestHealthAndKeepsTiesInFormationOrder()
        {
            var session = Session(new[] { 6, 5, 4, 3, 2 }, enemies: 3, edit: xml =>
                xml.Root.Element("Enemies").Elements().First().SetAttributeValue("maxHealth", 3));
            var result = Play(session, true);
            var hits = result.Events.Where(e => e.Effect.Kind == CombatEffectKind.Damage && e.Effect.Target != Player).ToArray();
            CollectionAssert.AreEqual(new[] { "enemy", "enemy-1", "enemy-2" }, hits.Select(e => e.Effect.Target.Value));
            CollectionAssert.AreEqual(new long[] { 4, 6, 8 }, hits.Select(e => e.SettledDamage));
            Assert.IsEmpty(session.State.Duel.Combat.Statuses.Where(s => s.DefinitionId.Value == "bleed"));
        }

        [Test]
        public void FinalScoreScaleKeepsComponentTracesValid()
        {
            var session = Session(new[] { 3, 3, 3, 2, 2 });
            var baseScore = session.Engine.PreviewHand(session.State, Player, Cards(session)).Score;
            var scaled = ScoreEngine.ScaleMultiplier(baseScore, 100m, "test/roulette");
            Assert.DoesNotThrow(() => ScoreEngine.Validate(scaled));
            Assert.AreEqual(baseScore.Total * 100, scaled.Total);
            CollectionAssert.AreEqual(baseScore.Components.Select(s => s.Total * 100), scaled.Components.Select(s => s.Total));
        }

        [TestCase(0)]
        [TestCase(10)]
        public void RemovingFinalScoreScaleRestoresComponentTracesWithoutDivision(int factor)
        {
            var session = Session(new[] { 3, 3, 3, 2, 2 });
            var original = session.Engine.PreviewHand(session.State, Player, Cards(session)).Score;
            var scaled = ScoreEngine.ScaleMultiplier(original, factor, "item/roulette");
            var restored = ScoreEngine.RemoveFinalMultiplier(scaled, "item/roulette");
            Assert.DoesNotThrow(() => ScoreEngine.Validate(restored));
            Assert.AreEqual(original.Total, restored.Total);
            Assert.AreEqual(original.Multiplier, restored.Multiplier);
            Assert.AreEqual(original.Steps.Count, restored.Steps.Count);
            CollectionAssert.AreEqual(original.Components.Select(s => s.Total), restored.Components.Select(s => s.Total));
            Assert.AreSame(scaled, ScoreEngine.RemoveFinalMultiplier(scaled, "another-source"));
            var later = ScoreEngine.ScaleMultiplier(scaled, 2m, "later-source");
            Assert.AreSame(later, ScoreEngine.RemoveFinalMultiplier(later, "item/roulette"));
            var fixedScore = ScoreEngine.Fixed("fixed", 7);
            var fixedRestored = ScoreEngine.RemoveFinalMultiplier(
                ScoreEngine.ScaleMultiplier(fixedScore, factor, "item/roulette"), "item/roulette");
            Assert.DoesNotThrow(() => ScoreEngine.Validate(fixedRestored));
            Assert.AreEqual(7, fixedRestored.Total);
            Assert.IsEmpty(fixedRestored.Steps);
        }
    }
}
