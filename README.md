# 战斗内容工坊 · BattleToolkit

独立的纯 C# 规则引擎、XML 配置工具和网页对局试玩。不需要下载 Unity 主工程或安装 Unity。

## 策划开始使用

1. 克隆本仓库，或下载并解压代码 ZIP（不要在压缩包内直接打开）。
2. Windows 双击 **start.cmd**；macOS 双击 **start.command**。
3. 首次启动会联网下载缺少的 .NET 10 SDK 到本仓库 `.runtime/dotnet`，构建后打开默认浏览器。保留终端窗口即可使用；失败时窗口显示错误，可按 R 重试。
4. 导入 `Engine/Content/SingleBattleDemoNew.xml`，即可修改游戏当前共享配置、统一校验并试玩。选择对局配置、填写固定种子，选牌预览、出牌、弃牌或结束回合。
5. 修改后导出 XML；需要交付到游戏时覆盖 `Engine/Content/SingleBattleDemoNew.xml` 并提交本仓库。运行中的对局配置冻结，重新开局才生效。

已有网页草稿保存在同一浏览器和地址中。工具不会自动写回 XML，导出文件仍需保存到约定位置。首次安装通常需要几分钟；不需要管理员权限，也不修改全局运行环境。macOS 若解压后失去执行权限，可在终端执行 `bash start.command`。

[详细工具用法](Tools/BattleContentEditor/README.md) · [启动与故障排查](scripts/README.md) · [XML 配置契约](Engine/Runtime/Composition/Content/README.md)

## 目录与依赖

| 目录 | 内容 |
| --- | --- |
| Engine/Runtime | 十个纯 C# 程序集，规则唯一实现 |
| Engine/Content | Unity 与网页共用的实际 XML |
| Engine/Tests/Editor | 同一套纯规则测试，供 .NET 和 Unity 使用 |
| Build | .NET SDK 项目、独立 HTTP 宿主 |
| Tools/BattleContentEditor | HTML 表单、试玩交互与工具适配代码 |
| Tests | 独立 NUnit 项目、启动器回归 |

运行工具只需要 .NET 10 SDK，启动器自动准备。Python、Node、Playwright 仅用于开发者回归；规则单元测试通过 NuGet 自动还原 NUnit。工具 HTTP 只监听本机，XML 编译与战斗结算均调用 Engine，不在 JavaScript 另写一套规则。

## 验证

```sh
./start.command --check
bash Tools/test-poker-engine.sh
python3 -m unittest discover -s Tools/BattleContentEditor/tests -p 'test_*.py' -v
python3 -m unittest discover -s Tests -p 'test_bootstrap.py' -v
```

Windows 对应 `start.cmd --check`、`powershell -File Tools/test-poker-engine.ps1`。浏览器回归在本机服务启动后运行 `npm ci`、`npx playwright install chromium`、`npm run test:browser` 和 `npm run test:playtest`。CI 在 Windows/macOS 验证独立构建、规则、HTTP、浏览器以及首次安装入口。

## 日常协作

策划改 XML、程序改规则、Unity 接入与回退的完整步骤见 [两个仓库的日常协作](COLLABORATION.md)。小仓库先提交/推送，主工程再提交采用的子模块版本。

## Unity 主工程集成

主工程使用 Git submodule 固定本仓库提交，路径 `External/BattleToolkit`；`Packages/manifest.json` 引用 `file:../External/BattleToolkit/Engine`，并在 `testables` 中列出 `com.kaya.battle-engine`。场景 XML 的 Unity 路径为 `Packages/com.kaya.battle-engine/Content/SingleBattleDemoNew.xml`。

程序集名称与原 .meta GUID 沿用；Unity 场景、SO、表现层和集成测试留在主工程。修改本仓库后先提交并推送，再由主工程提交更新后的子模块版本。更新主工程使用 `git submodule update --init --recursive`；不要把引擎另拷回 Assets。主工程锁定旧提交时不会自动采用新配置。
