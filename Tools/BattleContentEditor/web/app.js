import {createPlaytest} from './playtest.js';
import {sections,descriptor,exclusiveChildren,initialize,meta,tr,help} from './schema.js';
import {parse,serialize,pathOf,atPath,definitions,owner,references,uniqueId,addDefinition,addChild,cloneDefinition,fieldIssues} from './model.js';
const $=id=>document.getElementById(id);
const el=(tag,cls,text)=>{const node=document.createElement(tag);if(cls)node.className=cls;if(text!=null)node.textContent=text;return node;};
const btn=(text,fn,cls='')=>{const b=el('button',cls,text);b.type='button';b.addEventListener('click',()=>Promise.resolve().then(fn).catch(error=>notify(error.message)));return b;};
let doc,token,section='Encounters',selected=null,revision=0,report=null,reportRevision=-1,checking=false;
let importing=false,playtestMode=false,playtest;
let timer,noticeTimer,saveTimer,xmlMode=false,sourceDirty=false,lastExport='',draftBlocked=false,loadGeneration=0;
let undo=[],redo=[],expanded=new Set(),stepBrowser='',validationSequence=0;
const DRAFT_KEY='battle-content-editor.draft.v1';
const uiKey=(node,key)=>pathOf(node)+'/@'+key;
function notify(message){$('notice').textContent=message;$('notice').classList.add('visible');clearTimeout(noticeTimer);noticeTimer=setTimeout(()=>$('notice').classList.remove('visible'),6500);}
function label(node){return node?.getAttribute('name')||node?.getAttribute('id')||node?.getAttribute('instanceId')||tr(node?.tagName||'');}
function selectNode(node){selected=owner(node);if(selected===doc.documentElement)section=null;else section=selected.parentElement.tagName;xmlMode=false;playtestMode=false;render();}
function snapshot(){return serialize(doc);}
function dirty(){revision++;reportRevision=-1;clearTimeout(timer);timer=setTimeout(()=>validate().catch(error=>notify(error.message)),850);clearTimeout(saveTimer);saveTimer=setTimeout(()=>saveDraft(false),450);}
function mutate(fn){const before=snapshot(),selectedPath=selected?pathOf(selected):null;try{fn();if(before===snapshot())return;undo.push({xml:before,path:selectedPath,section});if(undo.length>40)undo.shift();redo=[];dirty();render();}catch(error){doc=parse(before);selected=selectedPath?atPath(doc,selectedPath):null;render();throw error;}}
function navigateHistory(from,to){if(!from.length)return;to.push({xml:snapshot(),path:selected?pathOf(selected):null,section});const state=from.pop();doc=parse(state.xml);section=state.section;selected=state.path?atPath(doc,state.path):null;dirty();render();}
function saveDraft(explicit){if(draftBlocked){if(explicit)notify('请先恢复或放弃检测到的旧草稿，避免覆盖。');return;}try{localStorage.setItem(DRAFT_KEY,JSON.stringify({schema:1,xml:snapshot(),pendingXml:sourceDirty?$('xml-source').value:null,savedAt:new Date().toISOString()}));$('save-state').textContent='草稿已保存 · '+new Date().toLocaleTimeString('zh-CN',{hour:'2-digit',minute:'2-digit'});if(explicit)notify('草稿已保存到本浏览器。需要跨机器使用时请下载草稿备份。');}catch{notify('浏览器无法保存草稿，请下载草稿备份，避免丢失修改。');$('save-state').textContent='草稿保存失败';}}
function download(text,name,type){const url=URL.createObjectURL(new Blob([text],{type}));const a=el('a');a.href=url;a.download=name;document.body.append(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);}
function safeName(){return (doc.documentElement.getAttribute('id')||'battle-content').replace(/[^\p{L}\p{N}_.-]/gu,'_');}
async function api(xml){const res=await fetch('/api/validate',{method:'POST',headers:{'Content-Type':'application/xml','X-Editor-Token':token},body:xml});const data=await res.json();if(!res.ok)throw new Error(data.error||'本机校验服务不可用');return data;}
async function validate(forExport=false){
 if(!doc)return false;clearTimeout(timer);if(forExport&&sourceDirty){notify('源码区有尚未应用的修改，请先应用修改或恢复当前内容。');return false;}const request=++validationSequence,version=revision,xml=snapshot();checking=true;renderIssues();
 try{const result=await api(xml);if(version!==revision||request!==validationSequence){if(forExport)notify('校验期间内容发生了修改，请再次导出。');return false;}
 report=result;reportRevision=version;$('connection').textContent='● C# 校验器已连接';
 if(forExport){if(!result.success){notify('存在规则错误，请修复后导出；草稿仍可保存。');$('issues').scrollIntoView({behavior:'smooth',block:'center'});}else{download(xml,safeName()+'.xml','application/xml');lastExport=xml;notify('已通过 C# 校验并导出 XML'+(result.diagnostics.length?'（含警告，请检查）':'')+'。');}}
 return result.success;
 }catch(error){if(version===revision&&request===validationSequence){report=null;reportRevision=-1;$('connection').textContent='● 校验服务未连接';}throw error;}finally{if(request===validationSequence){checking=false;renderIssues();renderSummary();}}
}
function renderSummary(){if(!doc)return;playtest?.updateConfig();for(const id of ['import','example','new-pack','export'])$(id).disabled=importing;const count=sections.reduce((sum,s)=>sum+definitions(doc,s[0]).length,0);$('pack-summary').textContent=count+' 项配置 · '+(snapshot()===lastExport?'已导出':'有未导出内容');$('undo').disabled=!undo.length;$('redo').disabled=!redo.length;}
function renderNavigation(){
 const nav=$('navigation');nav.replaceChildren();const query=$('search').value.trim().toLowerCase();let group;
 for(const s of sections){if(s[3]!==group){nav.append(el('div','nav-group',s[3]));group=s[3];}
 const entries=definitions(doc,s[0]),matches=entries.filter(n=>(label(n)+' '+n.getAttribute('id')).toLowerCase().includes(query));
 const b=btn('',()=>{section=s[0];selected=null;xmlMode=false;playtestMode=false;render();},'section-button'+(section===s[0]?' active':''));b.append(el('span','',s[2]),el('span','count',entries.length));nav.append(b);
 if(section===s[0]||query)for(const n of matches){const link=btn(label(n),()=>selectNode(n),'definition-button'+(selected===n?' active':''));link.title=n.getAttribute('id');nav.append(link);}
 }
}
function render(){
 if(!doc)return;renderNavigation();renderSummary();renderContext();$('form-tab').classList.toggle('active',!xmlMode&&!playtestMode);$('xml-tab').classList.toggle('active',xmlMode&&!playtestMode);$('playtest-tab').classList.toggle('active',playtestMode);$('editor').hidden=xmlMode||playtestMode;$('xml-view').hidden=!xmlMode||playtestMode;$('playtest-view').hidden=!playtestMode;document.querySelector('.workspace').classList.toggle('playtest-mode',playtestMode);document.querySelector('.page-actions').hidden=playtestMode;document.querySelector('.diagnostics').hidden=playtestMode;
 const definition=selected&&selected!==doc.documentElement;
 $('duplicate').hidden=!definition;$('delete').hidden=!definition;
 $('breadcrumb').textContent=(doc.documentElement.getAttribute('name')||doc.documentElement.getAttribute('id'))+' / '+(section?tr(section):'内容包设置');
 $('title').textContent=selected?label(selected):tr(section);
 $('subtitle').textContent=selected?(selected===doc.documentElement?'设置内容包的编号和版本；可选项留空时使用默认设置':selected.getAttribute('id')):sections.find(s=>s[0]===section)?.[4];
 if(playtestMode){$('title').textContent='对局试玩';$('subtitle').textContent='使用当前内容包开局，选牌操作并查看规则结算。修改配置后重开生效。';playtest?.render();return;}
 if(xmlMode){if(!sourceDirty)$('xml-source').value=snapshot();renderIssues();return;}
 const editor=$('editor');editor.replaceChildren();
 if(selected)renderNode(selected,editor,0);else{
  editor.append(btn('+ 新建'+tr(section),()=>mutate(()=>{selected=addDefinition(doc,section);}), 'primary'));
  const items=definitions(doc,section);if(!items.length)editor.append(el('div','empty','还没有'+tr(section)+'。创建第一项，或导入已有 XML。'));
  for(const n of items){const card=el('div','list-card');const content=el('div');content.append(el('strong','',label(n)),el('p','',n.getAttribute('id')));card.append(content,btn('编辑 →',()=>selectNode(n)));editor.append(card);}
 }
 renderIssues();
}
function renderNode(node,host,depth){
 const spec=descriptor(node),path=pathOf(node);let body=host;
 if(depth>0){const details=el('details','node-card');details.dataset.path=path;details.open=expanded.has(path)||(depth===1&&node===node.parentElement.firstElementChild);details.addEventListener('toggle',()=>{if(details.open)expanded.add(path);else expanded.delete(path);});
 const summary=el('summary');summary.append(el('strong','',tr(node.tagName)+(node.hasAttribute('category')?' · '+tr(node.getAttribute('category'))+(node.hasAttribute('variant')?' / '+tr(node.getAttribute('variant')):''):'')),el('small','',node.getAttribute('id')||node.getAttribute('ref')||node.getAttribute('statusRef')||tr(node.getAttribute('kind'))||''));details.append(summary);body=el('div','node-body');details.append(body);host.append(details);}
 else {body.dataset.path=path;}
 if(node.tagName==='HandModifier'){
  const helper=el('div','step-help');helper.append(el('span','','先选一套牌型效果，查看可修改的效果编号'));const picker=el('select');picker.setAttribute('aria-label','浏览步骤来源');picker.append(new Option('选择牌型效果规则…',''));for(const rule of definitions(doc,'HandRules'))picker.append(new Option(label(rule)+' · '+rule.getAttribute('id'),rule.getAttribute('id')));picker.value=stepBrowser;picker.addEventListener('change',()=>{stepBrowser=picker.value;render();});helper.append(picker);body.append(helper);
 }
 if(help[node.tagName])body.append(el('p','muted',help[node.tagName]));
 const advancedKeys=new Set(['attackMultiplier','attackFlat','incomingMultiplier','incomingFlat','attackTarget','allowRequestedTargets','maxReactionEffects','maxReactionDepth','sourcePolicy','priority','chain','sellPrice','upgradeCost','growthPerScoredRound']);
 const grid=el('div','field-grid');for(const field of spec.fields.filter(f=>!advancedKeys.has(f.key)))renderField(node,field,grid);body.append(grid);
 const advanced=spec.fields.filter(f=>advancedKeys.has(f.key));if(advanced.length){const panel=el('details','advanced-settings');panel.append(el('summary','','高级参数 · '+advanced.length+' 项'));const inner=el('div','field-grid');for(const field of advanced)renderField(node,field,inner);panel.append(inner);body.append(panel);}
 const known=new Set(spec.fields.map(f=>f.key)),unknown=[...node.attributes].filter(a=>!known.has(a.name));
 if(unknown.length){const box=el('div','unknown');box.append(el('strong','','以下字段不适用于当前选项，或尚未识别；原值已保留。'));for(const a of unknown){const row=el('div','row');row.append(el('code','',a.name));const input=el('input');input.value=a.value;input.setAttribute('aria-label','保留字段 '+a.name);input.addEventListener('change',()=>mutate(()=>node.setAttribute(a.name,input.value)));row.append(input,btn('移除',()=>mutate(()=>node.removeAttribute(a.name))));box.append(row);}body.append(box);}
 if(node.tagName==='Scoring'&&node.getAttribute('preset')==='Default')body.append(el('p','muted','使用现有默认计分。将“计分预设”切换为自定义，可展开十类牌型数值。'));
 if(node.tagName==='Deck')body.append(btn('添加标准 52 张牌',()=>mutate(()=>{for(const card of meta.cards){const n=addChild(doc,node,'Card');n.setAttribute('face',card.id);}})));
 for(const child of node.children){if(node===doc.documentElement&&sections.some(s=>s[0]===child.tagName))continue;if(!spec.children.includes(child.tagName))body.append(el('div','unknown','保留的子项 '+child.tagName+' 不适用于当前选项，可检查后移除。'));renderNode(child,body,depth+1);}
 const choices=spec.children.filter(name=>!exclusiveChildren.has(name)||![...node.children].some(c=>c.tagName===name));
 if(choices.length){const row=el('div','add-row');const pick=el('select');pick.setAttribute('aria-label','添加子项类型 '+path);for(const name of choices)pick.append(new Option(tr(name),name));row.append(pick,btn('+ 添加',async()=>{
  if(node.tagName==='Enemy'&&pick.value==='Effects'&&node.hasAttribute('damage')&&!await confirmDialog('切换敌人行动方式','改用效果列表后，将移除固定 damage='+node.getAttribute('damage')+'。可以撤销此修改。'))return;
  mutate(()=>{if(node.tagName==='Enemy'&&pick.value==='Effects')node.removeAttribute('damage');const n=addChild(doc,node,pick.value);if(n.tagName==='Effects')addChild(doc,n,'Effect');expanded.add(pathOf(n));});
 }));body.append(row);}
 if(depth>0){const controls=el('div','node-controls');const prev=node.previousElementSibling,next=node.nextElementSibling;const up=btn('↑ 上移',()=>mutate(()=>node.parentElement.insertBefore(node,prev)));up.disabled=!prev;const down=btn('↓ 下移',()=>mutate(()=>node.parentElement.insertBefore(next,node)));down.disabled=!next;controls.append(up,down,btn('移除',async()=>{if(await confirmDialog('移除'+tr(node.tagName),'将移除此项及其子项，可通过撤销恢复。'+(stepUses(node).length?' 可能影响遗物条目：'+stepUses(node).join('、'):'')))mutate(()=>node.remove());},'danger'));body.append(controls);}
}
function stepUses(node) {
 const steps=node.tagName==='Effect'?[node]:[...node.querySelectorAll('Effect')];
 return definitions(doc,'Relics').flatMap(relic=>[...relic.children].filter(mod=>mod.tagName==='HandModifier'&&steps.some(step=>step.parentElement?.tagName==='Hand'&&step.parentElement.parentElement?.tagName==='HandRule'&&mod.getAttribute('category')===step.parentElement.getAttribute('category')&&mod.getAttribute('stepId')===step.getAttribute('id'))).map(mod=>label(relic)+' / '+mod.getAttribute('id')));
}
function renderField(node,field,grid){
 const box=el('label','field');box.dataset.field=uiKey(node,field.key);const title=el('div','field-label');title.append(el('span','',field.label+(field.required?' *':'')));
 if(node.hasAttribute(field.key)&&!field.required)title.append(btn('恢复默认',()=>mutate(()=>node.removeAttribute(field.key)),'reset'));
 box.append(title);const wrap=el('div','field-input');let input;const raw=node.getAttribute(field.key);
 const apply=async value=>{
  if(field.type==='ref'||['id','instanceId','stepId'].includes(field.key))value=value.trim();
  if(field.key==='id'&&node.tagName==='Effect'&&value!==raw){const affected=stepUses(node);if(affected.length&&!await confirmDialog('修改被遗物引用的步骤','以下条目可能受影响：'+affected.join('、')+'。只修改当前步骤，不自动修改其他牌型规则的效果编号。修改后请重新校验，并检查相关遗物。')){render();return;}}
  if(field.key==='id'&&node.parentElement?.parentElement===doc.documentElement&&raw&&value!==raw){const uses=references(doc,node.parentElement.tagName,raw);if(uses.length){if(!value.trim()){notify('被引用的 ID 不能为空。');render();return;}if(!await confirmDialog('修改配置编号','有 '+uses.length+' 处引用使用 '+raw+'。确认后同步更新为 '+value+'。')){render();return;}mutate(()=>{node.setAttribute(field.key,value);for(const use of uses)use.node.setAttribute(use.field.key,value);});return;}}
  mutate(()=>{if(value==='')node.removeAttribute(field.key);else node.setAttribute(field.key,value);
   if(field.key==='preset'&&value===''&&node.tagName==='Scoring'&&!node.querySelector('Hand'))for(const rule of meta.scoring){const hand=addChild(doc,node,'Hand');for(const [key,v]of Object.entries(rule))hand.setAttribute(key,v);}
  });
 };
 if(field.type==='select'||field.type==='card'){
  input=el('select');input.append(new Option(field.required?'请选择…':'默认 / '+tr(field.default??'不设置'),''));
  const values=field.type==='card'?meta.cards.map(card=>[card.id,tr(card.suit)+' '+tr(card.rank)]):(field.options||[]).map(value=>[value,tr(value)]);
  if(raw!=null&&!values.some(([value])=>value===raw))input.append(new Option('未识别：'+raw,raw));
  for(const [value,label]of values)input.append(new Option(label,value));input.value=raw??'';
 }else if(field.type==='multi'){
  const group=el('div','multi');const values=(raw||'').split(',').map(s=>s.trim());
  const options=[...new Set([...field.options,...values.filter(Boolean)])];
  for(const value of options){const label=el('label');const check=el('input');check.type='checkbox';check.value=value;check.checked=values.includes(value);check.addEventListener('change',()=>apply([...group.querySelectorAll('input:checked')].map(i=>i.value).join(',')));label.append(check,document.createTextNode(tr(value)));group.append(label);}box.append(group,el('small','field-key',field.key+' · 默认 '+(field.default||'无')));grid.append(box);return;
 }else{
  input=el('input');input.type='text';if(field.type==='number')input.inputMode='decimal';input.value=raw??'';input.placeholder=field.default!=null?'默认：'+tr(String(field.default)):field.required?'必填':'留空使用默认值 / 不设置';
  if(field.type==='ref'||field.type==='step'){
   const list=el('datalist');list.id='choices-'+Math.random().toString(36).slice(2);input.setAttribute('list',list.id);
   if(field.type==='ref')for(const n of definitions(doc,field.section))list.append(new Option(label(n)+' · '+n.getAttribute('id'),n.getAttribute('id')));
   else {const rule=definitions(doc,'HandRules').find(n=>n.getAttribute('id')===stepBrowser);const category=node.getAttribute('category');const steps=rule?[...rule.children].filter(n=>n.getAttribute('category')===category).flatMap(n=>[...n.children]):[];for(const id of new Set(steps.map(n=>n.getAttribute('id')).filter(Boolean)))list.append(new Option(id,id));}
   box.append(list);
  }
 }
 input.setAttribute('aria-label',field.label+' '+pathOf(node));input.dataset.key=field.key;
 input.addEventListener('change',()=>apply(input.value).catch(error=>notify(error.message)));wrap.append(input);
 if(field.type==='ref'){
  const target=definitions(doc,field.section).find(n=>n.getAttribute('id')?.trim()===raw?.trim());
  const view=btn('查看',()=>selectNode(target));view.disabled=!target;wrap.append(view,btn('选择',()=>chooseReference(node,field)),btn('+',()=>mutate(()=>{const n=addDefinition(doc,field.section);node.setAttribute(field.key,n.getAttribute('id'));selected=n;section=field.section;})));
 }
 box.append(wrap,el('small','field-key',field.key));const issue=fieldIssues(doc,node,field);if(issue){box.classList.add('invalid');box.append(el('span','error-text',issue));}grid.append(box);
}
async function chooseReference(node,field){
 const body=$('modal-body');body.replaceChildren(el('h2','','选择'+field.label));const search=el('input');search.placeholder='搜索名称或 ID';search.setAttribute('aria-label','搜索引用');const results=el('div');let value=node.getAttribute(field.key);
 const draw=()=>{results.replaceChildren();for(const n of definitions(doc,field.section).filter(n=>(label(n)+' '+n.getAttribute('id')).toLowerCase().includes(search.value.toLowerCase()))){const line=btn(label(n)+' · '+n.getAttribute('id'),()=>{value=n.getAttribute('id');draw();},'reference');if(value===n.getAttribute('id'))line.prepend(document.createTextNode('✓ '));results.append(line);}if(!results.children.length)results.append(el('p','muted','没有匹配项，可关闭后点击 + 新建。'));};search.addEventListener('input',draw);body.append(search,results);draw();if(await showDialog()){if(value)mutate(()=>node.setAttribute(field.key,value));}
}
function showDialog(){return new Promise(resolve=>{const dialog=$('modal');dialog.returnValue='';dialog.addEventListener('close',()=>resolve(dialog.returnValue==='confirm'),{once:true});dialog.showModal();});}
async function confirmDialog(title,message){$('modal-body').replaceChildren(el('h2','',title),el('p','',message));return showDialog();}
async function deleteDefinition(){
 const node=selected,uses=references(doc,section,node.getAttribute('id'));const body=$('modal-body');body.replaceChildren(el('h2','','删除 '+label(node)));
 let replacement;
 if(uses.length){body.append(el('p','',uses.length+' 处引用使用此配置。请先选择替代项，否则不能删除。'));replacement=el('select');replacement.setAttribute('aria-label','替代引用');replacement.append(new Option('选择替代项…',''));for(const n of definitions(doc,section).filter(n=>n!==node))replacement.append(new Option(label(n)+' · '+n.getAttribute('id'),n.getAttribute('id')));body.append(replacement);for(const use of uses)body.append(el('small','reference',label(owner(use.node))+' / '+use.field.label));}
 else body.append(el('p','','删除后可撤销恢复。'));
 if(await showDialog()){if(uses.length&&!replacement.value){notify('尚未选择替代项，配置未删除。');return;}mutate(()=>{for(const use of uses)use.node.setAttribute(use.field.key,replacement.value);node.remove();selected=null;});}
}
function renderContext(){
 const box=$('context');box.replaceChildren();if(!selected){box.append(el('p','muted','选择一项配置，查看它使用了哪些配置，以及在哪里被使用。'));return;}
 box.append(el('div','badge',selected===doc.documentElement?'内容包':tr(section)));
 const description=sections.find(s=>s[0]===section)?.[4];if(description)box.append(el('p','muted',description));
 const outgoing=[];for(const n of [selected,...selected.querySelectorAll('*')])for(const field of descriptor(n).fields)if(field.type==='ref'&&n.hasAttribute(field.key))outgoing.push({id:n.getAttribute(field.key),section:field.section});
 const block=el('div','context-block');block.append(el('h3','','使用的配置'));if(!outgoing.length)block.append(el('p','','未使用其他配置'));
 for(const use of outgoing){const target=definitions(doc,use.section).find(n=>n.getAttribute('id')?.trim()===use.id?.trim());block.append(btn(tr(use.section)+' / '+(target?label(target):use.id+'（缺失）'),()=>{if(target)selectNode(target);},'reference'));}box.append(block);
 const incoming=selected===doc.documentElement?[]:references(doc,section,selected.getAttribute('id'));const other=el('div','context-block');other.append(el('h3','','使用它的配置'));if(!incoming.length)other.append(el('p','','尚未被其他配置使用'));for(const use of incoming)other.append(btn(label(owner(use.node))+' / '+use.field.label,()=>selectNode(use.node),'reference'));box.append(other);
 const affected=stepUses(selected);if(affected.length){const list=el('div','context-block');list.append(el('h3','','匹配了这些牌型步骤的遗物'));for(const name of affected)list.append(el('p','',name));box.append(list);}
 box.append(el('p','muted','配置编号用于关联其他配置，显示名称可自由修改。修改编号时，工具会提示同步更新关联项。'));
}
const errorLabels={UnknownReference:'引用不存在',UnknownAttribute:'字段无法识别',UnknownElement:'子项无法识别',NotApplicable:'字段不适用于当前选项',InvalidValue:'配置值或结构不符合规则',InvalidCombination:'对局中的配置不能一起使用',InvalidAdditionalHits:'额外段数必须作用于伤害步骤',HandEffectLimit:'组合后的效果超过 64 步',UnmatchedHandStep:'效果编号没有匹配项',Overflow:'数值超出范围',DuplicateSection:'目录重复'};
function renderIssues(){
 const state=$('validation-state'),box=$('issues');box.replaceChildren();state.className='badge';
 if(checking){state.textContent='校验中…';return;}
 if(!report){state.textContent='尚未校验';box.append(el('p','muted','点击“校验内容”读取权威规则诊断。'));return;}
 if(reportRevision!==revision){state.textContent='内容已修改 · 待校验';box.append(el('p','muted','之前的结果已失效，等待新版本校验。'));return;}
 const errors=report.diagnostics.filter(d=>d.severity==='Error').length,warnings=report.diagnostics.length-errors;state.textContent=errors+' 错误 / '+warnings+' 警告';state.classList.add(errors?'bad':warnings?'warn':'good');
 if(!report.diagnostics.length){box.append(el('p','muted','✓ 校验通过，可以导出 XML。'));return;}
 for(const issue of report.diagnostics){const row=btn('',()=>locate(issue),'issue'+(issue.severity==='Warning'?' warning':''));row.append(el('strong','',(issue.severity==='Error'?'错误':'警告')+' · '+(errorLabels[issue.code]||'XML 格式 / 内容错误')+(issue.definitionId?' · '+issue.definitionId:'')),el('span','',issue.message),el('small','',issue.path+(issue.field?' / '+issue.field:'')+' · 第 '+issue.line+' 行，'+issue.column+' 列 · '+issue.code));box.append(row);}
}
function locate(issue){
 if(reportRevision!==revision)return;const node=atPath(doc,issue.path);
 if(!node||node===doc.documentElement){selected=doc.documentElement;section=null;}else{const top=owner(node);if(top===doc.documentElement){selected=top;section=null;}else if(top.parentElement===doc.documentElement){section=top.tagName;selected=null;}else{selected=top;section=top.parentElement.tagName;}}
 for(let n=node;n&&n!==doc.documentElement;n=n.parentElement)expanded.add(pathOf(n));xmlMode=false;playtestMode=false;render();
 const field=issue.field?document.querySelector('[data-field="'+CSS.escape(issue.path+'/@'+issue.field)+'"]'):null;
 const target=field||[...document.querySelectorAll('[data-path]')].find(n=>n.dataset.path===issue.path)||$('editor');for(let parent=target.parentElement;parent;parent=parent.parentElement)if(parent.tagName==='DETAILS')parent.open=true;target.classList.add('highlight');target.scrollIntoView({behavior:'smooth',block:'center'});setTimeout(()=>target.classList.remove('highlight'),3500);target.querySelector('input,select')?.focus({preventScroll:true});
}
async function importXml(xml,{confirm=true,pending=null}={}){
 const generation=++loadGeneration,startingRevision=revision;
 if(confirm&&doc&&!await confirmDialog('替换当前内容包','当前内容将被替换。可通过撤销恢复，建议先保存或下载草稿。'))return false;
 importing=true;renderSummary();notify('正在读取内容包并执行 C# 校验…');
 try {
 const result=await api(xml);if(generation!==loadGeneration)return false;if(revision!==startingRevision){notify('载入期间内容已修改，本次导入未覆盖新修改，请重试。');return false;}
 if(!result.readable){xmlMode=true;playtestMode=false;sourceDirty=true;$('xml-source').value=xml;render();notify('XML 无法读取，已保留在源码区；原内容包未被覆盖。');$('issues').replaceChildren(...result.diagnostics.map(d=>el('p','issue',d.message+' · 第 '+d.line+' 行，第 '+d.column+' 列')));saveDraft(false);return false;}
 const incoming=parse(xml);if(doc)undo.push({xml:snapshot(),path:selected?pathOf(selected):null,section});redo=[];doc=incoming;selected=null;section='Encounters';revision++;report=null;reportRevision=-1;sourceDirty=!!pending;xmlMode=!!pending;if(pending)$('xml-source').value=pending;dirty();render();saveDraft(false);
 // Revalidate formatted text so line/column locations correspond to the displayed/exported XML.
 clearTimeout(timer);await validate();notify(result.success?'内容包已载入。':'内容已载入为草稿，请修复下方规则问题。');return true;
 }finally{importing=false;renderSummary();}
}
function bind(){
 playtest=createPlaytest($('playtest-view'),{getConfig:()=>({xml:snapshot(),sourceDirty,importing,encounters:definitions(doc,'Encounters').map(node=>({id:node.getAttribute('id')||'',name:label(node)}))}),getToken:()=>token});
 $('playtest-tab').onclick=()=>{playtestMode=true;render();};
 $('search').addEventListener('input',renderNavigation);$('pack-settings').onclick=()=>{selected=doc.documentElement;section=null;xmlMode=false;playtestMode=false;render();};
 $('save').onclick=()=>saveDraft(true);$('validate').onclick=()=>validate().catch(e=>notify(e.message));$('export').onclick=()=>validate(true).catch(e=>notify(e.message));
 $('undo').onclick=()=>navigateHistory(undo,redo);$('redo').onclick=()=>navigateHistory(redo,undo);
 $('duplicate').onclick=()=>mutate(()=>{selected=cloneDefinition(doc,selected);});$('delete').onclick=()=>deleteDefinition().catch(e=>notify(e.message));
 $('import').onclick=()=>$('xml-file').click();$('xml-file').onchange=async e=>{try{const file=e.target.files[0];if(file){if(file.size>4*1024*1024)throw new Error('文件超过 4 MiB，请检查内容包。');await importXml(await file.text());}}catch(error){notify(error.message);}finally{e.target.value='';}};
 $('new-pack').onclick=()=>importXml('<BattleContent schemaVersion="1" id="new-content" version="1" name="新内容包"/>').catch(e=>notify(e.message));
 $('example').onclick=async()=>{try{await importXml(await(await fetch('/api/example')).text());}catch(e){notify(e.message);}};
 $('form-tab').onclick=()=>{xmlMode=false;playtestMode=false;render();};$('xml-tab').onclick=()=>{xmlMode=true;playtestMode=false;render();};
 $('xml-source').addEventListener('input',()=>{sourceDirty=true;playtest?.updateConfig();clearTimeout(saveTimer);saveTimer=setTimeout(()=>saveDraft(false),500);});
 $('apply-xml').onclick=()=>importXml($('xml-source').value,{confirm:false}).catch(e=>notify(e.message));
 $('reset-xml').onclick=async()=>{if(!sourceDirty||await confirmDialog('放弃源码区修改','恢复为当前表单内容，未应用的 XML 文本会被丢弃。')){sourceDirty=false;$('xml-source').value=snapshot();playtest?.updateConfig();saveDraft(false);}};
 $('raw-download').onclick=()=>download($('xml-source').value,safeName()+'.draft.xml','application/xml');
 $('backup').onclick=()=>download(JSON.stringify({schema:1,xml:snapshot(),pendingXml:sourceDirty?$('xml-source').value:null,savedAt:new Date().toISOString()},null,2),safeName()+'.draft.json','application/json');
 $('backup-file').onchange=async e=>{try{const file=e.target.files[0];if(file){if(file.size>12*1024*1024)throw new Error('草稿文件过大。');const data=JSON.parse(await file.text());if(data.schema!==1||typeof data.xml!=='string'||(data.pendingXml!=null&&typeof data.pendingXml!=='string'))throw new Error('不支持的草稿格式。');await importXml(data.xml,{pending:data.pendingXml});}}catch(error){notify(error.message);}finally{e.target.value='';}};
 $('show-all').onclick=()=>$('issues').scrollIntoView({behavior:'smooth',block:'center'});
 window.addEventListener('beforeunload',event=>{if(doc&&(sourceDirty||snapshot()!==lastExport)){event.preventDefault();event.returnValue='';}});
 document.addEventListener('keydown',e=>{if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='s'){e.preventDefault();saveDraft(true);}if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='z'&&!['INPUT','TEXTAREA'].includes(document.activeElement.tagName)){e.preventDefault();navigateHistory(e.shiftKey?redo:undo,e.shiftKey?undo:redo);}});
}
async function boot(){
 const response=await fetch('/api/bootstrap');if(!response.ok)throw new Error('无法连接本机配置服务');const config=await response.json();token=config.token;initialize(config.metadata);
 doc=parse('<BattleContent schemaVersion="1" id="new-content" version="1" name="新内容包"/>');bind();render();$('connection').textContent='● C# 校验器已连接';
 let saved;try{saved=localStorage.getItem(DRAFT_KEY);}catch{notify('浏览器存储不可用，请使用下载草稿备份。');}
 if(saved){draftBlocked=true;const banner=$('restore-banner');banner.hidden=false;banner.append(document.createTextNode('检测到本浏览器保存的草稿。恢复前不会自动覆盖。'),btn('恢复草稿',async()=>{try{const data=JSON.parse(saved);if(data.schema!==1||typeof data.xml!=='string')throw new Error('草稿格式无效，可下载备份检查。');const restored=await importXml(data.xml,{confirm:false,pending:data.pendingXml});if(restored){draftBlocked=false;banner.hidden=true;saveDraft(false);}}catch(error){notify(error.message);}}),btn('下载已有草稿',()=>download(saved,'recovered-draft.json','application/json')),btn('放弃旧草稿',async()=>{if(await confirmDialog('放弃旧草稿','建议先下载已有草稿。确认后使用当前内容替换浏览器中的草稿。')){draftBlocked=false;banner.hidden=true;saveDraft(false);}}));}
 await validate();
}
boot().catch(error=>{$('title').textContent='本地服务未就绪';$('subtitle').textContent=error.message;$('connection').textContent='● 未连接';notify('请先运行 Tools/BattleContentEditor/start.command，再使用终端给出的地址打开。');});
