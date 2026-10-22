using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class SingleChapterEnemyRulesTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), Body = new ParticipantId("enemy");

        private static CompiledBattleContent Content()
        {
            var compiled = BattleContentCompiler.CompileXml(ToolkitTestFiles.Read("Engine/Content/SingleChapterBattle.xml"));
            Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics.Select(d => d.Message)));
            return compiled.Content;
        }

        private static BattleSession Battle(string encounter, bool durablePlayer = false)
        {
            var built = BattleContentFactory.Create(Content(), new BattleSetupRequest(encounter,
                "single-chapter-enemy-rules", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(built.Success, string.Join("\n", built.Diagnostics.Select(d => d.Message)));
            Assert.IsTrue(built.Session.Execute(0, Player, CardDuelAction.Start).Accepted);
            if (durablePlayer)
            {
                // Long rule checks need enough player health to observe the enemy's unchanged behavior.
                var edited = built.Session.ApplyAdmin(built.Session.State.Revision,
                    new BattleAdminPatch(new[] { new BattleAdminVitals(Player, 100000, 100000) }));
                Assert.IsTrue(edited.Accepted, edited.Error.ToString());
            }
            return built.Session;
        }

        private static TeamBattleState Round(TeamBattleEngine engine, TeamBattleState state)
        {
            var end = engine.Execute(state, state.Revision, Player, state.IsActionBlocked(Player)
                ? ScoredDuelActionKind.SkipPlayerTurn : ScoredDuelActionKind.EndPlayerTurn);
            Assert.IsTrue(end.Accepted, end.Error.ToString()); state = end.State;
            while (state.Phase == ScoredDuelPhase.AwaitingEnemyAction)
            {
                var next = engine.Execute(state, state.Revision, state.NextEnemy, ScoredDuelActionKind.EnemyAction);
                Assert.IsTrue(next.Accepted, next.Error.ToString()); state = next.State;
            }
            return state;
        }

        private static int Bleeding(TeamBattleState state) => state.Combat.Statuses
            .Where(s => s.DefinitionId.Value == "elite-bleeding" && s.Target.Id == Player.Value).Sum(s => s.Stacks);

        private static void Command(BattleAdventureSession run, AdventureCommandKind kind, string id = null)
        {
            var result = run.Execute(run.Revision, new AdventureCommand(kind, id));
            Assert.IsTrue(result.Accepted, kind + ": " + result.Error);
        }

        private static BattleAdventureSession AdventureAt(string target, bool durablePlayer = false)
        {
            var content = Content();
            var definition = BattleAdventureXml.Load(ToolkitTestFiles.Read("Engine/Content/SingleChapterAdventure.xml"), content);
            var run = new BattleAdventureSession(definition, content, "single-chapter-enemy-adventure", 71);
            foreach (string id in new[] { "battle-1", "chest", "battle-2", "rest-1", "elite", "rest-2", "boss" })
            {
                if (id == target)
                {
                    // Remove random chest items from this combat assertion; keep every shipped enemy definition intact.
                    Assert.IsTrue(run.ApplyAdmin(run.Revision, new BattleAdminPatch(relics: new[] {
                        new BattleAdminRelics(run.PlayerId, Array.Empty<BattleAdminRelic>()) })).Accepted);
                    Command(run, AdventureCommandKind.EnterNode, id);
                    if (durablePlayer)
                    {
                        // Alter only player vitals; keep the shipped enemies, growth, healing and timers.
                        var edited = run.ApplyAdmin(run.Revision,
                            new BattleAdminPatch(new[] { new BattleAdminVitals(run.PlayerId, 100000, 100000) }));
                        Assert.IsTrue(edited.Accepted, edited.Error);
                    }
                    return run;
                }
                Command(run, AdventureCommandKind.EnterNode, id);
                if (run.Phase == AdventurePhase.Battle)
                {
                    // Skip earlier encounters through the public administrator port, without a 1-HP content fixture.
                    var skipped = run.ApplyAdmin(run.Revision, new BattleAdminPatch(run.Battle.State.Duel.Enemies
                        .Select(e => new BattleAdminVitals(e.Id, health: 0))));
                    Assert.IsTrue(skipped.Accepted, skipped.Error);
                }
                while (run.PendingRelicRewards.Count > 0) Command(run, AdventureCommandKind.SkipRelicReward);
                Command(run, AdventureCommandKind.Continue);
            }
            Assert.Fail("Unknown target node: " + target); return null;
        }

        private static void Round(BattleAdventureSession run)
        {
            if (run.Battle.State.FlowFor(run.PlayerId).Window == BattlePlayerWindow.Draw)
                Assert.IsTrue(run.ExecuteBattle(run.Revision, CardDuelAction.SkipDraw).Accepted);
            var ended = run.ExecuteBattle(run.Revision, run.Battle.State.Duel.IsActionBlocked(run.PlayerId)
                ? CardDuelAction.SkipPlayerTurn : CardDuelAction.EndTurn);
            Assert.IsTrue(ended.Accepted, ended.Error);
            if (!run.Battle.State.Duel.IsFinished && run.Battle.State.FlowFor(run.PlayerId).Window == BattlePlayerWindow.Ending)
            {
                var finished = run.ExecuteBattle(run.Revision, CardDuelAction.FinishTurn);
                Assert.IsTrue(finished.Accepted, finished.Error);
            }
        }

        private static BattleAdventureSession Restore(BattleAdventureSession run)
        {
            string saved = run.Export();
            var restored = BattleAdventureSession.Restore(run.Definition, run.Content, saved);
            Assert.AreEqual(saved, restored.Export(), "Real encounter memory and economic history must replay together.");
            return restored;
        }

        [TestCase("battle-1", 6)]
        [TestCase("battle-2", 8)]
        public void RealSevenStationGruntsSpawnAtTwoHundredFiftyHealthAndUseTheirConfiguredAttackEveryRound(string node, long damage)
        {
            var run = AdventureAt(node);
            long health = run.Battle.State.Duel.Combat.Get(Body).Health;
            long maximum = run.Battle.State.Duel.Combat.Get(Body).MaxHealth;
            var losses = new long[3];
            var intents = new string[3];
            for (int round = 0; round < 3; round++)
            {
                intents[round] = run.Battle.State.Duel.Enemies.Single().BehaviorState.Intent.Id;
                long before = run.Health;
                Round(run);
                losses[round] = before - run.Health;
            }
            Assert.Multiple(() => {
                Assert.AreEqual(250, health, "Adventure entry must use the shipped enemy health without a second-node scale bonus.");
                Assert.AreEqual(250, maximum);
                CollectionAssert.AreEqual(new[] { "jab", "strike", "jab" }, intents);
                CollectionAssert.AreEqual(new[] { damage, damage, damage }, losses);
            });
        }

        [Test]
        public void ShippedBaseHeroHasOneHundredTwentyHealthAndDiesFromOrdinaryEnemyDamage()
        {
            var session = Battle("single-chapter-gatekeeper");
            var engine = session.Engine.Duel; var state = session.State.Duel;
            Assert.AreEqual(120, state.Combat.Get(Player).Health);
            Assert.AreEqual(120, state.Combat.Get(Player).MaxHealth);
            foreach (long remaining in new long[] { 116, 80, 59, 0 })
            {
                state = Round(engine, state);
                Assert.AreEqual(remaining, state.Combat.Get(Player).Health);
            }
            Assert.AreEqual(ScoredDuelPhase.Lost, state.Phase, "Normal attack damage still ends a zero-health hero's battle.");
            Assert.IsTrue(state.IsFinished);
        }

        [Test]
        public void ShippedGatekeeperAlternatesGrowingAttacksAndBleedingWithoutSummoning()
        {
            var session = Battle("single-chapter-gatekeeper", durablePlayer: true); var engine = session.Engine.Duel; var state = session.State.Duel;
            Assert.AreEqual(500, state.Combat.Get(Body).MaxHealth);
            var damage = new long[] { 4, 36, 21, 95 }; var layers = new[] { 2, 1, 3, 2 };
            for (int i = 0; i < damage.Length; i++)
            {
                Assert.AreEqual(i % 2 == 0 ? "cast" : "strike", state.Enemies.Single().BehaviorState.Intent.Id);
                var preview = engine.PreviewEnemyAction(state, Body);
                Assert.IsTrue(preview.Accepted, preview.Error.ToString());
                Assert.AreEqual(damage[i], preview.Hits.Single().DamageToHealth);
                long hp = state.Combat.Get(Player).Health; state = Round(engine, state);
                Assert.AreEqual(damage[i], hp - state.Combat.Get(Player).Health, "round " + (i + 1));
                Assert.AreEqual(layers[i], Bleeding(state), "round " + (i + 1));
                Assert.AreEqual(1, state.Enemies.Count);
            }
        }

        [Test]
        public void ShippedWrathKeepsItsOrdinarySequenceAndAddsTheSixthAndTwelfthRoundUltimate()
        {
            var session = Battle("single-chapter-wrath", durablePlayer: true); var engine = session.Engine.Duel; var state = session.State.Duel;
            Assert.AreEqual(1500, state.Combat.Get(Body).MaxHealth);
            Assert.IsNotNull(WrathBossQuery.Read(state));
            Assert.AreEqual(33, WrathBossQuery.Read(state).Attack);
            Assert.IsTrue(state.Enemies.Skip(1).All(h => state.Combat.Get(h.Id).MaxHealth == 1));
            CollectionAssert.AreEqual(new[] { "strike", "strike-2", "guard", "charge", "strike", "strike-2", "guard", "charge" },
                engine.PreviewEnemyIntents(state, Body).Select(a => a.Id));
            for (int round = 1; round <= 12; round++)
            {
                long hp = state.Combat.Get(Player).Health; state = Round(engine, state);
                var view = WrathBossQuery.Read(state);
                Assert.AreEqual(round == 5 || round == 11, view.WarningActive, "round " + round);
                if (round == 5 || round == 11) Assert.AreEqual(round + 1, view.ReleaseRound);
                if (round == 3) Assert.AreEqual(2, state.Combat.Statuses.Single(s => s.DefinitionId.Value == "wrath-shell").Stacks);
                if (round == 6) Assert.AreEqual(172, hp - state.Combat.Get(Player).Health);
                if (round == 12) Assert.AreEqual(189, hp - state.Combat.Get(Player).Health, "The charge raises D to 63 before the extra 3D hit.");
                Assert.AreEqual(round / 4, view.Charges);
                Assert.AreEqual(new[] { "strike", "strike-2", "guard", "charge" }[round % 4], state.Enemies[0].BehaviorState.Intent.Id);
            }
        }

        [Test]
        public void RealSevenStationEliteBleedingAndGrowthSurviveAdventureRestore()
        {
            var run = AdventureAt("elite", durablePlayer: true);
            Assert.AreEqual("single-chapter-gatekeeper", run.CurrentNode.EncounterId);
            Assert.AreEqual(500, run.Battle.State.Duel.Combat.Get(Body).MaxHealth);
            long initial = run.Health; Round(run);
            Assert.AreEqual(initial - 4, run.Health); Assert.AreEqual(2, Bleeding(run.Battle.State.Duel));
            var restored = Restore(run);
            foreach (long damage in new long[] { 36, 21, 95 })
            {
                long hp = run.Health; Round(run); Round(restored);
                Assert.AreEqual(damage, hp - run.Health);
                Assert.AreEqual(run.Export(), restored.Export());
            }
            Assert.AreEqual(4, run.Battle.State.Duel.CompletedRounds);
            Assert.AreEqual(2, Bleeding(restored.Battle.State.Duel));
        }

        [Test]
        public void RealSevenStationBossSeverRegenerationAndUltimateSurviveAdventureRestore()
        {
            var run = AdventureAt("boss", durablePlayer: true);
            Assert.AreEqual("single-chapter-wrath", run.CurrentNode.EncounterId);
            Assert.AreEqual(1500, run.Battle.State.Duel.Combat.Get(Body).MaxHealth);
            Round(run); Round(run);
            var equipped = run.ApplyAdmin(run.Revision, new BattleAdminPatch(relics: new[] {
                new BattleAdminRelics(run.PlayerId, new[] { new BattleAdminRelic("11004") }) }));
            Assert.IsTrue(equipped.Accepted, equipped.Error);
            var used = run.UseBattleItem(run.Revision, run.Relics.Single().Id);
            Assert.IsTrue(used.Accepted, used.Error);
            var severed = WrathBossQuery.Read(run.Battle.State.Duel);
            Assert.AreEqual(8, severed.Attack);
            Assert.IsTrue(severed.Hands.All(h => !h.IsAlive && h.ReviveRound == 11 && h.ReviveRemaining == 8));
            var restored = Restore(run);
            while (run.Battle.State.Duel.CompletedRounds < 11)
            {
                Round(run); Round(restored); Assert.AreEqual(run.Export(), restored.Export());
            }
            var regenerated = WrathBossQuery.Read(restored.Battle.State.Duel);
            Assert.IsTrue(regenerated.Hands.All(h => h.IsAlive && h.ReviveRound == null));
            Assert.AreEqual(1, restored.Battle.State.Duel.Combat.Get(new ParticipantId("hand-5")).Health);
            Assert.IsTrue(regenerated.WarningActive); Assert.AreEqual(12, regenerated.ReleaseRound);
            Assert.AreEqual(1500, run.Battle.State.Duel.Combat.Get(Body).Health, "Regenerated blood hands immediately heal the grenade damage.");
            long hp = run.Health; Round(run); Round(restored);
            Assert.AreEqual(189, hp - restored.Health); Assert.AreEqual(run.Export(), restored.Export());
        }

        [Test]
        public void FourBloodHandsHealFortyThenThirtyUntilOneSeveredHandRegeneratesEightRoundsLater()
        {
            var run = AdventureAt("boss", durablePlayer: true);
            var injured = run.ApplyAdmin(run.Revision, new BattleAdminPatch(new[] { new BattleAdminVitals(Body, health: 1000) }));
            Assert.IsTrue(injured.Accepted, injured.Error);
            Round(run);
            Assert.AreEqual(1040, run.Battle.State.Duel.Combat.Get(Body).Health,
                "Four blood hands heal 10 each; the living binding hand contributes no healing.");

            var hand = new ParticipantId("hand-1");
            var severed = run.ApplyAdmin(run.Revision, new BattleAdminPatch(new[] {
                new BattleAdminVitals(Body, health: 1000), new BattleAdminVitals(hand, health: 0) }));
            Assert.IsTrue(severed.Accepted, severed.Error);
            var engine = run.Battle.Engine.Duel; var state = run.Battle.State.Duel;
            Assert.AreEqual(10, WrathBossQuery.Read(state).Hands.Single(h => h.Id == hand).ReviveRound);
            // The adventure command automatically passes a stunned player and may settle two enemy phases.
            // Use the same compiled battle's team engine to inspect every individual enemy phase.
            for (int round = 2; round <= 9; round++)
            {
                long before = state.Combat.Get(Body).Health;
                state = Round(engine, state);
                Assert.AreEqual(round, state.CompletedRounds, "Each sample covers exactly one enemy phase.");
                Assert.AreEqual(30, state.Combat.Get(Body).Health - before, "round " + round);
                Assert.IsFalse(state.Combat.Get(hand).IsAlive, "round " + round);
            }
            Assert.AreEqual(1240, state.Combat.Get(Body).Health);
            state = Round(engine, state);
            Assert.AreEqual(10, state.CompletedRounds);
            Assert.AreEqual(1280, state.Combat.Get(Body).Health,
                "The severed hand returns at R+8 and immediately restores its missing 10 healing.");
            Assert.AreEqual(1, state.Combat.Get(hand).Health);
            Assert.IsTrue(WrathBossQuery.Read(state).Hands.All(h => h.IsAlive && h.ReviveRound == null));
        }

        [Test]
        public void RealSevenStationBossCompletesTwentyRoundsThenLosesWithoutSpendingReviver()
        {
            var run = AdventureAt("boss");
            var equipped = run.ApplyAdmin(run.Revision, new BattleAdminPatch(
                new[] { new BattleAdminVitals(run.PlayerId, 100000, 100000) }, relics: new[] {
                    new BattleAdminRelics(run.PlayerId, new[] { new BattleAdminRelic("13004") }) }));
            Assert.IsTrue(equipped.Accepted, equipped.Error);
            while (run.Phase == AdventurePhase.Battle) Round(run);
            Assert.AreEqual(AdventurePhase.Failed, run.Phase);
            Assert.AreEqual(20, run.Battle.State.Duel.CompletedRounds);
            Assert.AreEqual(98933, run.Health);
            Assert.AreEqual(5, WrathBossQuery.Read(run.Battle.State.Duel).Charges);
            Assert.AreEqual(1, run.Relics.Single(r => r.DefinitionId == "13004").Charges);
            Assert.AreEqual(run.Export(), Restore(run).Export());
        }
    }
}
