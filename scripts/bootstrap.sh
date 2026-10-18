#!/bin/bash
# 无 Unity/Python/Mono 依赖。只从 Microsoft 官方下载 .NET 安装器。
set -u
set -o pipefail

toolkit_root="$(cd -- "$(dirname -- "$0")/.." && pwd -P)" || exit 1
runtime_dir="$toolkit_root/.runtime"
sdk_dir="$runtime_dir/dotnet"
install_only=0
host_args=(--root "$toolkit_root")
open_browser=1
while [ "$#" -gt 0 ]; do
    case "$1" in
        --install-only) install_only=1 ;;
        --check) host_args+=(--check); open_browser=0 ;;
        --no-browser) open_browser=0 ;;
        --port)
            shift
            if [ "$#" -eq 0 ]; then printf '错误：--port 缺少端口号。\n' >&2; exit 2; fi
            host_args+=(--port "$1") ;;
        --help|-h)
            printf '用法：start.command [--check | --install-only] [--no-browser] [--port 18765]\n'
            printf '首次启动自动安装缺失的 .NET 10 SDK；--check 构建并检查，不启动服务。\n'
            exit 0 ;;
        *) printf '错误：未知选项 %s（可用 --help 查看）。\n' "$1" >&2; exit 2 ;;
    esac
    shift
done

mkdir -p "$runtime_dir" || exit 1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
export DOTNET_CLI_HOME="$runtime_dir/dotnet-home"
export NUGET_PACKAGES="$runtime_dir/nuget"
export DOTNET_MULTILEVEL_LOOKUP=0

has_sdk10() {
    [ -x "$1" ] && "$1" --list-sdks 2>/dev/null | grep -Eq '^10\.[0-9]+\.[0-9]+ \['
}

dotnet_exe=""
if [ -n "${BATTLE_TOOLKIT_DOTNET:-}" ]; then
    if ! has_sdk10 "$BATTLE_TOOLKIT_DOTNET"; then
        printf '错误：BATTLE_TOOLKIT_DOTNET 指定的程序没有稳定版 .NET 10 SDK。\n' >&2
        exit 1
    fi
    dotnet_exe="$BATTLE_TOOLKIT_DOTNET"
elif has_sdk10 "$sdk_dir/dotnet"; then
    dotnet_exe="$sdk_dir/dotnet"
elif [ "${BATTLE_TOOLKIT_LOCAL_SDK:-0}" != 1 ]; then
    candidate_dotnet="$(command -v dotnet 2>/dev/null || true)"
    if [ -n "$candidate_dotnet" ] && has_sdk10 "$candidate_dotnet"; then
        dotnet_exe="$candidate_dotnet"
    fi
fi

if [ -z "$dotnet_exe" ]; then
    case "$(uname -m)" in
        arm64|aarch64) sdk_arch=arm64 ;;
        x86_64|amd64) sdk_arch=x64 ;;
        *) printf '错误：目前自动安装支持 Apple Silicon/ARM64 和 Intel/AMD 64 位系统。\n' >&2; exit 1 ;;
    esac
    if ! command -v curl >/dev/null 2>&1; then
        printf '错误：系统缺少 curl，无法下载运行环境。macOS 通常已内置 curl。\n' >&2
        exit 1
    fi
    printf '首次启动：正在从 Microsoft 下载 .NET 10 SDK（可能需要几分钟）。\n'
    printf '安装位置：%s\n' "$sdk_dir"
    installer="$runtime_dir/dotnet-install.sh"
    curl --fail --show-error --location --retry 3 --connect-timeout 30 --max-time 180 \
        'https://dot.net/v1/dotnet-install.sh' --output "$installer.download"
    download_status=$?
    if [ "$download_status" -ne 0 ]; then
        rm -f "$installer.download"
        printf '下载失败。请检查网络或 HTTPS_PROXY/HTTP_PROXY 设置，然后重新启动。\n' >&2
        exit "$download_status"
    fi
    mv "$installer.download" "$installer" || exit 1
    /bin/bash "$installer" --channel 10.0 --quality GA --architecture "$sdk_arch" \
        --install-dir "$sdk_dir" --no-path
    install_status=$?
    if [ "$install_status" -ne 0 ]; then
        printf '安装失败。请检查磁盘空间及网络，重新启动会再次尝试，不需要管理员权限。\n' >&2
        exit "$install_status"
    fi
    if ! has_sdk10 "$sdk_dir/dotnet"; then
        printf '错误：安装完成后仍未检测到可用的 .NET 10 SDK，请查看上面的安装日志。\n' >&2
        exit 1
    fi
    dotnet_exe="$sdk_dir/dotnet"
fi

if [ "$dotnet_exe" = "$sdk_dir/dotnet" ]; then
    export DOTNET_ROOT="$sdk_dir"
fi
export PATH="$(dirname -- "$dotnet_exe"):$PATH"
printf '运行环境：%s\n' "$dotnet_exe"
if [ "$install_only" -eq 1 ]; then exit 0; fi

cd -- "$toolkit_root" || exit 1
project="$toolkit_root/Build/BattleToolkit.Host/BattleToolkit.Host.csproj"
if [ ! -f "$project" ]; then
    printf '错误：工具文件不完整，缺少 Build/BattleToolkit.Host/BattleToolkit.Host.csproj。请完整下载或克隆仓库。\n' >&2
    exit 1
fi
printf '正在准备战斗配置与试玩工具……\n'
"$dotnet_exe" build "$project" --configuration Release --nologo \
    -maxcpucount:1 -nodeReuse:false -p:UseSharedCompilation=false
build_status=$?
if [ "$build_status" -ne 0 ]; then
    printf '工具构建失败，请保留上方日志交给程序同事检查。\n' >&2
    exit "$build_status"
fi
if [ "$open_browser" -eq 1 ]; then host_args+=(--open); fi
"$dotnet_exe" "$toolkit_root/Build/BattleToolkit.Host/bin/Release/net10.0/BattleToolkit.Host.dll" "${host_args[@]}"
host_status=$?
if [ "$host_status" -ne 0 ]; then printf '工具未能正常运行，请检查上方提示。\n' >&2; fi
exit "$host_status"
