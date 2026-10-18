#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet="${POKER_DOTNET:-}"
if [[ -z "$dotnet" ]]; then
  if [[ -x .runtime/dotnet/dotnet ]]; then
    dotnet="$PWD/.runtime/dotnet/dotnet"
  else
    dotnet=dotnet
  fi
fi
if ! "$dotnet" --version >/dev/null 2>&1; then
  echo '请先运行 start.command 或 start.cmd 安装 .NET SDK，然后重新运行测试。' >&2
  exit 1
fi
args=(test Tests/BattleToolkit.Tests.csproj --configuration Release --logger 'console;verbosity=normal')
if [[ $# -gt 0 ]]; then
  filter=''
  for fixture in "$@"; do
    filter+="${filter:+|}FullyQualifiedName~$fixture"
  done
  args+=(--filter "$filter")
fi
"$dotnet" "${args[@]}"
