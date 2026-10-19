// Standalone H5 scene regression against an already running native editor.
// EDITOR_URL=http://127.0.0.1:18765 PLAYWRIGHT_CHANNEL=chrome node tests/h5-scene-browser.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const {chromium} = require('playwright');
const base = process.env.EDITOR_URL || 'http://127.0.0.1:18765';
const ids = ['standard-single-battle', 'standard-armored-summoner', 'standard-gluttony-boss'];

(async () => {
  const browser = await chromium.launch({headless:true, channel:process.env.PLAYWRIGHT_CHANNEL || 'chrome'});
  const contexts = [];
  const failures = [];
  let passes = 0;
  const test = async (name, action) => {
    try { await action(); passes++; console.log('PASS ' + name); }
    catch (error) { failures.push({name,error}); console.error('FAIL ' + name + '\n' + error.stack); }
  };
  const makePage = async options => {
    const context = await browser.newContext({viewport:{width:1440,height:1100},...options});
    contexts.push(context);
    const page = await context.newPage();
    const errors = [], assets = new Map();
    page.on('pageerror', error => errors.push(error.message));
    page.on('response', response => {
      const url = new URL(response.url());
      if(url.pathname.startsWith('/assets/')) assets.set(url.pathname,response.status());
    });
    page.on('dialog', dialog => dialog.type() === 'beforeunload' ? dialog.accept() : dialog.dismiss());
    await page.goto(base);
    await page.waitForFunction(() => !document.querySelector('#import').disabled && /\d+ 错误 \/ \d+ 警告/.test(document.querySelector('#validation-state').textContent), {}, {timeout:30000});
    await page.locator('#playtest-tab').click();
    return {context,page,errors,assets};
  };
  const responseFor = (page,op,action) => page.waitForResponse(response => {
    if(!response.url().endsWith('/api/playtest'))return false;
    const data=response.request().postDataJSON();
    return data.op===op&&(!action||data.action===action);
  },{timeout:30000});
  const clickRequest = async (page,selector,op,action) => {
    const response=responseFor(page,op,action);
    await page.locator(selector).click();
    const result=await (await response).json();
    assert.equal(result.success,true,JSON.stringify({error:result.error,message:result.message,diagnostics:result.diagnostics}));
    await page.waitForFunction(() => !document.querySelector('#playtest-seed').disabled);
    if(result.battle)await page.waitForFunction(({revision,runId}) => {
      const status=document.querySelector('#playtest-status');
      return status.dataset.revision===String(revision)&&status.dataset.runId===runId;
    },result.battle);
    return result;
  };
  const start = async (page,id) => {
    await page.locator('[data-encounter-id="'+id+'"]').click();
    return clickRequest(page,'#playtest-start','start');
  };
  const round = async page => {
    if(await page.locator('#playtest-skip-draw').isVisible())await clickRequest(page,'#playtest-skip-draw','command','SkipDraw');
    let result=await clickRequest(page,'#playtest-end-turn','command','EndTurn');
    if(result.battle.players[0].window==='Ending')result=await clickRequest(page,'#playtest-end-turn','command','FinishTurn');
    return result;
  };
  const draw = async (page,count=5) => {
    await page.locator('#playtest-draw-count').fill(String(count));
    return clickRequest(page,'#playtest-draw','command','Draw');
  };
  const noHorizontalOverflow = async page => {
    const size=await page.evaluate(() => ({viewport:innerWidth,html:document.documentElement.scrollWidth,body:document.body.scrollWidth}));
    assert.ok(size.html<=size.viewport+1&&size.body<=size.viewport+1,JSON.stringify(size));
  };
  const idleArtReady = async page => {
    await page.waitForFunction(() => [...document.querySelectorAll('#playtest-view img')].every(img=>img.complete&&img.naturalWidth===192));
  };
  const load = async (page,xml) => {
    const response=page.waitForResponse(r=>r.url().endsWith('/api/validate')&&r.request().postData()===xml);
    await page.locator('#xml-file').setInputFiles({name:'h5-scene-fixture.xml',mimeType:'application/xml',buffer:Buffer.from(xml)});
    await page.locator('#modal-confirm').click();
    await response;
    await page.waitForFunction(() => /内容包已载入|内容已载入为草稿/.test(document.querySelector('#notice').textContent)&&!document.querySelector('#import').disabled,{}, {timeout:30000});
    await page.locator('#playtest-tab').click();
  };

  try {
    const desktop=await makePage();
    const page=desktop.page;
    await test('占位资源不超过 200 KiB，四张角色 PNG 均为 192×192',async()=>{
      const folder=path.resolve(__dirname,'../web/assets');
      const files=(await fs.readdir(folder)).filter(name=>/\.(png|svg)$/i.test(name));
      const pngs=files.filter(name=>name.endsWith('.png'));
      assert.deepEqual(pngs.sort(),['gluttony.png','grunt.png','hero.png','summoner.png']);
      let total=0;
      for(const name of files){
        const data=await fs.readFile(path.join(folder,name));total+=data.length;
        if(name.endsWith('.png')){
          assert.equal(data.subarray(1,4).toString(),'PNG');
          assert.equal(data.readUInt32BE(16),192,name+' width');
          assert.equal(data.readUInt32BE(20),192,name+' height');
        }
      }
      assert.ok(total<=200*1024,'资源合计 '+total+' 字节，超过 200 KiB 预算');
    });
    await test('新浏览器默认标准 XML，提供三个独立可选试炼且未自动开局',async()=>{
      assert.deepEqual(await page.locator('#playtest-encounter option').evaluateAll(options=>options.map(option=>option.value)),ids);
      assert.equal(await page.locator('[data-encounter-id]').count(),3);
      assert.equal(await page.locator('#restore-banner').isVisible(),false);
      assert.equal(await page.locator('#playtest-empty').isVisible(),true);
      assert.equal(await page.locator('#playtest-live').isVisible(),false);
    });
    await test('试炼卡片与下拉选择同步，选择不会发送开局请求',async()=>{
      let starts=0;
      const watch=request=>{if(request.url().endsWith('/api/playtest')&&request.postDataJSON().op==='start')starts++;};
      page.on('request',watch);
      for(const id of ids){
        await page.locator('[data-encounter-id="'+id+'"]').click();
        assert.equal(await page.locator('#playtest-encounter').inputValue(),id);
        assert.equal(await page.locator('[data-encounter-id="'+id+'"]').getAttribute('aria-pressed'),'true');
        assert.equal(await page.locator('#playtest-empty').isVisible(),true);
      }
      await page.locator('#playtest-encounter').selectOption(ids[0]);
      assert.equal(await page.locator('[data-encounter-id="'+ids[0]+'"]').getAttribute('aria-pressed'),'true');
      page.off('request',watch);assert.equal(starts,0);
    });
    await test('键盘 Enter 和 Space 选择试炼后焦点留在所选卡片',async()=>{
      for(const [id,key] of [[ids[1],'Enter'],[ids[2],'Space']]){
        const card=page.locator('[data-encounter-id="'+id+'"]');
        await card.focus();await card.press(key);
        assert.equal(await page.locator('#playtest-encounter').inputValue(),id);
        assert.equal(await card.evaluate(el=>el===document.activeElement),true);
        assert.equal(await page.locator('#playtest-empty').isVisible(),true);
      }
    });
    await test('桌面横向战场玩家居左、怪物居右，四种小尺寸角色图片成功加载',async()=>{
      const result=await start(page,ids[0]);
      assert.equal(result.battle.enemies[0].definitionId,'rotator');
      await idleArtReady(page);
      const player=await page.locator('#playtest-players').boundingBox(),enemy=await page.locator('#playtest-enemies').boundingBox();
      assert.ok(player.x+player.width<enemy.x,'玩家与敌人应占据互不重叠的左右区域');
      for(const name of ['hero','grunt','summoner','gluttony']){
        const image=page.locator('img[src="/assets/'+name+'.png"]').first();
        assert.deepEqual(await image.evaluate(img=>({width:img.naturalWidth,height:img.naturalHeight})),{width:192,height:192});
        assert.equal(desktop.assets.get('/assets/'+name+'.png'),200);
      }
      assert.ok(await page.locator('#playtest-relic-tray use[href$="#relic"]').count());
      await noHorizontalOverflow(page);
    });
    await test('零护盾标识保持紧凑，不继承编辑器空内容样式',async()=>{
      const shield=await page.locator('#playtest-players .unit-shield').boundingBox();
      assert.ok(shield.height<=28&&shield.width<=60,'零护盾图标不应变成空面板：'+JSON.stringify(shield));
    });
    await test('头顶意图与护甲层数显示真实规则数据，精英首轮召唤两名下轮护卫',async()=>{
      const opened=await start(page,ids[1]);
      assert.equal(opened.battle.enemies[0].intentMeta.actionId,'summon');
      assert.match(await page.locator('#playtest-enemies .playtest-intent').getAttribute('aria-label'),/召唤/);
      assert.equal(await page.locator('#playtest-enemies .playtest-status b').textContent(),'3');
      assert.equal(await page.locator('#playtest-enemies .playtest-status use').getAttribute('href'),'/assets/icons.svg#armor');
      const intent=await page.locator('#playtest-enemies .playtest-intent').boundingBox(),figure=await page.locator('#playtest-enemies .unit-figure').boundingBox();
      assert.ok(intent.y+intent.height<=figure.y+1,'意图应位于角色形象上方');
      const result=await round(page);
      assert.equal(result.battle.enemies.length,3);
      assert.equal(result.battle.players[0].health,'800');
      assert.equal(await page.locator('#playtest-enemies .playtest-unit').count(),3);
      assert.equal(await page.locator('#playtest-enemies .playtest-intent').count(),3);
      assert.equal(await page.locator('#playtest-enemies [data-art="grunt"]').count(),2);
      await draw(page);
      await idleArtReady(page);
      await page.screenshot({path:'/private/tmp/h5-scene-desktop.png',fullPage:true,animations:'disabled'});
    });
    await test('战斗记录可展开，包含召唤与真实结算步骤',async()=>{
      assert.equal(await page.locator('.playtest-log-section').evaluate(el=>el.open),false);
      await page.locator('.playtest-log-section>summary').click();
      assert.equal(await page.locator('.playtest-log-section').evaluate(el=>el.open),true);
      assert.match(await page.locator('#playtest-log').textContent(),/召唤/);
      assert.ok(await page.locator('.playtest-log-entry').count()>1);
      await page.locator('.playtest-log-section>summary').click();
    });
    await test('动效开关可双向切换并停止角色及场景动画',async()=>{
      assert.equal(await page.locator('#playtest-motion').getAttribute('aria-pressed'),'true');
      await page.locator('#playtest-motion').click();
      assert.equal(await page.locator('#playtest-motion').getAttribute('aria-pressed'),'false');
      assert.equal(await page.locator('.unit-art').first().evaluate(el=>getComputedStyle(el).animationName),'none');
      assert.equal(await page.locator('.battle-scenery').evaluate(el=>getComputedStyle(el,'::after').animationName),'none');
      await page.locator('#playtest-motion').click();
      assert.equal(await page.locator('#playtest-motion').getAttribute('aria-pressed'),'true');
      assert.notEqual(await page.locator('.unit-art').first().evaluate(el=>getComputedStyle(el).animationName),'none');
    });
    await test('伤害动效期间关闭再开启，不重复播放旧伤害飘字',async()=>{
      await round(page);
      assert.ok(await page.locator('.battle-float:visible').count()>0,'真实受伤后应出现伤害飘字');
      await page.locator('#playtest-motion').click();
      assert.equal(await page.locator('.battle-float:visible').count(),0);
      await page.locator('#playtest-motion').click();
      assert.equal(await page.locator('.battle-float:visible').count(),0,'重新开启动效不应重放刚结束的伤害');
    });
    await test('暴食 Boss 结束一轮后塞入废牌，弃牌堆及意图同步显示',async()=>{
      await start(page,ids[2]);
      const result=await round(page);
      assert.equal(result.battle.players[0].discardCount,1);
      assert.equal(result.battle.players[0].health,'740');
      assert.equal(await page.locator('#playtest-discard-pile strong').textContent(),'1');
      assert.equal(result.battle.players[0].piles.discard.count,1);
      assert.equal(result.battle.players[0].piles.discard.unplayableCount,1);
      assert.equal(result.battle.players[0].hand.length,0,'Boss 废牌仍按规则进入弃牌堆，不能立即出现在手牌');
      assert.equal(result.steps.flatMap(step=>step.cardCreations||[]).filter(fact=>fact.zone==='discard').reduce((total,fact)=>total+fact.count,0),1);
      assert.equal(await page.locator('#playtest-discard-pile .pile-junk-count').textContent(),'废牌 1');
      assert.equal(await page.locator('#playtest-creation-notice').isVisible(),true);
      assert.match(await page.locator('#playtest-creation-notice').textContent(),/暴食Boss.*废牌 × 1.*弃牌堆/);
      let commands=0;
      const watch=request=>{if(request.url().endsWith('/api/playtest')&&request.postDataJSON().op==='command')commands++;};
      page.on('request',watch);
      const before=await page.locator('#playtest-status').getAttribute('data-revision');
      await page.locator('#playtest-discard-pile').click();
      assert.equal(await page.locator('#playtest-pile-view').isVisible(),true);
      assert.equal(await page.locator('#playtest-discard-pile').getAttribute('aria-expanded'),'true');
      assert.equal(await page.locator('.pile-preview-card.junk b').textContent(),'废牌');
      assert.match(await page.locator('.pile-preview-card.junk').textContent(),/不可出牌.*× 1/);
      assert.match(await page.locator('#playtest-pile-hint').textContent(),/牌库耗尽.*摸入手牌/);
      await noHorizontalOverflow(page);
      await page.screenshot({path:'/private/tmp/h5-junk-discard.png',fullPage:true,animations:'disabled'});
      await page.locator('#playtest-deck-pile').click();
      assert.match(await page.locator('#playtest-pile-title').textContent(),/牌库.*52/);
      assert.match(await page.locator('#playtest-pile-hint').textContent(),/不代表摸牌顺序/);
      await page.locator('#playtest-pile-view').getByRole('button',{name:'收起'}).click();
      assert.equal(await page.locator('#playtest-pile-view').isVisible(),false);
      assert.equal(await page.locator('#playtest-deck-pile').evaluate(el=>el===document.activeElement),true);
      page.off('request',watch);
      assert.equal(commands,0,'查看牌堆只读，不应发送战斗命令');
      assert.equal(await page.locator('#playtest-status').getAttribute('data-revision'),before);
      assert.match(await page.locator('#playtest-enemies .playtest-intent').getAttribute('aria-label'),/塞入卡牌 × 1/);
      assert.equal(await page.locator('#playtest-enemies .intent-main b').textContent(),'60');
      assert.match(await page.locator('#playtest-log').textContent(),/塞|生成|创建/);
    });
    await test('375px 移动布局无页面水平溢出，三怪阵容仍能摸牌并结束回合',async()=>{
      const mobile=await makePage({viewport:{width:375,height:812},isMobile:true,hasTouch:true});
      await noHorizontalOverflow(mobile.page);
      await start(mobile.page,ids[1]);
      await round(mobile.page);
      const drawn=await draw(mobile.page,5);
      assert.equal(drawn.battle.players[0].hand.length,5);
      assert.equal(await mobile.page.locator('#playtest-hand [data-card-id]').count(),5);
      await noHorizontalOverflow(mobile.page);
      await idleArtReady(mobile.page);
      await mobile.page.screenshot({path:'/private/tmp/h5-scene-mobile.png',fullPage:true,animations:'disabled'});
      const ended=await round(mobile.page);
      assert.equal(ended.battle.players[0].health,'630');
      await noHorizontalOverflow(mobile.page);
      await start(mobile.page,ids[2]);await round(mobile.page);
      await mobile.page.locator('#playtest-discard-pile').click();
      assert.equal(await mobile.page.locator('.pile-preview-card.junk b').textContent(),'废牌');
      assert.equal(await mobile.page.locator('#playtest-creation-notice').isVisible(),true);
      await noHorizontalOverflow(mobile.page);
      await mobile.page.screenshot({path:'/private/tmp/h5-junk-mobile.png',fullPage:true,animations:'disabled'});
      assert.deepEqual(mobile.errors,[]);
    });
    await test('prefers-reduced-motion 默认关闭动效，切换按钮也不覆盖系统减弱动画设置',async()=>{
      const reduced=await makePage({reducedMotion:'reduce'});
      await start(reduced.page,ids[0]);
      assert.equal(await reduced.page.locator('#playtest-motion').getAttribute('aria-pressed'),'false');
      assert.equal(await reduced.page.locator('.unit-art').first().evaluate(el=>getComputedStyle(el).animationName),'none');
      await reduced.page.locator('#playtest-motion').click();
      assert.equal(await reduced.page.locator('.unit-art').first().evaluate(el=>getComputedStyle(el).animationName),'none');
      assert.equal(await reduced.page.locator('.battle-scenery').evaluate(el=>getComputedStyle(el,'::after').animationName),'none');
      assert.deepEqual(reduced.errors,[]);
    });
    await test('手牌废牌清楚显示废牌标题和规则，普通牌保留点数花色，出弃牌权限一致',async()=>{
      let xml=await fs.readFile(path.resolve(__dirname,'../../../Engine/Content/StandardSingleBattle.xml'),'utf8');
      xml=xml.replace(/<Deck id="standard-deck"[\s\S]*?<\/Deck>/,
        '<Deck id="standard-deck"><Card id="junk" face="standard:1:2" effects="Ineffective,Unplayable"/>'+
        '<Card id="locked" face="standard:1:3" disabled="true"/>'+
        '<Card id="normal" face="standard:1:4"/></Deck>');
      await load(page,xml);await start(page,ids[0]);
      const result=await draw(page,3);
      const junk=result.battle.players[0].hand.find(card=>!card.canPlay&&card.canDiscard);
      const locked=result.battle.players[0].hand.find(card=>!card.canPlay&&!card.canDiscard);
      const normal=result.battle.players[0].hand.find(card=>card.canPlay&&card.canDiscard);
      assert.ok(junk&&locked&&normal);
      const junkButton=page.locator('[data-card-id="'+junk.id+'"]');
      const lockedButton=page.locator('[data-card-id="'+locked.id+'"]');
      const normalButton=page.locator('[data-card-id="'+normal.id+'"]');
      assert.equal(await junkButton.locator('.junk-card-title').textContent(),'废牌');
      assert.equal(await junkButton.locator('.junk-card-title').isVisible(),true);
      assert.equal(await junkButton.locator('.junk-card-rule').textContent(),'不可出牌');
      assert.equal(await junkButton.locator('.junk-card-rule').isVisible(),true);
      assert.equal(await junkButton.locator('small').textContent(),'可弃牌');
      assert.equal(await junkButton.locator('.playtest-card-rank:visible, .playtest-card-suit:visible').count(),0);
      assert.ok((await junkButton.getAttribute('title')).includes(junk.label),'废牌提示保留原始牌面信息');
      assert.equal(await normalButton.locator('.playtest-card-rank').isVisible(),true);
      assert.equal(await normalButton.locator('.playtest-card-suit').isVisible(),true);
      assert.ok((await normalButton.locator('.playtest-card-rank').textContent()).length>0);
      assert.ok((await normalButton.locator('.playtest-card-suit').textContent()).length>0);
      assert.equal(await normalButton.locator('.junk-card-title').count(),0);
      assert.equal(await junkButton.isDisabled(),false);
      assert.equal(await lockedButton.locator('small').textContent(),'不可使用');
      assert.equal(await lockedButton.isDisabled(),true);
      await page.screenshot({path:'/private/tmp/h5-junk-hand.png',fullPage:true,animations:'disabled'});
      const play=responseFor(page,'preview','Play'),discard=responseFor(page,'preview','Discard');
      await junkButton.click();
      const [a,b]=await Promise.all([play,discard]);
      assert.equal((await a.json()).preview.legal,false);
      assert.equal((await b.json()).preview.legal,true);
      await page.waitForFunction(()=>!document.querySelector('#playtest-discard').disabled);
      assert.equal(await page.locator('#playtest-play').isDisabled(),true);
      assert.equal(await page.locator('#playtest-discard').isDisabled(),false);
    });
    await test('XML 中恶意角色与状态名称按文本显示，不创建 HTML 节点或执行脚本',async()=>{
      const payload='<img id="h5-xss" src=x onerror="window.__h5Xss=1">';
      const encoded=payload.replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
      let xml=await fs.readFile(path.resolve(__dirname,'../../../Engine/Content/StandardSingleBattle.xml'),'utf8');
      xml=xml.replace(/(<Player id="hero" name=")[^"]+/, '$1'+encoded)
        .replace(/(<Enemy id="summoner" name=")[^"]+/, '$1'+encoded)
        .replace(/(<Status id="layer-armor" name=")[^"]+/, '$1'+encoded)
        .replace(/(<Encounter id="standard-armored-summoner" name=")[^"]+/, '$1'+encoded);
      await load(page,xml);
      await start(page,ids[1]);
      assert.equal(await page.locator('#playtest-players .playtest-unit-heading strong').textContent(),payload);
      assert.equal(await page.locator('#playtest-enemies .playtest-unit-heading strong').textContent(),payload);
      assert.match(await page.locator('#playtest-enemies .playtest-status').getAttribute('aria-label'),/^<img id="h5-xss"/);
      assert.equal(await page.locator('[data-encounter-id="'+ids[1]+'"] strong').textContent(),payload);
      assert.equal(await page.locator('#h5-xss').count(),0);
      assert.equal(await page.evaluate(()=>window.__h5Xss),undefined);
      assert.deepEqual(desktop.errors,[]);
    });
  } finally {
    // Release only sessions created in these isolated contexts.
    for(const context of contexts){
      for(const page of context.pages()){
        try{if(await page.locator('#playtest-close').isVisible()&&!await page.locator('#playtest-close').isDisabled())await clickRequest(page,'#playtest-close','close');}catch{}
      }
      await context.close();
    }
    await browser.close();
  }
  console.log(`H5 scene: ${passes} passed, ${failures.length} failed`);
  if(failures.length)process.exitCode=1;
})().catch(error=>{console.error(error.stack);process.exitCode=1;});
