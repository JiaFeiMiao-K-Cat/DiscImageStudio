#!/usr/bin/env bash
#
# 把 SwiftPM 产物组装成 macOS 应用包：
#   macos/build/DiscImageStudio.app
#
# 流程：swift build → 复制可执行文件 → 生成 Info.plist → 生成 AppIcon.icns
#       →（可选）内置 CLI 与许可证 → ad-hoc 签名。
#
# 用法：
#   macos/scripts/build-app.sh [--configuration release|debug]
#                              [--cli <path>] [--no-cli] [--no-sign]
#                              [-h|--help]
#
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
MACOS_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
REPO_ROOT="$(cd -- "$MACOS_DIR/.." && pwd -P)"

APP_NAME="DiscImageStudio.app"
APP="$MACOS_DIR/build/$APP_NAME"
BUNDLE_EXECUTABLE="DiscImageStudioMac"
ICON_SOURCE="$REPO_ROOT/src/DiscImageStudio.App/Assets/DiscImageStudio-256.png"
INFO_TEMPLATE="$MACOS_DIR/Resources/Info.plist.in"
VERSION_PROPS="$REPO_ROOT/Directory.Build.props"

usage() {
  cat <<'EOF'
用法：macos/scripts/build-app.sh [选项]

用 SwiftPM 构建 DiscImageStudioMac 并组装 macos/build/DiscImageStudio.app。

选项：
  --configuration <name>   SwiftPM 配置：release（默认）或 debug。
  --cli <path>             显式指定要内置的 disc-image-studio 路径。
                           默认自动查找 macos/build/cli/<rid>/disc-image-studio。
  --no-cli                 不内置 CLI（应用仍可启动，只是找不到内置 CLI）。
  --no-sign                跳过 ad-hoc 代码签名（仅用于排障）。
  -h, --help               显示本帮助。

说明：无论是否使用 --no-cli，脚本都会构建并内置原生流式刻录助手
      disc-burn-stream（Objective-C + DiscRecording，不依赖 .NET）。

示例：
  macos/scripts/build-app.sh
  macos/scripts/build-app.sh --configuration debug
  macos/scripts/build-app.sh --cli ~/.dotnet/tools/disc-image-studio
  macos/scripts/build-app.sh --no-cli
EOF
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

note() {
  printf '%s\n' "$*"
}

warn() {
  printf 'warning: %s\n' "$*" >&2
}

CONFIGURATION="release"
CLI_OVERRIDE=""
BUNDLE_CLI=1
SIGN=1
BUNDLED_CLI_PATH=""
BUNDLED_HELPER_PATH=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --configuration)
      [[ $# -ge 2 && -n "$2" ]] || die "--configuration 需要一个取值（release 或 debug）"
      CONFIGURATION="$2"
      shift 2
      ;;
    --configuration=*)
      CONFIGURATION="${1#*=}"
      shift
      ;;
    --cli)
      [[ $# -ge 2 && -n "$2" ]] || die "--cli 需要一个可执行文件路径"
      CLI_OVERRIDE="$2"
      shift 2
      ;;
    --cli=*)
      CLI_OVERRIDE="${1#*=}"
      shift
      ;;
    --no-cli)
      BUNDLE_CLI=0
      shift
      ;;
    --no-sign)
      SIGN=0
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

case "$CONFIGURATION" in
  release|Release) CONFIGURATION="release" ;;
  debug|Debug) CONFIGURATION="debug" ;;
  *) die "不支持的配置：$CONFIGURATION（只支持 release 或 debug）" ;;
esac

[[ -f "$INFO_TEMPLATE" ]] || die "找不到 Info.plist 模板：$INFO_TEMPLATE"
[[ -f "$VERSION_PROPS" ]] || die "找不到版本配置：$VERSION_PROPS"

host_rid() {
  case "$(uname -m)" in
    arm64) printf 'osx-arm64\n' ;;
    x86_64) printf 'osx-x64\n' ;;
    *) printf '\n' ;;
  esac
}

# ---------------------------------------------------------------- 版本号

read_props_value() {
  local key="$1"
  local value
  value="$(
    sed -n "s|.*<$key>\([^<]*\)</$key>.*|\1|p" "$VERSION_PROPS" | head -n 1
  )"
  printf '%s\n' "$value"
}

VERSION_PREFIX="$(read_props_value VersionPrefix)"
VERSION_SUFFIX="$(read_props_value VersionSuffix)"
[[ -n "$VERSION_PREFIX" ]] || die "$VERSION_PROPS 里没有找到 <VersionPrefix>"
VERSION="$VERSION_PREFIX"
if [[ -n "$VERSION_SUFFIX" ]]; then
  VERSION="$VERSION_PREFIX-$VERSION_SUFFIX"
fi

BUILD_NUMBER="$(git -C "$REPO_ROOT" rev-list --count HEAD 2>/dev/null | head -n 1 || true)"
case "$BUILD_NUMBER" in
  ''|*[!0-9]*) BUILD_NUMBER=1 ;;
esac
[[ "$BUILD_NUMBER" -ge 1 ]] || BUILD_NUMBER=1

