// The browser only presents authoritative C# results; it never settles combat.
const node=(tag,cls,text)=>{const n=document.createElement(tag);if(cls)n.className=cls;if(text!=null)n.textContent=String(text);return n;};
const button=(text,fn,cls='')=>{const n=node('button',cls,text);n.type='button';n.addEventListener('click',fn);return n;};
const phaseNames={AwaitingPlayerHand:'玩家行动',AwaitingEnemyAction:'敌人行动',Player:'玩家行动',Enemy:'敌人行动',PlayerTurn:'玩家行动',EnemyTurn:'敌人行动',PlayerPhase:'玩家行动',EnemyPhase:'敌人行动',Finished:'对局结束',NotStarted:'尚未开始',BattleEnded:'对局结束'};
const outcomeNames={Victory:'胜利',Defeat:'失败',Win:'胜利',Lose:'失败',PlayerVictory:'胜利',EnemyVictory:'失败',Draw:'平局',None:'进行中',Ongoing:'进行中'};
const suitNames={Spades:'♠',Hearts:'♥',Clubs:'♣',Diamonds:'♦',Spade:'♠',Heart:'♥',Club:'♣',Diamond:'♦'};
const rankNames={Ace:'A',Jack:'J',Queen:'Q',King:'K',Two:'2',Three:'3',Four:'4',Five:'5',Six:'6',Seven:'7',Eight:'8',Nine:'9',Ten:'10'};

