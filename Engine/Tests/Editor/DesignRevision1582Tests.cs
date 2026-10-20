using System;
using System.Linq;
using System.Xml;
using NUnit.Framework;
namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class DesignRevision1582Tests
    {
        private static readonly ParticipantId P = new ParticipantId("player"), E = new ParticipantId("enemy");
        private static BattleSession Load(Action<XmlDocument> edit = null, string encounter = "standard-single-battle", bool start = true)
        {
            var doc = new XmlDocument(); doc.LoadXml(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle.xml")); edit?.Invoke(doc);
            var content = BattleContentCompiler.CompileXml(doc.OuterXml); Assert.IsTrue(content.Success, string.Join("; ", content.Diagnostics.Select(d => d.Message)));
            var built = BattleContentFactory.Create(content.Content, new BattleSetupRequest(encounter, "revision-1582", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(built.Success); if (start) Act(built.Session, CardDuelAction.Start); return built.Session;
        }
        private static void Attr(XmlDocument d, string path, string name, string value) => ((XmlElement)d.SelectSingleNode(path)).SetAttribute(name, value);
        private static void Gear(XmlDocument d, string effects)
        {
            var relic = d.CreateElement("Relic"); relic.SetAttribute("id", "verification"); relic.InnerXml = effects; d.SelectSingleNode("/BattleContent/Relics").AppendChild(relic);
            var slot = d.CreateElement("Relic"); slot.SetAttribute("instanceId", "check"); slot.SetAttribute("ref", "verification"); d.SelectSingleNode("//Loadout[@id='baseline']").AppendChild(slot);
        }
        private static BattleSessionResult Act(BattleSession s, CardDuelAction action, CardInstanceId[] cards = null, int count = 0)
        { var r = s.Execute(s.State.Revision, P, action, cards, drawCount: count); Assert.IsTrue(r.Accepted, action + ": " + r.Error); return r; }
        private static CardInstanceId[] Hand(BattleSession s) => s.State.Zone(CardZoneKinds.Hand, P).Cards.ToArray();
        private static string Hook(string id, string timing, string effect) => "<BoundaryTrigger id='" + id + "' timing='" + timing + "' phase='OwnerTeam'>" + effect + "</BoundaryTrigger>";
        private const string Shield = "<Effect kind='GainBlock' amount='1' target='Self'/>";
        [TestCase(3, HandCategory.Flush)] [TestCase(4, HandCategory.Flush)] [TestCase(2, HandCategory.HighCard)]
        public void ShortFlushIsConfigurableAndDoesNotChangeHoldem(int count, HandCategory expected)
        {
            var cards = new[] { Rank.Two, Rank.Four, Rank.Seven, Rank.Nine }.Take(count).Select((r, i) => new EffectiveCard(new CardInstanceId("c" + i), r, Suit.Hearts)).ToArray();
            Assert.AreEqual(expected, PokerHandClassifier.Classify(cards, 3).Category);
            Assert.AreEqual(HandCategory.HighCard, PokerHandClassifier.Classify(cards).Category);
        }
        [Test] public void ShortFlushDoesNotAccidentallyEnableShortStraightFlush()
        {
            var cards = new[] { Rank.Ace, Rank.Two, Rank.Three }.Select((r, i) => new EffectiveCard(new CardInstanceId("c" + i), r, Suit.Hearts)).ToArray();
            Assert.AreEqual(HandCategory.Flush, PokerHandClassifier.Classify(cards, 3).Category);
            Assert.AreEqual(HandCategory.Straight, PokerHandClassifier.Classify(cards, 5, 3).Category);
        }
        [Test] public void RelicCanPermitRepeatedDefensiveDiscardsWithinActionBudget()
        {
            var s = Load(d => Gear(d, "<RuleValue id='continue' key='DiscardEndsTurn' delta='-1'/>")); Act(s, CardDuelAction.SkipDraw);
            Act(s, CardDuelAction.Discard, Hand(s).Take(1).ToArray()); Act(s, CardDuelAction.Discard, Hand(s).Take(1).ToArray());
            Assert.AreEqual(0, s.State.Duel.CompletedRounds); Assert.AreEqual(1, s.State.FlowFor(P).ActionsRemaining);
            Act(s, CardDuelAction.Discard, Hand(s).Take(1).ToArray());
            Assert.AreEqual(1, s.State.Duel.CompletedRounds);
        }
        [Test] public void ParameterMultipliersFollowAllAdditionsAndRetainBounds()
        {
            var s = Load(d => Gear(d, "<RuleValue id='double' key='ActionsPerTurn' multiplier='2'/><RuleValue id='add' key='ActionsPerTurn' delta='1'/><RuleValue id='flush' key='MinFlushCards' delta='-100'/>"));
            Assert.AreEqual(8, s.State.FlowFor(P).ActionsRemaining); Assert.AreEqual(8, Hand(s).Length);
        }
        [Test] public void PlayPhaseEndRunsOnceBeforeOverflowAndCountsRemainingHand()
        {
            var s = Load(d => Gear(d, Hook("closing", "PlayPhaseEnd", "<Effect kind='DrawCards' amount='2' target='Self'/><Effect kind='GainBlock' amount='0' amountPerHandCard='2' target='Self'/>")));
            var end = Act(s, CardDuelAction.EndTurn);
            Assert.AreEqual(BattlePlayerWindow.ForcedDiscard, s.State.FlowFor(P).Window); Assert.AreEqual(10, Hand(s).Length);
            Assert.AreEqual(16, s.State.Duel.Combat.Get(P).Block);
            Assert.AreEqual(1, end.Steps.SelectMany(x => x.Transition.RelicActivations).Count(a => a.TriggerId == "closing"));
            var overflow = Act(s, CardDuelAction.ConfirmOverflow, Hand(s).Take(2).ToArray());
            Assert.IsFalse(overflow.Steps.SelectMany(x => x.Transition.RelicActivations).Any(a => a.TriggerId == "closing"));
            Assert.AreEqual(8, Hand(s).Length); Assert.AreEqual(1, s.State.Duel.CompletedRounds);
            Assert.IsTrue(overflow.Steps.SelectMany(x => x.Transition.Events).Any(ev => ev.Effect.Kind == CombatEffectKind.GainBlock));
        }
        [Test] public void AutomaticActionExhaustionAlsoDispatchesClosingOnce()
        {
            var s = Load(d => Gear(d, "<RuleValue id='one' key='ActionsPerTurn' delta='-2'/>" + Hook("closing", "PlayPhaseEnd", Shield)));
            Act(s, CardDuelAction.SkipDraw); var r = Act(s, CardDuelAction.Play, Hand(s).Take(1).ToArray());
            Assert.AreEqual(1, r.Steps.SelectMany(x => x.Transition.RelicActivations).Count(a => a.TriggerId == "closing"));
            Assert.AreEqual(1, s.State.Duel.CompletedRounds);
        }
        [Test] public void DrawAndReshuffleHooksHaveCardFactsAndDoNotSpendManualDrawOnOpening()
        {
            var s = Load(d => Gear(d, Hook("before", "BeforeDraw", Shield) + Hook("draw", "Draw", Shield) + Hook("shuffle", "Reshuffle", Shield)), start: false);
            var opening = Act(s, CardDuelAction.Start);
            Assert.AreEqual(1, s.State.FlowFor(P).DrawUsesRemaining);
            CollectionAssert.AreEqual(new[] { "before", "draw" }, opening.Steps.SelectMany(x => x.Transition.RelicActivations).Select(x => x.TriggerId));
            var clock = opening.Steps.SelectMany(x => x.Transition.RuleEvents).OfType<CombatTimingEvent>().Single(x => x.Signal.Kind == CombatTimingKind.Draw);
            Assert.AreEqual(8, clock.Signal.Cards.Count); Assert.AreEqual(8, clock.Signal.HandCount);
            // Repeated pre-draw discards empty the full deck while preserving a valid draw window each turn.
            bool recycled = false;
            for (int i = 0; i < 12 && !recycled; i++) {
                if (Hand(s).Length > 3) Act(s, CardDuelAction.DiscardForDraw, Hand(s));
                var draw = Act(s, CardDuelAction.Draw, count: 5);
                recycled = draw.Steps.SelectMany(x => x.Transition.RelicActivations).Any(x => x.TriggerId == "shuffle");
                if (!recycled) Act(s, CardDuelAction.EndTurn);
            }
            Assert.IsTrue(recycled);
        }
        [Test] public void EffectDrawChainStopsAtEmptyDeckWithoutManufacturingCards()
        {
            var s = Load(d => Gear(d, Hook("loop", "Draw", "<Effect kind='DrawCards' amount='1' target='Self'/>")), start: false);
            // Finite 52-card deck ends naturally; no manufactured cards or manual draw usage.
            var r = Act(s, CardDuelAction.Start); Assert.AreEqual(52, Hand(s).Length); Assert.AreEqual(1, s.State.FlowFor(P).DrawUsesRemaining);
            Assert.AreEqual(52, s.State.Table.Cards.Count);
        }
        [Test] public void EnrageStacksAfterRoundTwentyAndPreviewIsPure()
        {
            var s = Load(d => { Attr(d, "//Players/Player", "maxHealth", "100000"); Attr(d, "//Rules", "enrageDamagePerRound", "7"); });
            for (int i = 0; i < 20; i++) Act(s, CardDuelAction.EndTurn);
            Assert.IsFalse(s.State.Duel.IsFinished); Assert.AreEqual(20, s.State.Duel.CompletedRounds);
            var before = s.State;
            var preview = s.Engine.Duel.PreviewEnemyAction(before.Duel, E);
            // Round 21 is the guard action; round 22 returns to jab (40 + 14).
            Act(s, CardDuelAction.EndTurn);
            Assert.AreEqual(54, s.Engine.Duel.PreviewEnemyAction(s.State.Duel, E).Hits.Sum(h => h.SettledDamage));
            Assert.AreEqual(20, before.Duel.CompletedRounds);
        }
        [Test] public void BossScheduleHasEightSlotsAndThresholdRefreshesImmediately()
        {
            var s = Load(d => { Attr(d, "//Hand[@category='HighCard']/Effect", "amount", "1300"); Attr(d, "//Hand[@category='HighCard']/Effect", "amountKind", "Constant"); }, "standard-gluttony-boss");
            var before = s.State; var forecast = s.Engine.Duel.PreviewEnemyIntents(before.Duel, E);
            Assert.AreEqual(8, forecast.Count); Assert.IsTrue(forecast.All(a => a.Id == "pollute")); Assert.AreSame(before, s.State);
            Act(s, CardDuelAction.SkipDraw); Act(s, CardDuelAction.Play, Hand(s).Take(1).ToArray());
            Assert.AreEqual("hungry", s.State.Duel.Enemies[0].BehaviorState.StageId);
            Assert.IsTrue(s.Engine.Duel.PreviewEnemyIntents(s.State.Duel, E).All(a => a.Id == "devour"));
            Assert.IsEmpty(s.State.Duel.Enemies[0].BehaviorState.Uses);
        }
        [Test] public void FailedSummonDoesNotSpendUseOrSequenceCursor()
        {
            var s = Load(d => { var action = d.SelectSingleNode("//Enemy[@id='armored-summoner']/Behavior/Stage/Action[@id='summon']");
                // Locate by action ID to keep template naming independent.
                action = d.SelectSingleNode("//Action[@id='summon']");
                foreach (XmlNode n in action.SelectNodes("When")) action.RemoveChild(n);
                ((XmlElement)action).SetAttribute("maxUses", "1"); ((XmlElement)action.SelectSingleNode("Summon")).SetAttribute("livingLimit", "1");
            }, "standard-armored-summoner");
            Act(s, CardDuelAction.EndTurn); Act(s, CardDuelAction.EndTurn);
            Assert.AreEqual(1, s.State.Duel.Enemies.Count); Assert.AreEqual("summon", s.State.Duel.Enemies[0].BehaviorState.Intent.Id);
            Assert.IsEmpty(s.State.Duel.Enemies[0].BehaviorState.Uses); Assert.AreEqual(0, s.State.Duel.Enemies[0].BehaviorState.SequenceCursor);
        }

        [Test] public void EffectDiscardCarriesItsKindAndDoesNotSpendPlayerAction()
        {
            var s = Load(d => Gear(d, "<ActionTrigger id='effect' timing='DiscardCompleted' discardKind='Effect'>" + Shield + "</ActionTrigger>"));
            var before = s.State;
            var result = s.Engine.DiscardByEffect(before, before.Revision, P, Hand(s).Take(1));
            Assert.IsTrue(result.Accepted, result.Error.ToString()); Assert.AreEqual(BattleDiscardKind.Effect, result.ActionCompletion.Context.DiscardKind);
            Assert.AreEqual(3, result.State.FlowFor(P).ActionsRemaining); Assert.AreEqual(BattlePlayerWindow.Draw, result.State.FlowFor(P).Window);
            Assert.AreEqual(1, result.State.Duel.Combat.Get(P).Block); Assert.AreEqual(7, result.State.Zone(CardZoneKinds.Hand, P).Cards.Count);
            Assert.AreSame(before, s.State);
            Assert.AreEqual(CardDuelError.StaleRevision, s.Engine.DiscardByEffect(result.State, before.Revision, P, Hand(s).Take(1)).Error);
        }
        [Test] public void BeforeDrawRecursionRejectsAndRollsBackOpening()
        {
            var s = Load(d => Gear(d, Hook("loop", "BeforeDraw", "<Effect kind='DrawCards' amount='1' target='Self'/>")), start: false);
            var before = s.State; var result = s.Execute(before.Revision, P, CardDuelAction.Start);
            Assert.AreEqual(CardDuelError.Overflow, result.Error); Assert.AreSame(before, s.State); Assert.IsEmpty(Hand(s));
            Assert.AreEqual(0, s.State.Duel.Player(P).Relics.Relics.Single().UsesThisRound);
        }
        [Test] public void LethalBeforeDrawStopsDealingAndFiresBattleEndExactlyOnce()
        {
            var s = Load(d => Gear(d, Hook("win", "BeforeDraw", "<Effect kind='Damage' amount='100000' target='Opponents'/>") +
                "<BoundaryTrigger id='end' timing='BattleEnded'>" + Shield + "</BoundaryTrigger>"), start: false);
            var r = Act(s, CardDuelAction.Start); Assert.AreEqual(ScoredDuelPhase.Won, s.State.Duel.Phase); Assert.IsEmpty(Hand(s));
            Assert.AreEqual(1, r.Steps.SelectMany(x => x.Transition.RelicActivations).Count(a => a.TriggerId == "end"));
        }
        [Test] public void SummonLimitBelongsToEachSummonerInsteadOfWholeBattlefield()
        {
            var s = Load(d => {
                var action = d.SelectSingleNode("//Action[@id='summon']"); foreach (XmlNode n in action.SelectNodes("When")) action.RemoveChild(n);
                var encounter = d.SelectSingleNode("//Encounter[@id='standard-armored-summoner']"); var slot = (XmlElement)encounter.SelectSingleNode("Enemy").CloneNode(true); slot.SetAttribute("id", "other"); encounter.AppendChild(slot);
            }, "standard-armored-summoner");
            Act(s, CardDuelAction.EndTurn); Assert.AreEqual(6, s.State.Duel.Enemies.Count);
            Assert.AreEqual(2, s.State.Duel.Enemies.Count(e => e.SummonerId == E)); Assert.AreEqual(2, s.State.Duel.Enemies.Count(e => e.SummonerId == new ParticipantId("other")));
            Assert.AreEqual(800, s.State.Duel.Combat.Get(P).Health);
        }
        [Test] public void ShieldRetentionRelicOverridesPlayerExpiryOnly()
        {
            var s = Load(d => Gear(d, "<RuleValue id='retain' key='RetainBlock' delta='1'/>" + Hook("shield", "PlayPhaseEnd", "<Effect kind='GainBlock' amount='100' target='Self'/>")));
            Act(s, CardDuelAction.EndTurn); Assert.AreEqual(60, s.State.Duel.Combat.Get(P).Block);
            Act(s, CardDuelAction.EndTurn); Assert.AreEqual(90, s.State.Duel.Combat.Get(P).Block);
            Act(s, CardDuelAction.EndTurn); Assert.AreEqual(0, s.State.Duel.Combat.Get(E).Block); Assert.AreEqual(190, s.State.Duel.Combat.Get(P).Block);
        }
    }
}
