using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class WrathBossRulesTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), Body = new ParticipantId("enemy");
        private static BattleSession Load(bool durable = true, bool reviveItem = false, Func<string, string> transform = null)
        {
            var xml = ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle-2026-10-21.xml");
            if (durable) xml = xml.Replace("id=\"hero\" name=\"玩家\" maxHealth=\"800\"", "id=\"hero\" name=\"玩家\" maxHealth=\"100000\"");
            if (reviveItem)
            {
                xml = xml.Replace("<Loadouts>", "<Loadouts><Loadout id=\"wrath-revive-test\"><Relic instanceId=\"revive-test\" ref=\"13004\" /></Loadout>");
                xml = xml.Replace("<Encounter id=\"standard-wrath-thousand-hands\"", "<Encounter id=\"standard-wrath-thousand-hands\"");
                var document = new System.Xml.XmlDocument(); document.LoadXml(xml);
                ((System.Xml.XmlElement)document.SelectSingleNode("//Encounter[@id='standard-wrath-thousand-hands']/Player")).SetAttribute("loadoutRef", "wrath-revive-test");
                xml = document.OuterXml;
            }
            var compiled = BattleContentCompiler.CompileXml(transform == null ? xml : transform(xml));
            Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics.Select(d => d.Message)));
            var built = BattleContentFactory.Create(compiled.Content, new BattleSetupRequest("standard-wrath-thousand-hands", "wrath-rules", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(built.Success, string.Join("\n", built.Diagnostics.Select(d => d.Message)));
            Assert.IsTrue(built.Session.Execute(0, Player, CardDuelAction.Start).Accepted);
            return built.Session;
        }

        private static string UseWrathOrdinaryStrike(string xml)
        {
            var document = new System.Xml.XmlDocument(); document.LoadXml(xml);
            var stage = (System.Xml.XmlElement)document.SelectSingleNode("/BattleContent/Enemies/Enemy[@id='wrath']/Behavior/Stage[@id='normal']");
            Assert.IsNotNull(stage, "The fixture must modify the intended boss stage regardless of XML formatting.");
            stage.SetAttribute("mode", "Priority");
            return document.OuterXml;
        }
        private static TeamBattleState Round(TeamBattleEngine engine, TeamBattleState state)
        {
            var end = engine.Execute(state, state.Revision, Player, state.IsActionBlocked(Player) ? ScoredDuelActionKind.SkipPlayerTurn : ScoredDuelActionKind.EndPlayerTurn);
            Assert.IsTrue(end.Accepted, end.Error.ToString()); state = end.State;
            while (state.Phase == ScoredDuelPhase.AwaitingEnemyAction)
            {
                var next = engine.Execute(state, state.Revision, state.NextEnemy, ScoredDuelActionKind.EnemyAction);
                Assert.IsTrue(next.Accepted, next.Error.ToString()); state = next.State;
            }
            return state;
        }
        [Test]
        public void ApprovedContentUses1500BodyAndFourStepOrdinarySequence()
        {
            var session = Load(); var state = session.State.Duel;
            Assert.AreEqual(1500, state.Combat.Get(Body).MaxHealth);
            CollectionAssert.AreEqual(new[] { "strike", "strike-2", "guard", "charge", "strike", "strike-2", "guard", "charge" },
                session.Engine.Duel.PreviewEnemyIntents(state, Body).Select(a => a.Id));
            CollectionAssert.AreEqual(new[] { "普攻", "普攻", "防御", "蓄力" },
                session.Engine.Duel.PreviewEnemyIntents(state, Body).Take(4).Select(a => a.Name.Split('·')[0]));
            Assert.AreEqual(6, state.Enemies.Count);
            Assert.AreEqual(PlayerBlockExpiry.PlayerTurnStart, session.Engine.Duel.Definition.PlayerBlockExpiry);
            Assert.AreEqual(BattleRoundLimitBehavior.Lose, session.Engine.Duel.Definition.RoundLimitBehavior);
        }
        [Test]
        public void OrdinaryChargeIncreasesOnlyOnTheSuccessfulChargeAction()
        {
            var session = Load(); var engine = session.Engine.Duel; var state = session.State.Duel;
            state = Round(engine, state); Assert.AreEqual("strike-2", state.Enemies[0].BehaviorState.Intent.Id);
            long before = state.Combat.Get(Player).Health;
            state = Round(engine, state); Assert.AreEqual(33, before - state.Combat.Get(Player).Health);
            state = Round(engine, state); Assert.AreEqual("charge", state.Enemies[0].BehaviorState.Intent.Id);
            before = state.Combat.Get(Player).Health; state = Round(engine, state);
            Assert.AreEqual(before, state.Combat.Get(Player).Health); Assert.AreEqual(1, WrathBossQuery.Read(state).Charges);
            state = Round(engine, state); Assert.AreEqual(43, before - state.Combat.Get(Player).Health);
        }

        private static TeamBattleState Apply(TeamBattleEngine engine, TeamBattleState state, params CombatEffect[] effects)
        {
            // Exercise the real item-effect authority without widening its production visibility for tests.
            var resolve = typeof(TeamBattleEngine).GetMethod("ResolveItemEffects", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var result = (TeamBattleTransition)resolve.Invoke(engine, new object[] { state, Player, effects, null });
            Assert.IsTrue(result.Accepted, result.Error.ToString()); return result.State;
        }
        private static CombatEffect Status(TeamBattleState state, string id, ParticipantId target, int stacks = 1, ParticipantId? source = null)
        {
            return CombatEffect.ApplyStatus(new StatusInstance(new StatusInstanceId("test/" + id + "/" + state.Revision + "/" + target),
                new StatusDefinitionId(id), EntityRef.Participant(source ?? Player), EntityRef.Participant(target), stacks, id == "stun" ? StatusDuration.ForTriggers(1) : StatusDuration.UntilRemoved()));
        }
        [Test]
        public void WarningAndReleaseFollowFiveSixAndElevenTwelveWithoutAdvancingOrdinarySequenceTwice()
        {
            var session = Load(); var engine = session.Engine.Duel; var state = session.State.Duel;
            Assert.AreEqual(33, WrathBossQuery.Read(state).Attack);
            for (int round = 1; round <= 12; round++)
            {
                long before = state.Combat.Get(Player).Health;
                if (round == 6) CollectionAssert.AreEqual(new long[] { 43, 129 }, engine.PreviewEnemyAction(state, Body).Hits.Select(hit => hit.SettledDamage));
                if (round == 12) CollectionAssert.AreEqual(new long[] { 189 }, engine.PreviewEnemyAction(state, Body).Hits.Select(hit => hit.SettledDamage));
                state = Round(engine, state); var view = WrathBossQuery.Read(state);
                Assert.AreEqual(round == 5 || round == 11, view.WarningActive, "after round " + round);
                if (round == 5 || round == 11) Assert.AreEqual(round + 1, view.ReleaseRound);
                if (round == 6) Assert.AreEqual(172, before - state.Combat.Get(Player).Health);
                if (round == 12) Assert.AreEqual(189, before - state.Combat.Get(Player).Health);
                Assert.AreEqual(round / 4, view.Charges);
                Assert.AreEqual(new[] { "strike", "strike-2", "guard", "charge" }[round % 4], state.Enemies[0].BehaviorState.Intent.Id);
            }
        }
        [Test]
        public void SeverDuringWarningCancelsImmediatelyAndRegenerationRequiresANewWarningWindow()
        {
            var session = Load(); var engine = session.Engine.Duel; var state = session.State.Duel;
            for (int r = 1; r <= 5; r++) state = Round(engine, state);
            var hand = state.Enemies[1].Id;
            state = Apply(engine, state, CombatEffect.Eliminate(Player, hand));
            var view = WrathBossQuery.Read(state);
            Assert.IsFalse(view.WarningActive); Assert.AreEqual(5, view.CooldownRemaining);
            Assert.AreEqual(14, view.Hands.Single(h => h.Id == hand).ReviveRound);
            Assert.AreEqual(8, view.Hands.Single(h => h.Id == hand).ReviveRemaining);
            Assert.AreEqual(38, view.Attack);
            for (int r = 6; r <= 13; r++) { state = Round(engine, state); Assert.IsFalse(WrathBossQuery.Read(state).WarningActive); }
            state = Round(engine, state); view = WrathBossQuery.Read(state);
            Assert.IsTrue(state.Combat.Get(hand).IsAlive); Assert.IsTrue(view.WarningActive); Assert.AreEqual(15, view.ReleaseRound);
        }
        [Test]
        public void HandsRegenerateIndependentlyAtStartOfRPlusEightAndJoinTheSameEnemyQueue()
        {
            var session = Load(); var engine = session.Engine.Duel; var state = session.State.Duel;
            var first = state.Enemies[1].Id; var second = state.Enemies[2].Id;
            state = Apply(engine, state, new CombatEffect(CombatEffectKind.Damage, Player, first, 1));
            Assert.AreEqual(9, WrathBossQuery.Read(state).Hands.Single(h => h.Id == first).ReviveRound);
            state = Round(engine, state);
            state = Apply(engine, state, new CombatEffect(CombatEffectKind.Damage, Player, second, 1));
            Assert.AreEqual(10, WrathBossQuery.Read(state).Hands.Single(h => h.Id == second).ReviveRound);
            for (int r = 2; r <= 8; r++) state = Round(engine, state);
            Assert.IsFalse(state.Combat.Get(first).IsAlive);
            var end = engine.Execute(state, state.Revision, Player, ScoredDuelActionKind.EndPlayerTurn);
            Assert.IsTrue(end.Accepted); state = end.State;
            Assert.AreEqual(1, state.Combat.Get(first).Health); Assert.IsFalse(state.Combat.Get(second).IsAlive);
            Assert.Contains(first, state.PendingEnemies.ToArray()); Assert.AreEqual(Body, state.PendingEnemies.Last());
            Assert.IsNull(WrathBossQuery.Read(state).Hands.Single(h => h.Id == first).ReviveRound);
        }
        [Test]
        public void HandPoisonSettlesBeforeHealingAndBodyPoisonSettlesAfterTheSurvivingHandsHeal()
        {
            var session = Load(); var engine = session.Engine.Duel; var state = session.State.Duel; var hand = state.Enemies[1].Id;
            state = Apply(engine, state, new CombatEffect(CombatEffectKind.Damage, Player, Body, 100), Status(state, "bleed", hand), Status(state, "bleed", Body));
            state = Round(engine, state);
            Assert.AreEqual(1414, state.Combat.Get(Body).Health);
            Assert.IsFalse(state.Combat.Get(hand).IsAlive); Assert.AreEqual(9, WrathBossQuery.Read(state).Hands.Single(h => h.Id == hand).ReviveRound);
        }
        [Test]
        public void MultiLayerHandAndBodyPoisonEachTickOnceAndConsumeOnlyOneBodyShellLayer()
        {
            var session = Load(); var engine = session.Engine.Duel; var state = session.State.Duel; var hand = state.Enemies[1].Id;
            state = Apply(engine, state, new CombatEffect(CombatEffectKind.Damage, Player, Body, 100),
                Status(state, "bleed", hand, 3), Status(state, "bleed", Body, 3), Status(state, "wrath-shell", Body, 2));
            var end = engine.Execute(state, state.Revision, Player, ScoredDuelActionKind.EndPlayerTurn);
            Assert.IsTrue(end.Accepted, end.Error.ToString()); state = end.State;
            var facts = end.RuleEvents.ToList();
            while (state.Phase == ScoredDuelPhase.AwaitingEnemyAction)
            {
                var next = engine.Execute(state, state.Revision, state.NextEnemy, ScoredDuelActionKind.EnemyAction);
                Assert.IsTrue(next.Accepted, next.Error.ToString()); facts.AddRange(next.RuleEvents); state = next.State;
            }
            var ticks = facts.OfType<CombatEvent>().Where(e => e.Effect.Kind == CombatEffectKind.Damage &&
                e.Provenance.Origin == CombatEffectOrigin.Periodic).ToArray();
            Assert.AreEqual(2, ticks.Length, "Each poisoned holder emits one combined damage event, not one event per stack.");
            var handTick = ticks.Single(e => e.Effect.Target == hand); var bodyTick = ticks.Single(e => e.Effect.Target == Body);
            Assert.AreEqual(3, handTick.Effect.Amount); Assert.AreEqual(1, handTick.HealthLost);
            Assert.AreEqual(3, bodyTick.Effect.Amount); Assert.AreEqual(0, bodyTick.SettledDamage);
            Assert.AreEqual(1, state.Combat.Statuses.Single(s => s.DefinitionId.Value == "wrath-shell").Stacks);
            Assert.AreEqual(2, state.Combat.Statuses.Single(s => s.DefinitionId.Value == "bleed" && s.Target.Id == Body.Value).Stacks);
            Assert.AreEqual(1415, state.Combat.Get(Body).Health, "Only the three surviving hands heal, then one poison tick consumes one shell layer.");
            Assert.IsFalse(state.Combat.Get(hand).IsAlive);
            Assert.AreEqual(9, WrathBossQuery.Read(state).Hands.Single(h => h.Id == hand).ReviveRound);
        }
        [Test]
        public void BodyStunSkipsChargeAndCancelsTheDueUltimateWithoutFreezingCooldown()
        {
            var session = Load(); var engine = session.Engine.Duel; var state = session.State.Duel;
            for (int r = 1; r <= 3; r++) state = Round(engine, state);
            Assert.AreEqual("charge", state.Enemies[0].BehaviorState.Intent.Id);
            state = Apply(engine, state, Status(state, "stun", Body));
            state = Round(engine, state); Assert.AreEqual(0, WrathBossQuery.Read(state).Charges);
            state = Round(engine, state); Assert.AreEqual(1, WrathBossQuery.Read(state).Charges);
            Assert.IsTrue(WrathBossQuery.Read(state).WarningActive);
            state = Apply(engine, state, Status(state, "stun", Body)); long hp = state.Combat.Get(Player).Health;
            state = Round(engine, state);
            Assert.AreEqual(hp, state.Combat.Get(Player).Health); Assert.IsFalse(WrathBossQuery.Read(state).WarningActive);
            Assert.AreEqual(4, WrathBossQuery.Read(state).CooldownRemaining);
        }
        [Test]
        public void PreviewAndRejectedCommandsDoNotMutateAuthorityAndCanExcludeExtraUltimate()
        {
            var session = Load(); var engine = session.Engine.Duel; var state = session.State.Duel;
            for (int r = 1; r <= 5; r++) state = Round(engine, state);
            var full = engine.PreviewEnemyAction(state, Body); var ordinary = engine.PreviewEnemyAction(state, Body, includeWrathUltimate: false);
            Assert.IsTrue(full.Accepted); Assert.IsTrue(ordinary.Accepted);
            Assert.AreEqual(172, full.Hits.Sum(e => e.SettledDamage)); Assert.AreEqual(43, ordinary.Hits.Sum(e => e.SettledDamage));
            Assert.IsTrue(WrathBossQuery.Read(state).WarningActive); Assert.AreEqual(1, WrathBossQuery.Read(state).Charges);
            var stale = engine.Execute(state, state.Revision + 1, Player, ScoredDuelActionKind.EndPlayerTurn);
            Assert.IsFalse(stale.Accepted); Assert.AreSame(state, stale.State);
        }
        [Test]
        public void UltimateDoesNotProduceAnotherActionCompletedDecay()
        {
            var session = Load(); var engine = session.Engine.Duel; var state = session.State.Duel;
            for (int r = 1; r <= 5; r++) state = Round(engine, state);
            state = Apply(engine, state, Status(state, "elite-bleeding", Player, 5, Body));
            state = Round(engine, state);
            Assert.AreEqual(4, state.Combat.Statuses.Single(s => s.DefinitionId.Value == "elite-bleeding").Stacks);
        }
        [Test]
        public void ZeroDamageAndPureControlLeaveHandsAliveAndBodyDeathWinsWithLivingHands()
        {
            var session = Load(); var engine = session.Engine.Duel; var state = session.State.Duel; var hand = state.Enemies[1].Id;
            state = Apply(engine, state, new CombatEffect(CombatEffectKind.Damage, Player, hand, 0), Status(state, "stun", hand));
            Assert.IsTrue(state.Combat.Get(hand).IsAlive); Assert.IsNull(WrathBossQuery.Read(state).Hands.Single(h => h.Id == hand).ReviveRound);
            state = Apply(engine, state, CombatEffect.Eliminate(Player, Body)); Assert.AreEqual(ScoredDuelPhase.Won, state.Phase);
            Assert.IsTrue(state.Combat.Get(hand).IsAlive);
        }
        [Test]
        public void RoundTwentyCompletesThenLosesWithoutInvokingTheReviveItem()
        {
            var session = Load(reviveItem: true); var engine = session.Engine.Duel; var state = session.State.Duel;
            for (int r = 1; r <= 20; r++) state = Round(engine, state);
            Assert.AreEqual(ScoredDuelPhase.Lost, state.Phase); Assert.AreEqual(20, state.CompletedRounds);
            Assert.IsTrue(state.Player(Player).Relics.Relics.Any(r => r.DefinitionId == "13004"));
        }

        [Test]
        public void BindingUsesGlobalFiveRoundClockAndControlImmunityBlocksIt()
        {
            var session = Load(); var engine = session.Engine.Duel; var state = session.State.Duel;
            for (int r = 1; r <= 4; r++) state = Round(engine, state);
            state = Round(engine, state); Assert.IsTrue(state.IsActionBlocked(Player));
            state = Round(engine, state); Assert.IsFalse(state.IsActionBlocked(Player));
            for (int r = 7; r <= 9; r++) state = Round(engine, state);
            state = Apply(engine, state, Status(state, "control-immunity", Player));
            state = Round(engine, state); Assert.IsFalse(state.IsActionBlocked(Player));
        }
        [Test]
        public void BindingStunMissesItsDeadlineAndDoesNotCatchUpOnRoundSix()
        {
            var session = Load(); var engine = session.Engine.Duel; var state = session.State.Duel;
            var binding = state.Enemies.Last().Id;
            for (int r = 1; r <= 4; r++) state = Round(engine, state);
            state = Apply(engine, state, Status(state, "stun", binding));
            state = Round(engine, state); Assert.IsFalse(state.IsActionBlocked(Player));
            state = Round(engine, state); Assert.IsFalse(state.IsActionBlocked(Player));
            for (int r = 7; r <= 10; r++) state = Round(engine, state);
            Assert.IsTrue(state.IsActionBlocked(Player));
        }
        [Test]
        public void BindingRegeneratedOnRoundTenImmediatelyBindsWithItsOriginalIdentity()
        {
            var session = Load(); var engine = session.Engine.Duel; var state = session.State.Duel; var binding = state.Enemies.Last().Id;
            state = Round(engine, state);
            state = Apply(engine, state, CombatEffect.Eliminate(Player, binding));
            for (int r = 2; r <= 9; r++) state = Round(engine, state);
            Assert.IsFalse(state.IsActionBlocked(Player)); Assert.IsFalse(state.Combat.Get(binding).IsAlive);
            state = Round(engine, state);
            Assert.IsTrue(state.Combat.Get(binding).IsAlive); Assert.IsTrue(state.IsActionBlocked(Player));
        }
        [Test]
        public void FourBloodHandsRegenerateTogetherAndHealImmediatelyBeforeBodyPoison()
        {
            var session = Load(); var engine = session.Engine.Duel; var state = session.State.Duel;
            state = Apply(engine, state, state.Enemies.Skip(1).Take(4).Select(e => CombatEffect.Eliminate(Player, e.Id)).ToArray());
            for (int r = 1; r <= 8; r++) state = Round(engine, state);
            // Consume any old shell layers, then establish a visible healing deficit.
            state = Apply(engine, state, new CombatEffect(CombatEffectKind.Damage, Player, Body, 1), new CombatEffect(CombatEffectKind.Damage, Player, Body, 1),
                new CombatEffect(CombatEffectKind.Damage, Player, Body, 100), Status(state, "bleed", Body));
            long hp = state.Combat.Get(Body).Health;
            state = Round(engine, state);
            Assert.AreEqual(hp + 19, state.Combat.Get(Body).Health);
            Assert.IsTrue(state.Enemies.Skip(1).Take(4).All(h => state.Combat.Get(h.Id).Health == 1));
        }
        [Test]
        public void StunnedLivingHandsStillAddAttackAndAllowTheFiveHandWarning()
        {
            var session = Load(); var engine = session.Engine.Duel; var state = session.State.Duel;
            for (int r = 1; r <= 4; r++) state = Round(engine, state);
            state = Apply(engine, state, state.Enemies.Skip(1).Select(h => Status(state, "stun", h.Id)).ToArray());
            Assert.AreEqual(43, WrathBossQuery.Read(state).Attack);
            state = Round(engine, state);
            Assert.IsTrue(WrathBossQuery.Read(state).WarningActive); Assert.AreEqual(43, WrathBossQuery.Read(state).Attack);
            Assert.IsFalse(state.IsActionBlocked(Player));
        }
        [Test]
        public void StunnedBloodHandDoesNotHealTheInjuredBodyButStillContributesAttack()
        {
            var session = Load(); var engine = session.Engine.Duel; var state = session.State.Duel; var hand = state.Enemies[1].Id;
            state = Apply(engine, state, new CombatEffect(CombatEffectKind.Damage, Player, Body, 100), Status(state, "stun", hand));
            Assert.AreEqual(1400, state.Combat.Get(Body).Health); Assert.AreEqual(33, WrathBossQuery.Read(state).Attack);
            long playerHealth = state.Combat.Get(Player).Health; state = Round(engine, state);
            Assert.AreEqual(1415, state.Combat.Get(Body).Health, "The stunned blood hand must not supply its five-point heal.");
            Assert.IsTrue(state.Combat.Get(hand).IsAlive); Assert.AreEqual(33, WrathBossQuery.Read(state).Attack);
            Assert.AreEqual(33, playerHealth - state.Combat.Get(Player).Health, "Control does not remove the living hand's attack contribution.");
            state = Round(engine, state);
            Assert.AreEqual(1435, state.Combat.Get(Body).Health, "All four blood hands heal again after the skipped action consumes stun.");
        }
        [Test]
        public void ReviveDuringTheOrdinaryStrikeAbortsTheExtraUltimate()
        {
            var session = Load(reviveItem: true, transform: UseWrathOrdinaryStrike);
            var engine = session.Engine.Duel; var state = session.State.Duel;
            for (int r = 1; r <= 5; r++) state = Round(engine, state);
            state = Apply(engine, state, new CombatEffect(CombatEffectKind.LoseHealth, Player, Player, state.Combat.Get(Player).Health - 1));
            state = Round(engine, state);
            Assert.AreEqual(60000, state.Combat.Get(Player).Health);
            Assert.IsFalse(state.Player(Player).Relics.Relics.Any(r => r.DefinitionId == "13004"));
            Assert.IsFalse(WrathBossQuery.Read(state).WarningActive); Assert.AreEqual(4, WrathBossQuery.Read(state).CooldownRemaining);
        }
        [Test]
        public void ReviveDuringTheUltimateEndsTheEnemyPhaseImmediately()
        {
            var session = Load(reviveItem: true); var engine = session.Engine.Duel; var state = session.State.Duel;
            for (int r = 1; r <= 5; r++) state = Round(engine, state);
            state = Apply(engine, state, new CombatEffect(CombatEffectKind.LoseHealth, Player, Player, state.Combat.Get(Player).Health - 44));
            state = Round(engine, state);
            Assert.AreEqual(60000, state.Combat.Get(Player).Health); Assert.IsEmpty(state.PendingEnemies);
            Assert.AreEqual(ScoredDuelPhase.AwaitingPlayerHand, state.Phase);
        }
        [Test]
        public void BodyKilledByReflectedOrdinaryDamageDoesNotReleaseItsUltimate()
        {
            var session = Load(transform: UseWrathOrdinaryStrike);
            var engine = session.Engine.Duel; var state = session.State.Duel;
            for (int r = 1; r <= 5; r++) state = Round(engine, state);
            Assert.AreEqual("strike", state.Enemies.Single(e => e.Id == Body).BehaviorState.Intent.Id);
            Assert.IsTrue(WrathBossQuery.Read(state).WarningActive);
            Assert.IsFalse(state.Combat.Statuses.Any(s => s.Target.Id == Body.Value && s.DefinitionId.Value == "wrath-shell"));
            state = Apply(engine, state, new CombatEffect(CombatEffectKind.LoseHealth, Player, Body, state.Combat.Get(Body).Health - 1),
                new CombatEffect(CombatEffectKind.GainBlock, Player, Player, 1000), Status(state, "shield-reflect", Player));
            state = Round(engine, state); Assert.AreEqual(ScoredDuelPhase.Won, state.Phase); Assert.AreEqual(967, state.Combat.Get(Player).Block);
        }
        [Test]
        public void RealGrenadeItemSeversAllHandsAndSchedulesEveryRegeneration()
        {
            var session = Load();
            Assert.IsTrue(session.ApplyAdmin(session.State.Revision, new BattleAdminPatch(relics: new[] { new BattleAdminRelics(Player, new[] { new BattleAdminRelic("11004") }) })).Accepted);
            if (session.State.FlowFor(Player).Window == BattlePlayerWindow.Draw) Assert.IsTrue(session.Execute(session.State.Revision, Player, CardDuelAction.SkipDraw).Accepted);
            var item = session.State.Duel.Player(Player).Relics.Relics.Single();
            var used = session.UseItem(session.State.Revision, Player, item.InstanceId);
            Assert.IsTrue(used.Accepted, used.Error.ToString());
            var view = WrathBossQuery.Read(session.State.Duel);
            Assert.AreEqual(8, view.Attack); Assert.IsTrue(view.Hands.All(h => !h.IsAlive && h.ReviveRound == 9 && h.ReviveRemaining == 8));
            Assert.AreEqual(3, session.State.FlowFor(Player).ActionsRemaining);
        }
        [Test]
        public void OrdinaryAndWrathSessionsRetainEnemyAndPlayerPhaseStartedTimelineEntries()
        {
            foreach (bool wrath in new[] { false, true })
            {
                var session = wrath ? Load() : Load(transform: xml => xml.Replace("id=\"standard-wrath-thousand-hands\"", "id=\"unused-wrath\"").Replace("id=\"standard-single-battle\"", "id=\"standard-wrath-thousand-hands\""));
                if (session.State.FlowFor(Player).Window == BattlePlayerWindow.Draw) Assert.IsTrue(session.Execute(session.State.Revision, Player, CardDuelAction.SkipDraw).Accepted);
                var result = session.Execute(session.State.Revision, Player, CardDuelAction.EndTurn);
                Assert.IsTrue(result.Accepted, result.Error.ToString());
                if (session.State.FlowFor(Player).Window == BattlePlayerWindow.Ending) result = session.Execute(session.State.Revision, Player, CardDuelAction.FinishTurn);
                Assert.IsTrue(result.Accepted, result.Error.ToString());
                CollectionAssert.AreEqual(new[] { "enemy", "player" }, result.Timeline.Where(e => e.Kind == BattleTimelineKind.PhaseStarted).Select(e => e.PhaseKey));
            }
        }

        [Test]
        public void RoyalPlayPreviewIsPureAndEliminationStillCompletesTheAttachedFlushHealing()
        {
            var session = Load();
            var patch = new BattleAdminPatch(new[] { new BattleAdminVitals(Player, 99900) },
                new[] { new BattleAdminZone(Player, CardZoneKinds.Hand, Enumerable.Range(10, 5).Select(rank => new BattleAdminCard(definitionId: "standard:1:" + rank))) });
            Assert.IsTrue(session.ApplyAdmin(session.State.Revision, patch).Accepted);
            var before = session.State; var cards = before.Zone(CardZoneKinds.Hand, Player).Cards.ToArray();
            var preview = session.Engine.PreviewPlay(before, before.Revision, Player, cards);
            Assert.IsTrue(preview.Accepted, preview.Error.ToString()); Assert.AreSame(before, session.State);
            var invalid = session.Execute(before.Revision, Player, CardDuelAction.Play, cards.Concat(cards.Take(1)));
            Assert.IsFalse(invalid.Accepted); Assert.AreSame(before, session.State); Assert.AreEqual(33, WrathBossQuery.Read(session.State.Duel).Attack);
            var played = session.Execute(before.Revision, Player, CardDuelAction.Play, cards);
            Assert.IsTrue(played.Accepted, played.Error.ToString()); Assert.AreEqual(ScoredDuelPhase.Won, session.State.Duel.Phase);
            // Current configured card points are 10 + 11 + 12 + 13 + 1 = 47; floor(47 * 0.2) = 9.
            Assert.AreEqual(99909, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(9, played.Steps.SelectMany(step => step.Transition.Events)
                .Where(e => e.Effect.Kind == CombatEffectKind.Heal && e.Effect.Target == Player).Sum(e => e.HealthRestored));
            Assert.AreEqual(6, played.Steps.SelectMany(step => step.Transition.Events).Count(e => e.Effect.Kind == CombatEffectKind.Eliminate));
        }

        [Test]
        public void PlayerReviveInAHandTurnClosesThePendingUltimateAndStartsFreshCooldown()
        {
            var session = Load(reviveItem: true, transform: xml => xml.Replace("</Statuses>",
                "<Status id='hand-backfire' lifetime='UntilRemoved'><PeriodicDamage timing='TurnStarted' timingSubject='Holder' amount='999999' target='Opponents' selection='FirstLiving' /></Status></Statuses>"));
            var engine = session.Engine.Duel; var state = session.State.Duel;
            for (int r = 1; r <= 5; r++) state = Round(engine, state);
            state = Apply(engine, state, Status(state, "hand-backfire", state.Enemies[1].Id));
            state = Round(engine, state);
            Assert.AreEqual(60000, state.Combat.Get(Player).Health); Assert.IsFalse(WrathBossQuery.Read(state).WarningActive);
            Assert.AreEqual(4, WrathBossQuery.Read(state).CooldownRemaining);
        }

        [TestCase(2, 0, 344)]
        [TestCase(1, 7, 186)]
        [TestCase(2, 7, 358)]
        public void UltimateUsesTheSameOutgoingAttackRulesAsTheOrdinaryMainAttack(decimal multiplier, long flat, long expected)
        {
            var session = Load(transform: xml => xml.Replace("id=\"wrath\" name=\"暴怒·千手\"", "id=\"wrath\" name=\"暴怒·千手\" attackMultiplier=\"" + multiplier + "\" attackFlat=\"" + flat + "\""));
            var engine = session.Engine.Duel; var state = session.State.Duel;
            for (int r = 1; r <= 5; r++) state = Round(engine, state);
            var preview = engine.PreviewEnemyAction(state, Body);
            Assert.IsTrue(preview.Accepted, preview.Error.ToString()); Assert.AreEqual(expected, preview.Hits.Sum(e => e.SettledDamage));
            long hp = state.Combat.Get(Player).Health; state = Round(engine, state);
            Assert.AreEqual(expected, hp - state.Combat.Get(Player).Health);
        }

        [Test]
        public void SharedUltimateBaseQueryIncludesThePendingOrdinaryChargeAndRemainsPure()
        {
            var session = Load();
            var engine = session.Engine.Duel; var state = session.State.Duel;
            for (int r = 1; r <= 11; r++) state = Round(engine, state);
            var first = WrathBossQuery.Read(state); var second = WrathBossQuery.Read(state);
            Assert.AreEqual(53, first.Attack); Assert.AreEqual(159, first.UltimateBaseDamage); Assert.AreEqual(189, first.PendingUltimateBaseDamage);
            Assert.AreEqual(first.PendingUltimateBaseDamage, second.PendingUltimateBaseDamage); Assert.AreEqual(2, second.Charges);
            var hp = state.Combat.Get(Player).Health; state = Round(engine, state); Assert.AreEqual(189, hp - state.Combat.Get(Player).Health);
            Assert.AreEqual(3, WrathBossQuery.Read(state).Charges);
        }

        private static BattleSessionResult FinishSessionRound(BattleSession session)
        {
            if (session.State.FlowFor(Player).Window == BattlePlayerWindow.Draw)
                Assert.IsTrue(session.Execute(session.State.Revision, Player, CardDuelAction.SkipDraw).Accepted);
            var result = session.Execute(session.State.Revision, Player,
                session.State.Duel.IsActionBlocked(Player) ? CardDuelAction.SkipPlayerTurn : CardDuelAction.EndTurn);
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            if (!session.State.Duel.IsFinished && session.State.FlowFor(Player).Window == BattlePlayerWindow.Ending)
            {
                result = session.Execute(session.State.Revision, Player, CardDuelAction.FinishTurn);
                Assert.IsTrue(result.Accepted, result.Error.ToString());
            }
            return result;
        }
        [Test]
        public void RealStraightConsumesTwoShellLayersThenDamagesAndAppliesAllFivePoisonStacks()
        {
            var session = Load(); for (int r = 1; r <= 3; r++) FinishSessionRound(session);
            Assert.AreEqual(2, session.State.Duel.Combat.Statuses.Single(s => s.DefinitionId.Value == "wrath-shell").Stacks);
            var cards = Enumerable.Range(2, 5).Select((rank, index) => new BattleAdminCard(definitionId: "standard:" + (index % 4 + 1) + ":" + rank));
            Assert.IsTrue(session.ApplyAdmin(session.State.Revision, new BattleAdminPatch(zones: new[] { new BattleAdminZone(Player, CardZoneKinds.Hand, cards) })).Accepted);
            var result = session.Execute(session.State.Revision, Player, CardDuelAction.Play, session.State.Zone(CardZoneKinds.Hand, Player).Cards, new[] { Body });
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            var hits = result.Steps.SelectMany(step => step.Transition.Events).Where(e => e.Effect.Kind == CombatEffectKind.Damage && e.Effect.Target == Body).ToArray();
            CollectionAssert.AreEqual(new long[] { 0, 0, 8, 10, 12 }, hits.Select(e => e.SettledDamage));
            Assert.AreEqual(1470, session.State.Duel.Combat.Get(Body).Health);
            Assert.AreEqual(5, session.State.Duel.Combat.Statuses.Single(s => s.DefinitionId.Value == "bleed" && s.Target.Id == Body.Value).Stacks);
            Assert.IsFalse(session.State.Duel.Combat.Statuses.Any(s => s.DefinitionId.Value == "wrath-shell"));
        }
        [Test]
        public void RealFullHouseConsumesBothShellLayersAndStillStunsTheBody()
        {
            var session = Load(); for (int r = 1; r <= 3; r++) FinishSessionRound(session);
            var ranks = new[] { 2, 2, 2, 3, 3 };
            var cards = ranks.Select((rank, index) => new BattleAdminCard(definitionId: "standard:" + (index % 4 + 1) + ":" + rank));
            Assert.IsTrue(session.ApplyAdmin(session.State.Revision, new BattleAdminPatch(zones: new[] { new BattleAdminZone(Player, CardZoneKinds.Hand, cards) })).Accepted);
            var result = session.Execute(session.State.Revision, Player, CardDuelAction.Play, session.State.Zone(CardZoneKinds.Hand, Player).Cards, new[] { Body });
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            var hits = result.Steps.SelectMany(step => step.Transition.Events).Where(e => e.Effect.Kind == CombatEffectKind.Damage && e.Effect.Target == Body).ToArray();
            Assert.AreEqual(2, hits.Length); Assert.IsTrue(hits.All(e => e.SettledDamage == 0)); Assert.AreEqual(1500, session.State.Duel.Combat.Get(Body).Health);
            Assert.IsTrue(session.State.Duel.IsActionBlocked(Body)); Assert.IsFalse(session.State.Duel.Combat.Statuses.Any(s => s.DefinitionId.Value == "wrath-shell"));
            Assert.IsTrue(WrathBossQuery.Read(session.State.Duel).Hands.All(h => !h.IsAlive && h.ReviveRemaining == 8));
        }

        [Test]
        public void TimelinePreservesPhaseEndHandDeathDeadlineAcrossRoundRefreshAndPlayerEntry()
        {
            var session = Load(transform: xml => {
                var doc = System.Xml.Linq.XDocument.Parse(xml);
                doc.Root.Element("Statuses").Add(System.Xml.Linq.XElement.Parse("<Status id='phase-poison' lifetime='UntilRemoved'><PeriodicDamage timing='PhaseEnding' amount='1'/></Status>"));
                doc.Descendants("Enemy").Single(e => (string)e.Attribute("id") == "wrath-blood-1").Element("Behavior")
                    .AddFirst(System.Xml.Linq.XElement.Parse("<OpeningEffects><Effect id='poison' kind='ApplyStatus' statusRef='phase-poison' target='Self'/></OpeningEffects>"));
                return doc.ToString();
            });
            var result = FinishSessionRound(session);
            var final = WrathBossQuery.Read(session.State.Duel).Hands[0];
            Assert.AreEqual(9, final.ReviveRound); Assert.AreEqual(7, final.ReviveRemaining);
            var markers = result.Timeline.Where(e => e.Kind == BattleTimelineKind.ResourcesChanged && e.State.Duel.CompletedRounds == 1 ||
                e.Kind == BattleTimelineKind.PhaseStarted && e.PhaseKey == "player").ToArray();
            Assert.IsNotEmpty(markers);
            foreach (var marker in markers)
            {
                var hand = WrathBossQuery.Read(marker.State.Duel).Hands[0];
                Assert.AreEqual(final.ReviveRound, hand.ReviveRound, marker.Kind.ToString());
                Assert.AreEqual(final.ReviveRemaining, hand.ReviveRemaining, marker.Kind.ToString());
            }
        }
        [Test]
        public void TimelineDoesNotResurrectCanceledUltimateAfterPlayerReviveEndsEnemyPhase()
        {
            var session = Load(reviveItem: true, transform: xml => {
                var doc = System.Xml.Linq.XDocument.Parse(xml);
                doc.Descendants("Enemy").Single(e => (string)e.Attribute("id") == "wrath-blood-1").Element("Behavior").Element("Stage")
                    .ReplaceWith(System.Xml.Linq.XElement.Parse("<Stage id='normal' mode='Priority'><Action id='backfire' name='backfire'><When kind='RoundModulo' value='6'/><Effects><Effect id='backfire' kind='Damage' amount='999999' target='Opponents' selection='FirstLiving'/></Effects></Action><Action id='wait' name='wait'><Effects><Effect id='wait' kind='GainBlock' amount='0' target='Self'/></Effects></Action></Stage>"));
                // Keep the next player window open so this test isolates the enemy-phase interruption.
                doc.Descendants("Enemy").Single(e => (string)e.Attribute("id") == "wrath-binding").Descendants("Effect").First()
                    .ReplaceWith(System.Xml.Linq.XElement.Parse("<Effect id='no-bind' kind='GainBlock' amount='0' target='Self'/>"));
                return doc.ToString();
            });
            for (int round = 1; round <= 5; round++) FinishSessionRound(session);
            Assert.IsTrue(WrathBossQuery.Read(session.State.Duel).WarningActive);
            var result = FinishSessionRound(session);
            var final = WrathBossQuery.Read(session.State.Duel);
            Assert.IsFalse(final.WarningActive); Assert.AreEqual(4, final.CooldownRemaining);
            var markers = result.Timeline.Where(e => e.Kind == BattleTimelineKind.ResourcesChanged && e.State.Duel.CompletedRounds == 6 ||
                e.Kind == BattleTimelineKind.PhaseStarted && e.PhaseKey == "player").ToArray();
            Assert.IsNotEmpty(markers);
            foreach (var marker in markers)
            {
                var view = WrathBossQuery.Read(marker.State.Duel);
                Assert.IsFalse(view.WarningActive, marker.Kind.ToString()); Assert.IsNull(view.ReleaseRound, marker.Kind.ToString());
                Assert.AreEqual(final.CooldownRemaining, view.CooldownRemaining, marker.Kind.ToString());
            }
        }
        [Test]
        public void TimelineEnemyEntryShowsTheFreshIntentForABindingHandRegeneratedThisRound()
        {
            var session = Load(); FinishSessionRound(session); var hand = session.State.Duel.Enemies.Last().Id;
            Assert.IsTrue(session.ApplyAdmin(session.State.Revision, new BattleAdminPatch(new[] { new BattleAdminVitals(hand, 0) })).Accepted);
            for (int round = 2; round <= 9; round++) FinishSessionRound(session);
            var result = FinishSessionRound(session);
            var entry = result.Timeline.Single(e => e.Kind == BattleTimelineKind.PhaseStarted && e.PhaseKey == "enemy" && e.State.Duel.CompletedRounds == 9);
            Assert.IsTrue(entry.State.Duel.Combat.Get(hand).IsAlive);
            Assert.AreEqual("binding", entry.State.Duel.Enemies.Single(e => e.Id == hand).BehaviorState.Intent.Id);
            Assert.IsNull(WrathBossQuery.Read(entry.State.Duel).Hands.Single(h => h.Id == hand).ReviveRound);
        }
    }
}
