# 两个仓库的日常协作

核心约定：**BattleToolkit 保存规则、配置和工具；Unity 主仓库保存采用 BattleToolkit 的哪一次提交。** 策划推送 XML 后，Unity 不会自动跟随小仓库 main。程序验证并提交主工程中的子模块版本后，团队才统一采用该版。

## 当前修改与验证约定

- 每次修改先核对相关策划案；用户明确补充或修正的口径优先于尚未同步更新的文档。局内规则参考[战斗策划案](https://moonton.feishu.cn/wiki/KZGawy0ypiSMPtkmqwDcAKLFnle)，局外 H5 参考[流程与界面需求](https://moonton.feishu.cn/docx/EQordOsHeobymRxxAubc2l4MnTb)。
- 只验证本次修改及直接受影响的链路，选择对应测试类或具体用例；默认不跑完整回归。只有新增失败或明确关联问题时才扩展验证范围。
- 用户要求上传时，使用中文提交说明并添加 `[skip ci]`，不等待 CI。以下流程中“已验收”指完成本次相关验证；CI 仅在另有明确要求时运行。
- 本地 Web 配置与局外 H5 共用现有 `18767` 服务。更新宿主时重启该服务，不另开常驻端口。

## 内容应该提交到哪里

| 修改内容 | 所在位置 | 提交仓库 |
| --- | --- | --- |
| 共享战斗 XML | `Engine/Content/SingleBattleDemoNew.xml` | BattleToolkit |
| 纯 C# 规则、XML 读取/校验、规则测试 | `Engine/Runtime`、`Engine/Tests/Editor` | BattleToolkit |
| 网页表单、试玩宿主、启动器 | `Tools`、`Build`、`scripts` | BattleToolkit |
| Unity 场景、HUD、动画、输入、SO、集成测试 | 主工程 `Assets` 等 | Texas-Holdem |
| 游戏采用的小仓库版本 | 主工程 `External/BattleToolkit` 的 gitlink | Texas-Holdem |

在 Unity 工程的 `External/BattleToolkit/Engine/Runtime` 里修改规则也是修改小仓库：Git 以文件所属仓库为准。主工程的一次提交只能记录子模块提交号，不能替子模块提交内部文件。

## 策划修改 XML

1. 在小仓库的 Git 客户端拉取最新 main。建议为一项配置调整建一个分支；已有修改先保存/提交，不直接覆盖。
2. 双击 `start.cmd`（Windows）或 `start.command`（macOS），导入仓库中的共享 XML。浏览器恢复的草稿可能是旧版本，更新 Git 后需要重新导入磁盘文件；草稿有未交付修改时先导出备份。
3. 修改、校验，并用固定种子试玩。修改表单后重开对局，才会采用新配置。
4. 点击“导出 XML”，把下载结果保存/覆盖到 `Engine/Content/SingleBattleDemoNew.xml`。**保存浏览器草稿、下载到 Downloads、网页里校验成功，都不等于已经修改或提交仓库文件。**
5. 在 Git 客户端检查文件差异，填写中文说明并添加 `[skip ci]`，提交并推送。按团队约定经审阅合并或直接推 main。
6. 将交付提交号、对局配置 ID、固定种子、关键操作和本次相关验证结果提供给程序，再由程序接入 Unity。

只下载 ZIP 可以配置试玩，但 ZIP 没有 Git 历史，不能直接提交同步；需要把导出的 XML 交回一个正式克隆的仓库。私有仓库访问权限需覆盖参与协作的策划/程序账号。

## Unity 接入策划的新配置

由负责集成的人在主工程根目录操作。先确认子模块没有未提交修改；以下 `<已验收的提交SHA>` 必须替换为已验收的真实提交号。

```sh
git submodule update --init --recursive
git -C External/BattleToolkit status --short
git -C External/BattleToolkit fetch origin
git -C External/BattleToolkit switch --detach <已验收的提交SHA>
```

`switch --detach` 是这里的正常状态：集成方固定一个版本，便于复现和回退。打开/刷新 Unity 后，会从本地包读取这份源码与 XML，无需另拷文件进 Assets。结束正在运行的对局并重新开局，检查真实场景与 HUD；涉及宿主/表现接口时同时运行对应 Unity 集成测试。

验证通过后，仅把子模块版本和本次必要的 Unity 配套修改提交到主仓库：

```sh
git add External/BattleToolkit
git commit -m "更新战斗配置到已验收版本"
git push origin dev
```

本项目当前主工程协作分支为 dev，小仓库发布基线为 main。有分支审阅流程时，分别把提交合并进对应分支。集成代码与子模块版本应在同一主工程提交中落地，避免取到不匹配的组合。

## 其他程序同步已经接入的版本

主工程工作区先保存/提交自己的修改，然后在 dev 分支执行：

```sh
git pull --ff-only
git submodule update --init --recursive
```

第二条命令取的是主工程锁定的提交，不保证等于小仓库 main 的最新提交；这是复现该版游戏的依据。日常同步不要换成 `git submodule update --remote`，后者用于主动升级跟踪分支，可能越过主工程已验收的版本。

首次克隆也可以：

```sh
git clone --recurse-submodules -b dev https://github.com/Kaya-s-Casino-Group/Texas-Holdem.git
```

小仓库是私有仓库，初始化子模块时同样需要它的读取权限。

## 程序修改规则代码

可以在独立小仓库开发，也可以直接在 Unity 主工程的子模块目录开发。二者提交归属完全相同。子模块更新后通常处于 detached HEAD，开始改代码前先建工作分支，例如：

```sh
cd External/BattleToolkit
git switch -c codex/rule-change
```

这里从主工程正在使用的规则版本开始。如果任务明确基于小仓库最新 main，则先 fetch 并选择该基线再建分支，不要把主工程的固定版本当成最新版本。

1. 规则实现在 `Engine/Runtime`，相关纯测试在 `Engine/Tests/Editor`。新增/移动包内资源维护 `.meta`，重命名保留 GUID；保持原程序集依赖及 Unity 支持的 C#/.NET API 范围。.NET 10 仅是独立宿主工具链，不表示纯规则可以任意使用 Unity 不支持的 API。
2. 新增可配置能力时，同步 XML 内容契约、读取器、统一校验、必要的网页字段和测试。发生契约不兼容时，将代码与适配 XML 放在同一小仓库提交/合并结果中，并说明兼容边界。
3. 准备环境后运行相关规则测试：macOS `bash Tools/test-poker-engine.sh 测试类名`；Windows `powershell -File Tools/test-poker-engine.ps1 -Fixtures 测试类名`。修改宿主或网页时补跑对应集成/浏览器测试。运行中的网页宿主需要重启才能加载新 C#。
4. **在小仓库完成相关验证后，按用户要求提交并推送，取得交付 SHA。** 中文提交说明添加 `[skip ci]`。可在本目录用 `git add <本次文件>`、`git commit`、`git push -u origin HEAD`；只提交这次变更。
5. 回到主工程，按“Unity 接入策划的新配置”的流程固定交付 SHA，并测试 Unity。主工程内的表现/宿主适配修改与新子模块版本一起提交。

必须先推送小仓库，再推送引用该 SHA 的主工程。否则同事能拉到主工程，却取不到它引用的规则版本。小仓库和主工程分别有自己的 Git 状态、分支和提交；不需要把源码复制两份。

## 回退与冲突

- **只回退游戏采用版本**：集成人 fetch 后把子模块切到已验证旧 SHA，检查 Unity 兼容性，再在主工程提交子模块指针回退。小仓库 main 不因此回退，策划可以继续开发。
- **修正已发布配置/规则**：在小仓库追加修复提交，重新跑测试，再由主工程接入。不要为了同步而强推改写共享历史。
- **子模块有未提交修改**：先在小仓库建工作分支并保存修改，再更新主工程版本；不要用强制 checkout 或 reset 丢掉工作。
- **XML 冲突**：按配置 ID 检查双方修改，尤其注意引用、效果步骤顺序与对局组合；解决后重新校验和试玩。Git 能合并不代表规则组合有效。
- **主仓库子模块指针冲突**：选择团队确认的兼容 SHA，必要时先在小仓库合并两边代码，再更新主工程。不能仅按提交时间选“较新”的指针。

## 另行要求运行 CI 时看哪一次

在 BattleToolkit 的 Actions 中检查**准备交付的 SHA**，确认 Windows/macOS 的 `rules-and-host` 与 `first-launch` 四个 job 均成功。历史失败不会因新提交成功而消失；不能只看最近一个绿色结果就认为所有旧失败都已修复。

当前独立 CI 覆盖规则、HTTP、配置与试玩浏览器，以及依赖安装/启动。Unity 场景与表现接入仍需在主工程验证。

2026-10-18 首次运行 `7fe1cfd` 的 Windows 浏览器步骤曾失败：测试先等到校验结束，再单独读取文本断言，期间重命名/撤销或延迟校验使界面变为“待校验”。`33f55ba` 的全套 CI 随后通过，但它只调整了 cmd 换行，并未修复该等待竞态。后续已把测试改为在同一次浏览器轮询中等待完整“0 错误”状态，保留 30 秒超时；规则和产品交互没有因此修改。
