#!/usr/bin/env bash
# Builds the per-user installer for one architecture: publish (self-contained), compile the Inno script, check the result.
# Usage: installer/build.sh [x64|arm64]   (default x64; output in artifacts/installers)
set -euo pipefail

arch="${1:-x64}"
case "$arch" in x64|arm64) ;; *) echo "Architecture must be x64 or arm64" >&2; exit 2 ;; esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
props="$root/Directory.Build.props"
csproj="$root/src/DreamBooks.App/DreamBooks.App.csproj"
script="$root/installer/DreamBooks.iss"

# Git Bash (MSYS) and WSL both run this script: cygpath or wslpath converts Windows paths.
if command -v cygpath >/dev/null 2>&1; then
  win() { cygpath -w "$1"; }
  unix() { cygpath -u "$1"; }
else
  win() { wslpath -w "$1"; }
  unix() { wslpath -u "$1"; }
fi

# LOCALAPPDATA is not exported to WSL: ask Windows for it when it is missing.
local_appdata="${LOCALAPPDATA:-}"
if [ -z "$local_appdata" ]; then
  local_appdata="$(cmd.exe /d /c "echo %LOCALAPPDATA%" 2>/dev/null </dev/null | tr -d '\r' | grep -m1 ':\\' || true)"
  [ -n "$local_appdata" ] && local_appdata="$(unix "$local_appdata")"
fi
iscc="$local_appdata/Programs/Inno Setup 6/ISCC.exe"

[ -f "$iscc" ] || { echo "Inno Setup not found at $iscc" >&2; exit 1; }
[ -f "$script" ] || { echo "Installer script not found: $script" >&2; exit 1; }

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$props" | head -1)"
[ -n "$version" ] || { echo "No <Version> in Directory.Build.props" >&2; exit 1; }
! grep -q '<Version>' "$csproj" || { echo "The version lives only in Directory.Build.props, not in the app csproj" >&2; exit 1; }

# The script must keep its promises (the installer itself can only be tried on a device).
grep -q 'PrivilegesRequired=lowest' "$script" || { echo "The installer must be per user (PrivilegesRequired=lowest)" >&2; exit 1; }
if grep -qiE 'HKLM|HKEY_LOCAL_MACHINE|\{commonpf|\{commondesktop|\{commonprograms' "$script"; then
  echo "The installer must not write machine-wide" >&2; exit 1
fi

for needed in 'Name: "desktopicon"' 'Name: "pdfassoc"' 'InfoBeforeFile=' 'Software\\Classes\\.pdf\\OpenWithProgids' \
              'Software\\RegisteredApplications' 'ms-settings:defaultapps' 'uninsdeletekey' \
              'function InstalledVersion' 'function InitializeSetup' 'CloseApplications=yes' \
              'procedure CurUninstallStepChanged' 'UsePreviousTasks=yes'; do
  grep -qF -- "$(printf '%b' "$needed")" "$script" || { echo "The installer script lacks: $needed" >&2; exit 1; }
done
[ -f "$root/installer/welcome.it.txt" ] || { echo "installer/welcome.it.txt is missing" >&2; exit 1; }

publish="$root/artifacts/publish/$arch"
dotnet publish "$root/src/DreamBooks.App" -c Release -r "win-$arch" --self-contained -o "$publish"

# Windows shows the exe's file description in "Apri con" and Default apps.
description="$(powershell -NoProfile -Command "(Get-Item '$(win "$publish")\\DreamBooks.App.exe').VersionInfo.FileDescription" | tr -d '\r')"
[ "$description" = "Dream Books" ] || { echo "The exe's file description is '$description', expected 'Dream Books'" >&2; exit 1; }
MSYS2_ARG_CONV_EXCL="*" "$iscc" "/DAppVersion=$version" "/DArch=$arch" "/DSourceDir=$(win "$publish")" "$(win "$script")"

setup="$root/artifacts/installers/DreamBooks-$version-$arch-setup.exe"
[ -f "$setup" ] || { echo "Expected output missing: $setup" >&2; exit 1; }
product="$(powershell -NoProfile -Command "(Get-Item '$(win "$setup")').VersionInfo.ProductVersion" | tr -d "[:space:]")"
case "$product" in "$version"*) ;; *) echo "Installer version '$product' does not match $version" >&2; exit 1 ;; esac

echo "OK: $setup ($(du -m "$setup" | cut -f1) MB, version $product)"
