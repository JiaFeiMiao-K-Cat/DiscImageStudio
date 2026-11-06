#!/usr/bin/env bash
#
# 为 macOS 构建自包含的 disc-image-studio CLI。
#
# 产物布局：macos/build/cli/<rid>/disc-image-studio
# 依赖：.NET 10 SDK（仅打包 CLI 时需要；Swift 侧不需要 .NET）
#
# 用法：
#   macos/scripts/build-cli.sh [--rid osx-arm64|osx-x64]
#                             [--configuration Release|Debug]
#                             [--output <dir>]
#                             [--update-fixtures]
#                             [-h|--help]
#
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
MACOS_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
REPO_ROOT="$(cd -- "$MACOS_DIR/.." && pwd -P)"

CLI_PROJECT="$REPO_ROOT/src/DiscImageStudio.Cli/DiscImageStudio.Cli.csproj"
FIXTURE_FILE="$MACOS_DIR/Tests/DiscImageStudioKitTests/Fixtures/catalog.json"

usage() {
  cat <<'EOF'
用法：macos/scripts/build-cli.sh [选项]

为 macOS 构建自包含（self-contained、单文件）的 disc-image-studio CLI。

选项：
  --rid <rid>              目标运行时：osx-arm64 或 osx-x64。
                           默认按 uname -m 推断：arm64 → osx-arm64，x86_64 → osx-x64。
  --configuration <name>   构建配置：Release（默认）或 Debug。
  --output <dir>           输出目录。默认 macos/build/cli/<rid>。
  --update-fixtures        发布成功后运行 `disc-image-studio catalog --json`，
                           把输出写入 macos/Tests/DiscImageStudioKitTests/Fixtures/catalog.json。
                           默认不修改任何快照文件。
  -h, --help               显示本帮助。

.NET 查找顺序：$DOTNET → $HOME/.dotnet/dotnet → PATH 中的 dotnet。

示例：
  macos/scripts/build-cli.sh
  macos/scripts/build-cli.sh --rid osx-x64 --configuration Debug
  macos/scripts/build-cli.sh --update-fixtures
EOF
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

note() {
  printf '%s\n' "$*"
}

default_rid() {
  case "$(uname -m)" in
    arm64) printf 'osx-arm64\n' ;;
    x86_64) printf 'osx-x64\n' ;;
    *)
      printf 'error: 无法从 uname -m（%s）推断 RID，请用 --rid 指定 osx-arm64 或 osx-x64。\n' "$(uname -m)" >&2
      return 1
      ;;
  esac
}

RID=""
CONFIGURATION="Release"
OUTPUT=""
UPDATE_FIXTURES=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid)
      [[ $# -ge 2 && -n "$2" ]] || die "--rid 需要一个取值（osx-arm64 或 osx-x64）"
      RID="$2"
      shift 2
      ;;
    --rid=*)
      RID="${1#*=}"
      shift
      ;;
    --configuration)
      [[ $# -ge 2 && -n "$2" ]] || die "--configuration 需要一个取值（Release 或 Debug）"
      CONFIGURATION="$2"
      shift 2
      ;;
    --configuration=*)
      CONFIGURATION="${1#*=}"
      shift
      ;;
    --output)
      [[ $# -ge 2 && -n "$2" ]] || die "--output 需要一个目录"
      OUTPUT="$2"
      shift 2
      ;;
    --output=*)
      OUTPUT="${1#*=}"
      shift
      ;;
    --update-fixtures)
      UPDATE_FIXTURES=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      printf 'error: 未知参数 %s\n\n' "$1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ -z "$RID" ]]; then
  RID="$(default_rid)" || exit 1
fi

case "$RID" in
  osx-arm64|osx-x64) ;;
  *) die "不支持的 RID：${RID}（只支持 osx-arm64 或 osx-x64）" ;;
esac

case "$CONFIGURATION" in
  Release|release) CONFIGURATION="Release" ;;
  Debug|debug) CONFIGURATION="Debug" ;;
  *) die "不支持的配置：${CONFIGURATION}（只支持 Release 或 Debug）" ;;
esac

HOST_ARCH="$(uname -m)"
if [[ ("$RID" == "osx-arm64" && "$HOST_ARCH" == "x86_64") || ("$RID" == "osx-x64" && "$HOST_ARCH" == "arm64") ]]; then
  note "提示：当前主机是 ${HOST_ARCH}，却要构建 ${RID}。dotnet publish 会交叉发布，但产出的 CLI 只能在本机通过 Rosetta 或目标机器上运行，本脚本最后的 --help 校验可能失败。"
