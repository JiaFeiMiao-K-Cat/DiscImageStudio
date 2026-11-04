#!/usr/bin/env bash
#
# 用 notarytool 给已用 Developer ID 签名的 .app 或 .dmg 做公证，并 stapling。
#
# 凭据（三选一）：
#   1) NOTARYTOOL_PROFILE（`xcrun notarytool store-credentials` 保存的钥匙串 profile）
#   2) APPLE_API_KEY_PATH + APPLE_API_KEY_ID + APPLE_API_ISSUER_ID（App Store Connect API Key）
#   3) APPLE_ID + APPLE_APP_SPECIFIC_PASSWORD + APPLE_TEAM_ID（Apple ID + App 专用密码）
#
# 用法：
#   macos/scripts/notarize.sh --app macos/build/DiscImageStudio.app
#   macos/scripts/notarize.sh --app macos/build/DiscImageStudio-1.3.6-osx-arm64.dmg
#   macos/scripts/notarize.sh --app ... --keychain-profile disc-image-studio
#   APPLE_ID=... APPLE_APP_SPECIFIC_PASSWORD=... APPLE_TEAM_ID=... macos/scripts/notarize.sh --app ...
#
# 退出码：0 成功（已 Accepted + staple 验证通过）；1 签名/公证失败；2 参数或凭据问题。
#
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
DEFAULT_APP="$SCRIPT_DIR/../build/DiscImageStudio.app"

APP_PATH="${DEFAULT_APP}"
PROFILE="${NOTARYTOOL_PROFILE:-}"
APPLE_ID_VALUE="${APPLE_ID:-}"
APPLE_PASSWORD_VALUE="${APPLE_APP_SPECIFIC_PASSWORD:-}"
TEAM_ID_VALUE="${APPLE_TEAM_ID:-}"
API_KEY_PATH="${APPLE_API_KEY_PATH:-}"
API_KEY_ID="${APPLE_API_KEY_ID:-}"
API_ISSUER="${APPLE_API_ISSUER_ID:-}"

usage() {
  cat <<'EOF'
用法：macos/scripts/notarize.sh [选项]

对已用 Developer ID 签名的 .app（要求 hardened runtime）或 .dmg 执行：
  校验签名 → notarytool submit --wait → stapler staple → stapler validate → spctl。
  .app 会先压成临时 zip 再提交；.dmg 直接提交（并校验其代码签名）。

选项：
  --app <path>              应用或 DMG 路径（默认 macos/build/DiscImageStudio.app）。
  --keychain-profile <name> notarytool 凭据 profile（等价于环境变量 NOTARYTOOL_PROFILE）。
  --apple-id <id>           Apple ID（等价于 APPLE_ID）。
  --password <password>     App 专用密码（等价于 APPLE_APP_SPECIFIC_PASSWORD）。
  --team-id <id>            Apple Team ID（等价于 APPLE_TEAM_ID）。
  --key <path>              App Store Connect API Key 私钥（.p8）。
  --key-id <id>             API Key ID。
  --issuer <id>             API Key Issuer ID。
  -h, --help                显示本帮助。

凭据优先级：--keychain-profile/NOTARYTOOL_PROFILE → API Key → Apple ID + 专用密码。
EOF
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit "${2:-1}"
}

