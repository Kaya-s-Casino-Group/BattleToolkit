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
 Item:'道具能力', Effects:'直接效果', ReplaceHeld:'替换其他持有道具', Roulette:'转盘状态', DiscardedCount:'弃多少补多少（最多到上限）', Draw:'摸牌后', Reshuffle:'弃牌堆洗回', PlayPhaseEnd:'出牌阶段结束', BattleEnded:'战斗结束', Enrage:'进入狂暴', Lose:'判负', Battlefield:'全场敌人', Summoner:'各召唤者独立计算',
 MinPlayCards:'最少出牌张数', MaxPlayCards:'最多出牌张数', DrawCount:'单次摸牌上限', DrawUses:'主动摸牌次数', DrawWarningThreshold:'摸牌提醒阈值', ActionsPerTurn:'每回合行动次数', AllCardsScore:'全部牌计分', DiscardEndsTurn:'弃牌结束回合', MinFlushCards:'同花最少张数', MinStraightCards:'顺子最少张数', RetainBlock:'保留玩家护盾',
 Shuffle:'随机洗入', Top:'牌顶（先摸）', Bottom:'牌底 / 末尾', deck:'抽牌堆', discard:'弃牌堆', hand:'直接入手',
 ScorePoints:'筹码加值', ScoreMultiplier:'倍率加值（最低1）', PoisonStacks:'施毒层数追加', HealRatioAdd:'治疗比例追加', HealRatioSet:'治疗基础比例替换', HealRatioMultiply:'治疗比例乘倍', HealMultiply:'治疗量乘倍', RepeatFirstHit:'首段额外命中', ExtraHit:'追加总分比例伤害', OverhealBlock:'溢疗转盾', OverhealDamage:'溢疗转单体伤害', OverhealArea:'溢疗转群体伤害', OverhealMultiply:'溢疗伤害乘倍', DamageMultiply:'伤害乘倍', PoisonDamageMultiply:'毒伤乘倍', DrawAfterPlay:'出牌后摸牌', SelfDamage:'出牌后自身受伤', ShrinkHandEachTurn:'每回合累减手牌上限', DiscardSuitBlock:'主花色弃牌额外护盾', DualHandRelics:'同花顺兼容两侧遗物', IgnorePlayLimit:'下次出牌忽略张数上限', TurnHealthLoss:'回合开始失去生命', Program:'条件机制', Link:'怪物联动', Revive:'复活', OnEnter:'首次进入阶段', DamageDone:'造成伤害', DamageTaken:'受到伤害', HealingDone:'治疗量', BlockDone:'获得护盾', Intercept:'拦截攻击', BypassBlock:'无视护盾', DamageHeal:'伤害吸血', HealFromMaxHealth:'目标最大生命比例治疗', PreviousStraight:'前一次出牌为顺子', EarlierStraight:'此前打出过顺子', EarlierFlush:'此前打出过同花', HealthBelow:'自身生命低于', UnitHealthBelow:'指定单位生命低于', UnitHealthAtMost:'指定单位生命不高于', UnitsAliveAtLeast:'指定单位存活数量', RoundModulo:'每N回合',
 Unplayable:'不可出牌（可弃牌）', Behavior:'怪物行为', Wrath:'暴怒本体与五手', Stage:'行为阶段', Action:'行为动作', When:'行为条件', OpeningEffects:'入场状态', Summon:'召唤怪物', AddCards:'生成卡牌', HitArmor:'逐击护甲', DamageImmunity:'逐次伤害免疫', Sequence:'顺序轮换', Priority:'条件优先', Weighted:'权重随机',
 BattleContent:'内容包', Hand:'牌型', Effect:'效果步骤', OnHitStatus:'每次命中附加状态', FlushBonus:'同花额外效果', FlushBaseTotalScale:'同花基础筹码 × 一级基础倍率', CardScoreScale:'本牌筹码 × 伤害牌型倍率', Effects:'行动效果', CardPoints:'单牌计分', Rank:'点数分值', Face:'指定牌面分值', Card:'卡牌', HandLevel:'牌型等级',
 BlockActionWindow:'眩晕 · 阻止行动', PreventStatusApplication:'控制免疫 · 阻止施加', ReflectBlockedDamage:'盾反', LifeSteal:'吸血', OverhealDamage:'溢出治疗转伤害', PeriodicDamage:'周期伤害（中毒 / 流血）', IncomingAttackDamagePerStack:'流血 · 每层增加敌人攻击承伤', StackDecay:'状态独立减层', EnemyAttackScale:'当前敌人攻击 × 系数',
 Numeric:'数值修正', HandModifier:'牌型效果修正', RuleValue:'战斗规则修正', Refill:'补牌修正', Budget:'共用次数与冷却', FactTrigger:'战斗事件触发', BoundaryTrigger:'开战 / 回合阶段触发', ActionTrigger:'出牌 / 弃牌完成触发', Conditions:'全部条件同时满足',
 FactAtLeast:'本次结算数值至少达到', OwnerHealthAtMost:'持有者生命比例不高于', OwnerHasStatus:'持有者具有状态', EventStatus:'本次施加的状态', HandIs:'出牌牌型为', SubmittedCardsAtLeast:'出牌张数至少', DiscardedCardsAtLeast:'弃牌张数至少',
 HighCard:'高牌', OnePair:'一对', TwoPair:'两对', ThreeOfAKind:'三条', Straight:'顺子', Flush:'同花', FullHouse:'葫芦', FourOfAKind:'四条', StraightFlush:'同花顺', FiveOfAKind:'五条',
 Any:'任意 / 统一', Ordinary:'普通', Royal:'皇家', Damage:'伤害', Heal:'治疗', GainBlock:'获得护盾', ApplyStatus:'施加状态', Cleanse:'净化负面状态', Negative:'负面', Eliminate:'消灭',
 Constant:'固定值', ScoreTotalScale:'总分 × 系数', ScorePointsScale:'筹码 × 系数', Self:'自身', Opponents:'敌方', Allies:'友方', EventActor:'本次行动者', EventSource:'本次效果的施加者', EventTarget:'本次效果的承受者',
 FirstLiving:'首个存活', LowestHealth:'生命最低', HighestHealth:'生命最高', AllLiving:'全部存活（AOE）', Requested:'手动指定', ActorPolicy:'按角色的选敌设置',
 UntilRemoved:'直到移除', TriggerCount:'触发次数', ThroughNextPhase:'持续到指定阶段结束', ThroughCurrentRound:'持续到本轮结束', KeepLonger:'保留持续更久的状态', IndependentInstances:'各自独立生效',
 None:'无', Control:'控制', ActionCompleted:'行动完成', PhaseEnding:'阶段结束', RequireLiving:'来源必须存活', AllowDefeated:'允许来源已死亡',
 DamageDealt:'造成伤害', DamageReceived:'受到伤害', HealingDone:'造成的治疗', HealingReceived:'受到的治疗', BlockGranted:'提供的护盾', BlockReceived:'获得的护盾', AddFlat:'固定值相加', AddRate:'百分比系数相加',
 Primary:'主效果', Triggered:'触发效果', Periodic:'周期伤害', Reflection:'盾反', Lifesteal:'吸血', Overheal:'溢疗转伤',
 AppendEffect:'追加效果', AddHits:'增加段数', ReplaceEffect:'替换效果', ChangeTarget:'修改目标', MaxEnergy:'能量上限', InitialEnergy:'初始能量', PlayEnergyCost:'出牌费用', DiscardEnergyCost:'弃牌费用', HandLimit:'手牌上限',
 OpeningTurn:'开局发牌', AfterPlay:'出牌后', AfterDiscard:'弃牌后', PlayerTurnStart:'玩家回合开始', Skip:'跳过补牌', FillToLimit:'补满手牌', FixedCount:'固定张数',
 DrawCards:'效果摸牌', TurnStarted:'持有者回合开始', AddStacks:'叠加层数且不刷新期限', ChipsOnly:'踢脚牌只加筹码', AllCards:'全部牌参与倍率', ThreeOfAKindTotalScale:'葫芦三条部分得分', OnePairTotalScale:'葫芦对子部分得分', Defense:'主动防御弃牌', BeforeDraw:'摸牌前', Overflow:'超限强制弃牌', DamageSettled:'伤害结算', HealingSettled:'治疗结算', BlockGained:'获得护盾', UnitDied:'单位死亡', StatusApplied:'状态施加', StatusBlocked:'状态被阻止',
 OwnerIsSource:'遗物持有者施加效果', OwnerIsTarget:'遗物持有者承受效果', AllyIsSource:'友方施加效果', AllyIsTarget:'友方承受效果', OpponentIsSource:'敌方施加效果', OpponentIsTarget:'敌方承受效果',
 PerEvent:'每次符合条件的事件', PerAction:'每次行动', PerBoundary:'每次开战或阶段切换', DamageToHealth:'实际生命伤害', BlockAbsorbed:'护盾吸收量', HealthRestored:'实际治疗量',
 BattleStarted:'战斗开始', PhaseStarted:'阶段开始', OwnerTeam:'己方阶段', OpponentTeam:'敌方阶段', PlayCompleted:'出牌完成', DiscardCompleted:'弃牌完成',
 OwnerIsActor:'持有者行动', AllyIsActor:'友方行动', OpponentIsActor:'敌方行动', Retain:'保留护盾', AfterEnemyPhase:'敌方阶段后清除', NextPlayerTurn:'下个玩家回合', AfterAction:'行动后',
 Default:'默认', player:'玩家阶段', enemy:'敌人阶段', true:'是', false:'否', Enhanced:'强化', Laminated:'覆膜', Ineffective:'失效', Clubs:'梅花 ♣', Diamonds:'方块 ♦', Hearts:'红桃 ♥', Spades:'黑桃 ♠',
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
 if(!automatic&&group==='Opponents'&&node.tagName==='Effect'&&['Damage','ApplyStatus'].includes(node.getAttribute('kind')))fields.push(text('retargetChain','击杀转火分组（伤害与附加状态使用同名）'));
 return fields;
}
const flowFields=()=>[bool('manualDraw','启用主动摸牌流程',false),bool('autoDrawToLimit','回合开始自动补至手牌上限',false),bool('openingDeal','开局自动发牌'),num('drawCount','主动摸牌上限',5),num('drawUses','每回合主动摸牌次数',1),num('drawWarningThreshold','摸牌提醒阈值',3),num('actionsPerTurn','每回合行动次数',3),num('minPlayCards','最少出牌张数',1),num('minFlushCards','同花最少张数',5),num('minStraightCards','顺子最少张数',5),bool('effectDrawOverflow','效果摸牌允许临时超限',true),bool('forceDiscardOverflow','结束阶段强制弃至上限',true),bool('discardEndsTurn','防御弃牌后结束回合',true),bool('discardCostsAction','防御弃牌消耗行动',true),bool('endDefense','结束阶段可弃牌防御',true),bool('overflowTriggersDefense','强制弃牌也触发防御收益'),en('kickerScoring','踢脚牌计分','KickerScoring','None'),bool('layeredScoreRelics','计分遗物先加后乘',false),num('discardBlockPerPoint','每点弃牌筹码转护盾')];
const actor=()=>[...base(),num('maxHealth','生命上限',100),num('initialBlock','初始护盾',0),
 num('attackMultiplier','攻击倍率',1),num('attackFlat','攻击固定修正',0),num('incomingMultiplier','承伤倍率',1),num('incomingFlat','承伤固定修正',0),
 en('attackTarget','角色攻击目标','OpponentTargetSelection','FirstLiving'),bool('allowRequestedTargets','接受手动目标',true)];
