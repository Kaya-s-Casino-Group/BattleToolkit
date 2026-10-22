using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public class EliteBleedingIntegrationTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player");
        private static readonly ParticipantId Enemy = new ParticipantId("enemy");

        private static XDocument ContentXml(string file = "Engine/Content/StandardSingleBattle-2026-10-21.xml", [CallerFilePath] string source = "") => XDocument.Load(
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "../../..", file)));

        private static BattleSession Load(Action<XDocument> edit = null, string file = "Engine/Content/StandardSingleBattle-2026-10-21.xml", [CallerFilePath] string source = "")
        {
            var xml = XDocument.Load(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "../../..", file)));
            edit?.Invoke(xml);
            var compiled = BattleContentCompiler.CompileXml(xml.ToString());
            Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics.Select(d => d.Message)));
            string encounter = file.Contains("Templates/") ? Path.GetFileNameWithoutExtension(file) : "standard-armored-summoner";
            var built = BattleContentFactory.Create(compiled.Content, new BattleSetupRequest(encounter, "elite-bleeding-test", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(built.Success, string.Join("\n", built.Diagnostics.Select(d => d.Message)));
            Assert.IsTrue(built.Session.Execute(0, Player, CardDuelAction.Start).Accepted);
            return built.Session;
        }

        private static int Layers(BattleSession session) => session.State.Duel.Combat.Statuses
            .Where(s => s.DefinitionId.Value == "elite-bleeding" && s.Target.Id == Player.Value).Sum(s => s.Stacks);

        [Test]
        public void CurrentEliteAlternatesGrowingAttacksAndAccumulatesBleedingAfterDamage()
        {
            var session = Load();
            Assert.AreEqual(400, session.State.Duel.Combat.Get(Enemy).MaxHealth);
            var amounts = new long[] { 4, 36, 21, 95 };
            var layers = new[] { 2, 1, 3, 2 };
            for (int i = 0; i < amounts.Length; i++)
            {
                Assert.AreEqual(i % 2 == 0 ? "cast" : "strike", session.State.Duel.Enemies.Single().BehaviorState.Intent.Id);
                var preview = session.Engine.Duel.PreviewEnemyAction(session.State.Duel, Enemy);
                Assert.IsTrue(preview.Accepted, preview.Error.ToString());
                Assert.AreEqual(amounts[i], preview.Hits.Single().DamageToHealth, "preview round " + (i + 1));
                long before = session.State.Duel.Combat.Get(Player).Health;
                DesignRevision1726Tests.Round(session);
                Assert.AreEqual(amounts[i], before - session.State.Duel.Combat.Get(Player).Health, "settled round " + (i + 1));
                Assert.AreEqual(layers[i], Layers(session), "layers after round " + (i + 1));
                Assert.AreEqual(1, session.State.Duel.Enemies.Count);
            }
        }

        [Test]
        public void StunnedEliteKeepsIntentAndGrowsWithoutDecayingBleeding()
        {
            var session = Load(xml => {
                var behavior = xml.Root.Element("Enemies").Elements("Enemy").Single(e => (string)e.Attribute("id") == "summoner").Element("Behavior");
                behavior.Element("OpeningEffects")?.Remove();
                behavior.AddFirst(XElement.Parse("<OpeningEffects><Effect id='stunned' kind='ApplyStatus' statusRef='stun' target='Self'/></OpeningEffects>"));
            });
            long before = session.State.Duel.Combat.Get(Player).Health;
            DesignRevision1726Tests.Round(session);
            Assert.AreEqual(before, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(0, Layers(session));
            Assert.AreEqual("cast", session.State.Duel.Enemies.Single().BehaviorState.Intent.Id);
            var preview = session.Engine.Duel.PreviewEnemyAction(session.State.Duel, Enemy);
            Assert.AreEqual(9, preview.Hits.Single().DamageToHealth);
            DesignRevision1726Tests.Round(session);
            Assert.AreEqual(before - 9, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(2, Layers(session));
        }

        [Test]
        public void HistoricalArmoredSummonerRetainsItsOpeningArmorAndSummons()
        {
            var session = Load(file: "Engine/Content/Templates/08_ArmoredSummoner.xml");
            Assert.AreEqual("summon", session.State.Duel.Enemies.Single().BehaviorState.Intent.Id);
            Assert.AreEqual(3, session.State.Duel.Combat.Statuses.Single(s => s.DefinitionId.Value == "layer-armor").Stacks);
            DesignRevision1726Tests.Round(session);
            Assert.AreEqual(3, session.State.Duel.Enemies.Count);
            Assert.AreEqual(0, Layers(session));
        }

        [TestCase("hand")]
        [TestCase("missing-attack")]
        [TestCase("negative-growth")]
        [TestCase("fixed-effects")]
        [TestCase("heal")]
        public void AttackScalingRejectsUnsupportedConfigurations(string scenario)
        {
            var xml = ContentXml();
            var enemy = xml.Root.Element("Enemies").Elements().Single(e => (string)e.Attribute("id") == "summoner");
            var behavior = enemy.Element("Behavior");
            switch (scenario)
            {
                case "hand":
                    var damage = xml.Root.Element("HandRules").Descendants("Effect").First(e => (string)e.Attribute("kind") == "Damage");
                    damage.SetAttributeValue("amountKind", "EnemyAttackScale"); break;
                case "missing-attack": behavior.Attribute("attack").Remove(); break;
                case "negative-growth": behavior.SetAttributeValue("attackPerRound", "-1"); break;
                case "fixed-effects":
                    behavior.Remove();
                    enemy.Add(XElement.Parse("<Effects><Effect id='hit' kind='Damage' amount='0.5' amountKind='EnemyAttackScale'/></Effects>")); break;
                case "heal": behavior.Descendants("Effect").First().SetAttributeValue("kind", "Heal"); break;
            }
            Assert.IsFalse(BattleContentCompiler.CompileXml(xml.ToString()).Success, scenario);
        }

        [Test]
        public void IndependentBleedingTemplateUsesTheNewEliteWithoutChangingHistoricalTemplate()
        {
            var session = Load(file: "Engine/Content/Templates/11_BleedingElite.xml");
            Assert.AreEqual("cast", session.State.Duel.Enemies.Single().BehaviorState.Intent.Id);
            Assert.AreEqual(4, session.Engine.Duel.PreviewEnemyAction(session.State.Duel, Enemy).Hits.Single().DamageToHealth);
            DesignRevision1726Tests.Round(session);
            Assert.AreEqual(2, Layers(session));
        }

        [Test]
        public void HistoricalContentKeepsItsFingerprintAndNewAttackConfigurationChangesIdentity()
        {
            var old = ContentXml();
            // Pin the pre-feature .20 content identity: adding optional capabilities must not invalidate old packages.
            string oldXml = ContentXml("Engine/Content/StandardSingleBattle-2026-10-20.xml").ToString();
            Assert.AreEqual("2e516d91179fb57db60c3cc314d95729887ca02569987fa5a52b8aa6a4d0427a", BattleContentCompiler.CompileXml(oldXml).Content.Fingerprint);
            var current = BattleContentCompiler.CompileXml(old.ToString());
            old.Root.Element("Enemies").Elements().Single(e => (string)e.Attribute("id") == "summoner").Element("Behavior").SetAttributeValue("attackPerRound", "11");
            Assert.AreNotEqual(current.Content.Fingerprint, BattleContentCompiler.CompileXml(old.ToString()).Content.Fingerprint);
        }

        [Test]
        public void BleedingBehaviorXmlDefaultsMatchThePublicConstructors()
        {
            var xml = ContentXml();
            var status = xml.Root.Element("Statuses").Elements().Single(e => (string)e.Attribute("id") == "elite-bleeding");
            foreach (var behavior in status.Elements()) behavior.RemoveAttributes();
            var compiled = BattleContentCompiler.CompileXml(xml.ToString());
            Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics.Select(d => d.Message)));
            var behaviors = compiled.Content.Statuses["elite-bleeding"].Behaviors;
            Assert.AreEqual(0.5m, behaviors.OfType<IncomingAttackDamagePerStackSpec>().Single().Rate);
            var decay = behaviors.OfType<StackDecaySpec>().Single();
            Assert.AreEqual(CombatTimingKind.ActionCompleted, decay.Timing);
            Assert.AreEqual(PeriodicTimingSubject.Applier, decay.TimingSubject);
            Assert.AreEqual(1, decay.Stacks);
        }
    }
}
