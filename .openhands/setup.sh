#!/usr/bin/env bash
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
cd "$ROOT"

ensure_bashrc_line() {
  local line="$1"
  touch "$HOME/.bashrc"
  grep -Fqx "$line" "$HOME/.bashrc" || printf '%s\n' "$line" >> "$HOME/.bashrc"
}

if ! command -v dotnet >/dev/null 2>&1 || [[ "$(dotnet --version 2>/dev/null || true)" != 10.* ]]; then
  install_dir="$HOME/.dotnet"
  mkdir -p "$install_dir"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$install_dir"
  export DOTNET_ROOT="$install_dir"
  export PATH="$install_dir:$PATH"
  ensure_bashrc_line 'export DOTNET_ROOT="$HOME/.dotnet"'
  ensure_bashrc_line 'export PATH="$HOME/.dotnet:$PATH"'
fi

dotnet --info
dotnet restore OpenDeviceStudio.slnx -p:EnableWindowsTargeting=true
