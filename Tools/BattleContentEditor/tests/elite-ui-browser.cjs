const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const {chromium} = require('playwright');

(async () => {
 const web = path.resolve(__dirname, '../web');
 const server = http.createServer(async (req,res) => {
  const file = /^\/([a-z-]+\.js)$/.exec(req.url);
  if (!file) { res.setHeader('Content-Type','text/html'); res.end('<main id="root"></main>'); return; }
  try { res.setHeader('Content-Type','text/javascript'); res.end(await fs.readFile(path.join(web,file[1]))); }
  catch { res.statusCode=404; res.end(); }
 });
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const browser = await chromium.launch({headless:true,channel:process.env.PLAYWRIGHT_CHANNEL||'chrome'});
 const page = await browser.newPage();
 try {
  await page.goto('http://127.0.0.1:'+server.address().port);
  const actual = await page.evaluate(async () => {
   const schema = await import('/schema.js'), model = await import('/model.js');
   schema.initialize({enums:{PeriodicTimingSubject:['Holder','Applier'],StatusTags:['None','Negative'],StatusLifetimeKind:['UntilRemoved'],StatusReapplyPolicy:['AddStacks']}});
   const doc = model.parse('<BattleContent><Statuses><Status id="elite-bleeding" tags="Negative" reapply="AddStacks" lifetime="UntilRemoved"/></Statuses></BattleContent>');
   const status=doc.querySelector('Status'), choices=schema.descriptor(status).children;
   const incoming=model.addChild(doc,status,'IncomingAttackDamagePerStack'),decay=model.addChild(doc,status,'StackDecay');
   return {choices,incoming:schema.descriptor(incoming).fields.map(f=>f.key),decay:schema.descriptor(decay).fields.map(f=>f.key),xml:model.serialize(doc)};
  });
  assert.ok(actual.choices.includes('IncomingAttackDamagePerStack'),'status editor must offer per-stack incoming attack damage');
  assert.ok(actual.choices.includes('StackDecay'),'status editor must offer independent stack decay');
  assert.deepEqual(actual.incoming,['rate']);
  assert.deepEqual(actual.decay,['timing','timingSubject','stacks']);
  assert.match(actual.xml,/<IncomingAttackDamagePerStack rate="0.5"\s*\/>/);
  assert.match(actual.xml,/<StackDecay timing="ActionCompleted" timingSubject="Applier" stacks="1"\s*\/>/);
  console.log('PASS status editor creates editable bleeding and applier-action decay without periodic damage');

  const configuration = await page.evaluate(async () => {
   const schema=await import('/schema.js'),model=await import('/model.js');
   schema.initialize({enums:{EffectAmountKind:['Constant','ScoredTotal','EnemyAttackScale'],EffectTargetSelection:['FirstLiving']}});
   const doc=model.parse('<BattleContent><Statuses><Status id="elite-bleeding"><IncomingAttackDamagePerStack rate="0.5"/></Status></Statuses><Enemies><Enemy id="summoner"><Behavior attack="90" attackPerRound="10"><Stage><Action><Effects><Effect kind="Damage" amountKind="EnemyAttackScale" amount="0.5"/><Effect kind="ApplyStatus" statusRef="elite-bleeding"/></Effects></Action></Stage></Behavior></Enemy><Enemy id="legacy"><Behavior><Stage><Action><Summon enemyRef="minion"/></Action></Stage></Behavior></Enemy></Enemies><Encounters><Encounter id="current"><Enemy ref="summoner"/></Encounter><Encounter id="old"><Enemy ref="legacy"/></Encounter></Encounters><HandRules><HandRule><Hand><Effect kind="Damage"/></Hand></HandRule></HandRules></BattleContent>');
   return {fields:schema.descriptor(doc.querySelector('Behavior')).fields.map(f=>f.key),enemyKinds:schema.descriptor(doc.querySelector('Effects Effect')).fields.find(f=>f.key==='amountKind').options,handKinds:schema.descriptor(doc.querySelector('Hand Effect')).fields.find(f=>f.key==='amountKind').options,current:model.encounterSummary(doc,doc.querySelector('#current')),old:model.encounterSummary(doc,doc.querySelector('#old'))};
  });
  assert.ok(configuration.fields.includes('attack')&&configuration.fields.includes('attackPerRound'));
  assert.ok(configuration.enemyKinds.includes('EnemyAttackScale'));
  assert.ok(!configuration.handKinds.includes('EnemyAttackScale'),'enemy attack scale must not be offered for player hands');
  assert.equal(configuration.current,'精英 / 攻击成长与流血');
  assert.equal(configuration.old,'精英 / 护甲与召唤','legacy summon template retains its actual behavior label');
  console.log('PASS attack-growth configuration and encounter labels distinguish new and legacy elites');

  const visible = await page.evaluate(async () => {
   const {createPlaytest}=await import('/playtest.js');
   const host=createPlaytest(document.querySelector('#root'),{getConfig:()=>({xml:'',encounters:[]}),getToken:()=>'',external:{request:async()=>({})}});
   host.setExternalBattle({runId:'elite-ui',revision:'1',seed:'fixture',round:2,phase:'AwaitingPlayerHand',finished:false,actors:['player'],players:[{
    id:'player',displayName:'玩家',health:'755',maxHealth:'800',block:'20',alive:true,gold:'0',energy:3,maxEnergy:3,actionsRemaining:3,canAct:true,costs:{play:1,discard:1},hand:[],relics:[],statuses:[{
     id:'bleeding-1',definitionId:'elite-bleeding',displayName:'流血',name:'流血 · 每层使敌人对玩家攻击伤害 +50%，盾前合成；施加者每次实际行动后减1层',description:'每层使敌人对玩家攻击伤害 +50%，盾前合成；施加者每次实际行动后减1层',behaviorKeys:['IncomingAttackDamagePerStack','StackDecay'],isNegative:true,stacks:2,duration:'持续生效'
    }]}],enemies:[{id:'enemy',displayName:'流血精英',definitionId:'summoner',health:'1800',maxHealth:'1800',block:'0',alive:true,statuses:[],intentMeta:{name:'施法',damage:'100',statusSummary:'流血 +3层；实际行动结束后减1层',damageDetails:['流血增伤：50→100，+50']},intent:['玩家：伤害100','流血 +3层；实际行动结束后减1层','流血增伤：50→100，+50']}]});
   const badge=document.querySelector('.playtest-status'),intent=document.querySelector('.playtest-intent');
   return {tooltip:badge.title,icon:badge.querySelector('use').getAttribute('href'),intent:intent.textContent};
  });
  assert.match(visible.tooltip,/敌人对玩家攻击/,'state tooltip must expose mechanics even when displayName exists');
  assert.equal(visible.icon,'/assets/icons.svg#bleed');
  assert.match(visible.intent,/流血 \+3层/,'cast debuff must be visible without hover');
  assert.match(visible.intent,/50→100/,'merged damage must retain authoritative bleeding contribution');
  console.log('PASS battle renders bleeding rules and cast/merged-damage details visibly');
 } finally { await browser.close(); await new Promise(resolve=>server.close(resolve)); }
})().catch(error=>{console.error(error);process.exitCode=1;});
