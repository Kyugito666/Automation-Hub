#!/usr/bin/env python3
"""
deploy_bots.py (v7 - Wrapper/Orchestrator Mode)
Menggantikan sistem 'ExpectManager' lama.
Sistem ini memanggil 'wrapper.py' menggunakan 'tmux send-keys'.
"""

import json
import subprocess
import sys
import re
from pathlib import Path
import time
import os

SCRIPT_DIR = Path(__file__).parent.resolve()
WORKDIR = SCRIPT_DIR
CONFIG_FILE = WORKDIR / "config" / "bots_config.json"
WRAPPER_SCRIPT_PATH = WORKDIR / "wrapper.py"

TMUX_SESSION = "automation_hub_bots"

def run_cmd(command, capture=False):
    """Helper untuk menjalankan command (terutama tmux)."""
    try:
        process = subprocess.run(
            command, shell=True, check=True, cwd=WORKDIR,
            text=True, capture_output=capture, encoding='utf-8', errors='ignore'
        )
        return (process.stdout.strip(), process.stderr.strip()) if capture else True
    except subprocess.CalledProcessError as e:
        print(f"  ❌ ERROR running: {command}")
        print(f"     Output: {e.stderr or e.stdout or str(e)}")
        return False
    except Exception as e:
        print(f"  ❌ UNEXPECTED ERROR running: {command}\n     {e}")
        return False

def main():
    os.chdir(WORKDIR)
    print("=" * 60)
    print("  🚀 DEPLOYER (v7 - Wrapper Mode) 🚀")
    print("=" * 60)

    print(f"\n[1/4] Memvalidasi file...")
    if not CONFIG_FILE.exists():
        print(f"  🔴 FATAL: '{CONFIG_FILE.name}' not found!"); sys.exit(1)
    if not WRAPPER_SCRIPT_PATH.exists():
        print(f"  🔴 FATAL: Wrapper script '{WRAPPER_SCRIPT_PATH.name}' not found!"); sys.exit(1)
    if not run_cmd("command -v jq", capture=True)[0]:
         print("  🔴 FATAL: 'jq' not found!"); sys.exit(1)
    if not run_cmd("command -v tmux", capture=True)[0]:
         print("  🔴 FATAL: 'tmux' not found!"); sys.exit(1)

    print("\n[2/4] 🧹 Membersihkan sesi tmux lama...")
    run_cmd(f"tmux kill-session -t {TMUX_SESSION}")

    print("\n[3/4] 📋 Membaca konfigurasi bot...")
    try:
        bots_json, _ = run_cmd(f"jq -c '.bots_and_tools[] | select(.enabled == true)' {CONFIG_FILE}", capture=True)
        if not bots_json:
            print("  🟡 Tidak ada bot yang 'enabled' di config. Keluar.")
            sys.exit(0)
        bots = [json.loads(line) for line in bots_json.splitlines()]
        print(f"       ✅ Ditemukan {len(bots)} bot yang 'enabled'.")
    except Exception as e:
        print(f"  🔴 FATAL: Gagal membaca/parsing '{CONFIG_FILE.name}': {e}"); sys.exit(1)

    print("\n[4/4] 🚀 Meluncurkan bot di tmux...")
    
    is_first = True
    for bot in bots:
        name = bot.get("name")
        config_path_str = bot.get("path")
        repo_url = bot.get("repo_url", "")
        
        if repo_url.startswith("http") or repo_url.startswith("git@"):
             folder_name = repo_url.split('/')[-1].replace(".git", "")
        else:
             folder_name = Path(config_path_str).name

        full_path = (WORKDIR / config_path_str).resolve()
        
        if not name or not full_path:
            print(f"  🟡 SKIP: Entri invalid (nama/path kosong): {bot}")
            continue
            
        if not full_path.is_dir():
             print(f"  🟡 SKIP: Folder bot tidak ditemukan di '{full_path}'.")
             print(f"     (Dicek dari config 'path': {config_path_str})")
             continue
        
        safe_name = re.sub(r'[^a-zA-Z0-9_.-]', '', name)[:50]
        safe_cmd_bash = f"cd \"{full_path}\" && bash"
        
        try:
            if is_first:
                print(f"Menciptakan sesi: {safe_name} (Path: {full_path})")
                if not run_cmd(f"tmux new-session -d -s {TMUX_SESSION} -n \"{safe_name}\" '{safe_cmd_bash}'"):
                     print("  🔴 FATAL: Gagal membuat bot pertama. Keluar."); sys.exit(1)
                is_first = False
            else:
                print(f"Menambahkan window: {safe_name} (Path: {full_path})")
                run_cmd(f"tmux new-window -t {TMUX_SESSION} -n \"{safe_name}\" '{safe_cmd_bash}'")
            
            time.sleep(0.1)
            
            wrapper_abs_path = WRAPPER_SCRIPT_PATH.resolve()
            cmd_to_run = f"python3 {wrapper_abs_path} \"{name}\""
            
            print(f"   -> Menjalankan wrapper: {cmd_to_run}")
            run_cmd(f"tmux send-keys -t \"{TMUX_SESSION}:{safe_name}\" '{cmd_to_run}' C-m")
            
            time.sleep(0.1)
        
        except Exception as e:
             print(f"  🔴 GAGAL meluncurkan {name}: {e}")

    print("\n" + "=" * 60)
    print("  📊 DEPLOYMENT SUMMARY (v7) 📊")
    print("  Semua bot yang 'enabled' telah diperintahkan untuk start.")
    print(f"  Gunakan 'tmux a -t {TMUX_SESSION}' atau TUI Menu 4 untuk monitor.")
    print("=" * 60)
    sys.exit(0)

if __name__ == "__main__":
    main()
