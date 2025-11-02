#!/usr/bin/env python3
"""
deploy_bots.py - Remote Environment Orchestrator
Fitur: Self-healing, Proxy distribution, Git sync, Dependency install, Tmux launch
(Versi 2: Smart Install & Prioritas Success Proxy)
(Versi 3: Perbaikan 'git reset --hard' yang menghapus kredensial)
"""

import os
import json
import subprocess
import sys
import random
import re
from pathlib import Path
import shutil # Import shutil untuk hapus folder saat sync gagal

# === Gunakan path absolut/relatif dari direktori skrip ===
SCRIPT_DIR = Path(__file__).parent.resolve()
WORKDIR = SCRIPT_DIR # Asumsi skrip ini ada di root project
CONFIG_DIR = WORKDIR / "config"
SETUP_TOOLS_DIR = WORKDIR / "setup_tools" # <--- FOLDER BARU
PROXYSYNC_DIR = WORKDIR / "proxysync" # Path ke folder proxysync

CONFIG_FILE = CONFIG_DIR / "bots_config.json"
PATHS_FILE = CONFIG_DIR / "paths.txt" # Path target distribusi ada di config/

# === PERBAIKAN PATH PROXY ===
MASTER_PROXY_FILE = PROXYSYNC_DIR / "success_proxy.txt"
FALLBACK_PROXY_FILE = PROXYSYNC_DIR / "proxy.txt"
# === AKHIR PERBAIKAN ===

TMUX_SESSION = "automation_hub_bots"
POSSIBLE_VENV_NAMES = [".venv", "venv", "myenv"]
DEFAULT_VENV_NAME = ".venv"

def run_cmd(command, cwd=None, env=None, capture=False):
    """Menjalankan perintah shell dengan error handling."""
    try:
        process = subprocess.run(
            command, shell=True, check=True, cwd=cwd, env=env,
            text=True, capture_output=capture, encoding='utf-8', errors='ignore' # Tambah errors='ignore'
        )
        return (process.stdout.strip(), process.stderr.strip()) if capture else True
    except subprocess.CalledProcessError as e:
        print(f"  ❌ ERROR running: {command}")
        error_output = e.stderr.strip() if e.stderr else e.stdout.strip() if e.stdout else str(e)
        if error_output:
            error_lines = error_output.splitlines()
            print(f"     Output:\n       " + "\n       ".join(error_lines[-5:]))
        else:
             print(f"     CalledProcessError: {e}")
        return False
    except FileNotFoundError:
         print(f"  ❌ ERROR: Command not found for: {command.split()[0]}")
         return False
    except Exception as e:
        print(f"  ❌ UNEXPECTED ERROR running: {command}")
        print(f"     Exception: {e}")
        return False


def load_proxies():
    """Memuat proxy dari success_proxy.txt atau fallback ke proxy.txt (di dalam proxysync/)."""
    if MASTER_PROXY_FILE.exists() and MASTER_PROXY_FILE.stat().st_size > 0:
        file_to_load = MASTER_PROXY_FILE
        source = f"'{file_to_load.name}' (tested)"
    elif FALLBACK_PROXY_FILE.exists() and FALLBACK_PROXY_FILE.stat().st_size > 0:
        file_to_load = FALLBACK_PROXY_FILE
        source = f"'{file_to_load.name}' (fallback)"
        print(f"  [Proxy] ⚠️  '{MASTER_PROXY_FILE.name}' not found or empty. Using fallback '{FALLBACK_PROXY_FILE.name}'.")
    else:
        print(f"  [Proxy] ⚠️  No proxy files found in '{PROXYSYNC_DIR.relative_to(WORKDIR)}' ('{MASTER_PROXY_FILE.name}' or '{FALLBACK_PROXY_FILE.name}'). Proxy distribution skipped.")
        return []

    try:
        with open(file_to_load, "r", encoding='utf-8') as f:
            proxies = [line.strip() for line in f if line.strip() and not line.startswith("#")]
        print(f"  [Proxy] ✅ {len(proxies)} proxies loaded from {source}.")
        return proxies
    except Exception as e:
        print(f"  [Proxy] 🔴 Failed to read '{file_to_load.name}': {e}")
        return []

