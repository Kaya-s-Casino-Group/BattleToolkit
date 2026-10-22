// Real engine + real DOM regression. Use an isolated host, never an existing user session.
const assert=require('node:assert/strict');
const {chromium}=require('playwright');
const base=process.env.EDITOR_URL;
if(!base)throw new Error('EDITOR_URL must point to an isolated test host.');
(async()=>{
 const browser=await chromium.launch({headless:true,channel:process.env.PLAYWRIGHT_CHANNEL||'chrome'});
 const page=await browser.newPage({viewport:{width:1440,height:1080}}),errors=[];
 page.on('pageerror',error=>errors.push(error.message));
 try{
  await page.route('**/wrath-harness',route=>route.fulfill({contentType:'text/html',body:'<link rel="stylesheet" href="/playtest.css"><main id="playtest-view"></main>'}));
  await page.goto(base+'/wrath-harness');
  await page.evaluate(async()=>{
   const {createPlaytest}=await import('/playtest.js');
   const token=(await(await fetch('/api/bootstrap')).json()).token;
   const original=await(await fetch('/api/standard')).text(),doc=new DOMParser().parseFromString(original,'application/xml');
   doc.querySelector('Player[id="hero"]').setAttribute('maxHealth','10000');
   const immune=doc.querySelector('Status[id="control-immunity"]');immune.setAttribute('lifetime','UntilRemoved');immune.removeAttribute('phase');
   const effect=doc.createElement('Effect');for(const [key,value]of Object.entries({id:'test-immunity',kind:'ApplyStatus',statusRef:'control-immunity',target:'Self'}))effect.setAttribute(key,value);
   doc.querySelector('HandRules Hand[category="HighCard"]').append(effect);
   const sessionId=crypto.randomUUID(),xml=new XMLSerializer().serializeToString(doc);
   const driver=window.driver={sessionId,xml,requests:[],responses:[],floats:[]};
   driver.api=async(op,payload={})=>{driver.requests.push({op,...payload});const data=await(await fetch('/api/playtest',{method:'POST',headers:{'Content-Type':'application/json','X-Editor-Token':token},body:JSON.stringify({op,sessionId,...payload})})).json();driver.responses.push(data);if(op!=='preview'&&data.battle)driver.battle=data.battle;return data;};
   driver.host=createPlaytest(document.querySelector('main'),{getConfig:()=>({xml,encounters:[]}),getToken:()=>token,external:{request:driver.api}});
   driver.start=async(override=xml)=>{const result=await driver.api('start',{xml:override,seed:'wrath-interaction',encounterId:'standard-wrath-thousand-hands',...(driver.battle?{runId:driver.battle.runId}:{})});if(!result.success)throw new Error(JSON.stringify(result));await driver.host.setExternalBattle(result.battle,result.steps);};
   new MutationObserver(records=>{for(const record of records)for(const added of record.addedNodes){if(added.nodeType!==1)continue;for(const item of [...(added.matches('.wrath-regeneration-float')?[added]:[]),...added.querySelectorAll('.wrath-regeneration-float')])driver.floats.push({text:item.textContent,eventId:item.dataset.eventId,phase:document.querySelector('#playtest-phase-banner').textContent,disabled:document.querySelector('#playtest-end-turn').disabled});}}).observe(document.querySelector('main'),{childList:true,subtree:true});
   await driver.start();
  });
  const entry='警告：每存活一只手，暴怒攻击力+5！！',ultimate='警告：Boss有五只手臂，将释放大招！！';
  assert.equal(await page.locator('.wrath-entry-warning').count(),1,'entry warning must appear once for a new battle');
  assert.ok((await page.locator('.wrath-entry-warning').innerText()).includes(entry));
  await page.getByRole('button',{name:'知道了'}).click();
  await page.evaluate(()=>driver.host.render());
  assert.equal(await page.locator('.wrath-entry-warning').isVisible(),false);
  await page.getByText('暴怒机制',{exact:true}).click();
  assert.ok((await page.locator('.wrath-mechanism').innerText()).includes(entry));
  console.log('PASS entry warning is once per run, dismissible and retained as readable mechanics');

  const idle=()=>page.waitForFunction(()=>!document.querySelector('#playtest-end-turn').disabled);
  const selectCard=async()=>{await page.locator('#playtest-hand [data-card-id]').first().click();await page.waitForFunction(()=>!document.querySelector('#playtest-play').disabled);};
  const selectTarget=async(id,key)=>{const button=page.locator('[data-participant-id="'+id+'"] .unit-target');if(key){await button.focus();await button.press(key);}else await button.click();await page.waitForFunction(()=>!document.querySelector('#playtest-play').disabled);};
  const round=async()=>{await page.locator('#playtest-end-turn').click();await idle();if(await page.locator('#playtest-end-turn').innerText()==='确认结束回合'){await page.locator('#playtest-end-turn').click();await idle();}};
  const bodyId=await page.evaluate(()=>driver.battle.enemies.find(unit=>unit.wrath?.role==='Body').id);
  const handId=await page.evaluate(()=>driver.battle.enemies.find(unit=>unit.wrath?.role==='Blood').id);
  await selectCard();
  assert.equal(await page.locator('#playtest-targets option').filter({hasText:'敌人 1 · enemy'}).count(),0);
  await selectTarget(bodyId);
  assert.equal(await page.locator('#playtest-targets select').inputValue(),bodyId);
  assert.equal(await page.locator('[data-participant-id="'+bodyId+'"]').getAttribute('data-target-selected'),'true');
  await selectTarget(handId,'Space');
  assert.equal(await page.locator('#playtest-targets select').inputValue(),handId);
  await page.locator('#playtest-targets select').selectOption(bodyId);await page.waitForFunction(()=>!document.querySelector('#playtest-play').disabled);
  assert.equal(await page.locator('[data-participant-id="'+bodyId+'"]').getAttribute('data-target-selected'),'true');
  await selectTarget(handId,'Enter');
  await page.locator('#playtest-play').click();await idle();
  const dead=page.locator('[data-participant-id="'+handId+'"]');
  assert.equal(await dead.locator('.wrath-regeneration-count').innerText(),'8');
  assert.equal(await dead.locator('.unit-figure .wrath-regeneration-count').count(),1,'the blood-red number belongs at the severed hand position');
  assert.equal(await dead.locator('.unit-target').isDisabled(),true);
  assert.equal(await dead.getAttribute('data-target-selected'),'false');
  assert.equal(await page.locator('.wrath-entry-warning').isVisible(),false);
  console.log('PASS click, Enter, Space and dropdown share one target binding; dead hands are disabled');

  await round();
  let floats=await page.evaluate(()=>driver.floats);
  assert.equal(floats.length,1);assert.equal(floats[0].text,'8个回合后再生');assert.match(floats[0].phase,/敌方阶段/);assert.equal(floats[0].disabled,true);
  assert.equal(await dead.locator('.wrath-regeneration-count').innerText(),'7');
  await selectCard();await page.evaluate(()=>{driver.host.render();driver.host.render();});
  assert.equal(await page.evaluate(()=>driver.floats.length),1,'preview and redraw must not replay an old cue');
  const replay=await page.evaluate(async()=>{const result=[...driver.responses].reverse().find(result=>result.steps?.some(step=>step.wrathPlayback?.length));await driver.host.setExternalBattle(result.battle,result.steps);return driver.floats.length;});
  assert.equal(replay,1,'duplicate committed response must not replay a cue');
  await page.locator('#playtest-clear').click();
  const intentIcon=()=>page.locator('[data-participant-id="'+bodyId+'"] .intent-main use').getAttribute('href');
  assert.equal(await intentIcon(),'/assets/icons.svg#sword');
  await round();assert.equal(await intentIcon(),'/assets/icons.svg#shield');
  await round();assert.equal(await intentIcon(),'/assets/icons.svg#spark');
  for(let i=0;i<6;i++)await round();
  assert.equal(await dead.locator('.wrath-regeneration-count').count(),0);
  floats=await page.evaluate(()=>driver.floats);
  assert.deepEqual(floats.map(item=>item.text),Array.from({length:8},(_,index)=>(8-index)+'个回合后再生'));
  assert.equal(new Set(floats.map(item=>item.eventId)).size,8);
  console.log('PASS phase-entry floats follow authoritative 8→1 timing, deduplicate and stop at regeneration; action IDs select distinct icons');

  await page.evaluate(async()=>{await driver.host.setExternalBattle(null);await driver.start();});
  await selectCard();await selectTarget(handId);await page.locator('#playtest-play').click();await idle();await round();
  assert.equal(await page.evaluate(()=>driver.floats.length),9,'same-seed new runs must not reuse the preceding run\'s seen-float IDs');
  assert.equal(await page.evaluate(()=>driver.floats.at(-1).text),'8个回合后再生');
  console.log('PASS same-seed new battles receive fresh regeneration events after the previous view closes');

  await page.evaluate(()=>driver.start());
  assert.equal(await page.locator('.wrath-entry-warning').isVisible(),true,'a new run gets its own entry warning');
  await page.getByRole('button',{name:'知道了'}).click();
  await selectCard();await selectTarget(bodyId);await page.locator('#playtest-play').click();await idle();
  for(let i=0;i<5;i++)await round();
  assert.match(await page.locator('.wrath-warning').innerText(),new RegExp(ultimate));
  assert.equal(await page.locator('.wrath-warning-active').count(),1);
  assert.equal(await page.locator('[data-participant-id="'+bodyId+'"] .intent-main b').innerText(),'43');
  assert.doesNotMatch(await page.locator('.intent-schedule').innerText(),/大招/);
  await selectCard();await selectTarget(handId);await page.locator('#playtest-play').click();await idle();
  assert.equal(await page.locator('.wrath-warning').count(),0);assert.equal(await page.locator('.wrath-warning-active').count(),0);
  console.log('PASS exact ultimate warning and red glow cancel with a hand death without replacing the ordinary intent');

  await page.evaluate(async()=>{await driver.start();driver.warningFrames=[];new MutationObserver(records=>{for(const record of records)for(const added of record.addedNodes)if(added.nodeType===1){const warning=added.matches('.wrath-warning')?added:added.querySelector('.wrath-warning');if(warning)driver.warningFrames.push({text:warning.textContent,phase:document.querySelector('#playtest-phase-banner').textContent});}}).observe(document.querySelector('main'),{childList:true,subtree:true});});
  for(let i=0;i<5;i++)await round();
  const skipped=await page.evaluate(()=>({frames:driver.warningFrames,round:driver.battle.round}));
  assert.equal(skipped.round,7);assert.ok(skipped.frames.some(frame=>frame.text.includes(ultimate)&&frame.phase.includes('第 6 轮敌方阶段')));
  assert.equal(await page.locator('.wrath-warning').count(),0,'a warning from an automatically skipped player window must not linger after the ultimate');
  console.log('PASS binding skip still shows the authoritative warning before release, then restores the terminal command snapshot');

  const cancelledPlayback=await page.evaluate(async()=>{
   const {createPlaytest}=await import('/playtest.js'),mount=document.createElement('section');document.body.append(mount);
   const view=createPlaytest(mount,{getConfig:()=>({xml:'',encounters:[]}),getToken:()=>'',external:{request:async()=>({})}});
   const result=structuredClone(driver.responses.find(result=>result.steps?.some(step=>step.wrathPlayback?.some(frame=>frame.handFloats?.length))));
   result.steps[0].title='OLD PLAYBACK SHOULD NOT REAPPEAR';
   await view.setExternalBattle({...result.battle,runId:'cancel-old',revision:'0'});
   const pending=view.setExternalBattle({...result.battle,runId:'cancel-old',revision:'1'},result.steps);
   await view.setExternalBattle(null);await view.setExternalBattle({...result.battle,runId:'replacement-run',revision:'1'});await pending;
   const actual={runId:mount.querySelector('#playtest-status').dataset.runId,log:mount.querySelector('#playtest-log').textContent,busy:mount.querySelector('#playtest-end-turn').disabled};mount.remove();return actual;
  });
  assert.equal(cancelledPlayback.runId,'replacement-run');assert.doesNotMatch(cancelledPlayback.log,/OLD PLAYBACK/);assert.equal(cancelledPlayback.busy,false);
  console.log('PASS closing or replacing a battle during playback prevents late UI writes from the old run');

  await page.evaluate(async()=>{const doc=new DOMParser().parseFromString(driver.xml,'application/xml');doc.querySelector('HandRules Hand[category="HighCard"] Effect[kind="Damage"]').setAttribute('selection','AllLiving');await driver.start(new XMLSerializer().serializeToString(doc));});
  await selectCard();
  assert.equal(await page.locator('#playtest-targets select').count(),0);
  assert.equal(await page.locator('.enemy-team .unit-target:not(:disabled)').count(),0);
  assert.equal(await page.locator('[data-target-selected="true"]').count(),0);
  assert.deepEqual(errors,[]);
  console.log('PASS automatic group attacks expose no fake target selection');
 }finally{await page.evaluate(()=>driver?.battle&&driver.api('close',{runId:driver.battle.runId,revision:driver.battle.revision})).catch(()=>{});await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
