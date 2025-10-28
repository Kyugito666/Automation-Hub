#!/bin/bash
# Shim script to ensure first-setup runs from the correct directory
# Also installs essential tools reliably.

echo "Executing postAttachCommand..."
# Go to workspace folder first, then execute first-setup.sh from there
cd "${CODESPACE_VSCODE_FOLDER:-/workspaces/automation-hub}" && ./first-setup.sh

# Install tmux & jq here instead of postCreateCommand (lebih reliable)
echo "Ensuring essential tools (tmux, jq) are installed..."
# Check if tools exist first to avoid unnecessary apt-get update
if ! command -v tmux &> /dev/null || ! command -v jq &> /dev/null; then
    sudo apt-get update && sudo apt-get install -y tmux jq
else
    echo "   Tools already installed."
fi

echo "postAttachCommand finished."

# Jangan lupa chmod +x .devcontainer/post-attach.sh

