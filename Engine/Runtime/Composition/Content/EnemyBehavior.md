# 可配置怪物行为

`battle-content.4` 增加怪物行为、逐击护甲、召唤及战斗内生成卡牌；`combat.11` 增加护甲的受击结算。固定 `damage` / `Effects` 的旧敌人仍可用。新增能力由同一规则引擎执行，无怪物名称分支。

## 定义、意图与运行时

Enemy 可以声明一个 Behavior，与固定 damage / Effects 互斥。Stage 按 XML 顺序检查条件，选择第一个满足的阶段；没有满足的阶段或可用动作时等待。阶段条件每次规划重新判断，因此回血可以回到之前的阶段。更严格的永久变身或阶段入场脚本尚未提供。

- `Sequence`：依次轮换，跳过当前不满足条件或冷却中的动作；被眩晕跳过不会推进游标。
- `Priority`：选择第一个条件满足、未达使用上限且冷却完成的动作。
- `Weighted`：在可用动作中按 weight 抽取。使用 `Behavior.seed`、战斗 ID、敌人实例 ID 和轮次稳定计算，与洗牌随机流独立；复现时保留这些值。
- `cooldownRounds="1"`：使用后至少间隔一轮才能再用；`maxUses="0"` 表示不限次数。
- `When` 支持 Always、All、Any、Not、HealthAtMost（0..1）、RoundAtLeast（从1开始）、AlliesBelow（不含自身的存活敌人数）、HasStatus。All/Any/Not 内继续嵌套 When，最多12层。

在开局状态结算后、每个玩家阶段开始时提交意图。动作、阶段和每个效果的目标锁定在不可变 `BattleEnemySetup.BehaviorState` 中；玩家造成伤害不立即换招。下次规划才按新生命/状态选招。敌人受击致死或眩晕时由实际行动规则取消/跳过。

意图目标仍存活时保留。`Action.deadTarget="Retarget"`（默认）使用原目标选择规则补足死亡目标；设为 Skip 则跳过死亡目标。伤害数值仍根据实际行动时的状态结算。`PreviewEnemyAction` 复用真实结算，只操作预测副本，不消耗正式行为记忆或随机流。

## 动作与效果

Action 内可有一个 Effects：复用现有 Damage、Heal、GainBlock、ApplyStatus、Cleanse、Eliminate、DrawCards 与选敌规则。Effects 中各效果和触发反应完成后，再按声明顺序执行 Summon / AddCards。编译器拒绝把 Effects 放在世界效果后；当前不支持任意交错“召唤→攻击新单位”等脚本。怪物本轮行动完成后，才结算行动/阶段边界。

```xml
<Behavior seed="17">
  <OpeningEffects>
    <Effect id="armor" kind="ApplyStatus" target="Self" statusRef="armor" stacks="3" />
  </OpeningEffects>
  <Stage id="normal" mode="Priority">
    <Action id="summon" name="召唤护卫" cooldownRounds="1">
      <When kind="AlliesBelow" value="2" />
      <Summon enemyRef="guard" count="2" livingLimit="3" />
    </Action>
    <Action id="attack" name="重击">
      <Effects><Effect id="hit" kind="Damage" amount="90" /></Effects>
    </Action>
  </Stage>
</Behavior>
```

OpeningEffects 只允许给自己施加状态，原生入场与召唤入场共用。多个单位的状态实例 ID 独立。

### 护甲

```xml
<Status id="armor" lifetime="UntilRemoved">
  <HitArmor multiplier="0.25" reduction="0" consumeStacks="1" primaryOnly="true" />
</Status>
```

每次正伤害主效果先使用护甲减伤，再消耗层数，即最后一层仍保护当前一击；护盾吸收也算命中。默认不消耗于流血/反伤等派生效果，`primaryOnly="false"` 可覆盖它们。零伤害不消耗。多段伤害逐段处理。层数耗尽即移除，生成真实 StatusChanged 记录。减伤在角色承伤修正后、承伤数值遗物前；多个护甲状态按状态顺序叠乘。护甲不与 TriggerCount 共用计数。

### 召唤

`enemyRef` 引用本包任何敌人定义，可以复用同一模板甚至自引用；实例化时才查表，避免配置递归展开。`livingLimit` 计入召唤者及所有存活敌人，达到上限则不再生成。全场最多保留64个单位（包含已死亡者），到达此硬上限后召唤不再产生单位。

新怪物追加到敌方队尾，获得独立身份和行为记忆；不会进入当前敌方待行动队列，从下一敌方阶段行动。生成 `EnemySummoned` 时间线快照。召唤者在战斗效果中死亡则不继续召唤。

### 塞牌

```xml
<AddCards face="standard:1:2" count="1" zone="discard" disabled="false" effects="Ineffective,Unplayable" />
```

face 使用标准牌面 ID（可从模板或网页牌面选择器取得）。对每个存活玩家独立生成实例，不改变局外 DeckEntry。每位玩家全场最多512张，超出部分不生成。每次生成记录 CardsCreated，包含接收人、卡 ID、目标区域、实际插入位置和状态快照。

| 配置 | 行为 |
| --- | --- |
| `zone="discard"` | 默认放入弃牌堆，等待回收后摸到；标准 Boss 保持此配置 |
| `zone="deck" position="Shuffle"` | 新牌与剩余抽牌堆一起随机洗牌 |
| `zone="deck" position="Top"` | 按生成顺序放到牌顶，下次优先摸到 |
| `zone="deck" position="Bottom"` | 按生成顺序追加到牌底 |
| `zone="hand"` | 直接进入手牌，立即显示卡面 |

`position` 省略或为 `Default` 时：抽牌堆使用 `Shuffle`，弃牌堆和手牌使用 `Bottom`。`Shuffle` / `Top` 只接受抽牌堆，错误组合在 XML 校验时拒绝。旧的 `zone="deck"` 未写位置配置会改为随机洗入；需要固定牌顶或牌底时必须显式填写 `Top` 或 `Bottom`。

随机洗入使用对局的可重放随机状态，同一种子与操作得到同一结果；预览、拒绝的命令和容量已满的空效果不会推进权威随机状态。插入洗牌不属于“抽牌堆耗尽后回收弃牌”，不会触发 `OnReshuffle` / `DeckRecycled`。多个 AddCards 按声明顺序依次结算。

直接入手是额外开放的配置能力；策划默认仍为按意图塞入抽牌堆或弃牌堆，不占手牌。直接入手不消耗摸牌次数，也不触发摸牌事件；允许暂时超过手牌上限（仍受512张总量限制），主动摸牌数量按剩余空间限制，启用主动摸牌流程时在结束阶段按现有规则强制弃至上限。`effectDrawOverflow` 只控制摸牌效果，不控制生成卡牌。

`Unplayable` 只禁止出牌，仍可弃牌；搭配 `Ineffective` 禁止单牌计分贡献。`disabled` 是完全禁用，连弃牌也禁止，不建议用它制作普通废牌。AddCards 默认采用可弃的废牌组合，也可以配置正常卡牌。

## 接口边界

XML 网页编辑器提供上述字段、引用重命名和试玩意图显示；规则测试使用真实 XML。Unity 项目能引用同一套规则和模板，现有固定怪物接口保持兼容。**Unity 场景中的动态怪物生成/绑定、召唤动画、废牌美术及新时间线事件表现尚需表现层接入**；当前不会自动创建 Prefab。绝杀及局外流程没有改动。
