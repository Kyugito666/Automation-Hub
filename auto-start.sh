#!/bin/bash
#
# auto-start.sh (Lightweight - Nexus Style)
# Dijalankan MANUAL oleh TUI via SSH SETELAH SSH ready.
# Fokus hanya pada runtime logic: git pull, proxysync, deploy_bots.
#

WORKDIR="/workspaces/automation-hub"
LOG_FILE="$WORKDIR/startup.log"

# Redirect output ke log
exec > >(tee -a "$LOG_FILE") 2>&1

echo "========================================="
echo "  AUTO START SCRIPT (Lightweight)"
echo "  $(date)"
echo "========================================="

cd "$WORKDIR" || exit 1

# Hapus flag health check/error lama
rm -f /tmp/auto_start_done /tmp/auto_start_failed_*
echo "Cleaned up previous status flags."

echo "[1/3] Self-update (git pull)..."
git pull

echo "[2/3] Menjalankan ProxySync (IP Auth, Download, Test)..."
# Asumsi python3 ada di PATH dan proxysync deps sudah diinstall oleh first-setup/postAttach
python3 "$WORKDIR/proxysync/main.py" --full-auto
if [ $? -ne 0 ]; then
    echo "   ❌ ERROR: ProxySync failed!"
    touch /tmp/auto_start_failed_proxysync # Buat flag error
    exit 1
fi
echo "   ✓ ProxySync selesai. success_proxy.txt updated."

echo "[3/3] Menjalankan Bot Deployer (Smart Install)..."
# Deployer akan cek venv/node_modules dan skip install jika ada
python3 "$WORKDIR/deploy_bots.py"
if [ $? -ne 0 ]; then
    echo "   ❌ ERROR: Bot deployment failed!"
    touch /tmp/auto_start_failed_deploy # Buat flag error
    exit 1
fi
echo "   ✓ Bot deployment selesai."


echo "========================================="
echo "  AUTO START SCRIPT SELESAI (Sukses)"
echo "  Gunakan Menu 4 (Attach) di TUI."
echo "========================================="

# Buat flag SUKSES untuk health check TUI
touch /tmp/auto_start_done
echo "Health check flag created: /tmp/auto_start_done"

exit 0 # Pastikan exit code 0 jika sukses

