#!/bin/bash
#
# auto-start.sh
# Entrypoint remote yang dipanggil oleh TUI C# lokal via 'gh codespace ssh'.
#

# Tentukan path kerja (standar Codespace)
WORKDIR="/workspaces/automation-hub"
LOG_FILE="$WORKDIR/startup.log"

# Mengalihkan semua output (stdout & stderr) ke file log
exec > >(tee -a "$LOG_FILE") 2>&1

echo "=================================================="
echo "  AUTOMATION-HUB CODESPACE RUNNER - AUTO START"
echo "  $(date)"
echo "=================================================="

cd "$WORKDIR"

echo "[1/4] Melakukan self-update (git pull)..."
git pull

echo "[2/4] Menginstal dependensi deployer (Python)..."
# Kita butuh 'rich' dan 'requests' dari proxysync untuk deploy_bots.py
if [ -f "$WORKDIR/proxysync/requirements.txt" ]; then
    pip install --no-cache-dir -r "$WORKDIR/proxysync/requirements.txt"
else
    echo "WARNING: proxysync/requirements.txt tidak ditemukan. Lanjut..."
fi

echo "[3/4] Memeriksa file konfigurasi..."
# TUI lokal bertanggung jawab meng-upload file-file ini sebelum memanggil auto-start
if [ ! -f "$WORKDIR/config/bots_config.json" ]; then
    echo "FATAL: config/bots_config.json tidak ditemukan!"
    echo "Pastikan TUI lokal sudah 'gh codespace cp' file config."
    exit 1
fi

if [ ! -f "$WORKDIR/config/proxy.txt" ]; then
    echo "WARNING: config/proxy.txt tidak ditemukan. Bot akan berjalan tanpa proxy."
fi

echo "[4/4] Menjalankan skrip deployer utama (deploy_bots.py)..."
python3 "$WORKDIR/deploy_bots.py"

echo "=================================================="
echo "  AUTO-START SCRIPT SELESAI"
echo "  Cek 'tmux ls' untuk melihat bot yang berjalan."
echo "=================================================="
