#!/bin/bash
# Shim script to ensure first-setup runs from the correct directory

echo "Executing postAttachCommand..."
# Go to workspace folder first, then execute first-setup.sh from there
cd "${CODESPACE_VSCODE_FOLDER:-/workspaces/automation-hub}" && ./first-setup.sh

# Install tmux & jq here instead of postCreateCommand (lebih reliable)
echo "Ensuring essential tools (tmux, jq) are installed..."
if ! command -v tmux &> /dev/null || ! command -v jq &> /dev/null; then
    sudo apt-get update && sudo apt-get install -y tmux jq
fi

echo "postAttachCommand finished."
