#!/bin/sh
# Installs (or removes) the Disc Image Studio bundle this script ships with.
#
#   ./install.sh                     current user: ~/.local (no root needed)
#   sudo ./install.sh                all users: /usr/local
#   ./install.sh --prefix /opt/dis   any prefix
#   ./install.sh --uninstall         remove it again (same prefix as the install)
#
# Packagers: ./install.sh --prefix /usr --destdir "$pkgdir" --no-post-install
set -eu

APP_ID="io.github.JiaFeiMiao_K_Cat.DiscImageStudio"
HERE="$(cd "$(dirname "$0")" && pwd)"

if [ "$(id -u)" -eq 0 ]; then prefix="/usr/local"; else prefix="$HOME/.local"; fi
destdir=""
uninstall=0
post_install=1

usage() { sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; }

while [ $# -gt 0 ]; do
  case "$1" in
    --prefix) prefix="${2:?--prefix needs a value}"; shift 2 ;;
    --prefix=*) prefix="${1#*=}"; shift ;;
    --destdir) destdir="${2:?--destdir needs a value}"; shift 2 ;;
    --destdir=*) destdir="${1#*=}"; shift ;;
    --uninstall) uninstall=1; shift ;;
    --no-post-install) post_install=0; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "install.sh: unknown option '$1' (see --help)" >&2; exit 2 ;;
  esac
done

case "$prefix" in /*) ;; *) echo "install.sh: --prefix must be an absolute path" >&2; exit 2 ;; esac
prefix="${prefix%/}"
[ -n "$destdir" ] && post_install=0

appdir="$prefix/lib/disc-image-studio"
bindir="$prefix/bin"
share="$prefix/share"
root="$destdir"

refresh_caches() {
  [ "$post_install" -eq 1 ] || return 0
  if command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database -q "$share/applications" 2>/dev/null || true; fi
  if command -v update-mime-database >/dev/null 2>&1; then update-mime-database "$share/mime" >/dev/null 2>&1 || true; fi
  if command -v gtk-update-icon-cache >/dev/null 2>&1 && [ -f "$share/icons/hicolor/index.theme" ]; then
    gtk-update-icon-cache -q -t -f "$share/icons/hicolor" 2>/dev/null || true
  fi
  if command -v xdg-desktop-menu >/dev/null 2>&1; then xdg-desktop-menu forceupdate 2>/dev/null || true; fi
}

remove_files() {
  rm -rf "$root$appdir"
  rm -f "$root$bindir/disc-image-studio-gui" "$root$bindir/disc-image-studio"
  rm -f "$root$share/applications/$APP_ID.desktop" \
        "$root$share/metainfo/$APP_ID.metainfo.xml" \
        "$root$share/mime/packages/$APP_ID.xml"
  for icon in "$root$share"/icons/hicolor/*/apps/"$APP_ID".png; do
    [ -e "$icon" ] && rm -f "$icon"
  done
  return 0
}

if [ "$uninstall" -eq 1 ]; then
  if [ ! -d "$root$appdir" ]; then
    echo "Disc Image Studio is not installed in $prefix (use --prefix, or sudo for /usr/local)." >&2
    exit 1
  fi
  remove_files
  refresh_caches
  echo "Removed Disc Image Studio from $prefix."
  echo "Your projects and settings (~/.config/DiscImageStudio, ~/.local/share/DiscImageStudio) were kept."
  exit 0
fi

[ -x "$HERE/app/disc-image-studio-gui" ] || { echo "install.sh: run this from an extracted Disc Image Studio bundle (app/ is missing)." >&2; exit 1; }

# An upgrade replaces the previous files completely.
remove_files

mkdir -p "$root$appdir" "$root$bindir" "$root$share/applications" "$root$share/metainfo" "$root$share/mime/packages"
cp -R "$HERE/app/." "$root$appdir/"
chmod 755 "$root$appdir/disc-image-studio-gui"
ln -sf "$appdir/disc-image-studio-gui" "$root$bindir/disc-image-studio-gui"

