# 血量冒险

`BattleAdventureSession` 把现有 `BattleSession` 串成连续冒险。它以敌方死亡/玩家死亡结算节点，旧 `RunEngine` 的目标积分模式不变。所有节点、购买、永久改牌、血量、金币和成长结算都在 C# 中执行；H5 或 Unity 只发送命令并显示结果。

## 配置

以下 `StandardAdventure.xml` 是保留的旧经济验证配置。体验版使用 `SingleChapterAdventure.xml` 和 `SingleChapterBattle.xml`，见下方“单章经济版本 2”。

`Engine/Content/StandardAdventure.xml` 使用独立 `AdventureContent schemaVersion="1"` 契约，引用标准单场 XML 的 encounter/relic ID。通过 `BattleAdventureXml.Load(xml, compiledBattleContent)` 验证并绑定两份内容指纹。

标准路线为三章，每章三个普通战斗、一个精英、一个商店、一个休息点、一个 Boss。`next` 可改为多个以空格或逗号分隔的后继节点，实现分支；路线必须无环、全部可达、按章推进，仅 Boss 能进入下一章，最终节点必须是最后一章的 Boss。节点行列仅用于画图，不参与通关判断。

- `enemyHealthMultiplier`：当前节点敌人与召唤怪的血量倍率。三章默认值为 `1 / 1.25 / 1.5`；后两个是验证占位值。
- `gold`：战斗胜利后发放的金币；只结算一次，停在 Reward 页面，Continue 才完成节点。
- 商店：每个商品每个商店限买一次；可卖持有遗物，价格由商品 `sellPrice` 给出，未列入商店的遗物默认卖价为零。遗物容量、重复上限及组合合法性由规则验证。
- 改牌：删牌、复制、改花色三项独立累计次数；本次价格为 `price + increase × 本次冒险已用次数`。跨商店保留次数；下一次冒险重置。改花色保留点数、卡牌效果和永久实例 ID；复制创建新 ID。
- 休息：配置回血比例、金币、遗物奖励，必须且只能选一次，再 Continue。博弈尚未实现。
- 塔罗：每次开局只能选一个已解锁塔罗，支持最大血量倍率、开局金币、开局遗物；遗物可复用现有全部可配置效果。愚者已解锁，最大血量和初始血量为模板的 120%。通关解锁两个示例塔罗，其效果及数值均标明验证占位。只有全部章节完成后才授予通关解锁。

金币、商店价格/涨价、休息收益、章节成长和两个新塔罗的效果都是验证占位值，需要策划继续定数值。此模块不包含绝杀转金币、推币机、休息博弈或资源养成 UI。

## 单章经济版本 2

根节点使用 `economyVersion="2" shopOfferCount="4" restHeal="20"`。省略 `economyVersion` 时保留旧固定商品、线性服务价格、休息三选一语义。`SingleChapterAdventure.xml` 提供固定七站、两处商店/休息二选一的四条路线。牌组、生命、金币与道具仍归整局会话所有。

- `Relic quality="0..5"` 是道具品质，默认 0；`Definition.Quality` 与实例升级 `Level` 分开。当前随机池为战斗内容中的全部道具。
- `kind="Chest" gold="100"`：进入即开箱，金币入账，并从本局从未获得的道具中等概率抽一件。初始道具、购买、实际领取的奖励、战斗替换和管理员新增均计入 `ObtainedRelicIds`；卖出/失去不会忘记。仅待领取或放弃的道具不计入获得历史，以后可再抽到。池耗尽只给金币。
- 商店进入时生成 `Session.ShopItems`，按 `MaxCopies` 过滤持有上限；唯一道具不会占多个货位，非唯一道具可占多个货位。每个货位仅买一次，不补货；池全为不可重复且不足四种时允许少于四件。0–4 品质购买价格为 10/20/30/50/100，5 品质为 50–200 整数。新经济出售按道具定义 `SellPrice`。
- `Services/Service price` 指定各类基础价（体验配置为撕牌45、加牌20、改牌30）。每类独立累计，前三次基础价，第4–10次连续乘1.1，第11次起连续乘1.07；先用decimal保留连乘精度，本次价格以 `AwayFromZero` 四舍五入到整数。`increase` 在此版本不参与计价，旧版本继续使用。
- 休息进入时先加 `restHeal`，不超过最大生命；`Continue` 可直接离开。`DrawRestReward` 免费一次，三类结果各1/3：3–4品质随机增减一件（失去时无此品质则失去最高品质，无道具则无事发生）；获得两件0–2品质；金币随机增减0–500且不少于0。增减方向各1/2，范围内候选均匀。抽奖失败、预览、读档不会再次触发。
- 自动奖励遇容量满则进入 `PendingRelicRewards`，顺序处理队首：`ClaimRelicReward` 可不传ID直接领取，或 `Id` 传当前持有实例以替换；`SkipRelicReward` 放弃。原子校验容量/唯一性；处理完才允许继续，不会被迫领取。此等待过程不再抽取。

`LastLocationReward` 提供 `Kind / HealthDelta / GoldDelta / GrantedRelicIds / LostRelicIds`；GrantedRelicIds 表示实际获得和仍待决策的奖励；放弃时从列表移除那一份，可能尚在待替换队列中。界面同时读取 `PendingRelicRewards`。`RestLotteryUsed` 与 `CanDrawRestReward` 用于抽奖按钮。商品的名称描述由宿主用 `RelicId` 从原始战斗内容投影，规则的随机货位名称回退到定义ID。

