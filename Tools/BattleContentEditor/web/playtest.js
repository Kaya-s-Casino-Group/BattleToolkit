// The browser only presents authoritative C# results; it never settles combat.
const node=(tag,cls,text)=>{const n=document.createElement(tag);if(cls)n.className=cls;if(text!=null)n.textContent=String(text);return n;};
const button=(text,fn,cls='')=>{const n=node('button',cls,text);n.type='button';n.addEventListener('click',fn);return n;};
const phaseNames={AwaitingPlayerHand:'玩家行动',AwaitingEnemyAction:'敌人行动',Player:'玩家行动',Enemy:'敌人行动',PlayerTurn:'玩家行动',EnemyTurn:'敌人行动',PlayerPhase:'玩家行动',EnemyPhase:'敌人行动',Finished:'对局结束',NotStarted:'尚未开始',BattleEnded:'对局结束'};
const outcomeNames={Victory:'胜利',Defeat:'失败',Win:'胜利',Lose:'失败',PlayerVictory:'胜利',EnemyVictory:'失败',Draw:'平局',None:'进行中',Ongoing:'进行中'};
const suitNames={Spades:'♠',Hearts:'♥',Clubs:'♣',Diamonds:'♦',Spade:'♠',Heart:'♥',Club:'♣',Diamond:'♦'};
const rankNames={Ace:'A',Jack:'J',Queen:'Q',King:'K',Two:'2',Three:'3',Four:'4',Five:'5',Six:'6',Seven:'7',Eight:'8',Nine:'9',Ten:'10'};

