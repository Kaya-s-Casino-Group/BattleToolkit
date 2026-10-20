using System;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;
namespace PokerEngine.Tests
{
 [TestFixture] public class DesignRevision1726Tests
 {
  internal static readonly ParticipantId Player = new ParticipantId("player");
  internal static BattleSession Load(string encounter = "standard-single-battle", Action<XDocument> edit = null)
  {
   var xml=XDocument.Parse(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle-2026-10-20.xml")); edit?.Invoke(xml);
   var compiled=BattleContentCompiler.CompileXml(xml.ToString()); Assert.IsTrue(compiled.Success,string.Join("\n",compiled.Diagnostics.Select(d=>d.Message)));
   var built=BattleContentFactory.Create(compiled.Content,new BattleSetupRequest(encounter,"revision1726",HmacDeckShuffler.FromSeed(new byte[32])));
   Assert.IsTrue(built.Success,string.Join("\n",built.Diagnostics.Select(d=>d.Message)));
   Assert.IsTrue(built.Session.Execute(0,Player,CardDuelAction.Start).Accepted);return built.Session;
  }
  internal static void Round(BattleSession s)
  {
   var r=s.Execute(s.State.Revision,Player,CardDuelAction.EndTurn);Assert.IsTrue(r.Accepted,r.Error.ToString());
   if(!s.State.Duel.IsFinished && s.State.FlowFor(Player).Window==BattlePlayerWindow.Ending) { r=s.Execute(s.State.Revision,Player,CardDuelAction.FinishTurn);Assert.IsTrue(r.Accepted,r.Error.ToString()); }
  }
  [Test] public void StandardStartsInActionAndAutomaticallyRefillsWithoutFiveCardCap()
  {
   var s=Load(); Assert.AreEqual(8,s.State.Zone(CardZoneKinds.Hand,Player).Cards.Count);Assert.AreEqual(BattlePlayerWindow.Action,s.State.FlowFor(Player).Window);
   Assert.IsFalse(s.Execute(s.State.Revision,Player,CardDuelAction.Draw,drawCount:1).Accepted);
   for(int i=0;i<2;i++){var r=s.Execute(s.State.Revision,Player,CardDuelAction.Play,s.State.Zone(CardZoneKinds.Hand,Player).Cards.Take(1));Assert.IsTrue(r.Accepted,r.Error.ToString());}
   Assert.AreEqual(6,s.State.Zone(CardZoneKinds.Hand,Player).Cards.Count);Round(s);Assert.AreEqual(8,s.State.Zone(CardZoneKinds.Hand,Player).Cards.Count);
  }
  [Test] public void BossTemplateCompilesAndFiveHandsChargeIsPreviewed()
  {
   var s=Load("standard-wrath-thousand-hands");Assert.AreEqual(6,s.State.Duel.Enemies.Count);Assert.AreEqual("all-hands",s.State.Duel.Enemies[0].BehaviorState.Intent.Id);
   var preview=s.Engine.Duel.PreviewEnemyAction(s.State.Duel,new ParticipantId("enemy"));Assert.IsTrue(preview.Accepted,preview.Error.ToString());
   Assert.AreEqual(210,preview.Hits[0].DamageToHealth);Round(s);Assert.AreEqual(54,s.State.Table.Cards.Count);
  }
  [Test] public void EnemyBlockPersistsUntilItsOwnNextTurnAndClearsBeforePoison()
  {
   var s=Load(edit:d=>d.Root.Element("Enemies").Elements().First().Element("Behavior").AddFirst(XElement.Parse("<OpeningEffects><Effect id='poison' kind='ApplyStatus' statusRef='bleed' stacks='5' target='Self'/></OpeningEffects>")));
   Round(s);Round(s);Round(s);var e=new ParticipantId("enemy");Assert.AreEqual(90,s.State.Duel.Combat.Get(e).Block);Assert.AreEqual(588,s.State.Duel.Combat.Get(e).Health);
   Round(s);Assert.AreEqual(0,s.State.Duel.Combat.Get(e).Block);Assert.AreEqual(586,s.State.Duel.Combat.Get(e).Health);
  }

 }
}
