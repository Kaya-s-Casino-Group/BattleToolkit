// UI metadata only: these descriptions never decide whether a content pack is valid.
export let meta;
export const sections = [
  ['Encounters','Encounter','对局配置','战斗配置','选择参战玩家、敌人、初始构筑和战斗规则'],
  ['Players','Player','玩家模板','角色与构筑','设置生命、能量、出牌弃牌次数和补牌方式'],
  ['Enemies','Enemy','敌人模板','角色与构筑','设置敌人属性，以及按顺序执行的行动效果'],
  ['Loadouts','Loadout','初始构筑','角色与构筑','设置开局携带的牌组、牌型等级和遗物'],
  ['Decks','Deck','初始牌组','角色与构筑','设置每张牌的点数、花色和附加效果'],
  ['HandRules','HandRule','牌型效果','战斗能力','设置各牌型的效果顺序和每个效果的作用目标'],
  ['Statuses','Status','状态配置','战斗能力','设置状态有什么效果、持续多久、重复施加时如何处理'],
  ['Relics','Relic','遗物配置','战斗能力','设置遗物如何改变数值、牌型、战斗规则及触发额外效果'],
  ['BattleRules','Rules','公共战斗规则','基础规则','设置回合上限、护盾清除时机和连锁触发上限'],
  ['ScoringProfiles','Scoring','计分规则','基础规则','牌型分值、倍率及等级成长']
];
export const labels = {
 BattleContent:'内容包', Hand:'牌型', Effect:'效果步骤', Effects:'行动效果', CardPoints:'单牌计分', Rank:'点数分值', Face:'指定牌面分值', Card:'卡牌', HandLevel:'牌型等级',
 BlockActionWindow:'眩晕 · 阻止行动', PreventStatusApplication:'控制免疫 · 阻止施加', ReflectBlockedDamage:'盾反', LifeSteal:'吸血', OverhealDamage:'溢出治疗转伤害', PeriodicDamage:'流血 / 中毒',
 Numeric:'数值修正', HandModifier:'牌型效果修正', RuleValue:'战斗规则修正', Refill:'补牌修正', Budget:'共用次数与冷却', FactTrigger:'战斗事件触发', BoundaryTrigger:'开战 / 回合阶段触发', ActionTrigger:'出牌 / 弃牌完成触发', Conditions:'全部条件同时满足',
 FactAtLeast:'本次结算数值至少达到', OwnerHealthAtMost:'持有者生命比例不高于', OwnerHasStatus:'持有者具有状态', EventStatus:'本次施加的状态', HandIs:'出牌牌型为', SubmittedCardsAtLeast:'出牌张数至少', DiscardedCardsAtLeast:'弃牌张数至少',
 HighCard:'高牌', OnePair:'一对', TwoPair:'两对', ThreeOfAKind:'三条', Straight:'顺子', Flush:'同花', FullHouse:'葫芦', FourOfAKind:'四条', StraightFlush:'同花顺', FiveOfAKind:'五条',
 Any:'任意 / 统一', Ordinary:'普通', Royal:'皇家', Damage:'伤害', Heal:'治疗', GainBlock:'获得护盾', ApplyStatus:'施加状态', Eliminate:'消灭',
 Constant:'固定值', ScoreTotalScale:'总分 × 系数', ScorePointsScale:'筹码 × 系数', Self:'自身', Opponents:'敌方', Allies:'友方', EventActor:'本次行动者', EventSource:'本次效果的施加者', EventTarget:'本次效果的承受者',
 FirstLiving:'首个存活', LowestHealth:'生命最低', HighestHealth:'生命最高', AllLiving:'全部存活（AOE）', Requested:'手动指定', ActorPolicy:'按角色的选敌设置',
 UntilRemoved:'直到移除', TriggerCount:'触发次数', ThroughNextPhase:'持续到指定阶段结束', ThroughCurrentRound:'持续到本轮结束', KeepLonger:'保留持续更久的状态', IndependentInstances:'各自独立生效',
 None:'无', Control:'控制', ActionCompleted:'行动完成', PhaseEnding:'阶段结束', RequireLiving:'来源必须存活', AllowDefeated:'允许来源已死亡',
 DamageDealt:'造成伤害', DamageReceived:'受到伤害', HealingDone:'造成的治疗', HealingReceived:'受到的治疗', BlockGranted:'提供的护盾', BlockReceived:'获得的护盾', AddFlat:'固定值相加', AddRate:'百分比系数相加',
 Primary:'主效果', Triggered:'触发效果', Periodic:'周期伤害', Reflection:'盾反', Lifesteal:'吸血', Overheal:'溢疗转伤',
 AppendEffect:'追加效果', AddHits:'增加段数', ReplaceEffect:'替换效果', ChangeTarget:'修改目标', MaxEnergy:'能量上限', InitialEnergy:'初始能量', PlayEnergyCost:'出牌费用', DiscardEnergyCost:'弃牌费用', HandLimit:'手牌上限',
 OpeningTurn:'开局发牌', AfterPlay:'出牌后', AfterDiscard:'弃牌后', PlayerTurnStart:'玩家回合开始', Skip:'跳过补牌', FillToLimit:'补满手牌', FixedCount:'固定张数',
 DamageSettled:'伤害结算', HealingSettled:'治疗结算', BlockGained:'获得护盾', UnitDied:'单位死亡', StatusApplied:'状态施加', StatusBlocked:'状态被阻止',
 OwnerIsSource:'遗物持有者施加效果', OwnerIsTarget:'遗物持有者承受效果', AllyIsSource:'友方施加效果', AllyIsTarget:'友方承受效果', OpponentIsSource:'敌方施加效果', OpponentIsTarget:'敌方承受效果',
 PerEvent:'每次符合条件的事件', PerAction:'每次行动', PerBoundary:'每次开战或阶段切换', DamageToHealth:'实际生命伤害', BlockAbsorbed:'护盾吸收量', HealthRestored:'实际治疗量',
 BattleStarted:'战斗开始', PhaseStarted:'阶段开始', OwnerTeam:'己方阶段', OpponentTeam:'敌方阶段', PlayCompleted:'出牌完成', DiscardCompleted:'弃牌完成',
 OwnerIsActor:'持有者行动', AllyIsActor:'友方行动', OpponentIsActor:'敌方行动', Retain:'保留护盾', AfterEnemyPhase:'敌方阶段后清除', NextPlayerTurn:'下个玩家回合', AfterAction:'行动后',
 Default:'现有默认计分', player:'玩家阶段', enemy:'敌人阶段', true:'是', false:'否', Enhanced:'强化', Laminated:'覆膜', Ineffective:'失效', Clubs:'梅花 ♣', Diamonds:'方块 ♦', Hearts:'红桃 ♥', Spades:'黑桃 ♠',
 Ace:'A', Jack:'J', Queen:'Q', King:'K', Two:'2', Three:'3', Four:'4', Five:'5', Six:'6', Seven:'7', Eight:'8', Nine:'9', Ten:'10'
};
for (const [section,item,label] of sections) { labels[section]=label; labels[item]=label; }
export const tr = value => labels[value] || value;
export function initialize(metadata) { meta=metadata; }
const f = (key,label,type='text',options={}) => ({key,label,type,...options});
const text=(k,l,required=false)=>f(k,l,'text',{required});
const num=(k,l,def,required=false)=>f(k,l,'number',{default:def,required});
const en=(k,l,enumName,def,required=false)=>f(k,l,'select',{options:Array.isArray(enumName)?enumName:meta.enums[enumName],default:def,required});
const ref=(k,l,section,required=false)=>f(k,l,'ref',{section,required});
const base=()=>[text('id','配置编号（ID）',true),text('name','显示名称')];
const version=()=>text('version','规则版本（留空使用内容包版本）');
const bool=(k,l,def=false)=>en(k,l,['true','false'],String(def));
const phase=()=>en('phase','结束阶段',['player','enemy'],null,true);
function lifetime(node,definition=false) {
 const fields=[en('lifetime','状态期限','StatusLifetimeKind',definition?'UntilRemoved':'继承状态配置')];
 const kind=node.getAttribute('lifetime');
 if(kind==='TriggerCount') fields.push(num('count','触发次数',1));
 if(kind==='ThroughNextPhase') fields.push(phase());
 return fields;
}
function target(node,automatic=false,groups=null,defaultGroup=null) {
 const group=node.getAttribute('target') || defaultGroup || (automatic?'Self':'Opponents');
 const fields=[en('target','作用对象',groups || ['Self','Opponents','Allies'],defaultGroup || (automatic?'Self':'Opponents'))];
 if(['Opponents','Allies'].includes(group)) {
  let choices=automatic?meta.enums.CombatTargetSelection:meta.enums.EffectTargetSelection;
  if(node.parentElement?.tagName==='Effects') choices=choices.filter(v=>v!=='Requested');
  fields.push(en('selection','选择方式',choices,'FirstLiving'));
  if(!automatic && ['Requested','ActorPolicy'].includes(node.getAttribute('selection'))) fields.push(text('requestSlot','手动选敌分组（默认 default）'));
 } else if(node.hasAttribute('selection')) {
  fields.push(en('selection','固定目标选择方式',[automatic?'FirstLiving':'AllLiving']));
 }
 return fields;
}
const actor=()=>[...base(),num('maxHealth','生命上限',100),num('initialBlock','初始护盾',0),
 num('attackMultiplier','攻击倍率',1),num('attackFlat','攻击固定修正',0),num('incomingMultiplier','承伤倍率',1),num('incomingFlat','承伤固定修正',0),
 en('attackTarget','角色攻击目标','OpponentTargetSelection','FirstLiving'),bool('allowRequestedTargets','接受手动目标',true)];
