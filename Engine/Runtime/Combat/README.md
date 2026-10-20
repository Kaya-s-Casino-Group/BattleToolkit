# Combat：战斗单位与公共结算

`PokerEngine.Combat` 是纯 C# 模块，只引用 Core、Abilities。玩家和怪物共用 `CombatantState`；当前不建立 Player/Monster 继承树。区别来自模板、阵营和上层控制方式。

## 所有权

| 对象 | 持有 / 负责 | 不负责 |
| --- | --- | --- |
| CombatantDefinition | 模板 ID、最大生命、初始格挡 | AI、手牌、奖励 |
| CombatantState | 实例 ID、模板 ID、阵营、当前/最大生命、格挡 | 出牌、回合推进、Run 成长 |
| CombatState | 本场 ID、Revision、全部单位、集中保存的 StatusInstance | 牌组、胜负、状态效果执行 |
| CombatEngine | 基础效果、即时触发队列、原子批量结算和事件 | 选目标、费用、分数换算、具体遗物行为 |
| ScoredDuelEngine（Composition） | 最小单玩家/单怪物流程、牌型分数换伤害、玩家能量/结束回合、固定攻击、胜负投影 | 卡牌合法性、洗牌/抽牌、完整 Run、Unity 表现 |

主线遗物的绑定与运行状态由 Composition 的 `BattlePlayerTurn.Relics` 持有，复用不可变 `AbilityMemory` 保存计数；不放入 CombatState，也不另建 AbilityWorld。支持绑定、权威运行状态更新、组件快照以及显式配置的被动数值修正，尚不自动执行遗物触发能力。详见 [主线遗物绑定与运行状态](../../README.md#主线遗物绑定与运行状态)。

生命值直接存于 `CombatantState.Health`，类型为必填 `int`，范围 0..MaxHealth；`IsAlive` 从生命值派生。单位不再持有 ResourceState，也不携带筹码或能量。不要同时持有另一份可变 AbilityWorld 作为生命真相；未来接能力执行器时，需要由模式投影并原子回写同一权威状态。

ScoredDuelState 组合持有独立的 PlayerTurnState（MaxEnergy / Energy），定义配置每回合能量和出牌消耗，费用与恢复时机由玩法控制；怪物行动由其行为规则决定，无需创建能量字段。既有 ResourceState 保留给 Draw/Holdem 及其能力执行流程，后续按具体玩法需要独立迁移。

状态效果复用 `StatusInstance`，集中放在 CombatState，检查唯一 ID、持续时间与归属。存储只允许指向本场或本场单位；`CombatEffect.ApplyStatus(status)` 要求来源和目标都是对应的单位。一个实例 ID 只能存在一次，最多 256 个实例。`Resolve(..., statusCatalog: catalog)` 可启用不可变的 `CombatStatusCatalog`；显式目录下未知定义拒绝。未传目录时保留旧被动模式：任意合法定义 ID、同定义不同实例独立保存，不执行行为或重施合并。最小 ScoredDuel 仍明确拒绝非空状态集合。

## 可配置状态行为与生命周期

`CombatStatusDefinition` 以任意 `StatusDefinitionId` 声明 Tags、Behaviors、DefaultLifetime 和 ReapplyPolicy，不依据某个 ID 硬编码行为。`BlockActionWindowSpec` 阻止行动机会；`PreventStatusApplicationSpec(StatusTags.Control)` 阻止带控制标签的状态施加；`ReflectBlockedDamageSpec`、`LifeStealSpec`、`OverhealDamageSpec` 声明下文的三种即时触发；`PeriodicDamageSpec` 声明可配置时机与计时对象的持续伤害。非法标签、未知行为类型、重复行为、非法重施策略均在构造时拒绝。次数期限不能同时驱动两种消费行为，定义和实际载荷均校验此约束。

施加状态时读取批次内最新状态；前一条施加的免疫可以拦截后一条控制。拦截是正常结算，不回滚同批伤害，也不移除已存在的控制。若多个免疫实例可用，按状态保存顺序选第一个实际拦截者；仅这个实例的次数型期限消耗一次，阶段型或永久免疫保持不变。已死亡目标仍在行为判定前跳过，不消耗免疫。

默认 `KeepLonger` 对同目标同定义保留原实例 ID，来源归因更新为最新成功施加者，Stacks 与剩余次数各取较大值，不相加。阶段／轮次期限取更晚的截止边界；必须是相同期限类型，阶段型还必须有相同 PhaseKey，否则原子拒绝。比较跨轮次期限使用 Composition 绑定的 `combat.Id + "/round/" + (CompletedRounds + 1).ToString("D10", InvariantCulture)` 一基稳定 scope；相同 scope 无需比较轮次。`IndependentInstances` 则允许同定义多个不同 ID，仍拒绝 ID 冲突。被拦截的施加不占容量，刷新现有实例也不额外占容量。

`StatusLifetimeSpec` 是与玩法无关的相对声明：UntilRemoved、ForTriggers、ThroughNextPhase(phaseKey)、ThroughCurrentRound。Combat 不猜测回合或阶段；Composition 在施加前绑定为 `StatusDuration`。`CombatStatusLifecycle.Blockers` 只读查询当前单位的阻止行动状态，死亡单位返回空；只有真正跳过存活单位的行动机会时才调用 `SkipActionWindow`，统一消费所有命中的次数型阻止实例一次，非次数型保留。无阻止状态时跳过请求拒绝，防止凭空产生跳过事实；旧目录为空的被动模式不阻止行动。

`EndPhase(state, revision, scopeId, phaseKey)` 精确移除匹配的 UntilPhaseEnd；`EndRound(state, revision, scopeId)` 只移除该 scope 的 UntilRoundEnd，不顺手清理任何阶段型状态。维护不要求状态来源／目标存活。无状态变化时返回原 CombatState 且不增加 Revision；实际跳过或到期原子推进一次 Revision。玩家阶段、敌人队列和回合推进仍由 Composition 控制，不创建第二份 AbilityWorld，也不直接调用其生命周期修改另一份状态。

## 结算契约

- `CombatEffect` 是上层完成合法性判断后的效果，不是玩家提交的命令。模式负责费用、阵营、目标和数值来源。
- Damage 先扣格挡再扣生命；生命最低为 0，事件单独报告吸收、实际生命损失、溢出伤害和死亡。
- Heal 最高恢复到 MaxHealth，不支持复活。来源和目标必须是本场已知单位；结算时目标已死亡则跳过该条效果，继续处理后续效果，不重新选目标。实际接受的治疗报告 HealthRestored 和 Overheal；跳过的治疗不产生溢出事实。
- 格挡不会自动在回合边界消失；模式需要时显式提交 ClearBlock。最小示例中格挡持续至消耗完毕。
- ApplyStatus 的强类型 `Status` 载荷必须与效果来源、目标一致；Amount 为 0，不携带攻击上下文或攻击修正。新状态对同批后续承伤修正立即可见；实际新增／刷新时检查实例 ID 与容量，ID 冲突、容量或载荷错误回滚整批。KeepLonger 重施可以引用其原实例 ID。已死亡目标的状态效果不占容量、不预留 ID，Composition 不提前按计划中的状态数量拒绝。
- `CombatEffect.Eliminate(source, target)` 直接将生命归零，不消耗格挡、不移除状态、不调用承伤修正；Amount 为 0，也不携带攻击上下文。它不代表巨额伤害；和伤害致死一样，后续对该死亡目标的任意效果都会跳过。
- 每批 1–64 个主效果，与所有派生效果共用一次 Revision。主效果整批先按行动开始状态校验已知单位和存活来源；初始死亡来源仍拒绝。来源在本次结算中死亡，其后续主动效果正常跳过，其他存活来源的效果继续，不撤销已结算结果。未知单位、溢出或任一步真正失败仍回滚整批，不发事件；不能借跳过掩盖非法身份。非空批次即使全部跳过也接受并推进一次 Revision；旧 Revision 拒绝，输入状态不修改。

`CombatTransition.RuleEvents` 是有序的实际结算事实：`CombatEvent` 记录生命／格挡变化，`CombatStatusEvent` 记录新增；`CombatStatusBlockedEvent` 记录拦截者，`CombatStatusChangedEvent` 记录刷新、消费或到期的前后状态及原因，`CombatActionWindowSkippedEvent` 记录实际跳过的单位和阻止实例。死亡目标跳过不产生事件，原 EffectPlan 保留完整声明；旧 `Events` 仍只投影 CombatEvent，不伪造零伤害。`Index` 是原效果位置，可有缺口，同一效果的多个事实共享 Index；生命周期事件 Index 为 -1、Effect 为 null。唯一事实标识使用 Revision + SequenceIndex，SequenceIndex 在同一次 Combat 提交中从 0 连续递增。Composition 全程透传 RuleEvents，阶段维护可另有独立 Combat Revision。

`CombatEvent.HealthLost` 保留实际生命差；伤害触发应使用 `DamageToHealth` 或检查 Damage 类型。Eliminate 的 DamageToHealth、SettledDamage、BlockAbsorbed 和 Overkill 均为 0，死亡原因是 `CombatDeathCause.Eliminate`；伤害致死为 Damage，未死亡为 None。默认牌型／弃牌规则尚不产出新操作；Composition 可通过可选的 IScoredHandEffectRule 声明五种基础操作及逐效果目标。正式启用消灭时，表现中把 HealthLost 直接当伤害的投影需要相应适配。
- 公共层不判胜负，支持多个阵营/单位；一对一限制只在 ScoredDuel 示例中。

## 双方数值修正

`Resolve` 与 `ResolveTiming` 末尾可选的 `amountModifier` 接收纯 `ICombatAmountModifier`，只处理 Damage / Heal / GainBlock，分别调用 Outgoing / Incoming 两个阶段。死亡跳过在调用之前完成；ApplyStatus、Eliminate 和 ClearBlock 不调用。来源被允许死亡的周期/反应仍可修正，不增加新的存活限制。

主动效果 Outgoing 的 Combat 上下文为批次开始快照，派生效果 Outgoing 与所有 Incoming 使用该条效果结算前的最新状态。现有 IIncomingDamageModifier 在两个新阶段之间执行；既有角色攻方修正仍由 Composition 预先执行。修正器返回 CombatAmountModification，不能修改来源、目标或状态；输入量不匹配或返回 null 报 InvalidAmountModifier，溢出仍报 Overflow，均原子拒绝。

CombatAmountContribution 只保存来源实例/规则条目身份、固定值和百分比，不引用 Relic / Runs。CombatAmountModification 汇总同阶段贡献，计算 `floor(max(0, input × max(0, 1 + sum(rates)) + sum(flats)))`；最多 1024 个唯一来源实例/条目组合。默认聚合模式保留原有调用行为；主线遗物传 sequential=true，按传入的获得顺序逐条执行 `floor(max(0, 当前量 × max(0, 1 + rate) + flat))`，每一步检查 long 溢出。贡献列表保留实际顺序，FlatAdjustment/RateAdjustment 仅为贡献总量，Sequential=true 时不能用总量重建最终值。

CombatEvent 保留原 Effect，新增 OutgoingAmountModification / IncomingAmountModification 和 SettledAmount；无贡献时对应记录为 null。伤害的最终承伤读取 SettledDamage；治疗的 HealthRestored 和 Overheal 由 SettledAmount 计算，格挡累加使用 SettledAmount。反应必须读取这些实际事实，不能把请求 Effect.Amount 当成最终结果。修正流程不改变反应次数消耗、派生资格或状态到期策略。Composition 的遗物配置和示例见 [被动数值修正](../../README.md#遗物的被动数值修正)。

## 即时触发队列

`Resolve(..., reactionProvider: provider, reactionLimits: limits)` 接受可选的纯 `ICombatReactionProvider`；传入 statusCatalog 时，目录中的已施加状态行为自动生效，不需要另行安装 provider。同一父事实先收集内置状态反应，再收集外部 provider 的反应；未配置触发状态和外部规则时保持原行为。`React(CombatReactionContext)` 根据本条事实和当前 CombatState 返回有序的 `CombatReaction`；目标已经由规则绑定，Combat 不依赖 Composition 的目标选择器。上下文是整个父效果完成后的不可变临时状态，Revision 仍为输入版本，并非一次独立提交。例如免疫拦截的 Blocked、Consumed 两条事实都看到已经扣除免疫次数的状态。

顺序固定为：主效果 A → A 的派生及其子效果 → 下一个主效果。每次先按事实顺序收集该效果的全部反应，兄弟反应按规则返回顺序执行，子反应在下一个兄弟之前执行。规则负责按遗物获得顺序等明确规则输出，不能依赖无序集合；所有反应都在同一事务内结算，成功前不对外提交事实。无目标时规则返回空集合，不能返回 null。

`CombatReaction` 包含基础效果、RuleId、Owner、可选的 SourceInstanceId 和 Origin（Triggered、Periodic、Reflection、Lifesteal、Overheal）。这些来源标签不自动开启任何行为，也独立于伤害的 AttackOrigin。派生伤害已是出伤数值，不重复套用主效果的攻击修正，仍逐条经过承伤修正和护盾。触发队列不预置防守方／攻击方优先级，具体行为组合时通过返回顺序确定。

派生效果默认 `CanTriggerReactions=false`，阻止隐式递归；后续需要“吸血治疗 → 溢出伤害”等明确链路时，由受信规则显式开启并筛选父事实来源。`CombatReactionLimits` 默认允许最多 256 条派生、16 层，硬上限为 4096 条、64 层；主效果不占派生预算。枚举时即检查预算，已入队但会因死亡跳过的派生也计数。超限返回 ReactionLimitExceeded；无效规则输出返回 InvalidReaction；算术溢出仍为 Overflow。它们均返回原状态、空事件，不静默截断已算出的链条。

派生默认 RequireLiving；规则可显式选择 AllowDefeated，让已经成立的被动反应保留原来源归因，即使来源已死亡也继续结算。未知来源始终拒绝、死亡目标始终跳过；外部主效果没有此开关，不能让死亡角色重新行动。目标承伤修正也能读取这个已死亡的真实来源。是否在致死打击后仍触发反伤留给具体规则，基础设施不替策划决定。

每条效果事实的 `Provenance` 记录 Origin、RootEffectIndex、Depth、父 Revision／SequenceIndex、规则及实例 ID、Owner 和继续触发／死亡来源策略。内置状态反应还有强类型 TriggerStatusId；外部 provider 的 SourceInstanceId 只作归因，不会凭字符串自动消费状态。主效果深度为 0、无父事实；派生的 Index 仍指根主效果位置，SequenceIndex 才是本次提交的唯一事实序号。同一 ApplyStatus 生成的多条事实共享效果来源；无因果关联的生命周期事实 Provenance 为 null；周期伤害及其次数消费关联时钟事实，RootEffectIndex 为 -1。被跳过的主／派生效果均不产生伪事实，也不触发新反应。原 EffectPlan 保留原始声明，实际派生结果通过 RuleEvents／Events 查看。

### 事务内的可累计触发状态

`Resolve` / `ResolveTiming` 末尾可选的 `reactionRuntime` 接受 `ICombatReactionRuntime`。它必须是不可变、无外部副作用的权威规则对象，`React(context)` 返回 `CombatReactionEvaluation`（NextRuntime + Batches）。公共层不认识遗物定义、Charges 或冷却；这些字段由 Composition 的运行状态桥管理。旧 ICombatReactionProvider 的签名和行为保留。

每个父事实依次收集内置状态、外部 provider、runtime 的反应。一个 `CombatReactionBatch` 表示一次激活，可包含 1–64 条同 Owner / RuleId / SourceInstanceId 的反应，所有条目共用非空 ActivationId。批次不得索取内置状态的 TriggerStatusId 消费权限；同一次 Combat 提交内激活 Id 不得重复。候选先全部验证、纳入原有总量/深度限制，再接纳 NextRuntime，让后续事实读取已预占的次数。兄弟与子效果仍按原深度优先队列执行，不因激活分组建立第二条队列。

成功时 `CombatTransition.ReactionRuntime` 返回最后的不可变草案，失败时为 null、状态仍为输入、事件为空。调用方必须与其外层事务共同提交，不能在 React 内写外部状态；预览也因此不消费真实预算。`Provenance.ActivationId` 将同组效果关联到激活报告，外部 provider 填写 SourceInstanceId 不具有写入运行状态的权限。目标后来死亡仍跳过，不撤销已经入队的激活；真正错误则撤销整批。

`ICombatReactionRuntime.React` 仍只接收可继续触发的实际效果事实。可选扩展 `ICombatBoundaryReactionRuntime.ReactBoundary(CombatBoundaryReactionContext)` 接收 BattleStarted、PhaseStarted、PhaseEnding 时钟；旧 fact-only runtime 与 provider 不会收到 Effect 为空的时钟，ActionCompleted 也不进入边界钩子。独立可选扩展 `ICombatActionCompletionReactionRuntime.ReactActionCompleted(CombatActionCompletionReactionContext)` 只接收 ActionCompleted，复用周期消费后的队列位置和最新快照；旧边界实现不自动获得完成订阅。开战、阶段和实际操作是否成立由 Composition 决定，Combat 不负责轮维护或胜负判定。

`ResolveTiming` 先发出时钟事实，处理已有周期伤害及其次数消费，再以最新临时 CombatState 调用一次对应的边界或完成 runtime。边界批次与周期条目共用原队列、总效果数与深度限制，父事实是该时钟、RootEffectIndex=-1、首层 Depth=1。边界后新施加的周期状态不会在同一时钟补触发。周期伤害仍为叶子；边界效果能否产生后继事实反应由 CanTriggerReactions 控制，随后复用原状态 → provider → runtime 顺序。ResolveTiming 新增可选 reactionProvider，仅用于真正的可触发效果事实。

无效返回、未知目标、重复激活 Id、队列超限都会回滚周期与边界的整个批次及 NextRuntime。新增时钟枚举不自动扩展 PeriodicDamageSpec 的有效时机，它只接受 TurnStarted / ActionCompleted / PhaseEnding。配置、共享预算与期限详见 [遗物边界触发](../../README.md#遗物开战与阶段边界触发)。

### 盾反、吸血与溢出治疗转伤害

三种强类型行为都把倍率和目标选择分开配置，最终派生量为 `floor(实际量 × Ratio × Stacks)`，最后取整一次。倍率必须非负，可大于 1；溢出 long／decimal 时整次行动回滚。默认不自动赋予任何角色这些状态，不改变既有牌型表或弃牌奖励。

| 行为 | 触发条件与实际量 | 默认目标 |
| --- | --- | --- |
| ReflectBlockedDamageSpec | 持有者承受 Primary Damage，取实际 BlockAbsorbed | 原攻击者 EventSource |
| LifeStealSpec | 持有者造成 Primary Damage，取实际 DamageToHealth，排除护盾和 Overkill | 持有者 Self |
| OverhealDamageSpec | 持有者接受 Primary 或 Lifesteal Heal，取实际 Overheal | 首个存活敌人 |

行为持有者是 StatusInstance.Target，派生 Source 也使用持有者；状态施加者 Source 仍保留在状态记录中，不把施加者误认为吸血／反伤归属者。消灭不计作伤害，不触发盾反或吸血；清盾也不算护盾吸收。满血时吸血仍产生 Heal，允许接溢出转伤。反射与溢出伤害是叶子，不继续触发这些行为；吸血治疗显式允许再触发，但内置规则只接受上表的来源，防止循环。

同一伤害事实先生成防守方盾反，再生成攻击方吸血；同组按 Statuses 保存顺序、行为声明顺序、目标顺序执行。次数消耗及反应均在同一事务内；反伤中途击杀攻击者后，后续吸血不会复活它，其剩余主效果继续按死亡规则取消。盾反默认 RequireLiving，致死打击后不触发；配置 `sourcePolicy: CombatReactionSourcePolicy.AllowDefeated` 可保留致死时已经产生的护盾吸收反应，仍经过承伤与护盾。双亡胜负仍由原有 Composition 规则决定。

`CombatTargetSpec` 支持 Self、EventSource、EventTarget、Opponents、Allies，后两者可选 FirstLiving／LowestHealth／HighestHealth／AllLiving；每个 AOE 目标获得完整派生量，不分摊。`CombatTargetResolver` 读取当前结算快照，按绝对生命选择低／高血量，平手保留阵容顺序，友方包含自身。事件端点死亡则为空，不悄悄换人；无存活候选也是正常空结果。允许已知死亡 owner 解析目标，是否能产生效果仍由行为的来源策略决定。新增 EventActor 只从 ActionCompleted 时钟的唯一参与者解析，不从效果 Source 推断；缺少该时钟时拒绝，死亡行动者返回空。仅完成订阅支持 EventActor，状态反应、周期状态、遗物事实/边界配置明确拒绝。Composition 的主计划自动选择复用此底层，但 Requested／ActorPolicy、活来源校验和主计划非空要求仍在原层，旧目标选择行为保持不变。

ForTriggers 状态只在生成至少一条正数、有存活目标的派生时消费一次；AOE 以“状态实例＋父事实”计一次，不按目标数扣。取整为零、没有吸收／扣血／溢出或无目标均不扣次数；Stacks 不随次数下降。触发资格、数值和目标固定后消费，次数用尽移除状态，但不取消已入队的 AOE 兄弟效果；后续目标死亡导致跳过也不退还此次消费。消费事实使用 Consumed 原因，Effect 指触发它的父效果，Provenance 记录本次状态激活；它只记录事实，不另行进入触发入口。后续真正结算的子效果读取消费后的状态。

```csharp
// 示例数值，非平衡默认值。定义注入 BattleRules(statusCatalog: catalog)，然后通过 ApplyStatus 施加。
var leech = new CombatStatusDefinition(new StatusDefinitionId("example.leech"), StatusTags.None,
    new StatusBehaviorSpec[] { new LifeStealSpec(0.25m) }, StatusLifetimeSpec.ForTriggers(2));
var overflow = new CombatStatusDefinition(new StatusDefinitionId("example.overflow"), StatusTags.None,
    new StatusBehaviorSpec[] { new OverhealDamageSpec(1m, CombatTargetSpec.Opponents(CombatTargetSelection.AllLiving)) },
    StatusLifetimeSpec.UntilRemoved());
var reflect = new CombatStatusDefinition(new StatusDefinitionId("example.reflect"), StatusTags.None,
    new StatusBehaviorSpec[] { new ReflectBlockedDamageSpec(0.5m) }, StatusLifetimeSpec.UntilRemoved());
var catalog = new CombatStatusCatalog("example.reactions.1", new[] { leech, overflow, reflect });
```

### 流血、中毒与时钟入口

`PeriodicDamageSpec` 配置结算时机、每层伤害、目标、每次衰减层数与计时对象，默认目标 Self、计时对象 Holder。时机支持 TurnStarted（回合开始）、ActionCompleted（有效行动后）、PhaseEnding（所属阵营阶段结束）。`PeriodicTimingSubject.Holder` 按中毒者／状态持有者计时，`Applier` 按施加者计时；定义 ID 没有内置语义。当前标准中毒使用 Holder/TurnStarted、每层伤害 1、每次衰减 1。数值为 `checked(DamagePerStack * Stacks)`，零伤害合法。目标选择始终以状态持有者为参照，伤害 Source 保留原状态施加者，Provenance.Owner 记录持有者。目标支持 Self / Allies / Opponents 及既有选择策略，时钟没有伤害端点，因此配置 EventSource / EventTarget / EventActor 时拒绝。

`CombatEngine.ResolveTiming(state, expectedRevision, signal, incomingDamage, statusCatalog, reactionLimits)` 复用统一结算器；信号由 `CombatTimingSignal.TurnStarted(boundaryId, participants)`、`ActionCompleted(boundaryId, actor, actionKey)` 或 `PhaseEnding(boundaryId, participants, phaseKey)` 声明。Combat 不认识出牌、弃牌或阵营阶段推进，由 Composition 提供真实行动与所属阵营名单。BoundaryId 用于审计／因果定位，不是独立去重账本；宿主按 expectedRevision 提交，模式负责每个真实边界只派发一次。直接调用即生成 CombatTimingEvent 并推进一次 Revision，即使没有可用状态或效果；未知参与者、重复名单、非法标识拒绝。

Holder 模式下，施加者死亡不阻止存活持有者继续结算。Applier 模式的回合开始／有效行动后要求施加者存活；所属阵营阶段结束仍按阵营名单触发，直到持有者死亡、状态移除或战斗结束。实际处理队列中的每个状态时重新检查存活条件，避免同一时钟前面的伤害杀死施加者后继续误触发后面的个体时钟。

时钟事实先写入，随后按 Statuses 存储顺序逐个检查状态；每个状态读取当时最新单位／状态快照，死持有者或无存活候选正常跳过且不扣次数。每个有效状态固定当次伤害及目标列表，按目标顺序执行完整数值（AOE 不分摊），伤害全部完成后 ForTriggers 消费一次；配置 stackDecay 时则扣除对应层数，降至零后移除，最后一跳也先造成伤害再移除。零配置伤害、承伤修正归零、全额格挡均属于有效机会，照常消费或衰减。stackDecay 与 ForTriggers 不可同时使用。已生成的 AOE 不因持有者被自己的第一段伤害杀死而撤销，之后的其他状态若持有者已死则跳过。次数消费的 Effect 为 null，Provenance 保留对应时钟、状态实例及规则归因。

周期伤害 Origin 为 Periodic，Depth 为 1，RootEffectIndex 为 -1，父事实指向同批 CombatTimingEvent；经过既有承伤修正、护盾和死亡处理，不重新应用主攻击倍率。固定为叶子，不进入内置或外部即时触发 provider，因此不触发盾反、吸血、溢出治疗。每个生成的目标效果计入同批 CombatReactionLimits.MaxEffects；超预算、算术溢出或无效承伤结果均回滚整次周期批次。Composition 进一步保证整个外层行动的牌区、费用、随机与状态一并回滚。

TeamBattle / CardDuel 的接入顺序：

- 成功出牌、弃牌、未被眩晕跳过的敌人行动，在主计划及即时派生完成后派发一次 ActionCompleted；多段、AOE 都不增加次数。EndPlayerTurn、SkipPlayerTurn 不派发行动完成。
- TurnStarted 在持有者回合开始派发；玩家摸牌前、敌人自己的行动窗口前结算。眩晕仍会触发。
- 本阵营最后行动结束时，整队派发一次 PhaseEnding；只有配置该时机的状态在此结算，被眩晕仍会结算。当次行动／阶段内新施加的状态可以触发。
- 阶段周期伤害先于既有清盾与 EndPhase / EndRound 到期清理；清盾与这两种到期清理的原相对顺序保留。最后一跳仍可读取尚未到期的状态和护盾。
- 主行动或行动后流血已经终结战斗时，不再启动下一个时钟；已启动的周期批次不提前按胜负中断。结算后依据实际存活者重建敌人队列并决定胜负／推进回合。
- 未施加匹配时机的存活周期状态、也没有匹配遗物边界/完成订阅时，不调用时钟入口，保持旧默认事件与 Revision 行为。不自动为角色或牌型附加流血／中毒，弃牌默认奖励不变。

固定次数的持续伤害可使用 ForTriggers；叠层中毒使用 UntilRemoved 与 stackDecay，按结算次数逐步减层。阶段末、轮次末期限也可复用，仍由原生命周期边界清理。KeepLonger 取层数和期限的较大值，IndependentInstances 独立结算，AddStacks 合并同目标同定义的层数且保留原期限。Applier 计时的状态还按施加者分开叠层与校验，同一施加者重施继续合并，多个施加者各自计时。SkipActionWindow、EndPhase、EndRound 本身不会作为即时反应钩子。

```csharp
// 示例数值，不是默认平衡配置；通过既有 ApplyStatus 显式施加。
var bleed = new CombatStatusDefinition(new StatusDefinitionId("example.bleed"), StatusTags.None,
    new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.ActionCompleted, 2) },
    StatusLifetimeSpec.ForTriggers(3));
var poison = new CombatStatusDefinition(new StatusDefinitionId("example.poison"), StatusTags.None,
    new StatusBehaviorSpec[] { new PeriodicDamageSpec(CombatTimingKind.PhaseEnding, 3) },
    StatusLifetimeSpec.ForTriggers(2));
```

## 独立伤害修正规则

`IAttackDamageModifier` / `IIncomingDamageModifier` 是纯函数协议；`ScaledAttackDamageModifier` / `ScaledIncomingDamageModifier` 保存不可变倍率、固定增减值和版本，按 `floor(max(0, 输入 × 倍率 + 固定值))` 生成 DamageModification。默认 1 倍、0 增减，负倍率拒绝；超出 decimal/long 范围按原子结算的 Overflow 路径拒绝，不写状态。

角色归属和规则分派由 Composition 的 BattleActorDamageRules 与 TeamBattleEngine 管理：按来源选择攻击修正、按目标选择承伤修正。CombatEngine 不持有 Unity 资产，不在 CombatantState / CombatState 的存档字段中存入策略对象；承伤接口仍在每条效果扣格挡前调用，读取该批次前序效果后的上下文。

## 最小可运行的牌型伤害流程

入口位于 `Composition/Encounters/ScoredDuel*.cs`。这套流程验证公共基建，不替换现有 Draw 出牌计分模式。

```csharp
var engine = new ScoredDuelEngine(new ScoredDuelDefinition(
    "duel.example.1",
    new CombatantDefinition("hero", 60),
    new CombatantDefinition("slime", 25),
    new ScoreDamagePolicy(pointsPerDamage: 10), enemyDamage: 6, roundLimit: 8));
var state = engine.Create("encounter-1", new ParticipantId("player"), new ParticipantId("slime-1"));

// authoritativeHost 已验证实际提交牌组，并调用既有分类和 ScoreEngine：
var score = ScoreEngine.EvaluatePattern(engine.NextScoreId(state), scoreProfile,
    classification, handLevel, submittedCards, orderedRelics);
var hand = engine.ResolveScoredHand(state, state.Revision, score);
if (hand.Accepted) state = hand.State;
// 宿主消费 hand.Events 播放动画；能量尚有余额可继续出牌。
// 收到玩家“结束回合”命令时（也可以零次出牌）：
if (state.Phase == ScoredDuelPhase.AwaitingPlayerHand && playerPressedEndTurn)
    state = engine.EndPlayerTurn(state, state.Revision).State;
// 自动或手动结束回合后，宿主安排一次怪物行动：
if (state.Phase == ScoredDuelPhase.AwaitingEnemyAction)
    state = engine.ResolveEnemyAction(state, state.Revision).State;
var progress = engine.ReadProgress(state);
```

分数换伤害暂用 `floor(Total / PointsPerDamage)`，可通过配置调整；它不改变 ScoreResult，也不把分数写入筹码。玩家可以同回合多次行动；出牌与弃牌分别消耗 PlayEnergyCost / DiscardEnergyCost，默认各 1，耗尽到 0 时自动转敌人阶段。某项动作不可支付时保留剩余能量，可改做另一项或手动结束。EndPlayerTurn 可在零次出牌时主动结束，剩余能量不跨回合保留。MaxEnergy 与 InitialEnergy 分开设置，当前能量可以高于上限；敌人行动后存活且战局继续则恢复到 MaxEnergy。怪物被击杀立即胜利，不再反击；玩家死亡失败；最后允许回合中玩家先行动、存活怪物反击，双方仍存活则按回合预算失败。CompletedRounds 在怪物行动或玩家击杀后递增，独立于 Scores.Count。每个出牌、付费弃牌、主动结束或怪物行动均写入 Actions；未配置奖励或周期状态时，弃牌/手动结束不改变 Combat.Revision，但改变战局自己的 Revision；周期结算或状态维护可以推进 Combat.Revision。

此入口只接受宿主权威计分结果。ScoreEngine 的算术校验和顺序 ScoreId 能防止不一致/重复结算，无法证明玩家持有或合法打出了这些牌。客户端应提交牌 ID，由拥有牌区的宿主完成验证和计分，不能直接提交任意 ScoreResult。

## 存档与结果

`ScoredDuelSnapshotCodec` schema v4 保存规则指纹、初始玩家生命、单位状态、玩家能量、完成回合数、阶段、计分与行动账本；反序列化按冻结配置重放行动账本，核对生命/格挡/能量/回合数/阶段/Revision，支持在等待怪物行动时恢复。v4 新增付费弃牌账本、独立最大/初始能量与两类消耗的规则指纹，允许合法的超过上限能量，但仍拒绝伪造能量；规则版本为 combat.9 / scored-duel.4，v1–v3 旧档明确拒绝。combat.3 新增状态施加、消灭和类型化规则事件；combat.4 将已死亡目标改为跳过；combat.5 新增可配置状态行为、重施、消费、精确到期和事件 SequenceIndex；combat.6 新增即时派生队列、因果字段、溢出治疗事实，并将结算中来源死亡改为取消其剩余主动效果；combat.7 接入正式状态触发行为、当前快照目标选择与激活次数消费；combat.8 增加行动／阶段时钟、周期伤害及其生命周期。combat.9 新增 Cleanse 和 Negative 标签。CombatState 存储形状未变；指纹包含 Combat 版本，因此旧 combat.2–combat.8 快照也明确拒绝。规则或数值变化拒绝旧档；暂不提供迁移或磁盘 I/O。该快照仍不包含 CardDuel 的四牌区和随机状态。

`EncounterContext`、`EncounterProgress`、`EncounterResult` 由状态即时投影，不重复存储。生命战的通用参与者描述只包含槽位/阵营，InitialResources 为 null；生命模板与初始生命由 ScoredDuelDefinition / ScoredDuelState 持有。结果包含双方胜负、原因、回合数和最终 Revision；奖励与幂等写回由 Run/宿主管理。

## 场景 Demo

Composition 已新增 CardDuelEngine，把 CardTableState 四牌区与 ScoredDuelState 原子组合；Unity 宿主在独立的 Assets/Scripts/Gameplay/SingleBattle 中。场景角色使用抽象 BattleActor 以及派生的 PlayerBattleActor / EnemyBattleActor，通过 ParticipantId 读取宿主的当前单位，不另存生命值；玩家输入与怪物意图/行为各自独立。打开 SingleBattleDemo.unity 可以玩完一场战斗，详见 Unity 主工程中的 Demo 结构。原 DrawBattleHost 仍为分数目标模式。CardDuel 的组合快照与完整 Run 接入尚未实现。

## 下一层接入边界

- Run 持有当前构筑；每场战斗实例化当前 Run.Deck（初始 52 张，随 Run 增减），卡牌 ID/OriginId 与四牌区继续沿用已有实现。CombatantState 不拥有牌组。
- 当前 DrawEngine/DrawBattleHost 仍是分数目标模式。接生命战时，由战局协调器统一拥有卡牌流程与 CombatState，复用现有 CardZone/View，不能简单在 Draw 分数胜利之后额外扣血。
- 当前 RunEngine/RunValidator 的胜负与历史节点校验都依赖 TargetScore。接入生命战需要显式的战局结果账本及对应快照升级，不能伪造分数来驱动胜利。本轮 Run v4、Draw v2、Match v5 保持现有行为。
- 玩家 Run 生命可以在创建战局时显式传入 InitialPlayerHealth；结果提交后的跨节点生命写回尚未接入。完整 AI、更多状态行为和角色 UI 留待后续。

回归覆盖公共伤害结算、批量回滚、死亡、生命单一所有权、单位不携带筹码/能量、真实牌型计分、能量消耗/恢复、主动/自动/零出牌结束回合、双方终局、重复提交、行动账本恢复和不一致快照拒绝。

CardDuelEngine.Create 只创建独立牌组与四区（Hand/Played/Discard 为 0），不发牌。首次 CardDuelAction.Start 免费补满 HandSize，HasStarted 置真、Table.Revision 加一，不改变 Duel、能量、动作账本、完成回合或弃牌次数；首回合前拒绝其他命令，重复 Start 拒绝。Unity 在空区绑定后执行 Start，以便开局发牌复用正常展示流程。

发牌策略以 ICardDuelDealPolicy.GetDrawCount(CardDuelDealContext) 注入 CardDuelEngine。四种触发点共用引擎的安全移牌/洗回路径，返回数量必须在 0..MissingCards；策略不修改牌区。CardDuelDealPolicy 允许出牌与弃牌分别选择 AfterAction / NextPlayerTurn，默认出牌延后、弃牌立刻补满。终局不再触发补牌；修改策略后需要创建新战局，CardDuel 完整存档仍未实现。

### 净化

`CombatEffect.Cleanse(source, target)` 在当前批次状态上移除目标全部 Negative 或 Control 状态，需要显式状态目录。Control 自动可净化，其他负面状态用 Negative 标签声明；不会根据定义 ID、来源阵营或行为类型猜测正负。增益和其他单位状态保留。每个被移除实例生成 `CombatStatusChangedEvent(Reason=Cleansed, StatusAfter=null)`，保留效果归因和顺序，不生成生命事件。无匹配状态不生成移除事实，死目标按已有规则跳过；后续非法效果仍回滚整个批次。净化不提供持续免疫，之后仍可重新施加负面状态。
