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
    pip install --user --no-cache-dir --upgrade -r "proxysync/requirements.txt"
    # Pastikan direktori bin user ada di PATH (biasanya sudah otomatis di Codespace)
    export PATH="$HOME/.local/bin:$PATH"
    echo "   ✓ Dependensi proxysync terinstal/update."
else
    echo "   ⚠️  proxysync/requirements.txt tidak ditemukan. Lewati."
fi

# Tambahan: Pastikan tmux terinstall (meskipun biasanya sudah ada di base image)
if ! command -v tmux &> /dev/null; then
    echo "   Menginstal tmux..."
    sudo apt-get update && sudo apt-get install -y tmux
fi

echo "========================================="
echo "  FIRST SETUP SELESAI"
echo "========================================="

# Jangan lupa chmod +x first-setup.sh setelah membuat file ini
