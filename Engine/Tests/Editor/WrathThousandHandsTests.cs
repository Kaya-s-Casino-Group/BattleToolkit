using System;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;
namespace PokerEngine.Tests
{
 [TestFixture] public class WrathThousandHandsTests
 {
  static readonly ParticipantId P=new ParticipantId("player"), Boss=new ParticipantId("enemy"), Fist=new ParticipantId("hand-1"), Shield=new ParticipantId("hand-2");
  static BattleSession Session(Action<XDocument> edit=null)=>DesignRevision1726Tests.Load("standard-wrath-thousand-hands",edit);
  [Test] public void ShieldInterceptsFirstHandAttackAndDamageReductionCountsAtActionStart()
  {
   var s=Session();var b=s.State.Duel;
   var r=CombatEngine.Resolve(b.Combat,b.Combat.Revision,new[]{new CombatEffect(CombatEffectKind.Damage,P,Fist,10),new CombatEffect(CombatEffectKind.Damage,P,Boss,100)},amountModifier:new RelicCombatAmountModifier(b));
   Assert.IsTrue(r.Accepted,r.Error.ToString());Assert.AreEqual(Shield,r.Events[0].Effect.Target);Assert.IsTrue(r.State.Get(Fist).IsAlive);Assert.IsFalse(r.State.Get(Shield).IsAlive);Assert.AreEqual(50,r.Events[1].DamageToHealth);
  }
  [Test] public void ChainHalvesPlayerBlockAndLetsFistBypassExistingBlock()
  {
   var s=Session();var b=s.State.Duel;
   var r=CombatEngine.Resolve(b.Combat,b.Combat.Revision,new[]{new CombatEffect(CombatEffectKind.GainBlock,P,P,100),new CombatEffect(CombatEffectKind.Damage,Fist,P,10)},amountModifier:new RelicCombatAmountModifier(b));
   Assert.IsTrue(r.Accepted,r.Error.ToString());Assert.AreEqual(50,r.State.Get(P).Block);Assert.AreEqual(782,r.State.Get(P).Health);
  }
  [Test] public void BloodHandRestoresMaxHealthAndFistDamageHealsBoss()
  {
   var s=Session();var b=s.State.Duel;var blood=new ParticipantId("hand-4");
   var r=CombatEngine.Resolve(b.Combat,b.Combat.Revision,new[]{new CombatEffect(CombatEffectKind.Damage,P,Boss,1000),new CombatEffect(CombatEffectKind.Heal,blood,Boss,0),new CombatEffect(CombatEffectKind.Damage,Fist,P,10)},amountModifier:new RelicCombatAmountModifier(b));
   Assert.IsTrue(r.Accepted,r.Error.ToString());Assert.AreEqual(3629,r.State.Get(Boss).Health);
  }
  static BattleSession StrongPlayer(long damage)
  {return Session(d=>{var p=d.Root.Element("Players").Elements().First();p.SetAttributeValue("actionsPerTurn",10);p.SetAttributeValue("maxEnergy",10);p.SetAttributeValue("initialEnergy",10);var hit=d.Root.Element("HandRules").Descendants("Hand").Single(h=>(string)h.Attribute("category")=="HighCard").Element("Effect");hit.SetAttributeValue("amountKind","Constant");hit.SetAttributeValue("amount",damage);});}
  static BattleSessionResult Hit(BattleSession s,ParticipantId target)
  {var r=s.Execute(s.State.Revision,P,CardDuelAction.Play,s.State.Zone(CardZoneKinds.Hand,P).Cards.Take(1),new[]{target});Assert.IsTrue(r.Accepted,r.Error.ToString());return r;}
  [Test] public void RemovingOneHandCancelsChargedIntentAndSecondPhaseRevivesOnce()
  {
   var s=StrongPlayer(5000);Hit(s,Shield);Assert.AreEqual("strike",s.State.Duel.Enemies[0].BehaviorState.Intent.Id);
   Hit(s,Fist);var r=Hit(s,Boss);Assert.AreEqual(2,r.Timeline.Count(t=>t.RuleEvent is CombatEvent e&&e.Effect.Kind==CombatEffectKind.Revive));
   Assert.IsTrue(s.State.Duel.Enemies.All(e=>s.State.Duel.Combat.Get(e.Id).IsAlive));Assert.IsTrue(s.State.Duel.Enemies[0].BehaviorState.Uses.ContainsKey("@entry/rage"));
   Hit(s,Shield);Assert.IsFalse(s.State.Duel.Combat.Get(Shield).IsAlive);
  }
  [Test] public void KillingBossWinsWhileHandsAreAlive()
  {var s=StrongPlayer(10000);Hit(s,Boss);Assert.AreEqual(ScoredDuelPhase.Won,s.State.Duel.Phase);Assert.IsTrue(s.State.Duel.Combat.Get(Fist).IsAlive);}
  [Test] public void RegenWaitsTwoRoundsThenRevivesInNumberOrder()
  {var s=StrongPlayer(10);Hit(s,Shield);Hit(s,Fist);DesignRevision1726Tests.Round(s);Assert.IsFalse(s.State.Duel.Combat.Get(Fist).IsAlive);DesignRevision1726Tests.Round(s);Assert.IsTrue(s.State.Duel.Combat.Get(Fist).IsAlive);Assert.IsFalse(s.State.Duel.Combat.Get(Shield).IsAlive);}
  [Test] public void InvalidLinkReferencesAreRejected()
  {var xml=ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle-2026-10-20.xml").Replace("healTarget=\"wrath\"","healTarget=\"missing\"");Assert.IsFalse(BattleContentCompiler.CompileXml(xml).Success);}
 }
}
