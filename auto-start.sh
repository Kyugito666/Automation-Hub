#!/bin/bash
#
# auto-start.sh (Smart Mode v6 - No Secret Extraction)
# Secrets sudah di-upload via SSH, langsung available
#

# === PERBAIKAN: Tentukan WORKDIR secara dinamis ===
# Ini akan mengambil path absolut dari folder tempat skrip ini berada
# misal: /workspaces/Automation-Hub
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &> /dev/null && pwd)
WORKDIR="$SCRIPT_DIR"
# === AKHIR PERBAIKAN ===

LOG_FILE="$WORKDIR/startup.log"
HEALTH_CHECK_DONE="/tmp/auto_start_done"
HEALTH_CHECK_FAIL_PROXY="/tmp/auto_start_failed_proxysync"
HEALTH_CHECK_FAIL_DEPLOY="/tmp/auto_start_failed_deploy"
FIRST_RUN_FLAG="/tmp/auto_start_first_run"

exec > >(tee -a "$LOG_FILE") 2>&1

echo "========================================="
echo "  AUTO START SCRIPT (Smart v6)"
echo "  $(date)"
echo "  WORKDIR: $WORKDIR" # <-- Tambahan log biar jelas
echo "========================================="

cd "$WORKDIR" || { echo "FATAL: Cannot cd to $WORKDIR"; exit 1; }

rm -f "$HEALTH_CHECK_DONE" "$HEALTH_CHECK_FAIL_PROXY" "$HEALTH_CHECK_FAIL_DEPLOY"
echo "Cleaned up previous status flags."

if [ ! -f "$FIRST_RUN_FLAG" ]; then
    echo "[FIRST RUN] Full setup will be performed."
    IS_FIRST_RUN=true
    touch "$FIRST_RUN_FLAG"
else
    echo "[SUBSEQUENT RUN] Fast mode - skip if already running."
    IS_FIRST_RUN=false
fi

echo "[1/3] Self-update (git pull)..."
# === PERBAIKAN: Cek dulu apa ini repo git ===
if [ -d ".git" ]; then
    git pull || { echo "   ⚠️  WARNING: git pull failed, continuing with existing code..."; }
else
    echo "   ⚠️  WARNING: .git directory not found. Skipping git pull."
fi
# === AKHIR PERBAIKAN ===

# === PERBAIKAN: Logic ProxySync Cerdas (Sesuai Request) ===
echo "[2/3] Running ProxySync..."
if ! command -v python3 &> /dev/null; then
    echo "   ❌ ERROR: python3 command not found!"
    touch "$HEALTH_CHECK_FAIL_DEPLOY"
    exit 1
fi

if [ "$IS_FIRST_RUN" = true ]; then
    echo "   [First Run] Running FULL ProxySync (IP Auth, Download, Test)..."
    python3 "$WORKDIR/proxysync/main.py" --full-auto
    if [ $? -ne 0 ]; then
        echo "   ❌ ERROR: ProxySync (Full Auto) failed! Check $LOG_FILE for details."
        touch "$HEALTH_CHECK_FAIL_PROXY"
        exit 1
    fi
    echo "   ✓ ProxySync (Full Auto) completed. success_proxy.txt updated."
else
    # --- BLOK INI YANG DIUBAH ---
    echo "   [Restart] Running IP Authorization ONLY..."
    python3 "$WORKDIR/proxysync/main.py" --ip-auth-only
    if [ $? -ne 0 ]; then
        echo "   ❌ ERROR: ProxySync (IP Auth Only) failed! Check $LOG_FILE for details."
        # Ini fatal, karena bot ga akan bisa jalan tanpa IP auth
        touch "$HEALTH_CHECK_FAIL_PROXY"
        exit 1
    fi
    echo "   ✓ ProxySync (IP Auth Only) completed."
    
    echo "   [Restart] Running Test & Save (re-validate existing proxies)..."
    python3 "$WORKDIR/proxysync/main.py" --test-and-save-only
    if [ $? -ne 0 ]; then
        echo "   ⚠️  WARNING: ProxySync (Test & Save) failed. May use stale proxies."
        # Ini BUKAN error fatal, biarkan deployer tetep jalan pake proxy lama
    else
        echo "   ✓ ProxySync (Test & Save) completed. success_proxy.txt updated."
    fi
    # --- AKHIR BLOK UBAHAN ---
fi
# === AKHIR PERBAIKAN ===

echo "[3/3] Running Bot Deployer (Smart Install)..."

if tmux has-session -t automation_hub_bots 2>/dev/null; then
    if [ "$IS_FIRST_RUN" = false ]; then
        echo "   ⏭️  SKIP: Bots already running in tmux (session 'automation_hub_bots' exists)."
        echo "   💡 Use 'tmux a -t automation_hub_bots' to attach or run deploy_bots.py manually to restart."
        touch "$HEALTH_CHECK_DONE"
        echo "Health check flag created: $HEALTH_CHECK_DONE (bots reused)"
        exit 0
    else
        echo "   🔄 First run detected but tmux exists. Killing old session..."
        tmux kill-session -t automation_hub_bots 2>/dev/null || true
    fi
fi

# === PERBAIKAN: Gunakan $WORKDIR variabel ===
python3 "$WORKDIR/deploy_bots.py"
if [ $? -ne 0 ]; then
    echo "   ❌ ERROR: Bot deployment failed! Check $LOG_FILE for details."
    touch "$HEALTH_CHECK_FAIL_DEPLOY"
    exit 1
fi
echo "   ✓ Bot deployment completed."

echo "========================================="
echo "  AUTO START SCRIPT COMPLETED (Success)"
echo "  Use Menu 4 (Attach) in TUI to monitor."
echo "========================================="

touch "$HEALTH_CHECK_DONE"
echo "Health check flag created: $HEALTH_CHECK_DONE"

exit 0
