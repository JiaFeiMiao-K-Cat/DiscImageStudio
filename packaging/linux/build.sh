#!/usr/bin/env bash
# Builds the Linux desktop app (GUI + CLI in one binary) as a self-contained bundle that runs
# on any distribution with the matching C library, then packs it with its installer.
#
#   packaging/linux/build.sh                   # build for this machine, write a .tar.gz
#   packaging/linux/build.sh --install         # build, then install for the current user
#   packaging/linux/build.sh --rid linux-arm64 # cross-build (x64 → arm64 works)
#
# Output: artifacts/linux/DiscImageStudio-<version>-<rid>/ and the matching .tar.gz.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/src/DiscImageStudio.Linux/DiscImageStudio.Linux.csproj"
DATA="$ROOT/packaging/linux/data"
APP_ID="io.github.JiaFeiMiao_K_Cat.DiscImageStudio"

rid=""
output="$ROOT/artifacts/linux"
archive=1
install_after=0
install_args=()
self_contained=true
ready_to_run=true
configuration=Release

usage() {
  sed -n '2,10p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  cat <<'USAGE'
Options:
  --rid RID              linux-x64, linux-arm64, linux-musl-x64 or linux-musl-arm64
                         (default: detected from this machine)
  --output DIR           where to write the bundle (default: artifacts/linux)
  --no-archive           keep the bundle directory, skip the .tar.gz
  --install [ARGS...]    run the bundle's install.sh afterwards; remaining arguments are
                         passed to it (for example --install --prefix /usr/local)
  --framework-dependent  use the system .NET 10 runtime instead of bundling it
  --no-ready-to-run      skip ahead-of-time compilation (smaller, slower start-up)
  -h, --help             show this help
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid) rid="${2:?--rid needs a value}"; shift 2 ;;
    --output) output="${2:?--output needs a value}"; shift 2 ;;
    --no-archive) archive=0; shift ;;
    --install) install_after=1; shift; install_args=("$@"); break ;;
    --framework-dependent) self_contained=false; shift ;;
    --no-ready-to-run) ready_to_run=false; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "build.sh: unknown option '$1' (see --help)" >&2; exit 2 ;;
  esac
done

die() { echo "build.sh: $*" >&2; exit 1; }

command -v dotnet >/dev/null 2>&1 || die "the .NET SDK is not installed. Install .NET 10 (see docs/LINUX.md), for example:
  Arch:          sudo pacman -S dotnet-sdk
  Debian/Ubuntu: sudo apt install dotnet-sdk-10.0
  Fedora:        sudo dnf install dotnet-sdk-10.0
  Any distro:    https://learn.microsoft.com/dotnet/core/install/linux-scripted-manual"

# global.json pins the 10.0 feature band; say so plainly instead of the SDK resolver's error.
if ! (cd "$ROOT" && dotnet --version >/dev/null 2>&1); then
  die "no .NET 10 SDK was found (installed: $(dotnet --list-sdks 2>/dev/null | awk '{print $1}' | paste -sd' ' -)).
Install the 10.0 SDK side by side, for example 'sudo pacman -S dotnet-sdk-10.0' on Arch once
.NET 11 is the default, or ./dotnet-install.sh --channel 10.0 (https://dot.net/v1/dotnet-install.sh)."
fi

if [[ -z "$rid" ]]; then
  case "$(uname -m)" in
    x86_64|amd64) arch=x64 ;;
    aarch64|arm64) arch=arm64 ;;
    *) die "unsupported CPU '$(uname -m)'; pass --rid explicitly" ;;
  esac
  if ldd --version 2>&1 | grep -qi musl; then rid="linux-musl-$arch"; else rid="linux-$arch"; fi
fi

version="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$ROOT/Directory.Build.props" | head -n1)"
suffix="$(sed -n 's:.*<VersionSuffix>\(.*\)</VersionSuffix>.*:\1:p' "$ROOT/Directory.Build.props" | head -n1)"
[[ -n "$suffix" ]] && version="$version-$suffix"
[[ -n "$version" ]] || die "cannot read the version from Directory.Build.props"

name="DiscImageStudio-$version-$rid"
stage="$output/$name"
rm -rf "$stage"
mkdir -p "$stage"

echo "==> Publishing Disc Image Studio $version for $rid (self-contained: $self_contained)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
publish_args=(
  publish "$PROJECT"
  -c "$configuration"
  -r "$rid"
  --self-contained "$self_contained"
  -o "$stage/app"
  -p:PublishReadyToRun="$ready_to_run"
  -p:DebugType=embedded
)
dotnet "${publish_args[@]}"
chmod 755 "$stage/app/disc-image-studio-gui"

echo "==> Adding desktop integration files"
install -Dm644 "$DATA/$APP_ID.desktop" "$stage/share/applications/$APP_ID.desktop"
install -Dm644 "$DATA/$APP_ID.metainfo.xml" "$stage/share/metainfo/$APP_ID.metainfo.xml"
install -Dm644 "$DATA/$APP_ID.mime.xml" "$stage/share/mime/packages/$APP_ID.xml"
(cd "$DATA" && find icons -name '*.png') | while read -r icon; do
  install -Dm644 "$DATA/$icon" "$stage/share/$icon"
done
install -m755 "$ROOT/packaging/linux/install.sh" "$stage/install.sh"
install -m644 "$ROOT/LICENSE" "$ROOT/LICENSE-NOTICE.md" "$ROOT/THIRD-PARTY-NOTICES.txt" "$stage/"
cat > "$stage/README.txt" <<README
Disc Image Studio $version for Linux ($rid)

Install for your user (no root needed):     ./install.sh
Install for all users:                      sudo ./install.sh
Uninstall:                                  ./install.sh --uninstall   (sudo for a system install)

Then start "Disc Image Studio" from the application menu, or run disc-image-studio-gui.
The command-line tool is disc-image-studio (same engines; run it with --help).

Full instructions: https://github.com/oreo1298/discimagestudio/blob/main/docs/LINUX.md
README

echo "==> Smoke test"
"$stage/app/disc-image-studio-gui" --version

if [[ $archive -eq 1 ]]; then
  tarball="$output/DiscImageStudio-Linux-v$version-$rid.tar.gz"
  tar -C "$output" -czf "$tarball" "$name"
  (cd "$output" && sha256sum "$(basename "$tarball")" > "$(basename "$tarball").sha256")
  echo "==> Wrote $tarball"
fi

echo "==> Bundle: $stage"
if [[ $install_after -eq 1 ]]; then
  "$stage/install.sh" "${install_args[@]}"
fi
