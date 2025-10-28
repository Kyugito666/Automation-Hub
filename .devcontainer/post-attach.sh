#!/bin/bash
# Shim script to ensure first-setup runs from the correct directory
# Also installs essential tools reliably FIRST.

echo "Executing postAttachCommand..."

# === PERBAIKAN: Install tmux & jq DI AWAL ===
echo "Ensuring essential tools (tmux, jq) are installed..."
# Check if tools exist first to avoid unnecessary apt-get update
if ! command -v tmux &> /dev/null || ! command -v jq &> /dev/null; then
    echo "   Installing tmux and jq..."
    sudo apt-get update -qq && sudo apt-get install -y tmux jq
    echo "   ✓ Tools installed."
else
    echo "   ✓ Tools already installed."
fi
# === AKHIR PERBAIKAN ===

# Go to workspace folder first, then execute first-setup.sh from there
cd "${CODESPACE_VSCODE_FOLDER:-/workspaces/automation-hub}" && ./first-setup.sh || echo "WARNING: first-setup.sh failed!"

echo "postAttachCommand finished."

# Jangan lupa chmod +x .devcontainer/post-attach.sh