COPYRIGHT="Copyright © $(date +%Y) Disc Image Studio contributors · GPL-3.0-or-later"

note "仓库          : $REPO_ROOT"
note "配置          : $CONFIGURATION"
note "版本          : $VERSION（CFBundleVersion=$BUILD_NUMBER）"
note ""

# ---------------------------------------------------------------- Swift 构建

note "==> swift build -c $CONFIGURATION --package-path macos"
swift build -c "$CONFIGURATION" --package-path "$MACOS_DIR"

BIN_DIR="$(swift build -c "$CONFIGURATION" --package-path "$MACOS_DIR" --show-bin-path)"
BIN_DIR="${BIN_DIR%$'\r'}"
EXECUTABLE="$BIN_DIR/$BUNDLE_EXECUTABLE"
[[ -f "$EXECUTABLE" ]] || die "swift build 成功但没找到可执行文件：$EXECUTABLE"
note "Swift 产物    : $EXECUTABLE"

# ---------------------------------------------------------------- 组装 .app

note ""
note "==> 组装 $APP"
rm -rf -- "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp -p "$EXECUTABLE" "$APP/Contents/MacOS/$BUNDLE_EXECUTABLE"
chmod +x "$APP/Contents/MacOS/$BUNDLE_EXECUTABLE"

# Info.plist（替换模板占位符）
sed -e "s|__VERSION__|$VERSION|g" \
    -e "s|__BUILD_NUMBER__|$BUILD_NUMBER|g" \
    -e "s|__COPYRIGHT__|$COPYRIGHT|g" \
    "$INFO_TEMPLATE" >"$APP/Contents/Info.plist"

if command -v plutil >/dev/null 2>&1; then
  plutil -lint "$APP/Contents/Info.plist" >/dev/null || die "生成的 Info.plist 不是合法 plist"
fi
note "Info.plist    : ok"

# AppIcon.icns（sips 生成 iconset → iconutil 合成，随后清理）
ICONSET_DIR="$MACOS_DIR/build/AppIcon.iconset"
ICON_TMP="$MACOS_DIR/build/AppIcon.icns"
if [[ -f "$ICON_SOURCE" ]] && command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1; then
  rm -rf -- "$ICONSET_DIR"
  mkdir -p "$ICONSET_DIR"
  icon_ok=1
  for spec in 16:16x16 32:16x16@2x 32:32x32 64:32x32@2x 128:128x128 256:128x128@2x \
              256:256x256 512:256x256@2x 512:512x512 1024:512x512@2x; do
    size="${spec%%:*}"
    name="${spec#*:}"
    if ! sips -z "$size" "$size" "$ICON_SOURCE" --out "$ICONSET_DIR/icon_$name.png" >/dev/null 2>&1; then
      icon_ok=0
      warn "sips 生成 icon_$name.png 失败"
      break
    fi
  done
  if [[ "$icon_ok" -eq 1 ]] && iconutil -c icns "$ICONSET_DIR" -o "$ICON_TMP" >/dev/null 2>&1; then
    cp -p "$ICON_TMP" "$APP/Contents/Resources/AppIcon.icns"
    note "AppIcon.icns  : ok（源图 256×256，512/1024 为放大生成）"
  else
    warn "无法生成 AppIcon.icns，应用将使用系统默认图标（不影响运行）"
  fi
  rm -rf -- "$ICONSET_DIR" "$ICON_TMP"
else
  warn "缺少源图标或 sips/iconutil，跳过 AppIcon.icns（不影响运行）"
fi

# ---------------------------------------------------------------- 内置 CLI

