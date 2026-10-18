# 更新记录

## 2026-10-18 — 独立规则与策划工具仓库

- 从 Unity 主工程迁移十个纯 C# 模块、共享 XML、HTML 配置与试玩工具，规则运行源码和共享 XML 保持逐字节一致；保留程序集名和资源 GUID。
- 提供 Windows `start.cmd`、macOS `start.command`，自动安装缺少的本地 .NET 10 SDK，构建后打开默认浏览器，不需要 Unity/Python/Mono。
- 使用独立 ASP.NET Core 宿主，复用 C# 编译器和 BattleSession，保持原网页功能、会话版本与固定种子行为。
- 纯规则测试由 .NET 和 Unity 共用，独立项目通过 NuGet 获取 NUnit；新增 Windows/macOS CI，覆盖首次安装、中文空格路径及网页交互。
- 修正旧 CardEffects 过滤测试对默认计分的隐式依赖，显式提供原有 20×2 测试计分；保留断言，未改运行规则。
- 本地 macOS 验证：1247 项规则、56 项 HTTP、37 项浏览器、12 项启动脚本通过；真实下载安装 SDK 10.0.402，并通过宿主自检。Windows 实跑结果由 CI 提供。
