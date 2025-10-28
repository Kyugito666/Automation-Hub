#!/bin/bash
#
# auto-start.sh (Smart Mode v5 - Handle Split Secrets)
#

WORKDIR="/workspaces/automation-hub"
LOG_FILE="$WORKDIR/startup.log"
HEALTH_CHECK_DONE="/tmp/auto_start_done"
HEALTH_CHECK_FAIL_PROXY="/tmp/auto_start_failed_proxysync"
HEALTH_CHECK_FAIL_DEPLOY="/tmp/auto_start_failed_deploy"
FIRST_RUN_FLAG="/tmp/auto_start_first_run"

exec > >(tee -a "$LOG_FILE") 2>&1

echo "========================================="
echo "  AUTO START SCRIPT (Smart v5)"
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

echo "[1/4] Self-update (git pull)..."
git pull || { echo "   ⚠️  WARNING: git pull failed, continuing with existing code..."; }

echo "[2/4] Extracting secrets from GitHub Codespaces (Smart Mode)..."

extract_secret_smart() {
    local bot_name="$1"
    local target_dir="$2"
    
    local sanitized_bot=$(echo "$bot_name" | tr -cs '[:alnum:]' '_' | tr '[:lower:]' '[:upper:]')
    
    local extracted_count=0
    
    for var_name in $(compgen -e | grep "^${sanitized_bot}_"); do
        # Skip PART variables for now (will be merged later)
        if [[ "$var_name" =~ _PART[0-9]+$ ]]; then
            continue
        fi
        
        local filename_part="${var_name#${sanitized_bot}_}"
        local filename=$(echo "$filename_part" | tr '[:upper:]' '[:lower:]' | tr '_' '.')
        
        local var_value="${!var_name}"
        
        if [ -n "$var_value" ]; then
            mkdir -p "$target_dir"
            echo "$var_value" > "$target_dir/$filename"
            echo "   ✓ Extracted: $filename for $bot_name"
            ((extracted_count++))
        fi
    done
    
    # Merge PART files if exist
    for var_name in $(compgen -e | grep "^${sanitized_bot}_.*_PART1$"); do
        local base_name="${var_name%_PART1}"
        local filename_part="${base_name#${sanitized_bot}_}"
        local filename=$(echo "$filename_part" | tr '[:upper:]' '[:lower:]' | tr '_' '.')
        
        local merged_content=""
        local part_num=1
        
        while true; do
            local part_var="${base_name}_PART${part_num}"
            local part_value="${!part_var}"
            
            if [ -z "$part_value" ]; then
                break
            fi
            
            merged_content="${merged_content}${part_value}"
            ((part_num++))
        done
        
        if [ -n "$merged_content" ]; then
            mkdir -p "$target_dir"
            echo "$merged_content" > "$target_dir/$filename"
            echo "   ✓ Merged: $filename for $bot_name (${part_num} parts)"
            ((extracted_count++))
        fi
    done
    
    if [ $extracted_count -eq 0 ]; then
        echo "   ○ No secrets for $bot_name"
    fi
}

if [ -f "$WORKDIR/config/bots_config.json" ]; then
    echo "   Reading bots_config.json..."
    
    if command -v jq &> /dev/null; then
        while IFS= read -r bot_entry; do
            bot_name=$(echo "$bot_entry" | jq -r '.name')
            bot_path=$(echo "$bot_entry" | jq -r '.path')
            bot_enabled=$(echo "$bot_entry" | jq -r '.enabled')
            
            if [ "$bot_enabled" != "true" ]; then
                continue
            fi
            
            target_dir="$WORKDIR/$bot_path"
            extract_secret_smart "$bot_name" "$target_dir"
            
        done < <(jq -c '.bots_and_tools[]' "$WORKDIR/config/bots_config.json")
        
        echo "   ✓ Smart secret extraction completed"
    else
        echo "   ⚠️  jq not found. Secrets not extracted (install via first-setup.sh)"
    fi
else
    echo "   ⚠️  bots_config.json not found. Skipping secret extraction."
fi

echo "[3/4] Running ProxySync (IP Auth, Download, Test)..."
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
    echo "   ✓ ProxySync completed. success_proxy.txt updated."
fi

echo "[4/4] Running Bot Deployer (Smart Install)..."

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
echo "   ✓ Bot deployment completed."

echo "========================================="
echo "  AUTO START SCRIPT COMPLETED (Success)"
echo "  Use Menu 4 (Attach) in TUI to monitor."
echo "========================================="

touch "$HEALTH_CHECK_DONE"
echo "Health check flag created: $HEALTH_CHECK_DONE"

exit 0