resolve_cli_path() {
  if [[ -n "$CLI_OVERRIDE" ]]; then
    case "$CLI_OVERRIDE" in
      /*) printf '%s\n' "$CLI_OVERRIDE" ;;
      *) printf '%s\n' "$REPO_ROOT/$CLI_OVERRIDE" ;;
    esac
    return 0
  fi

  local rid candidate
  rid="$(host_rid)"
  candidate="$MACOS_DIR/build/cli/${rid:-osx-arm64}/disc-image-studio"
  if [[ -n "$rid" && -f "$candidate" ]]; then
    printf '%s\n' "$candidate"
    return 0
  fi

  # 兜底：build/cli 下唯一的 disc-image-studio（例如手动构建了另一种架构）
  local matches=("$MACOS_DIR"/build/cli/*/disc-image-studio)
  if [[ ${#matches[@]} -eq 1 && -f "${matches[0]}" ]]; then
    printf '%s\n' "${matches[0]}"
    return 0
  fi
  return 1
}

if [[ "$BUNDLE_CLI" -eq 1 ]]; then
  if CLI_PATH="$(resolve_cli_path)"; then
    if [[ ! -f "$CLI_PATH" ]]; then
      die "--cli 指定的路径不存在：$CLI_PATH"
    fi
    [[ -x "$CLI_PATH" ]] || chmod +x "$CLI_PATH" 2>/dev/null || true
    mkdir -p "$APP/Contents/Resources/cli"
    cp -p "$CLI_PATH" "$APP/Contents/Resources/cli/disc-image-studio"
    chmod +x "$APP/Contents/Resources/cli/disc-image-studio"

    # 架构不匹配只警告：Rosetta 或跨机能跑起来，但运行会失败，提示用户排查。
    cli_binary="$APP/Contents/Resources/cli/disc-image-studio"
    cli_archs=""
    if command -v lipo >/dev/null 2>&1; then
      cli_archs="$(lipo -archs "$cli_binary" 2>/dev/null || true)"
    fi
    if [[ -z "$cli_archs" ]]; then
      cli_archs="$(file -b "$cli_binary" 2>/dev/null || true)"
    fi
    case "$(uname -m)" in
      arm64)
        [[ "$cli_archs" == *arm64* ]] \
          || warn "内置 CLI 不含 arm64 架构（$cli_archs），在 Apple Silicon 上需要 Rosetta；建议用 --rid osx-arm64 重新构建。"
        ;;
      x86_64)
        [[ "$cli_archs" == *x86_64* ]] \
          || warn "内置 CLI 不含 x86_64 架构（$cli_archs），在 Intel 上无法直接运行；建议用 --rid osx-x64 重新构建。"
        ;;
    esac

    for doc in LICENSE THIRD-PARTY-NOTICES.txt; do
      if [[ -f "$REPO_ROOT/$doc" ]]; then
        cp -p "$REPO_ROOT/$doc" "$APP/Contents/Resources/$doc"
      else
        warn "缺少 $doc，发布包可能不满足 GPL 的声明要求"
      fi
    done
    note "内置 CLI      : $CLI_PATH"
    note "许可证        : LICENSE、THIRD-PARTY-NOTICES.txt"
    BUNDLED_CLI_PATH="$APP/Contents/Resources/cli/disc-image-studio"
  else
    warn "没找到 CLI（macos/build/cli/<rid>/disc-image-studio），应用能启动但无法执行任务。"
    warn "先运行 macos/scripts/build-cli.sh，或用 --cli <path> 指定。"
    note "内置 CLI      : 未包含"
  fi
else
  note "内置 CLI      : 已跳过（--no-cli），UI 仍可启动，只是找不到 CLI"
fi

# ---------------------------------------------------------------- 流式刻录助手

# disc-burn-stream 是 Objective-C 写的原生流式刻录助手（DRTrack producer + DRBurn），
# 不依赖 .NET，因此 --no-cli 时仍然构建并内置它。
BURN_HELPER_PRODUCT="disc-burn-stream"
if swift package dump-package --package-path "$MACOS_DIR" 2>/dev/null \
    | grep -q "\"$BURN_HELPER_PRODUCT\""; then
  note ""
  note "==> swift build --product $BURN_HELPER_PRODUCT（流式刻录助手）"
  if ! swift build -c "$CONFIGURATION" --package-path "$MACOS_DIR" --product "$BURN_HELPER_PRODUCT"; then
    die "disc-burn-stream 构建失败（文件模式仍可用，但流式刻录会缺少助手）。"
  fi
  HELPER_EXECUTABLE="$BIN_DIR/$BURN_HELPER_PRODUCT"
  if [[ -f "$HELPER_EXECUTABLE" ]]; then
    mkdir -p "$APP/Contents/Resources/bin"
    cp -p "$HELPER_EXECUTABLE" "$APP/Contents/Resources/bin/$BURN_HELPER_PRODUCT"
    chmod +x "$APP/Contents/Resources/bin/$BURN_HELPER_PRODUCT"
    BUNDLED_HELPER_PATH="$APP/Contents/Resources/bin/$BURN_HELPER_PRODUCT"
    note "内置 helper   : $BUNDLED_HELPER_PATH"
  else
    warn "swift build 完成但没找到 $HELPER_EXECUTABLE，跳过流式刻录助手"
  fi
else
  warn "Package.swift 未声明 $BURN_HELPER_PRODUCT product，跳过流式刻录助手（文件模式仍可用）"
  note "内置 helper   : 未包含"
fi

# ---------------------------------------------------------------- 签名

if [[ "$SIGN" -eq 1 ]]; then
  note ""
  note "==> ad-hoc 代码签名（codesign --sign -）"
  codesign --force --deep --sign - "$APP" || die "codesign 失败；可加 --no-sign 跳过（仅用于排障）"
  if command -v codesign >/dev/null 2>&1; then
    codesign --verify --deep --strict "$APP" >/dev/null 2>&1 \
      && note "签名校验      : ok" \
      || warn "codesign --verify 未通过（ad-hoc 签名通常仍可本机运行）"
  fi
else
  note ""
  note "跳过代码签名（--no-sign）"
fi

# ---------------------------------------------------------------- 汇总

APP_SIZE="$(du -sh "$APP" | awk '{print $1}')"
note ""
note "完成。"
note "应用          : $APP"
note "大小          : $APP_SIZE"
note "内置 CLI      : ${BUNDLED_CLI_PATH:-未包含}"
note "刻录助手      : ${BUNDLED_HELPER_PATH:-未包含}"
note "启动          : open \"$APP\""
