# PokerEngine

共享 C# 卡牌基础层，以及主线卡牌战斗、独立分数挑战、德州和 Run 构筑规则模块。Unity 只负责宿主与表现；这些 Runtime 程序集均不引用 UnityEngine，不访问文件、网络或时钟。

生命战单位直接持有 Health/MaxHealth/Block，不依赖 ResourceState；玩家行动能量属于玩家玩法状态，筹码留在使用筹码的玩法中。公共战斗单位及最小牌型生命战见 [Combat README](Runtime/Combat/README.md)。主线 卡牌战斗（SingleBattle） 通过 Composition.BattleSession 驱动 CardDuelEngine 组合卡牌计分与阵营战斗；分数挑战（DrawBattle） 使用 DrawEngine，现有 Draw/Run 仍按原分数目标推进。完整主线 Run 场景及结果账本尚未接入。

XML 战斗内容入口：`BattleContentXmlReader` / `BattleContentCompiler` / `BattleContentFactory` 提供带来源位置的安全读取、统一校验、不可变类型化目录与独立 BattleSession 组装。配置边界、字段、触发与示例见 [XML 内容说明](Runtime/Composition/Content/README.md)。现有 SO 与场景入口继续保留，只有调用新入口时才消费 XML。 外部中文配置工具见 [战斗内容工坊](../Tools/BattleContentEditor/README.md)：支持十类表单、引用选择、XML 导入导出、草稿和现有 C# 编译器校验，不依赖 Unity 启动。

## 模块与依赖

```text
Engine/Runtime/
├── Core/          PokerEngine.Core：ID、卡牌/牌区、资源、随机、标准牌组、牌型分类
├── Encounters/    PokerEngine.Encounters：通用战局配置、实例绑定、进度投影与终局摘要
├── Abilities/     PokerEngine.Abilities：内容目录、能力世界、执行/选择/知识/记忆
├── Combat/        PokerEngine.Combat：共用战斗单位、生命/格挡、原子伤害结算
├── Scoring/       PokerEngine.Scoring：加分、倍率、重触发、顺序与计分明细
├── Holdem/        PokerEngine.Holdem：座位、盲注、下注街、摊牌、底池、德州存档
├── Draw/          PokerEngine.Draw：抽牌、选牌、出牌/弃牌、积分目标、独立存档
├── Runs/          PokerEngine.Runs：持久构筑、关卡、奖励、商店、泛型 Run 存档
├── Progression/   PokerEngine.Progression：跨 Run 解锁、局外货币、永久升级、结算与存档
├── Composition/   PokerEngine.Composition：玩法适配器与可执行示例内容
└── link.xml       首包保留上述十个程序集
```

```mermaid
graph TD
    Composition --> Runs
    Composition --> Progression
    Composition --> Combat
    Combat --> Core
    Combat --> Abilities
    Composition --> Encounters
    Encounters --> Core
    Progression --> Core
    Composition --> Holdem
    Composition --> Draw
    Runs --> Abilities
    Runs --> Scoring
    Runs --> Core
    Holdem --> Abilities
    Holdem --> Core
    Draw --> Abilities
    Draw --> Scoring
    Draw --> Core
    Abilities --> Core
    Scoring --> Core
```

箭头表示程序集引用。每个模块有独立 asmdef；离线测试也按此依赖图分别编译 DLL。`Holdem` 不引用 `Draw/Scoring/Runs`，`Draw` 不引用 `Holdem/Runs`，`Runs` 不引用任一具体玩法，基础层不反向引用上层。跨模块组装只发生在 Composition。

公共命名空间继续使用 `PokerEngine`，类型实际分属上述程序集。原 `PokerEngine.Runtime` 与 `MatchEngine` 入口已移除；德州入口明确命名为 `HoldemMatchEngine`。原 `.meta` 随文件移动，已有资产文件 GUID 保留；旧空目录及其元数据一并清理。新增文件及目录均维护元数据；测试和 Editor 测试菜单不进入 Player。

| 职责 | 共享机制 | 模式拥有的政策 |
| --- | --- | --- |
| 卡牌 | 定义、实例、OriginId、牌区、随机洗牌 | 初始牌组、牌区形状、发牌/补牌时机 |
| 牌型 | PokerHandClassifier 识别 1–5 张的牌型及组成牌 | 德州从 5–7 张选最佳五张；Draw 使用玩家提交的 1–5 张 |
| 能力 | 条件、费用、效果、替换、选择、暂停恢复、预算 | 时机/动作键、参与者执行顺序、资源与牌区限制 |
| Combat | 战斗单位、生命、格挡、伤害/治疗事件 | 回合/控制器、牌型换伤害、胜负、Run 写回 |
| 计分 | 有序 Points/Multiplier 变换和明细 | 计分牌子集、何时计分、分数与胜负的关系 |
| Run | 构筑、金币、商品、成长、关卡和账本 | 具体关卡对局、合法行动、玩法存档校验 |
| Encounters | 通用配置、参与者槽位绑定、进度投影与各方终局结果 | 回合/卡牌规则、目标判定、结果生成与具体状态 |
| Progression | 跨 Run 解锁、货币、升级等级、幂等结算、统计和存档 | 解锁条件、奖励公式、升级效果、开局内容过滤 |
| 表现/网络 | 类型化命令和确定性结果 | 宿主认证、玩家视图、动画、I/O、请求收据 |

主线战斗的 `BattleSession` 持有权威 CardDuelState，一次数据命令自动推进敌人队列及收牌到下一玩家决策点或终局，并返回有序、不可变的 `BattleSessionResult/Step`。补牌与洗牌顺序沿用 CardDuelEngine；所有步骤成功才提交。外部只提交 Start / Play / Discard / EndTurn，EnemyAction / CollectPlayed 是内部步骤。Session 不认识动画、场景对象或 Unity 时钟。

`CardDuelTransition.Timeline` 和 `BattleSessionResult.Timeline` 额外提供有序、只读的 `BattleTimelineEntry`，保留原有 Steps / RuleEvents / Events / Draws。CardDuel 的序号属于本步骤，Session 合并为从 0 开始连续的批次 `Sequence`，并标记 `StepIndex`、`ActionId`、`Action`、`Actor` 和已提交选牌。任何步骤失败时，Session 不发布部分事实、不提交部分状态。

时间线在真实结算位置记录 BattleStarted、PhaseStarted、PhaseEnding、PhaseEnded；因终局提前关闭已经进入的阶段时记录 PhaseClosed。BattleEnded 在该命令的规则和卡牌收尾完成后出现一次，开战直接终局不会虚构尚未进入的玩家阶段。稳定边界不要求存在周期状态或遗物，也不额外执行 CombatTiming、不增加权威 Revision 或消耗随机数。ActionStarted 表示接受的行动窗口；`IsActionSkipped` 在该窗口确实被控制跳过时对其所有事实为 true（包括 ActionStarted），并保留单独的 ActionSkipped 与原始跳过原因。不能把 EnemyAction 或 Damage 自动解释成普通攻击。

每个 `entry.State` 是该事实后的完整中间快照，提供单位、状态、能量、遗物、阶段、卡牌区及随机状态；它不是整步最终状态。效果和状态基于原始不可变 RuleEvent 的明确 delta 逐项记录，保留 Provenance、父事件和多段身份。RelicActivated 在触发父事实之后、其子效果之前记录。CardsMoved / CardsDealt / DeckRecycled 提供具体 `Cards`、`CardOwner`、`FromZone`、`ToZone`、`DealTrigger` 与有序牌区快照。各卡牌记录保持既有算法顺序，例如出牌伤害先结算、随后实际移入 Played，再补牌；界面的提前飞行动作不能改写该事实顺序。

这些中间快照只供投影与观察，不能拿来替换 Session 权威状态或提交命令。Revision 仍具有原来的规则含义，多个事实可以共享 Revision；表现顺序使用批次 Sequence。Preview 的临时时间线也不会自动发布给表现端。2D、3D、即时显示或外部前端均通过自己的适配器读取事实，规则程序集不引用任何动作、信号、场景或动画工具。专项验证入口为 `bash Tools/test-poker-engine.sh BattleTimelineTests`。

`BattleHost` 是普通 C# 的通用会话生命周期入口：通过工厂委托创建或重开，接入已有会话，并统一同步表现；不引用 Unity、配置资产或场景类型。

`IBattleSessionPresentation` 是 2D / 3D 共用表现端口：Initialize / Present / IsPresenting / Synchronize。纯 `BattleSessionPlayback` 把命令交给 Session，再把批次派发给端口；本地播放门禁只在此层，不进入 Session。当前 SingleBattleHost 实现 3D 端口，规则状态与展示快照分开；同步和动画完成不触发规则。使用方式和职责见 Unity 主工程中的 SingleBattle 模块说明。

多人战局的出牌转换由各自 `BattlePlayerRules.DamagePolicy` 持有，TeamBattle 按出牌者读取，纯 C# 默认 EqualScoreDamageRule。公共 BattleRules 不再接受出牌伤害参数；旧 ScoredDuelDefinition 适配入口将转换规则转入默认玩家规则。转换协议位于 Composition：`IScoredHandDamageRule.Resolve(ScoredHandDamageContext)` 接收得分、来源和目标策略选出的目标，返回不可变 `DamagePlan`。默认 `EqualScoreDamageRule` 将得分等额作为每个目标的基础伤害。引擎验证来源、目标及规则版本后，调用 `IAttackDamageModifier`，再交给 `CombatEngine` 进行 `IIncomingDamageModifier`、格挡与生命结算；两个修正接口默认原值通过。`DamageModification` 与 CombatEvent 保留各阶段真实输入、输出、版本和说明。攻击修正读取攻击开始时的状态，目标承伤读取该原子批次的当前状态；怪物直接产生基础伤害请求并共用两个修正接口。`CardDuelEngine.PreviewPlay` 复用正式 `Execute`，调用方只读取返回结果而不提交新状态；所有注入策略须遵守纯函数约定。Unity 独立配置与中文只读验算面板见 `Assets/Scripts/Gameplay/SingleBattle/README.md`。

个人牌型等级：`BattlePlayerRules.HandLevels` 持有不可变 `BattleHandLevels`，构造时复制每个 HandCategory 对应的正整数等级；未知/重复类别及非正等级拒绝，缺省类别补 1 级。Unity 在 PlayerBattleProfile 中逐牌型配置并于开战时生成快照，纯 C# 可用 `handLevels:` 注入。CardDuelEngine.PreviewHand 判型后从动作玩家读取对应等级，候选 UI、PreviewPlay 与正式 Play 共用；回合状态继续保留同一玩家 Rules。ScoreProfile 仅保存共享基础筹码、倍率、每级增长及单牌规则，不含个人等级。遗物实例绑定、运行状态、被动数值及牌型效果结构修正已接入，旧计分/主动触发能力与战斗内等级升级尚未启用，旧 Draw/Run 的等级模型不变。

角色伤害规则由 `BattleActorDamageRules` 表达，随各自 `BattlePlayerSetup` / `BattleEnemySetup` 输入，并保留在玩家回合状态与怪物阵容中。TeamBattleEngine 按攻击 Source 选攻击修正、按实际 Target 分派承伤修正，CombatEngine 仍提供该原子批次的最新只读上下文；不把这些规则放入序列化生命状态。`BattleRules` 不再持有攻击/承伤修正，双方 Setup 未指定角色修正规则时构建 Identity 快照。基础 ScaledAttackDamageModifier / ScaledIncomingDamageModifier 位于 Combat，支持倍率及固定增减，默认 1 倍、0 增减。Unity 作者配置位于角色 Profile，公共 BattleRulesProfile 不新增修正槽位。

角色目标策略由 `BattleActorTargetRules` 持有，Attack / Support 分别实现 IBattleTargetPolicy / IBattleAllyTargetPolicy；通过双方 Setup 的 `targetRules:` 输入，玩家回合状态在扣费、结束和恢复能量时保留同一策略快照。默认首个存活对手与自身/指定队友。`OpponentTargetPolicy` 提供首个、最低当前生命、最高当前生命及全部存活对手，生命相同时保持阵容顺序，可配置是否接受显式请求；策略必须纯粹、确定且不读 Unity 可变资产。TeamBattleEngine 按行动 Source 分派策略，仅负责目标合法性（活来源、阵营、存活、非空输出、数量上限、无重复），同时校验请求和输出。Host / TeamBattleEngine / CardDuelEngine 不再接受整场 targetPolicy / allyTargetPolicy；旧注入调用需迁移到各自 Setup 的 BattleActorTargetRules。预览和正式 Execute 共用上述路径。

敌人行动预览：`TeamBattleEngine.PreviewEnemyAction(state, enemy)` 在不可变状态上复用正式 Execute。玩家阶段假定所有可行动玩家立即结束回合，再按敌人队列预演前序行动，保持目标、格挡、存活状态及修正上下文一致；敌人阶段只预演当前剩余队列，不跨入下一轮。返回 `EnemyActionPreview`（来源、原始 revision、Error、逐目标 Hits），Hits 为正式 CombatEvent，包含攻击/承伤报告、SettledDamage、BlockAbsorbed、HealthLost 和 Overkill。SettledDamage 是两层修正后的格挡前伤害；已行动、死亡、终局或前序行动导致该敌人不再有行动则返回 null；解析失败保留错误，不提供猜测值。预览不提交任何状态；确定性纯函数策略和状态变化后重算是与实际结算保持一致的前提。