# disc-image-studio keeps the CLI behaviour: without arguments it prints help instead of
# opening a window.
cat > "$root$bindir/disc-image-studio" <<WRAPPER
#!/bin/sh
[ \$# -eq 0 ] && set -- --help
exec "$appdir/disc-image-studio-gui" "\$@"
WRAPPER
chmod 755 "$root$bindir/disc-image-studio"

# Menus do not search ~/.local/bin, so the launcher always points at the real file.
exec_path="$bindir/disc-image-studio-gui"
case "$exec_path" in *" "*) exec_field="\"$exec_path\"" ;; *) exec_field="$exec_path" ;; esac
sed -e "s|^Exec=disc-image-studio-gui|Exec=$exec_field|" \
    -e "s|^TryExec=disc-image-studio-gui|TryExec=$exec_path|" \
    "$HERE/share/applications/$APP_ID.desktop" > "$root$share/applications/$APP_ID.desktop"
chmod 644 "$root$share/applications/$APP_ID.desktop"
cp "$HERE/share/metainfo/$APP_ID.metainfo.xml" "$root$share/metainfo/"
cp "$HERE/share/mime/packages/$APP_ID.xml" "$root$share/mime/packages/"
for icon in "$HERE"/share/icons/hicolor/*/apps/"$APP_ID".png; do
  size_dir="$(basename "$(dirname "$(dirname "$icon")")")"
  mkdir -p "$root$share/icons/hicolor/$size_dir/apps"
  cp "$icon" "$root$share/icons/hicolor/$size_dir/apps/"
done
chmod -R u+rwX,go+rX "$root$appdir" "$root$share/icons/hicolor" 2>/dev/null || true

refresh_caches

echo "Installed Disc Image Studio in $prefix"
echo "  GUI: $bindir/disc-image-studio-gui   (or \"Disc Image Studio\" in the application menu)"
echo "  CLI: $bindir/disc-image-studio --help"

[ -n "$destdir" ] && exit 0

case ":${PATH:-}:" in
  *":$bindir:"*) ;;
  *) echo "Note: $bindir is not on your PATH. Add it to run the commands by name:"
     echo "  echo 'export PATH=\"$bindir:\$PATH\"' >> ~/.profile" ;;
esac

# Avalonia loads these X11 libraries at start-up (XWayland provides them on Wayland desktops).
have_lib() {
  if command -v ldconfig >/dev/null 2>&1 && ldconfig -p 2>/dev/null | grep -q "$1"; then return 0; fi
  for dir in /usr/lib /usr/lib64 /usr/lib/x86_64-linux-gnu /usr/lib/aarch64-linux-gnu /lib /lib64 /usr/local/lib; do
    [ -e "$dir/$1" ] && return 0
  done
  return 1
}
missing=""
for lib in libX11.so.6 libICE.so.6 libSM.so.6 libfontconfig.so.1; do
  have_lib "$lib" || missing="$missing $lib"
done
if [ -n "$missing" ]; then
  echo
  echo "Warning: missing libraries:$missing"
  echo "  Arch:          sudo pacman -S --needed libx11 libice libsm fontconfig"
  echo "  Debian/Ubuntu: sudo apt install libx11-6 libice6 libsm6 libfontconfig1"
  echo "  Fedora:        sudo dnf install libX11 libICE libSM fontconfig"
  echo "  openSUSE:      sudo zypper install libX11-6 libICE6 libSM6 fontconfig"
  echo "  Alpine:        sudo apk add libx11 libice libsm fontconfig font-dejavu"
fi

[ "$(id -u)" -eq 0 ] && exit 0
for group in optical cdrom; do
  if getent group "$group" >/dev/null 2>&1; then
    if ! id -nG 2>/dev/null | tr ' ' '\n' | grep -qx "$group"; then
      echo
      echo "If the Burn page reports no access to the drive, add yourself to the '$group' group,"
      echo "then log out and back in:  sudo usermod -aG $group \"\$USER\""
    fi
    break
  fi
done