export function createPlaytest(root,{getConfig,getToken}) {
 const sessionId=crypto.randomUUID();
 let battle=null,actor='',selected=new Set(),bindings={},previews={},previewSequence=0,previewTimer;
 let busy=false,previewBusy=false,uncertain=false,logs=[],startXml=null,configKey='',message='',diagnostics=[];
 let encounterValue='',newBattleSequence=0,renderedLogKey='';
 const refs={};
 const put=(id,n)=>{n.id='playtest-'+id;refs[id]=n;return n;};
 const label=(text,control)=>{const n=node('label','playtest-field');n.append(node('span','',text),control);return n;};
 const setup=node('section','playtest-setup');
 setup.append(label('对局配置',put('encounter',node('select'))),label('随机种子',put('seed',node('input'))));
 refs.seed.type='text';refs.seed.maxLength=256;refs.seed.value='1';refs.seed.placeholder='数字或文字均可';refs.seed.setAttribute('aria-label','随机种子');
 refs.encounter.setAttribute('aria-label','试玩对局配置');
 const startActions=node('div','playtest-start-actions');
 startActions.append(put('start',button('校验并开局',()=>start(false),'primary')),put('restart',button('用当前配置同种子重开',()=>start(true))));
 setup.append(startActions);root.append(setup,node('p','muted playtest-explanation','相同配置和种子可重现相同开局。试玩固定使用开局时的配置，编辑内容后重开生效。'));
 root.append(put('config-notice',node('p','playtest-config-notice')),put('message',node('p','playtest-message')),put('diagnostics',node('div')));
 refs.message.setAttribute('role','status');refs.message.setAttribute('aria-live','polite');
 const toolbar=node('div','playtest-battle-heading');const sessionActions=node('div','row');sessionActions.append(put('sync',button('重新同步对局',sync)),put('close',button('结束试玩',close)));toolbar.append(put('status',node('h2')),sessionActions);
 root.append(toolbar,put('empty',node('div','empty','选择一个对局配置并开局，即可在这里选牌、出牌和弃牌。')));
 const live=put('live',node('div'));root.append(live);
 const field=node('div','playtest-battlefield');field.append(put('enemies',node('div','playtest-units')),put('players',node('div','playtest-units')));live.append(field);
 const handHeading=node('div','playtest-hand-heading');handHeading.append(node('h3','','手牌'),label('当前操作角色',put('actor',node('select'))),put('hand-count',node('span','muted')));live.append(handHeading);
 refs.actor.setAttribute('aria-label','当前操作角色');
 live.append(put('hand',node('div','playtest-hand')),put('targets',node('div','playtest-targets')));
 const actionBar=node('div','playtest-action-bar');actionBar.append(put('play',button('出牌',()=>command('Play'),'primary')),put('discard',button('弃牌',()=>command('Discard'))),put('end-turn',button('结束回合',()=>command('EndTurn'))),put('clear',button('取消选牌',()=>{selected.clear();bindings={};queuePreview();render();})));
 live.append(actionBar,put('preview',node('div','playtest-preview')));
 const logSection=node('section','playtest-log-section');logSection.append(node('h3','','结算记录'),node('p','muted','按实际结算顺序记录；选牌预览不会写入这里。'),put('log',node('div','playtest-log')));live.append(logSection);
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
 function actionPayload(action){return {runId:battle.runId,revision:battle.revision,actor,action,selected:action==='EndTurn'?[]:[...selected],targetBindings:action==='Play'?bindings:{}};}
 function invalidatePreviews(){clearTimeout(previewTimer);previewSequence++;previews={};previewBusy=false;}
 function adopt(next){
  if(!next)return;
  const changed=!battle||battle.runId!==next.runId||battle.revision!==next.revision;
  if(battle&&battle.runId!==next.runId){startXml=null;logs=[];}
  battle=next;
  if(!battle.players.some(p=>p.id===actor&&p.canAct))actor=battle.actors?.find(id=>battle.players.some(p=>p.id===id&&p.canAct))||battle.players.find(p=>p.canAct)?.id||battle.players[0]?.id||'';
  if(changed){selected.clear();bindings={};invalidatePreviews();}
 }
 function addSteps(steps){logs.push(...(steps||[]));if(logs.length>500)logs=logs.slice(-500);}
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
  if(action!=='EndTurn'&&(previewBusy||!previews[action]?.legal))return;
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
  if(key!==configKey){configKey=key;refs.encounter.replaceChildren();for(const item of config.encounters)if(item.id)refs.encounter.append(new Option(item.name+' · '+item.id,item.id));if([...refs.encounter.options].some(o=>o.value===encounterValue))refs.encounter.value=encounterValue;encounterValue=refs.encounter.value;}
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
 }
 function appendStatuses(host,unit){
  const statuses=node('div','playtest-statuses');
  if(!unit.statuses?.length)statuses.append(node('span','muted','无状态'));
  for(const status of unit.statuses||[])statuses.append(node('span','playtest-status',status.name+(status.stacks>1?' ×'+status.stacks:'')+' · '+status.duration));
  host.append(statuses);
 }
 function unitCard(unit,isPlayer){
  const box=node('article','playtest-unit'+(!unit.alive?' defeated':'')+(unit.id===actor?' current':''));box.dataset.participantId=unit.id;
  const heading=node('div','playtest-unit-heading');heading.append(node('strong','',unit.name||unit.id),node('small','',unit.alive?(isPlayer?'我方':'敌方'):'已倒下'));box.append(heading);
  box.append(node('p','playtest-vitals','生命 '+unit.health+' / '+unit.maxHealth+'　护盾 '+unit.block));appendStatuses(box,unit);
  if(isPlayer){
   box.append(node('p','playtest-unit-detail','能量 '+unit.energy+' / '+unit.maxEnergy+'　剩余弃牌 '+(unit.discardsRemaining==null?'不限':unit.discardsRemaining)),node('p','playtest-unit-detail','牌库 '+unit.deckCount+' 张　弃牌堆 '+unit.discardCount+' 张'+(unit.hasEnded?' · 本回合已结束':'')));
   const relics=node('div','playtest-relics');
   for(const relic of unit.relics||[])relics.append(node('span','',relic.name+' · 等级 '+relic.level+' · 可用次数 '+(relic.charges==null?'不限':relic.charges)+' · 冷却 '+relic.cooldown+' · 本回合已用 '+relic.usesThisRound));
   if(!unit.relics?.length)relics.append(node('span','muted','无遗物'));box.append(relics);
  }else if(unit.alive){const intent=node('div','playtest-intent');intent.append(node('strong','','行动意图'));for(const line of unit.intent||[])intent.append(node('p','',line));box.append(intent);}
  return box;
 }
 function renderHand(){
  const player=currentPlayer();refs.hand.replaceChildren();refs.actor.replaceChildren();
  for(const p of battle.players)refs.actor.append(new Option((p.name||p.id)+(p.canAct?' · 可行动':p.alive?' · 已结束':' · 已倒下'),p.id));
  refs.actor.value=actor;refs.actor.disabled=busy||battle.players.length<2;
  refs['hand-count'].textContent=player?'已选 '+selected.size+' / '+player.hand.length+' 张 · 手牌上限 '+player.handLimit:'';
  for(const card of player?.hand||[]){
   const chosen=selected.has(card.id),red=/Heart|Diamond/i.test(card.suit);
   const cardButton=button('',()=>{if(selected.has(card.id))selected.delete(card.id);else selected.add(card.id);bindings={};queuePreview();render();},'playtest-card'+(chosen?' selected':'')+(red?' red-suit':''));
   cardButton.dataset.cardId=card.id;cardButton.setAttribute('aria-pressed',String(chosen));cardButton.setAttribute('aria-label',card.label||card.id);cardButton.title=[card.label,card.effects].filter(Boolean).join(' · ');cardButton.disabled=busy||uncertain||battle.finished||!player.canAct||card.disabled;
   cardButton.append(node('span','playtest-card-rank',rankNames[card.rank]||card.rank),node('span','playtest-card-suit',suitNames[card.suit]||card.suit),node('small','',card.disabled?'不可使用':chosen?'已选择':card.effects&&card.effects!=='None'?'附有效果':''));refs.hand.append(cardButton);
  }
  refs.targets.replaceChildren();
  for(const target of previews.Play?.targets||[]){
   const select=node('select');select.dataset.targetSlot=target.slot;select.setAttribute('aria-label','效果目标 '+target.label);select.append(new Option(target.required?'请选择目标…':'自动选择',''));
   for(const candidate of target.candidates)select.append(new Option(candidate.label,candidate.id));select.value=bindings[target.slot]?.[0]||'';select.disabled=busy;
   select.addEventListener('change',()=>{if(select.value)bindings[target.slot]=[select.value];else delete bindings[target.slot];queuePreview();render();});refs.targets.append(label(target.label+(target.required?' *':''),select));
  }
  refs.play.textContent='出牌 · '+(player?.costs.play??'—')+' 能量';refs.discard.textContent='弃牌 · '+(player?.costs.discard??'—')+' 能量';
  const unavailable=busy||uncertain||battle.finished||!player?.canAct;
  refs.play.disabled=unavailable||previewBusy||!previews.Play?.legal;refs.discard.disabled=unavailable||previewBusy||!previews.Discard?.legal;refs['end-turn'].disabled=unavailable;refs.clear.disabled=busy||!selected.size;
  renderPreview();
 }
 function renderPreview(){
  refs.preview.replaceChildren();
  if(!selected.size){refs.preview.append(node('p','muted',battle.finished?'对局已结束。':'点击手牌选择要出的牌或要弃掉的牌。'));return;}
  if(previewBusy){refs.preview.append(node('p','muted','正在预览出牌与弃牌效果…'));return;}
  refs.preview.append(node('p','muted','以下包含此次操作及随后自动发生的结算，例如敌人行动和补牌。'));
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
  refs.sync.disabled=busy;refs.sync.hidden=!battle&&!uncertain;refs.close.disabled=busy||uncertain;refs.close.hidden=!battle;refs.live.hidden=!battle;refs.empty.hidden=!!battle;refs.status.textContent=battle?'第 '+battle.round+' 回合 · '+(battle.finished?(outcomeNames[battle.outcome]||battle.outcome):(phaseNames[battle.phase]||battle.phase))+' · '+battle.encounterId+' · 种子 '+battle.seed:'尚未开局';
  if(!battle)return;
  refs.status.dataset.revision=battle.revision;refs.status.dataset.runId=battle.runId;
  refs.enemies.replaceChildren(...battle.enemies.map(unit=>unitCard(unit,false)));refs.players.replaceChildren(...battle.players.map(unit=>unitCard(unit,true)));renderHand();renderLogs();
 }
 return {updateConfig,render};
}
