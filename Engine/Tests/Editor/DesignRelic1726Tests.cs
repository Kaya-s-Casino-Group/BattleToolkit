using System;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;
namespace PokerEngine.Tests
{
 [TestFixture] public class DesignRelic1726Tests
 {
  static readonly ParticipantId P=DesignRevision1726Tests.Player;
  internal static BattleSession Session(int[] ranks,bool flush=false,string[] relics=null,int enemies=1,Action<XDocument> edit=null)
  {
   return DesignRevision1726Tests.Load(edit:d=>{
    d.Root.Element("Decks").Elements().First().ReplaceNodes(ranks.Select((rank,i)=>new XElement("Card",new XAttribute("id","c"+i),new XAttribute("face","standard:"+(flush?1:i%4+1)+":"+rank))));
    var e=d.Root.Element("Enemies").Elements().First();e.RemoveNodes();e.SetAttributeValue("maxHealth",100000);e.SetAttributeValue("damage",0);
    for(int i=1;i<enemies;i++)d.Root.Element("Encounters").Elements().First().Add(new XElement("Enemy",new XAttribute("id","enemy-"+i),new XAttribute("ref","rotator")));
    foreach(var r in relics??Array.Empty<string>())d.Root.Element("Loadouts").Elements().First().Add(new XElement("Relic",new XAttribute("ref",r),new XAttribute("instanceId",r)));
    edit?.Invoke(d);
   });
  }
  static CardInstanceId[] Cards(BattleSession s)=>s.State.Zone(CardZoneKinds.Hand,P).Cards.ToArray();
  internal static CardDuelTransition Play(BattleSession s,int count=100)
  {var r=s.Execute(s.State.Revision,P,CardDuelAction.Play,Cards(s).Take(count));Assert.IsTrue(r.Accepted,r.Error.ToString());return r.Steps[0].Transition;}
  static long Damage(CardDuelTransition t)=>t.Events.Where(e=>e.Effect.Kind==CombatEffectKind.Damage&&e.Effect.Target!=P).Sum(e=>e.SettledDamage);
  [Test] public void FullHouseSeparatelyScoresTripleAndPairAndKeepsControl()
  {
   var s=Session(new[]{7,7,7,8,8},enemies:2);var t=Play(s);
   CollectionAssert.AreEqual(new long[]{42,42,32},t.Events.Where(e=>e.Effect.Kind==CombatEffectKind.Damage).Select(e=>e.SettledDamage));
   Assert.AreEqual(2,t.Score.Components.Count);Assert.IsTrue(s.State.Duel.Combat.Statuses.Any(x=>x.DefinitionId.Value=="stun"));
  }
  [Test] public void FlushIsOverlayAndFaceChipsUseElevenTwelveThirteenAceOne()
  {
   var s=Session(new[]{11,12,14},true);var t=Play(s);Assert.AreEqual(HandCategory.HighCard,t.Score.Category);Assert.AreEqual(1,t.Score.Points);Assert.AreEqual(19,t.Events.Single(e=>e.Effect.Kind==CombatEffectKind.Heal).SettledAmount);
   var k=Session(new[]{13});Assert.AreEqual(13,Play(k).Score.Points);
  }
  [TestCase("R05",70)][TestCase("R09",60)][TestCase("R02",80)][TestCase("R28",120)]
  public void StraightScoreAndOneShotDamage(string relic,long expected)
  {var s=Session(new[]{2,3,4,5,6},relics:new[]{relic});var before=s.State;var preview=s.Engine.PreviewPlay(before,before.Revision,P,Cards(s));Assert.IsTrue(preview.Accepted,preview.Error.ToString());Assert.AreSame(before,s.State);Assert.AreEqual(expected,Damage(Play(s)));if(relic=="R28")Assert.AreEqual(0,s.State.Duel.Player(P).Relics.Relics[0].RemainingCharges);}
  [TestCase("R07",15)][TestCase("R18",35)][TestCase("R03",10)]
  public void PoisonStackProgramsApplyAtEveryHit(string relic,int stacks)
  {var s=Session(new[]{2,3,4,5,6},relics:new[]{relic});Play(s);Assert.AreEqual(stacks,s.State.Duel.Combat.Statuses.Single(x=>x.DefinitionId.Value=="bleed").Stacks);}
  [Test] public void ExtraHitsRepeatPoisonAndKeepBaseFiveHits()
  {var s=Session(new[]{2,3,4,5,6},relics:new[]{"R13","R18","R19"});var t=Play(s);Assert.AreEqual(7,t.Events.Count(e=>e.Effect.Kind==CombatEffectKind.Damage));Assert.AreEqual(49,s.State.Duel.Combat.Statuses.Single().Stacks);}
  [Test] public void FlushRatioAddsThenMultipliesAndOverhealChoosesAreaRoute()
  {var s=Session(new[]{2,4,7},true,new[]{"R08","R20","R21","R26"},2);var t=Play(s);Assert.AreEqual(41,t.Events.Single(e=>e.Effect.Kind==CombatEffectKind.Heal).SettledAmount);Assert.AreEqual(2,t.Events.Count(e=>e.Provenance.Origin==CombatEffectOrigin.Overheal));}
  [Test] public void FlushRelicsRequireDualHandBadgeOnStraightFlush()
  {var s=Session(new[]{2,3,4,5,6},true,new[]{"R05","R06"});Assert.AreEqual(160,Play(s).Score.Total);s=Session(new[]{2,3,4,5,6},true,new[]{"R05","R06","R27"});Assert.AreEqual(400,Play(s).Score.Total);}
  [Test] public void HandShrinkIsCumulativeAndRoundHealthLossBypassesShield()
  {var s=Session(Enumerable.Range(2,10).ToArray(),relics:new[]{"R02","R04"},edit:d=>d.Root.Element("Players").Elements().First().SetAttributeValue("initialBlock",100));Assert.AreEqual(797,s.State.Duel.Combat.Get(P).Health);Assert.AreEqual(7,s.State.Duel.Player(P).Limits.HandLimit.Value);DesignRevision1726Tests.Round(s);Assert.AreEqual(6,s.State.Duel.Player(P).Limits.HandLimit.Value);Assert.AreEqual(794,s.State.Duel.Combat.Get(P).Health);}
  [Test] public void OneShotAllowsEntireHandThenRestoresLimitAndPreviewNeverSpendsCharge()
  {var s=Session(new[]{2,2,2,2,2,5,7,9},relics:new[]{"R30"});Assert.AreEqual(8,s.Engine.PlayLimit(s.State,P,false));var t=Play(s);Assert.AreEqual(HandCategory.FiveOfAKind,t.Score.Category);Assert.AreEqual(5,s.Engine.PlayLimit(s.State,P,false));Assert.AreEqual(0,s.State.Duel.Player(P).Relics.Relics[0].RemainingCharges);DesignRevision1726Tests.Round(s);Assert.AreEqual(8,Cards(s).Length);}
  [Test] public void ExpandedClassifierHandlesFullDeckWithoutSubsetExplosion()
  {var cards=Enumerable.Range(0,52).Select(i=>new EffectiveCard(new CardInstanceId("x"+i),(Rank)(i%13+2),(Suit)(i/13+1)));var p=PokerHandCompatibility.Classify(cards,3,5,flushAsAttribute:true);Assert.AreEqual(HandCategory.StraightFlush,p.Highest.Category);Assert.AreEqual(52,p.Highest.SubmittedCards.Count);}
  static CardDuelTransition GroupPlay(BattleSession s,int from,int count)
  {var ids=Cards(s).Where(id=>Enumerable.Range(from,count).Select(i=>"c"+i).Contains(s.State.Table.GetCard(id).OriginId));var r=s.Execute(s.State.Revision,P,CardDuelAction.Play,ids);Assert.IsTrue(r.Accepted,r.Error.ToString());return r.Steps[0].Transition;}
  [Test] public void ConsecutiveStraightBonusOnlyAppliesOnceWithinRound()
  {var s=Session(Enumerable.Range(0,15).Select(i=>2+i%5).ToArray(),relics:new[]{"R15"},edit:d=>d.Root.Element("Players").Elements().First().SetAttributeValue("handSize",15));Assert.AreEqual(40,GroupPlay(s,0,5).Score.Total);Assert.AreEqual(100,GroupPlay(s,5,5).Score.Total);Assert.AreEqual(40,GroupPlay(s,10,5).Score.Total);}
  [Test] public void EarlierFlushBuffsFollowingStraight()
  {var s=Session(new[]{2,5,8,3,4,5,6,7},relics:new[]{"R22"},edit:d=>{foreach(var c in d.Root.Element("Decks").Descendants("Card").Take(3))c.SetAttributeValue("face",((string)c.Attribute("face")).Replace("standard:2:","standard:1:").Replace("standard:3:","standard:1:"));});GroupPlay(s,0,3);Assert.AreEqual(125,GroupPlay(s,3,5).Score.Total);}
  [Test] public void EarlierStraightBuffsFollowingFlushHeal()
  {var s=Session(new[]{3,4,5,6,7,2,6,9},relics:new[]{"R23"},edit:d=>{foreach(var c in d.Root.Element("Decks").Descendants("Card").Skip(5))c.SetAttributeValue("face","standard:1:"+((string)c.Attribute("face")).Split(':').Last());});GroupPlay(s,0,5);Assert.AreEqual(19,GroupPlay(s,5,3).Events.Single(e=>e.Effect.Kind==CombatEffectKind.Heal).SettledAmount);}
  [Test] public void AceStraightRequestsOneExtraDrawPerRound()
  {var s=Session(new[]{10,11,12,13,14,10,11,12,13,14},relics:new[]{"R12"},edit:d=>d.Root.Element("Players").Elements().First().SetAttributeValue("handSize",10));Assert.AreEqual(1,GroupPlay(s,0,5).Events.Count(e=>e.Effect.Kind==CombatEffectKind.DrawCards));Assert.AreEqual(0,GroupPlay(s,5,5).Events.Count(e=>e.Effect.Kind==CombatEffectKind.DrawCards));}
  [Test] public void PoisonMultiplierChangesTickDamageWithoutChangingDecay()
  {var s=Session(new[]{2,3,4,5,6},relics:new[]{"R25"});Play(s);DesignRevision1726Tests.Round(s);Assert.AreEqual(99950,s.State.Duel.Combat.Get(new ParticipantId("enemy")).Health);Assert.AreEqual(4,s.State.Duel.Combat.Statuses.Single().Stacks);}
  [Test] public void PoisonThresholdUsesStacksBeforeWholePlay()
  {var s=Session(new[]{2,3,4,5,6,2,3,4,5,6},relics:new[]{"R07","R17"},edit:d=>d.Root.Element("Players").Elements().First().SetAttributeValue("handSize",10));Assert.AreEqual(40,Damage(GroupPlay(s,0,5)));Assert.AreEqual(46,Damage(GroupPlay(s,5,5)));}
  [Test] public void OverhealRouteUsesAcquisitionOrderAndOneShotContractIsConsumed()
  {var s=Session(new[]{2,4,7},true,new[]{"R21","R14","R29"});var t=Play(s);Assert.AreEqual(52,t.Events.Single(e=>e.Effect.Kind==CombatEffectKind.Heal).SettledAmount);Assert.AreEqual(52,t.Events.Single(e=>e.Provenance.Origin==CombatEffectOrigin.Overheal).SettledAmount);Assert.AreEqual(0,s.State.Duel.Player(P).Relics.Relics.Last().RemainingCharges);}
  [Test] public void DiscardSuitBonusCountsDominantSuitInWholeHand()
  {var s=Session(new[]{2,4,6,8},relics:new[]{"R16"},edit:d=>{int i=0;foreach(var c in d.Root.Element("Decks").Descendants("Card"))c.SetAttributeValue("face","standard:"+(i++<3?1:2)+":"+((string)c.Attribute("face")).Split(':').Last());});var ids=Cards(s).Where(id=>new[]{"c0","c3"}.Contains(s.State.Table.GetCard(id).OriginId));var r=s.Execute(s.State.Revision,P,CardDuelAction.Discard,ids);Assert.IsTrue(r.Accepted,r.Error.ToString());Assert.AreEqual(13,r.Steps[0].Transition.Events.Where(e=>e.Effect.Kind==CombatEffectKind.GainBlock).Sum(e=>e.SettledAmount));}
  [Test] public void ShortStraightRelicAndPenaltyMultiplierFloorWork()
  {var s=Session(new[]{2,3,4,5},relics:new[]{"R24"});Assert.AreEqual(HandCategory.Straight,Play(s).Score.Category);s=Session(new[]{2},relics:new[]{"R01"});Assert.AreEqual(1,Play(s).Score.Multiplier);}

  [Test] public void ExpandedRankClassifierRetainsHighestKicker()
  {var ranks=new[]{2,2,2,2,4,4,6,6,8,8,10,10,12,12,14,14};var cards=ranks.Select((r,i)=>new EffectiveCard(new CardInstanceId("k"+i),(Rank)r,(Suit)(i%4+1)));var p=PokerHandClassifier.ClassifyBest(cards,ignoreFlush:true);Assert.AreEqual(HandCategory.FourOfAKind,p.Category);CollectionAssert.AreEqual(new[]{2,14},p.TieBreakRanks);}

  [Test] public void LargeValidScoreCanBeSplitWithoutDecimalIntermediateOverflow()
  {var s=Session(new[]{2,3,4,5,6},edit:d=>d.Root.Element("ScoringProfiles").Descendants("Hand").Single(h=>(string)h.Attribute("category")=="Straight").SetAttributeValue("multiplier","100000000000000"));var preview=s.Engine.PreviewPlay(s.State,s.State.Revision,P,Cards(s));Assert.IsTrue(preview.Accepted,preview.Error.ToString());Assert.AreEqual(2000000000000000L,preview.Score.Total);}

 }
}
