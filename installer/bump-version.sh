#!/usr/bin/env bash
# Raises the app's version, which lives only in Directory.Build.props (the exe, the settings and the installers read it).
# Usage: installer/bump-version.sh major|minor|patch|X.Y.Z [props-file]
set -euo pipefail

target="${1:-}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
props="${2:-$root/Directory.Build.props}"
[ -n "$target" ] || { echo "Usage: bump-version.sh major|minor|patch|X.Y.Z" >&2; exit 2; }
[ -f "$props" ] || { echo "Not found: $props" >&2; exit 1; }

current="$(sed -n 's:.*<Version>\([0-9][0-9.]*\)</Version>.*:\1:p' "$props" | head -1)"
[[ "$current" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)$ ]] || { echo "No X.Y.Z <Version> in $props" >&2; exit 1; }
major="${BASH_REMATCH[1]}"; minor="${BASH_REMATCH[2]}"; patch="${BASH_REMATCH[3]}"

case "$target" in
  major) next="$((major + 1)).0.0" ;;
  minor) next="$major.$((minor + 1)).0" ;;
  patch) next="$major.$minor.$((patch + 1))" ;;
  *)
    [[ "$target" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Not a version: $target" >&2; exit 2; }
    next="$target" ;;
esac

sed -i "s:<Version>$current</Version>:<Version>$next</Version>:" "$props"
echo "$current -> $next"