def load_paths(file_path):
    """Memuat daftar path target dari file config/paths.txt."""
    if not file_path.exists():
        print(f"  [Proxy] ⚠️  Paths file '{file_path.relative_to(WORKDIR)}' not found. Proxy distribution skipped.")
        return []
    try:
        with open(file_path, "r", encoding='utf-8') as f:
            paths = [
                (WORKDIR / line.strip()).resolve() 
                for line in f if line.strip() and not line.startswith("#")
            ]
        print(f"  [Proxy] ✅ {len(paths)} target paths loaded from '{file_path.relative_to(WORKDIR)}'.")
        return paths
    except Exception as e:
        print(f"  [Proxy] 🔴 Failed to read paths file '{file_path.relative_to(WORKDIR)}': {e}")
        return []


def distribute_proxies(proxies, paths):
    """Mendistribusikan proxy ke folder bot target (sudah benar dengan shuffle)."""
    if not proxies or not paths:
        print("  [Proxy] ⏭️  No proxies or target paths. Distribution skipped.")
        return
        
    print(f"  [Proxy] 📤 Distributing {len(proxies)} proxies to {len(paths)} target paths...")
    distributed_count = 0
    skipped_count = 0
    for bot_path in paths:
        if not bot_path.is_dir():
            print(f"    🟡 Skip (Invalid path): {bot_path.relative_to(WORKDIR)}")
            skipped_count += 1
            continue
        
        proxy_file_target = bot_path / "proxies.txt"
        target_filename = "proxies.txt"
        if not proxy_file_target.exists():
            fallback_target = bot_path / "proxy.txt"
            # === PERBAIKAN: Cek juga kalo file-nya ada tapi KOSONG ===
            if fallback_target.exists() and fallback_target.stat().st_size > 0:
                 proxy_file_target = fallback_target
                 target_filename = "proxy.txt"
            # === AKHIR PERBAIKAN ===
            
        proxies_shuffled = random.sample(proxies, len(proxies))
        try:
            proxy_file_target.parent.mkdir(parents=True, exist_ok=True)
            with open(proxy_file_target, "w", encoding='utf-8') as f:
                for proxy in proxies_shuffled:
                    f.write(proxy + "\n")
            rel_path = bot_path.relative_to(WORKDIR)
            print(f"    🟢 {rel_path}/{target_filename}")
            distributed_count += 1
        except IOError as e:
            rel_path = bot_path.relative_to(WORKDIR)
            print(f"    🔴 Failed: {rel_path}/{target_filename} -> {e}")
            skipped_count += 1
        except Exception as e:
            rel_path = bot_path.relative_to(WORKDIR)
            print(f"    🔴 Unexpected Error: {rel_path}/{target_filename} -> {e}")
            skipped_count += 1
            
    print(f"  [Proxy] Distribution finished. OK: {distributed_count}, Skipped/Failed: {skipped_count}")

def get_active_venv_path(bot_path: Path):
    """Mencari path venv yang aktif di dalam folder bot."""
    for name in POSSIBLE_VENV_NAMES:
        venv_path = bot_path / name
        if venv_path.is_dir() and (venv_path / "bin" / "activate").exists():
            return venv_path
    return None

def get_venv_executable(venv_path: Path, exe_name: str):
    """Mencari executable (python/pip) di dalam venv."""
    exe_path = venv_path / "bin" / exe_name
    if exe_path.exists(): return str(exe_path)
    exe_path_win = venv_path / "Scripts" / f"{exe_name}.exe"
    if exe_path_win.exists(): return str(exe_path_win)
    exe_path_win_noexe = venv_path / "Scripts" / exe_name
    if exe_path_win_noexe.exists(): return str(exe_path_win_noexe)
    return None

