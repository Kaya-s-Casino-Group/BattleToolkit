import {createPlaytest} from './playtest.js';

// All adventure and battle outcomes come from the C# host. This page only
// presents snapshots and stores the opaque replay save returned by the host.
const $=id=>document.getElementById(id);
const make=(tag,cls,text)=>{const n=document.createElement(tag);if(cls)n.className=cls;if(text!=null)n.textContent=String(text);return n;};
const SAVE_KEY='battle-adventure.save.v1',CONFIG_KEY='battle-adventure.config.v1';
const terminal=new Set(['Victory','Defeat','Abandoned','Completed','Failed']);
const typeNames={Battle:'小怪',Combat:'小怪',Normal:'小怪',Elite:'精英',Boss:'首领',Shop:'商店',Rest:'营地',Reward:'奖励',Start:'起点'};
const glyphs={Battle:'♠',Combat:'♠',Normal:'♠',Elite:'♜',Boss:'♛',Shop:'◇',Rest:'♨',Reward:'✧',Start:'◆'};
const suits={Spades:'♠',Hearts:'♥',Clubs:'♣',Diamonds:'♦',Spade:'♠',Heart:'♥',Club:'♣',Diamond:'♦'};
const suitOrder={Spades:0,Spade:0,Hearts:1,Heart:1,Clubs:2,Club:2,Diamonds:3,Diamond:3};
const ranks={Ace:'A',Two:'2',Three:'3',Four:'4',Five:'5',Six:'6',Seven:'7',Eight:'8',Nine:'9',Ten:'10',Jack:'J',Queen:'Q',King:'K'};
const rankOrder={Ace:1,Two:2,Three:3,Four:4,Five:5,Six:6,Seven:7,Eight:8,Nine:9,Ten:10,Jack:11,Queen:12,King:13};
let token='',catalog={characters:[],tarots:[],configXml:''},configXml='',standardConfigXml='',adventure=null,saved=null;
let screen='home',returnScreen='home',selectedCharacter='',selectedTarot='',inspectedTarot='',seed='1';
let busy=false,uncertain=false,battleView=null,battleRoot=null,battleIdentity='',lastSaveError=false,restoreBlocked=false,startWithoutSave=false,battleRequests=0,sessionMissing=false;
const sessionId=crypto.randomUUID();
const view=$('adventure-view');