攻击来源边界：`AttackContext` 携带原始基础伤害、来源类型和可选 `HandAttackInfo`；正式 CardDuel 出牌复用权威分类结果，并在移牌前捕获有效牌面与卡牌效果。SubmittedCards 包含所有提交牌，PatternCards 保留组成牌，ScoringCards 排除其中失效牌。Combat 不反向依赖 Scoring，也不重新识别牌型。攻击与承伤修正共用同一个 AttackContext，各目标共用同一份牌快照；它与实时 CombatState 分开，后者仍按原有阶段时机读取。`DamageModification.Triggers` 仅存储实际返回的触发来源/原因/卡牌范围与引用，不定义具体能力规则。旧无牌来源的数值调用明确标记 Other，不伪造出牌快照。

弃牌收益归属玩家：`BattlePlayerRules.DiscardReward` 持有每位玩家的收益规则，`BattleRules` 不再提供全局收益字段或构造参数。TeamBattle 按弃牌者读取规则，预览与执行共用路径；友方目标可以不同于弃牌者，但不会改变规则来源。现有调用需将 `discardReward:` 参数移到对应的 `BattlePlayerRules`。

弃牌收益与攻击结算独立：`IDiscardRewardRule.Resolve(DiscardRewardContext)` 接收权威弃牌快照和存活友方目标，返回 `DiscardRewardPlan`。默认 NoDiscardRewardRule 产生空计划，不判牌型、不计分、不创建 CombatEvent；有支持效果时仍通过 CombatEngine 原子应用，暂只允许 GainBlock / Heal，禁止改来源、攻击敌方或清空格挡。费用、次数和发牌继续由 TeamBattle / CardDuel 负责。`PreviewDiscard` 直接复用正式 Execute；`DiscardRewardResolution` 保存上下文及返回计划，供配置无关的只读面板投影。

统一效果计划：Composition 的 `BattleEffectPlan` 保存有序、不可变的已解析 `CombatEffect` 批次及来源规则的版本、说明。出牌、敌人攻击或弃牌收益共用 `BattleEffectResolver`；非空计划只调用一次 CombatEngine，空弃牌计划保留同一个 CombatState，不增加其 Revision。`TeamBattleTransition.EffectPlan` 和 `CardDuelTransition.EffectPlan` 提供行动计划；旧伤害／弃牌路径继续提供原 `Damage`、`DiscardReward`。敌方阶段末尾的 ClearBlock 仍是独立维护批次，不混入行动计划。Start / EndTurn / CollectPlayed、纯判型预览及被拒绝命令没有 EffectPlan。费用、牌区、随机与 Session 原子提交语义保持不变。

逐效果声明：通过 `BattlePlayerRules(effectRule: ...)` 注入 `IScoredHandEffectRule`，接收权威的 `ScoredHandEffectContext`（Combat、Source、Score、可选 Hand），返回 `BattleEffectSequence`。每个 `BattleEffectStep` 分别持有 `BattleEffectSpec` 与 `EffectTargetSpec`，可提供稳定语义 Id；前者只定义 Damage / Heal / GainBlock / ApplyStatus / Eliminate，后者独立决定自身、单体或全体目标。引擎先应用动作玩家的遗物结构修正，再通过 `BattleEffectBinder` 展开成已绑定目标的效果，再完成攻击修正、生成 BattleEffectPlan 并结算。未配置 EffectRule 时继续使用原 DamagePolicy；显式同时配置两种规则会拒绝。新路径的 Damage 为 null，读取 EffectPlan 和 RuleEvents；当前默认牌型映射和弃牌收益不变。

`EffectTargetSpec` 的分组为 Self / Opponents / Allies（友方包含自身），选择方式为 Requested（必须指定一个）、FirstLiving、LowestHealth、HighestHealth、AllLiving 或 ActorPolicy。固定 AllLiving 取全部存活目标；ActorPolicy 则复用来源角色的 Attack / Support 策略及其旧请求行为。固定选择器以阵容顺序打破平局。Requested / ActorPolicy 可声明具名 RequestSlot，命令用不可变的 `BattleTargetBindings` 分别提供目标，例如攻击敌人使用 `attack`、治疗队友使用 `support`。未声明槽位名时使用 `default`；新规则下旧 targets 参数映射到该槽位，不能与非空 targetBindings 同时提交。缺少必需请求、未使用槽位、错误阵营、死亡或重复目标都会拒绝。targetBindings 只用于配置了 EffectRule 的出牌；CardDuel、Session 与 Playback 的执行及预览入口均透传，自动敌人行动不会继承玩家的绑定。

```csharp
// 在 IScoredHandEffectRule.Resolve 中声明两个彼此独立的目标。
return new BattleEffectSequence(Version, "伤害指定敌人并治疗指定队友", new[] {
    new BattleEffectStep(BattleEffectSpec.Damage(context.Score.Total),
        EffectTargetSpec.Opponents(EffectTargetSelection.Requested, "attack")),
    new BattleEffectStep(BattleEffectSpec.Heal(5),
        EffectTargetSpec.Allies(EffectTargetSelection.Requested, "support"))
});
// 改为 AOE 只替换 Target 为 Opponents(AllLiving) / Allies(AllLiving)，基础操作不变。
```

所有步骤先按同一个行动开始快照解析存活目标，按“步骤顺序、目标顺序”展开，最多 64 个效果（包含结算时跳过的效果）；数值对每个目标完整应用，不自动分摊。攻击侧修正仍读取行动开始状态，承伤侧修正逐条读取批次内更新状态。结算时已死亡的目标直接跳过，不重新选目标、不产生事件、不调用承伤修正；后续其他目标和效果继续执行。例如伤害致死后施加状态，只跳过该目标的状态，不撤销致死伤害。事件 Index 保留原展开效果的位置，可有缺口。一次出牌只付一次费用；未知单位、初始死亡来源、溢出等真正错误仍回滚整次行动。来源在结算中死亡则取消其剩余主动效果，保留已经结算的结果。

ApplyStatus 使用 `StatusApplicationSpec` 声明定义、层数和期限，由引擎为每个目标生成 StatusInstance；新实例 ID 由战斗 ID、行动 Revision、步骤及目标序号确定，预览与正式执行一致。实例 ID 与状态容量在实际施加时校验，被跳过或免疫拦截的状态不占位。来源和伤害的 Hand 快照由引擎附加，规则不能替换。状态只存放在 CombatState.Statuses。Cleanse 移除目标上标记 Negative 或 Control 的全部状态实例，保留增益；每次移除产生带 Cleansed 原因的状态事实，不伪造伤害或治疗。Eliminate 直接归零生命并独立报告死因，不计算伤害；ClearBlock 仍只作为内部维护操作。实际结算事实通过 `RuleEvents` 贯穿结算与 Team/Card 结果，旧 `Events` 保留生命／格挡事件投影。契约与 combat.9 版本影响见 [Combat README](Runtime/Combat/README.md)。

通过 `BattleRules(reactionProvider: ..., reactionLimits: ...)` 可接入纯 `ICombatReactionProvider`。出牌、敌人行动、弃牌奖励和清盾均使用同一配置；派生伤害仍按目标应用承伤修正，队列全部完成后再判断胜负和阶段。目录中的已施加触发状态自动执行，外部 provider 在其后补充；未施加触发状态时，原牌型效果和弃牌收益保持原样。主效果逐条执行，每条后立即结算其反应及显式允许的子反应，所有效果共用原子 Combat 提交；预览复用同一流程。事件 Provenance 提供父事实与触发来源，CombatEvent.Overheal 只记录实际接受治疗的溢出。无效反应和预算超限分别透传为 CardDuelError.InvalidReaction / ReactionLimitExceeded，失败仍回滚牌区、能量、弃牌次数、随机状态和计分。盾反、吸血、溢出治疗转伤害已可通过状态行为配置；流血／中毒通过 `PeriodicDamageSpec` 接入行动后／所属阵营阶段末时钟，复用护盾、承伤与死亡结算，按有效机会消费次数，不做层数衰减。未施加周期状态时不生成额外时钟事实；遗物获取／装备流程仍未接入。

