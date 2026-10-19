using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    /// <summary>Uses the real scene XML, so planner changes are exercised through the production content path.</summary>
    [TestFixture]
    public sealed class SingleBattleSceneContentTests
    {
        private const string ContentPath = "Engine/Content/SingleBattleDemoNew.xml";
        private static readonly ParticipantId Player = new ParticipantId("player"), Enemy = new ParticipantId("enemy"), Other = new ParticipantId("enemy-2");
        private static string ReadProjectFile(string relative) => ToolkitTestFiles.Read(relative);
        private static string Explain(BattleContentBuildResult result) => string.Join("\n", result.Diagnostics.Select(item =>
            item.Code + ": " + item.Message + " at " + item.Location.Path));
        private static CompiledBattleContent Compile(string xml = null)
        {
            var result = BattleContentCompiler.CompileXml(xml ?? ReadProjectFile(ContentPath), ContentPath);
            Assert.IsTrue(result.Success, Explain(result)); Assert.IsEmpty(result.Diagnostics); return result.Content;
        }
        private static BattleSession Assemble(CompiledBattleContent content, string encounter = "single-battle-new")
        {
            var random = HmacDeckShuffler.FromSeed(new byte[32]); var before = random.StateBytes.ToArray();
            var result = BattleContentFactory.Create(content, new BattleSetupRequest(encounter, "scene-content-test", random));
            Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics.Select(item => item.Message)));
            CollectionAssert.AreEqual(before, random.StateBytes); return result.Session;
        }
        private static BattleSessionResult Act(BattleSession session, CardDuelAction action, CardInstanceId[] cards = null)
        {
            var result = session.Execute(session.State.Revision, Player, action, cards);
            Assert.IsTrue(result.Accepted, action + ": " + result.Error);
            if (action == CardDuelAction.Start) Draw(session);
            if (action == CardDuelAction.EndTurn && !session.State.Duel.IsFinished && session.State.FlowFor(Player).Window == BattlePlayerWindow.Ending)
            {
                var finish = session.Execute(session.State.Revision, Player, CardDuelAction.FinishTurn); Assert.IsTrue(finish.Accepted);
                result = finish;
            }
            if (action == CardDuelAction.EndTurn || action == CardDuelAction.Discard) Draw(session);
            return result;
        }
        private static void Draw(BattleSession session)
        {
            if (session.State.Duel.IsFinished || session.State.FlowFor(Player).Window != BattlePlayerWindow.Draw) return;
            int count = session.Engine.DrawLimit(session.State, Player);
            var result = session.Execute(session.State.Revision, Player, count == 0 ? CardDuelAction.SkipDraw : CardDuelAction.Draw, drawCount: count);
            Assert.IsTrue(result.Accepted, result.Error.ToString());
        }
        private static BattleSession Start(string encounter = "single-battle-new")
        {
            var session = Assemble(Compile(), encounter); Act(session, CardDuelAction.Start); return session;
        }
        private static string[] Faces(int kind)
        {
            switch (kind)
            {
                case 0: return new[] { "standard:1:5" };
                case 1: return new[] { "standard:1:5", "standard:2:5" };
                case 2: return new[] { "standard:1:5", "standard:2:5", "standard:1:2", "standard:2:2" };
                case 3: return new[] { "standard:1:5", "standard:2:5", "standard:3:5" };
                case 4: return new[] { "standard:1:2", "standard:2:3", "standard:3:4", "standard:4:5", "standard:1:6" };
                case 5: return new[] { "standard:1:2", "standard:1:4", "standard:1:7", "standard:1:9", "standard:1:13" };
                case 6: return new[] { "standard:1:5", "standard:2:5", "standard:3:5", "standard:1:2", "standard:2:2" };
                case 7: return new[] { "standard:1:5", "standard:2:5", "standard:3:5", "standard:4:5" };
                case 8: return new[] { "standard:1:2", "standard:1:3", "standard:1:4", "standard:1:5", "standard:1:6" };
                case 9: return new[] { "standard:1:10", "standard:1:11", "standard:1:12", "standard:1:13", "standard:1:14" };
                case 10: return Enumerable.Repeat("standard:1:5", 5).ToArray();
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
        private static XmlDocument Document()
        { var document = new XmlDocument(); document.LoadXml(ReadProjectFile(ContentPath)); return document; }
        private static BattleSession Fixture(int kind, string encounter = "single-battle-new", bool twoEnemies = false, int health = 700, bool controller = false, int enemyBlock = 0, Action<XmlDocument> configure = null)
        {
            var document = Document(); var deck = document.SelectSingleNode("/BattleContent/Decks/Deck[@id='standard-deck']"); deck.RemoveAll();
            ((XmlElement)deck).SetAttribute("id", "standard-deck");
            var faces = Faces(kind).Concat(Enumerable.Repeat("standard:4:14", 8 - Faces(kind).Length)).ToArray();
            for (int i = 0; i < faces.Length; i++)
            {
                var card = document.CreateElement("Card"); card.SetAttribute("id", i < Faces(kind).Length ? "selected/" + i : "spare/" + i);
                card.SetAttribute("face", faces[i]); deck.AppendChild(card);
            }
            ((XmlElement)document.SelectSingleNode("/BattleContent/Enemies/Enemy[@id='guardian']")).SetAttribute("maxHealth", "100000");
            ((XmlElement)document.SelectSingleNode("/BattleContent/Enemies/Enemy[@id='guardian']")).SetAttribute("initialBlock", enemyBlock.ToString(CultureInfo.InvariantCulture));
            var fight = document.SelectSingleNode("/BattleContent/Encounters/Encounter[@id='" + encounter + "']");
            ((XmlElement)fight.SelectSingleNode("Player")).SetAttribute("initialHealth", health.ToString(CultureInfo.InvariantCulture));
            if (twoEnemies)
            {
                var other = document.CreateElement("Enemy"); other.SetAttribute("id", Other.Value); other.SetAttribute("ref", controller ? "controller" : "guardian"); fight.AppendChild(other);
            }
            if (controller)
            {
                var definition = document.CreateElement("Enemy"); definition.SetAttribute("id", "controller"); definition.SetAttribute("maxHealth", "100000");
                definition.InnerXml = "<Effects><Effect id='control' kind='ApplyStatus' statusRef='stun' target='Opponents'/><Effect id='damage' kind='Damage' amount='4' target='Opponents'/></Effects>";
                document.SelectSingleNode("/BattleContent/Enemies").AppendChild(definition);
            }
            configure?.Invoke(document);
            var session = Assemble(Compile(document.OuterXml), encounter); Act(session, CardDuelAction.Start); return session;
        }
        private static CardInstanceId[] Selected(BattleSession session) => session.State.Zone(CardZoneKinds.Hand, Player).Cards.Where(id =>
            session.State.Table.GetCard(id).OriginId.StartsWith("selected/", StringComparison.Ordinal)).ToArray();
        private static CardDuelTransition Play(BattleSession session) => Act(session, CardDuelAction.Play, Selected(session)).Steps[0].Transition;

        [Test] public void RealFullHouseCleansesStackedBleedAndPreservesBuffsInPreviewAndTimeline()
        {
            var session = Fixture(6, configure: document => {
                var relic = document.CreateElement("Relic"); relic.SetAttribute("id", "starting-statuses");
                relic.InnerXml = "<BoundaryTrigger id='open' timing='BattleStarted'>" +
                    "<Effect kind='ApplyStatus' statusRef='bleed' target='Self'/>" +
                    "<Effect kind='ApplyStatus' statusRef='bleed' target='Self' stacks='2'/>" +
                    "<Effect kind='ApplyStatus' statusRef='shield-reflect' target='Self'/></BoundaryTrigger>";
                document.SelectSingleNode("/BattleContent/Relics").AppendChild(relic);
                var binding = document.CreateElement("Relic"); binding.SetAttribute("instanceId", "open"); binding.SetAttribute("ref", "starting-statuses");
                document.SelectSingleNode("/BattleContent/Loadouts/Loadout[@id='baseline']").AppendChild(binding);
            });
            var before = session.State;
            var preview = session.Engine.PreviewPlay(before, before.Revision, Player, Selected(session));
            Assert.IsTrue(preview.Accepted, preview.Error.ToString()); Assert.AreSame(before, session.State);
            Assert.AreEqual(2, before.Duel.Combat.Statuses.Count);
            Assert.AreEqual(2, before.Duel.Combat.Statuses.Single(x => x.DefinitionId.Value == "bleed").Stacks);
            var result = Act(session, CardDuelAction.Play, Selected(session));
            var cleansed = result.Steps.SelectMany(step => step.Transition.RuleEvents).OfType<CombatStatusChangedEvent>().Where(x => x.Reason == CombatStatusChangeReason.Cleansed).ToArray();
            CollectionAssert.AreEqual(new[] { "bleed" }, cleansed.Select(x => x.StatusBefore.DefinitionId.Value));
            Assert.AreEqual(697, session.State.Duel.Combat.Get(Player).Health);
            CollectionAssert.AreEquivalent(new[] { "shield-reflect", "stun", "control-immunity" }, session.State.Duel.Combat.Statuses.Select(x => x.DefinitionId.Value));
            CollectionAssert.AreEqual(preview.State.Duel.Combat.Statuses.Select(x => x.Id), session.State.Duel.Combat.Statuses.Select(x => x.Id));
            var facts = result.Timeline.Where(x => x.RuleEvent is CombatStatusChangedEvent change && change.Reason == CombatStatusChangeReason.Cleansed).ToArray();
            Assert.AreEqual(1, facts.Length);
            Assert.IsFalse(facts[0].State.Duel.Combat.Statuses.Any(x => x.DefinitionId.Value == "bleed"));
            Assert.IsTrue(facts[0].State.Duel.Combat.Statuses.Any(x => x.DefinitionId.Value == "shield-reflect"));
        }

        [Test] public void RealSceneContentCompilesAndCreatesAnUnstartedIndependentBaseline()
        {
            var content = Compile(); Assert.AreEqual("single-battle-new-content", content.Id);
            var first = Assemble(content); var second = Assemble(content);
            Assert.IsFalse(first.State.HasStarted); Assert.IsEmpty(first.State.Zone(CardZoneKinds.Hand, Player).Cards);
            Assert.AreEqual(52, first.State.Zone(CardZoneKinds.Deck, Player).Cards.Count);
            Assert.AreEqual(800, first.State.Duel.Combat.Get(Player).Health); Assert.AreEqual(1200, first.State.Duel.Combat.Get(Enemy).Health);
            Assert.IsEmpty(first.State.Duel.Player(Player).Relics.Relics); Assert.IsEmpty(first.State.Duel.Combat.Statuses);
            Assert.AreEqual(20, first.Engine.Duel.Definition.RoundLimit); Assert.AreEqual(PlayerBlockExpiry.AfterEnemyPhase, first.Engine.Duel.Definition.PlayerBlockExpiry);
            Assert.IsInstanceOf<ConfiguredHandEffectRule>(first.State.Duel.Player(Player).Rules.EffectRule);
            CollectionAssert.AreEqual(first.State.Zone(CardZoneKinds.Deck, Player).Cards, second.State.Zone(CardZoneKinds.Deck, Player).Cards);
            Act(first, CardDuelAction.Start); Assert.IsFalse(second.State.HasStarted); Assert.AreEqual(8, first.State.Zone(CardZoneKinds.Hand, Player).Cards.Count);
        }

        [Test] public void SharedSceneUsesManualDrawAndDefenseEndsTheTurn()
        {
            var session = Start(); var rules = session.State.Duel.Player(Player).Rules;
            Assert.IsTrue(rules.Flow.ManualDraw); Assert.IsTrue(rules.UnlimitedDiscards);
            var result = Act(session, CardDuelAction.Discard, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(2).ToArray());
            Assert.AreEqual(16, result.Steps[0].Transition.Events.Single(e => e.Effect.Kind == CombatEffectKind.GainBlock).SettledAmount);
            Assert.AreEqual(1, session.State.Duel.CompletedRounds); Assert.AreEqual(3, session.State.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(576, session.State.Duel.Combat.Get(Player).Health);
        }
        [Test] public void DefenseCanBeUsedAgainOnTheNextTurnWithoutWholeBattleBudget()
        {
            var session = Start();
            for (int i = 0; i < 3 && !session.State.Duel.IsFinished; i++)
                Act(session, CardDuelAction.Discard, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray());
            Assert.IsNull(session.State.RemainingDiscardBudgetFor(Player)); Assert.AreEqual(3, session.State.Duel.CompletedRounds);
        }

        [TestCase(0, HandCategory.HighCard)] [TestCase(1, HandCategory.OnePair)] [TestCase(2, HandCategory.TwoPair)]
        [TestCase(8, HandCategory.StraightFlush)] [TestCase(10, HandCategory.FiveOfAKind)]
        public void SingleHitDamageMappingsReadScoreFactorsFromTheSceneXml(int kind, HandCategory category)
        {
            var session = Fixture(kind, twoEnemies: true); var result = Play(session);
            Assert.AreEqual(category, result.Score.Category); Assert.AreEqual(1, result.EffectPlan.Effects.Count);
            var effect = result.EffectPlan.Effects.Single(); Assert.AreEqual(CombatEffectKind.Damage, effect.Kind); Assert.AreEqual(Enemy, effect.Target);
            Assert.AreEqual(result.Score.Total, effect.Amount); Assert.AreEqual(100000 - result.Score.Total, session.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(100000, session.State.Duel.Combat.Get(Other).Health); Assert.AreEqual(2, session.State.Duel.Player(Player).Energy.Energy);
        }

        [TestCase(3, HandCategory.ThreeOfAKind)] [TestCase(7, HandCategory.FourOfAKind)]
        public void GroupHandsDealTheConfiguredFullScoreToEveryLivingEnemy(int kind, HandCategory category)
        {
            var session = Fixture(kind, twoEnemies: true); var result = Play(session);
            Assert.AreEqual(category, result.Score.Category); Assert.AreEqual(2, result.Events.Count);
            CollectionAssert.AreEqual(new[] { Enemy, Other }, result.EffectPlan.Effects.Select(effect => effect.Target));
            Assert.IsTrue(result.EffectPlan.Effects.All(effect => effect.Amount == result.Score.Total));
            foreach (var id in new[] { Enemy, Other }) Assert.AreEqual(100000 - result.Score.Total, session.State.Duel.Combat.Get(id).Health);
        }

        [Test] public void StraightDealsThreeSeparatelyRoundedLowDamageHitsAtOneEnergyCost()
        {
            var session = Fixture(4, twoEnemies: true); var result = Play(session); Assert.AreEqual(HandCategory.Straight, result.Score.Category);
            long perHit = (long)decimal.Floor(result.Score.Total * 0.35m); Assert.AreEqual(3, result.Events.Count);
            Assert.IsTrue(result.Events.All(effect => effect.Effect.Amount == perHit && effect.Effect.Target == Enemy));
            Assert.AreEqual(100000 - perHit * 3, session.State.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(100000, session.State.Duel.Combat.Get(Other).Health);
            Assert.AreEqual(2, session.State.Duel.Player(Player).Energy.Energy);
        }

        [Test] public void FlushDamagesThenHealsFromChipsWithoutDefaultLifeSteal()
        {
            var session = Fixture(5); var result = Play(session); Assert.AreEqual(HandCategory.Flush, result.Score.Category);
            CollectionAssert.AreEqual(new[] { CombatEffectKind.Damage, CombatEffectKind.Heal }, result.Events.Select(effect => effect.Effect.Kind));
            long heal = (long)decimal.Floor(result.Score.Points * 0.2m); Assert.AreEqual(700 + heal, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(result.Score.Total, result.Events[0].Effect.Amount); Assert.AreEqual(heal, result.Events[1].Effect.Amount);
            Assert.IsEmpty(session.State.Duel.Combat.Statuses);
        }

        [Test] public void FullHouseSkipsOneEnemyWindowAndPreventsControlUntilTheEnemyPhaseEnds()
        {
            var session = Fixture(6, twoEnemies: true, controller: true); var primary = Play(session);
            Assert.AreEqual(HandCategory.FullHouse, primary.Score.Category); Assert.IsEmpty(primary.Events);
            CollectionAssert.AreEqual(new[] { CombatEffectKind.ApplyStatus, CombatEffectKind.Cleanse, CombatEffectKind.ApplyStatus }, primary.EffectPlan.Effects.Select(effect => effect.Kind));
            var immunity = session.State.Duel.Combat.Statuses.Single(status => status.DefinitionId.Value == "control-immunity");
            Assert.AreEqual(DurationKind.UntilPhaseEnd, immunity.Duration.Kind); Assert.AreEqual(BattleStatusTiming.EnemyPhase, immunity.Duration.PhaseKey);
            var round = Act(session, CardDuelAction.EndTurn); var events = round.Steps.SelectMany(step => step.Transition.RuleEvents).ToArray();
            Assert.AreEqual(Enemy, events.OfType<CombatActionWindowSkippedEvent>().Single().Actor);
            Assert.AreEqual(Player, events.OfType<CombatStatusBlockedEvent>().Single().Effect.Target);
            Assert.IsEmpty(session.State.Duel.Combat.Statuses); Assert.AreEqual(696, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(1, session.State.Duel.CompletedRounds);
        }

        [Test] public void RoyalFlushEliminatesAllEnemiesRegardlessOfTheirShield()
        {
            var session = Fixture(9, twoEnemies: true, enemyBlock: 2000000000); var result = Play(session);
            Assert.AreEqual(HandCategory.StraightFlush, result.Score.Category); Assert.AreEqual(ScoredDuelPhase.Won, session.State.Duel.Phase);
            Assert.AreEqual(2, result.Events.Count); Assert.IsTrue(result.Events.All(effect => effect.DeathCause == CombatDeathCause.Eliminate && effect.DamageToHealth == 0));
            Assert.IsTrue(result.EffectPlan.Effects.All(effect => effect.Kind == CombatEffectKind.Eliminate));
            foreach (var id in new[] { Enemy, Other }) Assert.AreEqual(2000000000, session.State.Duel.Combat.Get(id).Block);
        }

        [Test] public void OptionalStraightRelicAddsStackingBleedAtHolderTurnStart()
        {
            var session = Fixture(4, "single-battle-dot"); var result = Play(session);
            Assert.AreEqual(12, session.State.Duel.Combat.Statuses.Single().Stacks); Assert.AreEqual(6, result.RelicActivations.Count);
            long afterPlay = session.State.Duel.Combat.Get(Enemy).Health; Act(session, CardDuelAction.EndTurn);
            Assert.AreEqual(afterPlay - 12, session.State.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(11, session.State.Duel.Combat.Statuses.Single().Stacks);
            Act(session, CardDuelAction.EndTurn); Assert.AreEqual(afterPlay - 23, session.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(10, session.State.Duel.Combat.Statuses.Single().Stacks);
        }

        [Test] public void OptionalReflectionRelicAddsNoFreeShieldAndReflectsTheActualDiscardShield()
        {
            var session = Start("single-battle-reflect"); Assert.AreEqual(0, session.State.Duel.Combat.Get(Player).Block);
            Assert.AreEqual("shield-reflect", session.State.Duel.Combat.Statuses.Single().DefinitionId.Value);
            var round = Act(session, CardDuelAction.Discard, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray()); Assert.AreEqual(568, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(1192, session.State.Duel.Combat.Get(Enemy).Health); Assert.AreEqual(0, session.State.Duel.Combat.Get(Player).Block);
            Assert.AreEqual(8, round.Steps.SelectMany(step => step.Transition.Events).Single(effect => effect.Provenance.Origin == CombatEffectOrigin.Reflection).DamageToHealth);
        }

        [Test] public void OptionalFlushRelicIncreasesChipHealingAndConvertsOnlyTheOverflowToDamage()
        {
            var session = Fixture(5, "single-battle-overheal", health: 799); var result = Play(session);
            long baseHealing = (long)decimal.Floor(result.Score.Points * 0.2m), modifiedHealing = (long)decimal.Floor(baseHealing * 1.5m);
            var healing = result.Events.Single(effect => effect.Effect.Kind == CombatEffectKind.Heal);
            Assert.AreEqual(baseHealing, healing.Effect.Amount); Assert.AreEqual(modifiedHealing, healing.SettledAmount); Assert.AreEqual(1, healing.HealthRestored); Assert.AreEqual(modifiedHealing - 1, healing.Overheal);
            var overflow = result.Events.Single(effect => effect.Provenance.Origin == CombatEffectOrigin.Overheal);
            Assert.AreEqual(modifiedHealing - 1, overflow.DamageToHealth); Assert.AreEqual(800, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(100000 - result.Score.Total - modifiedHealing + 1, session.State.Duel.Combat.Get(Enemy).Health);
        }
    }
}
