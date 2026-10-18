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
