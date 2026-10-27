using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class EnergyOnlyBattleTests
    {
        private static readonly ParticipantId Player = new ParticipantId("energy-player");
        private sealed class DrawEffect : IScoredHandEffectRule
        {
            public string Version => "energy.draw";
            public BattleEffectSequence Resolve(ScoredHandEffectContext context) => new BattleEffectSequence(Version, "draw", new[] {
                new BattleEffectStep("draw", BattleEffectSpec.DrawCards(10), EffectTargetSpec.Self()) });
        }
        private sealed class View : IBattleSessionPresentation
        {
            public bool IsPresenting { get; set; }
            public void Initialize(CardDuelState state) { }
            public void Present(BattleSessionResult result, bool animate) { }
            public void Synchronize(CardDuelState state) { }
        }
        private static BattleSession Create(int energy = 3, int initial = -1, int actions = 3, int playCost = 1,
            int discardCost = 1, int deckSize = 20, IScoredHandEffectRule effect = null, IEnumerable<BattleRelicSetup> relics = null,
            int players = 1, CardDuelRefillTiming playRefill = CardDuelRefillTiming.NextPlayerTurn)
        {
            // Deliberately contradictory legacy flags and budgets must not control the current mode.
            var flow = new BattleTurnFlowRules(manualDraw: true, drawUses: 2, actionsPerTurn: actions,
                discardCostsAction: false, discardEndsTurn: true, endDefense: true, forceDiscardOverflow: true, energyOnly: true);
            var rules = new BattlePlayerRules(maxEnergy: energy, initialEnergy: initial < 0 ? energy : initial,
                playEnergyCost: playCost, discardEnergyCost: discardCost, handSize: 8, discards: 0, unlimitedDiscards: false,
                maxPlayCards: 10, maxDiscardCards: 512,
                dealPolicy: new CardDuelDealPolicy(playRefill, CardDuelRefillTiming.NextPlayerTurn),
                effectRule: effect, flow: flow);
            var deck = StandardDeck.Cards().Take(deckSize).Select((card, i) => new DeckEntry("card/" + i, card.Id));
            var engine = new CardDuelEngine(new BattleRules("energy-only", 20));
            var roster = Enumerable.Range(0, players).Select(i => new BattlePlayerSetup(i == 0 ? Player : new ParticipantId("ally"),
                new CombatantDefinition("hero", 1000), deck, rules: rules, relics: i == 0 ? relics : null));
            var session = new BattleSession(engine, engine.Create("energy-battle", HmacDeckShuffler.FromSeed(new byte[32]), roster,
                new[] { new BattleEnemySetup(new ParticipantId("enemy"), new CombatantDefinition("enemy", 1000000), 0) }));
            Assert.IsTrue(session.Execute(session.State.Revision, Player, CardDuelAction.Start).Accepted);
            return session;
        }
        private static CardInstanceId[] Hand(BattleSession s, int count = 512) => s.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(count).ToArray();
        private static BattleSessionResult Act(BattleSession s, bool discard, int count = 1)
        {
            var result = discard ? s.Discard(s.State.Revision, Player, Hand(s, count)) : s.Play(s.State.Revision, Player, Hand(s, count));
            Assert.IsTrue(result.Accepted, result.Error.ToString()); return result;
        }
        private static int Energy(BattleSession s) => s.State.Duel.Player(Player).Energy.Energy;

        [Test] public void ConfiguredAfterPlayRefillWorksAtEveryEnergyIncludingLastAction()
        {
            var s = Create(playRefill: CardDuelRefillTiming.AfterAction);
            for (int i = 0; i < 3; i++)
            {
                var before = s.State;
                var cards = Hand(s, 5);
                var preview = s.Engine.PreviewPlay(before, before.Revision, Player, cards);
                Assert.IsTrue(preview.Accepted, preview.Error.ToString());
                Assert.AreSame(before, s.State);
                var result = Act(s, false, 5);
                var transition = result.Steps[0].Transition;
                Assert.AreEqual(5, transition.Draws.Sum(d => d.DrawnCount));
                Assert.AreEqual(8, transition.State.Zone(CardZoneKinds.Hand, Player).Cards.Count);
                Assert.AreEqual(2 - i, transition.State.Duel.Player(Player).Energy.Energy);
                CollectionAssert.AreEqual(preview.State.Zone(CardZoneKinds.Hand, Player).Cards,
                    transition.State.Zone(CardZoneKinds.Hand, Player).Cards);
            }
            Assert.AreEqual(1, s.State.Duel.CompletedRounds);
        }

        [Test] public void PlayAndDiscardShareEnergyAndDiscardReplacesOnlyItsOwnCards()
        {
            var s = Create(); Assert.AreEqual(8, Hand(s).Length);
            var play = Act(s, false, 3);
            Assert.AreEqual(5, Hand(s).Length); Assert.AreEqual(2, Energy(s));
            Assert.AreEqual(0, play.Steps.Sum(step => step.Transition.Draws.Sum(draw => draw.DrawnCount)));
            var discard = Act(s, true, 2);
            Assert.AreEqual(5, Hand(s).Length); Assert.AreEqual(1, Energy(s));
            Assert.AreEqual(2, discard.Steps.Sum(step => step.Transition.Draws.Sum(draw => draw.DrawnCount)));
            Assert.IsNull(s.State.RemainingDiscardBudgetFor(Player));
            var last = Act(s, true, 1);
            Assert.AreEqual(0, last.Steps[0].Transition.State.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(1, last.Steps[0].Transition.Draws.Single().DrawnCount);
            Assert.AreEqual(1, s.State.Duel.CompletedRounds); Assert.AreEqual(3, Energy(s));
            Assert.IsTrue(last.Steps.Any(step => step.Action == CardDuelAction.FinishTurn));
        }

        [Test] public void MoreThanThreeOperationsUseEnergyDespiteLegacyActionAndDiscardBudgets()
        {
            var gear = RelicRuleTestData.Gear("actions", new[] { new RelicRuleValueSpec("legacy-count", BattleRuleValueKey.ActionsPerTurn, -2) });
            var s = Create(energy: 6, actions: 1, relics: new[] { gear });
            for (int i = 0; i < 5; i++)
            {
                Act(s, true); Assert.AreEqual(5 - i, Energy(s));
                Assert.AreEqual(Energy(s), s.State.FlowFor(Player).ActionsRemaining);
                Assert.AreEqual(0, s.State.Duel.CompletedRounds); Assert.AreEqual(8, Hand(s).Length);
            }
        }

        [TestCase(2, 1)] [TestCase(1, 2)]
        public void DifferentCostsSpendTheSamePoolAndEndOnlyWhenNeitherActionIsAffordable(int playCost, int discardCost)
        {
            var s = Create(energy: 4, playCost: playCost, discardCost: discardCost);
            Act(s, false); Assert.AreEqual(4 - playCost, Energy(s));
            Act(s, true); Assert.AreEqual(1, Energy(s));
            var playback = new BattleSessionPlayback(s, new View());
            Assert.AreEqual(playCost == 1, playback.CanPlay(Player, Hand(s, 1)));
            Assert.AreEqual(discardCost == 1, playback.CanDiscard(Player, Hand(s, 1)));
            Act(s, discardCost == 1);
            Assert.AreEqual(1, s.State.Duel.CompletedRounds);
        }

        [Test] public void ZeroCostCardsRemainUsableAtZeroEnergyWithoutAnActionCountCeiling()
        {
            var s = Create(initial: 0, actions: 1, playCost: 0, discardCost: 0);
            for (int i = 0; i < 7; i++) Act(s, true);
            Assert.AreEqual(0, Energy(s)); Assert.AreEqual(0, s.State.FlowFor(Player).ActionsRemaining);
            Assert.AreEqual(BattlePlayerWindow.Action, s.State.FlowFor(Player).Window);
            Assert.AreEqual(0, s.State.Duel.CompletedRounds);
            Assert.IsTrue(s.Execute(s.State.Revision, Player, CardDuelAction.EndTurn).Accepted);
            Assert.AreEqual(1, s.State.Duel.CompletedRounds);
        }

        [Test] public void QueryPreviewAndExecutionRejectSixCardsWithoutChangingStateOrRandom()
        {
            var s = Create(); var before = s.State; var cards = Hand(s, 6);
            var playback = new BattleSessionPlayback(s, new View());
            Assert.AreEqual(5, s.State.Duel.Player(Player).Rules.MaxDiscardCards);
            Assert.IsFalse(playback.CanPlay(Player, cards)); Assert.IsFalse(playback.CanDiscard(Player, cards));
            Assert.AreEqual(CardDuelError.InvalidSelection, playback.PreviewPlay(Player, cards).Error);
            Assert.AreEqual(CardDuelError.InvalidSelection, playback.PreviewDiscard(Player, cards).Error);
            Assert.AreEqual(CardDuelError.InvalidSelection, s.Play(before.Revision, Player, cards).Error);
            Assert.AreEqual(CardDuelError.InvalidSelection, s.Discard(before.Revision, Player, cards).Error);
            Assert.AreSame(before, s.State);
            Assert.AreEqual(CardDuelError.StaleRevision, s.Discard(before.Revision - 1, Player, Hand(s, 1)).Error);
            Assert.AreSame(before, s.State);
        }

        [TestCase(false)] [TestCase(true)]
        public void FiveCardsAreAcceptedForEitherOrdinaryAction(bool discard)
        {
            var s = Create(); Act(s, discard, 5);
            Assert.AreEqual(discard ? 8 : 3, Hand(s).Length); Assert.AreEqual(2, Energy(s));
        }

        [TestCase(false)] [TestCase(true)]
        public void PreviewUsesTheSameCostAndCardsButNeverCommitsEnergyOrRandomState(bool discard)
        {
            var s = Create(energy: 6, actions: 1); var before = s.State; var cards = Hand(s, 2);
            var preview = discard ? s.Engine.PreviewDiscard(before, before.Revision, Player, cards) :
                s.Engine.PreviewPlay(before, before.Revision, Player, cards);
            Assert.IsTrue(preview.Accepted); Assert.AreSame(before, s.State);
            Assert.AreEqual(5, preview.State.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(5, preview.State.FlowFor(Player).ActionsRemaining);
            var committed = discard ? s.Discard(before.Revision, Player, cards) : s.Play(before.Revision, Player, cards);
            Assert.IsTrue(committed.Accepted); Assert.AreEqual(5, Energy(s));
            CollectionAssert.AreEqual(preview.State.Random.StateBytes, committed.Steps[0].Transition.State.Random.StateBytes);
            CollectionAssert.AreEqual(preview.State.Zone(CardZoneKinds.Hand, Player).Cards, s.State.Zone(CardZoneKinds.Hand, Player).Cards);
        }

        [TestCase(CardDuelAction.Draw)] [TestCase(CardDuelAction.SkipDraw)]
        [TestCase(CardDuelAction.DiscardForDraw)] [TestCase(CardDuelAction.ConfirmOverflow)]
        [TestCase(CardDuelAction.FinishTurn)]
        public void RetainedCommandsCannotOpenManualOrClosingDiscardWindowsInCurrentMode(CardDuelAction action)
        {
            var s = Create(); var before = s.State;
            Assert.AreEqual(CardDuelError.InvalidPhase, s.Execute(before.Revision, Player, action).Error);
            Assert.AreSame(before, s.State);
        }

        [Test] public void SpecialDrawOverflowCanRemainAtEndButOrdinaryDiscardDoesNotDrawPastTheLimit()
        {
            var s = Create(effect: new DrawEffect(), deckSize: 30); Act(s, false);
            Assert.AreEqual(17, Hand(s).Length);
            var result = Act(s, true, 5); Assert.AreEqual(12, Hand(s).Length);
            Assert.AreEqual(0, result.Steps.Sum(step => step.Transition.Draws.Sum(draw => draw.DrawnCount)));
            var held = Hand(s);
            Assert.IsTrue(s.Execute(s.State.Revision, Player, CardDuelAction.EndTurn).Accepted);
            CollectionAssert.AreEqual(held, Hand(s));
            Assert.AreEqual(BattlePlayerWindow.Action, s.State.FlowFor(Player).Window);
        }

        [Test] public void DiscardCanRecycleItsOwnCardsWithoutDrawingMoreThanWereDiscarded()
        {
            var s = Create(deckSize: 8);
            var result = Act(s, true, 5);
            Assert.AreEqual(8, Hand(s).Length);
            Assert.AreEqual(5, result.Steps[0].Transition.Draws.Single().DrawnCount);
            Assert.IsTrue(result.Timeline.Any(entry => entry.Kind == BattleTimelineKind.DeckRecycled));
        }

        [Test] public void OrdinaryRefillCannotBeOverriddenIntoRefillingPlayedCardsOrMissingCards()
        {
            var gear = RelicRuleTestData.Gear("refill", refills: new[] {
                new RelicRefillRuleSpec("after-play", CardDuelDealTrigger.AfterPlay, BattleRefillMode.FillToLimit),
                new RelicRefillRuleSpec("after-discard", CardDuelDealTrigger.AfterDiscard, BattleRefillMode.FillToLimit) });
            var s = Create(relics: new[] { gear });
            Act(s, false, 3); Assert.AreEqual(5, Hand(s).Length);
            var discard = Act(s, true); Assert.AreEqual(5, Hand(s).Length);
            Assert.AreEqual(1, discard.Steps[0].Transition.Draws.Single().DrawnCount);
        }

        [Test] public void IgnorePlayLimitRemainsLegacyOnlyAboveFiveCards()
        {
            var gear = new BattleRelicSetup(new RelicDefinition("ignore", Array.Empty<ScoreEffect>()), new RunRelic("ignore", "ignore"),
                new BattleRelicRules("ignore.1", programs: new[] { new RelicProgramSpec("ignore", RelicProgramKind.IgnorePlayLimit) }));
            var s = Create(relics: new[] { gear });
            Assert.AreEqual(5, s.Engine.PlayLimit(s.State, Player, false));
            Assert.AreEqual(CardDuelError.InvalidSelection, s.Play(s.State.Revision, Player, Hand(s, 6)).Error);
        }

        [Test] public void CurrentItemsUseEnergyAsTheirGateAndNeverSpendAnActionBudget()
        {
            var xml = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle-2026-10-21.xml"));
            var player = xml.Root.Element("Players").Element("Player");
            player.SetAttributeValue("maxEnergy", 6); player.SetAttributeValue("initialEnergy", 6);
            player.SetAttributeValue("actionsPerTurn", 1);
            xml.Root.Element("Loadouts").Elements().Single(e => (string)e.Attribute("id") == "baseline")
                .Add(new XElement("Relic", new XAttribute("ref", "10002"), new XAttribute("instanceId", "potion")));
            var content = BattleContentCompiler.CompileXml(xml.ToString()); Assert.IsTrue(content.Success);
            var s = BattleContentFactory.Create(content.Content, new BattleSetupRequest("standard-single-battle", "items-energy", HmacDeckShuffler.FromSeed(new byte[32]))).Session;
            var actor = new ParticipantId("player"); Assert.IsTrue(s.Execute(s.State.Revision, actor, CardDuelAction.Start).Accepted);
            for (int i = 0; i < 4; i++) Assert.IsTrue(s.Discard(s.State.Revision, actor, s.State.Zone(CardZoneKinds.Hand, actor).Cards.Take(1)).Accepted);
            var instance = s.State.Duel.Player(actor).Relics.Relics.Single().InstanceId;
            Assert.IsTrue(BattleItemRuntime.CanUse(s.State, actor, instance));
            var before = s.State.Duel.Player(actor).Energy.Energy;
            Assert.IsTrue(s.UseItem(s.State.Revision, actor, instance).Accepted);
            Assert.AreEqual(before, s.State.Duel.Player(actor).Energy.Energy);
            Assert.AreEqual(before, s.State.FlowFor(actor).ActionsRemaining);
            Assert.AreEqual(0, s.State.Duel.CompletedRounds);
        }

        [Test] public void ExhaustedPlayerEndsIndependentlyBeforeTheOtherPlayerAndEnemyPhase()
        {
            var s = Create(energy: 1, players: 2); Act(s, true);
            Assert.IsTrue(s.State.Duel.Player(Player).HasEnded);
            Assert.IsFalse(s.State.Duel.Player(new ParticipantId("ally")).HasEnded);
            Assert.AreEqual(0, Energy(s)); Assert.AreEqual(0, s.State.Duel.CompletedRounds);
            var ally = new ParticipantId("ally");
            Assert.IsTrue(s.Discard(s.State.Revision, ally, s.State.Zone(CardZoneKinds.Hand, ally).Cards.Take(1)).Accepted);
            Assert.AreEqual(1, s.State.Duel.CompletedRounds); Assert.AreEqual(1, Energy(s));
        }

        [TestCase("Engine/Content/final.xml")]
        [TestCase("Engine/Content/StandardSingleBattle-2026-10-21.xml")]
        [TestCase("Engine/Content/SingleChapterBattle.xml")]
        public void CurrentXmlUsesEnergyAndFiveCardLimitsWhileLegacyXmlStillLoads(string path)
        {
            var source = ToolkitTestFiles.Read(path);
            var compiled = BattleContentCompiler.CompileXml(source);
            Assert.IsTrue(compiled.Success, string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
            var player = compiled.Content.Players["hero"];
            Assert.IsTrue(player.Rules.Flow.EnergyOnly); Assert.AreEqual(5, player.Rules.MaxPlayCards);
            Assert.AreEqual(5, player.Rules.MaxDiscardCards); Assert.IsFalse(player.Rules.Flow.ManualDraw);
            Assert.IsTrue(player.Rules.Flow.AutoDrawToLimit); Assert.IsFalse(player.Rules.Flow.EndDefense);
            Assert.IsFalse(player.Rules.Flow.ForceDiscardOverflow);
            var old = XDocument.Parse(source); old.Root.Element("Players").Element("Player").Attribute("energyOnly").Remove();
            var legacy = BattleContentCompiler.CompileXml(old.ToString());
            Assert.IsTrue(legacy.Success); Assert.IsFalse(legacy.Content.Players["hero"].Rules.Flow.EnergyOnly);
            Assert.AreNotEqual(compiled.Content.Fingerprint, legacy.Content.Fingerprint);
        }
    }
}
