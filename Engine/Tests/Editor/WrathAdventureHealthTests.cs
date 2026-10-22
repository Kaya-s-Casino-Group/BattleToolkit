using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class WrathAdventureHealthTests
    {
        [TestCase("standard-wrath-thousand-hands")]
        [TestCase("standard-single-battle")]
        public void FixedHandHealthOptsOutWhileBodyAndOrdinaryEnemiesStillScale(string encounterId)
        {
            var xml = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle-2026-10-21.xml"));
            var content = BattleContentCompiler.CompileXml(xml.ToString());
            Assert.IsTrue(content.Success, string.Join("; ", content.Diagnostics.Select(d => d.Message)));
            var adventure = XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/StandardAdventure.xml"));
            adventure.Root.SetAttributeValue("startNode", "boss");
            adventure.Root.Element("Nodes").ReplaceNodes(new XElement("Node", new XAttribute("id", "boss"), new XAttribute("kind", "Boss"),
                new XAttribute("chapter", 1), new XAttribute("encounter", encounterId), new XAttribute("enemyHealthMultiplier", 2)));
            var definition = BattleAdventureXml.Load(adventure.ToString(), content.Content);
            var run = new BattleAdventureSession(definition, content.Content, "wrath-health-test", 1);
            var entered = run.Execute(0, new AdventureCommand(AdventureCommandKind.EnterNode, "boss"));
            Assert.IsTrue(entered.Accepted, entered.Error);
            var battle = run.Battle.State.Duel;
            foreach (var enemy in battle.Enemies)
            {
                string id = enemy.Definition.Id;
                long baseHealth = content.Content.Enemies[id].Definition.MaxHealth;
                Assert.AreEqual(id.StartsWith("wrath-") ? 1 : baseHealth * 2, battle.Combat.Get(enemy.Id).MaxHealth, id);
            }
        }
    }
}
