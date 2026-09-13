#!/usr/bin/env bash
# Installs AutoDev's desktop entry and icon for the current user, so it shows up with its own icon in
# app launchers (including menu editors like Alacarte), taskbars/alt-tab, and file managers - a raw
# Linux executable carries no icon of its own the way a Windows .exe does, so this is the actual,
# portable fix rather than a one-off tweak to a single file manager's per-file metadata on one machine.
# The desktop entry's Icon= is written as this install's absolute icon path rather than a bare theme
# name, so it resolves immediately without depending on any icon-theme cache being fresh. Re-run any
# time the published binary is (re)deployed to a new path - safe to run repeatedly.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec_path="${1:-$HOME/Tools/AutoDev}"

if [[ ! -x "$exec_path" ]]; then
    echo "error: '$exec_path' does not exist or is not executable" >&2
    echo "usage: $0 [path-to-AutoDev-executable]" >&2
    exit 1
fi

icon_dir="$HOME/.local/share/icons/hicolor/1024x1024/apps"
apps_dir="$HOME/.local/share/applications"
mkdir -p "$icon_dir" "$apps_dir"

icon_path="$icon_dir/autodev.png"
cp "$script_dir/../Assets/app.png" "$icon_path"
sed -e "s|@EXEC_PATH@|$exec_path|" -e "s|@ICON_PATH@|$icon_path|" "$script_dir/autodev.desktop" > "$apps_dir/autodev.desktop"

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -f -t "$HOME/.local/share/icons/hicolor" 2>/dev/null || true
fi
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$apps_dir" 2>/dev/null || true
fi

echo "Installed AutoDev desktop entry for $exec_path"
