# XML 战斗内容 v1

本目录是纯规则层的内容入口。XML 文本 → 不可变来源树 → 统一校验/类型化内容 → 既有 CardDuelEngine / BattleSession。读取器只接收字符串；文件选择、读写、HTML 表单和 Unity 资产属于宿主。`SingleBattleDemoNew` 宿主现已消费独立的场景内容 XML；其他场景和旧 SO 入口继续保留，纯规则包不引用 Unity。

[完整示例](Examples/battle-content.xml) 展示所有配置目录的连接方式，数值仅用于格式与功能演示，不是正式平衡配置。

## 入口与提交边界

```csharp
var build = BattleContentCompiler.CompileXml(xmlText, "battle-content.xml");
if (!build.Success) { /* 展示 build.Diagnostics，不能运行部分内容 */ }
else
{
    var result = BattleContentFactory.Create(build.Content,
        new BattleSetupRequest("example-fight", "battle-001", randomState));
    if (result.Success)
    {
        var session = result.Session; // 未 Start；没有执行开战触发或发牌
        var started = session.Execute(session.State.Revision,
            new ParticipantId("p"), CardDuelAction.Start);
    }
}
```

也可分别调用 `BattleContentXmlReader.Read` 和 `BattleContentCompiler.Compile(source)`，便于工具保留原始位置。编译与组装结果均包含 Success、Diagnostics；任何 Error 时 Content/Session 为 null，Warning 不阻止编译。诊断携带 Code、Severity、Message、SourceName/Line/Column/Path，以及可用时的 DefinitionId、FieldPath。独立定义错误尽量一次收集；一个定义内依赖失败后不继续制造级联错误。位置精确到 XML 元素，属性错误附带字段名；组合错误定位编成并注明相关参与者/遗物/步骤。

`CompiledBattleContent` 保存类型化只读字典；定义与局内状态分离。Factory 复制玩家规则和构筑状态，每个参与者获得独立遗物身份、次数、冷却与记忆。随机状态必须显式传入；创建时沿用旧洗牌规则，但不修改调用方的 RandomState、不执行 Start。同一配置、对局 ID、初始随机状态产生相同初始对局。

编译阶段也走真实创建路径校验每个编成，但不 Start、不结算效果、不消耗遗物。运行中的溢出、触发队列上限、死亡目标、费用、补牌与整体事务提交继续由现有引擎负责。编译通过不表示任何运行状态下都不可能溢出，也不代替对局/平衡测试。

## 配置归属

根为 `<BattleContent schemaVersion="1" id="..." version="...">`。每个目录至多一次；目录可省略，适合先编辑内容草稿。目录内 ID 唯一、大小写敏感；跨目录通过明确的类型引用，不隐式查找同名内容。

| 目录 / 子项 | 拥有的数据 | 引用与边界 |
| --- | --- | --- |
| ScoringProfiles / Scoring | 全部牌型基础分、倍率、升级增量、单牌分值 | 公共计分；不含玩家当前等级 |
| BattleRules / Rules | 回合上限、清盾规则、触发队列限制 | scoringRef；共享状态目录；不含角色属性 |
| HandRules / HandRule | 各牌型的有序效果与稳定步骤 ID | ApplyStatus 引用状态；不含遗物实例 |
| Statuses / Status | 标签、默认期限、重复施加策略、行为列表 | 行为参数内联；不配置局内剩余期限 |
| Relics / Relic | 获取/升级约束、四类能力、共享触发预算 | 引用状态和牌型步骤；不持有局内使用次数 |
| Players / Player | 基础生命/护盾、个人行动与补牌规则、伤害/目标策略 | 可选 handRuleRef；不含当前血量或构筑 |
| Enemies / Enemy | 基础属性、固定伤害或固定效果序列 | 引用状态；本期没有敌人轮换 AI 脚本 |
| Decks / Deck | 卡牌来源 ID、标准牌面、卡牌效果、失效标志 | 定义初始牌组；不定义洗牌后的手牌/牌区 |
| Loadouts / Loadout | 初始牌组、牌型等级、遗物实例初始值 | deckRef 可省略；Relic.ref；不定义敌人 |
| Encounters / Encounter | 规则集、阵容顺序、参与者 ID、当前血量覆盖 | rulesRef、Player.ref/loadoutRef、Enemy.ref |