def install_dependencies(bot_path: Path, bot_type: str):
    """Menginstal dependensi (pip/npm) dengan 'smart install'."""
    bot_name = bot_path.name
    print(f"    [Deps] 🔧 Checking dependencies for '{bot_name}'...")
    
    if bot_type == "python":
        req_file = bot_path / "requirements.txt"
        if not req_file.exists():
            print(f"    [Deps] ⏭️  No '{req_file.name}'. Skipping.")
            return True

        venv_path = get_active_venv_path(bot_path)
        
        if not venv_path:
            print(f"    [Deps] 🆕 Venv not found. Creating '{DEFAULT_VENV_NAME}'...")
            venv_path = bot_path / DEFAULT_VENV_NAME
            if not run_cmd(f"python3 -m venv \"{venv_path}\"", cwd=str(bot_path)):
                 print(f"    [Deps] 🔴 Failed to create venv for '{bot_name}'.")
                 return False

            pip_exe = get_venv_executable(venv_path, "pip") or get_venv_executable(venv_path, "pip3")
            if not pip_exe:
                print(f"    [Deps] 🔴 pip not found in new venv '{venv_path}'. Cannot install dependencies.")
                return False

            print(f"    [Deps] 📦 Installing dependencies via '{Path(pip_exe).name}' (First Run)...")
            if not run_cmd(f"\"{pip_exe}\" install --no-cache-dir --upgrade pip", cwd=str(bot_path)): print(f"    [Deps] 🟡 Failed to upgrade pip.")
            if not run_cmd(f"\"{pip_exe}\" install --no-cache-dir -r \"{req_file.name}\"", cwd=str(bot_path)):
                 print(f"    [Deps] 🔴 Failed to install dependencies for '{bot_name}'.")
                 return False
            print(f"    [Deps] ✅ Dependencies installed.")
            return True
        else:
            print(f"    [Deps] ✅ Venv found ('{venv_path.name}'). Skipping install.")
            return True

    elif bot_type == "javascript":
        pkg_file = bot_path / "package.json"
        if not pkg_file.exists():
            print(f"    [Deps] ⏭️  No '{pkg_file.name}'. Skipping.")
            return True

        node_modules_dir = bot_path / "node_modules"
        if not node_modules_dir.is_dir() or not any(node_modules_dir.iterdir()):
            print(f"    [Deps] 🆕 'node_modules' not found or empty. Running 'npm install'...")
            if not run_cmd("command -v npm", capture=True)[0]:
                 print("    [Deps] 🔴 npm command not found. Cannot install dependencies.")
                 return False

            if not run_cmd("npm install --silent --no-progress --omit=dev", cwd=str(bot_path)):
                 print(f"    [Deps] 🔴 Failed to run 'npm install' for '{bot_name}'.")
                 return False
            print(f"    [Deps] ✅ Dependencies installed.")
            return True
        else:
            print(f"    [Deps] ✅ 'node_modules' found. Skipping install.")
            return True
    else:
        print(f"    [Deps] 🟡 Unknown bot type '{bot_type}'. Skipping dependency check.")
        return True