note() {
  printf '%s\n' "$*"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --app)
      [[ $# -ge 2 && -n "$2" ]] || die "--app 需要一个路径" 2
      APP_PATH="$2"
      shift 2
      ;;
    --app=*)
      APP_PATH="${1#*=}"
      shift
      ;;
    --keychain-profile)
      [[ $# -ge 2 && -n "$2" ]] || die "--keychain-profile 需要一个名称" 2
      PROFILE="$2"
      shift 2
      ;;
    --keychain-profile=*)
      PROFILE="${1#*=}"
      shift
      ;;
    --apple-id)
      [[ $# -ge 2 && -n "$2" ]] || die "--apple-id 需要一个值" 2
      APPLE_ID_VALUE="$2"
      shift 2
      ;;
    --apple-id=*)
      APPLE_ID_VALUE="${1#*=}"
      shift
      ;;
    --password)
      [[ $# -ge 2 ]] || die "--password 需要一个值" 2
      APPLE_PASSWORD_VALUE="$2"
      shift 2
      ;;
    --password=*)
      APPLE_PASSWORD_VALUE="${1#*=}"
      shift
      ;;
    --team-id)
      [[ $# -ge 2 && -n "$2" ]] || die "--team-id 需要一个值" 2
      TEAM_ID_VALUE="$2"
      shift 2
      ;;
    --team-id=*)
      TEAM_ID_VALUE="${1#*=}"
      shift
      ;;
    --key)
      [[ $# -ge 2 && -n "$2" ]] || die "--key 需要一个路径" 2
      API_KEY_PATH="$2"
      shift 2
      ;;
    --key-id)
      [[ $# -ge 2 && -n "$2" ]] || die "--key-id 需要一个值" 2
      API_KEY_ID="$2"
      shift 2
      ;;
    --issuer)
      [[ $# -ge 2 && -n "$2" ]] || die "--issuer 需要一个值" 2
      API_ISSUER="$2"
      shift 2
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

[[ -e "$APP_PATH" ]] || die "找不到应用或 DMG：$APP_PATH" 2
command -v xcrun >/dev/null 2>&1 || die "找不到 xcrun（需要 Xcode 或 Command Line Tools）" 2

IS_DMG=0
case "$APP_PATH" in
  *.dmg) IS_DMG=1 ;;
  *.app) IS_DMG=0 ;;
  *) die "只支持 .app 或 .dmg：$APP_PATH" 2 ;;
esac

# 1. 必须是 Developer ID 签名；.app 还要求 hardened runtime。
SIGN_INFO="$(codesign -dv --verbose=4 "$APP_PATH" 2>&1 || true)"
if ! grep -q "Authority=Developer ID Application" <<<"$SIGN_INFO"; then
  if [[ "$IS_DMG" -eq 1 ]]; then
    die "DMG 不是 Developer ID 签名，无法公证。请先：
  macos/scripts/package-app.sh --sign-identity \"Developer ID Application: ...\""
  else
    die "应用不是 Developer ID 签名，无法公证。请先运行：
  macos/scripts/build-app.sh --sign-identity \"Developer ID Application: ...\""
  fi
fi
if [[ "$IS_DMG" -eq 0 ]] && ! grep -q "flags=.*runtime" <<<"$SIGN_INFO"; then
  die "应用没有启用 hardened runtime（codesign --options runtime），无法公证。"
fi
codesign --verify --deep --strict --verbose=2 "$APP_PATH" >/dev/null 2>&1 \
  || die "codesign --verify 失败；请重新签名后再公证。"

# 2. 组装 notarytool 凭据。
NOTARY_ARGS=()
if [[ -n "$PROFILE" ]]; then
  NOTARY_ARGS+=(--keychain-profile "$PROFILE")
  note "凭据          : keychain profile \"$PROFILE\""
elif [[ -n "$API_KEY_PATH" && -n "$API_KEY_ID" && -n "$API_ISSUER" ]]; then
  [[ -f "$API_KEY_PATH" ]] || die "找不到 API Key 私钥：$API_KEY_PATH" 2
  NOTARY_ARGS+=(--key "$API_KEY_PATH" --key-id "$API_KEY_ID" --issuer "$API_ISSUER")
  note "凭据          : App Store Connect API Key（${API_KEY_ID}）"
elif [[ -n "$APPLE_ID_VALUE" && -n "$APPLE_PASSWORD_VALUE" && -n "$TEAM_ID_VALUE" ]]; then
  NOTARY_ARGS+=(--apple-id "$APPLE_ID_VALUE" --password "$APPLE_PASSWORD_VALUE" --team-id "$TEAM_ID_VALUE")
  note "凭据          : Apple ID（${APPLE_ID_VALUE}，Team ${TEAM_ID_VALUE}）"
else
  die "缺少公证凭据。设置 NOTARYTOOL_PROFILE，或 APPLE_API_KEY_PATH/APPLE_API_KEY_ID/APPLE_API_ISSUER_ID，或 APPLE_ID/APPLE_APP_SPECIFIC_PASSWORD/APPLE_TEAM_ID。" 2
fi

# 3. notarytool 只接受 zip / dmg / pkg；.app 先压成 zip，.dmg 直接提交。
TMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/disc-image-studio-notarize.XXXXXX")"
trap 'rm -rf "$TMP_DIR"' EXIT
if [[ "$IS_DMG" -eq 1 ]]; then
  ARCHIVE="$APP_PATH"
  note "==> 提交 DMG（$ARCHIVE）"
else
  ARCHIVE="$TMP_DIR/DiscImageStudio.zip"
  note "==> 打包待公证应用"
  ditto -c -k --keepParent --norsrc --noextattr "$APP_PATH" "$ARCHIVE"
fi

# 4. 提交并等待结果。
note "==> notarytool submit --wait"
set +e
SUBMIT_OUTPUT="$(xcrun notarytool submit "$ARCHIVE" "${NOTARY_ARGS[@]}" --wait --output-format json 2>&1)"
SUBMIT_STATUS=$?
set -e
printf '%s\n' "$SUBMIT_OUTPUT"
if [[ $SUBMIT_STATUS -ne 0 ]]; then
  die "notarytool submit 失败（退出码 ${SUBMIT_STATUS}）。" 1
fi

STATUS="$(python3 -c 'import json,sys; print(json.load(sys.stdin).get("status",""))' <<<"$SUBMIT_OUTPUT" 2>/dev/null || true)"
SUBMISSION_ID="$(python3 -c 'import json,sys; print(json.load(sys.stdin).get("id",""))' <<<"$SUBMIT_OUTPUT" 2>/dev/null || true)"
if [[ -z "$STATUS" ]]; then
  # 老版本 notarytool 或输出被包裹时退回文本匹配。
  if grep -q '"status"[[:space:]]*:[[:space:]]*"Accepted"' <<<"$SUBMIT_OUTPUT"; then
    STATUS="Accepted"
  fi
fi
if [[ "$STATUS" != "Accepted" ]]; then
  if [[ -n "$SUBMISSION_ID" ]]; then
    note "==> notarytool log（${SUBMISSION_ID}）"
    xcrun notarytool log "$SUBMISSION_ID" "${NOTARY_ARGS[@]}" || true
  fi
  die "公证未通过：${STATUS:-unknown}。" 1
fi
note "公证状态      : Accepted（${SUBMISSION_ID}）"

# 5. stapling + 验证。
note "==> stapler staple"
xcrun stapler staple "$APP_PATH"
note "==> stapler validate"
xcrun stapler validate "$APP_PATH"
note "==> spctl 评估"
if [[ "$IS_DMG" -eq 1 ]]; then
  if spctl --assess --type open --context context:primary-signature --verbose=2 "$APP_PATH" 2>&1; then
    note "Gatekeeper    : accepted"
  else
    die "DMG 的 spctl 评估未通过；公证已 Accepted 但 stapling/Gatekeeper 检查失败。" 1
  fi
else
  if spctl --assess --type execute --verbose=2 "$APP_PATH" 2>&1; then
    note "Gatekeeper    : accepted"
  else
    die "spctl 评估未通过；公证已 Accepted 但 stapling/Gatekeeper 检查失败。" 1
  fi
fi

note ""
note "完成。"
note "应用/DMG      : $APP_PATH"
note "提交 ID       : $SUBMISSION_ID"
note "状态          : Accepted + stapled"