export function descriptor(node) {
 const name=node.tagName, parent=node.parentElement?.tagName, a=k=>node.getAttribute(k);
 let fields=[],children=[];
 if(name==='BattleContent') fields=[text('id','内容包 ID',true),text('name','内容包名称'),text('version','内容版本',true),en('schemaVersion','格式版本',['1'],'1',true)];
 else if(name==='Scoring') { fields=[...base(),en('preset','计分预设',['Default'],'自定义')]; if(a('preset')!=='Default'){fields.push(version());children=['Hand','CardPoints'];} }
 else if(name==='Hand' && parent==='Scoring') fields=[en('category','牌型','HandCategory',null,true),num('points','基础筹码',0,true),num('multiplier','基础倍率',1,true),num('pointsPerLevel','每级筹码增长',10),num('multiplierPerLevel','每级倍率增长',1)];
 else if(name==='CardPoints') children=['Rank','Face'];
 else if(name==='Rank'||name==='Face') fields=[en('rank','点数','Rank',null,true),...(name==='Face'?[en('suit','花色','Suit',null,true)]:[]),num('points','单牌筹码',0,true)];
 else if(name==='Rules') fields=[...base(),version(),ref('scoringRef','计分规则','ScoringProfiles',true),num('roundLimit','回合上限',20),en('blockExpiry','护盾清除方式','PlayerBlockExpiry','Retain'),num('maxReactionEffects','单次结算最多触发效果数',256),num('maxReactionDepth','连锁触发最多层数',16)];
 else if(name==='HandRule') { fields=[...base(),version()];children=['Hand']; }
 else if(name==='Hand') { fields=[en('category','牌型','HandCategory',null,true)];if(a('category')==='StraightFlush') fields.push(en('variant','同花顺分支','HandEffectVariant','Any'));children=['Effect']; }
 else if(name==='Effect') {
  const trigger=['FactTrigger','BoundaryTrigger','ActionTrigger'].includes(parent), modifier=parent==='HandModifier';
  if(!trigger&&!modifier) fields.push(text('id','效果编号',true));
  fields.push(en('kind','基础能力',['Damage','Heal','GainBlock','ApplyStatus','Eliminate'],null,true));
  if(['Damage','Heal','GainBlock'].includes(a('kind'))) fields.push(en('amountKind','数值来源',parent==='Hand'?meta.enums.EffectAmountKind:['Constant'],'Constant'),num('amount','数值 / 系数',undefined,true));
  if(a('kind')==='ApplyStatus') fields.push(ref('statusRef','状态配置','Statuses',true),num('stacks','施加层数',1),...lifetime(node));
  if(!(modifier && node.parentElement.getAttribute('operation')==='ReplaceEffect')) {
   const groups=parent==='ActionTrigger'?['Self','Opponents','Allies','EventActor']:parent==='FactTrigger'?['Self','Opponents','Allies','EventSource','EventTarget']:null;
   fields.push(...target(node,trigger,groups));
  }
 }
 else if(name==='Status') {fields=[...base(),en('tags','状态标签','StatusTags','None'),...lifetime(node,true),en('reapply','重复施加','StatusReapplyPolicy','KeepLonger')];children=['BlockActionWindow','PreventStatusApplication','ReflectBlockedDamage','LifeSteal','OverhealDamage','PeriodicDamage'];}
 else if(name==='PreventStatusApplication') fields=[en('tags','阻止的状态标签','StatusTags','Control')];
 else if(['ReflectBlockedDamage','LifeSteal','OverhealDamage','PeriodicDamage'].includes(name)) {
  fields=name==='PeriodicDamage'?[en('timing','触发时机（流血：行动完成；中毒：阶段结束）',['ActionCompleted','PhaseEnding'],null,true),num('amount','每次伤害',undefined,true)]:[num('ratio','比例（0.2 = 20%）',undefined,true)];
  fields.push(...target(node,true,name==='ReflectBlockedDamage'?['Self','Opponents','Allies','EventSource']:null,name==='ReflectBlockedDamage'?'EventSource':name==='OverhealDamage'?'Opponents':'Self'));
  if(name==='ReflectBlockedDamage') fields.push(en('sourcePolicy','来源存活要求','CombatReactionSourcePolicy','RequireLiving'));
 }
 else if(name==='Relic'&&parent==='Relics') {fields=[...base(),version(),num('charges','初始触发总次数（空为无限）'),num('maxCopies','同种遗物最多携带数量',1),num('maxLevel','最高等级',1),num('slots','占用遗物栏数量',1),num('sellPrice','售价',1),num('upgradeCost','升级费用',5),num('growthPerScoredRound','每计分轮成长',0)];children=['Numeric','HandModifier','RuleValue','Refill','FactTrigger','BoundaryTrigger','ActionTrigger','Budget'];}
 else if(name==='Budget') fields=[num('maxUsesPerAction','每次行动最多触发次数'),num('maxUsesPerRound','每轮最多触发次数'),num('maxUsesPerBoundary','每次开战或阶段切换最多触发次数'),num('chargeCost','每次触发消耗次数',1),num('cooldownRounds','触发后冷却轮数',0)];
 else if(name==='Numeric') fields=[text('id','效果编号（ID）',true),en('stat','修正数值','BattleNumericStat',null,true),en('operation','修正方式','RelicNumericOperation',null,true),num('value','修正值（比例 0.2 = 20%）',undefined,true),f('origins','适用效果来源','multi',{options:meta.enums.CombatEffectOrigin,default:'Primary'}),en('hand','限定牌型（可选）','HandCategory')];
 else if(name==='HandModifier') {
  fields=[text('id','效果编号（ID）',true),en('category','适用牌型','HandCategory',null,true),en('operation','效果调整方式','RelicHandEffectOperation',null,true)];
  if(a('operation')!=='AppendEffect') fields.push(f('stepId','要修改的效果编号','step',{required:true}));
  if(a('operation')==='AddHits') fields.push(num('hits','额外段数',1));
  if(a('operation')==='ChangeTarget') fields.push(...target(node));
  if(['AppendEffect','ReplaceEffect'].includes(a('operation'))) children=['Effect'];
 }
 else if(name==='RuleValue') fields=[text('id','效果编号（ID）',true),en('key','修正规则','BattleRuleValueKey',null,true),num('delta','整数增减',undefined,true)];
 else if(name==='Refill') fields=[text('id','效果编号（ID）',true),en('timing','补牌时机','CardDuelDealTrigger',null,true),en('mode','补牌方式','BattleRefillMode','沿用原规则'),...(a('mode')==='FixedCount'?[num('fixedCount','固定补牌张数',undefined,true)]:[]),num('delta','额外补牌张数',0)];
 else if(['FactTrigger','BoundaryTrigger','ActionTrigger'].includes(name)) {
  fields=[text('id','触发规则编号（ID）',true),en('timing','触发时机',name==='FactTrigger'?'RelicTriggerTiming':name==='BoundaryTrigger'?'RelicBoundaryTiming':'RelicActionTiming',null,true)];
  if(name==='FactTrigger') fields.push(en('relation','谁施加或承受本次效果','RelicEventRelation','OwnerIsSource'),en('occurrence','触发频率','RelicTriggerOccurrence','PerEvent'),f('origins','哪些效果可以触发','multi',{options:meta.enums.CombatEffectOrigin,default:'Primary'}));
  if(name==='BoundaryTrigger'&&a('timing')!=='BattleStarted') fields.push(en('phase','所属阶段','RelicPhaseRelation'));
  if(name==='ActionTrigger') fields.push(en('relation','行动者关系','RelicActionRelation','OwnerIsActor'));
  fields.push(num('priority','优先级',0),bool('chain','允许派生效果继续触发'),en('sourcePolicy','来源存活要求','CombatReactionSourcePolicy','RequireLiving'));children=['Conditions','Effect'];
 }
 else if(name==='Conditions') {
  const trigger=node.parentElement;
  children=['OwnerHealthAtMost','OwnerHasStatus'];
  if(trigger.tagName==='FactTrigger') children.push('FactAtLeast','EventStatus','HandIs','SubmittedCardsAtLeast','DiscardedCardsAtLeast');
  if(trigger.tagName==='ActionTrigger') children.push(...(trigger.getAttribute('timing')==='PlayCompleted'?['HandIs','SubmittedCardsAtLeast']:['DiscardedCardsAtLeast']));
 }
 else if(name==='FactAtLeast') fields=[en('fact','比较哪项结算数值','RelicFactAmount',null,true),num('amount','最低数值',undefined,true)];
 else if(name==='OwnerHealthAtMost') fields=[num('ratio','最高生命比例',undefined,true)];
 else if(['OwnerHasStatus','EventStatus'].includes(name)) fields=[ref('statusRef','状态配置','Statuses',true)];
 else if(name==='HandIs') fields=[en('category','牌型','HandCategory',null,true)];
 else if(['SubmittedCardsAtLeast','DiscardedCardsAtLeast'].includes(name)) fields=[num('count','最低张数',1)];
 else if(name==='Player'&&parent==='Players') fields=[...actor(),ref('handRuleRef','牌型效果规则','HandRules'),num('pointsPerDamage','旧版伤害换算除数（使用牌型效果时留空）'),num('maxEnergy','能量上限',3),num('initialEnergy','初始能量',3),num('playEnergyCost','出牌费用',1),num('discardEnergyCost','弃牌费用',1),num('handSize','手牌上限',8),num('discards','整场战斗可弃牌次数',3),bool('unlimitedDiscards','无限弃牌次数'),num('maxPlayCards','最多出牌张数',5),num('maxDiscardCards','最多弃牌张数',5),en('playRefill','出牌补牌时机','CardDuelRefillTiming','NextPlayerTurn'),en('discardRefill','弃牌补牌时机','CardDuelRefillTiming','AfterAction'),num('discardBlockPerCard','每张弃牌获得护盾（空为无收益）')];
 else if(name==='Enemy'&&parent==='Enemies') {fields=[...actor()];if(![...node.children].some(n=>n.tagName==='Effects'))fields.push(num('damage','固定攻击伤害',0));children=['Effects'];}
 else if(name==='Effects') {fields=[version(),text('explanation','效果说明')];children=['Effect'];}
 else if(name==='Deck') {fields=base();children=['Card'];}
 else if(name==='Card') fields=[text('id','卡牌来源 ID',true),f('face','牌面','card',{required:true}),f('effects','卡牌效果','multi',{options:meta.enums.CardEffects,default:'None'}),bool('disabled','牌已失效')];
 else if(name==='Loadout') {fields=[...base(),ref('deckRef','初始牌组（空为标准 52 张）','Decks')];children=['HandLevel','Relic'];}
 else if(name==='HandLevel') fields=[en('category','牌型','HandCategory',null,true),num('level','初始等级',undefined,true)];
 else if(name==='Relic') fields=[text('instanceId','这件遗物的编号（同一构筑内唯一）',true),ref('ref','遗物配置','Relics',true),num('level','等级',1),num('growth','成长',0),num('charges','初始剩余触发次数（留空使用遗物配置）'),num('cooldownRounds','初始冷却轮数',0)];
 else if(name==='Encounter') {fields=[...base(),ref('rulesRef','战斗规则','BattleRules',true)];children=['Player','Enemy'];}
 else if(name==='Player') fields=[text('id','参战角色编号（本场唯一）',true),ref('ref','玩家模板','Players',true),ref('loadoutRef','初始构筑','Loadouts'),num('initialHealth','开局生命值（留空为满血）')];
 else if(name==='Enemy') fields=[text('id','参战角色编号（本场唯一）',true),ref('ref','敌人模板','Enemies',true)];
 return {fields,children,label:tr(name)};
}
export const exclusiveChildren=new Set(['Budget','Conditions','CardPoints','Effects']);

