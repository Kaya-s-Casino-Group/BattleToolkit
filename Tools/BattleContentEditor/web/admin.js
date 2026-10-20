// Runtime administrator editor. The C# session validates and applies every patch.
const el=(tag,cls,text)=>{const n=document.createElement(tag);if(cls)n.className=cls;if(text!=null)n.textContent=String(text);return n;};
const clone=value=>JSON.parse(JSON.stringify(value));
const zones={hand:'手牌',deck:'牌库',discard:'弃牌堆'};
const effects={Enhanced:'增强标记',Laminated:'覆膜标记',Ineffective:'无效牌',Unplayable:'废牌（不能打出）'};
const same=(a,b)=>JSON.stringify(a)===JSON.stringify(b);
const field=(name,control)=>{const label=el('label','admin-field');label.append(el('span','',name),control);return label;};
const button=(text,fn,cls='')=>{const b=el('button',cls,text);b.type='button';b.addEventListener('click',fn);return b;};
const select=(options,value,name,onChange)=>{const s=el('select');s.setAttribute('aria-label',name);for(const [v,t]of options)s.append(new Option(t,v));s.value=value;s.addEventListener('change',()=>onChange(s.value));return s;};
const cardData=c=>({...(c.instanceId?{instanceId:c.instanceId}:{}),definitionId:c.definitionId,effects:c.effects||'None',isDisabled:!!c.isDisabled});
const relicData=r=>({...(r.instanceId?{instanceId:r.instanceId}:{}),definitionId:r.definitionId,level:r.level,...(r.charges==null?{}:{charges:r.charges}),growth:String(r.growth),cooldownRounds:r.cooldownRounds,usesThisRound:r.usesThisRound});

