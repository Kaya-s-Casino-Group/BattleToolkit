using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace PokerEngine
{
 public enum RelicProgramKind { ScorePoints, ScoreMultiplier, PoisonStacks, HealRatioAdd, HealRatioSet, HealRatioMultiply, HealMultiply, RepeatFirstHit, ExtraHit, OverhealBlock, OverhealDamage, OverhealArea, OverhealMultiply, DamageMultiply, PoisonDamageMultiply, DrawAfterPlay, SelfDamage, ShrinkHandEachTurn, DiscardSuitBlock, DualHandRelics, IgnorePlayLimit, TurnHealthLoss }
 public enum RelicHistoryCondition { None, PreviousStraight, EarlierStraight, EarlierFlush }
 /// <summary>Declarative hand/fact modifiers. Runtime counters live in the existing immutable relic memory.</summary>
 public sealed class RelicProgramSpec
 {
  public string Id {get;} public RelicProgramKind Kind {get;} public decimal Amount {get;} public HandCategory? Hand {get;} public bool ExcludeHand {get;}
  public int MinCards {get;} public int MaxCards {get;} public RelicHistoryCondition History {get;} public bool OncePerRound {get;}
  public string StatusRef {get;} public int StatusThreshold {get;} public bool HighAceOrKing {get;}
  public RelicProgramSpec(string id,RelicProgramKind kind,decimal amount=0,HandCategory? hand=null,bool excludeHand=false,int minCards=1,int maxCards=512,
   RelicHistoryCondition history=RelicHistoryCondition.None,bool oncePerRound=false,string statusRef=null,int statusThreshold=0,bool highAceOrKing=false)
  {
   Id=Guard.Text(id,nameof(id));Kind=kind;Amount=amount;Hand=hand;ExcludeHand=excludeHand;MinCards=minCards;MaxCards=maxCards;History=history;OncePerRound=oncePerRound;StatusRef=statusRef;StatusThreshold=statusThreshold;HighAceOrKing=highAceOrKing;
   if(!Enum.IsDefined(typeof(RelicProgramKind),kind)||!Enum.IsDefined(typeof(RelicHistoryCondition),history)||minCards<1||maxCards<minCards||maxCards>512||statusThreshold<0||amount < -1 || amount>1000000)throw new ArgumentException("Invalid relic program.");
   if(hand.HasValue && !Enum.IsDefined(typeof(HandCategory),hand.Value) || amount<0 && kind!=RelicProgramKind.ScoreMultiplier)throw new ArgumentException("Invalid program amount or category.");
   if(new[]{RelicProgramKind.ScorePoints,RelicProgramKind.PoisonStacks,RelicProgramKind.RepeatFirstHit,RelicProgramKind.DrawAfterPlay,RelicProgramKind.SelfDamage,RelicProgramKind.ShrinkHandEachTurn,RelicProgramKind.DiscardSuitBlock,RelicProgramKind.TurnHealthLoss}.Contains(kind) && amount!=decimal.Truncate(amount))throw new ArgumentException("This program requires an integer amount.");
   if(kind==RelicProgramKind.RepeatFirstHit && amount>63 || kind==RelicProgramKind.DrawAfterPlay && amount>512)throw new ArgumentException("Program exceeds action capacity.");
   if((kind==RelicProgramKind.PoisonStacks||kind==RelicProgramKind.PoisonDamageMultiply||statusThreshold>0)&&string.IsNullOrWhiteSpace(statusRef))throw new ArgumentException("Program requires statusRef.");
  }
  internal void Write(BinaryWriter w){w.Write(Id);w.Write((int)Kind);w.Write(Amount);w.Write(Hand.HasValue?(int)Hand.Value:0);w.Write(ExcludeHand);w.Write(MinCards);w.Write(MaxCards);w.Write((int)History);w.Write(OncePerRound);w.Write(StatusRef??"");w.Write(StatusThreshold);w.Write(HighAceOrKing);}
 }
 internal static class RelicPrograms
 {
  internal static IEnumerable<Tuple<BattleRelicState,RelicProgramSpec>> Entries(BattleRelicLoadout loadout,HandAttackInfo hand=null)
  {
   bool dual=loadout.Relics.Any(r=>r.RemainingCharges!=0 && loadout.RulesFor(r.InstanceId).Programs.Any(p=>p.Kind==RelicProgramKind.DualHandRelics));
   foreach(var r in loadout.Relics.Where(r=>r.RemainingCharges!=0))foreach(var p in loadout.RulesFor(r.InstanceId).Programs)
   {
    if(p.OncePerRound&&r.Memory.CounterValue("program/used/"+p.Id)>0)continue;
    if(hand!=null){
     bool match=!p.Hand.HasValue||(p.Hand==HandCategory.Flush?hand.IsFlush:hand.MatchesCategory(p.Hand.Value));
     if(p.ExcludeHand)match=!match;
     if(!match||hand.SubmittedCards.Count<p.MinCards||hand.SubmittedCards.Count>p.MaxCards)continue;
     if(hand.Pattern.Category==HandCategory.StraightFlush&&p.Hand.HasValue&&!p.ExcludeHand&&!dual)continue;
     if(p.HighAceOrKing&&!hand.SubmittedCards.Any(c=>c.Rank==Rank.King||c.Rank==Rank.Ace))continue;
     string key=p.History==RelicHistoryCondition.PreviousStraight?"previous-straight":p.History==RelicHistoryCondition.EarlierStraight?"straight":"flush";
     if(p.History!=RelicHistoryCondition.None&&r.Memory.CounterValue("program/"+key)==0)continue;
    }else if(p.Hand.HasValue||p.History!=RelicHistoryCondition.None)continue;
    yield return Tuple.Create(r,p);
   }
  }
  internal static IEnumerable<ScoringRelic> Scores(BattleRelicLoadout l,HandAttackInfo hand)
  {
   var entries=Entries(l,hand).ToArray();
   foreach(var r in l.Relics){var effects=l.Definition(r.InstanceId).ScoreEffects.Concat(entries.Where(e=>e.Item1.InstanceId==r.InstanceId&&(e.Item2.Kind==RelicProgramKind.ScorePoints||e.Item2.Kind==RelicProgramKind.ScoreMultiplier)).Select(e=>new ScoreEffect(ScoreTiming.AfterCards,e.Item2.Kind==RelicProgramKind.ScorePoints?ScoreOperation.AddPoints:ScoreOperation.AddMultiplier,e.Item2.Amount)));
    yield return new ScoringRelic(r.InstanceId,r.Level,r.Growth,effects);}
  }
  internal static BattleEffectSequence Sequence(BattleRelicLoadout l,ScoredHandEffectContext c,BattleEffectSequence input)
  {
   var entries=Entries(l,c.Hand).ToArray();var steps=new List<BattleEffectStep>();var first=input.Steps.FirstOrDefault(s=>s.Effect.Kind==CombatEffectKind.Damage);bool repeated=false;
   decimal ratioSet=entries.Where(e=>e.Item2.Kind==RelicProgramKind.HealRatioSet).Select(e=>e.Item2.Amount).DefaultIfEmpty(-1).Max();
   foreach(var step in input.Steps){var effect=step.Effect;
    if(effect.Kind==CombatEffectKind.Heal&&c.Hand?.IsFlush==true&&c.Hand.FlushBaseTotal.HasValue){
     decimal basis=c.Hand.FlushBaseTotal.Value;decimal amount=ratioSet>=0?basis*ratioSet:effect.Amount;
     amount+=basis*entries.Where(e=>e.Item2.Kind==RelicProgramKind.HealRatioAdd).Sum(e=>e.Item2.Amount);
     foreach(var e in entries.Where(e=>e.Item2.Kind==RelicProgramKind.HealRatioMultiply||e.Item2.Kind==RelicProgramKind.HealMultiply))amount*=e.Item2.Amount;
     effect=BattleEffectSpec.Heal(checked((long)decimal.Floor(amount)));
    }
    steps.Add(new BattleEffectStep(step.Id,effect,step.Target,step.SourceAliases));
    if(step==first&&!repeated){repeated=true;foreach(var e in entries.Where(e=>e.Item2.Kind==RelicProgramKind.RepeatFirstHit))for(int i=0;i<(int)e.Item2.Amount;i++)steps.Add(new BattleEffectStep("program/"+e.Item1.InstanceId+"/repeat/"+i,effect,step.Target));}
   }
   if(first!=null)foreach(var e in entries.Where(e=>e.Item2.Kind==RelicProgramKind.ExtraHit))steps.Add(new BattleEffectStep("program/"+e.Item1.InstanceId+"/hit",BattleEffectSpec.Damage(checked((long)decimal.Floor(c.Score.Total*e.Item2.Amount)),first.Effect.OnHitStatus),first.Target));
   foreach(var e in entries.Where(e=>e.Item2.Kind==RelicProgramKind.DrawAfterPlay||e.Item2.Kind==RelicProgramKind.SelfDamage))steps.Add(new BattleEffectStep("program/"+e.Item1.InstanceId+"/after",e.Item2.Kind==RelicProgramKind.DrawAfterPlay?BattleEffectSpec.DrawCards((int)e.Item2.Amount):BattleEffectSpec.Damage((long)e.Item2.Amount),EffectTargetSpec.Self()));
   return new BattleEffectSequence(input.RuleVersion,input.Explanation,steps);
  }
  internal static BattleRelicLoadout Complete(BattleRelicLoadout l,HandAttackInfo hand)
  {
   var active=Entries(l,hand).ToArray();return l.WithRuntime(l.Relics.Select(r=>{
    if(l.RulesFor(r.InstanceId).Programs.Count==0)return r;
    var own=active.Where(e=>e.Item1.InstanceId==r.InstanceId).ToArray();var m=r.Memory;
    foreach(var e in own.Where(e=>e.Item2.OncePerRound))m=m.WithCounter("program/used/"+e.Item2.Id,1,MemoryScope.Round);
    if(hand!=null){m=m.WithCounter("program/previous-straight",hand.MatchesCategory(HandCategory.Straight)?1:0,MemoryScope.Round);
     if(hand.MatchesCategory(HandCategory.Straight))m=m.WithCounter("program/straight",1,MemoryScope.Round);
     if(hand.IsFlush)m=m.WithCounter("program/flush",1,MemoryScope.Round);}
    return r.WithRuntime(r.RemainingCharges.HasValue&&own.Length>0?Math.Max(0,r.RemainingCharges.Value-1):r.RemainingCharges,r.UsesThisRound,r.CooldownRounds,m);
   }));
  }
  internal static IEnumerable<CombatAmountContribution> Amounts(BattleRelicLoadout l,HandAttackInfo hand,CombatAmountContext c,CombatState start)
  {
   foreach(var e in Entries(l,hand)){
    var p=e.Item2;if(p.Kind==RelicProgramKind.DamageMultiply&&c.Stage==CombatAmountStage.Outgoing&&c.Effect.Kind==CombatEffectKind.Damage&&c.Provenance.Origin==CombatEffectOrigin.Primary){
     if(p.StatusThreshold>0&&start.Statuses.Where(s=>s.Target.Id==c.Effect.Target.Value&&s.DefinitionId.Value==p.StatusRef).Sum(s=>s.Stacks)<p.StatusThreshold)continue;
     yield return new CombatAmountContribution(e.Item1.InstanceId,p.Id,rateAdjustment:p.Amount-1);
    }
    if(p.Kind==RelicProgramKind.PoisonDamageMultiply&&c.Stage==CombatAmountStage.Outgoing&&c.Provenance.Origin==CombatEffectOrigin.Periodic&&start.Statuses.Any(s=>s.DefinitionId.Value==p.StatusRef&&s.Source.Id==l.OwnerId.Value&&s.Target.Id==c.Effect.Target.Value))yield return new CombatAmountContribution(e.Item1.InstanceId,p.Id,rateAdjustment:p.Amount-1);
   }
  }
  internal static IEnumerable<CombatReaction> Reactions(BattleRelicLoadout l,HandAttackInfo hand,CombatReactionContext c)
  {
   var hit=c.Event as CombatEvent;if(hit==null||hit.Effect.Kind!=CombatEffectKind.Heal||hit.Effect.Source!=l.OwnerId||hit.Overheal<=0||hand?.IsFlush!=true||hit.Provenance.Origin!=CombatEffectOrigin.Primary)yield break;
   var entries=Entries(l,hand).ToArray();var routes=entries.Where(e=>e.Item2.Kind==RelicProgramKind.OverhealBlock||e.Item2.Kind==RelicProgramKind.OverhealDamage||e.Item2.Kind==RelicProgramKind.OverhealArea).ToArray();
   var route=routes.LastOrDefault(e=>e.Item2.Kind==RelicProgramKind.OverhealArea)??routes.LastOrDefault();if(route==null)yield break;
   decimal factor=route.Item2.Amount;if(route.Item2.Kind!=RelicProgramKind.OverhealBlock)foreach(var e in entries.Where(e=>e.Item2.Kind==RelicProgramKind.OverhealMultiply))factor*=e.Item2.Amount;
   long amount=checked((long)decimal.Floor(hit.Overheal*factor));var own=c.State.Get(l.OwnerId);
   var targets=route.Item2.Kind==RelicProgramKind.OverhealBlock?new[]{own}:c.State.Combatants.Where(u=>u.IsAlive&&u.TeamId!=own.TeamId).Take(route.Item2.Kind==RelicProgramKind.OverhealArea?64:1);
   foreach(var target in targets)yield return new CombatReaction(new CombatEffect(route.Item2.Kind==RelicProgramKind.OverhealBlock?CombatEffectKind.GainBlock:CombatEffectKind.Damage,l.OwnerId,target.Id,amount),route.Item2.Id,l.OwnerId,CombatEffectOrigin.Overheal);
  }
 }
}
