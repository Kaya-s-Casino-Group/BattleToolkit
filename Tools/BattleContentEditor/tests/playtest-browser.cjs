const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const {chromium} = require('playwright');
const base = process.env.EDITOR_URL || 'http://127.0.0.1:18765';

(async () => {
  const browser = await chromium.launch({headless:true, channel:process.env.PLAYWRIGHT_CHANNEL || undefined});
  const context = await browser.newContext({viewport:{width:1440,height:1000}, acceptDownloads:true});
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('dialog', dialog => dialog.type() === 'beforeunload' ? dialog.accept() : dialog.dismiss());
  const scene = await fs.readFile(path.resolve(__dirname, '../../../Engine/Content/SingleBattleDemoNew.xml'), 'utf8');
  let passes = 0;
  const sessions = new Set();
  const test = async (name, fn) => { if (process.env.PLAYTEST_RELOAD_ONLY && name !== '刷新页面释放旧对局并保留配置草稿') return; await fn(); passes++; console.log('PASS ' + name); };
  const settle = async () => page.waitForFunction(() => !document.querySelector('#import').disabled && /\d+ 错误 \/ \d+ 警告/.test(document.querySelector('#validation-state').textContent), {}, {timeout:30000});
  const load = async xml => {
    const response = page.waitForResponse(r => r.url().endsWith('/api/validate') && r.request().postData() === xml);
    await page.locator('#xml-file').setInputFiles({name:'playtest-fixture.xml',mimeType:'application/xml',buffer:Buffer.from(xml)});
    await page.locator('#modal-confirm').click();
    await response;
    await page.waitForFunction(() => /内容包已载入|内容已载入为草稿/.test(document.querySelector('#notice').textContent) && !document.querySelector('#import').disabled, {}, {timeout:30000});
    await settle();
  };
  const responseFor = (op, action) => page.waitForResponse(response => {
    if (!response.url().endsWith('/api/playtest')) return false;
    const data = response.request().postDataJSON();
    return data.op === op && (!action || data.action === action);
  }, {timeout:30000});
  const clickRequest = async (selector, op, action) => {
    const response = responseFor(op, action);
    await page.locator(selector).click();
    const result = await (await response).json();
    if (result.battle) sessions.add(result.battle.sessionId);
    await page.waitForFunction(() => !document.querySelector('#playtest-seed').disabled, {}, {timeout:30000});
    return result;
  };
  const handIds = () => page.locator('#playtest-hand [data-card-id]').evaluateAll(cards => cards.map(card => card.dataset.cardId));
  const choose = async index => {
    const response = responseFor('preview', 'Play');
    await page.locator('#playtest-hand [data-card-id]').nth(index).click();
    const result = await (await response).json();
    if (result.battle) sessions.add(result.battle.sessionId);
    await page.waitForFunction(() => !document.querySelector('#playtest-play').disabled && !document.querySelector('#playtest-discard').disabled, {}, {timeout:30000});
    return result;
  };
  const editHealth = async value => {
    await page.locator('.section-button').filter({hasText:'玩家模板'}).click();
    await page.locator('.definition-button').first().click();
    const input = page.locator('#editor > .field-grid [data-key="maxHealth"]');
    await input.fill(value); await input.press('Tab'); await settle();
    await page.locator('#playtest-tab').click();
  };

  try {
    await page.goto(base); await settle();
    await load(scene); await page.locator('#playtest-tab').click();
    let opening, current;
    await test('对局试玩列出当前内容包的四个对局配置', async () => {
      assert.equal(await page.locator('#playtest-view').isVisible(), true);
      assert.equal(await page.locator('#playtest-encounter option').count(), 4);
      assert.equal(await page.locator('#playtest-hand [data-card-id]').count(), 0);
      await page.locator('#playtest-encounter').selectOption('single-battle-new');
      await page.locator('#playtest-seed').fill('9007199254740993');
      const result = await clickRequest('#playtest-start', 'start');
      assert.equal(result.success, true, JSON.stringify(result));
      opening = result.battle; current = opening;
      assert.equal(opening.seed, '9007199254740993');
      assert.equal(await page.locator('#playtest-hand [data-card-id]').count(), 8);
      assert.ok((await page.locator('#playtest-log').textContent()).length > 0);
    });
    await test('选牌预览显示真实规则结果且不会改变手牌与对局', async () => {
      const before = await handIds();
      const result = await choose(0);
      assert.equal(result.preview.legal, true);
      assert.equal(result.preview.hand.category, 'HighCard');
      assert.equal(result.battle.revision, opening.revision);
      assert.deepEqual(await handIds(), before);
      assert.equal(await page.locator('#playtest-hand [aria-pressed="true"]').count(), 1);
      assert.match(await page.locator('#playtest-preview').innerText(), /高牌/);
    });
    await test('网页出牌扣能量、结算伤害并补回手牌', async () => {
      const result = await clickRequest('#playtest-play', 'command', 'Play');
      assert.equal(result.success, true, JSON.stringify(result)); current = result.battle;
      assert.equal(current.players[0].energy, 2);
      assert.ok(Number(current.enemies[0].health) < 1200);
      assert.equal(await page.locator('#playtest-hand [data-card-id]').count(), 8);
      assert.equal(await page.locator('#playtest-hand [aria-pressed="true"]').count(), 0);
      assert.match(await page.locator('#playtest-log').textContent(), /伤害/);
    });
    await test('网页弃牌获得护盾并展示消耗后的次数', async () => {
      await choose(0);
      const result = await clickRequest('#playtest-discard', 'command', 'Discard');
      assert.equal(result.success, true, JSON.stringify(result)); current = result.battle;
      assert.equal(current.players[0].block, '8');
      assert.equal(current.players[0].energy, 1);
      assert.equal(current.players[0].discardsRemaining, 2);
      assert.equal(await page.locator('#playtest-hand [data-card-id]').count(), 8);
    });
    await test('结束回合自动执行敌方与下一回合并记录结算', async () => {
      const result = await clickRequest('#playtest-end-turn', 'command', 'EndTurn');
      assert.equal(result.success, true, JSON.stringify(result)); current = result.battle;
      assert.equal(current.players[0].health, '568');
      assert.equal(current.players[0].energy, 3);
      assert.ok(current.round > opening.round);
      assert.ok(result.steps.some(step => step.actor === 'enemy'));
      assert.match(await page.locator('#playtest-log').textContent(), /敌人行动/);
    });
    await test('固定种子重开恢复完全相同的起手牌', async () => {
      const result = await clickRequest('#playtest-restart', 'start');
      assert.equal(result.success, true, JSON.stringify(result)); current = result.battle;
      assert.notEqual(current.runId, opening.runId);
      assert.equal(current.seed, opening.seed);
      assert.deepEqual(await handIds(), opening.players[0].hand.map(card => card.id));
      assert.equal(current.players[0].health, '800');
    });
    await test('配置修改提示重开生效且旧局沿用开局配置', async () => {
      await editHealth('999');
      assert.match(await page.locator('#playtest-config-notice').innerText(), /重开/);
      const result = await clickRequest('#playtest-end-turn', 'command', 'EndTurn');
      assert.equal(result.success, true, JSON.stringify(result)); current = result.battle;
      assert.equal(current.players[0].maxHealth, '800');
      assert.equal(current.players[0].health, '560');
      const restarted = await clickRequest('#playtest-restart', 'start');
      assert.equal(restarted.success, true, JSON.stringify(restarted)); current = restarted.battle;
      assert.equal(current.players[0].health, '999');
      assert.notEqual(current.fingerprint, opening.fingerprint);
    });
    await test('校验失败的重开保留当前对局并显示具体诊断', async () => {
      await editHealth('0');
      const priorRun = current.runId;
      const result = await clickRequest('#playtest-restart', 'start');
      assert.equal(result.success, false);
      assert.equal(result.battle.runId, priorRun);
      assert.ok(result.diagnostics.length > 0);
      assert.ok((await page.locator('#playtest-diagnostics').innerText()).length > 0);
      assert.equal(await page.locator('#playtest-hand [data-card-id]').count(), 8);
      assert.equal(await page.locator('#playtest-end-turn').isDisabled(), false);
      await editHealth('999');
    });
    await test('未应用的 XML 源码阻止新开局且保留旧局操作', async () => {
      await page.locator('#xml-tab').click();
      await page.locator('#xml-source').fill('<pending/>');
      await page.locator('#playtest-tab').click();
      assert.equal(await page.locator('#playtest-start').isDisabled(), true);
      assert.equal(await page.locator('#playtest-restart').isDisabled(), true);
      assert.equal(await page.locator('#playtest-end-turn').isDisabled(), false);
      assert.match(await page.locator('#playtest-config-notice').innerText(), /未应用|应用/);
      await page.locator('#xml-tab').click();
      await page.locator('#reset-xml').click(); await page.locator('#modal-confirm').click();
      await page.locator('#playtest-tab').click();
      assert.equal(await page.locator('#playtest-restart').isDisabled(), false);
    });
    await test('服务错误明确反馈，成功结算记录仍保留', async () => {
      const before = await handIds();
      const log = await page.locator('#playtest-log').textContent();
      await page.route('**/api/playtest', route => route.fulfill({status:503,contentType:'application/json',body:'{"error":"测试服务暂不可用"}'}));
      await page.locator('#playtest-end-turn').click();
      await page.waitForFunction(() => document.querySelector('#playtest-message').textContent.includes('暂停操作'));
      assert.deepEqual(await handIds(), before);
      assert.equal(await page.locator('#playtest-log').textContent(), log);
      await page.unroute('**/api/playtest');
    });
    await test('遗物修改后的手动选敌要求在网页可选择并生效', async () => {
      const xml = scene.replace('</Relics>', '<Relic id="manual-target"><HandModifier id="target" category="HighCard" operation="ChangeTarget" stepId="damage/1" target="Opponents" selection="Requested" requestSlot="attack"/></Relic></Relics>')
        .replace('<Loadout id="baseline" name="默认·无遗物" deckRef="standard-deck"/>', '<Loadout id="baseline" deckRef="standard-deck"><Relic instanceId="manual-target" ref="manual-target"/></Loadout>')
        .replace('<Enemy id="enemy" ref="guardian"/></Encounter>', '<Enemy id="enemy" ref="guardian"/><Enemy id="enemy-2" ref="guardian"/></Encounter>');
      await load(xml); await page.locator('#playtest-tab').click();
      const started = await clickRequest('#playtest-restart', 'start');
      assert.equal(started.success, true, JSON.stringify(started));
      const missingResponse = responseFor('preview', 'Play');
      await page.locator('#playtest-hand [data-card-id]').first().click();
      const missing = await (await missingResponse).json();
      assert.equal(missing.preview.legal, false);
      const target = page.locator('#playtest-targets select[data-target-slot="attack"]');
      await target.waitFor();
      assert.equal(await target.locator('option').count(), 3);
      assert.equal(await page.locator('#playtest-play').isDisabled(), true);
      const chosenResponse = responseFor('preview', 'Play');
      await target.selectOption('enemy-2');
      const chosen = await (await chosenResponse).json();
      assert.equal(chosen.preview.legal, true);
      await page.waitForFunction(() => !document.querySelector('#playtest-play').disabled);
      const played = await clickRequest('#playtest-play', 'command', 'Play');
      assert.equal(played.success, true, JSON.stringify(played));
      assert.equal(played.battle.enemies[0].health, '1200');
      assert.ok(Number(played.battle.enemies[1].health) < 1200);
    });
    await test('紧凑屏幕和 HTML 名称安全显示', async () => {
      await load(scene.replaceAll('<Player id="player"', '<Player id="&lt;img src=x onerror=alert(1)&gt;"').replace('name="玩家"', 'name="&lt;img src=x onerror=alert(1)&gt;"'));
      await page.locator('#playtest-tab').click();
      const result = await clickRequest('#playtest-restart', 'start');
      assert.equal(result.success, true, JSON.stringify(result));
      assert.equal(await page.locator('#playtest-view img').count(), 0);
      assert.match(await page.locator('#playtest-view').innerText(), /<img src=x onerror=alert\(1\)>/);
      await page.setViewportSize({width:1100,height:850});
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
      await page.setViewportSize({width:1440,height:1000});
    });
    await test('结束试玩释放会话并能立即重新开局', async () => {
      const closed = await clickRequest('#playtest-close', 'close');
      assert.equal(closed.success, true, JSON.stringify(closed));
      assert.equal(await page.locator('#playtest-status').innerText(), '尚未开局');
      assert.equal(await page.locator('#playtest-live').isVisible(), false);
      assert.equal(await page.locator('#playtest-restart').isDisabled(), true);
      await load(scene); await page.locator('#playtest-tab').click();
      const reopened = await clickRequest('#playtest-start', 'start');
      assert.equal(reopened.success, true, JSON.stringify(reopened)); current = reopened.battle;
      assert.equal(await page.locator('#playtest-hand [data-card-id]').count(), 8);
    });
    await test('刷新页面释放旧对局并保留配置草稿', async () => {
      if (!current) {
        await page.locator('#playtest-encounter').selectOption('single-battle-new');
        const started = await clickRequest('#playtest-start', 'start');
        assert.equal(started.success, true, JSON.stringify(started)); current = started.battle;
      }
      const oldSession = current.sessionId;
      const bootstrap = await (await context.request.get(base + '/api/bootstrap')).json();
      await page.locator('#save').click();
      await page.reload();
      await page.waitForSelector('#restore-banner:not([hidden])');
      let state;
      for (let attempt = 0; attempt < 30; attempt++) {
        state = await (await context.request.post(base + '/api/playtest', {
          headers:{'X-Editor-Token':bootstrap.token}, data:{op:'state',sessionId:oldSession}
        })).json();
        if (state.error === 'SessionNotFound') break;
        await page.waitForTimeout(100);
      }
      assert.equal(state.error, 'SessionNotFound', JSON.stringify(state));
      await page.getByRole('button', {name:'恢复草稿',exact:true}).click();
      await page.waitForFunction(() => document.querySelector('#restore-banner').hidden);
      await settle();
      await page.locator('#xml-tab').click();
      assert.match(await page.locator('#xml-source').inputValue(), /id="single-battle-new-content"/);
      await page.locator('#playtest-tab').click();
      assert.equal(await page.locator('#playtest-encounter option').count(), 4);
      assert.equal(await page.locator('#playtest-status').innerText(), '尚未开局');
      assert.equal(await page.locator('#playtest-live').isVisible(), false);
    });
    await page.screenshot({path:process.env.PLAYTEST_SCREENSHOT || path.join(os.tmpdir(),'battle-content-playtest.png'),fullPage:true});
    assert.deepEqual(errors, []);
    console.log(`Playtest browser: ${passes} passed, 0 failed.`);
  } finally {
    // Dispose this isolated test's sessions without touching other tabs' battles.
    try {
      const bootstrap = await (await context.request.get(base + '/api/bootstrap')).json();
      for (const sessionId of sessions) {
        const headers = {'X-Editor-Token':bootstrap.token};
        const state = await (await context.request.post(base + '/api/playtest', {headers,data:{op:'state',sessionId}})).json();
        if (state.battle) await context.request.post(base + '/api/playtest', {headers,data:{op:'close',sessionId,runId:state.battle.runId,revision:state.battle.revision}});
      }
    } catch { /* The offline-service case must still close the isolated browser. */ }
    await context.close(); await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode=1; });