export function createBattleAdmin(root,{getContext,isBusy=()=>false,request,onResponse=()=>{}}){
 let source=null,draft=null,baseContext=null,waiting=false,notice='',previewed=false;
 const panel=el('details','battle-admin');panel.dataset.admin='panel';
 panel.append(el('summary','','管理员修改 · 血量 / 金币 / 遗物 / 卡牌'));
 const intro=el('p','admin-intro','直接调整当前状态，不消耗行动点。卡牌改动仅本场；旅程的血量、金币和遗物会随存档保留。');
 const toolbar=el('div','admin-toolbar'),refresh=button('读取当前状态',load),preview=button('预览修改',()=>submit(true)),apply=button('应用修改',()=>submit(false),'primary');
 refresh.dataset.admin='refresh';preview.dataset.admin='preview';apply.dataset.admin='apply';toolbar.append(refresh,preview,apply);
 const message=el('p','admin-message');message.setAttribute('role','status');message.setAttribute('aria-live','polite');
 const changes=el('div','admin-changes'),body=el('fieldset','admin-body');
 body.append(el('p','muted','展开后读取当前状态。'));panel.append(intro,toolbar,message,changes,body);root.append(panel);
 panel.addEventListener('toggle',()=>{if(panel.open&&!source&&!waiting&&!isBusy())load();});
 function currentKey(){const c=getContext();return c?String(c.runId)+':'+String(c.revision):'';}
 function stale(){return !!baseContext&&currentKey()!==String(baseContext.runId)+':'+String(baseContext.revision);}
 function edited(){previewed=false;notice='';update();}
 function number(value,name,change,small=false){
  const n=el('input');n.type='text';n.inputMode='numeric';n.value=value??'';n.setAttribute('aria-label',name);n.maxLength=small?10:19;
  n.addEventListener('input',()=>{change(small&&/^\d+$/.test(n.value)?Number(n.value):n.value);edited();});return n;
 }
 function reset(data){
  source=clone(data.admin);draft=clone(source);baseContext={...getContext()};
  // Rules store the top card last. The editor always displays next draw first.
  for(const state of [source,draft])for(const p of state.players)for(const z of p.zones)if(z.kind==='deck')z.cards.reverse();
  previewed=false;drawBody();
 }
 async function load(){
  if(waiting||isBusy()||!getContext())return;
  waiting=true;notice='正在读取当前状态…';update();
  try{
   const data=await request('admin-state',{...getContext()});onResponse(data,'admin-state');
   if(data.success&&data.admin){reset(data);notice='已读取。编辑后可预览或直接应用。';}
   else notice=data.message||data.error||'读取失败。';
  }catch(error){notice=error.message+' 请同步后重新读取。';}
  finally{waiting=false;update();}
 }
 function patch(){
  if(!draft)return {};
  const result={vitals:[],gold:[],zones:[],relics:[]};
  for(const u of [...draft.players,...draft.enemies]){
   const before=[...source.players,...source.enemies].find(v=>v.id===u.id),v={unit:u.id};
   for(const key of ['health','maxHealth','block'])if(u[key]!=null&&String(u[key])!==String(before[key]))v[key]=String(u[key]);
   if(Object.keys(v).length>1)result.vitals.push(v);
  }
  for(const p of draft.players){
   const before=source.players.find(v=>v.id===p.id);
   if(String(p.gold)!==String(before.gold))result.gold.push({owner:p.id,amount:String(p.gold)});
   for(const z of p.zones){
    const cards=z.cards.map(cardData),old=before.zones.find(v=>v.kind===z.kind).cards.map(cardData);
    if(!same(cards,old))result.zones.push({owner:p.id,kind:z.kind,cards:z.kind==='deck'?[...cards].reverse():cards});
   }
   if(!same(p.relics.map(relicData),before.relics.map(relicData)))result.relics.push({owner:p.id,items:p.relics.map(relicData)});
  }
  for(const key of Object.keys(result))if(!result[key].length)delete result[key];
  return result;
 }
 async function submit(isPreview){
  if(waiting||isBusy()||stale()||!source?.editable)return;
  const value=patch();if(!Object.keys(value).length)return;
  waiting=true;notice=isPreview?'正在预览…':'正在应用…';update();
  try{
   const op=isPreview?'admin-preview':'admin',data=await request(op,{...baseContext,patch:value});onResponse(data,op);
   if(isPreview){
    previewed=!!data.preview?.legal;notice=previewed?'预览通过，当前状态尚未改变。':data.preview?.message||data.message||data.error||'修改未通过校验。';
   }else if(data.success&&data.admin){reset(data);notice='修改已应用，行动点保持不变。';}
   else notice=data.message||data.error||'修改未应用。';
  }catch(error){notice=error.message+' 请同步后重新读取，避免重复提交。';}
  finally{waiting=false;update();}
 }
 function drawBody(){
  body.replaceChildren();if(!draft)return;
  const vitals=el('section','admin-section');vitals.append(el('h3','','血量与金币'));
  for(const u of [...draft.players,...draft.enemies]){
   const row=el('div','admin-vitals');row.dataset.unit=u.id;row.append(el('strong','',u.name));
   for(const [key,label]of [['health','生命'],['maxHealth','最大生命'],['block','护盾'],['gold','金币']]){
    if(u[key]==null)continue;row.append(field(label,number(u[key],u.name+' · '+label,v=>u[key]=v)));
   }
   vitals.append(row);
  }
  body.append(vitals);
  for(const p of draft.players){drawRelics(p);drawZones(p);}
 }
 function drawRelics(p){
  const section=el('details','admin-section');section.dataset.admin='relics';section.dataset.owner=p.id;
  section.append(el('summary','',p.name+' · 遗物 '+p.relics.length+' 件（从上到下结算）'));
  const list=el('div','admin-relics');
  const redraw=()=>{
   list.replaceChildren();section.querySelector('summary').textContent=p.name+' · 遗物 '+p.relics.length+' 件（从上到下结算）';
   p.relics.forEach((r,index)=>{
    const row=el('div','admin-relic');row.dataset.index=String(index);
    const setDefinition=id=>{const c=draft.catalog.relics.find(v=>v.definitionId===id);Object.assign(r,{definitionId:id,name:c.name,level:1,charges:c.charges,growth:'0',cooldownRounds:0,usesThisRound:0});delete r.instanceId;redraw();edited();};
    row.append(select(draft.catalog.relics.map(c=>[c.definitionId,c.name+' · '+c.definitionId]),r.definitionId,'遗物 '+(index+1),setDefinition));
    for(const [key,name]of [['level','等级'],['charges','剩余次数'],['growth','成长值'],['cooldownRounds','冷却回合数'],['usesThisRound','本回合已用次数']]){
     if(key==='charges'&&r.charges==null){row.append(el('span','muted','无限次数'));continue;}
     row.append(field(name,number(r[key],'遗物 '+(index+1)+' · '+name,v=>r[key]=v,key!=='growth')));
    }
    const up=button('↑',()=>{[p.relics[index-1],p.relics[index]]=[p.relics[index],p.relics[index-1]];redraw();edited();});up.title='提前结算';up.disabled=index===0;
    const down=button('↓',()=>{[p.relics[index+1],p.relics[index]]=[p.relics[index],p.relics[index+1]];redraw();edited();});down.title='延后结算';down.disabled=index===p.relics.length-1;
    row.append(up,down,button('移除',()=>{p.relics.splice(index,1);redraw();edited();}));list.append(row);
   });
   if(!p.relics.length)list.append(el('p','muted','当前没有遗物。'));
  };
  const add=button('添加遗物',()=>{const c=draft.catalog.relics[0];if(!c)return;p.relics.push({definitionId:c.definitionId,name:c.name,level:1,charges:c.charges,growth:'0',cooldownRounds:0,usesThisRound:0});redraw();edited();});add.disabled=!draft.catalog.relics.length;add.dataset.admin='add-relic';
  section.append(list,add);body.append(section);redraw();
 }
 function drawZones(p){
  if(!p.zones.length)return;
  const section=el('section','admin-section admin-zones');section.append(el('h3','',p.name+' · 卡牌'),el('p','muted','牌库第 1 张最先摸到；可以修改牌面、复制、移除或移到其他区域。'));
  const containers=new Map();
  const redraw=()=>{
   for(const z of p.zones){
    const {heading,list}=containers.get(z.kind);heading.textContent=zones[z.kind]+' · '+z.cards.length+' 张'+(z.kind==='deck'?'（顶 → 底）':'');list.replaceChildren();
    z.cards.forEach((c,index)=>{
     const row=el('div','admin-card');row.dataset.index=String(index);row.append(el('span','admin-card-index',index+1));
     const prefix=zones[z.kind]+'第 '+(index+1)+' 张';
     row.append(select(draft.catalog.cards.map(v=>[v.definitionId,v.label]),c.definitionId,prefix+'牌面',id=>{c.definitionId=id;edited();}));
     const flags=el('details','admin-card-flags');flags.append(el('summary','',c.effects==='None'&&!c.isDisabled?'普通牌':c.effects.includes('Unplayable')?'废牌':c.isDisabled?'已禁用':'有标记'));
     for(const [key,name]of Object.entries(effects)){
      const input=el('input');input.type='checkbox';input.checked=(c.effects||'').split(/,\s*/).includes(key);
      input.addEventListener('change',()=>{const set=new Set((c.effects||'').split(/,\s*/).filter(v=>v&&v!=='None'));if(input.checked)set.add(key);else set.delete(key);c.effects=[...set].join(', ')||'None';flags.querySelector('summary').textContent=set.has('Unplayable')?'废牌':set.size?'有标记':'普通牌';edited();});flags.append(field(name,input));
     }
     const disabled=el('input');disabled.type='checkbox';disabled.checked=!!c.isDisabled;disabled.addEventListener('change',()=>{c.isDisabled=disabled.checked;edited();});flags.append(field('禁用这张牌',disabled));row.append(flags);
     const move=select([['','移动到…'],...p.zones.filter(v=>v!==z).map(v=>[v.kind,zones[v.kind]+(v.kind==='deck'?'牌顶':'末尾')])],'',prefix+'移动到',kind=>{if(!kind)return;const destination=p.zones.find(v=>v.kind===kind);z.cards.splice(index,1);if(kind==='deck')destination.cards.unshift(c);else destination.cards.push(c);redraw();edited();});row.append(move);
     const up=button('↑',()=>{[z.cards[index-1],z.cards[index]]=[z.cards[index],z.cards[index-1]];redraw();edited();});up.disabled=index===0;up.title='向前一张';
     const down=button('↓',()=>{[z.cards[index+1],z.cards[index]]=[z.cards[index],z.cards[index+1]];redraw();edited();});down.disabled=index===z.cards.length-1;down.title='向后一张';
     row.append(up,down,button('复制',()=>{const copy=clone(c);delete copy.instanceId;z.cards.splice(index+1,0,copy);redraw();edited();}),button('移除',()=>{z.cards.splice(index,1);redraw();edited();}));list.append(row);
    });
    if(!z.cards.length)list.append(el('p','muted','此区域为空。'));
   }
  };
  for(const z of p.zones){
   const area=el('details','admin-zone');area.dataset.zone=z.kind;const heading=el('summary'),list=el('div','admin-card-list');
   const add=button('添加卡牌',()=>{const c=draft.catalog.cards[0];if(!c)return;z.cards.push({definitionId:c.definitionId,effects:'None',isDisabled:false});redraw();edited();});add.dataset.admin='add-card';
   area.append(heading,list,add,button('清空'+zones[z.kind],()=>{z.cards=[];redraw();edited();}));containers.set(z.kind,{heading,list});section.append(area);
  }
  body.append(section);redraw();
 }
 function update(){
  const externalBusy=isBusy(),outdated=stale(),editable=!!source?.editable,value=patch(),dirty=Object.keys(value).length>0;
  refresh.disabled=waiting||externalBusy||!getContext();refresh.textContent=source?'重新读取当前状态':'读取当前状态';
  preview.disabled=apply.disabled=waiting||externalBusy||outdated||!editable||!dirty;
  body.disabled=waiting||externalBusy||outdated||!editable;
  message.textContent=outdated?'对局已经变化，草稿尚未应用。请重新读取当前状态后修改。':!editable&&source?'当前阶段无法修改；请在玩家可操作时，或地图、商店、休息点使用。':notice;
  message.classList.toggle('admin-stale',outdated);changes.replaceChildren();changes.hidden=!dirty;
  if(dirty){
   changes.append(el('strong','',previewed?'预览通过 · 待应用':'待应用修改'));
   for(const v of value.vitals||[]){const unit=[...source.players,...source.enemies].find(u=>u.id===v.unit);for(const [key,title]of [['health','生命'],['maxHealth','最大生命'],['block','护盾']])if(v[key]!=null)changes.append(el('div','',unit.name+' '+title+'：'+unit[key]+' → '+v[key]));}
   for(const g of value.gold||[]){const unit=source.players.find(u=>u.id===g.owner);changes.append(el('div','',unit.name+' 金币：'+unit.gold+' → '+g.amount));}
   for(const z of value.zones||[])changes.append(el('div','',(source.players.find(u=>u.id===z.owner)?.name||z.owner)+'的'+zones[z.kind]+'：'+z.cards.length+' 张（完整顺序按编辑列表）'));
   for(const r of value.relics||[])changes.append(el('div','',(source.players.find(u=>u.id===r.owner)?.name||r.owner)+'的遗物：'+r.items.map(i=>draft.catalog.relics.find(c=>c.definitionId===i.definitionId)?.name||i.definitionId).join('、')+(r.items.length?'':'清空')));
  }
 }
 update();
 return {update,load};
}
