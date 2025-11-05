#!/bin/bash
# auto-start.sh (v7 - Wrapper/Orchestrator Mode)

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &> /dev/null && pwd)
WORKDIR="$SCRIPT_DIR"
LOG_FILE="$WORKDIR/startup.log"

exec > >(tee -a "$LOG_FILE") 2>&1

echo "========================================="
echo "  AUTO START SCRIPT (Orchestrator v7)"
echo "  $(date)"
echo "  WORKDIR: $WORKDIR"
echo "========================================="

cd "$WORKDIR" || { echo "FATAL: Cannot cd to $WORKDIR"; exit 1; }

INSTALL_TYPE=$1

if [ "$INSTALL_TYPE" == "--existing-install" ]; then
    echo "[1/3] Self-update (git pull) for existing codespace..."
    if [ -d ".git" ]; then
        git pull || echo "   ⚠️  WARNING: git pull failed, continuing..."
    else
        echo "   ⚠️  WARNING: .git directory not found. Skipping git pull."
    fi
    
    echo "[2/3] Running ProxySync (IP Auth Only)..."
    python3 "$WORKDIR/proxysync/main.py" --ip-auth-only
    if [ $? -ne 0 ]; then
        echo "   ❌ ERROR: ProxySync (IP Auth Only) failed!"
        exit 1
    fi
    
    echo "[2/3] Running ProxySync (Test & Save Only)..."
    python3 "$WORKDIR/proxysync/main.py" --test-and-save-only

elif [ "$INSTALL_TYPE" == "--new-install" ]; then
    echo "[1/3] Skipping Self-update (New Codespace)."
    
    echo "[2/3] Running ProxySync (Full Auto Download/Test)..."
    python3 "$WORKDIR/proxysync/main.py" --full-auto
    if [ $? -ne 0 ]; then
        echo "   ❌ ERROR: ProxySync (Full Auto) failed!"
        exit 1
    fi
    
else
    echo "FATAL: No install type specified (e.g., --new-install or --existing-install)."
    exit 1
fi

echo "[3/3] Running Bot Deployer (Wrapper System)..."
python3 "$WORKDIR/deploy_bots.py"
if [ $? -ne 0 ]; then
    echo "   ❌ ERROR: Bot deployment (deploy_bots.py) failed!"
    exit 1
fi

echo "========================================="
echo "  AUTO START SCRIPT COMPLETED (Success)"
echo "  (TUI is now in idle mode)"
echo "========================================="
exit 0