章节与独立定义可前向引用，解析顺序由编译器控制。编成参与者 ID 必须全局唯一；遗物 instanceId 在构筑内唯一，组装时按参与者生成不冲突的局内 ID。角色模板不因 initialHealth 覆盖而改变。

根和多数定义允许可选 name，供外部工具显示。XML 不持有图片、特效、动画、Unity 类型名或脚本路径。当前是单个内容包，不处理外部文件 include、跨包合并、覆盖顺序或热更新迁移；这些行为不能通过未知字段暗中启用。

## 主要字段与默认值

- `Scoring preset="Default"` 使用现有 ScoreProfile.Default，不能同时填写自定义版本/子项。自定义 Scoring 必须有十类 Hand（category、points、multiplier 必填；pointsPerLevel 默认 10、multiplierPerLevel 默认 1）。可选 CardPoints 内需要十三个 Rank（rank/points），可追加 Face（rank/suit/points）覆盖；缺省整个表沿用既有单牌分值。
- `Rules`：scoringRef 必填；roundLimit 默认 20，blockExpiry 默认 Retain；maxReactionEffects 默认 256、maxReactionDepth 默认 16。可选 version 默认继承包版本。
- `Player`：maxHealth=100、initialBlock=0、maxEnergy=3、initialEnergy=3、playEnergyCost=1、discardEnergyCost=1、handSize=8、discards=3、unlimitedDiscards=false、maxPlayCards=5、maxDiscardCards=5。playRefill=NextPlayerTurn、discardRefill=AfterAction。缺省弃牌收益为空；显式 discardBlockPerCard 启用既有按张护盾。未设置 handRuleRef 时仍为 EqualScoreDamageRule；可选 pointsPerDamage 使用旧除法伤害规则，不能与 handRuleRef 同时配置。可选 discardBlockPerPoint 按弃牌筹码总和计算护盾，与 discardBlockPerCard 互斥。新流程字段见下节。
- 双方模板可配 attackMultiplier/attackFlat、incomingMultiplier/incomingFlat、attackTarget/allowRequestedTargets；未填写时使用现有默认策略。单步目标仍由效果自己配置；ActorPolicy 才委托给角色目标策略。本期 XML 未开放独立 supportTarget 字段，沿用现有默认友方策略；需要自动选定友方时可直接配置效果的 Allies/selection。
- `Enemy`：maxHealth=100、initialBlock=0；缺省 damage=0。damage 与子项 Effects 互斥，Effects 内每个 Effect 必须有稳定 id。敌人序列只支持常量，不支持手动 Requested 目标。
- `Deck/Card`：id 是来源 ID，face 是现有标准牌面 ID（例如 standard:1:2，梅花 2），effects 为 CardEffects 的枚举名称或逗号组合，disabled 默认 false。来源 ID 不同可以引用同一牌面。目录验证 1–512 张和来源 ID 唯一；自动发牌流程还验证有效手牌上限所需牌数；手动摸牌流程允许短牌库。
- `Loadout`：deckRef 省略时使用既有标准 52 张牌组。HandLevel 要求 category/level，省略类别默认 1 级。Relic 要求 instanceId/ref，level=1、growth=0、cooldownRounds=0；charges 省略继承遗物定义，未限次遗物不能填有限 charges。实例参数不能越过定义的等级/拷贝约束。
- `Encounter`：rulesRef 必填。Player 子项要求 id/ref，可选 loadoutRef、initialHealth；Enemy 子项要求 id/ref。参与者数量、重复 ID 和初始血量通过现有组装检查。

## 手动摸牌与弃牌窗口

标准文件显式启用以下 Player 字段；直接 C# 组装和未声明 manualDraw 的已有内容仍可选择原补牌策略。标准与主题 XML 已原地更新。

| 字段 | 缺省值 | 说明 |
| --- | --- | --- |
| manualDraw / openingDeal | false / false | 启用新窗口；是否开局额外按手牌上限发牌 |
| drawCount / drawUses / drawWarningThreshold | 5 / 1 / 3 | 单次手动张数、每回合次数、已有手牌提示阈值 |
| minPlayCards / actionsPerTurn | 1 / 3 | 出牌下限、独立于能量费用的行动上限 |
| effectDrawOverflow | true | 效果摸牌可超过手牌上限，结束出牌时强制弃置 |
| discardEndsTurn / discardCostsAction | true / true | 行动内防御弃牌后结束、消耗一次行动及其费用 |
| endDefense / overflowTriggersDefense | true / false | 结束出牌后可免费防御一次；超限弃牌是否带防御基础收益及默认防御触发 |
| kickerScoring / layeredScoreRelics | None / false | 标准显式 ChipsOnly / true；可选 AllCards 或 None |

