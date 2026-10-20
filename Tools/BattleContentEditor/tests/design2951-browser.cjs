// Targeted UI integration. Reuses EDITOR_URL, never launches another editor service.
const assert=require('node:assert/strict');
const {chromium}=require('playwright');
const base=process.env.EDITOR_URL||'http://127.0.0.1:18767';
(async()=>{
 const browser=await chromium.launch({headless:true,channel:process.env.PLAYWRIGHT_CHANNEL||'chrome'});
 try{
  const page=await browser.newPage({viewport:{width:1440,height:1100}}),errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.goto(base);
  await page.waitForFunction(()=>document.querySelector('#validation-state')?.textContent.includes('0 错误'),{},{timeout:30000});
  await page.locator('#playtest-tab').click();
  await page.locator('#playtest-encounter').selectOption('standard-item-lab');
  const responseFor=(op,action)=>page.waitForResponse(r=>{if(!r.url().endsWith('/api/playtest'))return false;const d=r.request().postDataJSON();return d.op===op&&(!action||d.action===action);});
  const started=responseFor('start');await page.locator('#playtest-start').click();const first=await(await started).json();assert.equal(first.success,true);
  await page.locator('#playtest-items').waitFor({state:'visible'});
  const before=first.battle.players[0];assert.equal(before.hand.length,8);assert.equal(before.window,'Action');
  const potion=before.relics.find(r=>r.definitionId==='10002').id,dice=before.relics.find(r=>r.definitionId==='14001').id,grenade=before.relics.find(r=>r.definitionId==='11004').id;
  const preview=responseFor('preview','UseItem');await page.getByRole('combobox',{name:'选择主动道具',exact:true}).selectOption(potion);
  const p=await(await preview).json();assert.equal(p.preview.legal,true);assert.equal(p.battle.players[0].health,before.health);
  const used=responseFor('command','UseItem');await page.getByRole('button',{name:'使用道具',exact:true}).click();const after=await(await used).json();assert.equal(after.success,true);
  assert.equal(BigInt(after.battle.players[0].health),BigInt(before.health)+10n);assert.equal(after.battle.players[0].energy,before.energy);
  await page.waitForFunction(id=>![...document.querySelectorAll('#playtest-items option')].some(o=>o.value===id),potion);
  await page.getByRole('combobox',{name:'选择主动道具',exact:true}).selectOption(dice);
  const target=page.getByRole('combobox',{name:'要替换的持有道具',exact:true});
  assert.equal(await target.locator('option').filter({hasText:dice}).count(),0);
  assert.equal(await page.getByRole('button',{name:'使用道具',exact:true}).isDisabled(),true);
  const dicePreview=responseFor('preview','UseItem');await target.selectOption(grenade);assert.equal((await(await dicePreview).json()).preview.legal,true);
  const size=await page.evaluate(()=>({width:innerWidth,content:document.documentElement.scrollWidth}));assert.ok(size.content<=size.width+1,JSON.stringify(size));
  await page.screenshot({path:process.env.TEST_SCREENSHOT||'/private/tmp/design2951-h5.png',fullPage:true});
  assert.deepEqual(errors,[]);
  console.log('PASS 最新标准自动补牌、道具预览/消耗、骰子目标、桌面布局及无脚本错误');
 }finally{await browser.close();}
})().catch(e=>{console.error(e.stack);process.exitCode=1;});