`ReflectBlockedDamageSpec` 按护盾实际吸收量反伤，`LifeStealSpec` 按实际生命伤害治疗，`OverhealDamageSpec` 把实际溢出治疗转成伤害；均按倍率乘状态层数后向下取整。状态持有者由 StatusInstance.Target 确定，目标通过独立的 CombatTargetSpec 配置自身、事件端点或敌友方单体／AOE，并读取当前结算快照。一次 AOE 激活只消费一次状态次数，无正数效果或无目标不消费；未配置默认行为保持不变。具体连锁、归因、致死盾反开关与配置示例见 [Combat README](Runtime/Combat/README.md#盾反吸血与溢出治疗转伤害)。

状态定义通过 `BattleRules(statusCatalog: ...)` 注入不可变 `CombatStatusCatalog`，包含标签、强类型行为、默认期限与重复施加策略；状态 ID 本身没有行为含义。`BlockActionWindowSpec` 阻止一次个人行动窗口（玩家本阶段剩余操作／敌人队列中的一次行动）；`PreventStatusApplicationSpec(StatusTags.Control)` 按标签拦截控制施加。免疫读批次内最新状态，拦截是正常结果，不撤销伤害或后续效果，也不清除已有控制。默认 KeepLonger 对同目标同定义保留一个实例，期限、层数取较大值而非相加，实例 ID 保留、Source 更新；也可显式选择 IndependentInstances。显式目录拒绝未知定义，省略目录则保留旧的无行为状态存储方式。

```csharp
var stun = new StatusDefinitionId("stun");
var immunity = new StatusDefinitionId("control-immunity");
var statuses = new CombatStatusCatalog("battle-statuses.1", new[] {
    new CombatStatusDefinition(stun, StatusTags.Control,
        new StatusBehaviorSpec[] { new BlockActionWindowSpec() }, StatusLifetimeSpec.ForTriggers(1)),
    new CombatStatusDefinition(immunity, StatusTags.None,
        new StatusBehaviorSpec[] { new PreventStatusApplicationSpec(StatusTags.Control) },
        StatusLifetimeSpec.ThroughNextPhase(BattleStatusTiming.EnemyPhase))
});
var rules = new BattleRules("battle.1", 20, statusCatalog: statuses);
// 在出牌或敌人规则中使用定义默认期限，目标仍独立配置。
var protection = new BattleEffectStep(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(immunity)), EffectTargetSpec.Self());
```

`StatusApplicationSpec(id, stacks)` 使用定义默认期限；旧的显式 StatusDuration 构造保留，`WithLifetime` 可覆盖相对期限。`BattleStatusTiming` 将 ThroughNextPhase / ThroughCurrentRound 绑定到本场的 `battleId/round/0000000001` 与 player / enemy 阶段键。玩家阶段施加 ThroughNextPhase("enemy") 覆盖本轮全部敌人；敌方阶段中施加则覆盖下一轮敌方阶段。重复施加比较同种期限，不兼容的期限种类拒绝。ForTriggers 仅在该状态实际阻止行动或成功拦截状态时消费；默认计次期限含多种消费行为的配置拒绝，防止一次事件含糊地扣多次。

`TeamBattleState.CanAct` 只查询，不消耗状态；`NextBlockedPlayer` 标识待自动跳过的玩家。TeamBattle 的内部 SkipPlayerTurn 消费眩晕并清零本阶段能量、标记 HasEnded，不扣牌或弃牌次数；敌人 EnemyAction 被阻止时同样正常出队。BattleSession 自动驱动这些步骤，直到有玩家可决策或终局，全员被晕也不会卡住。每个新玩家阶段先执行既有能量恢复与补牌，再自动跳过被控玩家；不能把这些边界折叠成一次最终 Phase 判断。主动 Play / Discard / EndTurn 被控时拒绝，不靠点击消费眩晕。敌方意图预览复用跳过逻辑，EnemyActionPreview 的 RuleEvents / IsSkipped 保留原因，Hits 仍只含伤害。

阶段末与轮次末分别调用 CombatStatusLifecycle.EndPhase / EndRound，精确匹配到期边界；最后敌人被晕也执行收尾。维护不要求状态来源或目标存活，无状态变化不增加 Combat.Revision；不会借用另一份 AbilityWorld。新增刷新、拦截、消费、到期及行动跳过事实，生命周期事件 Effect 为 null、Index 为 -1；效果事件 Index 仍指原计划位置，SequenceIndex 与 Revision 共同标识实际事实，兼容一次效果产生多个状态事实。

敌人可通过 `BattleEnemySetup(effectRule: ...)` 注入纯 `IEnemyBattleEffectRule`，返回同一 BattleEffectSequence，伤害仍标记 EnemyAttack 并复用双方修正。未配置时保持固定伤害。所有默认牌型与弃牌配置、表现端和场景保持原样；本层提供可配置行为与规则事件。

### 文档牌型效果配置

`PokerHandEffectRule` 是可直接注入 `BattlePlayerRules.EffectRule` 的正式牌型规则，复用上述效果计划、逐效果目标、伤害修正和状态生命周期。其不可变 `PokerHandEffectProfile` 保存版本、每个伤害牌型的数值、同花回血系数、眩晕次数与免控期限。文档尚未填写倍率，因此所有伤害项和回血系数必须显式提供，没有内置平衡数值。

| 牌型 | 基础效果 |
| --- | --- |
| 高牌、一对、两对 | 单体一段伤害，各自独立系数 |
| 顺子 | 单体多段伤害，配置 2–64 段及每段系数；附加流血／中毒属于后续遗物机制 |
| 三条、四条 | 全部存活敌人各受一段伤害，各自独立系数，不分摊 |
| 葫芦 | 敌方单体眩晕＋自身净化已有负面/控制状态＋免控，不附加伤害 |
| 同花 | 单体一段伤害＋自身按筹码回血，不读取实际伤害或敌方数量 |
| 皇家同花顺 | 直接消灭全部存活敌人，绕过伤害修正与护盾 |
| 普通同花顺、五条 | 文档缺项的兼容策略：各自配置的单体一段伤害，不获得皇家消灭效果 |

每段基础伤害为 `floor(Score.Total × ScoreFactorPerHit)`，每段再独立经过既有攻击／承伤修正和护盾结算；顺子不把一个总量隐式均分，固定增伤也会按段重复生效。同花回血为 `floor(Score.Points × FlushHealPerChip)`，再由 Combat 限制到最大生命；当前 Points 仍包含既有牌型基础筹码，本次不修改计分口径。数值计算溢出由原行动管线拒绝并回滚牌区、分数、能量和战斗状态。

单体目标默认选择首个存活敌人，也可向规则构造传入 Opponents(Requested / LowestHealth / HighestHealth)，继续复用具名目标槽位。不接受可能返回多人的 ActorPolicy 或 AllLiving 作为单体策略；三条／四条和皇家仍独立声明 AllLiving，同花治疗与葫芦免控独立声明 Self。皇家只依据权威 Hand 分类中的 StraightFlush 与 Ace 高顺识别；同花顺缺失 Hand 快照时拒绝，A2345 同花顺不会被认作皇家。

下面的数值仅用于说明接入方式，不是策划定稿：

```csharp
var damage = new Dictionary<HandCategory, PokerHandDamageSettings> {
    { HandCategory.HighCard, new PokerHandDamageSettings(1m) },
    { HandCategory.OnePair, new PokerHandDamageSettings(1m) },
    { HandCategory.TwoPair, new PokerHandDamageSettings(1m) },
    { HandCategory.ThreeOfAKind, new PokerHandDamageSettings(1m) },
    { HandCategory.Straight, new PokerHandDamageSettings(0.2m, hitCount: 5) },
    { HandCategory.Flush, new PokerHandDamageSettings(1m) },
    { HandCategory.FourOfAKind, new PokerHandDamageSettings(1m) },
    { HandCategory.StraightFlush, new PokerHandDamageSettings(1m) },
    { HandCategory.FiveOfAKind, new PokerHandDamageSettings(1m) }
};
var content = new PokerHandEffectProfile("hand-skills.example.1", damage,
    flushHealPerChip: 0.1m, stunActionWindows: 1);
var battle = new BattleRules("battle.example.1", 20,
    statusCatalog: content.CreateStatusCatalog("hand-statuses.example.1"));
var player = new BattlePlayerRules(effectRule: new PokerHandEffectRule(content));
// 手动选敌时：new PokerHandEffectRule(content,
//     EffectTargetSpec.Opponents(EffectTargetSelection.Requested, "attack"))
// 命令用 BattleTargetBindings("attack", targets) 提交；自动 AOE 牌型不提交该槽位。
```

配置必须恰好包含所有九个非葫芦牌型，拒绝缺项、重复、额外葫芦项、负数和不符牌型的段数；字典防御性复制。眩晕默认消费一次行动窗口，免控默认覆盖下一完整敌方阶段，可通过 `stunActionWindows` / `controlImmunityLifetime` 覆盖；规则每次施加显式携带该配置期限。两个状态 ID 可自定义，`CreateStatusCatalog` 也可合并其它状态定义。`IBattleStatusCatalogValidator` 允许内容规则在 TeamBattle 创建时校验状态依赖，防止缺目录或错误行为导致控制仅被存储而不生效；此规则要求两个状态具有正确行为和 KeepLonger 重施策略。

只有显式注入该规则的玩家启用这份映射，未配置玩家仍保留既有 DamagePolicy。此接入不更改牌型等级、计分公式、弃牌收益、费用、补牌或表现端；遗物触发、持续伤害、盾反、召唤和废牌仍不在本次映射范围。

## 通用基础层

`CardDefinition` 为静态定义；`CardInstance` 为当前对局实体，`OriginId` 关联 Run 中的持久牌。复制牌产生新持久 ID 并继承强化，下一关重新创建实例牌。`StandardDeck` 提供标准 52 张牌，不依赖德州模块。

`CardInstance.Effects` 使用 `[Flags] CardEffects`：None=0、Enhanced=1<<0（强化）、Laminated=1<<1（塑封）、Ineffective=1<<2（失效）。不同效果可以共存；重复添加相同效果不会增加层数。强化/塑封在引擎中仍是语义标记，不自动赋予数值或材质；Unity 的 CardPresentation 通过玩法侧 CardStateArtBindings 将可见状态映射为美术组合 ID，再由 CardArtProfile 合成通用 Shader 参数；火漆、镭射、压暗和红叉只是当前默认映射，Core 不引用美术配置。已有 DrawCard/RunCard 的数值强化仍由对应模块管理，本轮不增加 Run 商店选项或跨关效果同步。

`IsDisabled` 独立于效果：卡牌层的 CanPlay / CanDiscard 都等于 !IsDisabled；CanScore 只受 Ineffective 影响。花色/点数继续从 DefinitionId 对应的 CardDefinition 获取。悬停、选中、观察者可见性和 Unity 颜色不进入 Core 卡牌或权威存档。

```csharp
// 不可变操作；返回的新实例牌由宿主写入下一份状态，身份/定义/OriginId 保持不变。
var next = card.AddEffects(CardEffects.Enhanced | CardEffects.Laminated);
next = next.AddEffects(CardEffects.Ineffective).WithDisabled(true);
next = next.RemoveEffects(CardEffects.Ineffective); // 强化/塑封保留，禁用仍生效
```

WithEffects / AddEffects / RemoveEffects / WithDisabled 返回新实例；未知位（含负数掩码）在构造和存档校验时拒绝。DeckEntry 可携带 Effects / IsDisabled，CardDuel 创建也保留这些属性与 Run 来源，玩家出/弃拒绝禁用牌；德州初始化和洗牌后重分配 ID 会保留这些属性。任意 CardZone 转移接口仍仅处理成员/顺序；Draw 的打出与丢弃命令统一检查所选牌的卡牌权限，在转移前原子拒绝包含禁用牌的请求。能力强制移牌不被这项玩家操作限制隐式禁止。

ScoringCard 携带 Effects。失效牌仍进入完整牌型判定，其逐牌点数、加分/加倍率/乘倍率、重触发及逐牌遗物计分都跳过；牌型基础分、等级和 BeforeCards / AfterCards 的整手效果照常结算。Draw 与 Holdem 的 Run 计分适配器均传递实例牌效果；两种玩法的牌型分类器不因失效而过滤卡牌。

`CardZone.Cards` 是唯一位置事实，Deck 最后一个元素为牌顶。牌区 Kind 使用稳定字符串：通用 `CardZoneKinds` 包含 Deck/Hand/Played/Discard/Removed；德州专属 HoleCards/Board/Burn 在 `HoldemZoneKinds`。基础层允许可选控制者，具体玩法校验控制者及区域数量，不把“两张底牌”写进共享模型。

ID 为区分大小写的非空字符串值类型；状态没有公开 setter，输入集合防御性复制。`Chips` 为带检查的非负 long，Energy 为非负整数，Health 可空；积分、下注筹码和商店金币没有隐式转换。

`HmacDeckShuffler` 使用版本化 HMAC-SHA256 计数器、拒绝采样和 Fisher–Yates；RandomState 保存秘密种子与计数器。相同输入产生相同输出，拒绝命令不消耗外部随机流。默认安全生成种子，`FromSeed` 供测试/回放。快照和随机状态只存在于权威端。

### 有序牌区操作

`Core/Cards/CardZoneOperations.cs` 提供 `CardZone` 扩展方法，可用于任意 Kind 的有序牌区。单区操作返回 `CardZone`；跨区操作返回 `CardZoneTransfer { Source, Target, Cards }`，其中 Cards 为实际转移顺序。原区域和输入集合均不修改，成功返回后由调用者将结果写入新的完整对局状态。

| 接口 | 语义 |
| --- | --- |
| `zone.Insert(index, card)` / `InsertRange(index, cards)` | 在 index 前插入；index 等于 Count 时追加，保留传入顺序 |
| `zone.Remove(card)` / `RemoveRange(cards)` | 按 ID 移除；所有指定牌必须存在，剩余牌保持相对顺序 |
| `zone.RemoveAt(index)` | 移除指定位置的牌 |
| `zone.Move(fromIndex, toIndex)` | 区域内移动一张牌，toIndex 是移动后的最终索引 |
| `zone.Swap(firstIndex, secondIndex)` | 交换两个位置 |
| `zone.Reorder(cards)` | 提交全量排列，必须恰好包含原有全部牌一次 |
| `zone.SortBySuit(resolveDefinition)` | 黑桃 → 红桃 → 梅花 → 方块，组内按 A、K、Q、J、10…2 |
| `zone.SortByRank(resolveDefinition)` | A、K、Q、J、10…2，同点数按黑桃 → 红桃 → 梅花 → 方块 |
| `source.TransferTo(target, card, targetIndex)` | 跨区转移一张牌到指定位置 |
| `source.TransferRangeTo(target, cards, targetIndex)` | 按传入顺序转移多张牌；来源中未选中的牌保持相对顺序 |
| `source.DrawTo(target, count, targetIndex)` | 从来源末尾依次取出 count 张，按抽取顺序插入目标区域 |

所有索引从 0 开始。插入/转移目标位置范围为 `[0, Count]`；Move/Swap/RemoveAt 为 `[0, Count)`。例如 `[A,B,C,D].Move(0,3)` 得到 `[B,C,D,A]`。从 `[A,B,C,D]` 抽取两张会返回 `[D,C]`，剩余 `[A,B]`。

```csharp
hand = hand.Move(0, hand.Cards.Count - 1);

var transfer = deck.DrawTo(hand, 2, hand.Cards.Count);
// 在同一次引擎命令内，将两个结果一起提交到新状态。
deck = transfer.Source;
hand = transfer.Target;
// transfer.Cards 可用于构造发牌事件；事件可见性仍由玩法/宿主过滤。

// Zone 仅持有实例牌 ID，由调用者从卡牌实例及内容目录解析固有花色/点数。
Func<CardInstanceId, CardDefinition> resolveDefinition = id => definitions[instances[id].DefinitionId];
hand = hand.SortBySuit(resolveDefinition);
hand = hand.SortByRank(resolveDefinition);
```

两种排序均为一次性操作，返回新的有序 Zone，不设置持续自动排序策略。A 固定排在 K 前，不受 A2345 的牌型判定规则影响；花色次序仅为排列约定，不代表玩法大小。相同花色/点数的不同实例牌保持原有相对顺序，不按 ID 或效果再次打散。解析器每张牌调用一次，可显式返回 null 表示无标准花色/点数的牌，这些牌按原顺序放在末尾；缺失定义等错误应抛出异常，整次排序不提交。排序保留 Zone 元数据及全部实例牌身份，不修改卡牌属性或消耗随机数；宿主提交新列表后由 View 消费重排。

空批次、零张抽牌和同位置移动/交换允许不改变状态，但仍校验参数。负数/越界、无效/重复 ID、移除缺失牌、抽牌不足、非全量重排、源目标同 ID 或两区已有重叠牌均抛出 `ArgumentException`（含其派生异常），不返回部分结果。区域 ID、Kind、ControllerId 保持不变。CardZone 构造时也检查本区 ID 有效且不重复；不同实例牌仍可使用相同牌面定义。

跨区操作会同时生成两个结果，但不替调用者提交状态。单独 Insert/Remove 仅编辑区域成员，不创建或销毁 CardInstance，也不检查第三个区域；宿主/玩法仍须验证全局“每张实例牌恰有一个位置”、实例存在性及自己的容量、归属、阶段规则。核心接口不是客户端直接修改权威状态的命令。

能力的移牌/抽牌/重排、德州轮流发底牌/烧牌/公共牌、Draw 出牌/弃牌/补牌都复用此层。洗牌继续使用 `IDeckShuffler`，可将其结果交给 Reorder 并一起保存新的 RandomState；有序列表操作本身不消耗随机数、不决定动画或 UI 选中状态。现有快照字段和 schema 无变化。

## 共用能力层

`AbilityContext.State` 是 `AbilityWorld`，不再是 MatchState。它只包含参与者及资源、牌/牌区、能力、状态、决策、知识，以及模式给出的 ScopeId、PhaseKey、CurrentActorId、能力窗口。参与者列表的顺序就是模式选择的执行顺序；德州适配器提供庄家起算的座位顺序，Draw 提供单玩家顺序。

`AbilityDefinition` 的窗口和 RequiredAction 是稳定字符串键。`HoldemTiming/HoldemActionKeys` 在德州模块，`DrawTimings` 在 Draw 模块；新增模式不用修改能力层枚举。模式负责验证这些键与自身实际阶段一致。

`AbilityRuntime` 注册代码，`AbilityExecutor` 执行相同的主动/触发/选择逻辑：

- `Triggers(world)`：优先级 → 参与者顺序 → AbilityInstance.Order → ID；每个窗口每个触发器只访问一次。
- `Activate(world, windowId, abilityId, actor)`：检查所有权与资格，支付修改后的费用，消耗次数与冷却，然后执行提案。
- `Select(world, decisionId, actor, keys, skip, timeout)`：从已保存步骤继续，恢复挂起的 ModeAction 决策，不重新扣除费用。
- 返回 `AbilityExecutionResult { World, Events }`，不写外部状态。模式在应用结果后再次验证自身规则；失败整个命令回滚。

支持移牌、抽牌、排序、资源、添加/消费状态、计数、知识和请求选择；牌面/费用修正、效果替换及强类型 `AbilityOutcomeHandler.For<T>` 结果修正复用同一套注册与排序。没有 object 效果载荷或反射调用行为。能力处理器必须纯粹、确定、无外部副作用。

一次提案最多 64 条，RequestSelection 必须是最后一项；每窗口最多 256 个实际效果步骤。ProcessedTriggers、StepsUsed、选择候选、Continuation 和 SuspendedDecision 进入快照。选择前已经执行的步骤不重放；`Resume` 重新验证目标并只规划后半段。费用不足的自动触发被跳过。

`MemoryScope.Round/Encounter`、`UsesThisRound/CooldownRounds` 与牌局阶段无关。`AbilityLifecycle.EndPhase/EndRound` 清理相应期限，`BeginRound` 重置每轮记忆和使用次数并递减冷却；具体玩法决定何时调用。状态期限使用 ScopeId/PhaseKey，TriggerCount 通过 ConsumeStatusEffect 显式消耗。

`AllowChipChanges`、`AllowCardChanges`、`AllowKnowledgeProjection` 是独立权限。知识包括历史 Snapshot 与绑定牌区的 TrackCardInZone；共享投影只给调用者可见的额外观察，不提供完整 PlayerView。底牌/牌库、候选实体映射、完整 AbilityWorld 均不能直接广播。

## 德州模块

`HoldemMatchEngine` 维持原标准无限注德州行为：2–10 人、盲注、四轮下注、全下、边池、未跟注退款、摊牌和平分余数、换庄、淘汰及下一手。加注规则包括短全下及累计短全下重开。

`StandardHoldem`：固定标准 52 张牌，无能力。`AbilityHoldem`：已注册能力和可重复牌面的自定义牌组，规则版本 2。两者固定两张底牌、五张公共牌及烧牌；自定义牌组至少满足底牌 + 8 张后续发牌，最多 512 张。效果完成后不得破坏牌区形状或耗尽剩余发牌量。

```csharp
var catalog = StandardHoldem.Catalog();
var engine = new HoldemMatchEngine(catalog);
// engine.Create(matchId, definition, table, participants, random, abilities)
// engine.Execute(state, BettingCommand / AbilityCommand, authenticatedActor)
// engine.ExecuteSystem(state, StartHandCommand / AdvanceWindowCommand / DecisionTimeoutCommand)
```

RaiseTo 表示本街累计目标；Call 也用于全下跟注。玩家命令匹配 Revision、DecisionId/WindowId 与认证身份；系统命令只由宿主提交。`GetLegalActions` 提供当前玩家可用行动和下注范围。

引擎在 BeforeHand、BeforeBettingAction、AfterBettingAction、AfterBoardDealt、BeforeShowdown、AfterHandSettled 打开窗口。系统推进至玩家决策；BeforeBettingAction 不能被 AdvanceWindow 跳过。结算先支付底池，关闭 AfterHandSettled 才淘汰零筹码玩家并判断整场结束。

`HoldemAbilityAdapter` 将德州状态投影成 AbilityWorld 并应用结果。筹码费用/效果仅限手间，BeforeHand 不能扣光活动玩家筹码。牌面与 HandEvaluation 修正发生在摊牌提交前，最终选中五张的有效牌面冻结到结算。手期限状态保留至下一次发牌清理，供旧 AppliedModifiers 引用；结算后处理器不能重复应用上一手临时修正。基础发牌不经过能力效果替换链。

`TransitionResult.EventsFor(viewer)` 筛选底牌/公开事件；完整 State 和未筛选 Events 是权威数据。MatchSnapshotCodec schema **v5** 校验结构、规则、内容及模式，4 MiB/100,000 对象上限；新增必填的卡牌 Effects / IsDisabled，旧 v1–v4 明确拒绝，无自动迁移。

## 出牌计分模块

`DrawEngine` 拥有独立 DrawState/DrawCommand/DrawDefinition，与德州没有程序集引用关系。规则可配置手牌上限、每关出牌次数、弃牌次数、单次选择 1–5 张及目标分数。

流程为：

```text
Ready → Play → BeforeScoring → 计分 → AfterScoring → Scored → Continue → Ready
           能力选择可暂停/读档恢复                  └→ Won / Lost
Ready → Discard → AfterDiscard → 补牌 → Ready
```

`Execute(state, command, authenticatedActor)` 提交 Play/Discard/ActivateAbility/SelectAbilityTargets/Continue。`ResolveTimeout` 是权威选择超时入口，玩家直接提交 Timeout 会被拒绝。Scored 是完整计分检查点，Continue 才补牌及开始下一轮，让 Run 先采集积分与成长。

牌型识别结果区分 SubmittedCards 和 ScoringCards。默认对子/三条/四条只计组成牌，高牌只计最高一张，其余踢脚牌不贡献点数；德州适配器仍计完整最佳五张。Draw 不自动回收弃牌，手牌和牌库都耗尽则失败。这是独立出牌计分基础模式，不宣称复刻某个商业游戏的完整规则。

Draw 使用相同 AbilityExecutor、选择状态、费用、修正与生命周期。计分前修改牌面，计分后触发效果；计分后选牌中途读档不会重复增加分数或再次扣除出牌次数。DrawSnapshotCodec schema **v2** 固定内容/玩法/计分版本，保存选择、随机、计分明细、阶段和卡牌 Effects / IsDisabled；旧 v1 明确拒绝。DrawCard.Card 与 AbilityWorld.Cards 中的卡牌属性必须一致。

## 主线遗物绑定与运行状态

`BattlePlayerSetup(..., relics: ...)` 接收 `BattleRelicSetup`（`RelicDefinition` + 持久 `RunRelic` + 可选 `BattleRelicRules`）；`CardDuelEngine.Create` 的单人入口也可传 `relics:`。创建后通过 `state.Duel.Player(playerId).Relics` 取得该玩家的不可变 `BattleRelicLoadout`。同定义可有不同实例，每位玩家保持自身装配顺序；实例 ID 在全场唯一，每位玩家最多 32 个，并校验定义的副本上限。

`BattleRelicState` 保存实例/定义/持有者/顺序、入场等级/成长，以及 RemainingCharges、UsesThisRound、CooldownRounds 和 AbilityMemory。定义通过绑定持有，不混入运行状态 JSON。初始导入保留 Run 的次数、冷却和 Encounter 计数；无 AbilityId 的遗物同样保留。运行时 Memory 支持 Round / Encounter 非负计数，暂不支持实体 Marks。所有集合只读，运行状态更新和反序列化重新验证、复制记忆。

```csharp
var equipment = new[] { new BattleRelicSetup(relicDefinition, runRelic) };
var player = new BattlePlayerSetup(playerId, heroDefinition, relics: equipment);
// 将 player 与敌人阵容传入 CardDuelEngine.Create。
var loadout = state.Duel.Player(playerId).Relics;
var json = BattleRelicSnapshotCodec.Serialize(loadout);
var restored = BattleRelicSnapshotCodec.Deserialize(json, loadout);
var persistentRelics = restored.ExportRunRelics();
```

`BattleRelicSnapshotCodec` schema v1 只保存遗物组件，读取时要求持有者、完整有序实例和绑定指纹相符。指纹包含实例身份、入场等级/成长和完整定义配置（含计分参数及 AbilityId），运行时次数/冷却/计数不参与指纹。缺失字段、非法记忆、未知 schema 或配置不符拒绝；沿用 16 MiB / 500,000 对象上限。它不恢复手牌、生命、随机或回合进度，也不证明某个战局 checkpoint：主线完整存档仍需后续将各组件一起保存和验证，不能把读出的遗物组件当作整场回档。

`CardDuelEngine.UpdateRelicRuntime`（以及 TeamBattleEngine 同名入口）供权威规则侧提交完整运行状态，要求当前 Revision，禁止改实例、持有者、定义、顺序及入场等级/成长。成功返回新状态并推进对应 Revision，失败保留原引用；不扣能量、不推进回合、不记录出牌/弃牌动作、不产生战斗事件。它不是玩家命令，BattleSession 仍只接收原有操作。运行状态只由当前 BattlePlayerTurn 持有，出牌、收牌、弃牌、结束、控制跳过、敌方行动和胜负转换均保留它。

绑定本身不自动激活 ScoreEffects / AbilityId，也不计算遗物成长；只有显式配置下述事实或边界触发时，主线才管理其次数、冷却和 Round 计数。未配置遗物修正时，现有牌型效果、目标选择、弃牌收益/费用/次数、补牌和随机流程保持原样。`ExportRunRelics()` 创建新的持久实例，仅导出 Encounter 计数并丢弃 Round 计数/UsesThisRound，不自动写回 Run。旧 Draw/Holdem 的能力安装、成长和存档契约不变。

## 遗物的被动数值修正

`BattleRelicRules` 属于 Composition，作为 `BattleRelicSetup(..., battleRules: ...)` 的可选静态配置；Runs 的 RelicDefinition 不反向引用战斗组装层。每件遗物的数值、效果结构、规则数值、补牌修正与触发条目合计最多 32 条，Id 跨类不可重复；数值条目使用 `RelicNumericModifierSpec`。六种 `BattleNumericStat` 是 DamageDealt / DamageReceived、HealingDone / HealingReceived、BlockGranted / BlockReceived；固定整数增减用 AddFlat，百分比增减用 AddRate（0.2 表示 +20%）。此入口只改变战斗效果数值；效果结构和行动规则分别通过下述专用入口配置。

```csharp
var numeric = new BattleRelicRules("relic.edge.1", new[] {
    new RelicNumericModifierSpec("damage-rate", BattleNumericStat.DamageDealt,
        RelicNumericOperation.AddRate, 0.2m, new[] { CombatEffectOrigin.Primary }),
    new RelicNumericModifierSpec("shield-flat", BattleNumericStat.BlockReceived,
        RelicNumericOperation.AddFlat, 3, new[] { CombatEffectOrigin.Primary })
});
var setup = new BattleRelicSetup(relicDefinition, runRelic, battleRules: numeric);
```

Origins 必填且不能重复，按 Primary / Triggered / Periodic / Reflection / Lifesteal / Overheal 显式选择。可选 HandCategory 条件只允许 Primary，读取权威 HandAttackInfo，同时适用于本次出牌的伤害和治疗；弃牌没有牌型事实，派生效果不继承触发它的牌型。首版不提供任意委托条件、等级成长公式或状态条件。

`IBattleNumericResolver.Resolve(BattleNumericQuery)` 返回 `CombatAmountModification`，读取绑定内不可变配置；每阶段按遗物获得顺序、同件遗物内 Numeric 配置顺序逐项执行 `floor(max(0, 当前量 × max(0, 1 + 比例增减) + 固定增减))`；每一步取整并作为下一步输入。100 基础量、+20%、+30%、+10 得到 166；先 +10 再 +30%、+20% 得到 171。每个存活目标、每段效果独立计算；自疗/自身护盾也依次走来源和接受两阶段。同定义合法副本各自贡献，不因定义 Id 相同去重。

伤害顺序为：现有角色攻方修正 → 来源遗物 → 现有角色承伤修正 → 目标遗物 → 格挡/扣血。治疗和护盾为：来源遗物 → 目标遗物 → 生命上限/护盾累加。Composition 的 RelicCombatAmountModifier 适配 Combat 的通用 ICombatAmountModifier，主动作、弃牌收益和即时/周期派生效果统一走公共队列。ApplyStatus、Cleanse、Eliminate、ClearBlock、空弃牌收益以及已死亡而跳过的效果都不参与数值修正。未绑定遗物的单位返回恒等结果。

`CombatEvent.Effect` 和 `EffectPlan` 保留原始待结算请求（主动伤害已包含既有角色攻方修正）；OutgoingAmountModification / IncomingAmountModification 保存双方贡献与输入输出，空贡献时为 null。`SettledAmount` 是护盾吸收/生命上限截断前的最终量，`SettledDamage` 仅对伤害返回该值。HealthRestored / Overheal 使用最终治疗量，吸血仍按实际 DamageToHealth 产生，反伤仍按 BlockAbsorbed 产生；新修正不改变原有触发资格和深度限制。不能用原始 Effect.Amount 代替修正后的治疗量或最终承伤。

查询和预览不写状态、不隐式消耗 Charges / UsesThisRound / CooldownRounds / Memory；被动修正也不自动受这些字段限制。消耗次数的“下一次强化”应由后续触发提交机制负责。任何溢出或非法修正输出拒绝整个事务，包括已执行的前序效果、牌区、资源和随机状态。无修正时既有数值/事件语义与状态转换保持不变。

非空数值配置（含版本、条目及条件）扩展绑定指纹；全部配置为空时沿用原有字节布局与指纹。配置本身不写进运行状态 JSON，读档时仍由外部绑定注入；不同配置拒绝读取同一快照。旧 Run / Draw / Holdem 契约不变，Combat 的可选参数默认关闭；当前版本兼容边界见文末“净化与遗物获得顺序”。

## 遗物对牌型效果的结构修正

在 `BattleRelicRules(..., handEffects: ...)` 配置 `RelicHandEffectModifierSpec`。仅对动作玩家的 `IScoredHandEffectRule` 输出执行一次：基础 BattleEffectSequence → IHandEffectModifier / RelicHandEffectModifier → 目标绑定 → 既有角色与双方数值修正 → Combat 队列。敌人、弃牌、即时反应和周期效果不进入结构修正。本入口为被动配置，预览/查询不修改或消费运行状态；下述事实触发的次数和冷却不自动限制这些被动结构修正。

`BattleEffectStep(id, effect, target)` 的 Id 在序列内唯一且不依赖数组下标。PokerHandEffectSteps 提供 DamageHit(1) / DamageHit(2)…（`damage/1`…）、Heal、Stun、Cleanse、ControlImmunity、Eliminate；同花治疗、葫芦的两种状态均可分别定位。原两参数构造保留，未命名步骤正常结算，但不能作为具名修改的目标。改变牌型基础规则时应保留同一语义步骤的 Id。

```csharp
var rules = new BattleRelicRules("relic.hand-shape.1", handEffects: new[] {
    RelicHandEffectModifierSpec.Append("flush-shield", HandCategory.Flush,
        BattleEffectSpec.GainBlock(5), EffectTargetSpec.Self()),
    RelicHandEffectModifierSpec.AddHits("straight-extra", HandCategory.Straight,
        PokerHandEffectSteps.DamageHit(1), 1),
    RelicHandEffectModifierSpec.Replace("flush-convert", HandCategory.Flush,
        PokerHandEffectSteps.Heal, BattleEffectSpec.GainBlock(8)),
    RelicHandEffectModifierSpec.Retarget("full-house-aoe", HandCategory.FullHouse,
        PokerHandEffectSteps.Stun, EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving))
});
var setup = new BattleRelicSetup(relicDefinition, runRelic, rules);
```

- Append 在整条基础序列处理完后追加一个效果，效果和目标必须分别指定；追加眩晕、流血、中毒或其他状态复用 ApplyStatus 与目录中的状态定义。
- AddHits 的 count 是额外段数（1–63），只复制指定的一段基础伤害，紧跟该步骤插入；不会将整条顺子序列再执行一次。该步骤的替换和目标修改先合并，新增段继承合并后的效果与目标，且仍逐段/逐目标参与普通数值修正及触发。
- Replace 只替换指定步骤的 Effect，保留 Id 和 Target；Retarget 只替换 Target，保留效果。两者可组合，替换状态时可配置状态类型、层数和相对时长。AddHits 与非伤害替换冲突会拒绝。
- 所有匹配只看原始牌型与基础步骤。类别不符或没有该具名步骤时不修改（例如皇家同花顺没有 damage/1）；配置拼错步骤 Id 同样不会生效，内容应以具名常量并配测试验证。新增步骤不会再被其他条目匹配。新增 Id 根据遗物实例 Id、条目 Id 和副本编号生成，长度编码避免分隔符碰撞；最终序列仍验证唯一性。
- 多个 AddHits 相加；追加效果与新增段按遗物获得顺序、同件内配置顺序排列。同类别/同一步骤的 Replace 或 Retarget，载荷相同则合并并保留各来源记录，不同则在配置或装配时拒绝，不使用隐式后写覆盖。

`BattleEffectPlan.HandModification` 保存 Input / Output 具名序列，以及 Changes 中的遗物实例、规则、操作、原步骤和结果步骤 Id。没有实际结构修改时该字段为 null，基础 RuleVersion / Explanation 保持不变；完整变化以报告为准。这些序列是目标展开和数值修正之前的声明，真正结算结果仍读取 Effects / RuleEvents。

绑定时校验状态目录依赖；静态配置使用相对 StatusLifetimeSpec（或永久/次数 StatusDuration），拒绝捕获其他战局回合、阶段 scope 的状态配置。旧 DamagePolicy 路径不承载结构修正，绑定非空结构配置但未提供 EffectRule 时在开战前拒绝。目标绑定以修正后的序列校验请求槽，不忽略失效的旧请求。序列及目标展开后仍各限 64 条，结构错误报告 InvalidHandEffectModifier，目标/展开错误沿用 InvalidTarget / InvalidEffectPlan；后续失败依然回滚整次行动，已死亡目标沿用跳过规则。

非空结构配置单独扩展遗物绑定指纹，包含操作、牌型、步骤 Id、额外段数、完整效果/状态/目标载荷及版本。旧空配置与纯数值配置的字节布局、指纹和 schema v1 运行状态快照保持兼容；带结构配置的快照必须用相同外部配置恢复。未配置或未匹配结构修正时保持现有牌型效果和弃牌行为。

## 遗物对能量、费用、手牌和补牌的规则修正

`BattleRelicRules(..., ruleValues: ..., refillRules: ...)` 保存被动规则配置。`RelicRuleValueSpec` 支持 MaxEnergy、InitialEnergy、PlayEnergyCost、DiscardEnergyCost、HandLimit 的固定整数增减；基础值加所有 Delta 后统一校验/截断，不连乘、不逐条截断。贡献按遗物获得顺序及条目配置顺序记录；这些纯整数加法仍在最终边界统一校验。费用最低为 0；有效能量上限至少 1、初始能量至少 0、手牌上限 1–52，非法容量或 int 溢出在创建对局时拒绝。上限与初始能量分别配置，上限 +1 不自动增加初始能量；初始能量允许超过上限，沿用原规则。

```csharp
var rules = new BattleRelicRules("relic.budget.1", ruleValues: new[] {
    new RelicRuleValueSpec("max-energy", BattleRuleValueKey.MaxEnergy, 1),
    new RelicRuleValueSpec("initial-energy", BattleRuleValueKey.InitialEnergy, 1),
    new RelicRuleValueSpec("free-discard", BattleRuleValueKey.DiscardEnergyCost, -1),
    new RelicRuleValueSpec("larger-hand", BattleRuleValueKey.HandLimit, 2)
}, refillRules: new[] {
    new RelicRefillRuleSpec("refill-after-play", CardDuelDealTrigger.AfterPlay, BattleRefillMode.FillToLimit),
    new RelicRefillRuleSpec("opening-count", CardDuelDealTrigger.OpeningTurn, BattleRefillMode.FixedCount, 5)
});
var setup = new BattleRelicSetup(relicDefinition, runRelic, rules);
```

`IBattleRuleResolver` / `BattleRuleResolver` 提供 ResolveLimits、ResolveActionCost、ResolveDeal 三个纯查询。`BattlePlayerTurn.Rules` 保留基础配置；`Limits` 保存开战时计算的有效容量和贡献明细。最大能量写入 PlayerTurnState，后续玩家阶段按该上限恢复；牌组最少张数、开局与各时机补牌使用有效 HandLimit。手牌上限不改变 MaxPlayCards / MaxDiscardCards 的选牌限制，也不自动生成牌。此版不支持中途动态换装或临时容量状态。

`TeamBattleEngine.QueryActionCost(state, actor, action)` 查询出牌/弃牌的当前费用与 CanAfford；只判断资源，不替代行动阶段、控制、选牌和弃牌次数校验。CardDuelEngine、TeamBattleEngine 和规则侧 BattleSessionPlayback 输入预算判断统一调用它，真正扣费由权威状态重算，不接收外部报价。成功出牌/弃牌的 transition.ActionCost 记录基础值、贡献、费用及扣费前能量；结束、跳过和失败没有费用报告。

本次只接规则层。现有 HUD、弃牌提示和审计窗口中的费用/补牌文案仍读取基础 Rules；表现层后续应改为读取查询/报告，避免把基础值当作最终费用或补牌结果。

能量扣至 0 时，只要出牌或弃牌任一种有效费用为 0，就保留玩家行动窗口，允许继续提交免费行动；即使当前无牌或弃牌次数耗尽也不隐式关窗，可主动 EndTurn。免费弃牌仍检查并消耗有限弃牌次数，UnlimitedDiscards 语义不变。主动结束与眩晕 SkipPlayerTurn 总会关闭窗口，免费行动不能绕过控制、已结束状态、Played 收牌边界或修订号。BattleSession 只自动推进既有跳过/敌方/收牌步骤，不自动重复免费操作。没有免费费用时仍按原规则在能量耗尽时自动结束。

`RelicRefillRuleSpec` 指定 OpeningTurn / AfterPlay / AfterDiscard / PlayerTurnStart 中的一个时机。Mode 为 null 时继承现有 `ICardDuelDealPolicy`；Skip 禁止该次补牌，FillToLimit 补至上限，FixedCount 使用明确张数（可为 0）。同一时机相同模式及固定张数可合并；不同模式或不同固定张数在绑定时拒绝，没有最后一件覆盖前一件的规则。不同玩家、不同触发时机各自独立。

补牌顺序为：移出出牌/弃牌 → 用新牌区与有效手牌上限构造 CardDuelDealContext → 校验基础策略输出在 0..MissingCards → 选择基础量或替换模式 → 累加所有 CountDelta → 截断到 0..MissingCards → 抽取牌。Skip 始终为 0，不被加张数重新开启；继承模式可以把基础 0 次补牌加为 1。基础策略非法不会被遗物模式掩盖，返回 InvalidDealPlan；非法规则查询返回 InvalidRuleModifier，溢出沿用 Overflow。

`CardDuelTransition.Draws` 按执行顺序保存 BattleDrawRecord（计划 Plan 与实际 DrawnCount）；计划包含基础张数、模式、所选张数、总增减、上限、缺额和各遗物贡献。牌源不足可少抽，AfterPlay 时 Played 仍不可洗回，只有现有 Discard 牌堆参与洗牌；CollectPlayed 本身不触发补牌。预览复用完整流水线，不修改输入和 RNG。任意后续失败保留原状态且不发布费用/抽牌报告；Session 自动步骤失败也撤销整次用户命令。

新增配置非空时才追加 `battle-relic.rules.1` 指纹段，旧空配置、纯数值和结构配置的指纹保持兼容。schema v1 仍仅存遗物运行状态，读取新配置快照必须提供相同外部绑定。规则查询不消费 Charges / UsesThisRound / CooldownRounds / Memory；首回合优惠、限定次数免单和动态条件留给后续触发提交机制。没有新修正时保留既有牌型效果、弃牌收益/次数及洗牌流程。

## 遗物事实触发与事务提交

`BattleRelicRules(..., triggers: ..., triggerBudget: ...)` 配置事实触发；默认空集合，不隐式安装任何能力或改变牌型、弃牌收益。每个 `RelicTriggerSpec` 有稳定 Id、Timing、Relation、Conditions、Occurrence、Origins、Priority、SourcePolicy 和独立目标的 Effects。配置 Id 与同一遗物的数值、结构、规则修正 Id 共用唯一命名空间，总数不超过 32。

| Timing | 可匹配的实际结算事实 |
| --- | --- |
| DamageSettled | Damage 结算；完全吸收或结算为 0 也属于伤害事实，要求实际扣血需配置阈值 |
| HealingSettled | Heal 结算，区分实际恢复 HealthRestored 与溢出 Overheal |
| BlockGained | GainBlock 实际增加护盾；ClearBlock 不匹配 |
| UnitDied | 本条效果导致死亡，含 Damage 与 Eliminate；不把消灭当成伤害 |
| StatusApplied | 状态成功新增或刷新 |
| StatusBlocked | 状态施加被免疫拦截；到期与消费不匹配 |

Relation 以遗物持有者为基准，支持自身/友方/敌方是来源或目标，以及 Any；友方包含自身。条件全部按 AND 组合，支持实际扣血/吸收/治疗/溢出/护盾阈值、持有者结算后生命比例/已有状态、事件状态定义、牌型、提交牌数、弃牌数。牌型和牌数只来自对应出牌/弃牌命令的权威主效果，派生效果不继承它们；缺失事实不满足条件。本步不包含随机条件或通用脚本表达式。

`RelicTriggeredEffectSpec` 复用五种 BattleEffectSpec，并单独绑定 CombatTargetSpec：自身、事件来源/目标、友方/敌方及首个/最低血/最高血/全部存活目标。每个目标获得完整数值，支持多效果与 AOE，一次激活最多展开 64 条效果。状态复用目录、免疫与生命周期；相对期限在触发时按当前战局绑定，不接受外部固定回合/阶段 scope。来源是遗物持有者，默认要求存活；死亡反应需显式 AllowDefeated，仍不能复活死目标。

同一遗物实例的所有触发共用一个 `RelicTriggerBudgetSpec`：RemainingCharges 按 ChargeCost 扣减，UsesThisRound 按激活次数增加，MaxUsesPerAction / MaxUsesPerRound 可选，CooldownRounds 为共享冷却。没有有限 Charges 则不限制总次数，ChargeCost=0 不消耗有限次数。PerEvent 默认逐事实判断；PerAction 让该触发条目在一次 TeamBattle 命令中最多激活一次，共享行动上限则限制该遗物所有条目总数。玩家与敌人行动各有独立的行动预算；同一整轮的玩家/敌人阶段共用 UsesThisRound。

同一事实的候选先按玩家阵容顺序，再按各玩家遗物获得顺序处理；同件遗物内按 Priority 升序、相同优先级按配置顺序处理。Priority 不跨遗物抢占。每条合格触发先绑定存活目标，再预占预算，后续候选立即读取新预算。同一激活里的多效果、AOE 不重复扣费；无目标或所有数值效果为 0 时不消费。进入队列后，目标后来死亡、满血治疗、状态被免疫或数值修正为 0 都不退款；真正结算错误则回滚整次事务。

冷却 N 表示跳过后续 N 个完整回合。触发时 N>0 写入 N+1，阻止当前回合再触发；每次进入下一玩家回合递减 1、清零 UsesThisRound，并清理 Round 记忆。比如 N=1：本回合写 2，下回合为 1 不触发，再下一回合为 0 可触发。免费行动、预览、CollectPlayed 和开局 Start 不推进冷却；导入冷却不在创建时额外递减。仅配置了触发的遗物参与维护，无触发遗物的既有运行字段保持不变，有限次数不自动补满。

```csharp
// 示例数值：实际受伤后治疗自己 10 点，整轮最多一次，冷却跳过下一整轮。
var trigger = new RelicTriggerSpec("recover", RelicTriggerTiming.DamageSettled,
    RelicEventRelation.OwnerIsTarget,
    new[] { new RelicTriggeredEffectSpec(BattleEffectSpec.Heal(10), CombatTargetSpec.Self()) },
    new[] { RelicTriggerConditionSpec.FactAtLeast(RelicFactAmount.DamageToHealth, 1) });
var battleRules = new BattleRelicRules("recovery.1", triggers: new[] { trigger },
    triggerBudget: new RelicTriggerBudgetSpec(maxUsesPerRound: 1, cooldownRounds: 1));
var setup = new BattleRelicSetup(new RelicDefinition("recovery", Array.Empty<ScoreEffect>(), charges: 3),
    new RunRelic("recovery-instance", "recovery", charges: 3), battleRules);
```

Composition 的 `BattleRelicTriggerResolver` 只生成激活草案；不可变的 RelicReactionRuntime 通过 Combat 的 `ICombatReactionRuntime` 在现有队列内逐事实累计状态。每个父事实仍先收集状态反应、再收集外部 provider，最后收集遗物反应；维持原有深度优先队列、死亡跳过及总条数/深度上限。默认只监听 Primary，派生效果归类 Triggered 且为叶子；要继续触发，发送方须允许 CanTriggerReactions，接收方须显式允许相应 Origins。原有反伤、周期伤害等叶子限制保持不变。派生数值修正使用 Triggered 来源，不重复套用主牌型/主攻击增益。

`TeamBattleTransition.RelicActivations` 与 `CardDuelTransition.RelicActivations` 提供激活 Id、父事实 Revision/SequenceIndex、持有者/实例/条目、预算前后快照及目标已绑定的批次；对应事实的 Provenance.ActivationId 可关联同次激活。原 EffectPlan 仍仅表示主动计划。次数与冷却随完整行动提交；后续数值溢出、补牌失败、触发链超限，以及 Session 后续自动步骤失败，都会撤销生命、护盾、状态、遗物、资源、牌区和 RNG，失败不发布激活报告。预览返回同样的临时结果，不修改原状态。

状态到期尚未提供遗物订阅；默认无收益的弃牌没有虚构效果事实，完成订阅使用下述独立行动入口。周期伤害保持叶子，清盾等阶段维护不进入此事实入口。开战和阶段起止使用下述独立边界入口；限次的被动数值/结构/费用修正仍未接入。

只有非空触发配置才追加 `battle-relic.triggers.1` 指纹段，保留旧空/数值/结构/规则配置指纹。schema v1 继续只存运行状态，反序列化要求同一外部配置绑定，冷却、次数和记忆原样恢复。RelicDefinition 的有限 Charges 不再要求旧 AbilityId，以支持独立的主线触发；旧主动能力与旧计分效果不会因此自动安装。

## 遗物开战与阶段边界触发

`BattleRelicRules(..., boundaryTriggers: ...)` 接受 `RelicBoundaryTriggerSpec`，复用 `RelicTriggeredEffectSpec`、五种基础效果、`CombatTargetSpec` 和同一份 `TriggerBudget`。边界与事实条目共用配置 Id 命名空间；不配置边界时，旧牌型、弃牌、开局发牌和事件行为不变。

| Timing | 规则层发出位置 | Phase 过滤 |
| --- | --- | --- |
| BattleStarted | CardDuel.Start 中、首次玩家阶段与开局发牌之前；成功开战只发出一次，Create 不触发 | 必须为空 |
| PhaseStarted | 进入一方完整阶段后、任何单位行动或控制跳过之前 | OwnerTeam / OpponentTeam / Any |
| PhaseEnding | 一方全部行动/跳过结束，周期伤害结算后，清盾及状态到期之前 | OwnerTeam / OpponentTeam / Any |

边界只接受持有者生命比例、持有者已有状态条件，条件按 AND 组合；不伪造效果来源、目标、牌型或弃牌数量，直接边界效果禁止 EventSource / EventTarget / EventActor。派生出的真实效果仍可被事实触发监听。眩晕不禁止被动触发，持有者默认必须存活；AllowDefeated 显式开放死亡来源，但不复活死亡目标，也不在整场战斗已结束后再开启边界。

结算顺序：

1. 开战：绑定与初始能量 → BattleStarted 完整队列 → 首次玩家 PhaseStarted 完整队列 → 开局发牌 → 控制检查/玩家决策。两窗口之间不重置轮次数或冷却，也不把 InitialEnergy 补成 MaxEnergy。
2. 阶段结束：最后行动与即时反应 → ActionCompleted 周期伤害与遗物完成触发 → PhaseEnding 周期伤害与次数消费 → 遗物 PhaseEnding → 既有清盾 → EndPhase 到期 → 必要时 EndRound 到期 → 终局/轮数上限检查。
3. 敌方阶段开始：切换阶段 → PhaseStarted → 按实际存活者刷新敌人行动队列。下一玩家阶段开始：进入新轮 → 清 UsesThisRound、递减冷却、清 Round 记忆、恢复能量 → PhaseStarted → 收牌/发牌 → 控制检查。

同一个 PhaseEnding 的周期伤害与遗物反应复用一条队列和总条数/深度限制。遗物条件读取周期伤害后的状态；此时新施加的中毒不会在同一边界补触发一次。终局发生后，已经入队的效果按原死亡跳过规则结算完，不开启后续边界或发牌；AfterEnemyPhase 清盾也会清掉该敌方结束边界刚获得的护盾。旧的非边界弃牌结算路径保持兼容。

每次开战、阶段开始、阶段结束都创建独立临时窗口，分别有稳定 `BoundaryId`，不继承最后一次行动的计数。`MaxUsesPerAction` / `PerAction` 只管行动窗口；新增可选 `MaxUsesPerBoundary` 管该遗物在一个边界内直接激活与派生事实激活的总数，事实条目的 `PerBoundary` 则让该条目每个边界最多激活一次、在普通行动中不匹配。`PerEvent` 可在两类窗口内逐事实匹配。每个直接边界条目在该边界只检查一次，多个候选按玩家阵容、遗物获得顺序、同件内 Priority 与配置顺序排列并先预占共享预算，再依既有队列结算。

有限次数、UsesThisRound、MaxUsesPerRound、CooldownRounds 在事实和边界间共享；下一轮维护后才检查玩家开始触发。冷却 N 仍表示跳过后续 N 个完整回合，开战与首个玩家开始之间不递减。只有配置了任一触发的遗物参与轮维护。窗口计数不写存档，schema v1 不变；仅有边界配置、新边界上限或 PerBoundary 时追加 `battle-relic.boundaries.1` 指纹段，旧事实配置指纹不变。

状态相对期限使用当前边界的阶段和新轮 scope 绑定：在阶段开始施加的 `ThroughNextPhase(当前阶段)` 于本阶段末到期；在阶段结束施加的同名期限延续到下一次该阶段结束；BattleStarted 施加的 player/enemy 期限都指向首轮对应阶段。`ThroughCurrentRound` 在新玩家阶段中指新轮，边界的事实派生状态沿用该窗口的期限上下文；原行动施加状态语义保持不变。

```csharp
var opening = new RelicBoundaryTriggerSpec("opening-shield", RelicBoundaryTiming.BattleStarted,
    new[] { new RelicTriggeredEffectSpec(BattleEffectSpec.GainBlock(8), CombatTargetSpec.Self()) });
var phase = new RelicBoundaryTriggerSpec("player-start-heal", RelicBoundaryTiming.PhaseStarted,
    new[] { new RelicTriggeredEffectSpec(BattleEffectSpec.Heal(3), CombatTargetSpec.Self()) },
    phase: RelicPhaseRelation.OwnerTeam);
var rules = new BattleRelicRules("boundary-example.1", boundaryTriggers: new[] { opening, phase },
    triggerBudget: new RelicTriggerBudgetSpec(maxUsesPerBoundary: 1));
```

`RelicTriggerActivation.Timing` 现在可空，直接边界激活使用 `BoundaryTiming`；边界及其事实派生报告携带同一个 BoundaryId，父 Revision/SequenceIndex 和效果 Provenance 仍可追溯。CardDuel.Start 的开战效果与发牌共同提交，TeamBattle 内的行动、阶段结束、阶段开始共同提交；溢出、队列超限、后续补牌或 Session 自动步骤失败时整体回滚。没有新增供客户端任意调用的边界命令，规则层内部依阶段推进发出信号。

## 遗物出牌与弃牌完成触发

`BattleRelicRules(..., actionTriggers: ...)` 接受 `RelicActionTriggerSpec`。`PlayCompleted` / `DiscardCompleted` 表示一次合法操作的战斗效果结算完成：主效果与即时反应 → 原位置扣能量 → 行动后流血及其次数消费 → 完成触发与允许的事实派生 → 阶段结束/到期/推进 → 原牌区移动与自动补牌。此处尚未补牌，也不等待动画。空收益弃牌和零费用行动同样成立；多牌、多段、AOE 都只派发一次。非法输入、主动结束、眩晕跳过、CollectPlayed、敌人行动不触发这两个订阅。

| 配置 | 可用值及语义 |
| --- | --- |
| Timing | PlayCompleted / DiscardCompleted |
| Relation | OwnerIsActor / AllyIsActor（含自身）/ OpponentIsActor / Any；与遗物拥有者比较 |
| Conditions | AND；共同支持 OwnerHealthAtMost、OwnerHasStatus；出牌支持 HandIs、SubmittedCardsAtLeast；弃牌支持 DiscardedCardsAtLeast |
| Effects / Target | 复用五种基础效果；Self、Allies、Opponents、EventActor；每条效果独立选单体/AOE |
| Priority | 仅在同件遗物内升序，同优先级按配置顺序；不同遗物按获得顺序 |
| SourcePolicy / CanTriggerReactions | 默认存活来源、叶子效果；显式开放死亡来源或后继事实触发 |

`EventActor` 是完成该操作的行动者，`Self` 和效果 Source 仍是遗物拥有者。例如队友出牌后给队友加盾，应配 AllyIsActor + EventActor。死亡行动者返回空目标，不改选他人。完成入口没有效果端点，拒绝 EventSource / EventTarget 以及 FactAtLeast / EventStatus；事实、边界、周期状态及即时状态反应配置也拒绝没有上下文的 EventActor。卡牌条件使用权威快照；旧 TeamBattle 调用没有 Hand 时视为未知，不伪造牌型或张数。

完成触发沿用本次行动的不可变 RelicReactionRuntime，主效果的事实触发、完成直接触发及其派生事实共用 MaxUsesPerAction / MaxUsesPerRound / Charges / CooldownRounds；不创建独立边界预算，不适用 MaxUsesPerBoundary / PerBoundary。主效果已经消费的次数不会被高优先级完成触发抢占；PerAction 事实条目先前已触发则不因完成派生再触发。每个直接条目每次操作只检查一次，候选按顺序预占共享预算；所有效果均为零或没有目标时不支付。免费操作不推进冷却，仍只在下一玩家轮维护次数、冷却和 Round 记忆。

完成条件及目标读取流血和次数消费后的快照，新增流血不会在同一时钟再跳一次。完成效果及其派生状态沿用行动开始的相对期限：ThroughCurrentRound 指本轮，玩家行动中的 ThroughNextPhase(enemy) 指本轮敌方阶段末，ThroughNextPhase(player) 指下一轮玩家阶段末。主效果或流血已经终局时不再激活完成触发；完成队列中的死亡跳过保持不变，已入队效果结算完后不启动后续边界或补牌。

```csharp
var discardHeal = new RelicActionTriggerSpec("discard-heal", RelicActionTiming.DiscardCompleted,
    new[] { new RelicTriggeredEffectSpec(BattleEffectSpec.Heal(3), CombatTargetSpec.EventActor()) },
    conditions: new[] { RelicTriggerConditionSpec.DiscardedCardsAtLeast(2) });
var pairShield = new RelicActionTriggerSpec("pair-shield", RelicActionTiming.PlayCompleted,
    new[] { new RelicTriggeredEffectSpec(BattleEffectSpec.GainBlock(5), CombatTargetSpec.Self()) },
    conditions: new[] { RelicTriggerConditionSpec.HandIs(HandCategory.OnePair) });
var allyShield = new RelicActionTriggerSpec("ally-play-shield", RelicActionTiming.PlayCompleted,
    new[] { new RelicTriggeredEffectSpec(BattleEffectSpec.GainBlock(2), CombatTargetSpec.EventActor()) },
    relation: RelicActionRelation.AllyIsActor);
var rules = new BattleRelicRules("completion-example.1", actionTriggers: new[] { discardHeal, pairShield, allyShield },
    triggerBudget: new RelicTriggerBudgetSpec(maxUsesPerAction: 1, maxUsesPerRound: 2));
```

TeamBattleTransition / CardDuelTransition 的 `ActionCompletion` 返回 Composition 层的 Context 和通用时钟 Clock。Context 包含稳定 ActionId、Timing、Actor、RoundScope、Phase、Hand 或 DiscardedCards、推导的 SelectedCardCount、ActionCost、EnergyBefore / EnergyAfter；Clock 提供 Revision / SequenceIndex。只在匹配了完成订阅且通过终局检查时产生报告，未实际激活（条件或预算不满足）仍可有报告。无匹配配置或失败时为 null。时钟先写入事件，再完成周期结算与完成派发，所以 Clock 的位置是因果根，不代表完成效果已经生效。

直接激活报告使用 `ActionTiming`，自身及派生事实携带同一个 ActionId；其他 Timing / BoundaryTiming 仍区分事实和边界。牌型/弃牌事实不会传播到派生事实条件。完成触发、费用、状态、牌区、随机和发牌共同提交；预览不消费真实状态，队列超限、溢出、后续补牌或 Session 自动步骤失败均整次回滚且不发布完成报告。

仅非空 ActionTriggers 才追加 `battle-relic.actions.1` 指纹段，旧配置指纹和 schema v1 运行快照格式保持不变。无完成订阅时不新增时钟、事件或 Revision，既有牌型效果与默认弃牌行为不变。此处仍为规则层配置接口，没有新增表现层或配置编辑器，也没有向客户端开放任意派发完成事件的命令。

## 计分模块

ScoreEngine 不知道下注、底池、胜者、商店或关卡。输入由模式明确选择：牌型、牌型等级、实际计分的 ScoringCard 子集、有序 ScoringRelic。

1. 从 ScoreProfile 取得牌型等级的基础分和基础倍率。
2. 按遗物槽位顺序执行 BeforeCards。
3. 逐张计分牌：点数 + 强化 → 卡牌加倍率/乘倍率 → 遗物 PerCard；重触发重复这一过程。
4. 按遗物顺序执行 AfterCards。
5. `Total = floor(Points × Multiplier)`，最后取整；long 分数、decimal 倍率，检查溢出。

支持零张到 512 张显式计分牌（零张可供纯基础分/遗物规则），最多 32 个遗物、每牌额外 8 次重触发、总计最多 1,024 步。花色/点数/牌型条件及等级/成长系数由 ScoreEffect 表达。

点数分 2–10 按点数、J/Q/K 为 10、A 为 11。`31 × (1+4) × 2 = 310` 与 `31 × (1×2+4) = 186` 不同，故遗物顺序属于持久构筑。每步保存来源、操作量及操作后值，供动画解释。`ScoreEngine.Validate` 重算 trace 验证存档内部一致性，不证明历史行为真实性。

### 单牌点数查询与悬停数据

`Scoring/CardScoreRules.cs` 提供纯 C# `GetBasePoints(Rank)` / `GetBasePoints(CardDefinition)`：2–10 按点数、J/Q/K 为 10、A 为 11。非法 Rank 或空定义直接拒绝。定义重载查询印刷牌面；如果能力改变了牌面，宿主应先解析有效 CardFace，再使用 `ScoringCard` 查询。规则归属 Scoring，通用 Core/CardInstance 不反向依赖计分模块。

`ScoringCard` 补充只读派生属性，UI 与 ScoreEngine 使用相同规则，无需先结算一手：

| 查询 | 含义 |
| --- | --- |
| `BasePoints` | 当前传入 CardFace 的基础点数 |
| `BonusPoints` | 已有的单牌额外点数 |
| `PointsPerTrigger` | 基础 + 额外点数，一次触发的名义点数，checked long 加法 |
| `CanScore` | 是否被失效状态禁止逐牌计分；不代表必然属于本手计分子集 |
| `BonusMultiplier / MultiplierFactor / Retriggers` | 已有倍率修正和额外重触发次数，可单独展示 |

失效牌仍返回原基础/额外/单次点数，CanScore 为 false。PointsPerTrigger 不含倍率、重复触发、遗物、牌型基础分，也不是“这张牌本次最终贡献”；实际贡献以 `ScoreResult.Steps` 为准。禁用控制能否提交，失效控制逐牌计分，两者继续分离。强化/塑封标记本身不产生隐含数值。PointsPerTrigger 溢出会抛出 OverflowException，BasePoints 与 BonusPoints 仍可单独读取；引擎保留先过滤失效、再计算数值的行为。

```csharp
long printedPoints = CardScoreRules.GetBasePoints(definition);

// drawCard 保存该牌的计分修正；effectiveFace 由玩法规则解析。
ScoringCard info = drawCard.WithFace(effectiveFace);
long basePoints = info.BasePoints;          // 例如 A = 11
long bonusPoints = info.BonusPoints;        // 例如 +20
long pointsPerTrigger = info.PointsPerTrigger; // 31，不含倍率或重触发
bool canScore = info.CanScore;              // 失效时 false，仍可显示 11 和 +20
```

未来悬停 UI 由宿主按 CardId 获取该牌及观察者获准查看的数据，构造/读取 ScoringCard；CardView 不计算分数，也不通过图集 ID 反推规则。当前补齐的是查询接口，未添加悬停面板或计分动画。ScoringCard 的派生属性不写入存档，既有 ScoreProfile、快照 schema 和计分结果语义不变；运行时文件与 .meta 继续归入 PokerEngine.Scoring 首包程序集。

是否只有德州获奖者计分、弃牌获胜给多少分，现放在 **HoldemEncounterSettings**。ScoreProfile 已移除 WinnersOnly/FoldWinPoints。默认示例值可以替换，改变语义必须升级对应版本。

## Run 与玩法组装

规则 API 的局内构筑统一称为遗物（`Relic`），使用 `RunRelic`、`RelicDefinition`、`ScoringRelic`、`DrawRelic` 和 `UnlockKind.Relic`。此次仅调整源码命名；为兼容既有存档，`RunJoker` 数据契约名、JSON 的 `Jokers` / `GrowJokers` 字段、`/joker/` 实例 ID 前缀及既有枚举数值、规则版本和指纹保持不变。这些旧线协议名称是刻意保留，不能随 API 批量替换。固定旧版 Run、德州与局外成长快照覆盖读取、继续执行及原格式回写。

Run 负责牌组、遗物槽位/副本数、等级/成长、牌型等级、金币，以及楼层序列、每层统一节点与分支路线。状态、命令和事件通过泛型保持强类型，运行时不 switch 玩法，也不保存任意 JSON/object 代替状态。

### 完整 Run 与多楼层

Unity 策划工具位于独立 `Assets/Scripts/Gameplay/RunFlow` 模块，菜单 `工具 > 关卡流程 > 流程编辑器`。流程保存为 ScriptableObject `RunFlowAsset`，经 `BuildDefinition` / `BuildContent` 转换为本节纯 C# 模型后初始化一次 Run；GameCore 不引用 Unity 或编辑器，Run v4 进度快照与策划资产分开保存。编辑操作与初始化示例见 Unity 主工程中的 RunFlow README。

一次完整肉鸽由有序 `RunDefinition.Floors` 组成，每个 `RunFloorDefinition` 独立保存 Id、Kind（Normal/Boss）和自己的 NodeTree。例如“三层 + Boss 层”对应四棵独立节点树：

```text
Run
├─ 第 1 层：NodeTree 1（普通 / 精英 / 奖励 / 商店分支）
├─ 第 2 层：NodeTree 2（独立分支）
├─ 第 3 层：NodeTree 3（独立分支）
└─ Boss 层：NodeTree 4（Boss 及最终奖励）
```

楼层按配置顺序推进；完成当前树中选定路线的终点后，保存该层路径和完成记录，再自动进入下一层的入口节点。只有最后一层终点完成后 Run 才变成 Completed；任意战斗失败立即变成 Failed，未来层保持未访问状态。层数可配置为 1–32，不写死四层。每层内部允许分支与汇合，NextNodeIds 只能指向同一层中的节点，跨层推进由 RunEngine 统一管理。

```csharp
var definition = new RunDefinition("run.floors.1", ScoreProfile.Default,
    new[] {
        new RunFloorDefinition("floor-1", firstTree),
        new RunFloorDefinition("floor-2", secondTree),
        new RunFloorDefinition("floor-3", thirdTree),
        new RunFloorDefinition("boss-floor", bossTree, RunFloorKind.Boss)
    }, shopPool, rewardPool);
// 可执行示例（两种玩法）：ExampleRunContent.CreateFourFloors(drawMode: true/false)
```

`state.FloorIndex` 是当前层的零基索引；`state.Floors` 保存所有层的 FloorId、Kind、Nodes 和 IsCompleted，`state.Nodes` 是当前层节点进度的便捷访问。之前的层必须全部完成，未来层必须未访问；存档会校验每一层的身份、图版本及指纹，未来层也提前固定。每层可重复使用相同节点 Id、Stage.Id，身份按楼层区分，计分仍使用跨层唯一的战斗索引。

金币、牌组、遗物、等级、成长和积分历史属于整局 Run，换层不重置；只有进入新的战斗节点才重置该战斗的分数、回合预算和模式状态。下一层若从商店或奖励开始，上一场冻结对局会继续保留，直到新战斗启动。`RunEncounterContext.Floor/FloorIndex` 指向当前或最近那场战斗所属的层，因此在跨层后的非战斗节点中可能不同于 `state.FloorIndex`。

旧的 stages 或单 NodeTree 构造仍可用，会包装为一个默认楼层。`RunDefinition.NodeTree` 仅用于单层配置，多层配置必须访问 `definition.Floors[index].NodeTree`；对多层配置读取该兼容属性会拒绝。

### 统一节点与 Node Tree

`RunNodeDefinition` 是所有节点的共同基类，保存 Id、Kind 与有序 NextNodeIds。节点配置不可变；一次完整肉鸽的共享构筑和当前节点进度由 RunState 持有。

| 节点配置 | 内容 |
| --- | --- |
| RunEncounterNodeDefinition | RunStageDefinition（目标分数、回合上限、通关金币、能量）；Kind 为 Encounter / Elite / Boss |
| RunRewardNodeDefinition | 选择一个奖励或跳过；可配置独立 OfferPool |
| RunShopNodeDefinition | 购买、出售、升级、刷新、排序和离开；可配置独立 OfferPool |

`RunNodeTree(version, entryNodeId, nodes)` 支持多后继分支和路径汇合，是有唯一入口的有向无环图。每层最多 500 个节点、100 个战斗节点；拒绝层内重复身份、重复连接、未知后继、自连接、循环和不可达节点。节点可以任意组合，奖励或商店可作为入口或终点；终点成功完成后结束本层，战斗失败则立即结束整局为 Failed。无需在未选择的分支中完成战斗。

完成节点后：无后继 → 下一层入口（最后一层则 Completed）；一个后继 → 自动进入；多个后继 → NodeChoice，等待玩家提交 ChooseNextNode。战斗只在完整计分检查点判定成功或失败；奖励选择/跳过即完成，商店通过 CompleteNode 离开。进入节点时只生成该节点的对局或报价，未选择的分支不消耗随机数、不发奖励、不记分。

```csharp
var tree = new RunNodeTree("route.1", "reward", new RunNodeDefinition[] {
    new RunRewardNodeDefinition("reward", new[] { "normal", "elite" }),
    new RunEncounterNodeDefinition("normal", new RunStageDefinition("normal", 200, 6), new[] { "shop" }),
    new RunEncounterNodeDefinition("elite", new RunStageDefinition("elite", 500, 6, clearGold: 10),
        new[] { "shop" }, RunNodeKind.Elite),
    new RunShopNodeDefinition("shop", new[] { "boss" }),
    new RunEncounterNodeDefinition("boss", new RunStageDefinition("boss", 1000, 8), kind: RunNodeKind.Boss)
});
var definition = new RunDefinition("run.route.1", ScoreProfile.Default, tree, shopPool, rewardPool);
```

`RunAction.StartRun` 由宿主通过 ExecuteSystem 启动入口节点；玩家在奖励节点提交 ChooseReward / SkipReward，在商店节点提交 CompleteNode，在 NodeChoice 中通过 `new RunCommand<DrawCommand>(id, state.Revision, RunAction.ChooseNextNode, nextNodeId: "elite")` 选择下一节点。`engine.GetAvailableNextNodes(state)` 返回当前可选的后继定义，非 NodeChoice 时返回空列表。所有玩家路线命令校验 Revision、认证身份、阶段和实际连接，拒绝时保持原状态与随机数。

每层的 Nodes 保存 TreeVersion、TreeFingerprint、CurrentNodeId、Path 与 CompletedNodeIds。Path 是该层实际进入顺序，CompletedNodeIds 是其中已完成的连续前缀；NodeChoice 中当前节点已经完成。`StageIndex` 是最近进入的战斗在全局内容列表中的位置（按楼层顺序拼接所有 EncounterNodes），不是路线深度，层内可能向较小索引移动；尚未进入任何战斗时为 -1。计分账本按各层实际访问的战斗校验，奖励/商店保留上一场冻结对局，后续构筑变化不重算历史分数。

`RunEncounterContext.Node` 将战斗节点身份、Kind、配置与后继传给模式适配器。普通、精英、Boss 目前共用对局机制，可以配置不同目标、预算和奖励；特殊 Boss 技能与策略需要在内容/适配器中继续实现。

旧的 `RunDefinition(... stages, ...)` 自动生成“战斗 → 奖励 → 商店 → 下一场战斗”的节点图，最后一个奖励为终点。原 StartEncounter 的初次启动及商店单一战斗后继路径继续可用；有分支的商店使用 CompleteNode 和 ChooseNextNode。`ExampleRunContent.CreateBranching(drawMode: true/false)` 提供普通/精英分支、精英专属奖励、商店汇合和最终 Boss 的两种模式组装示例。

```csharp
var content = ExampleRunContent.Create();
var adapter = new HoldemRunAdapter(ExampleRunContent.Runtime());
var engine = new RunEngine<HoldemEncounterState, MatchCommand, MatchEvent>(content, adapter);
var run = engine.Create("run-001", new ParticipantId("player"));
var started = engine.ExecuteSystem(run,
    new RunCommand<MatchCommand>("start", run.Revision, RunAction.StartEncounter));
if (!started.Accepted) throw new System.InvalidOperationException(started.Error.ToString());
run = started.State;
string json = RunSnapshotCodec<HoldemEncounterState>.Serialize(run, content, adapter);
run = RunSnapshotCodec<HoldemEncounterState>.Deserialize(json, content, adapter);
```

切换出牌计分只在组装层更换强类型 adapter：

```csharp
var content = ExampleRunContent.Create(drawMode: true);
var adapter = new DrawRunAdapter(ExampleRunContent.Runtime(drawMode: true));
var engine = new RunEngine<DrawState, DrawCommand, DrawEvent>(content, adapter);
var run = engine.Create("draw-run-001", new ParticipantId("player"));
```

`IRunEncounter<TState,TCommand,TEvent>` 包含 Create、Execute、Validate、ReadProgress、SyncBuild 和 FilterEvents；另有 Kind/RulesVersion/SchemaVersion/MinimumDeckSize。具体 adapter 放 Composition：

- HoldemRunAdapter：下注筹码/盲注/获奖计分策略属于自身 settings。HoldemEncounterState 保存 Match 和冻结的 LastScore。
- DrawRunAdapter：手牌上限/弃牌次数/最多选择数量属于自身 settings；关卡目标和出牌预算来自 RunStageDefinition。
- ReadProgress 返回随机状态、是否到达完整检查点、是否耗尽、冻结的本轮积分，以及可迁移的能力记忆。Run 按 Score.Id 只采集一次，然后增长构筑并 SyncBuild。

RunAction.PlayerEncounter/SystemEncounter 包装具体命令；玩家/系统权限由 adapter 转交模式引擎验证。商店购买、卖出、升级、排序及刷新仍使用普通 RunAction。删除牌的下限来自 adapter（默认德州 12，Draw 8），不再写死在 Run 中。起始牌组及其版本由 RunContent 显式注入。

下注筹码、商店金币和关卡积分独立。默认德州每关重新提供 1,000/1,000 筹码和配置能量，10/20 盲注；Run 不包含这些德州字段。能力 portable Memory 只迁移 Encounter counters，实体 marks 不跨对局。`build.level/build.growth` 从 RunRelic 的唯一事实注入，不当作另一份可迁移构筑存档。

```text
Preparing → 入口节点 → 完成 → 单后继：下一节点
                        ├→ 多后继：NodeChoice → 选择下一节点
                        └→ 无后继：下一层入口 / 最后层 Completed
            战斗失败 → Failed
```

只有未完成效果/选择全部关闭的 checkpoint 才能记分或离开关卡。达到积分目标发奖励金币，耗尽轮数或对局无法继续则失败。历史得分在进入奖励前冻结；商店卖遗物、改牌和后续成长不会重算旧得分。

RunSnapshotCodec<TState> schema **v4** 将当前楼层、每层节点路线及完成记录、构筑、当前具体玩法状态、报价、随机和积分账本一起保存，固定内容/Run/计分/起始牌组及玩法身份、规则和存档版本，并保存所有楼层的节点图版本与配置指纹。16 MiB/500,000 对象上限；旧 Run v1–v3 明确拒绝，无迁移器。反序列化后同时校验楼层身份/顺序、图身份、路径连接、完成顺序、已访问战斗的积分及注入 adapter 的模式状态；修改楼层顺序、任一层图连接、节点顺序或战斗配置即使沿用同一个版本也不能读取旧路径。

含旧 Match v4 / Draw v1 内层状态的 Run 存档也会被 EncounterSchemaVersion 校验拒绝。

## 宿主与未覆盖边界

所有引擎是纯状态转换：战斗与 Run 的一次命令成功返回新状态和递增 Revision，失败返回原引用及空事件。Progression 的结算/升级成功返回新状态，非法操作抛出异常且不修改原状态，同一结算重试返回已有状态。能力处理器和适配器属于受信任注册代码，不允许从客户端动态提供。

本地宿主持有状态；PvP 服务端应为每个对局串行处理并映射认证身份。Revision 是前置条件，持久化仍需单写者或 CAS。CommandId 没有持久去重收据；网络重试若要求返回第一次响应，由宿主在同一事务提交快照、请求收据和待发送事件。Run 与内嵌对局必须原子保存。

当前未实现完整 PlayerView、HTTP/WebSocket DTO、文件/数据库 I/O、网络投递、AI、完整玩法的场景接入或计分动画。卡牌渲染/动画应消费投影与事件，不反向驱动规则。节点分支与局外成长数据/结算已具备；地图生成/选路界面、Boss 策略、消耗品背包和局外成长场景界面/开局效果接入尚未实现。

结构合法不证明离线存档可信。schema/规则/内容版本的变化需要明确迁移策略，不能仅改版本号；本次升级没有尝试读取旧格式。

## 独立验证

仓库根目录执行 `./start.command --check`（Windows 为 `start.cmd --check`）准备环境并自检，再执行 `bash Tools/test-poker-engine.sh` 或 `powershell -File Tools/test-poker-engine.ps1` 跑规则测试。测试工程从 NuGet 获取 NUnit，不读取 Unity Library。纯测试源码只存在于 `Engine/Tests/Editor`，供 .NET 测试工程与 Unity 包测试共同消费。

## Unity 集成验证与首包

统一入口为 `工具 > 测试面板`，界面为中文。编辑模式专项显示实际用例数量，实战验证要求对应玩法场景处于 Play，截图独立分页；全量回归收进折叠区，不自动执行。面板显示进度、通过/失败数量并可打开报告，编辑模式测试可停止；进入 Play 或脚本编译时停止剩余用例并释放重载锁。

ModuleBoundaryTests 检查真实程序集引用，防止基础层反向依赖、德州/Draw互相引用或 Run 引入具体模式。保留原德州规则/随机守恒/恢复测试，新增独立能力、牌型子集、Draw 存档/续执行、模式无关 Run 和跨玩法集成测试。

Unity Test Runner 可正常运行 EditMode；若当前场景有未保存修改，可用 `工具 > 测试面板 > 编辑模式测试 > 更多模块 / 全量回归 > 规则引擎全模块` 在当前 Editor 场景逐项执行同步 NUnit 方法，结果写入 `Library/PokerEngineTests/status.json` 与 `results.xml`。该轻量运行器不保存或替换场景，不支持协程/参数化/setup/teardown；遇到这些测试会拒绝，需改用完整 Test Runner。

PackagingTests 验证所有十个模块出现在 Player 编译列表、linker 保留和资源 GUID 唯一。具体本轮结果见根 CHANGELOG。本轮未进行完整 Player 或 IL2CPP 构建；目标平台仍须验证泛型 DataContract 序列化与 AOT 行为。

## 通用战局数据

独立 Encounters 模块仅引用 Core。EncounterDefinition 保存玩法/规则版本、目标、回合预算、参与者模板与特殊规则 ID；EncounterContext 保存本次 ID、实际参与者绑定和可选 Run/楼层/节点归属。EncounterSetup<TSettings> 保持玩法配置强类型，DrawRunAdapter.Describe / HoldemRunAdapter.Describe 可从真实 Run 输入生成对应描述。

EncounterProgress 是粗粒度进度投影，EncounterResult 是含各参与者胜负与结束原因的终局摘要。卡牌、牌区、资源、效果、随机与具体阶段仍由已有 DrawState / HoldemEncounterState 拥有；这些新对象不额外保存权威运行状态，不改变 IRunEncounter 的计分推进或既有快照 schema。数据结构、代码和后续接入边界见 [Encounters README](Runtime/Encounters/README.md)。

## 跨 Run 局外成长

`PlayerProgressionState` 是完整 Run 之外的玩家成长对象，包含玩家身份/Revision、成长规则版本/指纹、局外货币、类型化内容解锁、永久升级等级及幂等 Run 结算记录。统计由结算记录汇总，可按玩法查询；不保存本局卡牌或遗物实例。

静态 `ProgressionDefinition` 与策划地图、单局 RunDefinition 分开。`ProgressionEngine.Settle` 接受宿主解析后的结果/奖励，按 RunId 去重；`PurchaseUpgrade` 校验解锁、等级与货币后扣费。Composition 的 `RunProgressionSettlement.Create` 从经校验的 Run 提取终局摘要，不自动转移 Run 金币或构筑。

成长 JSON 为独立 `ProgressionSnapshotCodec` schema v1，Run 快照仍为 v4。宿主保存两者并原子处理终局结算/清除活动 Run；下一局由宿主解释升级效果及已解锁内容。完整数据图、代码和存档边界见 [Progression README](Runtime/Progression/README.md)。

## 共享卡牌对局数据与存档

Core 的 `CardTableState` 保存 MatchId、Revision、完整 CardInstance 注册表和所有有序 CardZone；全局身份不能重复，每张牌必须且只能属于一个已注册牌区。此层不包含德州下注或出牌计分回合规则，两种玩法通过各自引擎适配共享卡牌归属，不互相引用。

`CardTableSession` 提供 PrepareDraw / PreparePlay / PrepareDiscard / PrepareReorder 及玩法集成用 PrepareZones。Prepare 计算新不可变状态，Commit 只接受本会话且 Before 仍为当前状态的更新，防止旧命令或另一个会话重复消耗同一堆。出/弃按源列表顺序提交，禁用拒绝整批，失效仍可转移并保留 CanScore 语义。容量、Zone Kind、身份归属在提交前校验；PrepareZones 用于玩法替换相关牌区/牌属性，移除无归属的已删除实例，保留未受影响牌区。

`CardTableSnapshotCodec` 的独立 schema 1 保存完整卡牌状态，限制 16 MiB 严格 UTF-8，并在反序列化后验证所有归属和属性。存档可恢复为新的 Session 并继续命令；不包含 Unity 美术、观察者可见性、鼠标悬停或选中，也不改变原 Holdem/Draw/Run 存档格式。此快照包含隐藏牌，属于可信玩法/服务器存档，不能直接当作 PVP 观察者网络消息；网络仍需玩法层构建授权投影。

Unity 的 CardMatchHost 消费此基础层，运行时及编辑器围绕同一对局数据读取/操作；编辑器调试 API 使用 UNITY_EDITOR 宏，不进入 Player。新增 Core 类型沿现有 Core asmdef/link.xml 进入首包。

所有项目测试 asmdef 限定 Editor 并关闭 autoReferenced；可挂载的 CardPresentation.Tests.Support 通过 UNITY_EDITOR 编译约束隔离，避免 Unity 将辅助 MonoBehaviour 识别为不可挂载的编辑器脚本；Player 编译列表检查不包含任何项目测试程序集。测试面板及实战验证逻辑均留在 Tests/Editor。Tools 的旧离线辅助脚本位于 Unity Assets 之外，不参与游戏构建，也不作为编辑器面板的入口；日常改动只运行对应专项。


## 可配置筹码计分

`PokerEngine.Scoring` 保留纯 C# 边界：`ScoreProfile` 提供牌型基础筹码/倍率和每级成长，`CardPointProfile` 提供 13 点数基础筹码及可选的花色+点数覆盖。`ScoreEngine` 只计算明确提交的计分子集，不选择目标、不移牌、不处理生命。

默认计分版本为 `score.balatro.1`，采用小丑牌基础数值；不改变现有德州牌型识别或加入新牌型。新旧计分版本不能作为同版存档互换。Unity 资产与中文配置界面位于独立的 `Assets/Scripts/Rules/PokerScoring` 模块，详见其 README。Run 和对局都可以接收同一资产 Build 出的纯配置快照；牌型等级仍属于各自运行状态。


`CardDuelEngine.PreviewHand(state, actor, selected)` 提供只读候选牌型/计分结果，正式 Play 复用此入口。它验证选牌成员、数量、禁用状态及内容归属，不检查出牌费用，不调用伤害/目标/发牌策略，不推进随机或状态；返回的 CardDuelTransition.State 是原输入，Damage/Events 为空。无快照格式变化。

## 净化与遗物获得顺序（2026-10-19）

主线 Loadout 中的遗物列表就是获得顺序，运行时保留 Order，不能重排；跨战斗的获得/卖出/再次获得仍由后续 Run 接入负责。Numeric 按获得顺序逐条计算；HandModifier 的追加与加段、RuleValue/Refill 的贡献记录、事实/边界/行动完成触发均保留获得顺序。不同事件的时机与主效果→派生效果顺序保持不变，同一父事实仍统一判定触发资格及预占预算。旧 Draw/Holdem 的计分与 Run 接口保持原状。

葫芦在眩晕后净化自身，再施加免控。净化使用已注册目录的标签，Control 兼容旧控制定义，流血/中毒等其他负面状态须显式标记 Negative。净化不绕过玩家行动限制：已被眩晕的玩家仍不能主动出牌，但可由队友或合法触发效果净化。预览、回滚与时间线使用同一净化事实。

本次更新 Combat 规则至 combat.9、XML 规则契约至 battle-content.2，并升级非空主线遗物行为的绑定指纹；旧规则快照明确拒绝，没有自动迁移。XML 格式仍为 schemaVersion=1。
