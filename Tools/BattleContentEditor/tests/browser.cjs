const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const os=require('node:os');
const {chromium}=require('playwright');
const base=process.env.EDITOR_URL||'http://127.0.0.1:18765';
(async()=>{
 const browser=await chromium.launch({headless:true,channel:process.env.PLAYWRIGHT_CHANNEL||undefined});
 const context=await browser.newContext({viewport:{width:1440,height:1000},acceptDownloads:true});
 const page=await context.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));page.on('dialog',d=>d.type()==='beforeunload'?d.accept():d.dismiss());
 let passes=0;const test=async(name,fn)=>{await fn();passes++;console.log('PASS '+name);};
 const confirm=()=>page.locator('#modal-confirm').click();
 const settle=async()=>{await page.waitForFunction(()=>!document.querySelector('#import').disabled&&/\d+ 错误 \/ \d+ 警告/.test(document.querySelector('#validation-state').textContent),{},{timeout:30000});};
 // Wait for the successful state in one browser evaluation. A second DOM read after
 // settle() can race with a rename/undo mutation or the debounced validation timer.
 const clean=async()=>{await page.waitForFunction(()=>!document.querySelector('#import').disabled&&/^0 错误 \/ \d+ 警告$/.test(document.querySelector('#validation-state').textContent),{},{timeout:30000});};
 const nav=async(title)=>page.locator('.section-button').filter({hasText:title}).click();
 const open=async(title,id)=>{await nav(title);await page.locator('.definition-button').filter({hasText:id}).click();};
 const edit=async(key,value)=>{const input=page.locator('#editor > .field-grid [data-key="'+key+'"]');await input.fill(value);await input.press('Tab');};
 const load=async xml=>{const received=page.waitForResponse(response=>response.url().endsWith('/api/validate')&&response.request().postData()===xml);await page.locator('#xml-file').setInputFiles({name:'fixture.xml',mimeType:'application/xml',buffer:Buffer.from(xml)});await confirm();await received;await page.waitForFunction(()=>/内容包已载入|内容已载入为草稿/.test(document.querySelector('#notice').textContent)&&!document.querySelector('#import').disabled,{},{timeout:30000});await settle();};
 const example=await fs.readFile(path.resolve(__dirname,'../../../Engine/Runtime/Composition/Content/Examples/battle-content.xml'),'utf8');
 try{
 await page.goto(base);await clean();
 await test('十类目录和本机 C# 校验在线',async()=>{assert.equal(await page.locator('.section-button').count(),10);assert.match(await page.locator('#connection').innerText(),/已连接/);});
 await test('首访延迟标准响应不得覆盖表单编辑或未应用源码',async()=>{
  const standard=await fs.readFile(path.resolve(__dirname,'../../../Engine/Content/StandardSingleBattle.xml'),'utf8');
  for(const mode of ['form','source']){
   const isolated=await browser.newContext({viewport:{width:1440,height:1000}});
   const fresh=await isolated.newPage();fresh.on('pageerror',e=>errors.push(e.message));
   let release,received;const gate=new Promise(resolve=>release=resolve),requested=new Promise(resolve=>received=resolve);
   await fresh.route('**/api/standard',async route=>{received();await gate;await route.fulfill({status:200,contentType:'application/xml',body:standard});});
   try{
    await fresh.goto(base,{waitUntil:'domcontentloaded'});await requested;
    if(mode==='form'){
     await fresh.locator('#pack-settings').click();
     const name=fresh.locator('#editor > .field-grid [data-key="name"]');
     await name.fill('标准加载期间手动编辑');await name.press('Tab');
    }else{
     await fresh.locator('#xml-tab').click();
     await fresh.locator('#xml-source').fill('<BattleContent id="pending-before-standard"');
    }
    const defaultResponse=fresh.waitForResponse(response=>response.url().endsWith('/api/standard'));
    const validated=fresh.waitForResponse(response=>response.url().endsWith('/api/validate'));
    release();await (await defaultResponse).finished();await validated;
    await fresh.waitForLoadState('networkidle');
    if(mode==='form'){
     await fresh.locator('#xml-tab').click();
     assert.match(await fresh.locator('#xml-source').inputValue(),/name="标准加载期间手动编辑"/);
    }else assert.equal(await fresh.locator('#xml-source').inputValue(),'<BattleContent id="pending-before-standard"');
    // The authoritative document must remain the initial pack in both cases;
    // preserving only the pending text while replacing its backing doc is unsafe.
    await fresh.locator('#playtest-tab').click();
    assert.equal(await fresh.locator('#playtest-encounter option').count(),0,mode+' was overwritten by delayed defaults');
    await fresh.locator('#form-tab').click();
    assert.match(await fresh.locator('#breadcrumb').innerText(),mode==='form'?/标准加载期间手动编辑/:/新内容包/);
   }finally{release();await isolated.close();}
  }
 });
 await test('示例导入通过权威校验',async()=>{await load(example);await clean();assert.match(await page.locator('#pack-summary').innerText(),/12 项配置/);});
 await test('十类配置表单完整可打开',async()=>{
  for(const [title,id]of [['对局配置','example-fight'],['玩家模板','hero'],['敌人模板','guard'],['初始构筑','example-build'],['初始牌组','small-deck'],['牌型效果','example-hands'],['状态配置','stun'],['遗物配置','example-relic'],['公共战斗规则','standard-battle'],['计分规则','standard-score']]){await open(title,id);assert.ok(await page.locator('#editor [data-key="id"]').count());}
 });
 await test('净化效果和负面状态可在共享配置表单中读取并导出',async()=>{
  const scene=await fs.readFile(path.resolve(__dirname,'../../../Engine/Content/SingleBattleDemoNew.xml'),'utf8');
  await load(scene);await clean();await nav('状态配置');await page.locator('.definition-button[title="bleed"]').click();
  assert.equal(await page.locator('#editor > .field-grid [data-key="tags"]').inputValue(),'Negative');
  await nav('牌型效果');await page.locator('.definition-button[title="document-hands"]').click();
  assert.equal(await page.locator('select[data-key="kind"] option[value="Cleanse"]:checked').count(),1);
  const download=page.waitForEvent('download');await page.locator('#export').click();
  const saved=await fs.readFile(await (await download).path(),'utf8');assert.match(saved,/kind="Cleanse"/);assert.match(saved,/tags="Negative"/);
  await load(example);await clean();
 });
 await test('字段错误定位并修复',async()=>{await open('玩家模板','hero');await edit('maxHealth','0');await settle();assert.match(await page.locator('#validation-state').innerText(),/^[1-9]/);await page.locator('.issue').first().click();assert.ok(await page.locator('.highlight').count());await edit('maxHealth','100');await clean();});
 await test('引用选择器搜索名称和 ID',async()=>{await open('公共战斗规则','standard-battle');await page.locator('[data-field$="/@scoringRef"] button').filter({hasText:'选择'}).click();await page.getByLabel('搜索引用').fill('standard');await page.locator('#modal-body .reference').click();await confirm();assert.equal(await page.locator('[data-key="scoringRef"]').inputValue(),'standard-score');});
 await test('重命名定义自动更新类型化引用',async()=>{await open('状态配置','stun');await page.locator('#editor > .field-grid [data-key="id"]').fill('stun-renamed');await page.locator('#editor > .field-grid [data-key="id"]').press('Tab');await confirm();await clean();await page.locator('#xml-tab').click();assert.match(await page.locator('#xml-source').inputValue(),/statusRef="stun-renamed"/);await page.locator('#undo').click();await clean();});
 await test('正式导出必须通过 C# 校验',async()=>{const downloaded=page.waitForEvent('download');await page.locator('#export').click();const file=await downloaded;assert.match(file.suggestedFilename(),/\.xml$/);const xml=await fs.readFile(await file.path(),'utf8');assert.match(xml,/BattleContent/);assert.match(xml,/statusRef="stun"/);});
 await test('已有引用的定义不能无替代项删除',async()=>{await open('状态配置','stun');await page.locator('#delete').click();assert.match(await page.locator('#modal-body').innerText(),/引用/);await confirm();assert.match(await page.locator('#title').innerText(),/stun/);});
 await test('未知字段与注释无损保留且阻止正式导出',async()=>{
  await load(example.replace('id="hero"','id="hero" mystery="keep-me"').replace('<Players>','<!-- keep this comment -->\n<Players>').replace('</BattleContent>','<parsererror/></BattleContent>'));
  await open('玩家模板','hero');assert.match(await page.locator('.unknown').first().innerText(),/mystery/);
  await page.locator('#xml-tab').click();const raw=await page.locator('#xml-source').inputValue();assert.match(raw,/keep-me/);assert.match(raw,/keep this comment/);assert.match(raw,/<parsererror/);
  let downloaded=false;const hook=()=>downloaded=true;page.on('download',hook);await page.locator('#export').click();await page.waitForTimeout(1500);assert.equal(downloaded,false);page.off('download',hook);
 });
 await test('无效 XML 保留原内容并允许修复',async()=>{await page.locator('#xml-source').fill('<BattleContent broken>');await page.locator('#apply-xml').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('无法读取'));assert.equal(await page.locator('#xml-source').inputValue(),'<BattleContent broken>');await page.locator('#form-tab').click();assert.match(await page.locator('#title').innerText(),/hero/);await page.locator('#xml-tab').click();await page.locator('#xml-source').fill(example);await page.locator('#apply-xml').click();await clean();});
 await test('草稿保存、刷新恢复及未应用源码恢复',async()=>{
  await open('玩家模板','hero');await edit('name','测试角色');await page.locator('#xml-tab').click();await page.locator('#xml-source').fill('<pending>');await page.locator('#save').click();
  await page.reload();await page.waitForSelector('#restore-banner:not([hidden])');await page.getByRole('button',{name:'恢复草稿',exact:true}).click();await page.waitForFunction(()=>document.querySelector('#restore-banner').hidden);assert.equal(await page.locator('#xml-source').inputValue(),'<pending>');await page.locator('#reset-xml').click();await confirm();await page.locator('#form-tab').click();await open('玩家模板','测试角色');assert.equal(await page.locator('#editor > .field-grid [data-key="name"]').inputValue(),'测试角色');await clean();
 });
 await test('复制、新建、撤销和子项重排',async()=>{await page.locator('#duplicate').click();assert.match(await page.locator('#subtitle').innerText(),/hero-copy/);await page.locator('#undo').click();await nav('牌型效果');await page.getByRole('button',{name:'+ 新建牌型效果',exact:true}).click();assert.equal(await page.locator('#editor > details.node-card').count(),10);await clean();await page.locator('#editor > details.node-card').first().locator('.node-controls').last().getByRole('button',{name:'↓ 下移',exact:true}).click();await clean();await page.locator('#xml-tab').click();const xml=await page.locator('#xml-source').inputValue();assert.ok(xml.lastIndexOf('<HandRule')>=0);});
 await test('切换计分预设生成完整牌型表',async()=>{await open('计分规则','standard-score');await page.locator('[data-key="preset"]').selectOption('');assert.equal(await page.locator('#editor > details.node-card').count(),10);await clean();});
 await test('导出时拒绝尚未应用的源码修改',async()=>{await page.locator('#xml-tab').click();await page.locator('#xml-source').fill('<pending/>');await page.locator('#export').click();assert.match(await page.locator('#notice').innerText(),/尚未应用/);await page.locator('#reset-xml').click();await confirm();});
 await test('延迟校验不得覆盖新修订或导出旧内容',async()=>{
  await open('玩家模板','测试角色');let intercepted=false;
  await page.route('**/api/validate',async route=>{if(!intercepted){intercepted=true;await new Promise(r=>setTimeout(r,1800));}await route.continue();});
  await page.locator('#validate').click();await edit('maxHealth','0');await page.waitForTimeout(3000);await settle();assert.match(await page.locator('#validation-state').innerText(),/^[1-9]/);await page.unroute('**/api/validate');await edit('maxHealth','100');await clean();
 });
 await test('校验服务离线不能导出并保留编辑内容',async()=>{await page.route('**/api/validate',route=>route.fulfill({status:503,contentType:'application/json',body:'{"error":"服务暂不可用"}'}));await page.locator('#export').click();await page.waitForFunction(()=>document.querySelector('#notice').textContent.includes('服务暂不可用'));assert.equal(await page.locator('#editor > .field-grid [data-key="maxHealth"]').inputValue(),'100');await page.unroute('**/api/validate');await page.locator('#validate').click();await clean();});
 await test('安全呈现名称与紧凑屏幕布局',async()=>{await edit('name','<img src=x onerror=alert(1)>');assert.equal(await page.locator('#title img').count(),0);await page.setViewportSize({width:1100,height:850});assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);await page.setViewportSize({width:1440,height:1000});await edit('name','测试角色');});
 await test('内容包设置不误把目录当成未知子项',async()=>{await page.locator('#pack-settings').click();assert.equal(await page.locator('#editor .unknown').count(),0);assert.equal(await page.locator('#editor [data-key]').count(),4);await edit('version','2');await clean();});
 await test('同修订较早的失败响应不得覆盖较新的成功',async()=>{let count=0;await page.route('**/api/validate',async route=>{count++;if(count===1){await new Promise(r=>setTimeout(r,1800));await route.fulfill({status:503,contentType:'application/json',body:'{"error":"old failure"}'});}else await route.continue();});await page.locator('#validate').click();await page.waitForTimeout(100);await page.locator('#validate').click();await page.waitForTimeout(2500);await clean();await page.unroute('**/api/validate');});
 await test('只有警告时可导出，长整数保持精确',async()=>{await load(example.replace('stepId="damage/1"','stepId="missing-step"').replace('id="example-relic"','id="example-relic" sellPrice="9007199254740993"'));await clean();assert.match(await page.locator('#validation-state').innerText(),/1 警告/);const event=page.waitForEvent('download');await page.locator('#export').click();const file=await event;const xml=await fs.readFile(await file.path(),'utf8');assert.match(xml,/9007199254740993/);await load(example);await clean();});
 await test('删除缺少 ID 的草稿不会改写未设置的可选引用',async()=>{await load(example.replace('</Loadouts>','<Loadout id="empty-build"/></Loadouts>'));await nav('初始牌组');await page.getByRole('button',{name:'+ 新建初始牌组',exact:true}).click();await edit('id','');await page.locator('#delete').click();assert.equal(await page.locator('#modal select').count(),0);await confirm();await clean();await page.locator('#xml-tab').click();assert.match(await page.locator('#xml-source').inputValue(),/<Loadout id="empty-build"\s*\/>/);await load(example);});
 await test('导入期间新增的编辑不会被旧导入覆盖',async()=>{await open('玩家模板','hero');let delayed=false;await page.route('**/api/validate',async route=>{if(!delayed){delayed=true;await new Promise(r=>setTimeout(r,1500));}await route.continue();});await page.locator('#xml-file').setInputFiles({name:'replacement.xml',mimeType:'application/xml',buffer:Buffer.from(example.replace('id="content-example"','id="replacement"'))});await confirm();await page.waitForFunction(()=>document.querySelector('#import').disabled);await edit('maxHealth','125');await page.waitForFunction(()=>!document.querySelector('#import').disabled,{},{timeout:30000});await settle();assert.equal(await page.locator('#editor > .field-grid [data-key="maxHealth"]').inputValue(),'125');assert.match(await page.locator('#breadcrumb').innerText(),/content-example/i);await page.unroute('**/api/validate');await edit('maxHealth','100');await clean();});
 await test('修改被匹配的牌型步骤会提示遗物影响',async()=>{await open('牌型效果','example-hands');const hand=page.locator('#editor > details.node-card').nth(1);await hand.locator('summary').first().click();const effect=hand.locator('details.node-card').first();await effect.locator('summary').first().click();await effect.locator('[data-key="id"]').fill('new-step');await effect.locator('[data-key="id"]').press('Tab');await page.waitForSelector('#modal[open]');assert.match(await page.locator('#modal-body').innerText(),/example-relic/);await page.getByRole('button',{name:'取消',exact:true}).click();await clean();});
 await test('三个怪物模板支持行为表单与无损导出',async()=>{
  for(const [file,id] of [['07_RotationGrunt','rotator'],['08_ArmoredSummoner','summoner'],['09_GluttonyBoss','gluttony']]){
   const xml=await fs.readFile(path.resolve(__dirname,'../../../Engine/Content/Templates/'+file+'.xml'),'utf8');
   await load(xml);await clean();await nav('敌人模板');await page.locator('.definition-button[title="'+id+'"]').click();
   assert.equal(await page.locator('#editor .unknown').count(),0);
   assert.ok(await page.locator('#editor [data-key="mode"]').count()>0);
   const event=page.waitForEvent('download');await page.locator('#export').click();
   const saved=await fs.readFile(await (await event).path(),'utf8');assert.match(saved,/<Behavior/);assert.match(saved,/<Stage/);
   if(file==='08_ArmoredSummoner')assert.match(saved,/enemyRef="rotator"/);
   if(file==='09_GluttonyBoss')assert.match(saved,/Unplayable/);
  }
  await load(example);await clean();
 });
 await test('塞牌区域和插入位置表单支持修改、校验与 XML 导出',async()=>{
  const xml=await fs.readFile(path.resolve(__dirname,'../../../Engine/Content/Templates/09_GluttonyBoss.xml'),'utf8');
  await load(xml);await clean();await nav('敌人模板');await page.locator('.definition-button[title="gluttony"]').click();
  await page.locator('#editor details').evaluateAll(nodes=>nodes.forEach(node=>node.open=true));
  const zone=page.locator('[data-key="zone"]').first(),position=page.locator('[data-key="position"]').first();
  for(const [destination,mode] of [['deck','Shuffle'],['deck','Top'],['deck','Bottom'],['hand','Default'],['discard','Default']]){
   await zone.selectOption(destination);await position.selectOption(mode);await clean();
   const event=page.waitForEvent('download');await page.locator('#export').click();
   const saved=await fs.readFile(await (await event).path(),'utf8');
   assert.match(saved,new RegExp('<AddCards[^>]*zone="'+destination+'"[^>]*position="'+mode+'"'));
  }
  await zone.selectOption('hand');await position.selectOption('Top');await settle();
  assert.match(await page.locator('#validation-state').innerText(),/^[1-9]/);
  await position.selectOption('Default');await clean();
  await load(example);await clean();
 });
 await test('标准顺子逐张攻击、命中状态及同花治疗可配置并导出',async()=>{
  const standard=await fs.readFile(path.resolve(__dirname,'../../../Engine/Content/StandardSingleBattle.xml'),'utf8');
  await load(standard);await clean();await nav('牌型效果');await page.locator('.definition-button[title="document-hands"]').click();
  assert.equal(await page.locator('#editor > .field-grid [data-key="flushUsesRankEffects"]').inputValue(),'true');
  assert.equal(await page.locator('[data-key="perSubmittedCard"] option[value="true"]:checked').count(),1);
  assert.equal(await page.locator('[data-key="amountKind"] option[value="CardScoreScale"]:checked').count(),1);
  assert.equal(await page.locator('[data-key="amountKind"] option[value="FlushBaseTotalScale"]:checked').count(),1);
  assert.equal(await page.locator('[data-key="stacksPerSubmittedCard"]').count(),3);
  assert.equal(await page.locator('.unknown').count(),0);
  const event=page.waitForEvent('download');await page.locator('#export').click();
  const saved=await fs.readFile(await (await event).path(),'utf8');
  assert.match(saved,/<OnHitStatus[^>]*statusRef="bleed"[^>]*stacks="1"/);
  assert.match(saved,/<FlushBonus>/);assert.match(saved,/amountKind="FlushBaseTotalScale" amount="0.2"/);
  await load(example);await clean();
 });
 await open('牌型效果','example-hands');await page.screenshot({path:process.env.EDITOR_SCREENSHOT||path.join(os.tmpdir(),'battle-content-editor-form.png'),fullPage:true});
 assert.deepEqual(errors,[]);console.log(`Browser: ${passes} passed, 0 failed.`);
 }finally{await context.close();await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
