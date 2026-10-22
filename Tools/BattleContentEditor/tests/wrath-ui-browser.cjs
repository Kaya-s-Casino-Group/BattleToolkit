const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const {chromium} = require('playwright');

(async () => {
 const web=path.resolve(__dirname,'../web');
 const server=http.createServer(async(req,res)=>{
  const file=/^\/([a-z-]+\.(?:js|css))$/.exec(req.url);
  if(!file){res.setHeader('Content-Type','text/html');res.end('<link rel="stylesheet" href="/playtest.css"><main id="root"></main>');return;}
  try{res.setHeader('Content-Type',file[1].endsWith('.css')?'text/css':'text/javascript');res.end(await fs.readFile(path.join(web,file[1])));}
  catch{res.statusCode=404;res.end();}
 });
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const browser=await chromium.launch({headless:true,channel:process.env.PLAYWRIGHT_CHANNEL||'chrome'});
 const page=await browser.newPage({viewport:{width:1440,height:1080}});
 try{
  await page.goto('http://127.0.0.1:'+server.address().port);
  const config=await page.evaluate(async()=>{
   const schema=await import('/schema.js'),model=await import('/model.js');
   schema.initialize({enums:{StatusTags:['None'],StatusLifetimeKind:['UntilRemoved'],StatusReapplyPolicy:['KeepLonger'],OpponentTargetSelection:['FirstLiving']}});
   const doc=model.parse('<BattleContent><Statuses><Status id="wrath-shell" lifetime="UntilRemoved"/></Statuses><Enemies><Enemy id="wrath"><Behavior><Wrath/></Behavior></Enemy><Enemy id="hand" scaleHealthWithAdventure="false"/></Enemies></BattleContent>');
   const shell=doc.querySelector('Status'),immunity=model.addChild(doc,shell,'DamageImmunity');
   return {statusChoices:schema.descriptor(shell).children,immunityFields:schema.descriptor(immunity).fields.map(f=>f.key),behaviorChoices:schema.descriptor(doc.querySelector('Behavior')).children,wrathFields:schema.descriptor(doc.querySelector('Wrath')).fields.map(f=>f.key),enemyFields:schema.descriptor(doc.querySelector('Enemy')).fields.map(f=>f.key),xml:model.serialize(doc)};
  });
  assert.ok(config.statusChoices.includes('DamageImmunity'),'editor must offer the distinct damage immunity behavior');
  assert.deepEqual(config.immunityFields,[],'damage immunity has no misleading multiplier options');
  assert.ok(config.behaviorChoices.includes('Wrath'),'behavior editor must offer Wrath configuration');
  for(const field of ['bloodHandRefs','bindingHandRef','baseAttack','attackPerHand','attackPerCharge','chargeActionId','initialWarningRound','cooldownRounds','regenerationRounds','ultimateScale'])assert.ok(config.wrathFields.includes(field),'missing Wrath editor field: '+field);
  assert.ok(config.enemyFields.includes('scaleHealthWithAdventure'));
  assert.match(config.xml,/<DamageImmunity\s*\/>/);
  console.log('PASS wrath configuration, fixed hand health and distinct damage immunity are editable');

  const visible=await page.evaluate(async()=>{
   const {createPlaytest}=await import('/playtest.js');
   const host=createPlaytest(document.querySelector('#root'),{getConfig:()=>({xml:'',encounters:[]}),getToken:()=>'',external:{request:async()=>({})}});
   const fixture={runId:'wrath-ui',revision:'1',seed:'fixture',round:6,phase:'AwaitingPlayerHand',finished:false,actors:['player'],players:[{id:'player',displayName:'玩家',health:'755',maxHealth:'800',block:'20',alive:true,gold:'0',energy:3,maxEnergy:3,actionsRemaining:3,canAct:true,costs:{play:1,discard:1},hand:[],relics:[],statuses:[]}],enemies:[{id:'enemy',displayName:'暴怒',definitionId:'wrath',health:'1300',maxHealth:'1500',block:'0',alive:true,statuses:[],intentMeta:{name:'普攻',damage:'43'},intent:['普攻'],intentSchedule:Array.from({length:8},(_,i)=>({round:6+i,name:['普攻','防御','蓄力','普攻'][i%4]})),wrath:{role:'Body',bodyId:'enemy',attack:'43',charges:1,warningActive:true,releaseRound:6,cooldownRemaining:0}},...Array.from({length:5},(_,i)=>({id:'hand-'+(i+1),displayName:i===4?'束缚之手':'饮血之手 '+(i+1),definitionId:i===4?'wrath-binding':'wrath-blood-'+(i+1),health:'1',maxHealth:'1',block:'0',alive:true,statuses:[],intentMeta:{name:'等待',damage:'0'},intent:['等待'],wrath:{role:i===4?'Binding':'Blood',bodyId:'enemy',reviveRound:null,reviveRemaining:0}}))]};
   fixture.enemies[0].wrath.ultimateBaseDamage='129';fixture.enemies[0].wrath.pendingUltimateBaseDamage='129';
   window.wrathHost=host;window.wrathFixture=fixture;host.setExternalBattle(fixture);
   const body=document.querySelector('[data-participant-id="enemy"]');
   return {count:document.querySelectorAll('.enemy-team .playtest-unit').length,warning:body.querySelector('.wrath-warning')?.textContent,warningClass:body.classList.contains('wrath-warning-active'),stats:body.querySelector('.wrath-stats')?.textContent,schedule:body.querySelector('.intent-schedule')?.textContent,scheduleLabel:body.querySelector('.intent-schedule')?.getAttribute('aria-label'),sequenceNote:body.querySelector('.intent-sequence-note')?.textContent,stepTitles:[...body.querySelectorAll('.intent-step')].map(step=>step.title),steps:body.querySelectorAll('.intent-step').length,ordinaryDamage:body.querySelector('.intent-main b').textContent};
  });
  assert.equal(visible.count,6);assert.equal(visible.warningClass,true,'pending ultimate must visibly mark only the body');
  assert.match(visible.warning,/第 6 轮/);assert.match(visible.warning,/常规动作后/);
  assert.match(visible.warning,/基础伤害 129/);assert.equal(visible.ordinaryDamage,'43');
  assert.match(visible.stats,/43/);assert.match(visible.stats,/蓄力 1/);
  assert.equal(visible.steps,8);assert.doesNotMatch(visible.schedule,/大招|万手/,'ultimate must not consume a regular intent slot');
  assert.match(visible.schedule,/当前 普攻/);assert.match(visible.schedule,/后续 1 防御/);
  assert.match(visible.scheduleLabel,/常规意图序列/);assert.match(visible.sequenceNote,/眩晕时不推进/);
  assert.ok(visible.stepTitles.every(title=>!title.includes('回合')),'stunnable sequence must not promise absolute execution rounds');
  console.log('PASS six units expose authoritative attack, charge count and an independent ultimate warning');

  const charged=await page.evaluate(()=>{
   const next=structuredClone(window.wrathFixture);next.revision='2';
   next.round=12;next.enemies[0].intentMeta={name:'蓄力',damage:'0'};next.enemies[0].intent=['蓄力'];
   next.enemies[0].intentSchedule=Array.from({length:8},(_,i)=>({round:12+i,name:['蓄力','普攻','普攻','防御'][i%4]}));
   next.enemies[0].wrath={...next.enemies[0].wrath,attack:'53',charges:2,releaseRound:12,ultimateBaseDamage:'159',pendingUltimateBaseDamage:'189'};
   window.wrathHost.setExternalBattle(next);
   return document.querySelector('.wrath-warning').textContent;
  });
  assert.match(charged,/基础伤害 189/,'pending damage must use the authoritative value including a preceding charge');
  console.log('PASS extra ultimate damage follows the shared query instead of calculating a client multiplier');

  const cancelled=await page.evaluate(()=>{
   const next=structuredClone(window.wrathFixture);next.revision='3';
   next.enemies[0].wrath={...next.enemies[0].wrath,attack:'38',warningActive:false,releaseRound:null,cooldownRemaining:5,ultimateBaseDamage:'114',pendingUltimateBaseDamage:'114'};
   next.enemies[0].intentMeta.damage='38';
   next.enemies[1].alive=false;next.enemies[1].health='0';next.enemies[1].wrath.reviveRound=14;next.enemies[1].wrath.reviveRemaining=8;
   window.wrathHost.setExternalBattle(next);
   const body=document.querySelector('[data-participant-id="enemy"]'),hand=document.querySelector('[data-participant-id="hand-1"]');
   return {count:document.querySelectorAll('.enemy-team .playtest-unit').length,warning:body.classList.contains('wrath-warning-active'),warnings:body.querySelectorAll('.wrath-warning').length,stats:body.querySelector('.wrath-stats')?.textContent,cooldown:body.querySelector('.wrath-cooldown')?.textContent,revive:hand.querySelector('.wrath-regeneration')?.textContent,dead:hand.classList.contains('defeated')};
  });
  assert.equal(cancelled.count,6);assert.equal(cancelled.warning,false);assert.equal(cancelled.warnings,0);
  assert.match(cancelled.stats,/38/);assert.match(cancelled.cooldown,/5 轮/);
  assert.match(cancelled.cooldown,/基础伤害 114/);
  assert.equal(cancelled.dead,true);assert.match(cancelled.revive,/8 轮/);assert.match(cancelled.revive,/第 14 轮/);
  console.log('PASS a broken hand remains visible with engine-provided regeneration while warning clears and attack updates');

  const regular=await page.evaluate(()=>{
   const next=structuredClone(window.wrathFixture);next.runId='regular-ui';next.revision='1';next.enemies=[{id:'enemy',definitionId:'summoner',displayName:'流血精英',health:'400',maxHealth:'400',block:'0',alive:true,statuses:[],intentMeta:{name:'施法',damage:'4',statusSummary:'伤害后流血 +3层'},intent:['施法'],intentSchedule:[{round:6,name:'施法'},{round:7,name:'攻击'}]}];window.wrathHost.setExternalBattle(next);
   return {bossElements:document.querySelectorAll('.wrath-stats,.wrath-warning,.wrath-cooldown,.wrath-regeneration,.intent-sequence-note').length,intent:document.querySelector('.playtest-intent').textContent,schedule:document.querySelector('.intent-schedule').getAttribute('aria-label'),firstTitle:document.querySelector('.intent-step').title};
  });
  assert.equal(regular.bossElements,0);assert.match(regular.intent,/伤害后流血 \+3层/);
  assert.equal(regular.schedule,'未来八回合意图');assert.equal(regular.firstTitle,'第 6 回合：施法');
  console.log('PASS ordinary and elite displays do not acquire boss UI');
 }finally{await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