# === PERBAIKAN: Tambahkan argumen 'entrypoint' ===
def get_run_command(bot_path: Path, bot_type: str, entrypoint: str = None):
    """Menentukan perintah untuk menjalankan bot."""
    if bot_type == "python":
        venv_path = get_active_venv_path(bot_path)
        python_exe = "python3"
        if venv_path:
            python_exe_in_venv = get_venv_executable(venv_path, "python") or get_venv_executable(venv_path, "python3")
            if python_exe_in_venv:
                python_exe = python_exe_in_venv
                print(f"    [Run] 🟢 Using Python from venv: {python_exe}")
            else:
                print(f"    [Run] 🟡 Venv found but Python executable missing. Falling back to global '{python_exe}'.")
        else:
             print(f"    [Run] 🟡 No active venv found. Using global '{python_exe}'.")
        
        # === PERBAIKAN: Cek entrypoint kustom DULU ===
        if entrypoint:
            if (bot_path / entrypoint).exists():
                print(f"    [Run] 🟢 Found custom entry point from config: {entrypoint}")
                return f"\"{python_exe}\"", entrypoint
            else:
                print(f"    [Run] 🔴 Custom entry point '{entrypoint}' not found. Falling back...")
        # === AKHIR PERBAIKAN ===
        
        for entry in ["run.py", "main.py", "bot.py", "app.py"]:
            if (bot_path / entry).exists():
                print(f"    [Run] 🟢 Found entry point: {entry}")
                return f"\"{python_exe}\"", entry
        print(f"    [Run] 🔴 Python entry point not found in '{bot_path.name}'.")
        return None, None

    elif bot_type == "javascript":
        # === PERBAIKAN: Cek entrypoint kustom DULU ===
        if entrypoint:
            if (bot_path / entrypoint).exists():
                print(f"    [Run] 🟢 Found custom entry point from config: {entrypoint}")
                return "node", entrypoint
            else:
                print(f"    [Run] 🔴 Custom entry point '{entrypoint}' not found. Falling back...")
        # === AKHIR PERBAIKAN ===
        
        pkg_file = bot_path / "package.json"
        if pkg_file.exists():
            try:
                with open(pkg_file, "r", encoding='utf-8') as f:
                    pkg_data = json.load(f)
                    if "scripts" in pkg_data and "start" in pkg_data["scripts"]:
                        print("    [Run] 🟢 Found 'npm start' script.")
                        return "npm", "start"
                    if "main" in pkg_data and (bot_path / pkg_data["main"]).exists():
                         entry = pkg_data["main"]
                         print(f"    [Run] 🟢 Found entry point from package.json 'main': {entry}")
                         return "node", entry
            except Exception as e:
                print(f"    [Run] 🟡 Failed to read '{pkg_file.name}': {e}")
        
        for entry in ["index.js", "main.js", "bot.js", "app.js"]:
            if (bot_path / entry).exists():
                print(f"    [Run] 🟢 Found standard entry point: {entry}")
                return "node", entry
        print(f"    [Run] 🔴 JS entry point not found in '{bot_path.name}'.")
        return None, None

    print(f"   [Run] 🔴 Unknown bot type '{bot_type}' for '{bot_path.name}'.")
    return None, None

# === PERBAIKAN: Hapus 'git reset --hard' ===
def sync_bot_repo(name: str, bot_path: Path, repo_url: str):
    """Melakukan git clone atau git pull (secara aman) untuk repo bot."""
    print(f"\n--- 🔄 Syncing: {name} ---")
    try: bot_path.relative_to(WORKDIR)
    except ValueError:
        print(f"  🔴 SECURITY WARNING: Path '{bot_path}' outside project. Skipping sync.")
        return False

    bot_path.parent.mkdir(parents=True, exist_ok=True)
    git_dir = bot_path / ".git"
    
    if git_dir.is_dir():
        print(f"  [Sync] Repository exists. Performing 'git pull' (safe mode)...")
        # Kita HAPUS 'git reset --hard HEAD'
        # run_cmd("git reset --hard HEAD", cwd=str(bot_path)) # <-- DIHAPUS
        
        # 'git pull --rebase --autostash' harusnya aman untuk file untracked (seperti pk.txt)
        if not run_cmd("git pull --rebase --autostash", cwd=str(bot_path)):
            print(f"  [Sync] 🟡 Pull failed. Attempting fetch + reset (safe)...")
            # Coba reset 'soft' atau 'mixed' dulu, tapi kalo gagal ya udah
            # Untuk skrip, 'autostash' biasanya udah cukup
            if run_cmd("git fetch origin", cwd=str(bot_path)) and \
               run_cmd("git reset --hard origin/HEAD", cwd=str(bot_path)): # <--- Pengecualian, ini OK kalo pull gagal
                 print(f"  [Sync] ✅ Recovered via fetch + HARD reset.")
            else:
                 print(f"  [Sync] 🔴 Recovery failed. Using existing code.")
    else:
        if bot_path.exists():
             print(f"  [Sync] 🟡 Path exists but not git repo. Removing and cloning...")
             try:
                  if bot_path.is_dir(): shutil.rmtree(bot_path)
                  else: bot_path.unlink()
             except Exception as e:
                  print(f"  [Sync] 🔴 Failed remove: {e}. Skipping clone.")
                  return False

        print(f"  [Sync] 📥 Cloning '{repo_url}'...")
        if not run_cmd(f"git clone --depth 1 \"{repo_url}\" \"{bot_path.name}\"", cwd=str(bot_path.parent)):
             print(f"  [Sync] 🔴 Clone failed for '{name}'.")
             return False
        print(f"  [Sync] ✅ Cloned successfully.")

    if not bot_path.is_dir():
         print(f"  [Sync] 🔴 Bot directory '{bot_path.name}' not found after sync.")
         return False
    return True
