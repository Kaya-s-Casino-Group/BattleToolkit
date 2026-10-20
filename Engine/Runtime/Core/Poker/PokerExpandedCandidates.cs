using System.Collections.Generic;
using System.Linq;
namespace PokerEngine
{
 /// <summary>Rank/suit candidates avoid enumerating every five-card combination of a large submitted hand.</summary>
 internal static class PokerExpandedCandidates
 {
  internal static IEnumerable<EffectiveCard[]> Enumerate(EffectiveCard[] cards,int minFlush,int minStraight)
  {
   if(cards.Length<=10) {
    for(int a=0;a<cards.Length-4;a++)for(int b=a+1;b<cards.Length-3;b++)for(int c=b+1;c<cards.Length-2;c++)for(int d=c+1;d<cards.Length-1;d++)for(int e=d+1;e<cards.Length;e++)yield return new[]{cards[a],cards[b],cards[c],cards[d],cards[e]};
    yield break;
   }
   yield return cards.GroupBy(c=>c.Rank).OrderByDescending(g=>(int)g.Key).Take(5).Select(g=>g.First()).ToArray();
   var groups=cards.GroupBy(c=>c.Rank).OrderByDescending(g=>(int)g.Key).Select(g=>g.ToArray()).ToArray();
   foreach(var g in groups)for(int n=1;n<=System.Math.Min(5,g.Length);n++) {
    yield return g.Take(n).ToArray();
    if(n<5)yield return g.Take(n).Concat(groups.Where(other=>other!=g).Take(5-n).Select(other=>other[0])).ToArray();
   }
   foreach(var a in groups.Where(g=>g.Length>=2))foreach(var b in groups.Where(g=>g!=a&&g.Length>=2)){
    yield return a.Take(2).Concat(b.Take(2)).ToArray();
    yield return a.Take(2).Concat(b.Take(2)).Concat(groups.Where(g=>g!=a&&g!=b).Take(1).Select(g=>g[0])).ToArray();
    if(a.Length>=3)yield return a.Take(3).Concat(b.Take(2)).ToArray();
   }
   foreach(var suit in cards.GroupBy(c=>c.Suit)){
    var sorted=suit.OrderByDescending(c=>(int)c.Rank).Take(5).ToArray();if(sorted.Length>=minFlush)yield return sorted;
   }
   foreach(var pool in new[]{cards}.Concat(cards.GroupBy(c=>c.Suit).Select(g=>g.ToArray())))
   for(int n=minStraight;n<=5;n++)for(int top=n;top<=14;top++){
    var ranks=Enumerable.Range(top-n+1,n).Select(r=>r==1?14:r).ToArray();
    if(ranks.All(r=>pool.Any(c=>(int)c.Rank==r)))yield return ranks.Select(r=>pool.First(c=>(int)c.Rank==r)).ToArray();
   }
  }
 }
}
