using System;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class SingleChapterScoringTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), Enemy = new ParticipantId("enemy");

        private static BattleSession Session(int[] ranks, bool flush = false, string relic = null, int level = 1)
        {
            var xml = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/SingleChapterBattle.xml"));
            if (level != 1)
            {
                // Levels are scenario input. The shipped scoring profiles and hand skills remain intact.
                var loadout = xml.Root.Element("Loadouts").Elements().Single();
                foreach (HandCategory category in Enum.GetValues(typeof(HandCategory)))
                    loadout.Add(new XElement("HandLevel", new XAttribute("category", category), new XAttribute("level", level)));
            }
            var content = BattleContentCompiler.CompileXml(xml.ToString());
            Assert.IsTrue(content.Success, string.Join("\n", content.Diagnostics.Select(d => d.Message)));
            var built = BattleContentFactory.Create(content.Content, new BattleSetupRequest("single-chapter-battle-1",
                "single-chapter-scoring", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(built.Success, string.Join("\n", built.Diagnostics.Select(d => d.Message)));
            var session = built.Session;
            var start = session.Execute(0, Player, CardDuelAction.Start);
            Assert.IsTrue(start.Accepted, start.Error.ToString());
            // Provide healing headroom without changing the shipped player template or enemy rules.
            var setup = session.ApplyAdmin(session.State.Revision, new BattleAdminPatch(
                vitals: new[] { new BattleAdminVitals(Enemy, 100000, 100000, 0), new BattleAdminVitals(Player, 600, 800, 0) },
                zones: new[] { new BattleAdminZone(Player, CardZoneKinds.Hand, ranks.Select((rank, index) =>
                    new BattleAdminCard(definitionId: "standard:" + (flush ? 1 : index % 4 + 1) + ":" + rank))) },
                relics: new[] { new BattleAdminRelics(Player, relic == null ? Array.Empty<BattleAdminRelic>() :
                    new[] { new BattleAdminRelic(relic) }) }));
            Assert.IsTrue(setup.Accepted, setup.Error.ToString());
            return session;
        }

        private static CombatEvent[] Hits(CardDuelTransition transition) => transition.Events.Where(e =>
            e.Effect.Kind == CombatEffectKind.Damage && e.Effect.Source == Player && e.Effect.Target == Enemy).ToArray();

        private static CardDuelTransition Play(BattleSession session, long expectedDamage, params long[] expectedHits)
        {
            var before = session.State;
            var cards = before.Zone(CardZoneKinds.Hand, Player).Cards.ToArray();
            var category = session.Engine.PreviewHand(before, Player, cards).Pattern.Category;
            // Automatic area attacks do not expose a requested target slot.
            var targets = category == HandCategory.ThreeOfAKind || category == HandCategory.FourOfAKind ||
                category == HandCategory.FiveOfAKind ? null : new[] { Enemy };
            var preview = session.Engine.PreviewPlay(before, before.Revision, Player, cards, targets);
            Assert.IsTrue(preview.Accepted, preview.Error.ToString());
            Assert.AreSame(before, session.State, "Preview must not spend cards or change combat state.");
            var played = session.Execute(before.Revision, Player, CardDuelAction.Play, cards, targets);
            Assert.IsTrue(played.Accepted, played.Error.ToString());
            var transition = played.Steps[0].Transition;
            var amounts = expectedHits.Length == 0 ? new[] { expectedDamage } : expectedHits;
            CollectionAssert.AreEqual(amounts, Hits(preview).Select(e => e.SettledDamage), "Previewed hit amounts");
            CollectionAssert.AreEqual(amounts, Hits(transition).Select(e => e.SettledDamage), "Committed hit amounts");
            Assert.AreEqual(expectedDamage, before.Duel.Combat.Get(Enemy).Health - session.State.Duel.Combat.Get(Enemy).Health,
                "The configured hand must deduct the expected health, without a hidden hand-point bonus.");
            Assert.AreEqual(preview.State.Duel.Combat.Get(Enemy).Health, session.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(preview.State.Duel.Combat.Get(Player).Health, session.State.Duel.Combat.Get(Player).Health);
            return transition;
        }

        // Restoring any active category's base points, or counting its kickers, changes these real hits.
        [TestCase(new[] { 2, 5, 9 }, HandCategory.HighCard, 9)]
        [TestCase(new[] { 14 }, HandCategory.HighCard, 1)]
        [TestCase(new[] { 14, 14, 13, 11, 9 }, HandCategory.OnePair, 4)]
        [TestCase(new[] { 2, 2, 13, 11, 9 }, HandCategory.OnePair, 8)]
        [TestCase(new[] { 2, 2, 3, 3, 13 }, HandCategory.TwoPair, 20)]
        [TestCase(new[] { 2, 2, 2, 13, 12 }, HandCategory.ThreeOfAKind, 18)]
        [TestCase(new[] { 2, 2, 2, 2, 13 }, HandCategory.FourOfAKind, 48)]
        [TestCase(new[] { 2, 2, 2, 2, 2 }, HandCategory.FiveOfAKind, 120)]
        public void HandDamageUsesOnlyItsScoringCardsWithoutExtraBasePoints(int[] ranks, HandCategory category, long damage)
        {
            var session = Session(ranks);
            var result = Play(session, damage);
            Assert.AreEqual(category, result.Score.Category);
            Assert.IsFalse(result.Events.Any(e => e.Effect.Kind == CombatEffectKind.Heal));
        }

        // Restoring pointsPerLevel must fail, while the existing multiplier growth still contributes.
        [TestCase(new[] { 9 }, HandCategory.HighCard, 27)]
        [TestCase(new[] { 14 }, HandCategory.HighCard, 3)]
        [TestCase(new[] { 2, 2, 13 }, HandCategory.OnePair, 16)]
        [TestCase(new[] { 2, 2, 3, 3, 13 }, HandCategory.TwoPair, 40)]
        [TestCase(new[] { 2, 2, 2, 13, 12 }, HandCategory.ThreeOfAKind, 30)]
        [TestCase(new[] { 2, 2, 2, 2, 13 }, HandCategory.FourOfAKind, 80)]
        [TestCase(new[] { 2, 2, 2, 2, 2 }, HandCategory.FiveOfAKind, 180)]
        public void LevelThreeKeepsMultiplierGrowthWithoutAddingHandPoints(int[] ranks, HandCategory category, long damage)
        {
            var result = Play(Session(ranks, level: 3), damage);
            Assert.AreEqual(category, result.Score.Category);
        }

        [TestCase(false, null, 1, 60, new long[] { 6, 9, 12, 15, 18 }, 0)]
        [TestCase(false, null, 3, 100, new long[] { 10, 15, 20, 25, 30 }, 0)]
        [TestCase(false, "13003", 1, 100, new long[] { 10, 15, 20, 25, 30 }, 0)]
        [TestCase(true, null, 1, 120, new long[] { 12, 18, 24, 30, 36 }, 10)]
        [TestCase(true, "10001", 1, 200, new long[] { 20, 30, 40, 50, 60 }, 10)]
        [TestCase(true, "13003", 1, 200, new long[] { 20, 30, 40, 50, 60 }, 10)]
        [TestCase(true, "11002", 1, 120, new long[] { 12, 18, 24, 30, 36 }, 16)]
        public void StraightHitsAscendingWithoutSharedBasePointsAndKeepsPoison(bool flush, string relic, int level,
            long damage, long[] hits, long healing)
        {
            var session = Session(new[] { 6, 2, 5, 3, 4 }, flush, relic, level);
            var result = Play(session, damage, hits);
            Assert.AreEqual(HandCategory.Straight, result.Score.Category);
            Assert.AreEqual(10, session.State.Duel.Combat.Statuses.Single(s => s.DefinitionId.Value == "bleed").Stacks,
                "All five hits still apply the shipped two poison stacks.");
            if (flush)
            {
                Assert.AreEqual(healing, result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).HealthRestored);
                Assert.AreEqual(600 + healing, session.State.Duel.Combat.Get(Player).Health);
            }
            else Assert.IsFalse(result.Events.Any(e => e.Effect.Kind == CombatEffectKind.Heal));
        }

        [TestCase(new[] { 5, 3, 14, 4, 2 }, 45, new long[] { 3, 6, 9, 12, 15 })]
        [TestCase(new[] { 14, 13, 10, 12, 11 }, 180, new long[] { 30, 33, 36, 39, 42 })]
        public void StraightAceUsesOneOrFourteenInEachActualHit(int[] ranks, long damage, long[] hits)
        {
            Play(Session(ranks), damage, hits);
        }

        [TestCase(null, 1, 26)]
        [TestCase("10001", 1, 78)]
        [TestCase("13003", 1, 78)]
        [TestCase(null, 3, 78)]
        public void FlushUsesHighestCardTimesTwoAndHealsFiftyPercentIndependently(string relic, int level, long damage)
        {
            var session = Session(new[] { 2, 5, 7, 9, 13 }, true, relic, level);
            var result = Play(session, damage);
            Assert.AreEqual(HandCategory.HighCard, result.Score.Category);
            Assert.AreEqual(18, result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).HealthRestored,
                "The five cards total 36; floor(36 × 0.5) stays 18 regardless of score multiplier or level.");
            Assert.AreEqual(618, session.State.Duel.Combat.Get(Player).Health);
        }

        [TestCase(null, 17)]
        [TestCase("11002", 28)]
        public void FlushHealingCombinesHeartBonusBeforeRounding(string relic, long healing)
        {
            var session = Session(new[] { 13, 9, 7, 4, 2 }, true, relic);
            var result = Play(session, 26);
            Assert.AreEqual(healing, result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).HealthRestored,
                "35 points heal 17 at 50%, or 28 at 50% + 30%; round only after combining the rates.");
            Assert.AreEqual(600 + healing, session.State.Duel.Combat.Get(Player).Health);
        }

        [Test]
        public void PairMultiplierPlusTwoRelicStillAddsToTheUnchangedPairMultiplier()
        {
            Play(Session(new[] { 2, 2, 13 }, relic: "13003"), 16);
        }

        [TestCase(1, null, 52, new long[] { 40, 12 })]
        [TestCase(3, null, 91, new long[] { 67, 24 })]
        [TestCase(1, "13003", 91, new long[] { 67, 24 })]
        public void FullHouseScalesTripleAndPairSeparatelyAndKeepsItsControlSkill(int level, string relic,
            long damage, long[] hits)
        {
            var session = Session(new[] { 3, 3, 3, 2, 2 }, relic: relic, level: level);
            var result = Play(session, damage, hits);
            Assert.AreEqual(HandCategory.FullHouse, result.Score.Category);
            Assert.IsTrue(session.State.Duel.Combat.Statuses.Any(s => s.DefinitionId.Value == "stun" && s.Target.Id == Enemy.Value));
            Assert.IsTrue(session.State.Duel.Combat.Statuses.Any(s => s.DefinitionId.Value == "control-immunity" && s.Target.Id == Player.Value));
        }

        [TestCase(1, null, 480, 30)]
        [TestCase(3, null, 960, 30)]
        [TestCase(1, "11002", 480, 48)]
        public void RoyalFlushRetainsEliminationAndIndependentHealing(int level, string relic, long score, long healing)
        {
            var session = Session(new[] { 10, 11, 12, 13, 14 }, true, relic, level);
            var cards = session.State.Zone(CardZoneKinds.Hand, Player).Cards.ToArray();
            var before = session.State;
            var preview = session.Engine.PreviewPlay(before, before.Revision, Player, cards);
            Assert.IsTrue(preview.Accepted, preview.Error.ToString());
            Assert.AreSame(before, session.State);
            var played = session.Execute(before.Revision, Player, CardDuelAction.Play, cards);
            Assert.IsTrue(played.Accepted, played.Error.ToString());
            var result = played.Steps[0].Transition;
            Assert.AreEqual(HandCategory.StraightFlush, result.Score.Category);
            Assert.AreEqual(score, result.Score.Total, "Royal scoring keeps its multiplier growth without hand points.");
            Assert.IsEmpty(Hits(result));
            Assert.AreEqual(1, result.Events.Count(e => e.Effect.Kind == CombatEffectKind.Eliminate));
            Assert.AreEqual(0, session.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(healing, result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).HealthRestored);
            Assert.AreEqual(600 + healing, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(ScoredDuelPhase.Won, session.State.Duel.Phase);
            Assert.AreEqual(preview.State.Duel.Combat.Get(Enemy).Health, session.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(preview.State.Duel.Combat.Get(Player).Health, session.State.Duel.Combat.Get(Player).Health);
        }

        private static int ShellLayers(CardDuelState state) => state.Duel.Combat.Statuses
            .Where(s => s.DefinitionId.Value == "wrath-shell" && s.Target.Id == Enemy.Value).Sum(s => s.Stacks);

        private static BattleSession WrathAfterGuard()
        {
            var content = BattleContentCompiler.CompileXml(ToolkitTestFiles.Read("Engine/Content/SingleChapterBattle.xml"));
            Assert.IsTrue(content.Success, string.Join("\n", content.Diagnostics.Select(d => d.Message)));
            var built = BattleContentFactory.Create(content.Content, new BattleSetupRequest("single-chapter-wrath",
                "single-chapter-scoring-shell", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(built.Success, string.Join("\n", built.Diagnostics.Select(d => d.Message)));
            var session = built.Session;
            Assert.IsTrue(session.Execute(0, Player, CardDuelAction.Start).Accepted);
            for (int round = 0; round < 3; round++)
            {
                var ended = session.Execute(session.State.Revision, Player, CardDuelAction.EndTurn);
                Assert.IsTrue(ended.Accepted, ended.Error.ToString());
                if (session.State.FlowFor(Player).Window == BattlePlayerWindow.Ending)
                    Assert.IsTrue(session.Execute(session.State.Revision, Player, CardDuelAction.FinishTurn).Accepted);
            }
            Assert.AreEqual(2, ShellLayers(session.State), "The shipped guard must actually execute before immunity is tested.");
            return session;
        }

        private static CardDuelTransition PlayAtWrath(BattleSession session, int[] ranks, params long[] damage)
        {
            var patched = session.ApplyAdmin(session.State.Revision, new BattleAdminPatch(zones: new[] {
                new BattleAdminZone(Player, CardZoneKinds.Hand, ranks.Select((rank, index) =>
                    new BattleAdminCard(definitionId: "standard:" + (index % 4 + 1) + ":" + rank))) }));
            Assert.IsTrue(patched.Accepted, patched.Error.ToString());
            var before = session.State;
            var cards = before.Zone(CardZoneKinds.Hand, Player).Cards.ToArray();
            var preview = session.Engine.PreviewPlay(before, before.Revision, Player, cards, new[] { Enemy });
            Assert.IsTrue(preview.Accepted, preview.Error.ToString());
            Assert.AreSame(before, session.State, "Preview must not consume live shell layers.");
            var played = session.Execute(before.Revision, Player, CardDuelAction.Play, cards, new[] { Enemy });
            Assert.IsTrue(played.Accepted, played.Error.ToString());
            var attack = played.Steps[0].Transition;
            CollectionAssert.AreEqual(damage, Hits(preview).Select(e => e.SettledDamage));
            CollectionAssert.AreEqual(damage, Hits(attack).Select(e => e.SettledDamage));
            // The third play can automatically end the turn and let blood hands heal the body.
            // Inspect the committed attack state before that later enemy phase.
            Assert.AreEqual(damage.Sum(), before.Duel.Combat.Get(Enemy).Health - attack.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(ShellLayers(preview.State), ShellLayers(attack.State));
            return attack;
        }

        [Test]
        public void SevenStationWrathBlocksTwoOrdinaryHitsThenAllowsTheThird()
        {
            var session = WrathAfterGuard();
            var first = PlayAtWrath(session, new[] { 9 }, 0);
            Assert.AreEqual(1, ShellLayers(first.State));
            var second = PlayAtWrath(session, new[] { 9 }, 0);
            Assert.AreEqual(0, ShellLayers(second.State));
            var third = PlayAtWrath(session, new[] { 9 }, 9);
            Assert.AreEqual(0, ShellLayers(third.State));
        }

        [Test]
        public void SevenStationStraightConsumesTwoShellLayersThenDealsItsRemainingThreeHits()
        {
            var session = WrathAfterGuard();
            var attack = PlayAtWrath(session, new[] { 6, 2, 5, 3, 4 }, 0, 0, 12, 15, 18);
            Assert.AreEqual(0, ShellLayers(attack.State));
            Assert.AreEqual(1455, attack.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(10, attack.State.Duel.Combat.Statuses.Single(s =>
                s.DefinitionId.Value == "bleed" && s.Target.Id == Enemy.Value).Stacks);
        }

        [Test]
        public void SevenStationFullHouseConsumesBothShellLayersAndStillControlsAndSeversHands()
        {
            var session = WrathAfterGuard();
            var attack = PlayAtWrath(session, new[] { 3, 3, 3, 2, 2 }, 0, 0);
            Assert.AreEqual(0, ShellLayers(attack.State));
            Assert.IsTrue(attack.State.Duel.IsActionBlocked(Enemy));
            Assert.IsTrue(WrathBossQuery.Read(attack.State.Duel).Hands.All(h => !h.IsAlive && h.ReviveRemaining == 8));
        }
    }
}
