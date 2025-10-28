#!/bin/bash
#
# first-setup.sh
# Dijalankan OTOMATIS oleh Codespace via devcontainer.json (postAttachCommand)
# Hanya install dependensi dasar yang STABIL dan JARANG BERUBAH.
# Dependencies bot spesifik (40 bot) tetap dihandle deploy_bots.py (tapi di-skip jika venv/node_modules ada).
#

echo "========================================="
echo "  FIRST SETUP / ATTACH SCRIPT (Codespace)"
echo "========================================="

# Ganti ke direktori workspace
cd "${CODESPACE_VSCODE_FOLDER:-/workspaces/automation-hub}" || exit 1

echo "[1/1] Menginstal dependensi dasar (proxysync)..."
if [ -f "proxysync/requirements.txt" ]; then
    # Gunakan --user agar tidak perlu sudo dan lebih aman jika base image berubah
    # Pastikan pip ada dan terupdate
    python3 -m ensurepip --upgrade
    python3 -m pip install --user --no-cache-dir --upgrade pip
    python3 -m pip install --user --no-cache-dir --upgrade -r "proxysync/requirements.txt"
    # Pastikan direktori bin user ada di PATH (biasanya sudah otomatis di Codespace)
    if [[ ":$PATH:" != *":$HOME/.local/bin:"* ]]; then
        echo 'export PATH="$HOME/.local/bin:$PATH"' >> ~/.bashrc
        export PATH="$HOME/.local/bin:$PATH" # Apply for current session
        echo "   (Added ~/.local/bin to PATH)"
    fi
    echo "   ✓ Dependensi proxysync terinstal/update."
else
    echo "   ⚠️  proxysync/requirements.txt tidak ditemukan. Lewati."
fi


echo "========================================="
echo "  FIRST SETUP SELESAI"
echo "========================================="

# Jangan lupa chmod +x first-setup.sh setelah membuat file ini

