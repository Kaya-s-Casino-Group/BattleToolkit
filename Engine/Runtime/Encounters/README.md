# 通用战局数据

`PokerEngine.Encounters` 为纯 C# 数据模块，仅引用 Core。战局表示一次节点战斗或独立对局，内部可以有多个回合；地图与跨战局构筑归 Run，跨 Run 成长归 Progression。

## 组成与归属

| 数据 | 职责 |
| --- | --- |
| EncounterDefinition | 配置 ID/版本、玩法/玩法规则版本、目标、回合上限、参与者模板、特殊规则 ID |
| EncounterObjectiveDefinition | ReachScore、DefeatOpposition、SurviveRounds 目标描述；实际判定由玩法实现 |
| EncounterParticipantDefinition | 配置槽位、阵营、可选的既有玩法初始资源；不绑定具体玩家或 AI |
| EncounterContext | 本次战局 ID、精确的槽位→ParticipantId 绑定，可选 Run/楼层/节点来源 |
| EncounterSetup<TSettings> | 通用上下文 + 强类型玩法配置，例如 DrawDefinition、MatchDefinition |
| EncounterProgress | 从玩法状态计算的通用进度投影：阶段、Revision、已完成回合、当前行动者、待决策 ID、可选结果 |
| EncounterResult | 冻结的终局摘要：战局 ID、最终 Revision、回合数、结束原因、各参与者结果 |
| EncounterParticipantResult | 相对于参与者的 Won/Lost/Draw/Aborted 与可选总分 |

实际运行数据继续由 `DrawState` / `HoldemEncounterState`（内含 MatchState）拥有：卡牌、牌区、能力/状态、待处理效果、随机状态与玩法阶段。本模块不创建另一套卡牌注册表或可写阶段。进度投影不参与权威存档；终局结果是摘要，不替代终局状态，也不执行奖励或持久构筑变更。

卡牌位置仍由已有 CardZone 唯一持有；本战局 CardInstance 通过 OriginId 关联 RunCard。参与者可只声明槽位与阵营；InitialResources 仅在 Draw/Holdem 等既有玩法需要 ResourceState 时提供，调用方应先判空。生命战使用独立的 CombatantState.Health/MaxHealth/Block，不通过通用描述强制创建筹码或能量；具体模板保留在强类型玩法配置中。控制器绑定（本地玩家/AI/网络）由宿主处理。

## 配置与约束

通用配置和结果都没有公开 setter，集合防御性复制。ID 大小写敏感。每个参与者模板槽位绑定一个唯一 ActorId，实例绑定须完整覆盖模板；支持 1–64 个参与者，单人计分无需虚构敌人。击败敌方的目标要求至少两个阵营；存活目标不能超过回合上限；回合上限范围 1–1000。最多 128 个不重复且非空的 ModifierIds，它们的目录解析与效果执行由玩法/宿主负责。

独立战局的 Context.Run 为 null；Run 内战局保存 RunId、FloorId、NodeId，节点 ID 可以在不同层重复。当前适配器描述中的 Definition.Id 使用节点局部 ID，Context.Run 提供所属范围。配置 Version 为 Run 规则版本，ModeRulesVersion 为具体适配器规则版本；配置数据本身不提供新的快照指纹或自动迁移机制。

`EncounterSetup<TSettings>` 保持配置强类型，不使用 object 或自由 JSON。TSettings 必须选用不可变玩法配置；包装器保留该对象引用，不复制任意可变对象，也不负责自动检查通用描述与玩法配置是否一致。已有适配器从同一 Run 输入生成两部分。

`EncounterProgress` 为宿主/UI 准备的粗粒度投影：Preparing → AwaitingAction → Resolving，结算中可进入 AwaitingChoice，完全处理后进入 Checkpoint 或 Finished。等待行动/选择必须有合法行动者；只有 AwaitingChoice 能携带决策 ID；检查点和终局不携带行动者/待决策。Finished 必须带结果，且结果的 Revision 和回合数与投影一致。Validate(context) 校验参与者、战局身份与回合预算，但不会代替玩法的胜负判定。

结果按每个参与者分别表达胜负，支持对抗的一胜一负和合作的多人获胜。中止结果必须统一为 Aborted，平局必须统一为 Draw；不能把局部中途退出直接当作整局中止。结束原因包含目标达成、回合耗尽、资源耗尽、参与者淘汰、中止、玩法自定义规则。目标达成要求至少一名胜者；RoundLimit 原因须匹配配置回合上限。实际积分、伤害、胜者归属由可信玩法规则生成。

## 已有玩法的描述入口

```csharp
var context = RunEncounterContext.From(run, content); // 已进入战斗节点
EncounterSetup<DrawDefinition> setup = drawAdapter.Describe(context);
string encounterId = setup.Context.Id;
ParticipantId player = setup.Context.Actor("player");
int handSize = setup.Settings.HandSize;
long target = setup.Context.Definition.Objective.Target.Value;
// 德州：EncounterSetup<MatchDefinition> setup = holdemAdapter.Describe(context);
```

Draw 描述绑定玩家和零筹码/节点能量；强类型 DrawDefinition 携带手牌大小、出/弃牌预算、计分规则及本局构筑。Holdem 描述绑定玩家/对手、各自初始筹码与能量；MatchDefinition 携带桌面规则及盲注。Holdem 适配器其余计分政策仍由 HoldemEncounterSettings 持有。Describe 不消耗随机数、不执行动作，也不改变 Run 或战局状态。

```csharp
var result = new EncounterResult(setup.Context.Id, finalRevision, roundsCompleted,
    EncounterEndReason.ObjectiveReached, new[] {
        new EncounterParticipantResult(player, EncounterOutcome.Won, totalScore)
    });
result.Validate(setup.Context);
var progress = new EncounterProgress(EncounterPhase.Finished, finalRevision,
    roundsCompleted, result: result);
progress.Validate(setup.Context);
```

Composition 的 ScoredDuel 已从独立生命战状态生成通用进度和双方结果，提供怪物固定攻击的最小流程。通用胜负执行、完整敌人 AI、特殊规则执行、生命战场景 Session/Host 与编辑器配置入口尚未接入。现有 RunEngine 仍使用 IRunEncounter 与 RunEncounterProgress 的计分检查点推进，不因新增数据结构改变行为。

持久化继续保存现有强类型战局状态；Run schema v4 / Draw v2 / Match v5 保持不变。独立战局使用对应玩法快照，Run 内使用完整 RunSnapshotCodec 原子包含当前战局。本模块暂不提供独立 JSON schema/codec，新增通用结果也尚未写入现有快照。

EncounterDataTests 覆盖单人/对抗/合作的数据表达、输入集合隔离、目标与参与者约束、选择/检查点/终局一致性、Draw/Holdem 实际描述和既有 Run 快照恢复。程序集依赖、Player 编译列表、linker 保留和 GUID 由既有模块/首包回归检查。
