# 独立 C# 构建

运行环境为 .NET SDK 10，不需要 Unity、Mono 或 Python。策划可直接使用仓库根目录的 `start.cmd`（Windows）或 `start.command`（macOS）；启动器会准备本地依赖并打开浏览器。

十个 `PokerEngine.*` 项目直接编译 `Engine/Runtime` 中对应模块的原始源码。程序集名称与依赖来自同目录的 `.asmdef`，因此独立工具和 Unity 使用同一套规则与模块边界。新增规则模块或依赖时，两处构建清单应一起调整。

```sh
dotnet build Build/BattleToolkit.Host/BattleToolkit.Host.csproj --configuration Release
dotnet Build/BattleToolkit.Host/bin/Release/net10.0/BattleToolkit.Host.dll --root . --open
```

宿主参数：

- `--root <目录>`：独立仓库根目录；所有内容与网页文件路径相对此目录解析。
- `--port <端口>`：默认 `18767`，`0` 可由系统分配测试端口。始终仅绑定 `127.0.0.1`。
- `--open`：监听成功后打开默认浏览器；无此参数或使用 `--no-browser` 时只启动服务。
- `--check`：检查目录、运行时与真实规则编译器，输出 JSON 后退出。

重复启动会核对 `/api/health` 的工具标识与仓库标识；同一仓库的现有服务可以复用，其他服务占用端口时提示更换端口，不会终止其他程序。

HTTP 接口维持原协议：`GET /api/bootstrap`、`GET /api/example`、`POST /api/validate` 和 `POST /api/playtest`。写操作要求同源、正确 Host 和页面令牌。XML 请求上限 4 MiB，试玩 JSON 上限 8 MiB。静态文件使用白名单和 CSP，不提供任意文件访问与写盘接口。

规则执行在相同程序的子进程中：`--metadata` / `--validate` 为一次性调用，`--playtest` 为逐行 JSON 会话协议。校验最多并行两个进程；试玩命令串行处理。每次规则调用最多 25 秒，输出最多 16 MiB，试玩超时会终止该工作进程并提示重新开局。HTTP 进程不承担规则实现。

`Tools/BattleContentEditor/server.py` 保留给 Python 集成测试，调用相同的 .NET 程序，不再要求 Mono。它不是策划启动入口。