`PlayLimit`/`DrawLimit`/`RequiredDiscards`/`RequiresDrawWarning` 返回当前遗物修正后的约束。MinPlayCards/MaxPlayCards 为 1–10（超过 5 张时识别最佳五张并保留其余筹码）；DrawCount/DrawWarningThreshold 为 0–512、DrawUses 为 0–64、ActionsPerTurn 为 1–64。RuleValue 修改新参数后钳制到范围，AllCardsScore 为 0/1。

窗口依次为 Draw → Action → ForcedDiscard（超限时）→ Ending。Draw 的 0 张等于 SkipDraw；满手尝试正数摸牌拒绝且保留次数。DiscardForDraw 只允许提示分支，未确认选择时宿主不提交命令。ConfirmOverflow 必须恰好选择超限张数，默认没有防御收益；可用 discardKind=Overflow/Any 明确订阅强制弃牌。辅助弃牌不推进旧 ActionCompleted 周期伤害。普通 Discard 是防御弃牌，结束窗口中的一次防御不扣行动/能量；FinishTurn 才真正结束此玩家回合。

计分分成乘倍率的 Points 和不乘倍率的 UnmultipliedPoints；Total 向下取整后相加。ScorePointsScale 读取两部分筹码之和。`<Score operation="AddMultiplier" amount="2"/>` 为遗物加倍率；`<Score operation="MultiplyMultiplier" amount="1.5"/>` 乘倍率。同层按获得顺序；Score 改筹码/倍率，Numeric 继续按获得顺序修改最终伤害等量，两者用途不同。

## 效果、目标和状态

HandRule 中每类 Hand 必须覆盖且不能重叠。同花顺可用单个 Any 分支统一行为，或分别配置 Ordinary/Royal；其他类别只允许 Any。每个分支有 1–64 个步骤，id 只需在本分支唯一，效果顺序影响结算。

Effect.kind 允许 Damage、Heal、GainBlock、DrawCards、ApplyStatus、Cleanse、Eliminate。前四者必须显式填写 amount：默认 amountKind=Constant（非负整数），牌型效果另外支持 ScoreTotalScale / ScorePointsScale（乘以权威总分/全部筹码后向下取整），葫芦另支持 ThreeOfAKindTotalScale / OnePairTotalScale（分别使用三条和一对的筹码、倍率及等级）。系数非负且检查溢出。DrawCards 每次最多 512 张，只允许玩家接收，按牌库/弃牌堆实际剩余量摸取，不消耗手动摸牌次数。这是封闭表达式集合，不执行自由公式或代码。ApplyStatus 要求 statusRef，可填 stacks（默认 1）及期限覆盖；Cleanse / Eliminate 不接受数值/状态字段。Cleanse 移除目标的 Negative 或 Control 状态实例，保留增益。当前类型无关的字段会报错，而不是读取后忽略。

主效果目标默认 Opponents/FirstLiving；可选 Self、Opponents、Allies，selection 可选 FirstLiving、LowestHealth、HighestHealth、AllLiving、Requested、ActorPolicy（组合限制复用 EffectTargetSpec）。Requested 可配 requestSlot。AOE 是 selection=AllLiving，与能力类型解耦；Heal 若要治疗自己须明确 target=Self，不按效果类型推测目标。

主伤害/状态步骤可配 `retargetChain="straight"`，仅限单体敌方目标。同链后续伤害在前目标死亡时改选最低生命存活敌人（同血量按阵容顺序），附加状态跟随最近一击且跳过尸体；反击、遗物等实际结算变化也会参与下一击选择。

自动触发使用 CombatTargetSpec，可配置 Self、Opponents、Allies、EventSource、EventTarget、EventActor；后面三者仅在相应事实存在的入口使用。EventActor 仅供行动完成触发；边界没有事件来源/目标，不允许借用。自动触发不接收 Requested/requestSlot。遗物触发目标默认 Self；盾反默认 EventSource、吸血默认 Self、溢出治疗转伤默认 Opponents、周期伤害默认 Self。