const icon=(name,cls='')=>{const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.classList.add('battle-icon');if(cls)svg.classList.add(cls);svg.setAttribute('aria-hidden','true');const use=document.createElementNS(svg.namespaceURI,'use');use.setAttribute('href','/assets/icons.svg#'+name);svg.append(use);return svg;};
const artFor=(value,isPlayer=false)=>isPlayer?'hero':/glutton|暴食|boss/i.test(value)?'gluttony':/summon|召唤|精英/i.test(value)?'summoner':'grunt';
const unitName=unit=>unit.displayName||unit.name||unit.id;
const stateIcon=status=>{const keys=(status.behaviorKeys||[]).join(' ');return /HitArmor/.test(keys)?'armor':/PeriodicDamage/.test(keys)?'bleed':/BlockActionWindow/.test(keys)?'stun':/PreventStatus/.test(keys)?'cleanse':/LifeSteal|Overheal/.test(keys)?'heal':/Reflect/.test(keys)?'shield':status.isNegative?'curse':'spark';};
export function createPlaytest(root,{getConfig,getToken,onLoadStandard}) {
 const sessionId=crypto.randomUUID();
 let battle=null,actor='',selected=new Set(),bindings={},previews={},previewSequence=0,previewTimer;
 let busy=false,previewBusy=false,uncertain=false,logs=[],startXml=null,configKey='',message='',diagnostics=[];
 let encounterValue='',newBattleSequence=0,renderedLogKey='',renderedUnitsKey='';
 let effects=new Map(),effectTime=0,inspectZone='',creationNotice='';
 let motion=!window.matchMedia('(prefers-reduced-motion: reduce)').matches;
 const refs={};
 const put=(id,n)=>{n.id='playtest-'+id;refs[id]=n;return n;};
 const label=(text,control)=>{const n=node('label','playtest-field');n.append(node('span','',text),control);return n;};
 const brand=node('div','playtest-brand');brand.append(node('div','', '♠  BATTLE LAB'),node('span','','一场牌局，一次冒险。'));root.append(brand);
 const setup=node('section','playtest-setup');
 setup.append(label('选择试炼',put('encounter',node('select'))),label('随机种子',put('seed',node('input'))));
 refs.seed.type='text';refs.seed.maxLength=256;refs.seed.value='1';refs.seed.placeholder='数字或文字均可';refs.seed.setAttribute('aria-label','随机种子');
 refs.encounter.setAttribute('aria-label','试玩对局配置');
 const startActions=node('div','playtest-start-actions');
 startActions.append(put('start',button('校验并开局',()=>start(false),'primary')),put('restart',button('同种子重开',()=>start(true))),put('standard',button('载入标准试炼',async()=>{try{if(!busy)await onLoadStandard?.();}catch(error){message=error.message;render();}})));
 setup.append(startActions);root.append(setup,put('encounter-rail',node('div','playtest-encounter-rail')));
 root.append(put('config-notice',node('p','playtest-config-notice')),put('message',node('p','playtest-message')),put('diagnostics',node('div')));
 refs.message.setAttribute('role','status');refs.message.setAttribute('aria-live','polite');
 const toolbar=node('div','playtest-battle-heading');const sessionActions=node('div','row');
 sessionActions.append(put('motion',button('动效 开',()=>{motion=!motion;effects.clear();renderedUnitsKey='';root.classList.toggle('motion-off',!motion);refs.motion.textContent='动效 '+(motion?'开':'关');refs.motion.setAttribute('aria-pressed',String(motion));render();})),put('sync',button('同步',sync)),put('close',button('结束试玩',close)));
 refs.motion.setAttribute('aria-pressed',String(motion));refs.motion.textContent='动效 '+(motion?'开':'关');root.classList.toggle('motion-off',!motion);
 toolbar.append(put('status',node('h2')),put('battle-tag',node('span','battle-tag')),sessionActions);root.append(toolbar);
 const empty=put('empty',node('div','playtest-empty'));empty.append(icon('summon'),node('h2','','下一场战斗，由你选牌。'),node('p','','选择上方试炼，开始一场标准对局。'),node('small','','先摸牌，再组合牌型。怪物将在你结束回合后行动。'));root.append(empty);
 const live=put('live',node('div'));root.append(live);
 const field=put('battlefield',node('div','playtest-battlefield'));
 const scenery=node('div','battle-scenery');scenery.setAttribute('aria-hidden','true');scenery.append(node('i','scene-arch arch-one'),node('i','scene-arch arch-two'),node('i','scene-arch arch-three'),node('i','scene-moon'),node('div','scene-floor'));field.append(scenery);
 const topRail=node('div','battle-top-rail');topRail.append(put('relic-tray',node('div','battle-relic-tray')),put('phase-banner',node('div','battle-phase-banner')));field.append(topRail);
 field.append(put('players',node('div','playtest-units player-team')),node('div','battle-versus','VS'),put('enemies',node('div','playtest-units enemy-team')),put('result',node('div','battle-result')));live.append(field);
 const handPanel=node('section','battle-hand-panel');live.append(handPanel);
 const handHeading=node('div','playtest-hand-heading');handHeading.append(put('energy',node('div','battle-energy')),label('操作角色',put('actor',node('select'))),put('flow-hint',node('span','battle-flow-hint')),put('hand-count',node('span','muted')));handPanel.append(handHeading);
 refs.actor.setAttribute('aria-label','当前操作角色');
 const handRow=node('div','battle-hand-row');handRow.append(put('deck-pile',button('',()=>togglePile('deck'),'battle-pile deck-pile')),put('hand',node('div','playtest-hand')),put('discard-pile',button('',()=>togglePile('discard'),'battle-pile discard-pile')));handPanel.append(put('creation-notice',node('p','battle-creation-notice')),handRow);
 refs['creation-notice'].setAttribute('role','status');refs['creation-notice'].setAttribute('aria-live','polite');
 const pilePanel=put('pile-view',node('section','battle-pile-view'));const pileHeading=node('div','pile-view-heading');pileHeading.append(put('pile-title',node('h3')),button('收起',()=>{const zone=inspectZone;inspectZone='';renderPiles();refs[zone+'-pile']?.focus();}));pilePanel.append(pileHeading,put('pile-hint',node('p')),put('pile-cards',node('div','pile-card-list')));handPanel.append(pilePanel,put('targets',node('div','playtest-targets')));
 const actionBar=node('div','playtest-action-bar');actionBar.append(put('play',button('出牌',()=>command('Play'),'primary')),put('discard',button('弃牌',()=>command('Discard'))),put('clear',button('取消选牌',()=>{selected.clear();bindings={};queuePreview();render();})),put('end-turn',button('结束回合',()=>command('EndTurn'))));
 const drawBar=put('draw-bar',node('div','playtest-draw-bar'));
 const count=node('input');count.type='number';count.min='1';count.value='5';count.setAttribute('aria-label','主动摸牌张数');put('draw-count',count);
 drawBar.append(icon('draw'),node('span','','本轮摸牌'),count,put('draw',button('摸牌',()=>command('Draw'),'primary')),put('skip-draw',button('跳过',()=>command('SkipDraw'))),put('replace-draw',button('先弃掉所选手牌',()=>command('DiscardForDraw'))),put('overflow',button('确认强制弃牌',()=>command('ConfirmOverflow'))));
 handPanel.append(drawBar,actionBar,put('preview',node('div','playtest-preview')));
 const logSection=node('details','playtest-log-section');logSection.append(node('summary','','战斗记录 · 展开查看每一步结算'),put('log',node('div','playtest-log')));live.append(logSection);
 refs.encounter.addEventListener('change',()=>{encounterValue=refs.encounter.value;updateConfig();});
 refs.actor.addEventListener('change',()=>{actor=refs.actor.value;selected.clear();bindings={};queuePreview();render();});
 window.addEventListener('pagehide',event=>{
  // A cached page may return with its current battle. Only release a confirmed,
  // idle session when this document is really being left, including a reload.
  if(event.persisted||busy||uncertain||!battle)return;
  fetch('/api/playtest',{method:'POST',keepalive:true,headers:{'Content-Type':'application/json','X-Editor-Token':getToken()},body:JSON.stringify({op:'close',sessionId,runId:battle.runId,revision:battle.revision})}).catch(()=>{});
 });

 async function request(op,payload={}) {
  const controller=new AbortController(),timeout=setTimeout(()=>controller.abort(),40000);
  try{
   const response=await fetch('/api/playtest',{method:'POST',headers:{'Content-Type':'application/json','X-Editor-Token':getToken()},body:JSON.stringify({op,sessionId,...payload}),signal:controller.signal});
   let data;try{data=await response.json();}catch{throw new Error('本机试玩服务没有返回可读取的结果。');}
   // A transport/server error can arrive after a command has committed. Never retry it.
   if(!response.ok&&!data.battle&&!data.diagnostics)throw new Error(data.message||data.error||'本机试玩服务不可用。');
   return data;
  }catch(error){if(error.name==='AbortError')throw new Error('本机试玩服务响应超时。');throw error;}
  finally{clearTimeout(timeout);}
 }
 function currentPlayer(){return battle?.players.find(p=>p.id===actor);}
 function actionPayload(action){return {runId:battle.runId,revision:battle.revision,actor,action,selected:['Play','Discard','DiscardForDraw','ConfirmOverflow'].includes(action)?[...selected]:[],drawCount:action==='Draw'?Number(refs['draw-count'].value):0,targetBindings:action==='Play'?bindings:{}};}
 function invalidatePreviews(){clearTimeout(previewTimer);previewSequence++;previews={};previewBusy=false;}
 function adopt(next){
  if(!next)return;
  const changed=!battle||battle.runId!==next.runId||battle.revision!==next.revision;
  if(battle&&battle.runId!==next.runId){startXml=null;logs=[];creationNotice='';inspectZone='';}
  if(changed){
   creationNotice='';effects=new Map();effectTime=performance.now();
   if(battle?.runId===next.runId){
    const previous=new Map([...battle.players,...battle.enemies].map(unit=>[unit.id,unit]));
    for(const unit of [...next.players,...next.enemies]){
     const before=previous.get(unit.id);const fx=[];
     if(!before)fx.push({kind:'spawn',text:'召唤'});
     else{const hp=Number(unit.health)-Number(before.health),shield=Number(unit.block)-Number(before.block);
      if(hp)fx.push({kind:hp<0?'hurt':'heal',text:(hp>0?'+':'')+hp});
      if(shield>0)fx.push({kind:'guard',text:'+'+shield});
      if((unit.statuses?.length||0)>(before.statuses?.length||0))fx.push({kind:'status',text:'状态'});
     }
     if(fx.length)effects.set(unit.id,fx);
    }
   }
  }
  battle=next;
  if(!battle.players.some(p=>p.id===actor&&p.canAct))actor=battle.actors?.find(id=>battle.players.some(p=>p.id===id&&p.canAct))||battle.players.find(p=>p.canAct)?.id||battle.players[0]?.id||'';
  if(changed){selected.clear();bindings={};invalidatePreviews();}
 }
 function addSteps(steps){
  const units=[...(battle?.players||[]),...(battle?.enemies||[])];const name=id=>unitName(units.find(unit=>unit.id===id)||{id});
  creationNotice=(steps||[]).flatMap(step=>step.cardCreations||[]).filter(event=>event.count>0).map(event=>name(event.actor)+' 塞入'+(event.unplayableCount===event.count?'废牌':'卡牌')+' × '+event.count+' → '+name(event.owner)+'的'+(event.zone==='discard'?'弃牌堆（洗回牌库后可能摸到）':event.zone==='deck'?(event.position==='Top'?'牌顶（下次优先摸到）':event.position==='Bottom'?'牌底':'牌库（随机洗入）'):event.zone==='hand'?'手牌':event.zone)).join('；');
  logs.push(...(steps||[]));if(logs.length>500)logs=logs.slice(-500);for(const step of steps||[])if(['Play','EnemyAction'].includes(step.action)){const fx=effects.get(step.actor)||[];fx.push({kind:'act',text:''});effects.set(step.actor,fx);}}
 function forgetExpired(data){if(data.error!=='SessionNotFound')return false;battle=null;actor='';selected.clear();bindings={};invalidatePreviews();startXml=null;logs=[];uncertain=false;return true;}
 function setResponseError(data){forgetExpired(data);message=data.message||data.error||'操作未完成，请检查配置与当前对局。';diagnostics=data.diagnostics||[];}
 async function start(restart){
  if(busy)return;const config=getConfig();
  if(config.sourceDirty){message='XML 源码有尚未应用的修改，请先应用修改或恢复当前内容，再开局。';render();return;}
  if(config.importing)return;
  const encounterId=refs.encounter.value,seed=restart&&battle?String(battle.seed):refs.seed.value.trim();
  if(!encounterId){message='请先创建或选择对局配置。';render();return;}
  if(!seed||seed.length>256){message='随机种子请填写 1 至 256 个字符，数字或文字均可。';render();return;}
  const attempt=++newBattleSequence,xml=config.xml;
  busy=true;uncertain=false;invalidatePreviews();message='正在校验当前配置并开局…';diagnostics=[];render();
  try{
   const data=await request('start',{xml,encounterId,seed,...(battle?{runId:battle.runId}:{})});
   if(attempt!==newBattleSequence)return;
   if(!data.success){adopt(data.battle);setResponseError(data);return;}
   adopt(data.battle);startXml=xml;logs=[];addSteps(data.steps);refs.seed.value=String(battle.seed);message='开局完成，可以选牌。';diagnostics=data.diagnostics||[];
  }catch(error){message=error.message+' 正在同步本机对局…';await recover({xml,encounterId,seed});}
  finally{busy=false;render();queuePreview();}
 }
 async function recover(attempt){
  try{
   const data=await request('state');
   if(data.success&&data.battle){
    const changedRun=battle?.runId!==data.battle.runId;
    adopt(data.battle);
    if(changedRun){
     logs=[];startXml=null;
     if(attempt&&data.battle.encounterId===attempt.encounterId&&String(data.battle.seed)===attempt.seed){
      const hash=await crypto.subtle.digest('SHA-256',new TextEncoder().encode(attempt.xml));
      const hex=[...new Uint8Array(hash)].map(value=>value.toString(16).padStart(2,'0')).join('');
      if(hex===data.battle.xmlHash)startXml=attempt.xml;
     }
    }
    uncertain=false;message='已同步当前对局。上次请求的结果未完整收到，请核对当前生命、能量和手牌；结算记录可能缺少该次操作。';
   }else{forgetExpired(data);uncertain=!!battle;message=(data.message||data.error||'没有可恢复的对局。')+' 可重新开局。';}
  }catch{uncertain=true;message='暂时无法确认对局状态，已暂停操作。请点击“重新同步对局”，避免重复出牌。';}
 }
 async function sync(){if(busy)return;busy=true;invalidatePreviews();render();try{await recover();}finally{busy=false;render();queuePreview();}}
 async function close(){
  if(busy||!battle)return;busy=true;invalidatePreviews();render();
  try{const data=await request('close',{runId:battle.runId,revision:battle.revision});if(data.success){forgetExpired({error:'SessionNotFound'});message='试玩已结束，可以重新选择配置开局。';diagnostics=[];}else{adopt(data.battle);setResponseError(data);}}
  catch{await recover();}finally{busy=false;render();queuePreview();}
 }
 async function command(action){
  const player=currentPlayer();if(busy||uncertain||!battle||battle.finished||!player?.canAct)return;
  if(['Play','Discard'].includes(action)&&(previewBusy||!previews[action]?.legal))return;
  if(action==='EndTurn'&&player.manualDraw&&player.window==='Ending')action='FinishTurn';
  const payload=actionPayload(action);busy=true;invalidatePreviews();message='正在结算…';diagnostics=[];render();
  try{
   const data=await request('command',payload);adopt(data.battle);
   if(data.success){addSteps(data.steps);selected.clear();bindings={};message=battle?.finished?'对局已结束，可使用同一种子重开。':'结算完成。';}
   else setResponseError(data);
  }catch(error){message=error.message;await recover();}
  finally{busy=false;render();queuePreview();}
 }
 function queuePreview(){
  invalidatePreviews();
  if(busy||uncertain||!battle||battle.finished||!currentPlayer()?.canAct||!selected.size)return;
  previewBusy=true;const sequence=previewSequence,runId=battle.runId,revision=battle.revision;
  const play=actionPayload('Play'),discard=actionPayload('Discard');
  previewTimer=setTimeout(async()=>{
   const results=await Promise.allSettled([request('preview',play),request('preview',discard)]);
   if(sequence!==previewSequence||battle?.runId!==runId||battle?.revision!==revision)return;
   previewBusy=false;
   for(let i=0;i<results.length;i++){
    const result=results[i],action=i===0?'Play':'Discard';
    if(result.status==='rejected'){previews[action]={legal:false,message:result.reason.message};continue;}
    const data=result.value;
    if(data.error==='SessionNotFound'){setResponseError(data);render();return;}
    if(data.battle&&(data.battle.runId!==runId||data.battle.revision!==revision)){adopt(data.battle);message='对局状态已更新，请重新选牌。';render();return;}
    previews[action]=data.success&&data.preview?data.preview:{legal:false,message:data.message||data.error||'无法预览此操作。'};
   }
   render();
  },160);
 }
 function updateConfig(){
  const config=getConfig(),key=JSON.stringify(config.encounters);
  if(key!==configKey){configKey=key;refs.encounter.replaceChildren();for(const item of config.encounters)if(item.id)refs.encounter.append(new Option(item.name||item.id,item.id));if([...refs.encounter.options].some(o=>o.value===encounterValue))refs.encounter.value=encounterValue;encounterValue=refs.encounter.value;}
  let note='';
  if(config.sourceDirty)note='XML 源码有尚未应用的修改。请先应用修改或恢复当前内容，再开局；已开始的对局仍可继续。';
  else if(battle&&startXml===null)note='尚未确认当前对局使用的配置版本。重开后会使用当前编辑内容。';
  else if(battle&&config.xml!==startXml)note='配置已修改，重开后生效。当前对局继续使用开局时的配置。';
  else if(battle&&refs.encounter.value!==battle.encounterId)note='已选择其他对局配置，开局后生效。当前对局仍为 '+battle.encounterId+'。';
  refs['config-notice'].textContent=note;refs['config-notice'].hidden=!note;
  refs.start.disabled=busy||config.sourceDirty||config.importing||!refs.encounter.value;
  refs.restart.disabled=refs.start.disabled||!battle;
  refs.start.textContent=battle?'用当前配置开新局':'校验并开局';
  refs.seed.disabled=busy;refs.encounter.disabled=busy;
  refs.standard.disabled=busy||config.importing;
  const focusedEncounter=refs['encounter-rail'].contains(document.activeElement)?document.activeElement.dataset.encounterId:null;
  refs['encounter-rail'].replaceChildren();
  for(const item of config.encounters){
   const chosen=refs.encounter.value===item.id,kind=artFor(item.id+' '+item.name);
   const choice=button('',()=>{refs.encounter.value=item.id;encounterValue=item.id;updateConfig();},'battle-encounter'+(chosen?' selected':''));
   choice.dataset.encounterId=item.id;choice.setAttribute('aria-pressed',String(chosen));choice.disabled=busy||config.importing;
   const portrait=node('img');portrait.src='/assets/'+kind+'.png';portrait.alt='';portrait.loading='lazy';portrait.addEventListener('error',()=>{portrait.hidden=true;});
   const words=node('span');words.append(node('strong','',item.name||item.id),node('small','',kind==='gluttony'?'BOSS / 废牌压力':kind==='summoner'?'精英 / 护甲与召唤':'遭遇 / 行为轮换'));choice.append(portrait,words,icon(chosen?'spark':'sword'));refs['encounter-rail'].append(choice);if(focusedEncounter===item.id&&!choice.disabled)choice.focus({preventScroll:true});
  }
  refs['encounter-rail'].hidden=config.encounters.length===0||config.encounters.length>6;

 }
 function appendStatuses(host,unit){
  const statuses=node('div','playtest-statuses');
  for(const status of unit.statuses||[]){
   const name=status.displayName||status.name,info=name+' ×'+status.stacks+' · '+status.duration;
   const badge=node('span','playtest-status '+(status.isNegative?'debuff':'buff'));badge.title=info;badge.tabIndex=0;badge.setAttribute('aria-label',info);
   badge.append(icon(stateIcon(status)),node('b','',status.stacks),node('span','battle-tooltip',info));statuses.append(badge);
  }
  host.append(statuses);
 }
 function unitCard(unit,isPlayer){
  const fx=motion&&performance.now()-effectTime<1100?effects.get(unit.id)||[]:[];
  const box=node('article','playtest-unit'+(!unit.alive?' defeated':'')+(unit.id===actor?' current':'')+fx.map(f=>' fx-'+f.kind).join(''));box.dataset.participantId=unit.id;box.dataset.definitionId=unit.definitionId||'';
  box.style.setProperty('--fx-delay',Math.min(0,effectTime-performance.now())+'ms');
  const kind=artFor(unit.definitionId||unitName(unit),isPlayer);box.dataset.art=kind;
  if(!isPlayer&&unit.alive){
   const intent=node('div','playtest-intent');const meta=unit.intentMeta,world=meta?.worldEffects||[];
   const intentKind=meta?.skipped?'stun':world.some(e=>e.kind==='Summon')?'summon':Number(meta?.damage)>0?'sword':world.some(e=>e.kind==='AddCards')?'curse':/盾|block/i.test((unit.intent||[]).join(' '))?'shield':'spark';
   const main=node('div','intent-main');main.append(icon(intentKind),node('b','',meta?.skipped?'跳过':Number(meta?.damage)>0?meta.damage:world.length?'×'+world[0].count:'准备'));
   intent.append(main,node('span','intent-name',meta?.name||(unit.intent||[])[0]||'等待'));intent.tabIndex=0;
   const description=(unit.intent||[]).join('；');intent.title=description;intent.setAttribute('aria-label','行动意图：'+description);
   intent.append(node('span','battle-tooltip',description));box.append(intent);
   if((unit.intentSchedule||[]).length>1){const strip=node('div','intent-schedule');strip.setAttribute('aria-label','未来八回合意图');for(const item of unit.intentSchedule){const step=node('span','intent-step',item.round+' '+item.name.split(/[：:]/)[0]);step.title='第 '+item.round+' 回合：'+item.name;strip.append(step);}box.append(strip);}
  }else box.append(node('div','hero-caption',isPlayer?'YOUR HAND. YOUR FATE.':''));
  const figure=node('div','unit-figure');figure.append(node('span','unit-shadow'));
  const image=node('img','unit-art');image.src='/assets/'+kind+'.png';image.alt=isPlayer?'玩家占位形象':unitName(unit)+'占位形象';image.draggable=false;image.addEventListener('error',()=>{image.hidden=true;figure.classList.add('art-missing');});figure.append(image,icon(isPlayer?'crown':'skull','unit-fallback'));
  for(const [i,f]of fx.filter(f=>f.text).entries()){const number=node('span','battle-float '+f.kind,f.text);number.style.setProperty('--float-index',i);figure.append(number);}
  box.append(figure);
  const heading=node('div','playtest-unit-heading');heading.append(node('strong','',unitName(unit)));if(!unit.alive)heading.append(node('small','','已倒下'));box.append(heading);
  const vitals=node('div','unit-vitals-row');const hp=node('div','unit-hp');hp.setAttribute('role','progressbar');hp.setAttribute('aria-label',unitName(unit)+'生命');hp.setAttribute('aria-valuenow',unit.health);hp.setAttribute('aria-valuemax',unit.maxHealth);hp.setAttribute('aria-valuemin','0');
  hp.style.setProperty('--health',Math.max(0,Math.min(100,Number(unit.health)/Number(unit.maxHealth)*100))+'%');hp.append(node('span','hp-fill'),node('span','hp-label',unit.health+' / '+unit.maxHealth));
  const shield=node('span','unit-shield');shield.title='护盾 '+unit.block;shield.append(icon('shield'),node('b','',unit.block));shield.classList.toggle('zero-block',Number(unit.block)===0);vitals.append(hp,shield);box.append(vitals);appendStatuses(box,unit);
  return box;
 }
 function renderRelics(){
  const relics=currentPlayer()?.relics||[];refs['relic-tray'].replaceChildren(node('small','relic-caption','遗物'));
  for(const relic of relics){const info=(relic.displayName||relic.name)+' · 等级 '+relic.level+' · 冷却 '+relic.cooldown+' · 次数 '+(relic.charges??'不限')+(relic.description?' / '+relic.description:'');
   const tile=node('span','battle-relic'+(relic.cooldown>0?' cooling':''));tile.title=info;tile.tabIndex=0;tile.setAttribute('aria-label',info);tile.append(icon('relic'),node('span','battle-tooltip',info));if(relic.level>1)tile.append(node('b','',relic.level));refs['relic-tray'].append(tile);
  }
  if(!relics.length){const empty=node('span','battle-relic empty-slot');empty.append(icon('relic'));refs['relic-tray'].append(empty,node('span','relic-empty-label','当前构筑无遗物'));}
 }
 function togglePile(zone){inspectZone=inspectZone===zone?'':zone;renderPiles();}
 function renderPiles(){
  const player=currentPlayer();
  for(const zone of ['deck','discard']){
   const pile=player?.piles?.[zone],title=zone==='deck'?'牌库':'弃牌堆',count=pile?.count??player?.[zone==='deck'?'deckCount':'discardCount']??0;
   const junk=(pile?.cards||[]).filter(card=>(card.effects||'').includes('Unplayable')).reduce((sum,card)=>sum+card.count,0),tile=refs[zone+'-pile'];
   tile.replaceChildren(icon(zone==='deck'?'draw':'discard'),node('strong','',count),node('small','',title));
   if(junk)tile.append(node('span','pile-junk-count','废牌 '+junk));
   tile.setAttribute('aria-label','查看'+title+'，共 '+count+' 张，其中废牌 '+junk+' 张');tile.setAttribute('aria-expanded',String(inspectZone===zone));tile.setAttribute('aria-controls','playtest-pile-view');tile.disabled=!pile;
  }
  refs['creation-notice'].textContent=creationNotice;refs['creation-notice'].hidden=!creationNotice;
  refs['pile-view'].hidden=!inspectZone;refs['pile-cards'].replaceChildren();if(!inspectZone)return;
  const pile=player?.piles?.[inspectZone];refs['pile-title'].textContent=(inspectZone==='deck'?'牌库':'弃牌堆')+' · '+(pile?.count??0)+' 张';
  refs['pile-hint'].textContent=inspectZone==='deck'?'按牌面合并展示，不代表摸牌顺序。':'牌库耗尽后，弃牌会洗回牌库；废牌摸入手牌后不可出牌，可按规则弃置。';
  for(const card of pile?.cards||[]){const junk=(card.effects||'').includes('Unplayable'),tile=node('div','pile-preview-card'+(junk?' junk':''));tile.title=card.label+' · '+card.effects;
   tile.append(junk?icon('curse'):node('span','pile-card-face',card.label),node('b','',junk?'废牌':card.disabled?'禁用牌':'扑克'),node('span','',junk?'不可出牌':card.disabled?'不可使用':''),node('small','','× '+card.count));refs['pile-cards'].append(tile);}
  if(!pile?.count)refs['pile-cards'].append(node('p','muted','这里暂时没有牌。'));
 }
 function renderHand(){
  const player=currentPlayer();refs.hand.replaceChildren();refs.actor.replaceChildren();
  for(const p of battle.players)refs.actor.append(new Option(unitName(p)+(p.canAct?' · 可行动':p.alive?' · 已结束':' · 已倒下'),p.id));
  refs.actor.value=actor;refs.actor.disabled=busy||battle.players.length<2;
  refs.actor.parentElement.hidden=battle.players.length<2;
  refs.energy.replaceChildren(icon('spark'),node('strong','',player?.energy??'0'),node('span','','/ '+(player?.maxEnergy??'0')));refs.energy.title='本轮能量';
  renderPiles();
  renderRelics();
  refs['hand-count'].textContent=player?'已选 '+selected.size+' / '+player.hand.length+' 张 · 手牌上限 '+player.handLimit:'';
  for(const [cardIndex,card] of (player?.hand||[]).entries()){
   const chosen=selected.has(card.id),red=/Heart|Diamond/i.test(card.suit);
   const cardButton=button('',()=>{if(selected.has(card.id))selected.delete(card.id);else selected.add(card.id);bindings={};queuePreview();render();},'playtest-card'+(chosen?' selected':'')+(red?' red-suit':''));
   cardButton.style.setProperty('--card-index',cardIndex);cardButton.dataset.cardId=card.id;cardButton.setAttribute('aria-pressed',String(chosen));cardButton.setAttribute('aria-label',card.label||card.id);cardButton.title=[card.label,card.effects].filter(Boolean).join(' · ');cardButton.disabled=busy||uncertain||battle.finished||!player.canAct||(card.disabled&&!['Draw','ForcedDiscard'].includes(player.window));
   if(card.canPlay===false&&!card.disabled){
    cardButton.classList.add('junk');cardButton.setAttribute('aria-label','废牌 · 不可出牌 · '+(card.canDiscard===false?'不可弃牌':'可弃牌'));
    cardButton.title='废牌：不能出牌，'+(card.canDiscard===false?'不能弃牌':'可以弃牌')+'。原始牌面 '+card.label+' · '+card.effects;
    cardButton.append(node('span','junk-card-title','废牌'),icon('curse','card-curse'),node('span','junk-card-rule','不可出牌'),node('small','',card.canDiscard===false?'不可弃牌':chosen?'已选 · 可弃牌':'可弃牌'));
   }else{
    cardButton.append(node('span','card-corner',(rankNames[card.rank]||card.rank)+(suitNames[card.suit]||card.suit)),node('span','playtest-card-rank',rankNames[card.rank]||card.rank),node('span','playtest-card-suit',suitNames[card.suit]||card.suit),node('small','',card.disabled?'不可使用':card.canDiscard===false?'不可弃牌':chosen?'已选择':card.effects&&card.effects!=='None'?'附有效果':''));
   }
   refs.hand.append(cardButton);
  }
  if(!player?.hand.length)refs.hand.append(node('p','hand-empty',player?.window==='Draw'?'牌库已准备好。摸牌，开始这一回合。':'暂时没有手牌。'));
  refs.targets.replaceChildren();
  for(const target of previews.Play?.targets||[]){
   const select=node('select');select.dataset.targetSlot=target.slot;select.setAttribute('aria-label','效果目标 '+target.label);select.append(new Option(target.required?'请选择目标…':'自动选择',''));
   for(const candidate of target.candidates)select.append(new Option(candidate.label,candidate.id));select.value=bindings[target.slot]?.[0]||'';select.disabled=busy;
   select.addEventListener('change',()=>{if(select.value)bindings[target.slot]=[select.value];else delete bindings[target.slot];queuePreview();render();});refs.targets.append(label(target.label+(target.required?' *':''),select));
  }
  refs.play.textContent='出牌 · '+(player?.costs.play??'—')+' 能量';refs.discard.textContent='弃牌 · '+(player?.costs.discard??'—')+' 能量';
  const unavailable=busy||uncertain||battle.finished||!player?.canAct;
  refs.play.disabled=unavailable||previewBusy||!previews.Play?.legal;refs.discard.disabled=unavailable||previewBusy||!previews.Discard?.legal;refs['end-turn'].disabled=unavailable;refs.clear.disabled=busy||!selected.size;
  const drawing=!battle.finished&&player?.manualDraw&&player.window==='Draw',forced=!battle.finished&&player?.manualDraw&&player.window==='ForcedDiscard';
  refs.draw.hidden=refs['skip-draw'].hidden=refs['replace-draw'].hidden=refs['draw-count'].hidden=!drawing;
  refs['draw-bar'].hidden=!drawing&&!forced;
  refs.overflow.hidden=!forced;refs.draw.disabled=unavailable||player?.drawLimit===0;refs['skip-draw'].disabled=unavailable;
  refs['replace-draw'].disabled=unavailable||!selected.size||!player.drawWarning;refs.overflow.disabled=unavailable||selected.size!==player?.requiredDiscards;
  refs['draw-count'].max=String(player?.drawLimit??5);
  if(drawing)refs['draw-count'].value=String(Math.max(player.drawLimit?1:0,Math.min(Number(refs['draw-count'].value)||5,player.drawLimit)));
  refs['end-turn'].disabled=unavailable||forced;refs['end-turn'].textContent=player?.window==='Ending'?'确认结束回合':'结束出牌阶段';
  refs['flow-hint'].textContent=forced?'必须恰好弃掉 '+player.requiredDiscards+' 张；本次默认不获得护盾。':drawing?
   (player.drawWarning?'手牌较多：可以少摸，或先选择手牌弃置，再摸牌。':'先选择摸牌张数，或跳过。'):'剩余行动 '+(player?.actionsRemaining??'—');
  renderPreview();
 }
 function renderPreview(){
  refs.preview.replaceChildren();
  if(!selected.size){refs.preview.append(node('p','muted',battle.finished?'对局已结束。':'点击手牌选择要出的牌或要弃掉的牌。'));return;}
  if(previewBusy){refs.preview.append(node('p','muted','正在预览出牌与弃牌效果…'));return;}
  refs.preview.append(node('p','muted','结算预览 · 包含此操作之后的自动行动'));
  for(const [action,title] of [['Play','出牌预览'],['Discard','弃牌预览']]){
   const preview=previews[action];if(!preview)continue;
   const section=node('section','playtest-preview-part');section.append(node('h3','',title));
   if(action==='Play'&&preview.hand)section.append(node('p','',preview.hand.label+' · 点数 '+preview.hand.points+' × 倍率 '+preview.hand.multiplier+' = 计分 '+preview.hand.total));
   if(preview.targetMessage)section.append(node('p','playtest-invalid',preview.targetMessage));
   if(!preview.legal)section.append(node('p','playtest-invalid',preview.message||preview.error||'当前不能执行此操作。'));
   else{const lines=(preview.steps||[]).flatMap(step=>(step.lines||[]).filter((line,index)=>!(action==='Play'&&preview.hand&&step.action==='Play'&&index===0&&line.includes('牌型得分'))));for(const line of lines)section.append(node('p','playtest-preview-line',line));if(!lines.length)section.append(node('p','muted','当前操作可以执行。'));}
   refs.preview.append(section);
  }
 }
 function renderLogs(){
  const key=(battle?.runId||'')+':'+logs.length+':'+(logs.at(-1)?.revision||'');if(key===renderedLogKey)return;renderedLogKey=key;
  refs.log.replaceChildren();
  if(!logs.length){refs.log.append(node('p','muted','尚无结算记录。'));return;}
  for(const [i,step]of logs.entries()){
   const entry=node('details','playtest-log-entry');entry.open=i===logs.length-1;entry.append(node('summary','',step.title||step.action||'规则结算'));
   for(const line of step.lines||[])entry.append(node('p','',line));refs.log.append(entry);
  }
  refs.log.scrollTop=refs.log.scrollHeight;
 }
 function render(){
  updateConfig();refs.message.textContent=message;refs.message.hidden=!message;refs.diagnostics.replaceChildren();
  for(const diagnostic of diagnostics){const row=node('div','issue'+(diagnostic.severity==='Warning'?' warning':''));row.append(node('strong','',(diagnostic.severity==='Warning'?'警告':'错误')+(diagnostic.definitionId?' · '+diagnostic.definitionId:'')),node('span','',diagnostic.message),node('small','',[diagnostic.path,diagnostic.line?'第 '+diagnostic.line+' 行':''].filter(Boolean).join(' · ')));refs.diagnostics.append(row);}
  refs.sync.disabled=busy;refs.sync.hidden=!battle&&!uncertain;refs.close.disabled=busy||uncertain;refs.close.hidden=!battle;refs.live.hidden=!battle;refs.empty.hidden=!!battle;refs.status.textContent=battle?'第 '+battle.round+' 回合 · '+(battle.finished?(outcomeNames[battle.outcome]||battle.outcome):(phaseNames[battle.phase]||battle.phase)):'尚未开局';
  refs['battle-tag'].textContent=battle?'种子 '+battle.seed:'';
  if(!battle)return;
  refs.status.dataset.revision=battle.revision;refs.status.dataset.runId=battle.runId;
  const unitsKey=battle.runId+':'+battle.revision+':'+actor;
  if(unitsKey!==renderedUnitsKey){renderedUnitsKey=unitsKey;refs.enemies.replaceChildren(...battle.enemies.map(unit=>unitCard(unit,false)));refs.players.replaceChildren(...battle.players.map(unit=>unitCard(unit,true)));}
  refs.battlefield.classList.toggle('battle-finished',battle.finished);
  refs['phase-banner'].textContent=battle.finished?'试炼结束':currentPlayer()?.window==='Draw'?'DRAW · 摸牌阶段':'YOUR TURN · 你的回合';
  refs.result.hidden=!battle.finished;refs.result.replaceChildren(icon(battle.outcome==='胜利'?'crown':'skull'),node('strong','',battle.outcome==='胜利'?'试炼完成':'下次，再来一局。'),node('span','',battle.outcome==='胜利'?'每一张牌，都算数。':'复盘记录或用同一种子重新挑战。'));
  renderHand();renderLogs();
 }
 return {updateConfig,render};
}
