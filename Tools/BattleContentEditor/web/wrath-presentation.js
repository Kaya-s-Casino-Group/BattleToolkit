// Presentation only: all battle clocks and damage values come from engine snapshots.
export const WRATH_ENTRY_WARNING='警告：每存活一只手，暴怒攻击力+5！！';
export const WRATH_ULTIMATE_WARNING='警告：Boss有五只手臂，将释放大招！！';
const node=(tag,cls,text)=>{const element=document.createElement(tag);element.className=cls;if(text!=null)element.textContent=text;return element;};

export function createWrathNotices(host){
 const seen=new Set(),notice=node('div','wrath-entry-warning');notice.setAttribute('role','alert');
 const dismiss=node('button','','知道了');dismiss.type='button';dismiss.addEventListener('click',()=>{notice.hidden=true;});
 notice.append(node('strong','',WRATH_ENTRY_WARNING),dismiss);
 const mechanism=node('details','wrath-mechanism');mechanism.append(node('summary','','暴怒机制'),node('p','',WRATH_ENTRY_WARNING),
  node('p','','普攻 → 普攻 → 防御 → 蓄力。打断任意一只手可取消正在预警的大招；断手按各自倒计时再生。'),
  node('p','','先选择手牌，再点击本体或存活手臂选敌，也可使用下方目标列表。群体攻击无需选敌。'));
 host.append(notice,mechanism);host.hidden=true;
 return {update(battle){
  host.hidden=!battle?.enemies.some(unit=>unit.wrath?.role==='Body');if(host.hidden)return;
  if(!seen.has(battle.runId)){seen.add(battle.runId);notice.hidden=false;mechanism.open=false;}
 }};
}

export function wrathIntentIcon(unit){
 if(unit.wrath?.role!=='Body')return null;
 return {strike:'sword','strike-2':'sword',guard:'shield',charge:'spark'}[unit.intentMeta?.actionId]||null;
}

export function appendWrathState(box,unit,battle,compact){
 const wrath=unit.wrath;if(!wrath)return;
 box.dataset.wrathRole=wrath.role;
 if(wrath.role==='Body'){
  box.classList.add('wrath-body');
  box.append(node('div','wrath-stats','攻击 D '+compact(wrath.attack)+' · 蓄力 '+wrath.charges+' 次'));
  if(!unit.alive||battle.finished)return;
  box.classList.toggle('wrath-warning-active',!!wrath.warningActive);
  if(wrath.warningActive){
   const warning=node('div','wrath-warning');warning.setAttribute('role','status');
   warning.append(node('strong','',WRATH_ULTIMATE_WARNING),node('span','','第 '+wrath.releaseRound+' 轮常规动作后释放'+
    (wrath.pendingUltimateBaseDamage!=null?' · 按当前状态预计基础伤害 '+compact(wrath.pendingUltimateBaseDamage):'')));box.append(warning);
  }else box.append(node('div','wrath-cooldown',(wrath.cooldownRemaining>0?'大招冷却 · '+wrath.cooldownRemaining+' 轮':'大招就绪 · 五手齐全且本体可行动时预警')+
   (wrath.ultimateBaseDamage!=null?' · 当前基础伤害 '+compact(wrath.ultimateBaseDamage):'')));
 }else{
  box.classList.add('wrath-hand');
  if(!unit.alive&&wrath.reviveRound!=null&&!battle.finished){
   box.append(node('div','wrath-regeneration','再生剩余 '+wrath.reviveRemaining+' 轮 · 第 '+wrath.reviveRound+' 轮敌方阶段复活'));
  }
 }
}

export function createWrathPlayback(){
 let runId=null;const seen=new Set();
 return {take(battle,steps){
  if(runId!==battle.runId){runId=battle.runId;seen.clear();}
  return (steps||[]).flatMap(step=>step.wrathPlayback||[]).filter(frame=>{
   if(seen.has(frame.id))return false;seen.add(frame.id);return true;
  });
 }};
}
