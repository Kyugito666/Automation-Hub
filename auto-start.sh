#!/bin/bash
#
# auto-start.sh (Lightweight - Nexus Style)
# Dijalankan MANUAL oleh TUI via SSH SETELAH SSH ready.
# Fokus hanya pada runtime logic: git pull, proxysync, deploy_bots.
#

WORKDIR="/workspaces/automation-hub"
LOG_FILE="$WORKDIR/startup.log"
HEALTH_CHECK_DONE="/tmp/auto_start_done"
HEALTH_CHECK_FAIL_PROXY="/tmp/auto_start_failed_proxysync"
HEALTH_CHECK_FAIL_DEPLOY="/tmp/auto_start_failed_deploy"

# Redirect output ke log, tapi tampilkan juga ke stderr TUI saat dipanggil
exec > >(tee -a "$LOG_FILE") 2>&1

echo "========================================="
echo "  AUTO START SCRIPT (Lightweight)"
echo "  $(date)"
echo "========================================="

cd "$WORKDIR" || { echo "FATAL: Cannot cd to $WORKDIR"; exit 1; }

# Hapus flag health check/error lama
rm -f "$HEALTH_CHECK_DONE" "$HEALTH_CHECK_FAIL_PROXY" "$HEALTH_CHECK_FAIL_DEPLOY"
echo "Cleaned up previous status flags."

echo "[1/3] Self-update (git pull)..."
# Tambahkan retry sederhana untuk git pull
git pull || { echo "   ⚠️  WARNING: git pull failed, continuing with existing code..."; }


echo "[2/3] Menjalankan ProxySync (IP Auth, Download, Test)..."
# Pastikan python3 ada di PATH
if ! command -v python3 &> /dev/null; then
    echo "   ❌ ERROR: python3 command not found!"
    touch "$HEALTH_CHECK_FAIL_DEPLOY" # Anggap error deploy jika python tidak ada
    exit 1
fi
# Jalankan proxysync
python3 "$WORKDIR/proxysync/main.py" --full-auto
if [ $? -ne 0 ]; then
    echo "   ❌ ERROR: ProxySync failed! Check $LOG_FILE for details."
    touch "$HEALTH_CHECK_FAIL_PROXY" # Buat flag error proxysync
    exit 1
fi
echo "   ✓ ProxySync selesai. success_proxy.txt updated."

echo "[3/3] Menjalankan Bot Deployer (Smart Install)..."
# Deployer akan cek venv/node_modules dan skip install jika ada
python3 "$WORKDIR/deploy_bots.py"
if [ $? -ne 0 ]; then
    echo "   ❌ ERROR: Bot deployment failed! Check $LOG_FILE for details."
    touch "$HEALTH_CHECK_FAIL_DEPLOY" # Buat flag error deploy
    exit 1
fi
echo "   ✓ Bot deployment selesai."


echo "========================================="
echo "  AUTO START SCRIPT SELESAI (Sukses)"
echo "  Gunakan Menu 4 (Attach) di TUI."
echo "========================================="

# Buat flag SUKSES untuk health check TUI
touch "$HEALTH_CHECK_DONE"
echo "Health check flag created: $HEALTH_CHECK_DONE"

exit 0 # Pastikan exit code 0 jika sukses

