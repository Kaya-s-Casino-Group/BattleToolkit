# 独立回归测试

运行工具只需要启动器自动安装的 .NET SDK。下面的 Python 和 Node.js 依赖仅供开发、回归与 CI 使用。

## 规则引擎

先运行根目录 `start.cmd --install-only`（Windows）或 `bash start.command --install-only`（macOS），再执行：

```sh
# macOS
bash Tools/test-poker-engine.sh
bash Tools/test-poker-engine.sh BattleContentIntegrationTests SingleBattleSceneContentTests
```

```powershell
# Windows PowerShell
powershell -ExecutionPolicy Bypass -File Tools/test-poker-engine.ps1
powershell -ExecutionPolicy Bypass -File Tools/test-poker-engine.ps1 BattleContentIntegrationTests
```

也可以直接使用 .NET 10 SDK：

```sh
dotnet test Tests/BattleToolkit.Tests.csproj --configuration Release
```

测试项目从 NuGet 还原固定版本的 NUnit、测试适配器和 Microsoft.NET.Test.Sdk，不读取 Unity 的 `Library`。
规则测试的唯一源码位于 `Engine/Tests/Editor`；Unity 通过 UPM `testables` 使用同一份测试。
场景、旧 SO 兼容性和 Unity 资产打包检查由 Unity 主仓库的集成测试负责。

## 单场战斗职责提取回归

`BattleRefactoringContractTests` 的六组 SHA-256 期望值采集于重构前的 `b788c096e553633f18fe62d00c3d8deab1b61341` 实现，覆盖旧补牌、手动摸牌、自动摸牌，以及一名/两名玩家。序列包含按钮查询、播放门禁、过期版本拒绝、手牌/出牌预览、自动推进和终局；摘要记录步骤、牌区、生命/格挡、资源、随机状态、计分以及逐条时间线中间快照。它补充已有状态、遗物、Boss 和事务测试，不代替它们。

```sh
dotnet test Tests/BattleToolkit.Tests.csproj --configuration Release --filter "FullyQualifiedName~BattleRefactoringContractTests|FullyQualifiedName~BattleSessionPlaybackTests|FullyQualifiedName~BattleSessionTests|FullyQualifiedName~BattleTimelineTests|FullyQualifiedName~DesignFlow2951Tests|FullyQualifiedName~DesignScoring2951Tests|FullyQualifiedName~CompatibleHandMechanismTests|FullyQualifiedName~HandGuide"
```

不得为通过结构重构而重新生成摘要期望；规则行为有意变更时应单独审查和更新。

2026-10-24 Windows/.NET 10 本地验证：重构前全量 1791 项，通过 1777、失败 14；第一轮后全量 1797 项，通过 1783、失败 14。原有用例的结果及失败消息完全一致，新增六组摘要全部通过；上述专项 127 项全部通过。全量尚未全绿，既有失败没有跳过或修改断言。

## 当前共享能量规则回归

`EnergyOnlyBattleTests` 覆盖出/弃牌共用能量、五张上限、弃几补几/出牌不补、不同费用、零费用、无独立行动/弃牌次数限制、预览不提交、溢出留牌、洗牌回收、多人自动推进、道具门禁以及新旧 XML 兼容。`BattleRefactoringContractTests` 的六组旧模式轨迹继续保持原期望值。

```sh
dotnet test Tests/BattleToolkit.Tests.csproj --configuration Release --filter "FullyQualifiedName~EnergyOnlyBattleTests|FullyQualifiedName~BattleRefactoringContractTests|FullyQualifiedName~DesignItem2951Tests"
```

旧双预算道具测试显式指定 `energyOnly=false`；六张选牌的手牌指南测试显式使用兼容模式，保留原断言。当前模式不使用这些旧规则。

2026-10-24 本轮本地结果：调整前 1797 项，通过 1783、失败 14；调整后 1821 项，通过 1807、失败 14。新增 24 项全部通过，原有用例结果和失败消息未变化；5 项 `test_design2951.py` 正式 HTTP 宿主回归全部通过。宿主构建与 `--check` 成功，网页脚本语法和新旧模式的配表字段检查通过。未运行 Unity 编辑器或完整浏览器回归。

## 既有 14 项失败修复

2026-10-24 Windows/.NET 10 全量结果：1824 项全部通过，失败 0、跳过 0，报告位于 `Tests/TestResults/tyche-failures-fixed.trx`。原 14 项中，13 项属于过时断言或夹具问题，1 项为 Primary Outgoing 状态快照的实现问题。

- 九项溢出回滚夹具在实际受影响的目标上预置 1 点格挡，使 `1 + long.MaxValue` 确实溢出；保留错误码、原状态引用、空事件以及各用例的资源/牌区/随机状态等回滚断言。新增边界用例同时验证 `0 + long.MaxValue` 合法，以及先伤害再溢出时整批回滚。
- 开局检查改为验证不可变状态的字段内容；零/负有效手牌上限按既定钳制规则验证为 0，并检查空手牌回合可以结束；Boss 夹具通过 XML 节点修改，消除换行格式依赖；皇家同花顺治疗期望按当前配表点数修正为 9。
- Primary Outgoing 使用整批开始时的状态；派生 Outgoing 和所有 Incoming 使用各自结算时的最新状态。保留原失败用例，新增穿插即时反应的回归，检查状态引用、调用顺序、来源和结算结果。

原错误的手牌上限失败参数替换为零/负两项边界验证，另新增两项战斗边界/快照测试，因此总数由 1821 增至 1824；没有跳过失败用例或重新生成六组旧模式摘要。24 项共享能量测试及六组旧模式摘要均在此次全量中通过。本轮未运行 Unity 编辑器测试。

## 配置和试玩宿主

```sh
python3 -m unittest discover -s Tools/BattleContentEditor/tests -p 'test_*.py' -v
python3 -m unittest discover -s Tests -p 'test_*.py' -v
```

Windows 将 `python3` 替换为 `python`。前者覆盖原 Python 兼容宿主及正式 .NET HTTP 宿主，后者检查启动脚本的依赖发现、下载失败重试和退出码。
临时服务只监听回环地址并使用自动分配的端口，不占用正在运行的配置工具。

## 浏览器交互

```sh
npm ci
npx playwright install chromium
python3 Tools/BattleContentEditor/tests/run_browser.py
```

该入口构建并启动一个临时 .NET 宿主，然后验证配置表单、XML、草稿和完整对局试玩；完成后自动停止服务。
如果要使用已安装的 Chrome，可设置环境变量 `PLAYWRIGHT_CHANNEL=chrome`。

GitHub Actions 在 Windows 和 macOS 分别运行以上检查，并用含空格和中文的目录验证首次自动安装与启动。
