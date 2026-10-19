using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleTemplateContentTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), Support = new ParticipantId("support");
        private static BattleSession Start(string file, string encounter)
        {
            string path = "Engine/Content/Templates/" + file + ".xml";
            var build = BattleContentCompiler.CompileXml(ToolkitTestFiles.Read(path), path);
            Assert.IsTrue(build.Success, string.Join("\n", build.Diagnostics.Select(x => x.Message)));
            Assert.IsEmpty(build.Diagnostics);
            byte[] seed;
            using (var sha = SHA256.Create()) seed = sha.ComputeHash(Encoding.UTF8.GetBytes("template-demo"));
            var assembly = BattleContentFactory.Create(build.Content, new BattleSetupRequest(encounter, "template-test", HmacDeckShuffler.FromSeed(seed)));
            Assert.IsTrue(assembly.Success, string.Join("\n", assembly.Diagnostics.Select(x => x.Message)));
            Act(assembly.Session, Player, CardDuelAction.Start);
            return assembly.Session;
        }
        private static BattleSessionResult Act(BattleSession session, ParticipantId actor, CardDuelAction action, IEnumerable<CardInstanceId> cards = null)
        {
            var result = session.Execute(session.State.Revision, actor, action, cards);
            Assert.IsTrue(result.Accepted, action + ": " + result.Error);
            return result;
        }
        private static IEnumerable<CardInstanceId[]> Choose(CardInstanceId[] cards, int count, int start = 0)
        {
            if (count == 0) { yield return Array.Empty<CardInstanceId>(); yield break; }
            for (int i = start; i <= cards.Length - count; i++)
                foreach (var tail in Choose(cards, count - 1, i + 1))
                    yield return new[] { cards[i] }.Concat(tail).ToArray();
        }
        private static CardInstanceId[] Hand(BattleSession session, ParticipantId actor, HandCategory category, int count)
        {
            foreach (var cards in Choose(session.State.Zone(CardZoneKinds.Hand, actor).Cards.ToArray(), count))
            {
                var preview = session.Engine.PreviewHand(session.State, actor, cards);
                if (preview.Accepted && preview.Score.Category == category) return cards;
            }
            Assert.Fail("The documented template-demo seed should offer " + category + " to " + actor);
            return null;
        }
        private static long Health(BattleSession session, string id) => session.State.Duel.Combat.Get(new ParticipantId(id)).Health;

        [TestCase("01_VenomStraight", "venom-straight", 1, 1)]
        [TestCase("02_ThornBastion", "thorn-bastion", 1, 2)]
        [TestCase("03_CrimsonChalice", "crimson-chalice", 1, 2)]
        [TestCase("03_CrimsonChalice", "crimson-recovery", 1, 2)]
        [TestCase("04_PlagueChapel", "plague-chapel", 1, 2)]
        [TestCase("05_TwinVanguard", "twin-vanguard", 2, 2)]
        [TestCase("06_RelicOrder", "flat-then-rate", 1, 1)]
        [TestCase("06_RelicOrder", "rate-then-flat", 1, 1)]
        public void RealTemplateStartsPreviewsAndCanReachAnOutcome(string file, string encounter, int players, int enemies)
        {
            var session = Start(file, encounter);
            Assert.AreEqual(players, session.State.Duel.Players.Count);
            Assert.AreEqual(enemies, session.State.Duel.Enemies.Count);
            var before = session.State;
            var preview = session.Engine.PreviewPlay(before, before.Revision, Player, before.Zone(CardZoneKinds.Hand, Player).Cards.Take(1));
            Assert.IsTrue(preview.Accepted); Assert.AreSame(before, session.State);
            // Inaction must advance rounds to a loss, never deadlock the multi-player roster or trigger queues.
            for (int i = 0; i < 64 && !session.State.Duel.IsFinished; i++)
            {
                var actor = session.State.Duel.Players.FirstOrDefault(x => session.State.Duel.CanAct(x.Id));
                Assert.IsNotNull(actor);
                Act(session, actor.Id, CardDuelAction.EndTurn);
            }
            Assert.IsTrue(session.State.Duel.IsFinished);
            Assert.AreEqual(ScoredDuelPhase.Lost, session.State.Duel.Phase);
        }

        [Test] public void VenomExpandsToFourHitsAndEachHitAddsBothIndependentDots()
        {
            var session = Start("01_VenomStraight", "venom-straight");
            var result = Act(session, Player, CardDuelAction.Play, Hand(session, Player, HandCategory.Straight, 5));
            Assert.AreEqual(4, result.Steps[0].Transition.EffectPlan.Effects.Count);
            Assert.AreEqual(4, session.State.Duel.Combat.Statuses.Count(x => x.DefinitionId.Value == "bleed"));
            Assert.AreEqual(4, session.State.Duel.Combat.Statuses.Count(x => x.DefinitionId.Value == "poison"));
            long before = Health(session, "enemy-1");
            Act(session, Player, CardDuelAction.EndTurn);
            Assert.AreEqual(before - 44, Health(session, "enemy-1"));
            Assert.IsTrue(session.State.Duel.Combat.Statuses.All(x => x.Duration.RemainingTriggers == 2));
        }

        [Test] public void DiscardThreeGainsFiftyBlockAndReflectsOnlyTheFiftyActuallyAbsorbed()
        {
            var session = Start("02_ThornBastion", "thorn-bastion");
            Act(session, Player, CardDuelAction.Discard, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(3));
            Assert.AreEqual(50, session.State.Duel.Combat.Get(Player).Block);
            var result = Act(session, Player, CardDuelAction.EndTurn);
            Assert.AreEqual(238, Health(session, "player"));
            Assert.AreEqual(146, Health(session, "enemy-1")); Assert.AreEqual(104, Health(session, "enemy-2"));
            Assert.AreEqual(50, result.Steps.SelectMany(x => x.Transition.Events)
                .Where(x => x.Provenance.Origin == CombatEffectOrigin.Reflection).Sum(x => x.DamageToHealth));
        }

        [TestCase("crimson-chalice", 240)] [TestCase("crimson-recovery", 100)]
        public void ChaliceHealsFirstAndOnlyOverflowDamagesEveryEnemy(string encounter, int initialHealth)
        {
            var session = Start("03_CrimsonChalice", encounter);
            var result = Act(session, Player, CardDuelAction.Play, Hand(session, Player, HandCategory.Flush, 5));
            var step = result.Steps[0].Transition;
            long healing = (long)decimal.Floor(step.Score.Points * 1.5m);
            long overflow = Math.Max(0, initialHealth + healing - 240);
            Assert.AreEqual(Math.Min(240, initialHealth + healing), Health(session, "player"));
            Assert.AreEqual(360 - (long)decimal.Floor(step.Score.Total * 0.5m) - overflow, Health(session, "enemy-1"));
            Assert.AreEqual(160 - overflow, Health(session, "enemy-2"));
        }

        [Test] public void ChapelHighCardCleansesAndDiscardAntidoteStopsAfterTwoUses()
        {
            var session = Start("04_PlagueChapel", "plague-chapel");
            Act(session, Player, CardDuelAction.EndTurn);
            Assert.IsTrue(session.State.Duel.Combat.Statuses.Any(x => x.DefinitionId.Value == "poison"));
            Act(session, Player, CardDuelAction.Play, Hand(session, Player, HandCategory.HighCard, 1));
            Assert.IsEmpty(session.State.Duel.Combat.Statuses);
            for (int i = 0; i < 3; i++)
            {
                Act(session, Player, CardDuelAction.EndTurn);
                Act(session, Player, CardDuelAction.Discard, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(2));
                Assert.AreEqual(Math.Max(0, 1 - i), session.State.Duel.Player(Player).Relics.Relics.Single().RemainingCharges);
                Assert.AreEqual(i == 2, session.State.Duel.Combat.Statuses.Any(x => x.DefinitionId.Value == "poison"));
            }
        }

        [Test] public void SupportHealsAndCleansesBothPlayersAndEnemiesWaitForBothToFinish()
        {
            var session = Start("05_TwinVanguard", "twin-vanguard");
            Act(session, Support, CardDuelAction.Play, Hand(session, Support, HandCategory.OnePair, 2));
            Assert.AreEqual(185, Health(session, "player")); Assert.AreEqual(145, Health(session, "support"));
            Act(session, Player, CardDuelAction.EndTurn);
            Assert.AreEqual(185, Health(session, "player")); Assert.AreEqual(0, session.State.Duel.CompletedRounds);
            Act(session, Support, CardDuelAction.EndTurn);
            Assert.AreEqual(1, session.State.Duel.CompletedRounds);
            Assert.AreEqual(2, session.State.Duel.Combat.Statuses.Count(x => x.DefinitionId.Value == "poison"));
            Act(session, Support, CardDuelAction.Play, Hand(session, Support, HandCategory.TwoPair, 4));
            Assert.IsEmpty(session.State.Duel.Combat.Statuses);
            Assert.AreEqual(10, session.State.Duel.Combat.Get(Player).Block);
            Assert.AreEqual(10, session.State.Duel.Combat.Get(Support).Block);
        }

        [TestCase("flat-then-rate", 180)] [TestCase("rate-then-flat", 170)]
        public void OrderExperimentReallyChangesSettledDamage(string encounter, int expectedDamage)
        {
            var session = Start("06_RelicOrder", encounter);
            Act(session, Player, CardDuelAction.Play, Hand(session, Player, HandCategory.HighCard, 1));
            Assert.AreEqual(1000 - expectedDamage, Health(session, "enemy-1"));
        }
    }
}
