import {sections,descriptor,meta} from './schema.js';
export function parse(xml) {
 const doc=new DOMParser().parseFromString(xml,'application/xml');
 if(doc.getElementsByTagNameNS('http://www.mozilla.org/newlayout/xml/parsererror.xml','parsererror').length||doc.doctype||doc.documentElement?.tagName!=='BattleContent') throw new Error('XML 结构无效，或根元素不是 BattleContent。原内容包未改变，请修复后重试。');
 return doc;
}
export function serialize(doc) {
 const copy=doc.cloneNode(true);
 function indent(node,depth) {
  if(!node.children.length) return;
  // Preserve unexpected text: the compiler, not this editor, decides whether it is legal.
  if([...node.childNodes].some(n=>n.nodeType===3 && n.textContent.trim())) return;
  for(const child of [...node.childNodes]) if(child.nodeType===3&&!child.textContent.trim())child.remove();
  for(const child of [...node.childNodes]) {
   if(child.nodeType===1)indent(child,depth+1);
   node.insertBefore(copy.createTextNode('\n'+'  '.repeat(depth+1)),child);
  }
  node.append(copy.createTextNode('\n'+'  '.repeat(depth)));
 }
 indent(copy.documentElement,0);
 return new XMLSerializer().serializeToString(copy)+'\n';
}
export function pathOf(node) {
 if(!node?.parentElement)return '/'+node.tagName;
 const siblings=[...node.parentElement.children].filter(n=>n.tagName===node.tagName);
 return pathOf(node.parentElement)+'/'+node.tagName+'['+(siblings.indexOf(node)+1)+']';
}
export function atPath(doc,path) {
 const parts=path.split('/').filter(Boolean); if(parts.shift()!==doc.documentElement.tagName)return null;
 let node=doc.documentElement;
 for(const part of parts) {const match=/^([^\[]+)(?:\[(\d+)\])?$/.exec(part);if(!match)return null;node=[...node.children].filter(n=>n.tagName===match[1])[Number(match[2]||1)-1];if(!node)return null;}
 return node;
}
export function definitions(doc,section) {
 return [...doc.documentElement.children].filter(n=>n.tagName===section).flatMap(n=>[...n.children]);
}
export function owner(node) {
 if(!node?.parentElement)return node;
 while(node.parentElement && node.parentElement.parentElement?.tagName!=='BattleContent')node=node.parentElement;
 return node;
}
export function references(doc,section,id) {
 const result=[];if(!id?.trim())return result;id=id.trim();
 for(const node of doc.getElementsByTagName('*'))for(const field of descriptor(node).fields)
  if(field.type==='ref'&&field.section===section&&node.getAttribute(field.key)?.trim()===id)result.push({node,field});
 return result;
}
export function uniqueId(doc,scope,base) {
 const used=new Set([...scope.getElementsByTagName('*'),scope].flatMap(n=>[n.getAttribute('id')?.trim(),n.getAttribute('instanceId')?.trim()]));
 let id=base,i=2;while(used.has(id))id=base+'-'+i++;return id;
}
export function addDefinition(doc,sectionName) {
 let section=[...doc.documentElement.children].find(n=>n.tagName===sectionName);
 if(!section) {section=doc.createElement(sectionName);doc.documentElement.append(section);}
 const item=sections.find(s=>s[0]===sectionName)[1];
 return addChild(doc,section,item);
}
export function addChild(doc,parent,name) {
 const node=doc.createElement(name);parent.append(node);
 const set=attrs=>Object.entries(attrs).forEach(([k,v])=>node.setAttribute(k,String(v)));
 const parentName=parent.tagName;
 const id=base=>uniqueId(doc,parent,base);
 if(sections.some(s=>s[0]===parentName))set({id:id(name.toLowerCase())});
 if(name==='Scoring')set({preset:'Default'});
 if(name==='HandRule')for(const category of meta.enums.HandCategory){const hand=addChild(doc,node,'Hand');hand.setAttribute('category',category);}
 if(name==='Hand') {
  const used=new Set([...parent.children].filter(n=>n!==node).map(n=>n.getAttribute('category')));
  const category=meta.enums.HandCategory.find(n=>!used.has(n))||'HighCard';set({category});
  if(parentName==='Scoring')set(meta.scoring.find(r=>r.category===category));else addChild(doc,node,'Effect');
 }
 if(name==='CardPoints')for(const rule of meta.rankPoints){const child=doc.createElement('Rank');for(const [k,v]of Object.entries(rule))child.setAttribute(k,v);node.append(child);}
 if(name==='Rank'||name==='Face')set({rank:'Ace',points:'11',...(name==='Face'?{suit:'Spades'}:{})});
 if(name==='Status'){set({tags:'Control',lifetime:'TriggerCount',count:'1'});addChild(doc,node,'BlockActionWindow');}
 if(name==='Effect') {
  set({kind:'Damage',amount:'10'});
  if(['Hand','Effects'].includes(parentName))set({id:id('damage')});
  if(['FactTrigger','BoundaryTrigger','ActionTrigger'].includes(parentName))set({target:'Self'});
  else if(parentName!=='HandModifier'||parent.getAttribute('operation')!=='ReplaceEffect')set({target:'Opponents'});
 }
 if(name==='PeriodicDamage')set({timing:'ActionCompleted',amount:'2'});
 if(['ReflectBlockedDamage','LifeSteal','OverhealDamage'].includes(name))set({ratio:'0.2'});
 if(name==='Numeric')set({id:id('numeric'),stat:'DamageDealt',operation:'AddFlat',value:'1'});
 if(name==='HandModifier')set({id:id('hand-modifier'),category:'HighCard',operation:'AddHits',stepId:'damage',hits:'1'});
 if(name==='RuleValue')set({id:id('rule-value'),key:'MaxEnergy',delta:'1'});
 if(name==='Refill')set({id:id('refill'),timing:'AfterPlay',delta:'1'});
 if(['FactTrigger','BoundaryTrigger','ActionTrigger'].includes(name)) {
  set({id:id('trigger'),timing:name==='FactTrigger'?'DamageSettled':name==='BoundaryTrigger'?'BattleStarted':'DiscardCompleted'});
  const effect=addChild(doc,node,'Effect');effect.setAttribute('kind','GainBlock');effect.setAttribute('amount','1');
 }
 if(name==='FactAtLeast')set({fact:'DamageToHealth',amount:'1'});
 if(name==='OwnerHealthAtMost')set({ratio:'0.5'});
 if(name==='HandIs')set({category:'HighCard'});
 if(name==='HandLevel') {
  const used=new Set([...parent.children].filter(n=>n!==node).map(n=>n.getAttribute('category')));
  set({category:meta.enums.HandCategory.find(n=>!used.has(n))||'HighCard',level:'1'});
 }
 if(name==='Card')set({id:id('card'),face:meta.cards[0].id});
 if(name==='Relic'&&parentName==='Loadout')set({instanceId:id('relic')});
 if(['Player','Enemy'].includes(name)&&parentName==='Encounter')set({id:uniqueId(doc,parent,name==='Player'?'player':'enemy')});
 // Intentionally leave references unselected. Choosing another definition must be an explicit authoring action.
 return node;
}
export function cloneDefinition(doc,node) {
 const clone=node.cloneNode(true);clone.setAttribute('id',uniqueId(doc,node.parentElement,(node.getAttribute('id')||node.tagName)+'-copy'));
 if(clone.hasAttribute('name'))clone.setAttribute('name',clone.getAttribute('name')+'（副本）');node.parentElement.append(clone);return clone;
}
export function fieldIssues(doc,node,field) {
 const raw=node.getAttribute(field.key);
 if(field.required && !raw?.trim())return '请填写'+field.label;
 if(raw==null)return '';
 if(field.type==='number'&&!/^[+-]?\d+(?:\.\d+)?$/.test(raw.trim()))return '请输入数字，小数点使用 .；整数约束由规则校验确认';
 if(field.type==='ref'&&!definitions(doc,field.section).some(n=>n.getAttribute('id')?.trim()===raw.trim()))return '引用不存在，请选择'+field.label;
 if(field.key==='id'&&node.parentElement&&[...node.parentElement.children].filter(n=>n.getAttribute('id')?.trim()===raw.trim()).length>1)return '同级标识重复';
 return '';
}
