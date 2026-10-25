using System;
using System.Linq;
using System.Xml;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class DesignItem2951Tests
    {
        private static readonly ParticipantId P = new ParticipantId("player"), E = new ParticipantId("enemy");
        private static BattleSession Load(string[] gear, Action<XmlDocument> edit = null, int seed = 0)
        {
            var d = new XmlDocument(); d.LoadXml(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle-2026-10-21.xml"));
            var loadout = d.SelectSingleNode("//Loadout[@id='baseline']");
            loadout.InnerXml = string.Join("", gear.Select((id,i) => "<Relic ref='"+id+"' instanceId='gear-"+i+"'/>"));
            var enemy = (XmlElement)d.SelectSingleNode("//Enemy[@id='rotator']"); enemy.InnerXml = "";
            enemy.SetAttribute("maxHealth", "1000000"); enemy.SetAttribute("damage", "0");
            ((XmlElement)d.SelectSingleNode("//Encounter[@id='standard-single-battle']/Player")).SetAttribute("initialHealth","500");
            edit?.Invoke(d);
            var compiled = BattleContentCompiler.CompileXml(d.OuterXml);
            Assert.IsTrue(compiled.Success, string.Join(";",compiled.Diagnostics.Select(x=>x.Message)));
            var bytes = new byte[32]; bytes[0]=(byte)seed;
            var result = BattleContentFactory.Create(compiled.Content,new BattleSetupRequest("standard-single-battle","items-2951",HmacDeckShuffler.FromSeed(bytes)));
            Assert.IsTrue(result.Success,string.Join(";",result.Diagnostics.Select(x=>x.Message)));
            Act(result.Session,CardDuelAction.Start); return result.Session;
        }
        private static BattleSessionResult Act(BattleSession s,CardDuelAction action,CardInstanceId[] cards=null)
        { var r=s.Execute(s.State.Revision,P,action,cards); Assert.IsTrue(r.Accepted,action+":"+r.Error);return r; }
        private static BattleRelicState Item(BattleSession s,string definition) => s.State.Duel.Player(P).Relics.Relics.First(r=>r.DefinitionId==definition);
        private static BattleSessionResult Use(BattleSession s,string definition,string target=null)
        { var r=s.UseItem(s.State.Revision,P,Item(s,definition).InstanceId,target); Assert.IsTrue(r.Accepted,definition+":"+r.Error);return r; }
        private static long Hp(BattleSession s,ParticipantId id) => s.State.Duel.Combat.Get(id).Health;
        private static void Deck(XmlDocument d,params int[] ranks)
        { d.SelectSingleNode("//Deck[@id='standard-deck']").InnerXml=string.Join("",ranks.Select((r,i)=>"<Card id='test-"+i+"' face='standard:"+(i%4+1)+":"+r+"'/>")); }
        private static void EnemyCount(XmlDocument d,int count)
        { for(int i=1;i<count;i++){var e=d.CreateElement("Enemy");e.SetAttribute("id","enemy-"+i);e.SetAttribute("ref","rotator");d.SelectSingleNode("//Encounter[@id='standard-single-battle']").AppendChild(e);} }
        private static CombatEvent[] Events(BattleSessionResult r) => r.Steps.SelectMany(s=>s.Transition.Events).ToArray();

        [Test] public void ConsumablesRemoveTheirOwnInstanceAndDoNotSpendActions()
        {
            var s=Load(new[]{"10002","10002","11004","11004"},d=>EnemyCount(d,2));
            int actions=s.State.FlowFor(P).ActionsRemaining; Use(s,"10002");
            Assert.AreEqual(510,Hp(s,P)); Assert.AreEqual(1,s.State.Duel.Player(P).Relics.Relics.Count(r=>r.DefinitionId=="10002"));
            var first=Item(s,"11004").InstanceId;
            for(int i=0;i<3;i++){var r=s.UseItem(s.State.Revision,P,first);Assert.IsTrue(r.Accepted,r.Error.ToString());}
            Assert.IsFalse(s.State.Duel.Player(P).Relics.Relics.Any(r=>r.InstanceId==first));
            Assert.AreEqual(3,Item(s,"11004").RemainingCharges);Assert.AreEqual(999940,Hp(s,E));
            Assert.AreEqual(actions,s.State.FlowFor(P).ActionsRemaining);Assert.AreEqual(3,s.State.Duel.Player(P).Energy.Energy);
        }
        [Test] public void FreeItemsRemainUsableWithActionsButNoSeparateEnergy()
        {
            // This regression deliberately exercises the retained legacy two-budget contract.
            var s=Load(new[]{"10002"},d=>{
                var player=(XmlElement)d.SelectSingleNode("//Player[@id='hero']");
                player.SetAttribute("energyOnly","false"); player.SetAttribute("playEnergyCost","3");
            });
            var card=s.State.Zone(CardZoneKinds.Hand,P).Cards.First();
            Act(s,CardDuelAction.Play,new[]{card});
            Assert.AreEqual(0,s.State.Duel.Player(P).Energy.Energy);
            Assert.AreEqual(2,s.State.FlowFor(P).ActionsRemaining);
            Assert.IsTrue(BattleItemRuntime.CanUse(s.State,P,Item(s,"10002").InstanceId));
            Use(s,"10002");
            Assert.AreEqual(510,Hp(s,P));Assert.AreEqual(0,s.State.Duel.Player(P).Energy.Energy);
            Assert.AreEqual(2,s.State.FlowFor(P).ActionsRemaining);
        }
        [Test] public void EnemyLethalSequenceConsumesOneReviverAndAbortsEntireEnemyPhase()
        {
            var s=Load(new[]{"13004","13004"},d=>{
                EnemyCount(d,3);var e=(XmlElement)d.SelectSingleNode("//Enemy[@id='rotator']");e.RemoveAttribute("damage");
                e.InnerXml="<Effects><Effect id='first' kind='Damage' amount='1000'/><Effect id='second' kind='Damage' amount='1000'/></Effects>";
            });
            var r=Act(s,CardDuelAction.EndTurn);
            Assert.AreEqual(480,Hp(s,P));Assert.AreEqual(1,s.State.Duel.Player(P).Relics.Relics.Count);
            Assert.AreEqual(1,Events(r).Count(e=>e.Effect.Kind==CombatEffectKind.Revive));
            Assert.AreEqual(1,Events(r).Count(e=>e.Effect.Kind==CombatEffectKind.Damage&&e.Effect.Source!=P));
            Assert.AreEqual(ScoredDuelPhase.AwaitingPlayerHand,s.State.Duel.Phase);
            Assert.AreEqual(3,s.State.FlowFor(P).ActionsRemaining);Assert.AreEqual(8,s.State.Zone(CardZoneKinds.Hand,P).Cards.Count);
        }
        [Test] public void PoisonLeafDeathCanReviveAndClearsAllStatusesAndBlock()
        {
            var s=Load(new[]{"13004"},d=>{
                var r=d.CreateElement("Relic");r.SetAttribute("id","seed-poison");
                r.InnerXml="<BoundaryTrigger id='start' timing='BattleStarted'><Effect kind='ApplyStatus' statusRef='bleed' stacks='900' target='Self'/><Effect kind='GainBlock' amount='1' target='Self'/></BoundaryTrigger>";
                d.SelectSingleNode("/BattleContent/Relics").AppendChild(r);
                var slot=d.CreateElement("Relic");slot.SetAttribute("ref","seed-poison");slot.SetAttribute("instanceId","seed");d.SelectSingleNode("//Loadout[@id='baseline']").AppendChild(slot);
            });
            Assert.AreEqual(480,Hp(s,P));Assert.AreEqual(0,s.State.Duel.Combat.Get(P).Block);
            Assert.IsEmpty(s.State.Duel.Combat.Statuses);Assert.IsFalse(s.State.Duel.Player(P).Relics.Relics.Any(r=>r.DefinitionId=="13004"));
        }
        [Test] public void ApplierTurnPoisonReviveCancelsTheUnstartedEnemyAndFollowingEnemies()
        {
            var s=Load(new[]{"13004","13004"},d=>{
                EnemyCount(d,2);var periodic=(XmlElement)d.SelectSingleNode("//Status[@id='bleed']/PeriodicDamage");periodic.SetAttribute("timingSubject","Applier");
                var e=(XmlElement)d.SelectSingleNode("//Enemy[@id='rotator']");e.RemoveAttribute("damage");
                e.InnerXml="<Effects><Effect id='poison' kind='ApplyStatus' statusRef='bleed' stacks='900' target='Opponents'/><Effect id='damage' kind='Damage' amount='1'/></Effects>";
            });
            Act(s,CardDuelAction.EndTurn);
            var r=Act(s,CardDuelAction.EndTurn);
            Assert.AreEqual(480,Hp(s,P));Assert.AreEqual(1,Events(r).Count(e=>e.Effect.Kind==CombatEffectKind.Revive));
            Assert.IsFalse(Events(r).Any(e=>e.Effect.Kind==CombatEffectKind.Damage&&e.Provenance.Origin==CombatEffectOrigin.Primary));
            Assert.AreEqual(1,s.State.Duel.Player(P).Relics.Relics.Count);Assert.IsEmpty(s.State.Duel.Combat.Statuses);
        }
        [Test] public void RouletteGrowsByTurnNotByUseAndDoesNotAmplifyHealingOrDiscardShield()
        {
            BattleSession s=null;
            for(int seed=0;seed<100;seed++){
                var candidate=Load(new[]{"15001","10002"},seed:seed);Use(candidate,"15001");
                if(Item(candidate,"15001").Memory.CounterValue(BattleItemRuntime.RouletteBombKey)>=4){s=candidate;break;}
            }
            Assert.NotNull(s);var before=s.State;var card=before.Zone(CardZoneKinds.Hand,P).Cards.First();
            Use(s,"10002");Assert.AreEqual(510,Hp(s,P));
            var discarded=Act(s,CardDuelAction.Discard,new[]{card});
            var gain=Events(discarded).Single(e=>e.Effect.Kind==CombatEffectKind.GainBlock);
            Assert.AreEqual(s.Engine.Face(before.Table.GetCard(card)).Rank==Rank.Ace?1:(int)s.Engine.Face(before.Table.GetCard(card)).Rank,gain.Effect.Amount);
            Assert.AreEqual(10,BattleItemRuntime.HandMultiplier(s.State.Duel.Player(P).Relics));
            Act(s,CardDuelAction.EndTurn);Assert.AreEqual(100,BattleItemRuntime.HandMultiplier(s.State.Duel.Player(P).Relics));
            Act(s,CardDuelAction.EndTurn);Assert.AreEqual(1000,BattleItemRuntime.HandMultiplier(s.State.Duel.Player(P).Relics));
        }
        [Test] public void DiceExcludesOriginalHeldUniqueAndDiceAndCreatesFreshCharges()
        {
            var s=Load(new[]{"14001","10002","10001"},d=>{
                foreach(XmlNode node in d.SelectNodes("/BattleContent/Relics/Relic").Cast<XmlNode>().Where(n=>!new[]{"14001","10002","10001","11004"}.Contains(n.Attributes["id"].Value)).ToArray())node.ParentNode.RemoveChild(node);
                d.SelectSingleNode("//Loadout[@id='item-lab']").InnerXml="";
            });
            var old=Item(s,"10002").InstanceId;Use(s,"14001",old);
            Assert.IsFalse(s.State.Duel.Player(P).Relics.Relics.Any(r=>r.InstanceId==old));Assert.AreEqual(3,Item(s,"11004").RemainingCharges);
            var dice=Item(s,"14001");Assert.AreEqual(6,dice.Memory.CounterValue(BattleItemRuntime.CooldownBattlesKey));
            Assert.IsFalse(BattleItemRuntime.CanUse(s.State,P,dice.InstanceId));
            var persistent=s.State.Duel.Player(P).Relics.ExportRunRelics().Single(r=>r.DefinitionId=="14001");
            for(int i=0;i<6;i++)persistent=BattleItemRuntime.CompleteBattle(persistent);
            Assert.AreEqual(0,persistent.Memory.CounterValue(BattleItemRuntime.CooldownBattlesKey));
        }
        [Test] public void ChanceExecutionUsesEachDirectTargetButNeverGrenadesOrShieldOnlyDamage()
        {
            Action<XmlDocument> modify=d=>{EnemyCount(d,3);Deck(d,3,3,3);((XmlElement)d.SelectSingleNode("//Relic[@id='13002']/Item")).SetAttribute("executeChance","1");};
            var s=Load(new[]{"13002","11004"},modify);var grenade=Use(s,"11004");
            Assert.IsFalse(Events(grenade).Any(e=>e.Effect.Kind==CombatEffectKind.Eliminate));
            var r=Act(s,CardDuelAction.Play,s.State.Zone(CardZoneKinds.Hand,P).Cards.ToArray());
            Assert.AreEqual(3,Events(r).Count(e=>e.Effect.Kind==CombatEffectKind.Eliminate));Assert.AreEqual(ScoredDuelPhase.Won,s.State.Duel.Phase);
            var shielded=Load(new[]{"13002"},d=>{modify(d);((XmlElement)d.SelectSingleNode("//Enemy[@id='rotator']")).SetAttribute("initialBlock","1000");});
            var blocked=Act(shielded,CardDuelAction.Play,shielded.State.Zone(CardZoneKinds.Hand,P).Cards.ToArray());
            Assert.IsFalse(Events(blocked).Any(e=>e.Effect.Kind==CombatEffectKind.Eliminate));
        }
        [Test] public void ExecutionPreviewMatchesCommitWithoutConsumingTheLiveRandomCursor()
        {
            var s=Load(new[]{"13002"},d=>Deck(d,2,3,4,5,6));var before=s.State;var cards=before.Zone(CardZoneKinds.Hand,P).Cards.ToArray();
            var preview=s.Engine.PreviewPlay(before,before.Revision,P,cards);Assert.IsTrue(preview.Accepted);Assert.AreSame(before,s.State);
            var result=Act(s,CardDuelAction.Play,cards);Assert.AreEqual(preview.State.Duel.Combat.Get(E).Health,Hp(s,E));
            CollectionAssert.AreEqual(preview.State.Random.StateBytes,s.State.Random.StateBytes);
            Assert.IsFalse(before.Random.StateBytes.SequenceEqual(s.State.Random.StateBytes));
        }
        [Test] public void RouletteImmediateExplosionRevivesWithinSameTurnAndCanBeReused()
        {
            BattleSession chosen=null;BattleSessionResult use=null;CardInstanceId[] hand=null;
            for(int seed=0;seed<100;seed++){
                var s=Load(new[]{"15001","13004","13004"},seed:seed);hand=s.State.Zone(CardZoneKinds.Hand,P).Cards.ToArray();
                var r=Use(s,"15001");if(Events(r).Any(e=>e.Effect.Kind==CombatEffectKind.Revive)){chosen=s;use=r;break;}
            }
            Assert.NotNull(chosen);Assert.AreEqual(480,Hp(chosen,P));Assert.AreEqual(3,chosen.State.FlowFor(P).ActionsRemaining);
            CollectionAssert.AreEqual(hand,chosen.State.Zone(CardZoneKinds.Hand,P).Cards);
            Assert.AreEqual(1,BattleItemRuntime.HandMultiplier(chosen.State.Duel.Player(P).Relics));
            Assert.IsTrue(BattleItemRuntime.CanUse(chosen.State,P,Item(chosen,"15001").InstanceId));
            Assert.AreEqual(int.MaxValue,Events(use).Single(e=>e.Effect.Kind==CombatEffectKind.Damage).Effect.Amount);
        }
        [Test] public void RouletteFixedNextTurnExplosionInitializesExactlyOneTurn()
        {
            BattleSession s=null;
            for(int seed=0;seed<100;seed++){
                var candidate=Load(new[]{"15001","13004"},seed:seed);Use(candidate,"15001");
                if(Item(candidate,"15001").Memory.CounterValue(BattleItemRuntime.RouletteBombKey)==2){s=candidate;break;}
            }
            Assert.NotNull(s);Assert.AreEqual(10,BattleItemRuntime.HandMultiplier(s.State.Duel.Player(P).Relics));
            Assert.IsFalse(BattleItemRuntime.CanUse(s.State,P,Item(s,"15001").InstanceId));
            var r=Act(s,CardDuelAction.EndTurn);Assert.AreEqual(480,Hp(s,P));Assert.AreEqual(1,s.State.Duel.CompletedRounds);
            Assert.AreEqual(3,s.State.FlowFor(P).ActionsRemaining);Assert.AreEqual(8,s.State.Zone(CardZoneKinds.Hand,P).Cards.Count);
            Assert.AreEqual(1,Events(r).Count(e=>e.Effect.Kind==CombatEffectKind.Revive));Assert.AreEqual(1,BattleItemRuntime.HandMultiplier(s.State.Duel.Player(P).Relics));
            Assert.IsFalse(s.State.Duel.Player(P).Relics.Relics.Any(x=>x.DefinitionId=="13004"));
        }
        [Test] public void MidHandRevivalClearsRouletteBeforeRemainingDamageSegments()
        {
            BattleSession s=null;
            for(int seed=0;seed<100;seed++) {
                var candidate=Load(new[]{"15001","13004"},d=>{
                    Deck(d,2,3,4,5,6);((XmlElement)d.SelectSingleNode("//Encounter[@id='standard-single-battle']/Player")).SetAttribute("initialHealth","30");
                    ((XmlElement)d.SelectSingleNode("//Enemy[@id='rotator']")).SetAttribute("initialBlock","1000000");
                    var r=d.CreateElement("Relic");r.SetAttribute("id","reflect-seed");
                    r.InnerXml="<BoundaryTrigger id='seed' timing='BattleStarted'><Effect kind='ApplyStatus' statusRef='shield-reflect' target='Opponents'/></BoundaryTrigger>";
                    d.SelectSingleNode("/BattleContent/Relics").AppendChild(r);
                    var slot=d.CreateElement("Relic");slot.SetAttribute("ref","reflect-seed");slot.SetAttribute("instanceId","reflection");d.SelectSingleNode("//Loadout[@id='baseline']").AppendChild(slot);
                },seed);
                Use(candidate,"15001");if(BattleItemRuntime.HandMultiplier(candidate.State.Duel.Player(P).Relics)==10){s=candidate;break;}
            }
            Assert.NotNull(s);var result=Act(s,CardDuelAction.Play,s.State.Zone(CardZoneKinds.Hand,P).Cards.ToArray());
            CollectionAssert.AreEqual(new long[]{40,6,8,10,12},Events(result).Where(e=>e.Effect.Kind==CombatEffectKind.Damage&&e.Effect.Source==P&&e.Effect.Target==E).Select(e=>e.BlockAbsorbed));
            Assert.AreEqual(444,Hp(s,P));Assert.AreEqual(1,BattleItemRuntime.HandMultiplier(s.State.Duel.Player(P).Relics));
        }
        [Test] public void ItemVictoryDispatchesBattleEndedExactlyOnce()
        {
            var s=Load(new[]{"11004"},d=>{
                ((XmlElement)d.SelectSingleNode("//Enemy[@id='rotator']")).SetAttribute("maxHealth","20");
                d.SelectSingleNode("//Relic[@id='11004']").InnerXml += "<Budget chargeCost='0'/><BoundaryTrigger id='end' timing='BattleEnded'><Effect kind='Heal' amount='4' target='Self'/></BoundaryTrigger>";
            });
            var r=Use(s,"11004");Assert.AreEqual(ScoredDuelPhase.Won,s.State.Duel.Phase);Assert.AreEqual(504,Hp(s,P));
            Assert.AreEqual(1,r.Steps.SelectMany(x=>x.Transition.RuleEvents).OfType<CombatTimingEvent>().Count(e=>e.Signal.Kind==CombatTimingKind.BattleEnded));
            Assert.AreEqual(1,Events(r).Count(e=>e.Effect.Kind==CombatEffectKind.Heal));
        }
        [Test] public void ReviveDoesNotRoundOneMaximumHealthUpFromZero()
        {
            var s=Load(new[]{"13004","13004"},d=>{
                ((XmlElement)d.SelectSingleNode("//Player[@id='hero']")).SetAttribute("maxHealth","1");
                foreach(XmlElement player in d.SelectNodes("//Encounter/Player[@ref='hero']")) player.SetAttribute("initialHealth","1");
                ((XmlElement)d.SelectSingleNode("//Enemy[@id='rotator']")).SetAttribute("damage","1");
            });
            var r=Act(s,CardDuelAction.EndTurn);Assert.AreEqual(ScoredDuelPhase.Lost,s.State.Duel.Phase);Assert.AreEqual(0,Hp(s,P));
            Assert.AreEqual(1,s.State.Duel.Player(P).Relics.Relics.Count);Assert.AreEqual(0,Events(r).Single(e=>e.Effect.Kind==CombatEffectKind.Revive).After.Health);
        }
        [Test] public void RouletteMultipliesAfterOrdinaryDamageRoundingIncludingFixedExtraHits()
        {
            BattleSession s=null;
            for(int seed=0;seed<100;seed++) {
                var candidate=Load(new[]{"15001"},d=>{
                    Deck(d,2);var extra=d.CreateElement("Relic");extra.SetAttribute("id","rounding-check");
                    extra.InnerXml="<Program id='fraction' kind='DamageMultiply' amount='0.34'/><HandModifier id='extra' category='HighCard' operation='AppendEffect'><Effect kind='Damage' amount='5' target='Opponents'/></HandModifier>";
                    d.SelectSingleNode("/BattleContent/Relics").AppendChild(extra);
                    var slot=d.CreateElement("Relic");slot.SetAttribute("ref","rounding-check");slot.SetAttribute("instanceId","extra");d.SelectSingleNode("//Loadout[@id='baseline']").AppendChild(slot);
                },seed);
                Use(candidate,"15001");if(BattleItemRuntime.HandMultiplier(candidate.State.Duel.Player(P).Relics)==10){s=candidate;break;}
            }
            Assert.NotNull(s);var cards=s.State.Zone(CardZoneKinds.Hand,P).Cards.ToArray();
            var preview=s.Engine.PreviewPlay(s.State,s.State.Revision,P,cards);Assert.IsTrue(preview.Accepted);
            var result=Act(s,CardDuelAction.Play,cards);
            // Base 2*.34 floors to 0; fixed extra 5*.34 floors to 1; only then apply x10.
            Assert.AreEqual(999990,Hp(s,E));Assert.AreEqual(preview.State.Duel.Combat.Get(E).Health,Hp(s,E));
            CollectionAssert.AreEqual(new long[]{0,10},Events(result).Where(e=>e.Effect.Kind==CombatEffectKind.Damage&&e.Effect.Target==E).Select(e=>e.DamageToHealth));
        }
        [Test] public void ReflectUsesActualShieldAbsorptionAndRemainsAnInventoryPassive()
        {
            var s=Load(new[]{"12001"},d=>{((XmlElement)d.SelectSingleNode("//Enemy[@id='rotator']")).SetAttribute("damage","100");});
            var cards=s.State.Zone(CardZoneKinds.Hand,P).Cards.Take(3).ToArray();var discard=Act(s,CardDuelAction.Discard,cards);
            long block=s.State.Duel.Combat.Get(P).Block;Assert.Greater(block,0);
            var end=Act(s,CardDuelAction.EndTurn);var reflection=Events(end).Single(e=>e.Provenance.Origin==CombatEffectOrigin.Reflection);
            Assert.AreEqual(decimal.Floor(Math.Min(100,block)*.2m),reflection.Effect.Amount);
        }
    }
}
