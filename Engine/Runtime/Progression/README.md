# 跨 Run 局外成长

`PlayerProgressionState` 是一次完整 Run 之外的玩家成长数据。`PokerEngine.Progression` 为独立纯 C# 程序集，仅引用 Core，不保存战斗状态、Run 牌组、遗物实例或地图配置，也不访问文件/网络/时钟。

## 数据归属

```text
宿主的玩家存档容器（宿主实现）
├── PlayerProgressionState         跨 Run 长期保存
│   ├── PlayerId / Revision       玩家归属与并发版本
│   ├── RulesVersion/Fingerprint  固定成长规则与经济配置
│   ├── Currencies                局外货币余额
│   ├── Unlocks                   已解锁的内容定义
│   ├── Upgrades                  永久升级等级
│   └── Settlements               已结算 Run 的摘要和局外奖励
│       └── Statistics            从结算记录汇总，不重复序列化
├── ActiveRunSnapshot             可选，继续未结束的 Run
└── Preferences                   音量等用户设置
```

`ProgressionUnlock` 用 `(UnlockKind, Id)` 标识解锁资格，支持卡牌、遗物、起始牌组、角色、玩法、难度、挑战。不同种类可使用同名 ID，大小写敏感。这里只保存定义 ID；“解锁遗物”表示它可以被开局内容策略纳入后续 Run，而不是把上局的遗物实例、等级或成长带入新局。角色/挑战等类型只是内容标识，不代表这些玩法已实现。

`ProgressionCurrency` 是局外货币，例如 shards；与 Run 内 Gold 分开。`ProgressionUpgradeLevel` 保存升级 ID 和等级，例如 starting-gold 2 级；等级对应的具体效果由开局组装规则解释，不直接修改运行中的 Run。可只配置 Unlocks 而不配置货币/升级，实现纯内容解锁的成长模式。

`RunSettlement` 保存 RunId、PlayerId、Won/Lost/Abandoned、玩法 ID、Run 规则版本、起始牌组版本、总分、完成楼层/总楼层，以及解析后的 `ProgressionReward`。统计包含结算次数、胜/败/放弃次数、最高总分和最多完成楼层；可按 ModeId 查询。总分定义为 Run 的所有计分检查点 Total 之和。

## 静态规则与状态

`ProgressionDefinition` 是版本化规则：局外货币 ID、所有可解锁内容、默认已解锁内容、永久升级定义。`ProgressionUpgradeDefinition` 指定扣费货币、升到各级的费用、可选解锁前置；费用数组长度就是最高等级。它不属于玩家状态或 RunFlowAsset。

新玩家货币和升级等级均为 0，仅获得 InitialUnlocks。创建时复制输入集合；之后所有操作返回新状态。每次成功结算或升一级，Revision 加 1。配置指纹按内容排序生成，单纯调整目录顺序不改变指纹；费用、解锁目录或默认解锁改变会改变指纹。

本层验证解锁 ID 在成长目录中存在；该目录与真正的卡牌/遗物等内容目录的一致性由宿主组装负责。解锁条件、通关奖励公式、难度进阶条件目前由受信任宿主计算，不内置一套具体游戏数值。

## 使用

```csharp
var player = new ParticipantId("player");
var basicDeck = new ProgressionUnlock(UnlockKind.StartingDeck, "deck.basic");
var steady = new ProgressionUnlock(UnlockKind.Relic, "joker.steady");
var rules = new ProgressionDefinition("meta.1", new[] { "shards" },
    new[] { basicDeck, steady },
    upgrades: new[] {
        new ProgressionUpgradeDefinition("starting-gold", "shards",
            new long[] { 5, 10 }, requiredUnlock: steady)
    }, initialUnlocks: new[] { basicDeck });
var engine = new ProgressionEngine(rules);
var profile = engine.Create(player);

// 宿主验证完成后的 Run，并按局外规则计算奖励。
var reward = new ProgressionReward(
    new[] { new ProgressionCurrency("shards", 20) }, new[] { steady });
// finishedRun / runContent / adapter 是当前权威 Run 及其匹配内容与适配器。
var settlement = RunProgressionSettlement.Create(finishedRun, runContent, adapter, reward);
profile = engine.Settle(profile, settlement, profile.Revision, player);
profile = engine.PurchaseUpgrade(profile, "starting-gold", profile.Revision, player);

string json = ProgressionSnapshotCodec.Serialize(profile, rules);
profile = ProgressionSnapshotCodec.Deserialize(json, rules);
bool unlocked = profile.IsUnlocked(steady);
int level = profile.UpgradeLevel("starting-gold");
```

`RunProgressionSettlement` 位于 Composition。默认仅接受 Completed/Failed 的有效 Run，经 RunValidator 校验后提取结果；显式 `abandoned: true` 仅接受尚未结束的 Run。奖励由宿主提供，不把 Run 金币、卡牌或遗物隐式转换成局外奖励。放弃后，宿主必须终止并清除该 Run 的继续游戏入口。

`Settle` 对同一 RunId 与相同结果/奖励返回已有状态，即使重试仍携带原 Revision；不会重复发奖励或重复计入统计。相同 RunId 携带不同结果/奖励时拒绝。新的结算和升级要求正确玩家与当前 Revision；未知奖励、锁定升级、超出等级、余额不足和溢出均不会修改输入状态。

## 存档与开局边界

`ProgressionSnapshotCodec` 使用独立 schema v1 JSON。Run 快照仍是 schema v4。读档校验版本/指纹、身份、结算唯一性、默认与奖励解锁、升级等级、Revision，并从奖励减去升级支出核对余额；统计由记录重建。规则变更需要显式迁移，目前不提供迁移器。结构校验不提供签名或认证，宿主负责可信数据与玩家身份。

宿主应原子保存“新的 PlayerProgressionState + 清除 ActiveRunSnapshot”；并用单写者或 CAS 检查旧 Revision。RunId 必须跨本玩家的 Runs 唯一。断电后的重复结算可用同一完整结算记录安全重试。核心不实现文件、数据库、云同步或两份存档的事务。

开新 Run 时，宿主读取 Unlocks 和 Upgrades，验证玩家开局选择，组装本次可用内容池、起始牌组和 RunDefinition，再交给 RunEngine.Create。升级效果、内容过滤和起始选择验证尚未自动接到场景宿主。策划 RunFlowAsset 继续作为流程模板。

结算记录保留用于去重和统计，不能直接删除旧记录以缩小存档。当前上限 100000 条结算，JSON 上限 16 MiB（实际可能先达到字节上限）；超过后需新增有去重保障的归档/迁移方案。ModuleBoundaryTests 与 PackagingTests 校验该程序集及其依赖/首包保留，ProgressionTests 覆盖结算、升级、快照和真实 Run 桥接。