export function descriptor(node) {
 const name=node.tagName, parent=node.parentElement?.tagName, a=k=>node.getAttribute(k);
 let fields=[],children=[];
 if(name==='BattleContent') fields=[text('id','内容包 ID',true),text('name','内容包名称'),text('version','内容版本',true),en('schemaVersion','格式版本',['1'],'1',true)];
 else if(name==='Scoring') { fields=[...base(),en('preset','计分预设',['Default'],'自定义')]; if(a('preset')!=='Default'){fields.push(version());children=['Hand','CardPoints'];} }
 else if(name==='Hand' && parent==='Scoring') fields=[en('category','牌型','HandCategory',null,true),num('points','基础筹码',0,true),num('multiplier','基础倍率',1,true),num('pointsPerLevel','每级筹码增长',10),num('multiplierPerLevel','每级倍率增长',1)];
 else if(name==='Score')fields=[en('operation','计分修正',['AddPoints','AddMultiplier','MultiplyMultiplier'],null,true),num('amount','数值',0,true),en('hand','限定牌型','HandCategory')];
 else if(name==='CardPoints') children=['Rank','Face'];
 else if(name==='Rank'||name==='Face') fields=[en('rank','点数','Rank',null,true),...(name==='Face'?[en('suit','花色','Suit',null,true)]:[]),num('points','单牌筹码',0,true)];
 else if(name==='Rules') fields=[...base(),version(),ref('scoringRef','计分规则','ScoringProfiles',true),num('roundLimit','回合上限',20),en('roundLimitBehavior','达到回合上限后',['Lose','Enrage'],'Lose'),num('enrageDamagePerRound','狂暴每回合叠加伤害',0),bool('clearEnemyBlock','敌人阶段结束清空敌方护盾'),bool('clearEnemyBlockAtTurnStart','敌人下次行动开始清空护盾'),ref('victoryEnemyRef','击败此本体即胜利（空为消灭全部）','Enemies'),en('blockExpiry','护盾清除方式','PlayerBlockExpiry','Retain'),num('maxReactionEffects','单次结算最多触发效果数',256),num('maxReactionDepth','连锁触发最多层数',16)];
 else if(name==='HandRule') { fields=[...base(),version(),bool('modernScoring','使用当前案子计分规则'),num('flushDamageMultiplier','同花伤害乘倍',2),bool('combineCompatibleEffects','兼容机制叠加'),bool('flushAsAttribute','同花作为属性，不参与伤害牌型优先级'),bool('fullHouseComponentDamage','葫芦按三条＋对子分别计伤'),bool('flushUsesRankEffects','旧版：伤害忽略花色独立判型')];children=['Hand','FlushBonus']; }
 else if(name==='Hand') { fields=[en('category','牌型','HandCategory',null,true)];if(a('category')==='StraightFlush') fields.push(en('variant','同花顺分支','HandEffectVariant','Any'));children=['Effect']; }
 else if(name==='FlushBonus') children=['Effect'];
 else if(name==='OnHitStatus') fields=[ref('statusRef','状态配置','Statuses',true),num('stacks','固定层数',1),num('stacksPerSubmittedCard','每张实际出牌追加层数',0),...lifetime(node)];
 else if(name==='Effect') {
  const trigger=['FactTrigger','BoundaryTrigger','ActionTrigger','Item'].includes(parent), modifier=parent==='HandModifier';
  if(!trigger&&!modifier) fields.push(text('id','效果编号',true));
  fields.push(en('kind','基础能力',parent==='Item'?['Damage','Heal','GainBlock']:['Damage','Heal','GainBlock','ApplyStatus','Cleanse','Eliminate','DrawCards'],null,true));
  if(['Damage','Heal','GainBlock','DrawCards'].includes(a('kind'))) fields.push(en('amountKind','数值来源',['Hand','FlushBonus'].includes(parent)?meta.enums.EffectAmountKind.filter(kind=>kind!=='EnemyAttackScale'):parent==='Effects'&&node.parentElement.parentElement?.tagName==='Action'&&a('kind')==='Damage'?['Constant','EnemyAttackScale']:['Constant'],'Constant'),num('amount','数值 / 系数',undefined,true));
  if(['Hand','FlushBonus'].includes(parent)) fields.push(bool('perSubmittedCard','按实际出牌逐张重复效果'));
  if(parent==='BoundaryTrigger'&&['Damage','Heal','GainBlock'].includes(a('kind'))) fields.push(num('amountPerHandCard','每张当前手牌追加数值',0));
  if(a('kind')==='ApplyStatus') fields.push(ref('statusRef','状态配置','Statuses',true),num('stacks','固定层数',1),...(['Hand','FlushBonus','FactTrigger'].includes(parent)?[num('stacksPerSubmittedCard','每张实际出牌追加层数',0)]:[]),...lifetime(node));
  if(a('kind')==='Damage'&&['Hand','FlushBonus','Effects'].includes(parent)) children=['OnHitStatus'];
  if(!(modifier && node.parentElement.getAttribute('operation')==='ReplaceEffect')) {
   const groups=parent==='ActionTrigger'?['Self','Opponents','Allies','EventActor']:parent==='FactTrigger'?['Self','Opponents','Allies','EventSource','EventTarget']:null;
   fields.push(...target(node,trigger,groups));
  }
 }
 else if(name==='Status') {fields=[...base(),en('tags','状态标签','StatusTags','None'),...lifetime(node,true),en('reapply','重复施加','StatusReapplyPolicy','KeepLonger')];children=['BlockActionWindow','PreventStatusApplication','ReflectBlockedDamage','LifeSteal','OverhealDamage','PeriodicDamage','HitArmor','DamageImmunity','IncomingAttackDamagePerStack','StackDecay'];}
 else if(name==='IncomingAttackDamagePerStack') fields=[num('rate','每层额外承伤比例（0.5 = 50%）',0.5,true)];
 else if(name==='StackDecay') fields=[en('timing','减层时机',['TurnStarted','ActionCompleted','PhaseEnding'],'ActionCompleted',true),en('timingSubject','计时对象','PeriodicTimingSubject','Applier',true),num('stacks','每次减少层数',1,true)];
 else if(name==='PreventStatusApplication') fields=[en('tags','阻止的状态标签','StatusTags','Control')];
 else if(['ReflectBlockedDamage','LifeSteal','OverhealDamage','PeriodicDamage'].includes(name)) {
  fields=name==='PeriodicDamage'?[
   {...en('timingSubject','计时对象','PeriodicTimingSubject','Holder'),optionLabels:{Holder:'中毒者 / 状态持有者',Applier:'施加者'}},
   {...en('timing','结算时机',['TurnStarted','ActionCompleted','PhaseEnding'],'TurnStarted',true),optionLabels:{TurnStarted:'回合开始',ActionCompleted:'每次有效行动后',PhaseEnding:'所属阵营阶段结束'}},
   num('amount','每层伤害',undefined,true)
  ]:[num('ratio','比例（0.2 = 20%）',undefined,true)];
  fields.push(...target(node,true,name==='ReflectBlockedDamage'?['Self','Opponents','Allies','EventSource']:null,name==='ReflectBlockedDamage'?'EventSource':name==='OverhealDamage'?'Opponents':'Self'));
  if(name==='ReflectBlockedDamage') fields.push(en('sourcePolicy','来源存活要求','CombatReactionSourcePolicy','RequireLiving'));
 }
 else if(name==='Relic'&&parent==='Relics') {fields=[...base(),version(),num('charges','初始触发总次数（空为无限）'),num('maxCopies','同种遗物最多携带数量',1),num('maxLevel','最高等级',1),num('slots','占用遗物栏数量',1),num('sellPrice','售价',1),num('upgradeCost','升级费用',5),num('growthPerScoredRound','每计分轮成长',0)];children=['Numeric','HandModifier','Score','RuleValue','Refill','FactTrigger','BoundaryTrigger','ActionTrigger','Budget','Program','Item'];}
 else if(name==='Item') {
  fields=[en('activation','主动使用方式',['None','Effects','ReplaceHeld','Roulette'],'None'),bool('removeOnDepleted','次数耗尽后移除'),num('cooldownBattles','使用后冷却战斗场数',0),num('reviveRatio','死亡时恢复最大生命比例',0),num('executeChance','出牌造成生命伤害时消灭概率',0),num('reflectRatio','实际挡伤反伤比例',0),num('locationEnterHeal','进入每个地点回复生命',0),num('locationLeaveGoldRatio','离开每个地点金币增加比例',0),num('rouletteBase','转盘每回合伤害倍率底数',10),num('rouletteTurns','随机爆炸回合范围（1—6）',6),num('explosionDamage','转盘爆炸伤害',2147483647)];
  if(a('activation')==='Effects')children=['Effect'];
 }
 else if(name==='Program') fields=[text('id','规则编号',true),en('kind','机制',['ScorePoints', 'ScoreMultiplier', 'PoisonStacks', 'HealRatioAdd', 'HealRatioSet', 'HealRatioMultiply', 'HealMultiply', 'RepeatFirstHit', 'ExtraHit', 'OverhealBlock', 'OverhealDamage', 'OverhealArea', 'OverhealMultiply', 'DamageMultiply', 'PoisonDamageMultiply', 'DrawAfterPlay', 'SelfDamage', 'ShrinkHandEachTurn', 'DiscardSuitBlock', 'DualHandRelics', 'IgnorePlayLimit', 'TurnHealthLoss'],null,true),num('amount','数值 / 系数',0),en('hand','牌型条件（同花含属性）','HandCategory'),bool('excludeHand','排除此牌型'),num('minCards','最少出牌张数',1),num('maxCards','最多出牌张数',512),en('history','本回合历史条件',['None','PreviousStraight','EarlierStraight','EarlierFlush'],'None'),bool('oncePerRound','每回合一次'),ref('statusRef','关联状态','Statuses'),num('statusThreshold','目标状态层数阈值',0),bool('highAceOrKing','最高牌须为 A 或 K')];
 else if(name==='Budget') fields=[num('maxUsesPerAction','每次行动最多触发次数'),num('maxUsesPerRound','每轮最多触发次数'),num('maxUsesPerBoundary','每次开战或阶段切换最多触发次数'),num('chargeCost','每次触发消耗次数',1),num('cooldownRounds','触发后冷却轮数',0)];
 else if(name==='Numeric') fields=[text('id','效果编号（ID）',true),en('stat','修正数值','BattleNumericStat',null,true),en('operation','修正方式','RelicNumericOperation',null,true),num('value','修正值（比例 0.2 = 20%）',undefined,true),f('origins','适用效果来源','multi',{options:meta.enums.CombatEffectOrigin,default:'Primary'}),en('hand','限定牌型（可选）','HandCategory')];
 else if(name==='HandModifier') {
  fields=[text('id','效果编号（ID）',true),en('category','适用牌型','HandCategory',null,true),en('operation','效果调整方式','RelicHandEffectOperation',null,true)];
  if(a('operation')!=='AppendEffect') fields.push(f('stepId','要修改的效果编号','step',{required:true}));
  if(a('operation')==='AddHits') fields.push(num('hits','额外段数',1));
  if(a('operation')==='ChangeTarget') fields.push(...target(node));
  if(['AppendEffect','ReplaceEffect'].includes(a('operation'))) children=['Effect'];
 }
 else if(name==='RuleValue') fields=[text('id','效果编号（ID）',true),en('key','修正规则','BattleRuleValueKey',null,true),num('delta','整数增减',0),num('multiplier','加法结算后乘倍率',1)];
 else if(name==='Refill') fields=[text('id','效果编号（ID）',true),en('timing','补牌时机','CardDuelDealTrigger',null,true),en('mode','补牌方式','BattleRefillMode','沿用原规则'),...(a('mode')==='FixedCount'?[num('fixedCount','固定补牌张数',undefined,true)]:[]),num('delta','额外补牌张数',0)];
 else if(['FactTrigger','BoundaryTrigger','ActionTrigger'].includes(name)) {
  fields=[text('id','触发规则编号（ID）',true),en('timing','触发时机',name==='FactTrigger'?'RelicTriggerTiming':name==='BoundaryTrigger'?'RelicBoundaryTiming':'RelicActionTiming',null,true)];
  if(name==='FactTrigger') fields.push(en('relation','谁施加或承受本次效果','RelicEventRelation','OwnerIsSource'),en('occurrence','触发频率','RelicTriggerOccurrence','PerEvent'),f('origins','哪些效果可以触发','multi',{options:meta.enums.CombatEffectOrigin,default:'Primary'}));
  if(name==='BoundaryTrigger'&&!['BattleStarted','BattleEnded'].includes(a('timing'))) fields.push(en('phase','所属阶段','RelicPhaseRelation'));
  if(name==='ActionTrigger') {fields.push(en('relation','行动者关系','RelicActionRelation','OwnerIsActor'));if(a('timing')==='DiscardCompleted')fields.push(en('discardKind','弃牌类型',['Any',...meta.enums.BattleDiscardKind],'Defense'));}
  fields.push(num('priority','同件遗物内优先级',0),bool('chain','允许派生效果继续触发'),en('sourcePolicy','来源存活要求','CombatReactionSourcePolicy','RequireLiving'));children=['Conditions','Effect'];
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
 else if(name==='Player'&&parent==='Players') fields=[...actor(),ref('handRuleRef','牌型效果规则','HandRules'),num('pointsPerDamage','旧版伤害换算除数（使用牌型效果时留空）'),num('maxEnergy','能量上限',3),num('initialEnergy','初始能量',3),num('playEnergyCost','出牌费用',1),num('discardEnergyCost','弃牌费用',1),num('handSize','手牌上限',8),num('discards','整场战斗可弃牌次数',3),bool('unlimitedDiscards','无限弃牌次数'),num('maxPlayCards','最多出牌张数',5),num('maxDiscardCards','最多弃牌张数',5),en('playRefill','出牌补牌时机','CardDuelRefillTiming','NextPlayerTurn'),en('discardRefill','弃牌补牌时机','CardDuelRefillTiming','AfterAction'),num('discardBlockPerCard','每张弃牌获得护盾（空为无收益）'),...flowFields()];
 else if(name==='Enemy'&&parent==='Enemies') {fields=[...actor(),bool('scaleHealthWithAdventure','生命上限参与冒险章节成长',true)];if(![...node.children].some(n=>['Effects','Behavior'].includes(n.tagName)))fields.push(num('damage','固定攻击伤害',0));children=['Effects','Behavior'];}
 else if(name==='Effects'||name==='OpeningEffects') {fields=[version(),text('explanation','效果说明')];children=['Effect'];}
 else if(name==='Behavior') {fields=[num('seed','行为随机种子',1),num('attack','初始攻击（配合当前敌人攻击系数）'),num('attackPerRound','每完整回合结束攻击增加',0),num('intentPreviewTurns','预告回合数（1—8）',1),bool('refreshOnHealthThreshold','血量阈值变化立即刷新意图'),bool('consumeFailedSummon','召唤失败消耗意图',true),bool('recheckIntentOnAction','战场变化刷新意图，执行前再次校验')];children=['OpeningEffects','Stage','Link','Wrath'];}
 else if(name==='Wrath') fields=[text('bloodHandRefs','四只饮血手模板 ID（逗号分隔）',true),ref('bindingHandRef','束缚手模板','Enemies',true),num('baseAttack','本体基础攻击',8),num('attackPerHand','每只存活手增加攻击',5),num('attackPerCharge','每次成功蓄力增加攻击',10),text('chargeActionId','蓄力动作 ID',true),num('initialWarningRound','首次可预警轮次',5),num('cooldownRounds','释放或取消后冷却轮数',5),num('regenerationRounds','断手再生间隔轮数',8),num('ultimateScale','额外大招攻击系数',3)];
 else if(name==='Stage') {fields=[text('id','阶段 ID',true),en('mode','选择方式',['Sequence','Priority','Weighted'],'Sequence')];children=['When','Action','OnEnter'];}
 else if(name==='Action') {fields=[text('id','动作 ID',true),text('name','意图名称'),num('weight','权重',1),num('cooldownRounds','间隔回合数',0),num('maxUses','最多使用次数（0不限）',0),en('deadTarget','锁定目标死亡时',['Retarget','Skip'],'Retarget')];children=['When','Effects','Summon','AddCards','Revive'];}
 else if(name==='When') {fields=[en('kind','条件类型',['Always','All','Any','Not','HealthAtMost','RoundAtLeast','AlliesBelow','HasStatus','UnitsAliveAtLeast','UnitHealthAtMost','RoundModulo','HealthBelow','UnitHealthBelow'],'Always')];if(['HealthAtMost','RoundAtLeast','AlliesBelow','UnitsAliveAtLeast','UnitHealthAtMost','RoundModulo','HealthBelow','UnitHealthBelow'].includes(a('kind')))fields.push(num('value','阈值（生命比例0..1）',0));if(['UnitsAliveAtLeast','UnitHealthAtMost','UnitHealthBelow'].includes(a('kind')))fields.push(text('enemyRefs','怪物模板 ID（逗号分隔）',true));if(a('kind')==='HasStatus')fields.push(ref('statusRef','状态','Statuses',true));if(['All','Any','Not'].includes(a('kind')))children=['When'];}
 else if(name==='OnEnter') {fields=[];children=['Revive'];}
 else if(name==='Revive') fields=[text('enemyRefs','按此模板顺序复活（逗号分隔）',true),num('count','复活数量',1)];
 else if(name==='Link') {fields=[text('id','联动编号',true),en('kind','联动类型',['DamageDone','DamageTaken','BlockReceived','HealingDone','BlockDone','Intercept','BypassBlock','DamageHeal','HealFromMaxHealth'],null,true),text('targets','作用模板 ID / Self / Opponents，逗号分隔',true),num('rate','每单位增减比例 / 系数',0),text('countRefs','计数模板 ID（逗号分隔；空按1）'),bool('snapshot','按整次行动开始时快照计数'),ref('healTarget','吸血治疗目标模板','Enemies')];children=['When'];}
 else if(name==='Summon') fields=[ref('enemyRef','召唤模板','Enemies',true),num('count','数量',1),num('livingLimit','含本体的存活上限',4),en('limitScope','上限计算范围',['Battlefield','Summoner'],'Battlefield')];
 else if(name==='AddCards') fields=[f('face','牌面','card',{required:true}),num('count','数量',1),en('zone','放入区域',['deck','discard','hand'],'discard'),en('position','插入位置（默认：抽牌堆随机洗入，其他末尾）','CardInsertionPosition',node.getAttribute('zone')==='deck'?'Shuffle':'Bottom'),bool('disabled','完全禁用（不可出牌也不可弃牌）',false),f('effects','卡牌效果','multi',{options:meta.enums.CardEffects,default:'Ineffective,Unplayable'})];
 else if(name==='HitArmor') fields=[num('multiplier','伤害倍率',0.5),num('reduction','固定减伤',0),num('consumeStacks','每击消耗层数',1),bool('primaryOnly','仅主效果消耗',true)];
 else if(name==='Deck') {fields=base();children=['Card'];}
 else if(name==='Card') fields=[text('id','卡牌来源 ID',true),f('face','牌面','card',{required:true}),f('effects','卡牌效果','multi',{options:meta.enums.CardEffects,default:'None'}),bool('disabled','牌已失效')];
 else if(name==='Loadout') {fields=[...base(),ref('deckRef','初始牌组（空为标准 52 张）','Decks')];children=['HandLevel','Relic'];}
 else if(name==='HandLevel') fields=[en('category','牌型','HandCategory',null,true),num('level','初始等级',undefined,true)];
 else if(name==='Relic') fields=[text('instanceId','这件遗物的编号（同一构筑内唯一）',true),ref('ref','遗物配置','Relics',true),num('level','等级',1),num('growth','成长',0),num('charges','初始剩余触发次数（留空使用遗物配置）'),num('cooldownRounds','初始冷却轮数',0)];
 else if(name==='Encounter') {fields=[...base(),ref('rulesRef','战斗规则','BattleRules',true)];children=['Player','Enemy'];}
 else if(name==='Player') fields=[text('id','参战角色编号（本场唯一）',true),ref('ref','玩家模板','Players',true),ref('loadoutRef','初始构筑','Loadouts'),num('initialHealth','开局生命值（留空为满血）')];
 else if(name==='Enemy') fields=[text('id','参战角色编号（本场唯一）',true),ref('ref','敌人模板','Enemies',true)];
 if(name==='PeriodicDamage')fields.push(num('stackDecay','结算后减少层数',0));
 if(name==='ActionTrigger'&&a('timing')==='DiscardCompleted')fields.push(en('discardKind','弃牌类型',['Defense','BeforeDraw','Overflow','Any'],'Defense'));
 return {fields,children,label:tr(name)};
}
export const exclusiveChildren=new Set(['Item','Budget','Conditions','CardPoints','Effects','Behavior','OpeningEffects','Wrath']);

// Explanations for concepts that need more than a short field label.
export const help = {
 DamageImmunity: '每段正数伤害消耗一层并最终归零，包含攻击、毒伤、反伤和道具伤害。原本为零的伤害不消层；施毒和眩晕仍正常生效，强制击杀不受免伤阻挡。',
 Wrath: '本体攻击由基础攻击、当前存活手和成功蓄力共同决定。五手齐全且冷却结束时预警，下一敌方阶段在常规动作后追加大招。断手立即取消预警并重新冷却；本体眩晕阻止释放也会取消。每只手独立计时再生，所有轮次均按全场回合计算。常规序列仍由 Stage 配置。',
 IncomingAttackDamagePerStack: '每层按配置比例增加敌人对玩家的攻击伤害；层数系数相加，在护盾前合成为一次伤害，不另算一次流血伤害。道具自伤、毒伤、反伤等非敌人攻击不受影响。当前精英流血每层 +30%，可累积，可被净化。',
 StackDecay: '独立减少状态层数，不产生周期伤害。标准流血选择施加者 + 每次实际行动完成后，减少1层；眩晕跳过不减层。施法先伤害、再施加3层，随后行动完成减1层，首轮结束剩2层。',
 Behavior: '可设置初始攻击与每完整回合结束的攻击增量；第一回合使用初始攻击，眩晕不阻止完整回合的攻击成长。伤害步骤选择“当前敌人攻击 × 系数”：标准精英施法0.5、普攻1，以Sequence顺序循环。未配置攻击时保留原固定伤害行为。',
 Item: '主动道具只能在玩家行动阶段、有剩余行动点时使用，不消耗行动点。消耗品按每件实例记录次数，次数耗尽后可移除；替换道具只能选择另一件持有道具，新道具以完整初始状态生成。转盘使用时抽取爆炸回合并固定，激活期间不能再次使用；爆炸、死亡复活或战斗结束后状态解除。预览使用同一条规则链但不提交消耗与随机状态。地点进出效果包含商店和休息点，冷却只按完成的战斗计数。',
 PeriodicDamage: '本次基础伤害 = 当前层数 × 每层伤害，伤害结算后再减少层数。计时对象与结算时机组合，可选择中毒者回合开始、中毒者所属阵营阶段结束、施加者回合开始、施加者所属阵营阶段结束。回合开始：按所选角色每回合结算一次，敌人在行动窗口前、玩家在摸牌前；所属阵营阶段结束：等所选角色同阵营全部行动结束后结算，眩晕仍会触发。也可保留每次有效行动后结算，眩晕跳过和直接结束回合不触发。计时对象只决定何时结算，不改变伤害目标；“自身”仍指状态持有者。标准中毒默认按中毒者回合开始（Holder + TurnStarted），每层伤害 1、每次结算后减少 1 层。',
 AddCards: '按意图向每个存活玩家塞牌。默认进弃牌堆；抽牌堆默认随机洗入，也可选择牌顶或牌底。手牌/弃牌堆仅支持默认或末尾。直接入手是额外配置。是否允许超限、超限后是否强制弃牌由玩家规则配置；最新标准允许保留超限手牌。',
 Encounter: '将玩家、初始构筑、敌人和战斗规则组合成一场对局。同一个敌人模板可以多次加入，每个参战角色需要不同的编号。',
 Loadout: '设置开局带什么：牌组、牌型等级和遗物。在对局配置中为玩家选择这套初始构筑。',
 Budget: '这件遗物的所有触发规则共用这里的次数限制和冷却。一次行动包含它引发的连锁效果；开战和每次阶段切换分别计算一次次数限制。',
 FactTrigger: '当伤害、治疗、护盾、死亡或状态等事件发生后，检查条件并触发效果。可指定是谁施加或承受效果。',
 BoundaryTrigger: '在开战、玩家或敌人阶段开始／结束时触发。阶段指一方行动的时间段，一轮通常包含玩家阶段和敌人阶段。',
 ActionTrigger: '一次出牌或弃牌结算完成后触发。“本次行动者”指完成这次出牌或弃牌的角色。',
 HandModifier: '先选牌型，再选择追加效果、增加段数、替换效果或修改目标。修改已有效果时，填写它的效果编号；编号不会随排列顺序改变。',
 HandRule: '当前案子模式：只计有效牌，同花五张作为属性，伤害额外乘同花系数；普通同花顺按顺子倍率，葫芦按三条群攻＋对子单体分别计伤。顺子从小到大逐张攻击，A 在 10JQKA 中计 14、A2345 中计 1。治疗按五张同花基础筹码 × 比例计算，比例追加后最终向下取整。旧模板保留原算法；当前日期版开启此模式。',
 Status: '先设置持续时间和重复施加方式，再添加状态的具体效果。“各自独立生效”会保留多份状态，各自结算和到期。'
};