function button(text,fn,cls='',id=''){
 const n=make('button',cls,text);n.type='button';if(id)n.id=id;
 n.addEventListener('click',()=>Promise.resolve().then(fn).catch(error=>notify(error.message,true)));return n;
}
function notify(text,error=false){const box=$('adventure-notice');box.textContent=text;box.hidden=!text;box.classList.toggle('error',error);}
function active(){return adventure&&!terminal.has(adventure.status||adventure.phase);}
function phase(){return adventure?.status||adventure?.phase||'Map';}
function titleForType(type){return typeNames[type]||'旅途节点';}
function download(text,name,type){const url=URL.createObjectURL(new Blob([text],{type})),a=make('a');a.href=url;a.download=name;document.body.append(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);}
function storageGet(key){try{return localStorage.getItem(key);}catch{return null;}}
function readSaved(){const raw=storageGet(SAVE_KEY);if(!raw)return null;try{const value=JSON.parse(raw);if(value&&typeof value.save==='string')return value;}catch{}notify('本机存档无法读取。原存档仍保留，可导出备份后恢复其他存档。',true);return {invalid:true,raw};}
function saveResult(result){
 if(!result.success||typeof result.save!=='string')return;
 const a=result.adventure||adventure;
 const next={schema:1,save:result.save,savedAt:new Date().toISOString(),summary:{character:a?.character?.name,chapter:a?.chapter,chapterCount:a?.chapterCount,health:a?.player?.health,maxHealth:a?.player?.maxHealth,gold:a?.player?.gold,status:a?.status||a?.phase}};
 saved=next;lastSaveError=false;restoreBlocked=false;startWithoutSave=false;
 try{localStorage.setItem(SAVE_KEY,JSON.stringify(next));}catch{lastSaveError=true;notify('浏览器保存失败，请立即导出存档。当前旅途仍可继续。',true);}
}
function accept(result,{renderNow=true}={}){
 if(result.adventure)adventure=result.adventure;
 if(result.tarots||result.adventure?.tarots)catalog.tarots=result.tarots||result.adventure.tarots;
 if(result.error==='SessionNotFound'){sessionMissing=true;uncertain=true;}
 else if(result.success)sessionMissing=false;
 saveResult(result);
 if(!result.success)notify(result.message||result.error||'操作未完成，请检查当前状态。',true);
 else if(!lastSaveError)notify(result.message||'');
 if(renderNow)render();else renderHeader();
}
async function request(op,payload={}){
 const controller=new AbortController(),timer=setTimeout(()=>controller.abort(),40000);
 try{
  const response=await fetch('/api/adventure',{method:'POST',headers:{'Content-Type':'application/json','X-Editor-Token':token},body:JSON.stringify({op,sessionId,...payload}),signal:controller.signal});
  let result;try{result=await response.json();}catch{throw new Error('本机服务没有返回可读取的结果。');}
  if(!response.ok&&!result.adventure&&!result.error)throw new Error(result.message||'本机冒险服务暂时不可用。');
  return result;
 }catch(error){if(error.name==='AbortError')throw new Error('本机服务响应超时，请同步后再继续。');if(error instanceof TypeError)throw new Error('无法连接本机冒险服务，请确认服务仍在运行后同步。');throw error;}finally{clearTimeout(timer);}
}
async function operation(op,payload={},after){
 if(busy)return;busy=true;renderHeader();lockControls(true);
 try{
  const result=await request(op,payload);
  if(result.success)uncertain=false;
  accept(result,{renderNow:false});
  if(result.success&&after)after(result);
  return result;
 }catch(error){uncertain=op!=='catalog';notify(error.message,true);}
 finally{busy=false;render();}
}
function command(action,extra={},after){
 if(!adventure)return;
 if(uncertain){notify('上次操作的结果尚未确认，请先同步旅途。',true);return;}
 return operation('command',{runId:adventure.runId,revision:adventure.revision,action,...extra},after);
}
function lockControls(value){for(const control of view.querySelectorAll('button,input,select,textarea')){if(value){control.dataset.wasDisabled=String(control.disabled);control.disabled=true;}else if(control.dataset.wasDisabled!=null){control.disabled=control.dataset.wasDisabled==='true';delete control.dataset.wasDisabled;}}}
async function refreshCatalog(){
 const payload={...(saved?.save&&!startWithoutSave&&!restoreBlocked?{save:saved.save}:{}),...(configXml?{configXml}:{})};
 const result=await request('catalog',payload);
 if(!result.success){notify(result.message||'无法读取冒险配置。',true);return false;}
 if(result.standardConfigXml)standardConfigXml=result.standardConfigXml;
 else if(!standardConfigXml&&!configXml)standardConfigXml=result.configXml||'';
 catalog={...catalog,...result};if(!configXml)configXml=storageGet(CONFIG_KEY)||catalog.configXml||'';
 return true;
}
function renderHeader(){
 const resource=$('adventure-resources');resource.replaceChildren();
 if(adventure){const p=adventure.player||{};resource.append(make('span','','第 '+(adventure.chapter??1)+' / '+(adventure.chapterCount??3)+' 章'),make('span','resource-heart','♥ '+(p.health??'—')+' / '+(p.maxHealth??'—')),make('span','resource-gold','◈ '+(p.gold??'—')));}
 $('adventure-deck').hidden=!adventure;
 $('adventure-deck').disabled=busy;$('adventure-menu').disabled=busy;$('adventure-tarots').disabled=busy;
 $('adventure-save-state').textContent=lastSaveError?'存档保存失败 · 请导出备份':saved?.savedAt?'已自动保存 · '+new Date(saved.savedAt).toLocaleTimeString('zh-CN',{hour:'2-digit',minute:'2-digit'}):saved?'有本机存档':'尚无冒险存档';
}
function heading(title,description,eyebrow=''){
 const h=make('div','adventure-heading'),copy=make('div');
 if(eyebrow)copy.append(make('div','adventure-eyebrow',eyebrow));
 copy.append(make('h1','',title));if(description)copy.append(make('p','',description));h.append(copy);view.append(h);return h;
}
function backButton(to='home'){return button('← 返回',()=>{screen=to;render();});}
function goHome(){screen='home';render();}
function render(){
 renderHeader();
 if(!uncertain)$('adventure-recovery')?.remove();
 // Keep the live combat DOM intact while it handles selection and animation.
 if(screen==='journey'&&adventure&&phase()==='Battle'&&battleRoot?.isConnected&&!uncertain){
  battleRoot.inert=false;
  if(adventure.battle&&battleIdentity!==adventure.battle.runId+':'+adventure.battle.revision){battleView?.setExternalBattle(adventure.battle);battleIdentity=adventure.battle.runId+':'+adventure.battle.revision;}
  return;
 }
 view.replaceChildren();battleRoot=null;battleView=null;battleIdentity='';
 if(screen==='home')renderHome();
 else if(screen==='characters')renderCharacters();
 else if(screen==='talents')renderTalents();
 else if(screen==='tarots')renderTarots();
 else if(!adventure){screen='home';renderHome();}
 else if(phase()==='Battle')renderBattle();
 else if(phase()==='Shop')renderShop();
 else if(phase()==='Rest')renderRest();
 else if(phase()==='Reward')renderReward();
 else if(terminal.has(phase()))renderEnding();
 else renderMap();
 if(uncertain&&adventure){const box=make('div','adventure-empty');box.id='adventure-recovery';box.append(make('p','',sessionMissing?'本机服务中已没有当前旅途，可以从最近一次成功保存处恢复。':'上次操作的结果尚未确认。请先同步旅途状态。'),sessionMissing?button('恢复已保存旅途',()=>operation('restore',{save:saved.save,runId:adventure.runId},()=>{screen='journey';}),'primary','adventure-restore-local'):button('同步旅途',()=>operation('state',{},()=>{screen='journey';}),'primary','adventure-sync'));view.prepend(box);}
 if(busy)lockControls(true);
}
function renderHome(){
 const home=make('section','adventure-home'),copy=make('div','home-copy');
 copy.append(make('div','adventure-eyebrow','每一次选择，都改变下一张牌'));
 const title=make('h1');title.append(document.createTextNode('命运在手，'),make('br'),make('span','','启程在即。'));copy.append(title,make('p','','穿过战斗、商店与篝火，把一副寻常的牌，变成属于你的冒险。'));
 const actions=make('div','home-buttons');
 actions.append(button('开始冒险　→',beginSetup,'primary','adventure-new'));
 const resume=button(adventure?'返回当前旅途　→':'继续冒险　→',resumeSaved,'','adventure-continue');resume.disabled=!adventure&&!saved?.save;actions.append(resume,button('塔罗图鉴　◇',()=>openTarots(),'','adventure-home-tarots'));copy.append(actions);
 if(saved?.summary){const s=saved.summary;copy.append(make('div','home-save-summary',(s.character||'上次旅途')+' · 第 '+(s.chapter??1)+' 章 · '+(terminal.has(s.status)?({Victory:'已通关',Completed:'已通关',Defeat:'已结束',Failed:'已结束',Abandoned:'已结束'}[s.status]):'血量 '+(s.health??'—')+' / '+(s.maxHealth??'—'))));}
 const stage=make('div','home-stage');stage.setAttribute('aria-hidden','true');const art=make('img');art.src='/assets/hero.png';art.alt='';stage.append(make('span','stage-mark','旅人 · 执牌者'),make('span','stage-suit','♠'),art);home.append(copy,stage);view.append(home);
 renderConfig();
}
async function beginSetup(){
 if(restoreBlocked&&!startWithoutSave){
  showDialog('原存档暂时无法恢复',body=>{
   body.append(make('p','','开始新的冒险会在开局成功后替换本机存档。请先导出原存档，便于之后修复或恢复。'));
   const row=make('div','adventure-actions');row.append(button('导出原存档',exportSave),button('取消',()=>$('adventure-dialog').close()),button('确认开始新冒险',async()=>{startWithoutSave=true;$('adventure-dialog').close();await setupCharacters();},'primary'));body.append(row);
  });return;
 }
 if(active()){
  showDialog('开始一段新旅途',body=>{
   body.append(make('p','','当前冒险尚未结束。开始新旅途会结束当前冒险，请先导出存档以便保留。'));
   const row=make('div','adventure-actions');row.append(button('导出当前存档',exportSave),button('继续当前旅途',()=>{$('adventure-dialog').close();screen='journey';render();}),button('结束并重新开始',async()=>{$('adventure-dialog').close();const result=await command('abandon');if(result?.success)await setupCharacters();},'danger'));body.append(row);
  });return;
 }
 await setupCharacters();
}
async function setupCharacters(){
 if(busy)return;
 busy=true;renderHeader();
 try{if(!await refreshCatalog())return;selectedCharacter='';selectedTarot='';inspectedTarot='';screen='characters';notify('');}finally{busy=false;render();}
}
async function resumeSaved(){
 if(adventure){screen='journey';render();return;}
 if(!saved?.save)return;
 await operation('restore',{save:saved.save},()=>{screen='journey';notify('旅途已恢复。');});
}
function renderConfig(){
 const panel=make('details','adventure-config');panel.id='adventure-config';
 panel.append(make('summary','','旅途配置与存档'));
 panel.append(make('p','',active()?'当前旅途的配置已固定；修改配置后，在下一次开始冒险时生效。':'此处包含章节、怪物、商店、休整和塔罗配置。未定数值使用可修改的试玩默认值。'));
 const xml=make('textarea');xml.id='adventure-config-xml';xml.setAttribute('aria-label','冒险配置 XML');xml.spellcheck=false;xml.value=configXml;
 xml.addEventListener('input',()=>{configXml=xml.value;try{localStorage.setItem(CONFIG_KEY,configXml);}catch{}});
 panel.append(xml);
 const configActions=make('div','adventure-actions');
 configActions.append(button('导入配置 XML',()=>$('adventure-config-file').click()),button('导出配置 XML',()=>download(configXml,'StandardAdventure.xml','application/xml')),button('载入标准配置',()=>{configXml=standardConfigXml||catalog.configXml||'';try{localStorage.setItem(CONFIG_KEY,configXml);}catch{}xml.value=configXml;notify('已载入标准冒险配置，下次开局生效。');}));
 const saveActions=make('div','adventure-actions'),exportButton=button('导出冒险存档',exportSave,'','adventure-export-save');exportButton.disabled=!saved;
 saveActions.append(exportButton,button('恢复冒险存档',()=>$('adventure-save-file').click(),'','adventure-import-save'));
 panel.append(configActions,make('p','','旅途会在每次成功操作后自动保存到本浏览器。换电脑前请导出存档。'),saveActions);view.append(panel);
}
function exportSave(){if(!saved)return;download(saved.invalid?saved.raw:JSON.stringify(saved,null,2),'牌途-冒险存档.json','application/json');}
function progress(step){const n=make('div','setup-progress');for(const [i,text] of ['选择角色','选择天赋','踏入旅途'].entries())n.append(make('span',i===step?'active':'',(i+1)+'　'+text));view.append(n);}
function renderCharacters(){
 const h=heading('谁将踏上这段旅途？','选择角色，再为旅途挑选一份初始天赋。','开始冒险');h.append(backButton());progress(0);
 const grid=make('div','character-grid');
 for(const character of catalog.characters||[]){const card=button('',()=>{selectedCharacter=character.id;render();},'character-card');card.dataset.characterId=character.id;card.setAttribute('aria-pressed',String(selectedCharacter===character.id));const art=make('img');art.src='/assets/'+(['hero','grunt','summoner','gluttony'].includes(character.art)?character.art:'hero')+'.png';art.alt='';card.append(art,make('strong','',character.name),make('small','',character.description));if(selectedCharacter===character.id)card.append(make('span','selected-check','✓'));grid.append(card);}
 if(!catalog.characters?.length)grid.append(make('div','adventure-empty','当前配置尚未提供角色。请检查冒险配置。'));
 view.append(grid);
 const controls=make('div','setup-controls'),seedLabel=make('label','setup-seed','旅途种子');
 const input=make('input');input.id='adventure-seed';input.type='text';input.maxLength=256;input.value=seed;input.setAttribute('aria-label','旅途种子');input.addEventListener('input',()=>{seed=input.value;});seedLabel.append(input);
 const next=button('选择初始天赋　→',()=>{screen='talents';render();},'primary','adventure-character-next');next.disabled=!selectedCharacter;controls.append(seedLabel,next);view.append(controls);
}
function tarotGlyph(t,index=0){return t.glyph||['☀','☽','✧'][index%3];}
function tarotUnlocked(t){return t.unlocked===true;}
function renderTalents(){const h=heading('让命运，倾向你。','选择一张已解锁塔罗，效果从这次冒险开始生效。','初始天赋');h.append(backButton('characters'));progress(1);renderTarotLayout(true);
 const controls=make('div','setup-controls');controls.append(make('span','muted','角色：'+(catalog.characters.find(x=>x.id===selectedCharacter)?.name||'')));
 const start=button('开始旅途　→',async()=>{await operation('start',{seed,characterId:selectedCharacter,tarotId:selectedTarot,configXml,...(adventure?.runId?{runId:adventure.runId}:{})},()=>{screen='journey';notify('旅途开始。选择亮起的节点继续。');});},'primary','adventure-start');start.disabled=!selectedCharacter||!catalog.tarots?.some(t=>t.id===selectedTarot&&tarotUnlocked(t));controls.append(start);view.append(controls);
}
function openTarots(){if(busy)return;returnScreen=screen;screen='tarots';inspectedTarot=selectedTarot||catalog.tarots?.[0]?.id||'';render();}
function renderTarots(){const h=heading('塔罗图鉴','查看已解锁的天赋与解锁条件。此处不会改变当前冒险。','命运收藏');h.append(backButton(returnScreen));renderTarotLayout(false);}
function renderTarotLayout(selectable){
 const layout=make('div','tarot-layout'),grid=make('div','tarot-grid'),detail=make('div','tarot-detail');grid.setAttribute('aria-label',selectable?'选择初始塔罗':'塔罗图鉴');
 const entries=catalog.tarots||[];const chosen=inspectedTarot||selectedTarot;let inspected=entries.find(t=>t.id===chosen);
 entries.forEach((tarot,index)=>{const item=button('',()=>{inspectedTarot=tarot.id;if(selectable&&tarotUnlocked(tarot))selectedTarot=tarot.id;render();},'tarot-card'+(tarotUnlocked(tarot)?'':' locked'));item.dataset.tarotId=tarot.id;item.setAttribute('aria-pressed',String((selectable?selectedTarot:chosen)===tarot.id));item.append(make('small','',tarot.number||String(index).padStart(2,'0')),make('span','tarot-glyph',tarotGlyph(tarot,index)),make('strong','',tarot.name),make('small','',tarotUnlocked(tarot)?'已解锁':'未解锁'));grid.append(item);});
 if(!entries.length)grid.append(make('div','adventure-empty','当前配置尚未提供塔罗。'));
 if(inspected){detail.append(make('span','tarot-glyph',tarotGlyph(inspected,entries.indexOf(inspected))),make('h2','',inspected.name),make('p','',inspected.description),make('p','unlock-note',tarotUnlocked(inspected)?'已解锁 · '+(selectable?'可作为初始天赋选择':'可在开始新冒险时选择'):(inspected.unlockDescription||'尚未满足解锁条件。')));if(inspected.isExample)detail.append(make('p','muted','此项为可配置的试玩示例。'));}
 else detail.append(make('h2','','等待你的选择'),make('p','','点击一张塔罗，查看它会如何影响旅途。'));
 layout.append(grid,detail);view.append(layout);
}
function journeyActions(h){
 const actions=make('div','actions');
 if(uncertain)actions.append(button('同步',()=>operation('state')));
 actions.append(button('导出存档',exportSave));h.append(actions);
}
function renderMap(){
 const h=heading('选择下一站','亮起的路标，是此刻可以前往的地方。','第 '+(adventure.chapter??1)+' 章 · 旅途地图');journeyActions(h);
 const layout=make('div','adventure-map-layout');layout.style.gridTemplateColumns='150px minmax(0,1fr)';
 const legend=make('aside','map-legend');legend.append(make('h2','','沿途地点'));
 for(const type of ['Battle','Elite','Boss','Shop','Rest']){const row=make('div','legend-item'),glyph=make('span');glyph.append(make('b','',glyphs[type]));row.append(glyph,make('div','',typeNames[type]));legend.append(row);}
 legend.append(make('div','map-status-key','金色　可以前往\n绿色　当前位置\n灰色　尚未到达'));
 const map=make('section','route-map');map.setAttribute('aria-label','旅途地图');
 let nodes=(adventure.nodes||[]).filter(n=>n.chapter==null||Number(n.chapter)===Number(adventure.chapter));
 if(!nodes.length)nodes=adventure.nodes||[];
 const rows=[...new Set(nodes.map(n=>Number(n.row??n.depth??0)))].sort((a,b)=>a-b),height=Math.max(420,rows.length*92+45);
 const canvas=make('div','route-canvas');canvas.style.height=height+'px';
 const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.setAttribute('viewBox','0 0 700 '+height);svg.setAttribute('preserveAspectRatio','none');svg.classList.add('route-lines');canvas.append(svg);
 const positions=new Map(),columns=nodes.map(n=>Number(n.column??0)),minColumn=Math.min(...columns),maxColumn=Math.max(...columns);
 for(const row of rows){const same=nodes.filter(n=>Number(n.row??n.depth??0)===row).sort((a,b)=>Number(a.column??0)-Number(b.column??0));same.forEach((n,index)=>positions.set(n.id,{x:maxColumn>minColumn?120+(Number(n.column??0)-minColumn)*460/(maxColumn-minColumn):(index+1)*700/(same.length+1),y:height-45-rows.indexOf(row)*92}));}
 for(const n of nodes)for(const nextId of n.nextIds||[]){const from=positions.get(n.id),to=positions.get(nextId);if(!from||!to)continue;const line=document.createElementNS(svg.namespaceURI,'line');for(const [key,value]of Object.entries({x1:from.x,y1:from.y,x2:to.x,y2:to.y}))line.setAttribute(key,String(value));if(n.state==='completed')line.classList.add('reached');svg.append(line);}
 for(const n of nodes){const state=String(n.state||'locked').toLowerCase(),available=state==='available'||n.available===true,pos=positions.get(n.id);
  const point=button('',()=>command('enter',{nodeId:n.id},()=>{screen='journey';}),'map-node '+state);point.dataset.nodeId=n.id;point.dataset.nodeType=n.type;point.dataset.nodeState=state;point.disabled=busy||uncertain||!available;
  point.style.left=(pos.x/7)+'%';point.style.top=pos.y+'px';point.setAttribute('aria-label',titleForType(n.type)+(available?' · 可以前往':state==='completed'?' · 已完成':state==='current'?' · 当前位置':' · 尚未到达'));
  const diamond=make('span','map-node-symbol');diamond.append(make('b','',state==='completed'?'✓':glyphs[n.type]||'◇'));point.append(diamond,make('span','map-node-label',titleForType(n.type)));
  if(state==='current'||n.id===adventure.currentNodeId)point.append(make('span','map-node-marker','你在这里'));canvas.append(point);
 }
 if(!nodes.length)canvas.append(make('div','adventure-empty','没有可显示的路线，请检查冒险配置。'));
 map.append(canvas);layout.append(legend,map);view.append(layout);
 const bottom=make('div','adventure-actions');bottom.style.marginTop='20px';bottom.append(button('查看牌库 · '+(adventure.deck?.length??0)+' 张',()=>showDeck()),button('查看遗物 · '+(adventure.relics?.length??0)+' 件',showRelics));view.append(bottom);
}
function renderShop(){
 const h=heading('旅途补给','整理牌组，挑选能陪你走得更远的遗物。','商店');const actions=make('div','actions'),leave=button('离开商店　→',()=>command('continue'),'primary','adventure-leave-shop');leave.disabled=uncertain;actions.append(leave);h.append(actions);
 const layout=make('div','adventure-shop'),keeper=make('aside','shop-keeper'),art=make('img');art.src='/assets/hero.png';art.alt='玩家';keeper.append(art,make('p','','“把多余的留在这里，\n把需要的带上路。”'));layout.append(keeper);
 const stock=make('div'),shop=adventure.shop||{};stock.append(make('h2','shop-section-heading','遗物 · 为下一场战斗做准备'));
 const items=make('div','shop-stock');
 for(const item of shop.items||[]){const card=stockCard(item,'✧'),buy=button(item.soldOut||item.purchased?'已售出':'购买遗物',()=>command('buy',{itemId:item.id}),'primary');buy.dataset.itemId=item.id;buy.disabled=busy||uncertain||item.canBuy!==true;card.append(buy);items.append(card);}
 if(!shop.items?.length)items.append(make('div','adventure-empty','本店暂时没有可购买的遗物。'));
 stock.append(items,make('h2','shop-section-heading','牌组整理 · 服务价格以当前标价为准'));
 const services=make('div','shop-stock');for(const service of shop.services||[]){const card=stockCard(service,/删除/.test(service.name)?'−':/复制/.test(service.name)?'⊕':'♧'),use=button('选择卡牌',()=>openService(service));use.dataset.serviceId=service.id;use.disabled=busy||uncertain||service.canBuy!==true;card.append(use);services.append(card);}
 if(!shop.services?.length)services.append(make('div','adventure-empty','本店暂时没有改牌服务。'));
 stock.append(services);
 if(adventure.relics?.some(r=>r.sellPrice!=null)){stock.append(button('出售持有遗物',()=>showRelics(true),'','adventure-sell-relic'));}
 layout.append(stock);view.append(layout);
}
function stockCard(item,glyph){const card=make('article','stock-card');card.append(make('span','stock-glyph',glyph),make('h3','',item.name),make('p','',item.description||''),make('div','stock-price','◈ '+(item.price??'—')+' 金币'));return card;}
function openService(service){
 showDialog(service.name,body=>{
  body.append(make('p','adventure-service-hint',(service.description||'选择要处理的卡牌。')+'　当前费用：'+service.price+' 金币'));
  let selected='',suit='Spades';const grid=make('div','adventure-deck-grid');
  const apply=button('确认'+service.name,async()=>{if(!selected)return;const result=await command('service',{itemId:service.id,cardId:selected,...(service.requiresSuit?{suit}:{})});if(result?.success)$('adventure-dialog').close();},'primary','adventure-service-confirm');apply.disabled=true;
  for(const card of sortedDeck()){const tile=playingCard(card,true);tile.addEventListener('click',()=>{selected=card.id;for(const b of grid.querySelectorAll('button'))b.setAttribute('aria-pressed',String(b.dataset.cardId===selected));apply.disabled=false;});grid.append(tile);}
  body.append(grid);
  const controls=make('div','service-controls');if(service.requiresSuit){const picker=make('select');picker.setAttribute('aria-label','目标花色');for(const key of ['Spades','Hearts','Clubs','Diamonds'])picker.append(new Option({Spades:'黑桃 ♠',Hearts:'红桃 ♥',Clubs:'梅花 ♣',Diamonds:'方块 ♦'}[key],key));picker.addEventListener('change',()=>{suit=picker.value;});controls.append(picker);}controls.append(apply);body.append(controls);
 });
}
function renderRest(){
 heading('在篝火旁，歇一会儿。','选择一项休整，再继续前进。','营地');
 const scene=make('section','rest-scene'),fire=make('div','camp-fire');fire.setAttribute('aria-hidden','true');
 // A tiny inline vector avoids another image asset.
 fire.innerHTML='<svg viewBox="0 0 140 160" fill="none"><path d="M27 139L111 151M32 151L114 138" stroke="#876c47" stroke-width="9" stroke-linecap="round"/><path d="M70 4C84 43 122 53 118 96C115 135 84 140 69 140C43 140 21 125 22 99C24 77 38 66 40 48C52 54 57 66 56 78C72 59 76 35 70 4Z" fill="#d99d51"/><path d="M70 70C75 89 100 100 95 118C91 132 79 138 68 138C49 138 40 123 47 111C55 98 61 88 70 70Z" fill="#efcc84"/></svg>';
 scene.append(fire);
 const options=make('div','rest-options'),rest=adventure.rest||{};
 for(const [i,option]of (rest.options||[]).entries()){const chosen=option.selected||rest.selectedOptionId===option.id,item=button('',()=>confirmRest(option),'rest-option'+(chosen?' chosen':''));item.dataset.optionId=option.id;item.disabled=busy||uncertain||option.enabled!==true;item.append(make('span','stock-glyph',['♥','◈','✧'][i%3]),make('strong','',option.name),make('small','',option.description));if(chosen)item.append(make('span','rest-selected','✓ 已完成'));options.append(item);}
 scene.append(options);
 const chosen=(rest.options||[]).find(option=>option.selected||rest.selectedOptionId===option.id);
 if(rest.message||adventure.message||chosen)scene.append(make('p','rest-message',rest.message||adventure.message||(chosen.name+'已完成，可以继续旅途。')));
 const next=button('继续旅途　→',()=>command('continue'),'primary rest-continue','adventure-rest-continue');next.disabled=busy||uncertain||adventure.canContinue!==true;scene.append(next);view.append(scene);
}
function confirmRest(option){
 showDialog(option.name,body=>{
  body.append(make('p','',option.description||'确认选择这项休整。'));const row=make('div','adventure-actions');row.append(button('再想想',()=>$('adventure-dialog').close()),button('确认休整',async()=>{const result=await command('rest',{optionId:option.id});if(result?.success)$('adventure-dialog').close();},'primary','adventure-rest-confirm'));body.append(row);
 });
}
function renderReward(){
 const result=make('section','adventure-result');result.append(make('div','result-glyph','✧'),make('div','adventure-eyebrow','战斗结算'),make('h1','','这一战，值得。'),make('p','',adventure.reward?.description||adventure.message||'战斗奖励已结算，血量与牌组将带入下一站。'));
 const resources=make('div','result-resources');for(const [label,value]of [['获得金币',adventure.reward?.gold],['当前血量',adventure.player?.health],['持有金币',adventure.player?.gold]]){if(value==null)continue;const item=make('div');item.append(make('strong','',value),make('small','',label));resources.append(item);}result.append(resources);
 const row=make('div','adventure-actions'),next=button('返回地图　→',()=>command('continue'),'primary','adventure-reward-continue');next.disabled=uncertain;row.append(next);result.append(row);view.append(result);
}
function renderEnding(){
 const victory=['Victory','Completed'].includes(phase()),result=make('section','adventure-result');
 result.append(make('div','result-glyph',victory?'♛':'♠'),make('div','adventure-eyebrow',victory?'冒险完成':'旅途结束'),make('h1','',victory?'下一次，再创传奇。':'带着经验，再走一程。'),make('p','',adventure.message||(victory?'你已完成所有章节。已获得的成长与解锁会保存在冒险存档中。':'这段旅途已经结束。你可以导出记录，或选择新的起始天赋再次出发。')));
 const resources=make('div','result-resources');for(const [label,value]of [['抵达章节',adventure.chapter],['持有金币',adventure.player?.gold],['遗物数量',adventure.relics?.length]]){const item=make('div');item.append(make('strong','',value??0),make('small','',label));resources.append(item);}result.append(resources);
 const row=make('div','adventure-actions');row.append(button('新的冒险',beginSetup,'primary','adventure-new-after'),button('导出存档',exportSave),button('返回主菜单',goHome));result.append(row);view.append(result);
}
function renderBattle(){
 const h=heading('将命运，握在手中。','战后血量与构筑会带入下一站。','旅途战斗');journeyActions(h);
 battleRoot=make('section','adventure-battle-shell');battleRoot.id='adventure-battle';battleRoot.inert=uncertain;view.append(battleRoot);
 battleView=createPlaytest(battleRoot,{
  getConfig:()=>({xml:'',sourceDirty:false,importing:false,encounters:[]}),
  getToken:()=>token,
  external:{
   request:async(op,payload={})=>{
    if(sessionMissing&&op!=='state')throw new Error('请先恢复已保存的旅途，再继续战斗。');
    battleRequests++;
    try{return await request(op==='preview'?'preview':op==='state'?'state':'battle',{...payload,runId:adventure.runId,revision:adventure.revision,battleRevision:payload.revision,battleOp:op});}
    finally{battleRequests--;}
   },
   onResponse:result=>{
    accept(result,{renderNow:false});
    if(sessionMissing||result.adventure&&phase()!=='Battle')queueMicrotask(()=>render());
   },
   onExit:()=>{screen='journey';render();}
  }
 });
 if(adventure.battle){battleView.setExternalBattle?.(adventure.battle);battleIdentity=adventure.battle.runId+':'+adventure.battle.revision;}
 else battleRoot.append(make('p','adventure-empty','正在恢复战斗…'));
}
function showDialog(title,build){$('adventure-dialog-title').textContent=title;const body=$('adventure-dialog-body');body.replaceChildren();build(body);if(!$('adventure-dialog').open)$('adventure-dialog').showModal();}
function faceOf(card){
 const raw=String(card.face||''),rank=card.rank||raw.split(/[_ ]/)[0],suit=card.suit||raw.split(/[_ ]/)[1];
 return {rank:ranks[rank]||rank||'?',suit:suits[suit]||suit||'',red:['Hearts','Diamonds','Heart','Diamond','♥','♦'].includes(suit),sortSuit:suitOrder[suit]??9,sortRank:rankOrder[rank]??(Number(rank)||0)};
}
function sortedDeck(order='suit'){
 return [...(adventure?.deck||[])].sort((a,b)=>{const aa=faceOf(a),bb=faceOf(b);return order==='rank'?(aa.sortRank-bb.sortRank||aa.sortSuit-bb.sortSuit):(aa.sortSuit-bb.sortSuit||aa.sortRank-bb.sortRank);});
}
function playingCard(card,selectable=false){
 const face=faceOf(card),tile=make(selectable?'button':'div','adventure-playing-card'+(face.red?' red':''));if(selectable){tile.type='button';tile.setAttribute('aria-pressed','false');}
 tile.dataset.cardId=card.id;tile.setAttribute('aria-label',card.name||face.rank+face.suit);tile.append(make('span','card-corner',face.rank+face.suit),make('span','card-center',face.suit||face.rank));
 if(card.name&&card.name!==face.rank+face.suit)tile.append(make('small','',card.name));return tile;
}
function showDeck(){
 if(!adventure)return;showDialog('我的牌库 · '+(adventure.deck?.length??0)+' 张',body=>{
  const tools=make('div','deck-tools');tools.append(make('span','','排列方式'));const order=make('select');order.setAttribute('aria-label','牌库排列方式');order.append(new Option('按花色排列','suit'),new Option('按点数排列','rank'));tools.append(order);const grid=make('div','adventure-deck-grid');
  const draw=()=>grid.replaceChildren(...sortedDeck(order.value).map(card=>playingCard(card)));order.addEventListener('change',draw);draw();body.append(tools,grid);
 });
}
function showRelics(selling=false){
 showDialog(selling?'出售遗物':'持有遗物',body=>{
  if(!adventure?.relics?.length){body.append(make('div','adventure-empty','还没有遗物。沿途商店和休整可能带来新的收获。'));return;}
  const grid=make('div','shop-stock');for(const relic of adventure.relics){const card=make('article','stock-card');card.append(make('span','stock-glyph','✧'),make('h3','',relic.name),make('p','',relic.description||''));if(selling&&relic.sellPrice!=null){const sell=button('出售 · '+relic.sellPrice+' 金币',async()=>{const result=await command('sell',{itemId:relic.id});if(result?.success)showRelics(true);});sell.dataset.relicId=relic.id;card.append(sell);}grid.append(card);}body.append(grid);
 });
}
$('adventure-menu').addEventListener('click',goHome);
$('adventure-tarots').addEventListener('click',openTarots);
$('adventure-deck').addEventListener('click',showDeck);
$('adventure-dialog-close').addEventListener('click',()=>$('adventure-dialog').close());
window.addEventListener('pagehide',event=>{
 if(event.persisted||busy||battleRequests||uncertain||lastSaveError||!saved?.save||!adventure)return;
 // A successful save can reconstruct this isolated session after a reload.
 // Never close a session while a command could still be committing.
 fetch('/api/adventure',{method:'POST',keepalive:true,headers:{'Content-Type':'application/json','X-Editor-Token':token},body:JSON.stringify({op:'close',sessionId,runId:adventure.runId,revision:adventure.revision})}).catch(()=>{});
});
$('adventure-config-file').addEventListener('change',async event=>{
 const file=event.target.files[0];if(!file)return;
 try{if(file.size>8*1024*1024)throw new Error('配置文件过大，请使用小于 8 MB 的 XML。');const text=await file.text();const doc=new DOMParser().parseFromString(text,'application/xml');if(doc.querySelector('parsererror'))throw new Error('XML 格式无法读取，原配置已保留。');configXml=text;try{localStorage.setItem(CONFIG_KEY,configXml);}catch{}render();$('adventure-config').open=true;notify('已载入配置，下次开局时由规则层校验。');}catch(error){notify(error.message,true);}finally{event.target.value='';}
});
$('adventure-save-file').addEventListener('change',async event=>{
 const file=event.target.files[0];if(!file)return;
 try{
  if(file.size>16*1024*1024)throw new Error('存档文件过大，无法恢复。');
  const text=await file.text();let value;try{value=JSON.parse(text);}catch{throw new Error('存档格式无法读取，原存档已保留。');}
  const save=typeof value==='string'?value:value?.save;if(typeof save!=='string')throw new Error('没有找到完整冒险存档，原存档已保留。');
  const restore=()=>operation('restore',{save,...(adventure?.runId?{runId:adventure.runId}:{})},()=>{screen='journey';notify('存档已恢复。');});
  if(active())showDialog('恢复另一段旅途',body=>{body.append(make('p','','恢复成功后会替换当前旅途。恢复失败时，当前存档仍会保留。'));const row=make('div','adventure-actions');row.append(button('先导出当前存档',exportSave),button('确认恢复',async()=>{$('adventure-dialog').close();await restore();},'primary'));body.append(row);});else await restore();
 }catch(error){notify(error.message,true);}finally{event.target.value='';}
});
async function init(){
 saved=readSaved();restoreBlocked=!!saved?.invalid;
 try{
  const response=await fetch('/api/bootstrap');if(!response.ok)throw new Error('无法连接本机配置服务。');
  const bootstrap=await response.json();token=bootstrap.token;
  if(saved?.save){
   const restored=await request('restore',{save:saved.save});
   if(restored.success)accept(restored,{renderNow:false});
   else{restoreBlocked=true;notify((restored.message||'无法恢复本机存档。')+' 原存档已保留。',true);}
  }
  await refreshCatalog();render();
 }catch(error){notify(error.message+' 请确认新版服务仍在运行。',true);view.replaceChildren(make('div','adventure-empty','暂时无法载入冒险。'),button('重新连接',init,'primary'));}
}
init();
