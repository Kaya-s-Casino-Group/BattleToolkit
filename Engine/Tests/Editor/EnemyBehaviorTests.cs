using System;
using System.Linq;
using NUnit.Framework;
namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class EnemyBehaviorTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), Enemy = new ParticipantId("enemy");
        private static BattleSession Load(string file, Func<string, string> transform = null, bool standard = false)
        {
            var xml = ToolkitTestFiles.Read(standard ? "Engine/Content/StandardSingleBattle.xml" : "Engine/Content/Templates/" + file + ".xml");
            var encounter = standard
                ? file == "07_RotationGrunt" ? "standard-single-battle"
                  : file == "08_ArmoredSummoner" ? "standard-armored-summoner" : "standard-gluttony-boss"
                : file;
            var compiled = BattleContentCompiler.CompileXml(transform == null ? xml : transform(xml));
            Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics.Select(d => d.Message)));
            var built = BattleContentFactory.Create(compiled.Content, new BattleSetupRequest(encounter, "enemy-test", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(built.Success, string.Join("\n", built.Diagnostics.Select(d => d.Message)));
            var session = built.Session;
            var start = session.Execute(session.State.Revision, Player, CardDuelAction.Start);
            Assert.IsTrue(start.Accepted, start.Error.ToString());
            return session;
        }
        private static BattleSessionResult Round(BattleSession session)
        {
            if (session.State.FlowFor(Player).Window == BattlePlayerWindow.Draw)
                Assert.IsTrue(session.Execute(session.State.Revision, Player, CardDuelAction.SkipDraw).Accepted);
            var result = session.Execute(session.State.Revision, Player, CardDuelAction.EndTurn);
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            if (session.State.FlowFor(Player).Window == BattlePlayerWindow.Ending && !session.State.Duel.IsFinished)
            {
                result = session.Execute(session.State.Revision, Player, CardDuelAction.FinishTurn);
                Assert.IsTrue(result.Accepted, result.Error.ToString());
            }
            return result;
        }
        [TestCase(false)]
        [TestCase(true)]
        public void RotationTemplateLocksIntentAndCyclesAttackAttackBlock(bool standard)
        {
            var session = Load("07_RotationGrunt", standard: standard);
            foreach (string action in new[] { "jab", "strike", "guard", "jab" })
            {
                var before = session.State;
                Assert.AreEqual(action, before.Duel.Enemies[0].BehaviorState.Intent.Id);
                var a = session.Engine.Duel.PreviewEnemyAction(before.Duel, Enemy);
                var b = session.Engine.Duel.PreviewEnemyAction(before.Duel, Enemy);
                Assert.IsTrue(a.Accepted); Assert.IsTrue(b.Accepted);
                CollectionAssert.AreEqual(a.Hits.Select(h => h.DamageToHealth), b.Hits.Select(h => h.DamageToHealth));
                Assert.AreSame(before, session.State);
                Round(session);
            }
            Assert.AreEqual(90, session.State.Duel.Combat.Get(Enemy).Block);
            Assert.AreEqual(650, session.State.Duel.Combat.Get(Player).Health);
        }
        [TestCase(false)]
        [TestCase(true)]
        public void SummonerTemplateCapsRosterAndNewUnitsWaitUntilNextRound(bool standard)
        {
            var session = Load("08_ArmoredSummoner", standard: standard);
            Assert.AreEqual(3, session.State.Duel.Combat.Statuses.Single(s => s.DefinitionId.Value == "layer-armor").Stacks);
            var result = Round(session);
            Assert.AreEqual(3, session.State.Duel.Enemies.Count);
            Assert.AreEqual(800, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(2, result.Timeline.Count(e => e.Kind == BattleTimelineKind.EnemySummoned));
            Assert.AreEqual("strike", session.State.Duel.Enemies[0].BehaviorState.Intent.Id);
            Round(session);
            Assert.AreEqual(630, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(3, session.State.Duel.Enemies.Count);
        }
        [TestCase(false)]
        [TestCase(true)]
        public void GluttonyTemplateCreatesUniqueBattleLocalJunkAndPreviewDoesNotPollute(bool standard)
        {
            var session = Load("09_GluttonyBoss", standard: standard);
            for (int round = 0; round < 3; round++)
            {
                var before = session.State;
                Assert.IsTrue(session.Engine.Duel.PreviewEnemyAction(before.Duel, Enemy).Accepted);
                Assert.AreEqual(52 + round, before.Table.Cards.Count);
                var result = Round(session);
                Assert.AreEqual(53 + round, session.State.Table.Cards.Count);
                var generated = session.State.Table.Cards.Where(c => c.OriginId == "enemy-generated").ToArray();
                Assert.AreEqual(round + 1, generated.Length);
                Assert.IsTrue(generated.All(c => !c.CanPlay && c.CanDiscard && !c.CanScore));
                Assert.IsTrue(generated.All(c => session.State.Zone(CardZoneKinds.Discard, Player).Cards.Contains(c.Id)));
                Assert.IsTrue(result.Timeline.Any(e => e.Kind == BattleTimelineKind.CardsCreated));
            }
            Assert.AreEqual(session.State.Table.Cards.Count, session.State.Table.Cards.Select(c => c.Id).Distinct().Count());
        }
        [TestCase(false)]
        [TestCase(true)]
        public void BossPhaseSwitchesAtNextPlanningBoundaryAndIntentRemainsLocked(bool standard)
        {
            var session = Load("09_GluttonyBoss", xml => {
                var doc = new System.Xml.XmlDocument(); doc.LoadXml(xml);
                var effect = (System.Xml.XmlElement)doc.SelectSingleNode("//Hand[@category='HighCard']/Effect");
                effect.SetAttribute("amount", "1300"); effect.SetAttribute("amountKind", "Constant");
                return doc.OuterXml;
            }, standard: standard);
            Assert.IsTrue(session.Execute(session.State.Revision, Player, CardDuelAction.Draw, drawCount: 1).Accepted);
            Assert.IsTrue(session.Execute(session.State.Revision, Player, CardDuelAction.Play, session.State.Zone(CardZoneKinds.Hand, Player).Cards).Accepted);
            Assert.AreEqual("normal", session.State.Duel.Enemies[0].BehaviorState.StageId);
            Round(session);
            Assert.AreEqual(1, session.State.Table.Cards.Count - 52);
            Assert.AreEqual("hungry", session.State.Duel.Enemies[0].BehaviorState.StageId);
            Assert.AreEqual(3, session.State.Duel.Enemies[0].BehaviorState.Intent.WorldEffects.Single().Count);
        }
        [TestCase(false)]
        [TestCase(true)]
        public void ArmorConsumesOneLayerPerHitAndFourthHitDealsFullDamage(bool standard)
        {
            var session = Load("08_ArmoredSummoner", standard: standard); var state = session.State.Duel;
            var hits = Enumerable.Range(0, 4).Select(_ => new CombatEffect(CombatEffectKind.Damage, Player, Enemy, 100));
            var result = CombatEngine.Resolve(state.Combat, state.Combat.Revision, hits, statusCatalog: session.Engine.Duel.Definition.StatusCatalog);
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            CollectionAssert.AreEqual(new long[] { 25, 25, 25, 100 }, result.Events.Select(e => e.SettledDamage));
            Assert.IsFalse(result.State.Statuses.Any(s => s.DefinitionId.Value == "layer-armor"));
            Assert.AreEqual(3, result.RuleEvents.OfType<CombatStatusChangedEvent>().Count());
        }
        [TestCase("07_RotationGrunt", false)]
        [TestCase("08_ArmoredSummoner", false)]
        [TestCase("09_GluttonyBoss", false)]
        [TestCase("07_RotationGrunt", true)]
        [TestCase("08_ArmoredSummoner", true)]
        [TestCase("09_GluttonyBoss", true)]
        public void RealTemplatesReachAnOutcomeWithoutDeadlock(string file, bool standard)
        {
            var session = Load(file, standard: standard);
            for (int i = 0; i < 24 && !session.State.Duel.IsFinished; i++) Round(session);
            Assert.IsTrue(session.State.Duel.IsFinished);
            Assert.AreEqual(ScoredDuelPhase.Lost, session.State.Duel.Phase);
        }
        [TestCase("enemyRef=\"rotator\"", "enemyRef=\"missing\"")]
        [TestCase("livingLimit=\"3\"", "livingLimit=\"0\"")]
        [TestCase("mode=\"Priority\"", "mode=\"Typo\"")]
        public void InvalidBehaviorConfigurationIsRejected(string before, string after)
        {
            var xml = ToolkitTestFiles.Read("Engine/Content/Templates/08_ArmoredSummoner.xml").Replace(before, after);
            Assert.IsFalse(BattleContentCompiler.CompileXml(xml).Success);
        }
        private static BattleEffectSequence Hit(long amount) => new BattleEffectSequence("test", "攻击", new[] {
            new BattleEffectStep("hit", BattleEffectSpec.Damage(amount), EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving)) });
        private static TeamBattleState Team(TeamBattleEngine engine, EnemyBehaviorDefinition behavior, string id = "behavior") => engine.Create(id,
            new[] { new BattlePlayerSetup(Player, new CombatantDefinition("player", 10000)) },
            new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("enemy", 10000), 0, behavior: behavior) });
        private static TeamBattleState Next(TeamBattleEngine engine, TeamBattleState state)
        {
            var end = engine.Execute(state, state.Revision, Player, ScoredDuelActionKind.EndPlayerTurn);
            Assert.IsTrue(end.Accepted, end.Error.ToString());
            var result = engine.Execute(end.State, end.State.Revision, Enemy, ScoredDuelActionKind.EnemyAction);
            Assert.IsTrue(result.Accepted, result.Error.ToString()); return result.State;
        }
        [Test] public void PriorityCooldownAndMaximumUsesCountOnlyCompletedActions()
        {
            var behavior = new EnemyBehaviorDefinition(new[] { new EnemyBehaviorStage("stage", EnemySelectionMode.Priority, new[] {
                new EnemyBehaviorAction("burst", "爆发", Hit(10), cooldownRounds: 1, maxUses: 2),
                new EnemyBehaviorAction("normal", "普攻", Hit(1)) }) });
            var engine = new TeamBattleEngine(new BattleRules("test", 20)); var state = Team(engine, behavior);
            foreach (var expected in new[] { "burst", "normal", "burst", "normal", "normal" })
            {
                Assert.AreEqual(expected, state.Enemies[0].BehaviorState.Intent.Id);
                engine.PreviewEnemyAction(state, Enemy); engine.PreviewEnemyAction(state, Enemy);
                var stale = engine.Execute(state, state.Revision + 1, Player, ScoredDuelActionKind.EndPlayerTurn);
                Assert.IsFalse(stale.Accepted); Assert.AreSame(state, stale.State);
                state = Next(engine, state);
            }
            Assert.AreEqual(2, state.Enemies[0].BehaviorState.Uses["stage/burst"]);
        }
        [Test] public void WeightedSelectionIsStableAndCanChooseBothBranches()
        {
            var behavior = new EnemyBehaviorDefinition(new[] { new EnemyBehaviorStage("stage", EnemySelectionMode.Weighted, new[] {
                new EnemyBehaviorAction("a", "A", Hit(1)), new EnemyBehaviorAction("b", "B", Hit(2), weight: 2) }) });
            var engine = new TeamBattleEngine(new BattleRules("test", 20));
            var picks = Enumerable.Range(0, 30).Select(i => {
                var a = Team(engine, behavior, "weighted" + i); var b = Team(engine, behavior, "weighted" + i);
                Assert.AreEqual(a.Enemies[0].BehaviorState.Intent.Id, b.Enemies[0].BehaviorState.Intent.Id);
                return a.Enemies[0].BehaviorState.Intent.Id;
            }).Distinct().ToArray();
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, picks);
        }
        [Test] public void NestedConditionsAndNoEligibleActionsProduceAWait()
        {
            var condition = new EnemyCondition(EnemyConditionKind.All, children: new[] {
                new EnemyCondition(EnemyConditionKind.RoundAtLeast, 2),
                new EnemyCondition(EnemyConditionKind.Not, children: new[] { new EnemyCondition(EnemyConditionKind.HealthAtMost, 0.5m) }) });
            var behavior = new EnemyBehaviorDefinition(new[] { new EnemyBehaviorStage("stage", EnemySelectionMode.Priority,
                new[] { new EnemyBehaviorAction("ready", "准备完毕", Hit(1), condition: condition) }) });
            var engine = new TeamBattleEngine(new BattleRules("test", 20)); var state = Team(engine, behavior);
            Assert.IsNull(state.Enemies[0].BehaviorState.Intent);
            state = Next(engine, state);
            Assert.AreEqual("ready", state.Enemies[0].BehaviorState.Intent.Id);
            Assert.AreEqual(10000, state.Combat.Get(Player).Health);
        }
        [Test] public void MultipleArmoredEnemiesReceiveDistinctOpeningStatusInstances()
        {
            var session = Load("08_ArmoredSummoner", xml => xml.Replace("<Enemy id=\"enemy\" ref=\"summoner\" />", "<Enemy id=\"enemy\" ref=\"summoner\"/><Enemy id=\"second\" ref=\"summoner\"/>"));
            var armor = session.State.Duel.Combat.Statuses.Where(s => s.DefinitionId.Value == "layer-armor").ToArray();
            Assert.AreEqual(2, armor.Length); Assert.AreEqual(2, armor.Select(s => s.Id).Distinct().Count());
        }
        [Test] public void JunkRecyclesCanBeDiscardedAndCannotBePlayed()
        {
            var session = Load("09_GluttonyBoss", xml => {
                var doc = new System.Xml.XmlDocument(); doc.LoadXml(xml);
                var deck = doc.SelectSingleNode("//Deck");
                while (deck.ChildNodes.Count > 1) deck.RemoveChild(deck.LastChild);
                return doc.OuterXml;
            });
            // Take the only original card, then the next draw must recycle the generated junk.
            Assert.IsTrue(session.Execute(session.State.Revision, Player, CardDuelAction.Draw, drawCount: 1).Accepted);
            Round(session);
            Assert.IsTrue(session.Execute(session.State.Revision, Player, CardDuelAction.Draw, drawCount: 1).Accepted);
            var junk = session.State.Zone(CardZoneKinds.Hand, Player).Cards.Where(id => !session.State.Table.GetCard(id).CanPlay).ToArray();
            Assert.AreEqual(1, junk.Length);
            var before = session.State;
            Assert.IsFalse(session.Execute(before.Revision, Player, CardDuelAction.Play, junk).Accepted);
            Assert.AreSame(before, session.State);
            Assert.IsTrue(session.Execute(before.Revision, Player, CardDuelAction.Discard, junk).Accepted);
        }

        [TestCase(EnemyDeadTargetPolicy.Retarget, 90)]
        [TestCase(EnemyDeadTargetPolicy.Skip, 100)]
        public void DeadLockedTargetUsesConfiguredFallback(EnemyDeadTargetPolicy fallback, int expectedHealth)
        {
            var second = new ParticipantId("second-player"); var firstEnemy = new ParticipantId("first-enemy");
            var behavior = new EnemyBehaviorDefinition(new[] { new EnemyBehaviorStage("stage", EnemySelectionMode.Sequence,
                new[] { new EnemyBehaviorAction("hit", "攻击", Hit(10), deadTargetPolicy: fallback) }) });
            var engine = new TeamBattleEngine(new BattleRules("test", 20));
            var state = engine.Create("target-test", new[] {
                new BattlePlayerSetup(Player, new CombatantDefinition("p", 50)), new BattlePlayerSetup(second, new CombatantDefinition("p", 100)) },
                new[] { new BattleEnemySetup(firstEnemy, new CombatantDefinition("e", 100), 100),
                    new BattleEnemySetup(Enemy, new CombatantDefinition("e", 100), 0, behavior: behavior) });
            Assert.AreEqual(Player, state.Enemies[1].BehaviorState.Targets[0].Single());
            foreach (var player in new[] { Player, second }) {
                var end = engine.Execute(state, state.Revision, player, ScoredDuelActionKind.EndPlayerTurn); Assert.IsTrue(end.Accepted); state = end.State;
            }
            var kill = engine.Execute(state, state.Revision, firstEnemy, ScoredDuelActionKind.EnemyAction); Assert.IsTrue(kill.Accepted); state = kill.State;
            Assert.AreEqual(Player, state.Enemies[1].BehaviorState.Targets[0].Single());
            var hit = engine.Execute(state, state.Revision, Enemy, ScoredDuelActionKind.EnemyAction); Assert.IsTrue(hit.Accepted, hit.Error.ToString());
            Assert.AreEqual(expectedHealth, hit.State.Combat.Get(second).Health);
        }
    }
}
