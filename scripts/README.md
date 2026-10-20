# 一键启动

- **Windows**：双击仓库根目录的 `start.cmd`。
- **macOS**：双击仓库根目录的 `start.command`。

启动器会检查 .NET 10 SDK，缺少时从 Microsoft 官方下载安装到当前仓库的 `.runtime/dotnet`，然后构建工具并打开默认浏览器。无需 Unity、Python、Mono，也不需要管理员权限。已有可用的 .NET 10 SDK 会直接复用；只有旧版 SDK 时，会另外安装本工具所需的版本。

首次下载通常需要几分钟，请保持网络连接。`.runtime` 内保存运行环境和缓存，不提交到 Git；换电脑后再次双击即可准备环境。正在运行时请保留终端窗口；结束使用后可关闭窗口。

启动失败时，窗口会保留中文错误和退出码，按 `R` 可重试。自动化调用传入参数时不会停下来等待输入。下载遵循当前网络环境；企业代理可以使用 `HTTPS_PROXY` / `HTTP_PROXY` 环境变量，Windows 同时使用系统默认代理。

Windows 只对本次 PowerShell 子进程使用 `ExecutionPolicy Bypass`，不会修改系统的执行策略。安装器使用 Microsoft 官方地址：

- <https://dot.net/v1/dotnet-install.ps1>
- <https://dot.net/v1/dotnet-install.sh>
- [Microsoft 安装脚本文档](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script)

## 程序与测试用法

以下参数在两个入口保持一致：

| 参数 | 用途 |
| --- | --- |
| `--install-only` | 只检查或安装 .NET 10 SDK，不构建、不启动服务 |
| `--check` | 准备环境、构建工具并执行宿主自检，不启动服务 |
| `--no-browser` | 启动服务，但不自动打开浏览器 |
| `--port 18888` | 指定服务端口；默认 18767 |
| `--help` | 显示帮助 |

例如：

```sh
./start.command --check
./start.command --no-browser --port 18888
```

```bat
start.cmd --check
start.cmd --no-browser --port 18888
```

`BATTLE_TOOLKIT_LOCAL_SDK=1` 强制只使用仓库自己的运行环境，便于验证首次安装流程；`BATTLE_TOOLKIT_DOTNET` 可以指定一个已经带有稳定版 .NET 10 SDK 的可执行文件绝对路径。两个变量均只影响本次进程，不改变全局设置。

启动脚本回归（仅运行这些测试需要 Python）：

```sh
python3 -m unittest discover -s Tests -p test_bootstrap.py -v
```

测试会在包含中文和空格的临时目录运行真实启动脚本，用可控的下载器与 SDK 替身验证安装、重试、参数传递和失败退出码；Windows/macOS CI 另外运行真实下载安装与宿主检查。
