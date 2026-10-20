using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class DesignRevision1563Tests
    {
        private static readonly ParticipantId P = new ParticipantId("p"), E = new ParticipantId("e");
        private sealed class Effects : IScoredHandEffectRule
        {
            public string Version => "design-test";
            private readonly BattleEffectStep[] steps;
            public Effects(params BattleEffectStep[] steps) { this.steps = steps; }
            public BattleEffectSequence Resolve(ScoredHandEffectContext c) => new BattleEffectSequence(Version, "test", steps);
        }
        private static BattleSession Session(BattleTurnFlowRules flow = null, IScoredHandEffectRule effects = null,
            CombatStatusCatalog statuses = null, IEnumerable<BattleEnemySetup> enemies = null, int deckSize = 20, int energy = 3,
            IEnumerable<BattleRelicSetup> relics = null, int playCost = 1)
        {
            var engine = new CardDuelEngine(new BattleRules("design-test", 20, statusCatalog: statuses));
            var deck = StandardDeck.Cards().Take(deckSize).Select((c, i) => new DeckEntry("c" + i, c.Id));
            var rules = new BattlePlayerRules(maxEnergy: Math.Max(1, energy), initialEnergy: energy, playEnergyCost: playCost, handSize: 8, unlimitedDiscards: true,
                maxDiscardCards: 52, effectRule: effects, discardReward: new ChipBlockRewardRule(1),
                flow: flow ?? new BattleTurnFlowRules(manualDraw: true, kickerScoring: KickerScoring.ChipsOnly, layeredScoreRelics: true));
            var state = engine.Create("design-test", HmacDeckShuffler.FromSeed(Enumerable.Range(1, 32).Select(i => (byte)i)),
                new[] { new BattlePlayerSetup(P, new CombatantDefinition("hero", 1000), deck, rules: rules, relics: relics) },
                enemies ?? new[] { new BattleEnemySetup(E, new CombatantDefinition("enemy", 10000), 0) });
            var session = new BattleSession(engine, state); Act(session, CardDuelAction.Start); return session;
        }
        private static BattleSessionResult Act(BattleSession s, CardDuelAction a, IEnumerable<CardInstanceId> cards = null, int count = 0)
        {
            var result = s.Execute(s.State.Revision, P, a, cards, drawCount: count);
            Assert.IsTrue(result.Accepted, a + ": " + result.Error); return result;
        }
        private static CardInstanceId[] Hand(BattleSession s) => s.State.Zone(CardZoneKinds.Hand, P).Cards.ToArray();
        private static void Finish(BattleSession s)
        {
            Act(s, CardDuelAction.EndTurn);
            if (!s.State.Duel.IsFinished && s.State.FlowFor(P).Window == BattlePlayerWindow.Ending) Act(s, CardDuelAction.FinishTurn);
        }
        [Test] public void EmptyOpeningDrawOnceThenThreeActionsAndRetainHand()
        {
            var s = Session(); Assert.IsEmpty(Hand(s)); Assert.AreEqual(BattlePlayerWindow.Draw, s.State.FlowFor(P).Window);
            Assert.IsFalse(s.Execute(s.State.Revision, P, CardDuelAction.Play, Hand(s)).Accepted);
            Act(s, CardDuelAction.Draw, count: 5); Assert.AreEqual(5, Hand(s).Length); Assert.AreEqual(3, s.State.Duel.Player(P).Energy.Energy);
            Assert.IsFalse(s.Execute(s.State.Revision, P, CardDuelAction.Draw, drawCount: 1).Accepted);
            for (int i = 0; i < 3; i++) Act(s, CardDuelAction.Play, Hand(s).Take(1));
            Assert.AreEqual(BattlePlayerWindow.Ending, s.State.FlowFor(P).Window); Assert.AreEqual(0, s.State.FlowFor(P).ActionsRemaining);
            Assert.AreEqual(2, Hand(s).Length); Act(s, CardDuelAction.FinishTurn);
            Assert.AreEqual(1, s.State.Duel.CompletedRounds); Assert.AreEqual(2, Hand(s).Length);
            Assert.AreEqual(BattlePlayerWindow.Draw, s.State.FlowFor(P).Window);
        }
        [Test] public void FullHandDrawDoesNotConsumeUseAndReplacementDiscardHasNoShieldOrCost()
        {
            var s = Session(new BattleTurnFlowRules(manualDraw: true, openingDeal: true));
            Assert.AreEqual(8, Hand(s).Length); Assert.IsTrue(s.Engine.RequiresDrawWarning(s.State, P));
            var before = s.State; Assert.IsFalse(s.Execute(before.Revision, P, CardDuelAction.Draw, drawCount: 5).Accepted);
            Assert.AreSame(before, s.State); Assert.AreEqual(1, s.State.FlowFor(P).DrawUsesRemaining);
            Act(s, CardDuelAction.DiscardForDraw, Hand(s).Take(2)); Assert.AreEqual(0, s.State.Duel.Combat.Get(P).Block);
            Assert.AreEqual(3, s.State.Duel.Player(P).Energy.Energy); Act(s, CardDuelAction.Draw, count: 5);
            Assert.AreEqual(8, Hand(s).Length); Assert.AreEqual(0, s.State.FlowFor(P).DrawUsesRemaining);
        }
        [Test] public void EffectDrawCanOverflowAndConfirmationRequiresExactlyTheExcess()
        {
            var s = Session(effects: new Effects(new BattleEffectStep(BattleEffectSpec.DrawCards(10), EffectTargetSpec.Self())));
            Act(s, CardDuelAction.Draw, count: 5); Act(s, CardDuelAction.Play, Hand(s).Take(1)); Assert.AreEqual(14, Hand(s).Length);
            Act(s, CardDuelAction.EndTurn); Assert.AreEqual(BattlePlayerWindow.ForcedDiscard, s.State.FlowFor(P).Window);
            Assert.AreEqual(6, s.Engine.RequiredDiscards(s.State, P)); var before = s.State;
            Assert.IsFalse(s.Execute(before.Revision, P, CardDuelAction.ConfirmOverflow, Hand(s).Take(5)).Accepted);
            Assert.IsFalse(s.Execute(before.Revision, P, CardDuelAction.FinishTurn).Accepted); Assert.AreSame(before, s.State);
            Act(s, CardDuelAction.ConfirmOverflow, Hand(s).Take(6)); Assert.AreEqual(8, Hand(s).Length);
            Assert.AreEqual(0, s.State.Duel.Combat.Get(P).Block); Act(s, CardDuelAction.FinishTurn); Assert.AreEqual(1, s.State.Duel.CompletedRounds);
        }
        [Test] public void DefenseUsesChipsAndImmediatelyEndsTurnWithoutRefill()
        {
            var s = Session(); Act(s, CardDuelAction.Draw, count: 5); var cards = Hand(s).Take(2).ToArray();
            long chips = cards.Sum(id => s.Engine.Scoring.CardPoints.GetBasePoints(new CardFace(s.Engine.Face(s.State.Table.GetCard(id)).Rank, s.Engine.Face(s.State.Table.GetCard(id)).Suit)));
            var result = Act(s, CardDuelAction.Discard, cards);
            Assert.AreEqual(chips, result.Steps[0].Transition.Events.Single(e => e.Effect.Kind == CombatEffectKind.GainBlock).SettledAmount);
            Assert.AreEqual(1, result.Steps[0].Transition.ActionCost.Cost); Assert.AreEqual(1, s.State.Duel.CompletedRounds);
            Assert.AreEqual(3, Hand(s).Length); Assert.AreEqual(BattlePlayerWindow.Draw, s.State.FlowFor(P).Window);
        }
        [Test] public void SkipDrawShortDeckAndReshuffleAreDeterministic()
        {
            var s = Session(deckSize: 3); Act(s, CardDuelAction.Draw, count: 5); Assert.AreEqual(3, Hand(s).Length);
            Act(s, CardDuelAction.Discard, Hand(s)); Assert.AreEqual(BattlePlayerWindow.Draw, s.State.FlowFor(P).Window);
            var before = s.State; var preview = new BattleSession(s.Engine, before);
            var a = Act(preview, CardDuelAction.Draw, count: 5); Assert.AreSame(before, s.State);
            var b = Act(s, CardDuelAction.Draw, count: 5);
            CollectionAssert.AreEqual(a.State.Zone(CardZoneKinds.Hand).Cards, b.State.Zone(CardZoneKinds.Hand).Cards);
            Assert.IsTrue(b.Timeline.Any(t => t.Kind == BattleTimelineKind.DeckRecycled));
            Finish(s); Act(s, CardDuelAction.SkipDraw); Assert.AreEqual(3, Hand(s).Length);
        }
        [Test] public void KickerChipsAreOutsideMultiplierAndScoreRelicsAreLayered()
        {
            var cards = new[] { new ScoringCard("a", new CardFace(Rank.Two, Suit.Clubs)), new ScoringCard("b", new CardFace(Rank.Two, Suit.Hearts)), new ScoringCard("c", new CardFace(Rank.King, Suit.Clubs)) };
            var pattern = PokerHandClassifier.Classify(cards.Select(c => new EffectiveCard(new CardInstanceId(c.Id), c.Face.Rank, c.Face.Suit)));
            var profile = new ScoreProfile("test", Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>().Select(c => new HandScoreRule(c, 0, 2, 0, 0)));
            var relics = new[] { new ScoringRelic("first", 1, 0, new[] { new ScoreEffect(ScoreTiming.AfterCards, ScoreOperation.MultiplyMultiplier, 3) }),
                new ScoringRelic("second", 1, 0, new[] { new ScoreEffect(ScoreTiming.AfterCards, ScoreOperation.AddMultiplier, 4) }) };
            var score = ScoreEngine.EvaluateBattle("score", profile, pattern, 1, cards, relics, KickerScoring.ChipsOnly, true);
            Assert.AreEqual(4, score.Points); Assert.AreEqual(10, score.UnmultipliedPoints); Assert.AreEqual(18, score.Multiplier);
            Assert.AreEqual(82, score.Total); ScoreEngine.Validate(score);
        }
        [Test] public void FullHouseComponentsUseSeparateRanksAndMultipliers()
        {
            var cards = new[] { Rank.Two, Rank.Two, Rank.Two, Rank.Ten, Rank.Ten }.Select((r, i) => new ScoringCard("c" + i, new CardFace(r, (Suit)(i % 4 + 1)))).ToArray();
            var pattern = PokerHandClassifier.Classify(cards.Select(c => new EffectiveCard(new CardInstanceId(c.Id), c.Face.Rank, c.Face.Suit)));
            var profile = new ScoreProfile("test", Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>().Select(c => new HandScoreRule(c, 0, c == HandCategory.ThreeOfAKind ? 3 : 2, 0, 0)));
            var score = ScoreEngine.EvaluateBattle("score", profile, pattern, 1, cards, Array.Empty<ScoringRelic>(), KickerScoring.ChipsOnly, true);
            Assert.AreEqual(18, new EffectAmountSpec(EffectAmountKind.ThreeOfAKindTotalScale, 1).Resolve(score));
            Assert.AreEqual(40, new EffectAmountSpec(EffectAmountKind.OnePairTotalScale, 1).Resolve(score)); ScoreEngine.Validate(score);
        }
        [Test] public void BleedAddsStacksWithoutRefreshAndDecaysAtEachHolderTurnStart()
        {
            var id = new StatusDefinitionId("bleed");
            var catalog = new CombatStatusCatalog("test", new[] { new CombatStatusDefinition(id, StatusTags.Negative,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.TurnStarted, 1, stackDecay: 1) }, StatusLifetimeSpec.UntilRemoved(), StatusReapplyPolicy.AddStacks) });
            var effects = new Effects(new BattleEffectStep(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(id, 2)), EffectTargetSpec.Opponents()),
                new BattleEffectStep(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(id, 3)), EffectTargetSpec.Opponents()));
            var s = Session(effects: effects, statuses: catalog); Act(s, CardDuelAction.Draw, count: 5); Act(s, CardDuelAction.Play, Hand(s).Take(1));
            Assert.AreEqual(5, s.State.Duel.Combat.Statuses.Single().Stacks); Finish(s);
            Assert.AreEqual(9995, s.State.Duel.Combat.Get(E).Health); Assert.AreEqual(4, s.State.Duel.Combat.Statuses.Single().Stacks);
            Act(s, CardDuelAction.SkipDraw); Finish(s); Assert.AreEqual(9991, s.State.Duel.Combat.Get(E).Health);
            Assert.AreEqual(3, s.State.Duel.Combat.Statuses.Single().Stacks);
        }
        [Test] public void MultiHitRetargetsAfterDeathAndStatusFollowsTheHitNotTheCorpse()
        {
            var id = new StatusDefinitionId("bleed"); var catalog = new CombatStatusCatalog("test", new[] { new CombatStatusDefinition(id, StatusTags.Negative,
                Array.Empty<StatusBehaviorSpec>(), StatusLifetimeSpec.UntilRemoved(), StatusReapplyPolicy.AddStacks) });
            var target = EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving).WithRetargetChain("straight");
            var steps = Enumerable.Range(0, 3).SelectMany(i => new[] { new BattleEffectStep(BattleEffectSpec.Damage(2), target),
                new BattleEffectStep(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(id)), target) }).ToArray();
            var e2 = new ParticipantId("e2"); var e3 = new ParticipantId("e3");
            var enemies = new[] { new BattleEnemySetup(E, new CombatantDefinition("one", 1), 0),
                new BattleEnemySetup(e2, new CombatantDefinition("two", 30), 0), new BattleEnemySetup(e3, new CombatantDefinition("three", 20), 0) };
            var s = Session(effects: new Effects(steps), statuses: catalog, enemies: enemies); Act(s, CardDuelAction.Draw, count: 5);
            var result = Act(s, CardDuelAction.Play, Hand(s).Take(1));
            CollectionAssert.AreEqual(new[] { E, e3, e3 }, result.Steps[0].Transition.Events.Where(e => e.Effect.Kind == CombatEffectKind.Damage).Select(e => e.Effect.Target));
            Assert.AreEqual(16, s.State.Duel.Combat.Get(e3).Health); Assert.AreEqual(30, s.State.Duel.Combat.Get(e2).Health);
            Assert.AreEqual(e3.Value, s.State.Duel.Combat.Statuses.Single().Target.Id); Assert.AreEqual(2, s.State.Duel.Combat.Statuses.Single().Stacks);
        }
        [Test] public void RuntimeRuleValuesChangeDrawAndActionLimitsWithinGuards()
        {
            var relic = new BattleRelicSetup(new RelicDefinition("limits", Array.Empty<ScoreEffect>()), new RunRelic("r", "limits"),
                new BattleRelicRules("test", ruleValues: new[] { new RelicRuleValueSpec("draw", BattleRuleValueKey.DrawCount, 1),
                    new RelicRuleValueSpec("actions", BattleRuleValueKey.ActionsPerTurn, -2), new RelicRuleValueSpec("play", BattleRuleValueKey.MaxPlayCards, 1) }));
            var s = Session(relics: new[] { relic }); Act(s, CardDuelAction.Draw, count: 6);
            Assert.AreEqual(6, Hand(s).Length); Assert.AreEqual(6, s.Engine.PlayLimit(s.State, P, false));
            Act(s, CardDuelAction.Play, Hand(s).Take(1)); Assert.AreEqual(BattlePlayerWindow.Ending, s.State.FlowFor(P).Window);
            Assert.IsFalse(s.Execute(s.State.Revision, P, CardDuelAction.Play, Hand(s).Take(1)).Accepted);
        }
        [Test] public void ForeignEngineStateCannotBeMutatedByDrawCommands()
        {
            var a = Session(); var b = Session(); var before = a.State;
            var result = b.Engine.Execute(before, before.Revision, P, CardDuelAction.Draw, drawCount: 5);
            Assert.AreEqual(CardDuelError.InvalidState, result.Error); Assert.AreSame(before, result.State);
        }
        [Test] public void FreePlaysAtZeroEnergyStillRespectIndependentActionLimit()
        {
            var s = Session(energy: 0, playCost: 0); Act(s, CardDuelAction.Draw, count: 5);
            Act(s, CardDuelAction.Play, Hand(s).Take(1));
            Assert.AreEqual(BattlePlayerWindow.Action, s.State.FlowFor(P).Window); Assert.AreEqual(2, s.State.FlowFor(P).ActionsRemaining);
            Act(s, CardDuelAction.Play, Hand(s).Take(1)); Act(s, CardDuelAction.Play, Hand(s).Take(1));
            Assert.AreEqual(BattlePlayerWindow.Ending, s.State.FlowFor(P).Window);
        }
        [Test] public void BleedKillAtEnemyTurnStartPreventsItsAttack()
        {
            var id = new StatusDefinitionId("bleed");
            var catalog = new CombatStatusCatalog("bleed", new[] { new CombatStatusDefinition(id, StatusTags.Negative,
                new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.TurnStarted, 1, stackDecay: 1) }, StatusLifetimeSpec.UntilRemoved(), StatusReapplyPolicy.AddStacks) });
            var s = Session(statuses: catalog, effects: new Effects(new BattleEffectStep(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(id, 5)), EffectTargetSpec.Opponents())),
                enemies: new[] { new BattleEnemySetup(E, new CombatantDefinition("enemy", 5), 999) });
            Act(s, CardDuelAction.Draw, count: 5); Act(s, CardDuelAction.Play, Hand(s).Take(1)); Finish(s);
            Assert.IsTrue(s.State.Duel.IsFinished); Assert.AreEqual(1000, s.State.Duel.Combat.Get(P).Health); Assert.AreEqual(0, s.State.Duel.Combat.Get(E).Health);
        }
        [Test] public void StunnedPlayerCanDrawBeforeSkippingItsActionWindow()
        {
            var id = new StatusDefinitionId("stun");
            var catalog = new CombatStatusCatalog("stun", new[] { new CombatStatusDefinition(id, StatusTags.Control,
                new StatusBehaviorSpec[] { new BlockActionWindowSpec() }, StatusLifetimeSpec.ForTriggers(1)) });
            var enemyRule = new ConfiguredEnemyEffectRule(new BattleEffectSequence("stun", "stun", new[] {
                new BattleEffectStep(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(id)), EffectTargetSpec.Opponents()) }));
            var s = Session(statuses: catalog, enemies: new[] { new BattleEnemySetup(E, new CombatantDefinition("enemy", 10000), 0, effectRule: enemyRule) });
            Act(s, CardDuelAction.Draw, count: 5); Finish(s);
            Assert.AreEqual(1, s.State.Duel.CompletedRounds); Assert.AreEqual(BattlePlayerWindow.Draw, s.State.FlowFor(P).Window);
            Assert.IsTrue(s.State.Duel.IsActionBlocked(P));
            var drawn = Act(s, CardDuelAction.Draw, count: 2);
            Assert.AreEqual(7, Hand(s).Length); Assert.AreEqual(2, s.State.Duel.CompletedRounds);
            Assert.IsTrue(drawn.Steps.Any(step => step.Transition.RuleEvents.OfType<CombatStatusChangedEvent>().Any(e => e.Reason == CombatStatusChangeReason.Consumed)));
            Assert.IsTrue(s.State.Duel.IsActionBlocked(P)); // The enemy reapplied stun on the following turn.

        }
        [Test] public void SixSubmittedCardsClassifyBestFiveAndKeepExtraChipsOutsideMultiplier()
        {
            var cards = new[] { Rank.Two, Rank.Two, Rank.Three, Rank.Three, Rank.Four, Rank.King }
                .Select((rank, i) => new ScoringCard("c" + i, new CardFace(rank, (Suit)(i % 4 + 1)))).ToArray();
            var pattern = PokerHandClassifier.ClassifyBest(cards.Select(c => new EffectiveCard(new CardInstanceId(c.Id), c.Face.Rank, c.Face.Suit)));
            Assert.AreEqual(HandCategory.TwoPair, pattern.Category);
            var profile = new ScoreProfile("six", Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>().Select(c => new HandScoreRule(c, 0, 4, 0, 0)));
            var score = ScoreEngine.EvaluateBattle("six", profile, pattern, 1, cards, Array.Empty<ScoringRelic>(), KickerScoring.ChipsOnly, true);
            Assert.AreEqual(10, score.Points); Assert.AreEqual(14, score.UnmultipliedPoints); Assert.AreEqual(54, score.Total);
        }
        [TestCase("standard-single-battle")]
        [TestCase("standard-armored-summoner")]
        [TestCase("standard-gluttony-boss")]
        public void StandardXmlUsesRevision1582OpeningDefaults(string encounter)
        {
            var build = BattleContentCompiler.CompileXml(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle.xml"));
            Assert.IsTrue(build.Success, string.Join("; ", build.Diagnostics.Select(d => d.Message)));
            var created = BattleContentFactory.Create(build.Content, new BattleSetupRequest(encounter, "standard-test", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(created.Success); var s = created.Session; var player = s.State.Duel.PlayerId;
            Assert.IsTrue(s.Execute(s.State.Revision, player, CardDuelAction.Start).Accepted);
            Assert.AreEqual(8, s.State.Zone(CardZoneKinds.Hand, player).Cards.Count); Assert.AreEqual(0, s.Engine.DrawLimit(s.State, player));
            Assert.AreEqual(1, s.State.FlowFor(player).DrawUsesRemaining);
            Assert.AreEqual(3, s.State.FlowFor(player).ActionsRemaining); Assert.AreEqual(8, s.State.Duel.Player(player).Limits.HandLimit.Value);
            Assert.IsTrue(s.Execute(s.State.Revision, player, CardDuelAction.SkipDraw).Accepted);
            Assert.AreEqual(8, s.State.Zone(CardZoneKinds.Hand, player).Cards.Count);
        }
        [Test] public void XmlScoreRelicsChangeAuthorityScoreAndInvalidRewardConfigurationIsRejected()
        {
            var document = new System.Xml.XmlDocument(); document.LoadXml(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle.xml"));
            var relic = document.CreateElement("Relic"); relic.SetAttribute("id", "scoring-test");
            relic.InnerXml = "<Score operation='MultiplyMultiplier' amount='3'/><Score operation='AddMultiplier' amount='2'/>";
            document.SelectSingleNode("/BattleContent/Relics").AppendChild(relic);
            var binding = document.CreateElement("Relic"); binding.SetAttribute("instanceId", "score-test"); binding.SetAttribute("ref", "scoring-test");
            document.SelectSingleNode("/BattleContent/Loadouts/Loadout[@id='baseline']").AppendChild(binding);
            var build = BattleContentCompiler.CompileXml(document.OuterXml); Assert.IsTrue(build.Success);
            var s = BattleContentFactory.Create(build.Content, new BattleSetupRequest("standard-single-battle", "score-xml", HmacDeckShuffler.FromSeed(new byte[32]))).Session;
            var actor = s.State.Duel.PlayerId; Assert.IsTrue(s.Execute(s.State.Revision, actor, CardDuelAction.Start).Accepted);
            Assert.IsTrue(s.Execute(s.State.Revision, actor, CardDuelAction.SkipDraw).Accepted);
            var card = s.State.Zone(CardZoneKinds.Hand, actor).Cards.Take(1).ToArray();
            var result = s.Execute(s.State.Revision, actor, CardDuelAction.Play, card); Assert.IsTrue(result.Accepted);
            Assert.AreEqual(9, result.Steps[0].Transition.Score.Multiplier);
            ((System.Xml.XmlElement)document.SelectSingleNode("/BattleContent/Players/Player")).SetAttribute("discardBlockPerCard", "1");
            Assert.IsFalse(BattleContentCompiler.CompileXml(document.OuterXml).Success);
        }
    }
}
