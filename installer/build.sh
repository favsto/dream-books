#!/usr/bin/env bash
# Builds the per-user installer for one architecture: publish (self-contained), compile the Inno script, check the result.
# Usage: installer/build.sh [x64|arm64]   (default x64; output in artifacts/installers)
set -euo pipefail

arch="${1:-x64}"
case "$arch" in x64|arm64) ;; *) echo "Architecture must be x64 or arm64" >&2; exit 2 ;; esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
csproj="$root/src/DreamBooks.App/DreamBooks.App.csproj"
script="$root/installer/DreamBooks.iss"
iscc="$LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe"

[ -f "$iscc" ] || { echo "Inno Setup not found at $iscc" >&2; exit 1; }
[ -f "$script" ] || { echo "Installer script not found: $script" >&2; exit 1; }

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$csproj" | head -1)"
[ -n "$version" ] || { echo "No <Version> in the app csproj" >&2; exit 1; }

# The script must keep its promises (the installer itself can only be tried on a device).
grep -q 'PrivilegesRequired=lowest' "$script" || { echo "The installer must be per user (PrivilegesRequired=lowest)" >&2; exit 1; }
if grep -qiE 'HKLM|HKEY_LOCAL_MACHINE|\{commonpf|\{commondesktop|\{commonprograms' "$script"; then
  echo "The installer must not write machine-wide" >&2; exit 1
fi

publish="$root/artifacts/publish/$arch"
dotnet publish "$root/src/DreamBooks.App" -c Release -r "win-$arch" --self-contained -o "$publish"

win() { cygpath -w "$1"; }
MSYS2_ARG_CONV_EXCL="*" "$iscc" "/DAppVersion=$version" "/DArch=$arch" "/DSourceDir=$(win "$publish")" "$(win "$script")"

setup="$root/artifacts/installers/DreamBooks-$version-$arch-setup.exe"
[ -f "$setup" ] || { echo "Expected output missing: $setup" >&2; exit 1; }
product="$(powershell -NoProfile -Command "(Get-Item '$(win "$setup")').VersionInfo.ProductVersion" | tr -d "[:space:]")"
case "$product" in "$version"*) ;; *) echo "Installer version '$product' does not match $version" >&2; exit 1 ;; esac

echo "OK: $setup ($(du -m "$setup" | cut -f1) MB, version $product)"
