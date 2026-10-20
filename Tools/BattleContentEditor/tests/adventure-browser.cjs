const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const {chromium}=require('playwright');
const base=process.env.EDITOR_URL||process.env.BASE_URL||'http://127.0.0.1:18767';
const saveKey='battle-adventure.save.v1';

(async()=>{
 const browser=await chromium.launch({headless:true,channel:process.env.PLAYWRIGHT_CHANNEL||undefined});
 const context=await browser.newContext({viewport:{width:1440,height:1000},acceptDownloads:true});
 const page=await context.newPage(),errors=[];let passes=0,current;
 page.on('pageerror',error=>errors.push(error.message));
 const test=async(name,fn)=>{await fn();passes++;console.log('PASS '+name);};
 const waitReady=()=>page.waitForFunction(()=>document.querySelector('#adventure-menu')&&!document.querySelector('#adventure-menu').disabled);
 const responseFor=(op,action)=>page.waitForResponse(response=>{
  if(!response.url().endsWith('/api/adventure'))return false;
  const request=response.request().postDataJSON();return request.op===op&&(!action||request.action===action);
 },{timeout:30000});
 const click=async(selector,op,action)=>{
  const pending=responseFor(op,action);await page.locator(selector).click();
  const data=await(await pending).json();if(data.adventure)current=data.adventure;
  assert.equal(data.success,true,JSON.stringify(data));await waitReady();return data;
 };
 const saved=()=>page.evaluate(key=>localStorage.getItem(key),saveKey);
 const fixture=(await fs.readFile(path.resolve(__dirname,'../../../Engine/Content/StandardAdventure.xml'),'utf8'))
  .replace('startNode="chapter-1-battle-1"','startNode="test-battle"')
  .replace('startingGold="30"','startingGold="200"')
  .replace('name="愚者"','name="愚者·浏览器测试"')
  .replace(/<Nodes>[\s\S]*?<\/Nodes>/,`<Nodes>
    <Node id="test-battle" name="不应提前显示的怪物" kind="Battle" chapter="1" row="0" column="1" encounter="standard-single-battle" enemyHealthMultiplier="0.01" gold="20" next="test-shop" />
    <Node id="test-shop" name="测试商店" kind="Shop" chapter="1" row="1" column="0" next="test-rest" />
    <Node id="test-rest" name="测试营地" kind="Rest" chapter="1" row="2" column="2" next="test-boss" />
    <Node id="test-boss" name="不应提前显示的首领" kind="Boss" chapter="1" row="3" column="1" encounter="standard-gluttony-boss" enemyHealthMultiplier="0.01" gold="60" next="test-boss-2" />
    <Node id="test-boss-2" name="第二章测试首领" kind="Boss" chapter="2" row="0" column="1" encounter="standard-single-battle" enemyHealthMultiplier="0.01" gold="60" />
   </Nodes>`);
 const startSetup=async()=>{
  const pending=responseFor('catalog');await page.locator('#adventure-new').click();await pending;await waitReady();
 };
 const finishBattle=async()=>{
  for(let attempt=0;attempt<30&&current.status==='Battle';attempt++){
   const player=current.battle.players[0];
   if(player.window==='Draw'){await click('#playtest-skip-draw','battle','SkipDraw');continue;}
   if(player.window==='Ending'){await click('#playtest-end-turn','battle','FinishTurn');continue;}
   const cards=page.locator('#playtest-hand [data-card-id]');
   if(await cards.count()){
    const preview=responseFor('preview','Play');await cards.first().click();await preview;
    await page.waitForFunction(()=>!document.querySelector('#playtest-play').disabled);
    await click('#playtest-play','battle','Play');
   }else await click('#playtest-end-turn','battle','EndTurn');
  }
  assert.notEqual(current.status,'Battle','弱化测试怪应当可以在有限操作内击败');
 };
 try{
  await page.goto(base+'/adventure.html');await page.waitForSelector('#adventure-new');
  await test('主菜单提供开始、继续和只读塔罗图鉴',async()=>{
   assert.equal(await page.locator('#adventure-continue').isDisabled(),true);
   await page.locator('#adventure-home-tarots').click();
   assert.equal(await page.locator('.tarot-card').count(),3);
   assert.equal(await page.locator('.tarot-card.locked').count(),2);
   await page.locator('[data-tarot-id="magician"]').click();
   assert.match(await page.locator('.tarot-detail').innerText(),/通关|完成一次冒险/);
   assert.equal(await page.locator('#adventure-start').count(),0);
   await page.getByRole('button',{name:'← 返回',exact:true}).click();
  });
  await test('可编辑局外XML，角色和天赋均须主动选择',async()=>{
   await page.locator('#adventure-config summary').click();
   await page.locator('#adventure-config-xml').fill(fixture);
   await startSetup();
   assert.equal(await page.locator('#adventure-character-next').isDisabled(),true);
   assert.equal(await page.locator('.character-card[aria-pressed=true]').count(),0);
   await page.locator('.character-card').first().click();
   await page.locator('#adventure-seed').fill('冒险浏览器验证');
   await page.locator('#adventure-character-next').click();
   assert.match(await page.locator('[data-tarot-id="fool"]').innerText(),/愚者·浏览器测试/);
   assert.equal(await page.locator('#adventure-start').isDisabled(),true);
   await page.locator('[data-tarot-id="magician"]').click();
   assert.equal(await page.locator('#adventure-start').isDisabled(),true);
   await page.locator('[data-tarot-id="fool"]').click();
   const data=await click('#adventure-start','start');
   assert.equal(data.adventure.player.maxHealth,'960');
   assert.equal(data.adventure.player.gold,'200');
   assert.equal(data.adventure.status,'Map');
   assert.ok((await saved()).includes('冒险浏览器验证'));
  });
  await test('地图只有当前可达节点可点击且不泄露未来具体怪物',async()=>{
   assert.equal(await page.locator('.map-node:enabled').count(),1);
   assert.equal(await page.locator('.map-node[data-node-id="test-battle"]').isEnabled(),true);
   assert.doesNotMatch(await page.locator('.route-map').innerText(),/轮换小怪|暴食|不应提前显示/);
   assert.equal(await page.locator('.journey-side').count(),0);
   if(process.env.ADVENTURE_SCREENSHOT)await page.screenshot({path:process.env.ADVENTURE_SCREENSHOT.replace(/\.png$/, '-map.png'),fullPage:true});
  });
  await test('牌库浮层展示持有52张牌并支持花色点数排序',async()=>{
   await page.locator('#adventure-deck').click();
   assert.equal(await page.locator('.adventure-playing-card').count(),52);
   await page.getByLabel('牌库排列方式').selectOption('rank');
   const first=await page.locator('.adventure-playing-card .card-corner').first().innerText();
   assert.match(first,/A/);
   await page.locator('#adventure-dialog-close').click();
  });
  await test('地图进入真实战斗并嵌入既有出牌界面',async()=>{
   const data=await click('[data-node-id="test-battle"]','command','enter');
   assert.equal(data.adventure.status,'Battle');
   await page.waitForSelector('#playtest-hand [data-card-id]');
   assert.equal(await page.locator('#playtest-start').isVisible(),false);
   assert.equal(await page.locator('#playtest-close').isVisible(),false);
   assert.equal(await page.locator('#playtest-hand [data-card-id]').count(),8);
  });
  await test('战斗预览不改变冒险存档，胜利奖励由服务器结算',async()=>{
   if(current.battle.players[0].window==='Draw')await click('#playtest-skip-draw','battle','SkipDraw');
   const before=await saved(),revision=current.revision;
   const preview=responseFor('preview','Play');await page.locator('#playtest-hand [data-card-id]').first().click();
   const data=await(await preview).json();assert.equal(data.success,true);assert.equal(data.adventure.revision,revision);assert.equal(await saved(),before);
   await page.locator('#playtest-clear').click();
   await finishBattle();assert.equal(current.status,'Reward');
   assert.equal(current.player.gold,'220');
   await page.waitForSelector('#adventure-reward-continue');
   await click('#adventure-reward-continue','command','continue');
   assert.equal(current.status,'Map');
  });
  await test('商店显示真实价格，购买遗物和改牌服务立即回写存档',async()=>{
   await click('[data-node-id="test-shop"]','command','enter');
   assert.equal(current.status,'Shop');
   await click('[data-item-id="shop-straight"]','command','buy');
   assert.equal(current.relics.length,1);assert.equal(current.player.gold,'175');
   assert.equal(await page.locator('[data-item-id="shop-straight"]').isDisabled(),true);
   const count=current.deck.length;
   await page.locator('[data-service-id="duplicate"]').click();
   assert.equal(await page.locator('#adventure-service-confirm').isDisabled(),true);
   await page.locator('#adventure-dialog [data-card-id]').first().click();
   await click('#adventure-service-confirm','command','service');
   await page.waitForFunction(()=>!document.querySelector('#adventure-dialog').open);
   assert.equal(current.deck.length,count+1);assert.equal(current.player.gold,'155');
   assert.equal(current.shop.services.find(s=>s.id==='duplicate').price,'30');
   assert.ok((await saved()).includes('test-shop'));
   if(process.env.ADVENTURE_SCREENSHOT)await page.screenshot({path:process.env.ADVENTURE_SCREENSHOT.replace(/\.png$/, '-shop.png'),fullPage:true});
  });
  await test('染色服务提交选牌及目标花色，卡牌由规则层更新',async()=>{
   const card=current.deck.find(c=>c.suit!=='Diamonds');assert.ok(card);
   await page.locator('[data-service-id="suit"]').click();
   await page.locator('#adventure-dialog [data-card-id]').evaluateAll((nodes,id)=>nodes.find(n=>n.dataset.cardId===id).click(),card.id);
   await page.getByLabel('目标花色').selectOption('Diamonds');
   await click('#adventure-service-confirm','command','service');
   await page.waitForFunction(()=>!document.querySelector('#adventure-dialog').open);
   assert.equal(current.deck.find(c=>c.id===card.id).suit,'Diamonds');
   assert.equal(current.player.gold,'145');
  });
  await test('商店离开直接回地图；营地先确认休整才能继续',async()=>{
   await click('#adventure-leave-shop','command','continue');
   await click('[data-node-id="test-rest"]','command','enter');
   assert.equal(await page.locator('#adventure-rest-continue').isDisabled(),true);
   const before=await saved();
   await page.locator('[data-option-id="rest-gold"]').click();await page.getByRole('button',{name:'再想想',exact:true}).click();
   assert.equal(await saved(),before);assert.equal(await page.locator('.rest-scene').count(),1);
   await page.locator('[data-option-id="rest-gold"]').click();await click('#adventure-rest-confirm','command','rest');
   assert.equal(current.player.gold,'165');
   await page.waitForFunction(()=>!document.querySelector('#adventure-dialog').open);
   assert.equal(await page.locator('#adventure-rest-continue').isEnabled(),true);
   assert.equal(await page.locator('.rest-option:enabled').count(),0);
   if(process.env.ADVENTURE_SCREENSHOT)await page.screenshot({path:process.env.ADVENTURE_SCREENSHOT.replace(/\.png$/, '-rest.png'),fullPage:true});
  });
  await test('刷新窗口恢复当前营地，保留金币牌库与遗物',async()=>{
   const pending=responseFor('restore');await page.reload();const data=await(await pending).json();
   assert.equal(data.success,true);current=data.adventure;
   await page.waitForSelector('#adventure-continue');await page.locator('#adventure-continue').click();
   assert.equal(current.status,'Rest');assert.equal(current.player.gold,'165');assert.equal(current.deck.length,53);assert.equal(current.relics.length,1);
   assert.equal(await page.locator('#adventure-rest-continue').isEnabled(),true);
  });
  await test('连接中断后阻止继续操作，同步后恢复可操作状态',async()=>{
   const before=await saved();let intercepted=false;
   const block=async route=>{const data=route.request().postDataJSON();if(data.op==='command'&&data.action==='continue'&&!intercepted){intercepted=true;await route.abort('failed');}else await route.continue();};
   await page.route('**/api/adventure',block);
   await page.locator('#adventure-rest-continue').click();
   await page.waitForSelector('#adventure-sync');
   assert.equal(await page.locator('#adventure-rest-continue').isDisabled(),true);
   assert.equal(await saved(),before);
   await page.unroute('**/api/adventure',block);
   await click('#adventure-sync','state');
   assert.equal(current.status,'Rest');
   assert.equal(await page.locator('#adventure-rest-continue').isEnabled(),true);
  });
  await test('服务中的会话失效后可从已保存进度恢复',async()=>{
   const before=await saved();
   const closed=await page.evaluate(async snapshot=>{
    const bootstrap=await(await fetch('/api/bootstrap')).json();
    return(await fetch('/api/adventure',{method:'POST',headers:{'Content-Type':'application/json','X-Editor-Token':bootstrap.token},body:JSON.stringify({op:'close',sessionId:snapshot.sessionId,runId:snapshot.runId,revision:snapshot.revision})})).json();
   },current);assert.equal(closed.success,true);
   const pending=responseFor('command','continue');await page.locator('#adventure-rest-continue').click();
   const data=await(await pending).json();assert.equal(data.error,'SessionNotFound');
   await page.waitForSelector('#adventure-restore-local');
   assert.equal(await page.locator('#adventure-rest-continue').isDisabled(),true);
   assert.equal(await saved(),before);
   await click('#adventure-restore-local','restore');
   assert.equal(current.status,'Rest');assert.equal(current.player.gold,'165');
   assert.equal(await page.locator('#adventure-rest-continue').isEnabled(),true);
  });
  await test('损坏存档恢复失败时保留原有冒险存档',async()=>{
   await page.locator('#adventure-menu').click();await page.locator('#adventure-config summary').click();
   const before=await saved(),response=responseFor('restore');
   await page.locator('#adventure-save-file').setInputFiles({name:'bad-save.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify({save:'{}'}))});
   await page.getByRole('button',{name:'确认恢复',exact:true}).click();
   const data=await(await response).json();assert.equal(data.success,false);await waitReady();
   assert.equal(await saved(),before);assert.match(await page.locator('#adventure-notice').innerText(),/无效|存档/);
   await page.locator('#adventure-continue').click();
  });
  await test('击败第一章首领后显示第二章的可达节点',async()=>{
   await click('#adventure-rest-continue','command','continue');
   await click('[data-node-id="test-boss"]','command','enter');
   await finishBattle();
   if(current.status==='Reward')await click('#adventure-reward-continue','command','continue');
   assert.equal(current.status,'Map');assert.equal(current.chapter,2);
   assert.equal(await page.locator('[data-node-id="test-boss-2"]').isEnabled(),true);
   assert.match(await page.locator('#adventure-resources').innerText(),/第 2 \/ 2 章/);
  });
  await test('最终首领胜利完成冒险并解锁塔罗，下次开局可选',async()=>{
   await click('[data-node-id="test-boss-2"]','command','enter');await finishBattle();
   if(current.status==='Reward')await click('#adventure-reward-continue','command','continue');
   assert.equal(current.status,'Victory');
   await page.waitForSelector('#adventure-new-after');
   const pending=responseFor('catalog');await page.locator('#adventure-new-after').click();await pending;await waitReady();
   await page.locator('.character-card').first().click();await page.locator('#adventure-character-next').click();
   assert.equal(await page.locator('.tarot-card.locked').count(),0);
   await page.locator('[data-tarot-id="empress"]').click();
   assert.equal(await page.locator('#adventure-start').isEnabled(),true);
  });
  await test('小屏幕主页和图鉴无横向溢出，页面无脚本错误',async()=>{
   await page.setViewportSize({width:390,height:844});await page.locator('#adventure-menu').click();
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
   await page.locator('#adventure-home-tarots').click();
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
   assert.deepEqual(errors,[]);
  });
  await test('启动时损坏的旧存档须明确确认才允许新开',async()=>{
   const isolated=await browser.newContext({viewport:{width:1280,height:900}});
   try{
    const tab=await isolated.newPage(),raw=JSON.stringify({schema:1,save:'{}',savedAt:'2026-01-01T00:00:00Z'});
    await tab.addInitScript(({key,value})=>localStorage.setItem(key,value),{key:saveKey,value:raw});
    await tab.goto(base+'/adventure.html');await tab.waitForSelector('#adventure-new');
    await tab.locator('#adventure-new').click();
    assert.match(await tab.locator('#adventure-dialog').innerText(),/原存档暂时无法恢复/);
    assert.equal(await tab.locator('.character-card').count(),0);
    assert.equal(await tab.evaluate(key=>localStorage.getItem(key),saveKey),raw);
    await tab.getByRole('button',{name:'确认开始新冒险',exact:true}).click();
    await tab.waitForSelector('.character-card');
    assert.equal(await tab.evaluate(key=>localStorage.getItem(key),saveKey),raw);
   }finally{await isolated.close();}
  });
  console.log('Adventure browser tests: '+passes+' passed');
 }finally{await context.close();await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
