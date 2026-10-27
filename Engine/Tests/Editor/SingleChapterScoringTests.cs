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

        private static BattleSession Session(int[] ranks, bool flush = false, string relic = null, int level = 1, int seed = 0, Action<XDocument> configure = null)
        {
            var xml = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/SingleChapterBattle.xml"));
            configure?.Invoke(xml);
            if (level != 1)
            {
                // Levels are scenario input. The shipped scoring profiles and hand skills remain intact.
                var loadout = xml.Root.Element("Loadouts").Elements().Single();
                foreach (HandCategory category in Enum.GetValues(typeof(HandCategory)))
                    loadout.Add(new XElement("HandLevel", new XAttribute("category", category), new XAttribute("level", level)));
            }
            var content = BattleContentCompiler.CompileXml(xml.ToString());
            Assert.IsTrue(content.Success, string.Join("\n", content.Diagnostics.Select(d => d.Message)));
            var seedBytes = new byte[32]; seedBytes[0] = (byte)seed;
            var built = BattleContentFactory.Create(content.Content, new BattleSetupRequest("single-chapter-battle-1",
                "single-chapter-scoring", HmacDeckShuffler.FromSeed(seedBytes)));
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

        [Test] public void ShippedSingleChapterPlayImmediatelyRefillsHandToEight()
        {
            var session = Session(new[] { 2, 3, 4, 5, 6, 9, 11, 13 });
            var before = session.State;
            var cards = before.Zone(CardZoneKinds.Hand, Player).Cards.Take(5).ToArray();
            var result = session.Play(before.Revision, Player, cards, new[] { Enemy });
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            Assert.AreEqual(8, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Count);
            Assert.AreEqual(5, result.Steps[0].Transition.Draws.Sum(d => d.DrawnCount));
            Assert.AreEqual(2, session.State.Duel.Player(Player).Energy.Energy);
        }

        [TestCase(new[] { 9 }, false, 1)]
        [TestCase(new[] { 14, 14, 13 }, false, 1)]
        [TestCase(new[] { 2, 3, 4, 5, 6 }, false, 1)]
        [TestCase(new[] { 14, 2, 3, 4, 5 }, true, 1)]
        [TestCase(new[] { 3, 3, 3, 2, 2 }, false, 1)]
        [TestCase(new[] { 2, 5, 7, 9, 13 }, true, 1)]
        [TestCase(new[] { 9 }, false, 10)]
        public void RouletteFirstActiveTurnMultipliesActualDamageExactlyTenTimes(int[] ranks, bool flush, int level)
        {
            for (int seed = 0; seed < 100; seed++)
            {
                var session = Session(ranks, flush, "15001", level, seed);
                var before = session.State;
                var cards = before.Zone(CardZoneKinds.Hand, Player).Cards.ToArray();
                var baseline = session.Engine.PreviewPlay(before, before.Revision, Player, cards, new[] { Enemy });
                Assert.IsTrue(baseline.Accepted, baseline.Error.ToString());
                var item = before.Duel.Player(Player).Relics.Relics.Single();
                var used = session.UseItem(before.Revision, Player, item.InstanceId);
                Assert.IsTrue(used.Accepted, used.Error.ToString());
                if (BattleItemRuntime.HandMultiplier(session.State.Duel.Player(Player).Relics) != 10) continue;
                Assert.AreEqual(before.Duel.CompletedRounds, session.State.Duel.CompletedRounds);
                Assert.AreEqual(1, session.State.Duel.Player(Player).Relics.Relics.Single().Memory.CounterValue(BattleItemRuntime.RouletteTurnKey));
                var expectedHits = Hits(baseline).Select(e => e.SettledDamage * 10).ToArray();
                Play(session, expectedHits.Sum(), expectedHits);
                return;
            }
            Assert.Fail("No deterministic seed survived roulette activation.");
        }

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
            CollectionAssert.AreEqual(preview.State.Duel.Combat.Statuses.Select(s => s.Stacks), session.State.Duel.Combat.Statuses.Select(s => s.Stacks));
            return transition;
        }

        // Restoring any active category's base points, or counting its kickers, changes these real hits.
        [TestCase(new[] { 2, 5, 9 }, HandCategory.HighCard, 9)]
        [TestCase(new[] { 14 }, HandCategory.HighCard, 14)]
        [TestCase(new[] { 14, 2, 3, 4 }, HandCategory.HighCard, 14)]
        [TestCase(new[] { 13, 14, 12, 9, 2 }, HandCategory.HighCard, 14)]
        [TestCase(new[] { 14, 14, 13, 11, 9 }, HandCategory.OnePair, 56)]
        [TestCase(new[] { 2, 2, 13, 11, 9 }, HandCategory.OnePair, 8)]
        [TestCase(new[] { 2, 2, 3, 3, 13 }, HandCategory.TwoPair, 30)]
        [TestCase(new[] { 2, 2, 2, 13, 12 }, HandCategory.ThreeOfAKind, 18)]
        [TestCase(new[] { 14, 14, 14, 2, 3 }, HandCategory.ThreeOfAKind, 126)]
        [TestCase(new[] { 2, 2, 2, 2, 13 }, HandCategory.FourOfAKind, 32)]
        [TestCase(new[] { 2, 2, 2, 2, 2 }, HandCategory.FiveOfAKind, 120)]
        public void HandDamageUsesOnlyItsScoringCardsWithoutExtraBasePoints(int[] ranks, HandCategory category, long damage)
        {
            var session = Session(ranks);
            var result = Play(session, damage);
            Assert.AreEqual(category, result.Score.Category);
            Assert.IsFalse(result.Events.Any(e => e.Effect.Kind == CombatEffectKind.Heal));
        }

        [Test]
        public void HighCardSelectsAceAheadOfKingAndQueen()
        {
            var session = Session(new[] { 13, 14, 12, 9, 2 });
            var cards = session.State.Zone(CardZoneKinds.Hand, Player).Cards.ToArray();
            var preview = session.Engine.PreviewHand(session.State, Player, cards);
            Assert.IsTrue(preview.Accepted);
            Assert.AreEqual(HandCategory.HighCard, preview.Pattern.Category);
            Assert.AreEqual(14, preview.Pattern.TieBreakRanks[0]);
            Assert.AreEqual(1, preview.Pattern.ScoringCards.Count);
            Assert.AreEqual(Rank.Ace, session.Engine.Face(session.State.Table.GetCard(preview.Pattern.ScoringCards.Single())).Rank);
            Assert.AreEqual(14, preview.Score.Points);
        }

        // Restoring pointsPerLevel must fail, while the existing multiplier growth still contributes.
        [TestCase(new[] { 9 }, HandCategory.HighCard, 27)]
        [TestCase(new[] { 14 }, HandCategory.HighCard, 42)]
        [TestCase(new[] { 2, 2, 13 }, HandCategory.OnePair, 16)]
        [TestCase(new[] { 2, 2, 3, 3, 13 }, HandCategory.TwoPair, 50)]
        [TestCase(new[] { 2, 2, 2, 13, 12 }, HandCategory.ThreeOfAKind, 30)]
        [TestCase(new[] { 2, 2, 2, 2, 13 }, HandCategory.FourOfAKind, 64)]
        [TestCase(new[] { 2, 2, 2, 2, 2 }, HandCategory.FiveOfAKind, 180)]
        public void LevelThreeKeepsMultiplierGrowthWithoutAddingHandPoints(int[] ranks, HandCategory category, long damage)
        {
            var result = Play(Session(ranks, level: 3), damage);
            Assert.AreEqual(category, result.Score.Category);
        }

        [TestCase(false, null, 1, 60, new long[] { 6, 9, 12, 15, 18 }, 0)]
        [TestCase(false, null, 3, 100, new long[] { 10, 15, 20, 25, 30 }, 0)]
        [TestCase(false, "13003", 1, 100, new long[] { 10, 15, 20, 25, 30 }, 0)]
        [TestCase(true, null, 1, 60, new long[] { 6, 9, 12, 15, 18 }, 10)]
        [TestCase(true, "10001", 1, 100, new long[] { 10, 15, 20, 25, 30 }, 10)]
        [TestCase(true, "13003", 1, 100, new long[] { 10, 15, 20, 25, 30 }, 10)]
        [TestCase(true, "11002", 1, 60, new long[] { 6, 9, 12, 15, 18 }, 16)]
        public void StraightHitsAscendingWithoutSharedBasePointsAndKeepsPoison(bool flush, string relic, int level,
            long damage, long[] hits, long healing)
        {
            var session = Session(new[] { 6, 2, 5, 3, 4 }, flush, relic, level);
            var result = Play(session, damage, hits);
            Assert.AreEqual(flush ? HandCategory.StraightFlush : HandCategory.Straight, result.Score.Category);
            Assert.AreEqual(5, session.State.Duel.Combat.Statuses.Single(s => s.DefinitionId.Value == "bleed").Stacks,
                "Each of the five hits applies one poison stack, including flush and multiplier bonuses.");
            if (flush)
            {
                Assert.AreEqual(healing, result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).HealthRestored);
                Assert.AreEqual(600 + healing, session.State.Duel.Combat.Get(Player).Health);
            }
            else Assert.IsFalse(result.Events.Any(e => e.Effect.Kind == CombatEffectKind.Heal));
        }

        private static void EnablePointPoison(XDocument xml)
        {
            foreach (var status in xml.Descendants("OnHitStatus").Where(e => (string)e.Attribute("statusRef") == "bleed"))
                status.SetAttributeValue("stacksKind", "CardBasePoints");
        }

        [Test]
        public void RepeatedPointStatusResolvesCoefficientZeroAndLifetimeBeforeBinding()
        {
            var faces = new[] {
                new EffectiveCard(new CardInstanceId("zero"), Rank.Two, Suit.Clubs),
                new EffectiveCard(new CardInstanceId("seven"), Rank.Three, Suit.Hearts) };
            var cards = faces.Select((face, i) => new AttackCardSnapshot(
                new CardInstance(face.Id, new CardDefinitionId("test")), face, i == 0 ? 0 : 7)).ToArray();
            var hand = new HandAttackInfo(PokerHandClassifier.Classify(faces), cards);
            var lifetime = StatusLifetimeSpec.ForTriggers(3);
            var configured = new ConfiguredEffectStep("damage", CombatEffectKind.Damage, EffectTargetSpec.Opponents(),
                new EffectAmountSpec(EffectAmountKind.Constant, 1),
                onHitStatus: StatusApplicationSpec.WithLifetime(new StatusDefinitionId("bleed"), lifetime, 2),
                perSubmittedCard: true, onHitStacksKind: OnHitStacksKind.CardBasePoints);
            var steps = configured.ResolveAll(null, hand).ToArray();
            Assert.IsNull(steps[0].Effect.OnHitStatus, "Zero base points must not create an invalid zero-stack status.");
            Assert.AreEqual(14, steps[1].Effect.OnHitStatus.Stacks);
            Assert.AreSame(lifetime, steps[1].Effect.OnHitStatus.Lifetime);
            Assert.Throws<ArgumentException>(() => configured.ResolveOnHitStatus());
            var huge = new AttackCardSnapshot(new CardInstance(faces[1].Id, new CardDefinitionId("test")), faces[1], int.MaxValue);
            Assert.Throws<OverflowException>(() => configured.ResolveOnHitStatus(huge));
        }

        [TestCase(new[] { 6, 2, 5, 3, 4 }, false, null, 1, 20, new long[] { 6, 9, 12, 15, 18 })]
        [TestCase(new[] { 6, 2, 5, 3, 4 }, true, "13003", 1, 20, new long[] { 10, 15, 20, 25, 30 })]
        [TestCase(new[] { 6, 2, 5, 3, 4 }, false, null, 3, 20, new long[] { 10, 15, 20, 25, 30 })]
        [TestCase(new[] { 5, 3, 14, 4, 2 }, false, null, 1, 28, new long[] { 42, 6, 9, 12, 15 })]
        [TestCase(new[] { 5, 3, 14, 4, 2 }, true, null, 1, 28, new long[] { 42, 6, 9, 12, 15 })]
        [TestCase(new[] { 14, 13, 10, 12, 11 }, false, null, 1, 60, new long[] { 30, 33, 36, 39, 42 })]
        public void PointPoisonUsesContextualBasePointsWithoutDamageMultipliers(int[] ranks, bool flush, string relic, int level, int poison, long[] hits)
        {
            var session = Session(ranks, flush, relic, level, configure: EnablePointPoison);
            Play(session, hits.Sum(), hits);
            Assert.AreEqual(poison, session.State.Duel.Combat.Statuses.Single(s => s.DefinitionId.Value == "bleed").Stacks);
        }

        [TestCase(true, 20, new long[] { 3, 21, 9, 12, 15 })]
        [TestCase(false, 33, new long[] { 42, 21, 9, 12, 15 })]
        public void PointPoisonUsesConfiguredPointsAndHonorsWheelSwitch(bool wheel, int poison, long[] hits)
        {
            var session = Session(new[] { 5, 3, 14, 4, 2 }, configure: xml => {
                EnablePointPoison(xml);
                xml.Descendants().Single(e => e.Attribute("wheelAceScoresOne") != null).SetAttributeValue("wheelAceScoresOne", wheel);
                xml.Descendants("CardPoints").Single().Elements().Single(e => (string)e.Attribute("rank") == "Two").SetAttributeValue("points", 7);
            });
            Play(session, hits.Sum(), hits);
            Assert.AreEqual(poison, session.State.Duel.Combat.Statuses.Single(s => s.DefinitionId.Value == "bleed").Stacks);
        }

        [TestCase("Unknown", null, null)]
        [TestCase("CardBasePoints", "false", null)]
        [TestCase("CardBasePoints", null, "1")]
        public void InvalidPointPoisonConfigurationIsRejected(string kind, string repeated, string perSubmittedCard)
        {
            var xml = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/SingleChapterBattle.xml"));
            var status = xml.Descendants("OnHitStatus").First(e => (string)e.Attribute("statusRef") == "bleed");
            status.SetAttributeValue("stacksKind", kind);
            if (repeated != null) status.Parent.SetAttributeValue("perSubmittedCard", repeated);
            if (perSubmittedCard != null) status.SetAttributeValue("stacksPerSubmittedCard", perSubmittedCard);
            Assert.IsFalse(BattleContentCompiler.CompileXml(xml.ToString()).Success);
        }

        [TestCase(new[] { 5, 3, 14, 4, 2 }, 84, new long[] { 42, 6, 9, 12, 15 })]
        [TestCase(new[] { 14, 13, 10, 12, 11 }, 180, new long[] { 30, 33, 36, 39, 42 })]
        public void StraightAceKeepsConfiguredPointsAndUsesLowRankOnlyForWheelOrder(int[] ranks, long damage, long[] hits)
        {
            var session = Session(ranks);
            var cards = session.State.Zone(CardZoneKinds.Hand, Player).Cards.ToArray();
            var preview = session.Engine.PreviewHand(session.State, Player, cards);
            Assert.AreEqual(HandCategory.Straight, preview.Pattern.Category);
            Assert.AreEqual(ranks.Contains(2) ? 5 : 14, preview.Pattern.TieBreakRanks[0]);
            Assert.AreEqual(damage, preview.Score.Total);
            Play(session, damage, hits);
        }

        [TestCase(null, 14)]
        [TestCase("11002", 22)]
        public void WheelFlushKeepsFourteenForAceAndIndependentHealing(string relic, long healing)
        {
            var session = Session(new[] { 5, 3, 14, 4, 2 }, true, relic);
            var result = Play(session, 84, 42, 6, 9, 12, 15);
            Assert.AreEqual(HandCategory.StraightFlush, result.Score.Category);
            Assert.AreEqual(healing, result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).HealthRestored);
            Assert.AreEqual(600 + healing, session.State.Duel.Combat.Get(Player).Health);
        }

        [Test]
        public void NonStraightFlushAceAlsoScoresFourteen()
        {
            var result = Play(Session(new[] { 14, 2, 5, 7, 9 }, true), 148);
            Assert.AreEqual(HandCategory.Flush, result.Score.Category);
            Assert.AreEqual(18, result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).HealthRestored);
        }

        [TestCase(new[] { 9 }, false, BattleHandType.HighCard)]
        [TestCase(new[] { 7, 7 }, false, BattleHandType.OnePair)]
        [TestCase(new[] { 7, 7, 2, 2 }, false, BattleHandType.TwoPair)]
        [TestCase(new[] { 7, 7, 7 }, false, BattleHandType.ThreeOfAKind)]
        [TestCase(new[] { 2, 3, 4, 5, 6 }, false, BattleHandType.Straight)]
        [TestCase(new[] { 2, 5, 7, 9, 13 }, true, BattleHandType.Flush)]
        [TestCase(new[] { 7, 7, 7, 2, 2 }, false, BattleHandType.FullHouse)]
        [TestCase(new[] { 7, 7, 7, 7, 13 }, true, BattleHandType.FourOfAKind)]
        [TestCase(new[] { 2, 3, 4, 5, 6 }, true, BattleHandType.StraightFlush)]
        [TestCase(new[] { 7, 7, 7, 7, 7 }, false, BattleHandType.FiveOfAKind)]
        [TestCase(new[] { 7, 7, 7, 2, 2 }, true, BattleHandType.FlushHouse)]
        [TestCase(new[] { 7, 7, 7, 7, 7 }, true, BattleHandType.FlushFive)]
        [TestCase(new[] { 10, 11, 12, 13, 14 }, true, BattleHandType.RoyalFlush)]
        public void AllThirteenFinalIdentitiesAreAvailableToPresentation(int[] ranks, bool flush, BattleHandType expected)
        {
            var session = Session(ranks, flush);
            var facts = session.Engine.PreviewHandFacts(session.State, Player, session.State.Zone(CardZoneKinds.Hand, Player).Cards);
            Assert.AreEqual(expected, facts.FinalType);
        }

        [Test] public void SameSuitFourOfAKindIgnoresKickerAndDoesNotHeal()
        {
            var result = Play(Session(new[] { 7, 7, 7, 7, 13 }, true), 112);
            Assert.AreEqual(HandCategory.FourOfAKind, result.Pattern.Category);
            Assert.AreEqual(4, result.Pattern.ScoringCards.Count);
            Assert.IsFalse(result.Events.Any(e => e.Effect.Kind == CombatEffectKind.Heal));
        }

        [TestCase(false)] [TestCase(true)]
        public void FiveOfAKindKeepsItsDamageAndFlushOnlyAddsHealing(bool flush)
        {
            var result = Play(Session(new[] { 7, 7, 7, 7, 7 }, flush), 420);
            Assert.AreEqual(HandCategory.FiveOfAKind, result.Pattern.Category);
            Assert.AreEqual(flush ? 17 : 0, result.Events.Where(e => e.Effect.Kind == CombatEffectKind.Heal).Sum(e => e.HealthRestored));
            Assert.IsFalse(result.Events.Any(e => e.Effect.Kind == CombatEffectKind.ApplyStatus));
        }

        [Test] public void StraightFlushInheritsStraightLevelInsteadOfRoyalLevel()
        {
            var session = Session(new[] { 2, 3, 4, 5, 6 }, true, configure: xml => {
                var loadout = xml.Root.Element("Loadouts").Elements().Single();
                loadout.Add(new XElement("HandLevel", new XAttribute("category", "Straight"), new XAttribute("level", 3)));
                loadout.Add(new XElement("HandLevel", new XAttribute("category", "StraightFlush"), new XAttribute("level", 9)));
            });
            var result = Play(session, 100, 10, 15, 20, 25, 30);
            Assert.AreEqual(10, result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).HealthRestored);
        }

        [TestCase(null, 1, 144)]
        [TestCase("10001", 1, 216)]
        [TestCase("13003", 1, 216)]
        [TestCase(null, 3, 288)]
        public void FlushUsesItsOwnMultiplierAndHealsFiftyPercentIndependently(string relic, int level, long damage)
        {
            var session = Session(new[] { 2, 5, 7, 9, 13 }, true, relic, level);
            var result = Play(session, damage);
            Assert.AreEqual(HandCategory.Flush, result.Score.Category);
            Assert.AreEqual(18, result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).HealthRestored,
                "The five cards total 36; floor(36 × 0.5) stays 18 regardless of score multiplier or level.");
            Assert.AreEqual(618, session.State.Duel.Combat.Get(Player).Health);
        }

        [Test]
        public void PureFlushExcludesIneffectiveCardFromBothDamageAndHealing()
        {
            var session = Session(new[] { 2, 5, 7, 9, 13 }, true);
            var cards = session.State.Zone(CardZoneKinds.Hand, Player).Cards.ToArray();
            Assert.IsTrue(session.ApplyAdmin(session.State.Revision, new BattleAdminPatch(zones: new[] {
                new BattleAdminZone(Player, CardZoneKinds.Hand, cards.Select(id => new BattleAdminCard(instanceId: id.Value,
                    effects: session.Engine.Face(session.State.Table.GetCard(id)).Rank == Rank.Two ? CardEffects.Ineffective : CardEffects.None)))
            })).Accepted);
            // The ineffective 2 still makes a five-card flush but contributes neither damage nor healing.
            var result = Play(session, 136);
            Assert.AreEqual(17, result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).HealthRestored);
        }

        [TestCase(new[] { 2, 2, 7, 9, 13 }, 132)]
        [TestCase(new[] { 2, 2, 7, 7, 13 }, 124)]
        [TestCase(new[] { 2, 2, 2, 9, 13 }, 112)]
        public void FlushOverridesPairsAndTriplesAndScoresAllFiveCards(int[] ranks, long damage)
        {
            var result = Play(Session(ranks, true), damage);
            Assert.AreEqual(HandCategory.Flush, result.Score.Category);
            Assert.AreEqual(ranks.Sum() / 2, result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).HealthRestored);
        }

        [TestCase(null, 17)]
        [TestCase("11002", 28)]
        public void FlushHealingCombinesHeartBonusBeforeRounding(string relic, long healing)
        {
            var session = Session(new[] { 13, 9, 7, 4, 2 }, true, relic);
            var result = Play(session, 140);
            Assert.AreEqual(healing, result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).HealthRestored,
                "35 points heal 17 at 50%, or 28 at 50% + 30%; round only after combining the rates.");
            Assert.AreEqual(600 + healing, session.State.Duel.Combat.Get(Player).Health);
        }

        [Test]
        public void PairMultiplierPlusTwoRelicStillAddsToTheUnchangedPairMultiplier()
        {
            Play(Session(new[] { 2, 2, 13 }, relic: "13003"), 16);
        }

        [TestCase(1, null, false, 52, new long[] { 36, 16 })]
        [TestCase(3, null, false, 104, new long[] { 72, 32 })]
        [TestCase(1, "13003", false, 104, new long[] { 72, 32 })]
        [TestCase(1, null, true, 52, new long[] { 36, 16 })]
        public void FullHouseScalesTripleAndPairSeparatelyAndKeepsItsControlSkill(int level, string relic, bool flush,
            long damage, long[] hits)
        {
            var session = Session(new[] { 3, 3, 3, 2, 2 }, flush, relic, level);
            var result = Play(session, damage, hits);
            Assert.AreEqual(HandCategory.FullHouse, result.Score.Category);
            Assert.IsTrue(session.State.Duel.Combat.Statuses.Any(s => s.DefinitionId.Value == "stun" && s.Target.Id == Enemy.Value));
            Assert.IsTrue(session.State.Duel.Combat.Statuses.Any(s => s.DefinitionId.Value == "control-immunity" && s.Target.Id == Player.Value));
            if (flush) Assert.AreEqual(6, result.Events.Single(e => e.Effect.Kind == CombatEffectKind.Heal).HealthRestored,
                "Flush house keeps the original component damage and adds floor(13 × 0.5) healing.");
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
            // The round-two ultimate must resolve before this fixture reaches the real guard action.
            Assert.IsTrue(session.ApplyAdmin(session.State.Revision, new BattleAdminPatch(
                vitals: new[] { new BattleAdminVitals(Player, 100000, 100000) })).Accepted);
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
            Assert.AreEqual(1955, attack.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(5, attack.State.Duel.Combat.Statuses.Single(s =>
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

        [Test]
        public void SevenStationGrenadesConsumeOneShellLayerPerUseAndKeepTheirFixedDamage()
        {
            var session = WrathAfterGuard();
            Assert.IsTrue(session.ApplyAdmin(session.State.Revision, new BattleAdminPatch(relics: new[] {
                new BattleAdminRelics(Player, new[] { new BattleAdminRelic("11004") }) })).Accepted);
            string item = session.State.Duel.Player(Player).Relics.Relics.Single().InstanceId;
            foreach (long expected in new long[] { 0, 0, 20 })
            {
                var used = session.UseItem(session.State.Revision, Player, item);
                Assert.IsTrue(used.Accepted, used.Error.ToString());
                Assert.AreEqual(expected, Hits(used.Steps.Single().Transition).Single().SettledDamage);
            }
            Assert.AreEqual(0, ShellLayers(session.State));
            Assert.AreEqual(1980, session.State.Duel.Combat.Get(Enemy).Health);
            Assert.IsTrue(WrathBossQuery.Read(session.State.Duel).Hands.All(h => !h.IsAlive && h.ReviveRemaining == 8));
        }
    }
}
