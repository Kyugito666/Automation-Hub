#!/bin/bash
#
# first-setup.sh
# Dijalankan OTOMATIS oleh Codespace via devcontainer.json (postAttachCommand)
# Install dependensi dasar yang STABIL dan JARANG BERUBAH.
# Dependencies bot spesifik (40 bot) tetap dihandle deploy_bots.py (di-skip jika venv/node_modules ada).
#

echo "========================================="
echo "  FIRST SETUP / ATTACH SCRIPT (Codespace)"
echo "========================================="

# Ganti ke direktori workspace
cd "${CODESPACE_VSCODE_FOLDER:-/workspaces/automation-hub}" || exit 1

echo "[1/2] Installing essential tools (jq for JSON parsing)..."
# jq dibutuhkan untuk extract secrets di auto-start.sh
if ! command -v jq &> /dev/null; then
    echo "   Installing jq..."
    sudo apt-get update -qq && sudo apt-get install -y jq
    echo "   ✓ jq installed"
else
    echo "   ✓ jq already installed"
fi

echo "[2/2] Installing proxysync dependencies..."
if [ -f "proxysync/requirements.txt" ]; then
    # Gunakan --user agar tidak perlu sudo dan lebih aman
    python3 -m ensurepip --upgrade 2>/dev/null || true
    python3 -m pip install --user --no-cache-dir --upgrade pip
    python3 -m pip install --user --no-cache-dir --upgrade -r "proxysync/requirements.txt"
    
    # Pastikan direktori bin user ada di PATH
    if [[ ":$PATH:" != *":$HOME/.local/bin:"* ]]; then
        echo 'export PATH="$HOME/.local/bin:$PATH"' >> ~/.bashrc
        export PATH="$HOME/.local/bin:$PATH"
        echo "   (Added ~/.local/bin to PATH)"
    fi
    echo "   ✓ Proxysync dependencies installed"
else
    echo "   ⚠️  proxysync/requirements.txt not found. Skipping."
fi

echo "========================================="
echo "  FIRST SETUP COMPLETED"
echo "========================================="
