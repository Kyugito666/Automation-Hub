#!/bin/bash
#
# auto-start.sh (Lightweight - Nexus Style v2 - FAST MODE)
# Dijalankan MANUAL oleh TUI via SSH SETELAH SSH ready.
# Fokus: git pull, proxysync, deploy_bots (SKIP jika sudah jalan).
#

WORKDIR="/workspaces/automation-hub"
LOG_FILE="$WORKDIR/startup.log"
HEALTH_CHECK_DONE="/tmp/auto_start_done"
HEALTH_CHECK_FAIL_PROXY="/tmp/auto_start_failed_proxysync"
HEALTH_CHECK_FAIL_DEPLOY="/tmp/auto_start_failed_deploy"
FIRST_RUN_FLAG="/tmp/auto_start_first_run"

exec > >(tee -a "$LOG_FILE") 2>&1

echo "========================================="
echo "  AUTO START SCRIPT (Lightweight v2)"
echo "  $(date)"
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
git pull || { echo "   ⚠️  WARNING: git pull failed, continuing with existing code..."; }

echo "[2/3] Menjalankan ProxySync (IP Auth, Download, Test)..."
if ! command -v python3 &> /dev/null; then
    echo "   ❌ ERROR: python3 command not found!"
    touch "$HEALTH_CHECK_FAIL_DEPLOY"
    exit 1
fi

if [ "$IS_FIRST_RUN" = false ] && [ -f "$WORKDIR/proxysync/success_proxy.txt" ] && [ -s "$WORKDIR/proxysync/success_proxy.txt" ]; then
    echo "   ⏭️  SKIP: ProxySync already ran (success_proxy.txt exists)."
else
    python3 "$WORKDIR/proxysync/main.py" --full-auto
    if [ $? -ne 0 ]; then
        echo "   ❌ ERROR: ProxySync failed! Check $LOG_FILE for details."
        touch "$HEALTH_CHECK_FAIL_PROXY"
        exit 1
    fi
    echo "   ✓ ProxySync selesai. success_proxy.txt updated."
fi

echo "[3/3] Menjalankan Bot Deployer (Smart Install)..."

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

python3 "$WORKDIR/deploy_bots.py"
if [ $? -ne 0 ]; then
    echo "   ❌ ERROR: Bot deployment failed! Check $LOG_FILE for details."
    touch "$HEALTH_CHECK_FAIL_DEPLOY"
    exit 1
fi
echo "   ✓ Bot deployment selesai."

echo "========================================="
echo "  AUTO START SCRIPT SELESAI (Sukses)"
echo "  Gunakan Menu 4 (Attach) di TUI."
echo "========================================="

touch "$HEALTH_CHECK_DONE"
echo "Health check flag created: $HEALTH_CHECK_DONE"

exit 0