Status 可包含 BlockActionWindow、PreventStatusApplication、ReflectBlockedDamage、LifeSteal、OverhealDamage、PeriodicDamage。三种比例行为显式填写 ratio；周期行为显式填写 timing/amount，timing 支持 ActionCompleted、PhaseEnding、TurnStarted。stackDecay 默认 0；大于 0 时每次触发后扣除对应层数，层数归零移除。标准中毒（ID 保留 bleed）使用 TurnStarted/amount=1/stackDecay=1。默认 lifetime=UntilRemoved；也可 TriggerCount + count（默认 1）、ThroughNextPhase + phase（player/enemy）、ThroughCurrentRound。效果未覆盖期限时继承定义；多种行为不能共用 TriggerCount 消耗预算。reapply 默认 KeepLonger，也支持 IndependentInstances 和 AddStacks。AddStacks 对同目标同定义合并层数，保留原期限；stackDecay 与 TriggerCount 不能同时使用。控制免疫是 PreventStatusApplication 的提前状态；事后移除使用 Cleanse。Status.tags 支持 None、Control、Negative 及逗号组合；未标记 Negative/Control 的状态不会被净化。

## 遗物四类能力

遗物内部条目 id 跨四类能力和全部触发列表唯一，最多 32 项。配置复用 BattleRelicRules，XML 不引入另一套叠加或排序规则。

| 子项 | 字段和用途 |
| --- | --- |
| Numeric | id/stat/operation/value；stat 为伤害、治疗、护盾的施加方/接收方；operation 为 AddFlat/AddRate；origins 默认 Primary，可填逗号列表；可选 hand 限定牌型 |
| HandModifier | id/category/operation；AppendEffect 追加一个含目标的 Effect；ReplaceEffect 用 stepId 替换无目标 Effect；AddHits 用 stepId/hits；ChangeTarget 用 stepId/target/selection/requestSlot |
| Score | operation/amount，可选 hand；AddPoints、AddMultiplier、MultiplyMultiplier，按层次再按获得顺序计算 |
| RuleValue | id/key/delta：能量上限、初始能量、出牌/弃牌费用、手牌上限，以及 MinPlayCards、MaxPlayCards、DrawCount、DrawUses、DrawWarningThreshold、ActionsPerTurn、AllCardsScore 的整数修正 |
| Refill | id/timing，及可选 mode/fixedCount/delta：沿用既有补牌修正规则 |
| FactTrigger | id/timing；可选 relation/occurrence/origins/priority/chain/sourcePolicy，子项 Conditions 与 Effect |
| BoundaryTrigger | id/timing；可选 phase/priority/chain/sourcePolicy，子项 Conditions 与 Effect |
| ActionTrigger | id/timing；可选 relation/priority/chain/sourcePolicy，弃牌触发可加 discardKind=Defense（默认）/BeforeDraw/Overflow/Any；子项 Conditions 与 Effect |
| Budget | 共享 maxUsesPerAction/maxUsesPerRound/maxUsesPerBoundary，chargeCost 默认 1、cooldownRounds 默认 0；每件遗物最多一个 |

Conditions 是 AND 列表，支持 FactAtLeast、OwnerHealthAtMost、OwnerHasStatus、EventStatus、HandIs、SubmittedCardsAtLeast、DiscardedCardsAtLeast。事实时机/行动种类不可提供的条件会拒绝。触发 Effect 不填步骤 id，由触发条目标识；这里仅接受常量。Numeric 按 Loadout 中遗物的获得顺序、同件内配置顺序逐条计算 `floor(max(0, 当前量 × max(0, 1 + rate) + flat))`，前一步结果参与下一步。触发先按玩家阵容和遗物获得顺序，同件内再按 priority 升序、同优先级按配置顺序；priority 不跨遗物抢占。

结构修正先检查遗物之间替换/目标冲突，再结合实际编成的牌型分支校验：加段数只命中原始及替换后都为 Damage 的步骤，展开结果不能超过 64 步。某个 stepId 在该类别的全部分支均不存在时给出 Warning；允许皇家分支没有普通同花顺的伤害步骤。未装备的遗物不会被任意玩家模板套用。

## 格式、指纹与扩展

