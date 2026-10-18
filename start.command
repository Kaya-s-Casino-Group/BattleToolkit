#!/bin/bash
# Finder 双击入口；依赖与缓存只保存在当前工具仓库。
toolkit_root="$(cd -- "$(dirname -- "$0")" && pwd -P)" || exit 1
while :; do
    /bin/bash "$toolkit_root/scripts/bootstrap.sh" "$@"
    launch_status=$?
    if [ "$launch_status" -eq 0 ] || [ "$#" -gt 0 ] || [ ! -t 0 ]; then
        exit "$launch_status"
    fi
    printf '\n启动失败（退出码 %s）。检查上面的提示后，按 R 重试，其他键退出：' "$launch_status"
    read -r retry_launch
    case "$retry_launch" in r|R) ;; *) exit "$launch_status" ;; esac
done