# === AKHIR PERBAIKAN ===

def launch_in_tmux(name: str, bot_path: Path, executor: str, args: str):
    """Meluncurkan bot di window tmux baru."""
    safe_name = re.sub(r'[^a-zA-Z0-9_.-]', '', name)[:50]
    abs_bot_path = str(bot_path.resolve())
    abs_executor = executor
    tmux_cmd = f"tmux new-window -t {TMUX_SESSION} -n \"{safe_name}\" -c \"{abs_bot_path}\" '{abs_executor} {args}'"
    
    print(f"    [Tmux] 🚀 Launching '{safe_name}'...")
    if not run_cmd(tmux_cmd):
        print(f"    [Tmux] 🔴 Failed to launch '{safe_name}'.")
        return False
    print(f"    [Tmux] ✅ '{safe_name}' launched.")
    return True

def is_bot_running_in_tmux(name: str):
    """Mengecek apakah window dengan nama bot sudah ada di tmux."""
    safe_name = re.sub(r'[^a-zA-Z0-9_.-]', '', name)[:50]
    stdout, stderr = run_cmd(f"tmux list-windows -t {TMUX_SESSION} -F '#{{window_name}}'", capture=True)
    if stdout is not None:
        return safe_name in stdout.strip().split('\n')
    return False