局外随机按整局seed、节点ID、用途和抽取序号通过SHA-256派生，不使用Unity随机数或战斗随机流。库存价格、获得历史、待领取队列、抽奖结果纳入摘要校验。`Export/Restore` 从命令重放同样结果；未提交预览不改变任何随机结果和资源。

专项测试：`Tools/test-poker-engine.sh AdventureEconomyTests SingleChapterContentTests`。

## 状态边界与调用

```csharp
var definition = BattleAdventureXml.Load(adventureXml, battleContent);
var session = new BattleAdventureSession(definition, battleContent, runId, seed, tarotId, previousProgression);
var entered = session.Execute(session.Revision, new AdventureCommand(AdventureCommandKind.EnterNode, nodeId));
var preview = session.ExecuteBattle(session.Revision, CardDuelAction.Play, cards, preview: true);
var committed = session.ExecuteBattle(session.Revision, CardDuelAction.Play, cards);
var itemPreview = session.UseBattleItem(session.Revision, instanceId, targetInstanceId, preview: true);
var itemUsed = session.UseBattleItem(session.Revision, instanceId, targetInstanceId);
```

普通命令和战斗命令共用冒险 Revision。战斗自己的 Revision 由模块内部管理。失败和预览不改变原状态、金币、随机序列或历史记录；预览返回独立 `result.Session`。`Fork()` 是轻量分叉，宿主可在完整投影和序列化成功后替换自己的会话。公开 `Battle` 返回隔离的轻量包装，调用它不能绕过冒险命令记录。

下一场重新按战斗内容组装战斗，继承当前生命/最大生命、永久牌组、遗物获得顺序与等级/成长/剩余充能、初始牌型等级。牌堆和手牌重新洗发，临时废牌、临时卡牌效果、状态和护盾不带入；遗物剩余充能、战斗冷却和必要跨关记忆保留；本轮计数和转盘激活状态清除。道具冷却按完成战斗递减一次，包含使用时所在战斗，商店与休息点不递减。新获得遗物进入永久列表末尾。战后展示仍可读取刚结束的 Battle，用于伤害/状态回放，不代表其临时数据进入下一场。

成长使用现有 `ProgressionEngine`，在 `UnlockKind` 末尾追加 `Tarot`，原枚举值保持不变。每个 runId 仅结算一次；新冒险必须用新 runId。失败/放弃同样记录统计，不授予通关塔罗。宿主保存 `Progression` 并传给下一次冒险。

进入每个地点时执行 `Item.locationEnterHeal`，完成并离开地点时执行 `locationLeaveGoldRatio`；包含商店和休息点，每次合法进入/离开仅一次，预览与失败操作不提交。这些是通用道具钩子，现有地图、商店价格和完整经济仍为独立验证配置。

## 保存与恢复

当前冒险快照 schema 为 4，包含主动道具命令、完整道具记忆/战斗状态及新局外经济摘要；schema 1/2/3 旧档明确拒绝且不自动迁移。旧 XML 可继续创建新的冒险。生命与生命上限为 `long`。

`Export()` 返回 JSON；`Restore(definition, battleContent, json)` 从初始成长档和受限命令日志确定性重放。保存内容包含两份配置指纹、局外 XML、初始和最终成长数据、种子、最后 Revision 与结果摘要。当前上限为 8 MiB、8192 条命令。恢复拒绝内容不一致、非法动作、非连续 Revision、错误最终状态与不合法成长档。内部怪物/自动流程命令不能作为玩家日志重放。

这是离线/本地状态校验，不是防作弊签名。联网环境仍由宿主负责认证、权威存档和信任边界。配置变更后旧存档会明确拒绝加载，暂不提供迁移。

测试：`Tools/test-poker-engine.sh BattleAdventureTests`。

## 管理员配置接口

`ApplyAdmin(expectedRevision, BattleAdminPatch, preview: false)` 是与普通出牌分离的权威入口，Unity 与 H5 复用同一份规则实现。宿主负责授予管理员权限；玩家的普通 `Execute` / `ExecuteBattle` 动作不能模拟管理员命令。补丁作为独立命令进入存档并在恢复时验证重放；过期版本、非法整批补丁及预览均不会改变原状态或随机序列。

进行中的战斗可以编辑玩家/敌人生命、生命上限、护盾，玩家金币、持有道具，以及手牌/牌库/弃牌堆。所提供的区域或道具列表按完整顺序替换；牌库最后一项为下一张摸到的牌。现有实例保留 ID 和未填写字段，新增实例由规则生成稳定 ID。牌区修改只影响当前战斗，不污染冒险永久牌组；血量、生命上限、金币和道具会同步到冒险并带到下一关。

地图、商店和休息阶段允许编辑玩家生命、生命上限、金币和持有道具。Reward 和终局不可编辑。管理员修改不消耗行动点，不触发攻击、复活或地点奖励链；将生命直接设为零只推进终局判断，管理员结束战斗不发放该节点金币奖励。恢复或预览也不会执行两次修改。

H5 管理员协议在已有本地 token 和版本校验之上使用 `admin-state`、`admin-preview`、`admin` 操作；真实牌库顺序和卡牌实例 ID 只在显式的管理员状态内返回，普通战斗投影仍只展示牌堆汇总。64 位血量/护盾/金币以十进制字符串收发，避免 JavaScript 舍入。
