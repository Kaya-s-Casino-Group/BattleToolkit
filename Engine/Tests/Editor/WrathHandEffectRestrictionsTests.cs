using System;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class WrathHandEffectRestrictionsTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player"), Body = new ParticipantId("enemy"), Ordinary = new ParticipantId("ordinary");
        private static BattleSession Load(Action<XDocument> edit = null)
        {
            var xml = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle-2026-10-21.xml"));
            xml.Root.Element("Encounters").Elements("Encounter").Single(e => (string)e.Attribute("id") == "standard-wrath-thousand-hands")
                .Add(new XElement("Enemy", new XAttribute("id", Ordinary.Value), new XAttribute("ref", "rotator")));
            edit?.Invoke(xml);
            var compiled = BattleContentCompiler.CompileXml(xml.ToString());
            Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics.Select(d => d.Message)));
            var built = BattleContentFactory.Create(compiled.Content, new BattleSetupRequest("standard-wrath-thousand-hands", "wrath-hand-restrictions", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(built.Success, string.Join("\n", built.Diagnostics.Select(d => d.Message)));
            Assert.IsTrue(built.Session.Execute(0, Player, CardDuelAction.Start).Accepted);
            return built.Session;
        }
        private static XElement Enemy(XDocument xml, string id) => xml.Root.Element("Enemies").Elements("Enemy").Single(e => (string)e.Attribute("id") == id);
        private static void GiveAttack(XElement enemy, decimal multiplier, long flat)
        {
            enemy.SetAttributeValue("attackMultiplier", multiplier); enemy.SetAttributeValue("attackFlat", flat);
            enemy.Element("Behavior").Elements("Stage").Remove();
            enemy.Element("Behavior").Add(XElement.Parse("<Stage id='normal' mode='Sequence'><Action id='attack' name='攻击'><Effects><Effect id='attack' kind='Damage' amount='10' target='Opponents' selection='FirstLiving'/></Effects></Action></Stage>"));
        }
        private static CombatEffect Status(TeamBattleState state, string definition, ParticipantId target, int count = 1) => CombatEffect.ApplyStatus(
            new StatusInstance(new StatusInstanceId("restrictions/" + definition + "/" + state.Revision + "/" + target), new StatusDefinitionId(definition),
                EntityRef.Participant(Player), EntityRef.Participant(target), count, definition == "stun" ? StatusDuration.ForTriggers(1) : StatusDuration.UntilRemoved()));
        private static CombatTransition Resolve(TeamBattleEngine engine, TeamBattleState state, params CombatEffect[] effects) =>
            CombatEngine.Resolve(state.Combat, state.Combat.Revision, effects, statusCatalog: engine.Definition.StatusCatalog, amountModifier: new RelicCombatAmountModifier(state));

        [Test]
        public void AllFiveHandsRejectShieldWhileBodyPlayerAndOrdinaryEnemyReceiveIt()
        {
            var session = Load(); var state = session.State.Duel; var hands = WrathBossQuery.Read(state).Hands;
            var targets = hands.Select(h => h.Id).Concat(new[] { Body, Player, Ordinary }).ToArray();
            var result = Resolve(session.Engine.Duel, state, targets.Select(id => new CombatEffect(CombatEffectKind.GainBlock, Body, id, 12)).ToArray());
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            foreach (var hand in hands)
            {
                Assert.AreEqual(0, result.State.Get(hand.Id).Block, hand.Id.ToString());
                Assert.AreEqual(0, result.Events.Single(e => e.Effect.Target == hand.Id).SettledAmount);
            }
            foreach (var id in new[] { Body, Player, Ordinary }) Assert.AreEqual(12, result.State.Get(id).Block, id.ToString());
        }
        [TestCase(2, 7, 2, -.2, 8)]
        [TestCase(.5, 7, 2, -.2, 4)]
        [TestCase(2, -3, 2, -.2, 5)]
        [TestCase(.5, -1, 0, 0, 4)]
        public void HandAttackRejectsPositiveComponentsButKeepsIndependentNegativeComponents(decimal multiplier, long flat, decimal positiveLink, decimal negativeLink, long expected)
        {
            var session = Load(xml => {
                GiveAttack(Enemy(xml, "wrath-blood-1"), multiplier, flat);
                var behavior = Enemy(xml, "wrath").Element("Behavior");
                behavior.AddFirst(new XElement("Link", new XAttribute("id", "hand-positive"), new XAttribute("kind", "DamageDone"), new XAttribute("targets", "wrath-blood-1"), new XAttribute("rate", positiveLink)));
                behavior.AddFirst(new XElement("Link", new XAttribute("id", "hand-negative"), new XAttribute("kind", "DamageDone"), new XAttribute("targets", "wrath-blood-1"), new XAttribute("rate", negativeLink)));
            });
            var preview = session.Engine.Duel.PreviewEnemyAction(session.State.Duel, new ParticipantId("hand-1"));
            Assert.IsTrue(preview.Accepted, preview.Error.ToString()); Assert.AreEqual(expected, preview.Hits.Single().SettledDamage);
        }
        [Test]
        public void TheSameAttackBonusesStillApplyToBodyAndOrdinaryEnemy()
        {
            var session = Load(xml => {
                GiveAttack(Enemy(xml, "rotator"), 2, 7);
                Enemy(xml, "wrath").SetAttributeValue("attackMultiplier", 2); Enemy(xml, "wrath").SetAttributeValue("attackFlat", 7);
                Enemy(xml, "wrath").Element("Behavior").AddFirst(XElement.Parse("<Link id='ordinary-boost' kind='DamageDone' targets='rotator' rate='1'/>"));
            });
            var state = session.State.Duel;
            Assert.AreEqual(54, session.Engine.Duel.PreviewEnemyAction(state, Ordinary).Hits.Single().SettledDamage);
            Assert.AreEqual(73, session.Engine.Duel.PreviewEnemyAction(state, Body).Hits.Single().SettledDamage);
        }
        [Test]
        public void PoisonAndStunStillApplyAndReviveDoesNotRemoveTheIdentityRestriction()
        {
            var session = Load(); var state = session.State.Duel; var hand = new ParticipantId("hand-1");
            var result = Resolve(session.Engine.Duel, state, Status(state, "bleed", hand, 3), Status(state, "stun", hand),
                CombatEffect.Eliminate(Player, hand), new CombatEffect(CombatEffectKind.Revive, Body, hand, 1),
                new CombatEffect(CombatEffectKind.GainBlock, Body, hand, 12));
            Assert.IsTrue(result.Accepted, result.Error.ToString()); Assert.AreEqual(1, result.State.Get(hand).Health);
            Assert.AreEqual(0, result.State.Get(hand).Block); Assert.IsEmpty(result.State.Statuses.Where(s => s.Target.Id == hand.Value));
            var negative = CombatEngine.Resolve(result.State, result.State.Revision, new[] { Status(state, "bleed", hand, 2), Status(state, "stun", hand) },
                statusCatalog: session.Engine.Duel.Definition.StatusCatalog, amountModifier: new RelicCombatAmountModifier(state));
            Assert.IsTrue(negative.Accepted, negative.Error.ToString());
            CollectionAssert.AreEquivalent(new[] { "bleed", "stun" }, negative.State.Statuses.Where(s => s.Target.Id == hand.Value).Select(s => s.DefinitionId.Value));
        }
        [Test]
        public void FailedLaterEffectRollsBackTheWholeShieldBatch()
        {
            var session = Load(); var state = session.State.Duel; var hand = new ParticipantId("hand-1");
            var result = Resolve(session.Engine.Duel, state, new CombatEffect(CombatEffectKind.GainBlock, Body, hand, 12),
                new CombatEffect(CombatEffectKind.GainBlock, Body, Body, 1), new CombatEffect(CombatEffectKind.GainBlock, Body, Body, long.MaxValue));
            Assert.IsFalse(result.Accepted); Assert.AreSame(state.Combat, result.State); Assert.IsEmpty(result.RuleEvents);
            Assert.AreEqual(0, state.Combat.Get(hand).Block); Assert.AreEqual(0, state.Combat.Get(Body).Block);
        }

        private sealed class LateBlockBonusModifier : ICombatAmountModifier, ICombatEffectPolicy
        {
            private readonly RelicCombatAmountModifier inner;
            internal LateBlockBonusModifier(TeamBattleState state) { inner = new RelicCombatAmountModifier(state); }
            public bool CanReceiveBlock(CombatState current, ParticipantId target) => inner.CanReceiveBlock(current, target);
            public CombatAmountModification Modify(CombatAmountContext context)
            {
                var baseResult = inner.Modify(context);
                return context.Effect.Kind == CombatEffectKind.GainBlock && context.Stage == CombatAmountStage.Incoming
                    ? new CombatAmountModification(context.Amount, baseResult.Contributions.Concat(new[] { new CombatAmountContribution("test-late", "block-received", 7) }), true)
                    : baseResult;
            }
        }
        [TestCase(0)]
        [TestCase(12)]
        public void ShieldRejectionRunsAfterLateBlockReceivedFlatAdditions(long amount)
        {
            var session = Load(); var state = session.State.Duel; var hand = new ParticipantId("hand-1");
            var result = CombatEngine.Resolve(state.Combat, state.Combat.Revision, new[] {
                new CombatEffect(CombatEffectKind.GainBlock, Body, hand, amount), new CombatEffect(CombatEffectKind.GainBlock, Body, Body, amount) },
                amountModifier: new LateBlockBonusModifier(state));
            Assert.IsTrue(result.Accepted, result.Error.ToString());
            var blocked = result.Events.Single(e => e.Effect.Target == hand);
            Assert.AreEqual(amount + 7, blocked.IncomingAmountModification.OutputAmount);
            Assert.AreEqual(0, blocked.FinalAmountModification.OutputAmount); Assert.AreEqual(0, blocked.SettledAmount);
            Assert.AreEqual(0, result.State.Get(hand).Block); Assert.AreEqual(amount + 7, result.State.Get(Body).Block);
        }
        [Test]
        public void AttackBonusRejectionSurvivesReviveAndCleanseWithNegativeLinkStillActive()
        {
            var session = Load(xml => {
                GiveAttack(Enemy(xml, "wrath-blood-1"), 2, 7);
                Enemy(xml, "wrath").Element("Behavior").AddFirst(
                    XElement.Parse("<Link id='positive' kind='DamageDone' targets='wrath-blood-1' rate='2'/>"),
                    XElement.Parse("<Link id='negative' kind='DamageDone' targets='wrath-blood-1' rate='-.2'/>"));
            });
            var state = session.State.Duel; var hand = new ParticipantId("hand-1");
            var revived = Resolve(session.Engine.Duel, state, CombatEffect.Eliminate(Player, hand),
                new CombatEffect(CombatEffectKind.Revive, Body, hand, 1), CombatEffect.Cleanse(Body, hand));
            Assert.IsTrue(revived.Accepted, revived.Error.ToString()); Assert.IsEmpty(revived.State.Statuses.Where(status => status.Target.Id == hand.Value));
            var attack = new AttackContext(hand, AttackOrigin.EnemyAttack, 10);
            var outgoing = state.DamageRulesFor(hand).AttackDamage.Modify(new DamageModifierContext(revived.State, hand, Player, 10, attack));
            var result = CombatEngine.Resolve(revived.State, revived.State.Revision, new[] {
                new CombatEffect(CombatEffectKind.Damage, hand, Player, outgoing.OutputAmount, outgoing, attack) }, amountModifier: new RelicCombatAmountModifier(state));
            Assert.IsTrue(result.Accepted, result.Error.ToString()); Assert.AreEqual(8, result.Events.Single().SettledDamage);
        }
        [Test]
        public void RealEnemySupportEffectsCannotGrantShieldToHands()
        {
            var session = Load(xml => Enemy(xml, "rotator").Element("Behavior").ReplaceNodes(XElement.Parse(
                "<Stage id='support' mode='Sequence'><Action id='support' name='群体护盾'><Effects><Effect id='support' kind='GainBlock' amount='12' target='Allies' selection='AllLiving'/></Effects></Action></Stage>")));
            var engine = session.Engine.Duel; var state = session.State.Duel;
            var ended = engine.Execute(state, state.Revision, Player, ScoredDuelActionKind.EndPlayerTurn);
            Assert.IsTrue(ended.Accepted); state = ended.State;
            while (state.NextEnemy != Ordinary)
            {
                var action = engine.Execute(state, state.Revision, state.NextEnemy, ScoredDuelActionKind.EnemyAction);
                Assert.IsTrue(action.Accepted, action.Error.ToString()); state = action.State;
            }
            var supported = engine.Execute(state, state.Revision, Ordinary, ScoredDuelActionKind.EnemyAction);
            Assert.IsTrue(supported.Accepted, supported.Error.ToString());
            Assert.AreEqual(12, supported.State.Combat.Get(Body).Block);
            foreach (var hand in WrathBossQuery.Read(supported.State).Hands)
            {
                Assert.AreEqual(0, supported.State.Combat.Get(hand.Id).Block);
                Assert.AreEqual(0, supported.Events.Single(e => e.Effect.Target == hand.Id).SettledAmount);
            }
        }
        [Test]
        public void PlayerAttackBonusesAreUnaffectedInTheSameWrathEncounter()
        {
            var session = Load(xml => {
                var player = xml.Root.Element("Players").Elements("Player").Single();
                player.SetAttributeValue("attackMultiplier", 2); player.SetAttributeValue("attackFlat", 7);
            });
            var before = session.State;
            var preview = session.Engine.PreviewPlay(before, before.Revision, Player, before.Zone(CardZoneKinds.Hand, Player).Cards.Take(1), new[] { Body });
            Assert.IsTrue(preview.Accepted, preview.Error.ToString());
            var hit = preview.Events.Single(e => e.Effect.Kind == CombatEffectKind.Damage);
            Assert.AreEqual(hit.Effect.Attack.BaseDamage * 2 + 7, hit.SettledDamage);
            Assert.AreSame(before, session.State);
        }
        [Test]
        public void SecondaryDamageFromAHandIsNotMistakenForAnAttackBuff()
        {
            var session = Load(xml => Enemy(xml, "wrath").Element("Behavior").AddFirst(XElement.Parse(
                "<Link id='secondary-amount' kind='DamageDone' targets='wrath-blood-1' rate='1'/>")));
            var state = session.State.Duel; var hand = new ParticipantId("hand-1");
            var result = Resolve(session.Engine.Duel, state, new CombatEffect(CombatEffectKind.Damage, hand, Player, 10));
            Assert.IsTrue(result.Accepted, result.Error.ToString()); Assert.AreEqual(20, result.Events.Single().SettledDamage);
        }
        [Test]
        public void EnrageAttackBonusIsRejectedOnlyForWrathHands()
        {
            var session = Load(xml => {
                GiveAttack(Enemy(xml, "wrath-blood-1"), 1, 0);
                var rules = xml.Root.Element("BattleRules").Elements("Rules").Single(e => (string)e.Attribute("id") == "thousand-hands-rules");
                rules.SetAttributeValue("roundLimit", 1); rules.SetAttributeValue("roundLimitBehavior", "Enrage"); rules.SetAttributeValue("enrageDamagePerRound", 20);
            });
            var engine = session.Engine.Duel; var state = session.State.Duel;
            var end = engine.Execute(state, state.Revision, Player, ScoredDuelActionKind.EndPlayerTurn); Assert.IsTrue(end.Accepted); state = end.State;
            while (state.Phase == ScoredDuelPhase.AwaitingEnemyAction)
            {
                var action = engine.Execute(state, state.Revision, state.NextEnemy, ScoredDuelActionKind.EnemyAction);
                Assert.IsTrue(action.Accepted, action.Error.ToString()); state = action.State;
            }
            Assert.AreEqual(10, engine.PreviewEnemyAction(state, new ParticipantId("hand-1")).Hits.Single().SettledDamage);
            Assert.AreEqual(53, engine.PreviewEnemyAction(state, Body).Hits.Single().SettledDamage);
        }
    }
}
