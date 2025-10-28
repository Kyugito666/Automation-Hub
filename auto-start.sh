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

echo "[1/5] Melakukan self-update (git pull)..."
git pull

# === PERUBAHAN URUTAN ===
echo "[2/5] Menginstal/Update dependensi ProxySync..."
# Kita butuh 'rich' dan 'requests' dari proxysync
if [ -f "$WORKDIR/proxysync/requirements.txt" ]; then
    pip install --no-cache-dir --upgrade -r "$WORKDIR/proxysync/requirements.txt"
else
    echo "WARNING: proxysync/requirements.txt tidak ditemukan. Lanjut..."
fi

echo "[3/5] Menjalankan ProxySync (IP Auth, Download, Test)..."
# Menjalankan proxysync dalam mode non-interaktif
python3 "$WORKDIR/proxysync/main.py" --full-auto
echo "   ProxySync selesai. File 'success_proxy.txt' telah diperbarui."
# === AKHIR PERUBAHAN ===

echo "[4/5] Memeriksa file konfigurasi..."
# TUI lokal bertanggung jawab meng-upload file-file ini saat 'create'
if [ ! -f "$WORKDIR/config/bots_config.json" ]; then
    echo "FATAL: config/bots_config.json tidak ditemukan!"
    echo "Pastikan TUI lokal sudah 'gh codespace cp' file config."
    exit 1
fi

echo "[5/5] Menjalankan skrip deployer utama (deploy_bots.py)..."
# deploy_bots.py sekarang akan 'smart install' dan baca 'success_proxy.txt'
python3 "$WORKDIR/deploy_bots.py"

echo "=================================================="
echo "  AUTO-START SCRIPT SELESAI"
echo "  Gunakan 'Menu 4 (Attach)' di TUI untuk monitor."
echo "=================================================="