数值使用 invariant culture，十进制小数点为 `.`；布尔仅 true/false；枚举使用精确名称，不接受数字或大小写猜测。未知元素/属性、无效或不适用字段、重复 ID、断引用全部报错。只允许数据属性和空白正文；注释可用。读取上限为 1,048,576 字符、32 层、20,000 元素、每元素 64 属性；禁止 DTD、外部实体、命名空间、处理指令，不加载文件或网络资源。

编译结果提供 SchemaVersion、RulesContractVersion、包 Id/Version 和 SHA-256 Fingerprint。指纹忽略注释、排版空白、属性顺序、name/explanation，以及根章节和独立定义排列顺序；保留效果、阵容、牌组、构筑等子项的语义顺序。缺省字段与显式默认值、等值数字的不同写法仍可能产生不同指纹，本期不承诺完全语义归一化。它用于内容包身份，不能替代已有对局快照/绑定指纹；未改动旧存档 schema 或旧绑定算法。

需要新增机制时，先扩展规则类型、编译白名单、校验和测试，再更新内容契约；不能靠配置一个未实现名称获得新行为。旧 SO 继续保留，Unity 宿主通过显式来源选择配置入口。外部 HTML 配置工具已在 [Tools/BattleContentEditor](../../../../Tools/BattleContentEditor/README.md) 独立提供，通过宿主调用本编译器，规则包不依赖 HTML 或 HTTP。XSD/表单元数据生成、批量文件交换、固定场景命令脚本/断言和快照迁移仍未实现。

当前规则契约为 battle-content.5、Combat 为 combat.12（XML schemaVersion 仍为 1），包含手动摸牌/分阶段弃牌、计分遗物、葫芦分项、叠层流血和逐段转火。旧绑定/规则快照不自动迁移。

## 可配置怪物（battle-content.4）

行为阶段、条件、锁定意图、召唤、生成卡牌和护甲见 [EnemyBehavior.md](EnemyBehavior.md)。旧固定敌人定义保留支持，新模板在 `Engine/Content/Templates/07—09`。


## revision 1582 / battle-content.5

标准 XML 和新增字段、遗物边界、召唤归属、狂暴及意图预告约定见 [标准局内规则](../../../Content/README.md#revision-1582-扩展)。Combat 契约升级为 `combat.12`，新增卡牌边界事实；核心默认牌型仍保持传统扑克五张同花，标准对局通过配置启用短同花。

### 逐张攻击、命中附加状态与同花叠加

- `Effect.perSubmittedCard="true"`：按实际提交顺序展开（规则层使用手牌顺序），每张执行一次；未设置时只执行一次。展开后的首段沿用原 id，后续为 `id/card/2` 等，AddHits 仍按精确 stepId 匹配。总展开上限 64，超限回滚。
- `amountKind="CardScoreScale"`：限逐张牌型效果，读取本牌计分筹码乘权威伤害牌型倍率，再乘 amount；全局筹码加成只加入首段一次。各段向下取整。
- Damage 可包含一个 `<OnHitStatus statusRef="bleed" stacks="1"/>`。命中后立即施加给受击者，死亡目标跳过；与 retargetChain、AOE、AddHits 配合。将伤害整体 ReplaceEffect 替换时，是否附带状态由替换效果决定。
- ApplyStatus / OnHitStatus 的层数为 `stacks + stacksPerSubmittedCard × 实际出牌张数`。默认 stacks=1、stacksPerSubmittedCard=0；两者非负且不能同时为零。按出牌张数加层仅供牌型效果、持有手牌事实的 FactTrigger 使用；无手牌事实的触发不执行、不扣预算。敌人自动行为不接受此依赖。
- `HandRule.flushUsesRankEffects="true"`：除皇家同花顺外，伤害独立忽略花色判型、计分和选技能。`FlushBonus` 的 Effect 在符合 `minFlushCards` 的同花出牌后追加；不改变核心传统扑克默认判型。基础伤害牌型与 Flush 条件均可匹配遗物，计分遗物按伤害牌型匹配。
- `FlushBonus` 专用 `amountKind="FlushBaseTotalScale"`：全部有效出牌筹码 × 同花一级基础倍率 × amount，标准 amount=0.2。与牌型等级及计分遗物解耦，仍接受最终治疗数值修正。
- `CardDuelEngine.PreviewHandFacts` 与实际出牌共用权威手牌事实，供预览、选敌和同花条件展示使用；查询不改变状态。