def main():
    """Fungsi utama orchestrator."""
    os.chdir(WORKDIR)
    print("=" * 60)
    print("  🚀 AUTOMATION-HUB DEPLOYER (REMOTE ENVIRONMENT) 🚀")
    print(f"  WORKDIR: {WORKDIR}")
    print("=" * 60)

    print("\n[1/5] 🧹 Cleaning up old tmux session...")
    run_cmd(f"tmux kill-session -t {TMUX_SESSION}", capture=True)

    print("\n[2/5] 📡 Loading and Distributing Proxies...")
    master_proxies = load_proxies() # Load dari proxysync/
    target_paths = load_paths(PATHS_FILE) # Load dari config/
    if master_proxies and target_paths:
        distribute_proxies(master_proxies, target_paths) # Distribusi ke path target
    else:
        print("  [Proxy] ⏭️  Skipping distribution (proxies or paths missing).")

    print(f"\n[3/5] 📋 Loading Bot Configuration ('{CONFIG_FILE.relative_to(WORKDIR)}')...")
    try:
        with open(CONFIG_FILE, "r", encoding='utf-8') as f: config_data = json.load(f)
        bots_and_tools = config_data.get("bots_and_tools", [])
        if not bots_and_tools: print(f"  🟡 Config loaded, but 'bots_and_tools' empty.")
        else: print(f"       ✅ Found {len(bots_and_tools)} entries.")
    except FileNotFoundError: print(f"  🔴 FATAL: Config file '{CONFIG_FILE.name}' not found!"); sys.exit(1)
    except json.JSONDecodeError as e: print(f"  🔴 FATAL: Error decoding JSON '{CONFIG_FILE.name}': {e}"); sys.exit(1)
    except Exception as e: print(f"  🔴 FATAL: Failed load config '{CONFIG_FILE.name}': {e}"); sys.exit(1)

    print(f"\n[4/5] 🎬 Creating new tmux session '{TMUX_SESSION}'...")
    if not run_cmd(f"tmux new-session -d -s {TMUX_SESSION} -n 'dashboard'"):
        print(f"  🔴 FATAL: Failed create tmux session '{TMUX_SESSION}'."); sys.exit(1)

    print(f"\n[5/5] 🚀 Processing and Launching Bots...")
    success_count, fail_count, skip_count = 0, 0, 0

    for entry in bots_and_tools:
        name = entry.get("name"); path_str = entry.get("path"); repo_url = entry.get("repo_url")
        enabled = entry.get("enabled", False); bot_type = entry.get("type")
        # === PERBAIKAN: Baca field 'entrypoint' ===
        entrypoint = entry.get("entrypoint") 
        # === AKHIR PERBAIKAN ===

        if not all([name, path_str, repo_url, bot_type]):
            print(f"\n--- 🟡 Skipping Invalid Entry ---\n  Data: {entry}"); skip_count += 1; continue
            
        if name == "ProxySync-Tool": 
            print(f"\n--- ⏭️  Skipping: {name} (Handled by auto-start.sh) ---"); skip_count += 1; continue

        if not enabled: print(f"\n--- 🔵 Skipping Disabled: {name} ---"); skip_count += 1; continue
            
        if is_bot_running_in_tmux(name): print(f"\n--- ✅ Already Running: {name} ---"); success_count += 1; continue

        bot_path = (WORKDIR / path_str).resolve()

        if not sync_bot_repo(name, bot_path, repo_url): fail_count += 1; continue
        if not install_dependencies(bot_path, bot_type): fail_count += 1; continue
        
        # === PERBAIKAN: Kirim 'entrypoint' ke get_run_command ===
        executor_base, args_base = get_run_command(bot_path, bot_type, entrypoint)
        
        if not executor_base: print(f"  🔴 Cannot run '{name}'. Skipping launch."); fail_count += 1; continue
            
        # Perintah eksekusi BOT ASLI
        full_bot_command = f"{executor_base} {args_base}"
        
        # Cek apakah ada script replay (autostart.json)
        autostart_config = bot_path / "autostart.json"
        if autostart_config.exists():
            # Jika ada, ganti perintah eksekusi dengan EXPECT RUNNER
            print(f"    [Run] 🤖 Found autostart.json. Running via Expect Replay...")
            executor = f"python3"
            args = f"{SETUP_TOOLS_DIR / 'expect_runner.py'} \"{bot_path}\" {full_bot_command}"
        else:
            executor, args = executor_base, args_base
        # === AKHIR PERBAIKAN ===
            
        if launch_in_tmux(name, bot_path, executor, args): success_count += 1
        else: fail_count += 1

    print("\n" + "=" * 60)
    print("  📊 DEPLOYMENT SUMMARY 📊")
    print(f"  ✅ Success / Already Running: {success_count}")
    print(f"  🔴 Failed (Sync/Deps/Launch): {fail_count}")
    print(f"  ⏭️ Skipped (Disabled/Invalid/ProxySync): {skip_count}")
    print("=" * 60)
    
    if fail_count > 0: print(f"\n  ⚠️  {fail_count} bot(s) failed. Check logs.")
    print(f"\n  ✨ All complete. Use 'tmux a -t {TMUX_SESSION}' or TUI Menu 4.")
    print("=" * 60)

    # === PERBAIKAN LOG: Selalu exit 0 ===
    # Biarkan Orchestrator tahu bahwa skrip *selesai* berjalan,
    # meskipun beberapa bot gagal. Kegagalan bot bukan error fatal.
    sys.exit(0)
    # === AKHIR PERBAIKAN ===

if __name__ == "__main__":
    main()
