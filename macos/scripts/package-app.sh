#!/usr/bin/env bash
#
# 把 macos/build/DiscImageStudio.app 打包成可分发的 zip，并生成 SHA-256。
#
# 产物：
#   macos/build/DiscImageStudio-<version>-<rid>.zip
#   macos/build/DiscImageStudio-<version>-<rid>.zip.sha256
#
# 用法：
#   macos/scripts/package-app.sh [--rid osx-arm64|osx-x64]
#                               [--app <path>] [--output <dir>]
#                               [-h|--help]
#
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
MACOS_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
REPO_ROOT="$(cd -- "$MACOS_DIR/.." && pwd -P)"

DEFAULT_APP="$MACOS_DIR/build/DiscImageStudio.app"
VERSION_PROPS="$REPO_ROOT/Directory.Build.props"

usage() {
  cat <<'EOF'
用法：macos/scripts/package-app.sh [选项]

用 ditto 把 DiscImageStudio.app 压缩为 zip，并写同名 .sha256 校验文件。

选项：
  --rid <rid>       目标运行时：osx-arm64 或 osx-x64。
                    默认按 uname -m 推断（只影响文件名，不改变包内容）。
  --app <path>      要打包的 .app 路径。默认 macos/build/DiscImageStudio.app。
  --output <dir>    输出目录。默认 macos/build。
  -h, --help        显示本帮助。

示例：
  macos/scripts/package-app.sh
  macos/scripts/package-app.sh --rid osx-x64 --output /tmp/dist
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
      printf 'error: 无法从 uname -m（%s）推断 RID，请用 --rid 指定。\n' "$(uname -m)" >&2
      return 1
      ;;
  esac
}

RID=""
APP_PATH="$DEFAULT_APP"
OUTPUT_DIR="$MACOS_DIR/build"

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
    --app)
      [[ $# -ge 2 && -n "$2" ]] || die "--app 需要一个 .app 路径"
      APP_PATH="$2"
      shift 2
      ;;
    --app=*)
      APP_PATH="${1#*=}"
      shift
      ;;
    --output)
      [[ $# -ge 2 && -n "$2" ]] || die "--output 需要一个目录"
      OUTPUT_DIR="$2"
      shift 2
      ;;
    --output=*)
      OUTPUT_DIR="${1#*=}"
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

[[ -n "$RID" ]] || RID="$(default_rid)" || exit 1
case "$RID" in
  osx-arm64|osx-x64) ;;
  *) die "不支持的 RID：${RID}（只支持 osx-arm64 或 osx-x64）" ;;
esac

case "$APP_PATH" in
  /*) ;;
  *) APP_PATH="$REPO_ROOT/$APP_PATH" ;;
esac
case "$OUTPUT_DIR" in
  /*) ;;
  *) OUTPUT_DIR="$REPO_ROOT/$OUTPUT_DIR" ;;
esac

[[ -d "$APP_PATH" ]] || die "找不到 .app：${APP_PATH}（先运行 macos/scripts/build-app.sh）"
[[ -f "$VERSION_PROPS" ]] || die "找不到版本配置：$VERSION_PROPS"

VERSION_PREFIX="$(
  sed -n 's|.*<VersionPrefix>\([^<]*\)</VersionPrefix>.*|\1|p' "$VERSION_PROPS" | head -n 1
)"
VERSION_SUFFIX="$(
  sed -n 's|.*<VersionSuffix>\([^<]*\)</VersionSuffix>.*|\1|p' "$VERSION_PROPS" | head -n 1
)"
[[ -n "$VERSION_PREFIX" ]] || die "$VERSION_PROPS 里没有找到 <VersionPrefix>"
VERSION="$VERSION_PREFIX"
if [[ -n "$VERSION_SUFFIX" ]]; then
  VERSION="$VERSION_PREFIX-$VERSION_SUFFIX"
fi

mkdir -p "$OUTPUT_DIR"
ARCHIVE_NAME="DiscImageStudio-$VERSION-$RID.zip"
ARCHIVE="$OUTPUT_DIR/$ARCHIVE_NAME"

note "==> 打包 $APP_PATH"
rm -f -- "$ARCHIVE" "$ARCHIVE.sha256"
# --norsrc / --noextattr：不把 com.apple.provenance 等扩展属性写成 zip 里的
# ._* AppleDouble 条目，解包后的目录更干净（不影响代码签名有效性）。
ditto -c -k --keepParent --norsrc --noextattr "$APP_PATH" "$ARCHIVE"

[[ -f "$ARCHIVE" ]] || die "ditto 没有生成 $ARCHIVE"

# 校验和只写文件名，方便在发布页统一校验（与 CI 的 sha256 格式一致）。
(
  cd "$OUTPUT_DIR"
  shasum -a 256 "$ARCHIVE_NAME" >"$ARCHIVE_NAME.sha256"
)

ARCHIVE_SIZE="$(du -h "$ARCHIVE" | awk '{print $1}')"
CHECKSUM="$(awk '{print $1}' "$OUTPUT_DIR/$ARCHIVE_NAME.sha256")"

note ""
note "完成。"
note "压缩包  : $ARCHIVE"
note "大小    : $ARCHIVE_SIZE"
note "校验和  : $OUTPUT_DIR/$ARCHIVE_NAME.sha256"
note "SHA-256 : $CHECKSUM"