// Explanations for concepts that need more than a short field label.
export const help = {
 Encounter: '将玩家、初始构筑、敌人和战斗规则组合成一场对局。同一个敌人模板可以多次加入，每个参战角色需要不同的编号。',
 Loadout: '设置开局带什么：牌组、牌型等级和遗物。在对局配置中为玩家选择这套初始构筑。',
 Budget: '这件遗物的所有触发规则共用这里的次数限制和冷却。一次行动包含它引发的连锁效果；开战和每次阶段切换分别计算一次次数限制。',
 FactTrigger: '当伤害、治疗、护盾、死亡或状态等事件发生后，检查条件并触发效果。可指定是谁施加或承受效果。',
 BoundaryTrigger: '在开战、玩家或敌人阶段开始／结束时触发。阶段指一方行动的时间段，一轮通常包含玩家阶段和敌人阶段。',
 ActionTrigger: '一次出牌或弃牌结算完成后触发。“本次行动者”指完成这次出牌或弃牌的角色。',
 HandModifier: '先选牌型，再选择追加效果、增加段数、替换效果或修改目标。修改已有效果时，填写它的效果编号；编号不会随排列顺序改变。',
 Status: '先设置持续时间和重复施加方式，再添加状态的具体效果。“各自独立生效”会保留多份状态，各自结算和到期。'
};
