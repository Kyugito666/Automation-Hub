#!/usr/bin/env python3
"""
deploy_bots.py (v8 - Hybrid Mode)
- Menggunakan logic wrapper.py dan tmux send-keys (dari v7).
- Menambahkan kembali logic git clone/pull (dari v3) untuk repo
  yang didefinisikan sebagai URL, dan men-skip-nya untuk repo
  yang di-upload (path D:).
"""

import json
import subprocess
import sys
import re
from pathlib import Path
import time
import os
import shutil # <-- TAMBAHKAN IMPORT INI

SCRIPT_DIR = Path(__file__).parent.resolve()
WORKDIR = SCRIPT_DIR
CONFIG_FILE = WORKDIR / "config" / "bots_config.json"
WRAPPER_SCRIPT_PATH = WORKDIR / "wrapper.py"

TMUX_SESSION = "automation_hub_bots"

def run_cmd(command, cwd=None, capture=False):
    """Helper untuk menjalankan command (terutama tmux)."""
    # Set CWD default ke WORKDIR jika tidak dispesifikkan
    cwd = cwd or WORKDIR
    try:
        process = subprocess.run(
            command, shell=True, check=True, cwd=cwd,
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

# --- FUNGSI GIT YANG HILANG (DITAMBAHKAN KEMBALI) ---
def sync_bot_repo(name: str, bot_path: Path, repo_url: str):
    """Melakukan git clone atau git pull (secara aman) untuk repo bot."""
    print(f"\n--- 🔄 Syncing (Git): {name} ---")
    try: bot_path.relative_to(WORKDIR)
    except ValueError:
        print(f"  🔴 SECURITY WARNING: Path '{bot_path}' outside project. Skipping sync.")
        return False

    bot_path.parent.mkdir(parents=True, exist_ok=True)
    git_dir = bot_path / ".git"
    
    if git_dir.is_dir():
        print(f"  [Sync] Repository exists. Performing 'git pull' (safe mode)...")
        # Pull aman yang tidak merusak file untracked (seperti kredensial)
        if not run_cmd("git pull --rebase --autostash", cwd=str(bot_path)):
            print(f"  [Sync] 🟡 Pull failed. Attempting fetch + reset (safe)...")
            # Recovery jika pull rebase gagal
            if run_cmd("git fetch origin", cwd=str(bot_path)) and \
               run_cmd("git reset --hard origin/HEAD", cwd=str(bot_path)):
                 print(f"  [Sync] ✅ Recovered via fetch + HARD reset.")
            else:
                 print(f"  [Sync] 🔴 Recovery failed. Using existing code.")
                 return False # Gagal sync
    else:
        # Jika folder ada tapi bukan .git (mungkin sisa upload gagal?)
        if bot_path.exists() and any(bot_path.iterdir()):
             print(f"  [Sync] 🟡 Path exists but not git repo. Menghapus dan cloning...")
             try:
                  if bot_path.is_dir(): shutil.rmtree(bot_path)
                  else: bot_path.unlink()
             except Exception as e:
                  print(f"  [Sync] 🔴 Gagal remove: {e}. Skipping clone.")
                  return False

        print(f"  [Sync] 📥 Cloning '{repo_url}'...")
        # Perbaikan: Clone KE 'bot_path' (path absolut), BUKAN 'bot_path.name'
        if not run_cmd(f"git clone --depth 1 \"{repo_url}\" \"{bot_path}\"", cwd=str(bot_path.parent)):
             print(f"  [Sync] 🔴 Clone failed for '{name}'.")
             return False
        print(f"  [Sync] ✅ Cloned successfully.")

    if not bot_path.is_dir():
         print(f"  [Sync] 🔴 Bot directory '{bot_path.name}' not found after sync.")
         return False
    return True
# --- AKHIR FUNGSI GIT ---


def main():
    os.chdir(WORKDIR)
    print("=" * 60)
    print("  🚀 DEPLOYER (v8 - Hybrid Mode) 🚀")
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
        # Gunakan jq untuk parsing (lebih robust)
        bots_json, _ = run_cmd(f"jq -c '.bots_and_tools[] | select(.enabled == true)' {CONFIG_FILE}", capture=True)
        if not bots_json:
            print("  🟡 Tidak ada bot yang 'enabled' di config. Keluar.")
            sys.exit(0)
        bots = [json.loads(line) for line in bots_json.splitlines()]
        print(f"       ✅ Ditemukan {len(bots)} bot yang 'enabled'.")
    except Exception as e:
        print(f"  🔴 FATAL: Gagal membaca/parsing '{CONFIG_FILE.name}': {e}"); sys.exit(1)

    print("\n[4/4] 🚀 Mensinkronkan dan Meluncurkan bot di tmux...")
    
    is_first = True
    for bot in bots:
        name = bot.get("name")
        config_path_str = bot.get("path")
        repo_url = bot.get("repo_url", "")
        
        # Path absolut di dalam codespace
        # (Misal: /workspaces/Automation-Hub/bots/privatekey/abc)
        full_path = (WORKDIR / config_path_str).resolve()
        
        if not name or not full_path:
            print(f"  🟡 SKIP: Entri invalid (nama/path kosong): {bot}")
            continue
        
        # --- LOGIC GIT HYBRID (PERBAIKAN) ---
        is_git_repo = repo_url.startswith("http") or repo_url.startswith("git@")
        
        if is_git_repo:
            # Jika ini repo Git, panggil sync_bot_repo
            if not sync_bot_repo(name, full_path, repo_url):
                print(f"  🔴 GAGAL Sync Git untuk {name}. Skipping launch.")
                continue # Lanjut ke bot berikutnya
        else:
            # Ini adalah bot yang di-upload C# (dari D: drive)
            print(f"\n--- Processing (Lokal): {name} ---")
            print(f"  [Sync] ⏭️  Skipping Git Sync (Local Upload / D: Drive).")
        # --- AKHIR LOGIC GIT HYBRID ---

        # Cek folder (sekarang harusnya ada, baik di-clone atau di-upload)
        if not full_path.is_dir():
             print(f"  🟡 SKIP: Folder bot tidak ditemukan di '{full_path}'.")
             print(f"     (Dicek dari config 'path': {config_path_str})")
             continue
        
        # Logic dari v7 (start_bots.sh)
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
    print("  📊 DEPLOYMENT SUMMARY (v8 - Hybrid) 📊")
    print("  Semua bot yang 'enabled' telah diperintahkan untuk start.")
    print("  (Gunakan 'tmux a' di TUI Menu 1 untuk monitor)")
    print("=" * 60)
    sys.exit(0)

if __name__ == "__main__":
    main()