fi

if [[ -z "$OUTPUT" ]]; then
  OUTPUT="$MACOS_DIR/build/cli/$RID"
fi
# 相对路径按仓库根目录解析，方便在任意目录调用。
case "$OUTPUT" in
  /*) ;;
  *) OUTPUT="$REPO_ROOT/$OUTPUT" ;;
esac

[[ -f "$CLI_PROJECT" ]] || die "找不到 CLI 工程：$CLI_PROJECT"

resolve_dotnet() {
  if [[ -n "${DOTNET:-}" ]]; then
    if [[ -x "$DOTNET" ]]; then
      printf '%s\n' "$DOTNET"
      return 0
    fi
    printf 'error: 环境变量 DOTNET 指向 %s，但它不存在或不可执行。\n' "$DOTNET" >&2
    return 1
  fi

  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    printf '%s\n' "$HOME/.dotnet/dotnet"
    return 0
  fi

  if command -v dotnet >/dev/null 2>&1; then
    command -v dotnet
    return 0
  fi

  return 1
}

if ! DOTNET_BIN="$(resolve_dotnet)"; then
  cat >&2 <<'EOF'
error: 找不到 .NET SDK（dotnet）。

已尝试：$DOTNET → $HOME/.dotnet/dotnet → PATH 中的 dotnet。

安装 .NET 10 SDK 的方法（任选一种）：

  # 官方安装脚本（安装到 ~/.dotnet，随后本脚本会自动找到它）
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"

  # 或者
  brew install --cask dotnet-sdk

仓库根目录的 global.json 要求 SDK 10.0.100（rollForward: latestFeature），
因此必须安装 10.0.x 的 SDK。
EOF
  exit 1
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

note "dotnet        : $DOTNET_BIN"
note "RID           : $RID"
note "配置          : $CONFIGURATION"
note "输出目录      : $OUTPUT"
note ""

mkdir -p "$OUTPUT"

note "==> dotnet publish（自包含单文件）"
(
  cd "$REPO_ROOT"
  "$DOTNET_BIN" publish "$CLI_PROJECT" \
    -c "$CONFIGURATION" \
    -r "$RID" \
    --self-contained true \
    -o "$OUTPUT" \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true
)

BINARY="$OUTPUT/disc-image-studio"
[[ -f "$BINARY" ]] || die "发布结束但没找到 ${BINARY}（发布是否失败？）"
chmod +x "$BINARY"

BINARY_BYTES="$(stat -f '%z' "$BINARY" 2>/dev/null || stat -c '%s' "$BINARY")"
BINARY_SIZE="$(du -h "$BINARY" | awk '{print $1}')"

note ""
note "==> 校验 --help"
if ! "$BINARY" --help >/dev/null; then
  die "$BINARY --help 退出码非 0，CLI 无法运行（检查 RID 是否与主机架构匹配、是否有 quarantine 属性）。"
fi
note "--help 通过"

if [[ "$UPDATE_FIXTURES" -eq 1 ]]; then
  note ""
  note "==> 更新 catalog.json 快照"
  FIXTURE_DIR="$(dirname -- "$FIXTURE_FILE")"
  mkdir -p "$FIXTURE_DIR"
  TMP_FIXTURE="$(mktemp "$FIXTURE_DIR/.catalog.json.XXXXXX")"
  if ! "$BINARY" catalog --json >"$TMP_FIXTURE"; then
    rm -f -- "$TMP_FIXTURE"
    die "disc-image-studio catalog --json 失败；快照未改动。（若 CLI 尚未实现 catalog 子命令，这是预期失败。）"
  fi
  if [[ ! -s "$TMP_FIXTURE" ]]; then
    rm -f -- "$TMP_FIXTURE"
    die "catalog --json 没有输出任何内容；快照未改动。"
  fi
  mv -f -- "$TMP_FIXTURE" "$FIXTURE_FILE"
  note "已写入：${FIXTURE_FILE}（$(du -h "$FIXTURE_FILE" | awk '{print $1}')）"
fi

note ""
note "完成。"
note "CLI   : $BINARY"
note "大小  : ${BINARY_SIZE}（$BINARY_BYTES 字节）"
note "目录  : $OUTPUT"
