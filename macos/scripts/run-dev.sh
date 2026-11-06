#!/usr/bin/env bash
#
# 开发期快速启动：
#   - 已打包过 macos/build/DiscImageStudio.app → 直接 open
#   - 否则 → swift run --package-path macos DiscImageStudioMac
#
# 用法：
#   macos/scripts/run-dev.sh [--rebuild] [-- <build-app.sh 的参数>]
#
#   --rebuild 会先跑一遍 build-app.sh（可继续传它的参数，例如 --no-cli），再打开 .app。
#
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
MACOS_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
APP="$MACOS_DIR/build/DiscImageStudio.app"

usage() {
  cat <<'EOF'
用法：macos/scripts/run-dev.sh [选项] [-- <build-app.sh 参数>]

选项：
  --rebuild     先重新构建并打包 .app（build-app.sh），再打开。
  -h, --help    显示本帮助。

行为：
  存在 macos/build/DiscImageStudio.app 时 open 它；
  否则用 swift run --package-path macos DiscImageStudioMac 前台运行。

示例：
  macos/scripts/run-dev.sh
  macos/scripts/run-dev.sh --rebuild
  macos/scripts/run-dev.sh --rebuild -- --no-cli

注意：
  `swift run` 走 debug 构建，路径是 macos/.build/debug/DiscImageStudioMac；
  它不是 .app 包，因此不会内置 CLI，CLI 需从 PATH 或设置里的自定义路径获得。
EOF
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

note() {
  printf '%s\n' "$*"
}

REBUILD=0
BUILD_APP_ARGS=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rebuild)
      REBUILD=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    --)
      shift
      BUILD_APP_ARGS+=("$@")
      break
      ;;
    *)
      die "未知参数：$1（传给 build-app.sh 的参数请放在 -- 之后）"
      ;;
  esac
done

if [[ "$REBUILD" -eq 1 ]]; then
  [[ -x "$SCRIPT_DIR/build-app.sh" ]] || die "找不到可执行的 build-app.sh：$SCRIPT_DIR/build-app.sh"
  "$SCRIPT_DIR/build-app.sh" ${BUILD_APP_ARGS[@]+"${BUILD_APP_ARGS[@]}"}
fi

if [[ -d "$APP" ]]; then
  note "打开 $APP"
  open "$APP"
else
  note "未找到 ${APP}，改用 swift run（debug 构建，前台运行）"
  note "提示：打包过的应用请用 macos/scripts/build-app.sh 生成，再运行本脚本。"
  exec swift run --package-path "$MACOS_DIR" DiscImageStudioMac
fi
