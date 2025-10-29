#!/usr/bin/env python3
"""
deploy_bots.py - Remote Environment Orchestrator
Fitur: Self-healing, Proxy distribution, Git sync, Dependency install, Tmux launch
(Versi 2: Smart Install & Prioritas Success Proxy)
"""

import os
import json
import subprocess
import sys
import random
import re
from pathlib import Path

# === Gunakan path absolut/relatif dari direktori skrip ===
SCRIPT_DIR = Path(__file__).parent.resolve()
WORKDIR = SCRIPT_DIR # Asumsi skrip ini ada di root project
CONFIG_DIR = WORKDIR / "config"
PROXYSYNC_DIR = WORKDIR / "proxysync"

CONFIG_FILE = CONFIG_DIR / "bots_config.json"
PATHS_FILE = CONFIG_DIR / "paths.txt"
MASTER_PROXY_FILE = PROXYSYNC_DIR / "success_proxy.txt"
# === PERBAIKAN: Path fallback proxy juga di proxysync ===
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
        # Tampilkan error dengan lebih jelas
        print(f"  ❌ ERROR running: {command}")
        # Prioritaskan stderr jika ada, jika tidak, stdout
        error_output = e.stderr.strip() if e.stderr else e.stdout.strip() if e.stdout else str(e)
        if error_output:
            # Tampilkan beberapa baris terakhir error jika panjang
            error_lines = error_output.splitlines()
            print(f"     Output:\n       " + "\n       ".join(error_lines[-5:])) # Tampilkan max 5 baris terakhir
        else:
             print(f"     CalledProcessError: {e}") # Fallback
        return False
    except FileNotFoundError:
         print(f"  ❌ ERROR: Command not found for: {command.split()[0]}")
         return False
    except Exception as e:
        print(f"  ❌ UNEXPECTED ERROR running: {command}")
        print(f"     Exception: {e}")
        return False


def load_proxies():
    """Memuat proxy dari success_proxy.txt atau fallback ke proxy.txt."""
    if MASTER_PROXY_FILE.exists() and MASTER_PROXY_FILE.stat().st_size > 0:
        file_to_load = MASTER_PROXY_FILE
        source = f"'{MASTER_PROXY_FILE.name}' (tested)"
    elif FALLBACK_PROXY_FILE.exists() and FALLBACK_PROXY_FILE.stat().st_size > 0:
        file_to_load = FALLBACK_PROXY_FILE
        source = f"'{FALLBACK_PROXY_FILE.name}' (fallback)"
        print(f"  [Proxy] ⚠️  '{MASTER_PROXY_FILE.name}' not found or empty. Using fallback '{FALLBACK_PROXY_FILE.name}'.")
    else:
        print(f"  [Proxy] ⚠️  No proxy files found ('{MASTER_PROXY_FILE.name}' or '{FALLBACK_PROXY_FILE.name}'). Proxy distribution skipped.")
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
    """Memuat daftar path target dari file config."""
    if not file_path.exists():
        print(f"  [Proxy] ⚠️  Paths file '{file_path.name}' not found. Proxy distribution skipped.")
        return []
    try:
        with open(file_path, "r", encoding='utf-8') as f:
            # Buat path absolut relatif terhadap WORKDIR
            paths = [
                (WORKDIR / line.strip()).resolve() # Gunakan resolve() untuk path absolut bersih
                for line in f if line.strip() and not line.startswith("#")
            ]
        print(f"  [Proxy] ✅ {len(paths)} target paths loaded from '{file_path.name}'.")
        return paths
    except Exception as e:
        print(f"  [Proxy] 🔴 Failed to read paths file '{file_path.name}': {e}")
        return []


def distribute_proxies(proxies, paths):
    """Mendistribusikan proxy ke folder bot target."""
    if not proxies or not paths:
        print("  [Proxy] ⏭️  No proxies or target paths. Distribution skipped.")
        return
        
    print(f"  [Proxy] 📤 Distributing {len(proxies)} proxies to {len(paths)} target paths...")
    distributed_count = 0
    skipped_count = 0
    for bot_path in paths:
        # Validasi path sekali lagi
        if not bot_path.is_dir():
            print(f"    🟡 Skip (Invalid path): {bot_path}")
            skipped_count += 1
            continue
        
        # Coba cari 'proxies.txt' atau 'proxy.txt'
        proxy_file_target = bot_path / "proxies.txt"
        target_filename = "proxies.txt"
        if not proxy_file_target.exists():
            fallback_target = bot_path / "proxy.txt"
            if fallback_target.exists():
                 proxy_file_target = fallback_target
                 target_filename = "proxy.txt"
            # Jika keduanya tidak ada, tetap pakai 'proxies.txt' sebagai target
            
        proxies_shuffled = random.sample(proxies, len(proxies))
        try:
            # Pastikan direktori ada sebelum menulis (seharusnya sudah ada)
            proxy_file_target.parent.mkdir(parents=True, exist_ok=True)
            with open(proxy_file_target, "w", encoding='utf-8') as f:
                for proxy in proxies_shuffled:
                    f.write(proxy + "\n")
            # Tampilkan path relatif biar lebih pendek
            rel_path = bot_path.relative_to(WORKDIR)
            print(f"    🟢 {rel_path}/{target_filename}")
            distributed_count += 1
        except IOError as e:
            rel_path = bot_path.relative_to(WORKDIR)
            print(f"    🔴 Failed: {rel_path}/{target_filename} -> {e}")
            skipped_count += 1
        except Exception as e: # Tangkap error lain
            rel_path = bot_path.relative_to(WORKDIR)
            print(f"    🔴 Unexpected Error: {rel_path}/{target_filename} -> {e}")
            skipped_count += 1
            
    print(f"  [Proxy] Distribution finished. OK: {distributed_count}, Skipped/Failed: {skipped_count}")


def get_active_venv_path(bot_path: Path):
    """Mencari path venv yang aktif di dalam folder bot."""
    for name in POSSIBLE_VENV_NAMES:
        venv_path = bot_path / name
        # Cek apakah itu direktori DAN ada file activate
        if venv_path.is_dir() and (venv_path / "bin" / "activate").exists():
            return venv_path
    return None

def get_venv_executable(venv_path: Path, exe_name: str):
    """Mencari executable (python/pip) di dalam venv."""
    # Prioritaskan 'bin' (Linux/macOS)
    exe_path = venv_path / "bin" / exe_name
    if exe_path.exists():
        return str(exe_path)
    # Fallback ke 'Scripts' (Windows)
    exe_path_win = venv_path / "Scripts" / f"{exe_name}.exe"
    if exe_path_win.exists():
        return str(exe_path_win)
    # Coba tanpa .exe untuk Windows
    exe_path_win_noexe = venv_path / "Scripts" / exe_name
    if exe_path_win_noexe.exists():
         return str(exe_path_win_noexe)
    return None # Tidak ditemukan

def install_dependencies(bot_path: Path, bot_type: str):
    """Menginstal dependensi (pip/npm) dengan 'smart install'."""
    bot_name = bot_path.name
    print(f"    [Deps] 🔧 Checking dependencies for '{bot_name}'...")
    
    if bot_type == "python":
        req_file = bot_path / "requirements.txt"
        if not req_file.exists():
            print(f"    [Deps] ⏭️  No '{req_file.name}'. Skipping.")
            return True # Anggap sukses jika tidak ada requirements

        venv_path = get_active_venv_path(bot_path)
        
        if not venv_path:
            print(f"    [Deps] 🆕 Venv not found. Creating '{DEFAULT_VENV_NAME}'...")
            venv_path = bot_path / DEFAULT_VENV_NAME
            # Buat venv menggunakan python3
            if not run_cmd(f"python3 -m venv \"{venv_path}\"", cwd=str(bot_path)):
                 print(f"    [Deps] 🔴 Failed to create venv for '{bot_name}'.")
                 return False # Gagal buat venv

            # Cari pip di venv baru
            pip_exe = get_venv_executable(venv_path, "pip") or get_venv_executable(venv_path, "pip3")
            if not pip_exe:
                print(f"    [Deps] 🔴 pip not found in new venv '{venv_path}'. Cannot install dependencies.")
                return False # Gagal cari pip

            print(f"    [Deps] 📦 Installing dependencies via '{Path(pip_exe).name}' (First Run)...")
            # Install menggunakan pip dari venv
            if not run_cmd(f"\"{pip_exe}\" install --no-cache-dir --upgrade pip", cwd=str(bot_path)): print(f"    [Deps] 🟡 Failed to upgrade pip.") # Coba lanjut
            if not run_cmd(f"\"{pip_exe}\" install --no-cache-dir -r \"{req_file.name}\"", cwd=str(bot_path)):
                 print(f"    [Deps] 🔴 Failed to install dependencies for '{bot_name}'.")
                 return False # Gagal install deps
            print(f"    [Deps] ✅ Dependencies installed.")
            return True
        else:
            print(f"    [Deps] ✅ Venv found ('{venv_path.name}'). Skipping install.")
            return True # Sukses (sudah ada)

    elif bot_type == "javascript":
        pkg_file = bot_path / "package.json"
        if not pkg_file.exists():
            print(f"    [Deps] ⏭️  No '{pkg_file.name}'. Skipping.")
            return True # Anggap sukses

        node_modules_dir = bot_path / "node_modules"
        # Cek apakah node_modules ADA dan BUKAN file kosong
        if not node_modules_dir.is_dir() or not any(node_modules_dir.iterdir()):
            print(f"    [Deps] 🆕 'node_modules' not found or empty. Running 'npm install'...")
            # Cek npm ada
            if not run_cmd("command -v npm", capture=True)[0]:
                 print("    [Deps] 🔴 npm command not found. Cannot install dependencies.")
                 return False

            # Jalankan npm install
            if not run_cmd("npm install --silent --no-progress --omit=dev", cwd=str(bot_path)): # Tambah --omit=dev
                 print(f"    [Deps] 🔴 Failed to run 'npm install' for '{bot_name}'.")
                 return False # Gagal npm install
            print(f"    [Deps] ✅ Dependencies installed.")
            return True
        else:
            print(f"    [Deps] ✅ 'node_modules' found. Skipping install.")
            return True # Sukses (sudah ada)
    else:
        print(f"    [Deps] 🟡 Unknown bot type '{bot_type}'. Skipping dependency check.")
        return True # Anggap sukses

def get_run_command(bot_path: Path, bot_type: str):
    """Menentukan perintah untuk menjalankan bot."""
    
    if bot_type == "python":
        venv_path = get_active_venv_path(bot_path)
        python_exe = "python3" # Default ke python3 global
        
        if venv_path:
            # Cari python atau python3 di venv
            python_exe_in_venv = get_venv_executable(venv_path, "python") or get_venv_executable(venv_path, "python3")
            if python_exe_in_venv:
                python_exe = python_exe_in_venv
                print(f"    [Run] 🟢 Using Python from venv: {python_exe}")
            else:
                print(f"    [Run] 🟡 Venv found but Python executable missing inside. Falling back to global '{python_exe}'.")
        else:
             print(f"    [Run] 🟡 No active venv found. Using global '{python_exe}'.")
        
        # Cari file entry point
        for entry in ["run.py", "main.py", "bot.py", "app.py"]: # Tambah app.py
            if (bot_path / entry).exists():
                print(f"    [Run] 🟢 Found entry point: {entry}")
                # Kembalikan path absolut ke python_exe dan nama file entry point
                return f"\"{python_exe}\"", entry
        print(f"    [Run] 🔴 Python entry point (run.py, main.py, bot.py, app.py) not found in '{bot_path.name}'.")
        return None, None

    elif bot_type == "javascript":
        pkg_file = bot_path / "package.json"
        if pkg_file.exists():
            try:
                with open(pkg_file, "r", encoding='utf-8') as f:
                    pkg_data = json.load(f)
                    # Prioritaskan scripts.start
                    if "scripts" in pkg_data and "start" in pkg_data["scripts"]:
                        print("    [Run] 🟢 Found 'npm start' script.")
                        # Kembalikan 'npm' dan 'start'
                        return "npm", "start"
                    # Fallback ke main jika ada
                    if "main" in pkg_data and (bot_path / pkg_data["main"]).exists():
                         entry = pkg_data["main"]
                         print(f"    [Run] 🟢 Found entry point from package.json 'main': {entry}")
                         return "node", entry # Kembalikan 'node' dan path dari main
            except Exception as e:
                print(f"    [Run] 🟡 Failed to read '{pkg_file.name}': {e}")
        
        # Jika tidak ada package.json atau start script/main, cari file standar
        for entry in ["index.js", "main.js", "bot.js", "app.js"]: # Tambah app.js
            if (bot_path / entry).exists():
                print(f"    [Run] 🟢 Found standard entry point: {entry}")
                # Kembalikan 'node' dan nama file
                return "node", entry
        print(f"    [Run] 🔴 JS entry point (npm start, main in package.json, index.js, main.js, bot.js, app.js) not found in '{bot_path.name}'.")
        return None, None

    print(f"   [Run] 🔴 Unknown bot type '{bot_type}' for '{bot_path.name}'. Cannot determine run command.")
    return None, None

def sync_bot_repo(name: str, bot_path: Path, repo_url: str):
    """Melakukan git clone atau git pull untuk repo bot."""
    print(f"\n--- 🔄 Syncing: {name} ---")
    
    # Keamanan: Pastikan path ada di dalam WORKDIR
    try:
        bot_path.relative_to(WORKDIR)
    except ValueError:
        print(f"  🔴 SECURITY WARNING: Path '{bot_path}' is outside the project directory. Skipping sync.")
        return False # Gagal

    # Buat direktori induk jika belum ada
    bot_path.parent.mkdir(parents=True, exist_ok=True)
    git_dir = bot_path / ".git"
    
    if git_dir.is_dir():
        print(f"  [Sync] Repository exists. Performing 'git pull --rebase'...")
        # Coba reset dulu jika ada perubahan lokal
        run_cmd("git reset --hard HEAD", cwd=str(bot_path))
        # Pull dengan rebase
        if not run_cmd("git pull --rebase --autostash", cwd=str(bot_path)): # Tambah autostash
            print(f"  [Sync] 🟡 Pull failed for '{name}'. Attempting fetch + reset...")
            # Jika pull gagal, coba fetch + reset
            if run_cmd("git fetch origin", cwd=str(bot_path)) and \
               run_cmd("git reset --hard origin/HEAD", cwd=str(bot_path)): # Ganti HEAD dengan origin/HEAD
                 print(f"  [Sync] ✅ Recovered via fetch + reset.")
            else:
                 print(f"  [Sync] 🔴 Recovery failed. Using existing code.")
                 # Tetap lanjut, tapi repo mungkin tidak update
    else:
        # Hapus folder jika ada tapi bukan repo git
        if bot_path.exists():
             print(f"  [Sync] 🟡 Path '{bot_path.name}' exists but is not a git repository. Removing and cloning...")
             try:
                  # Hati-hati dengan ini, pastikan path benar
                  if bot_path.is_dir():
                       shutil.rmtree(bot_path)
                  else:
                       bot_path.unlink()
             except Exception as e:
                  print(f"  [Sync] 🔴 Failed to remove existing directory/file: {e}. Skipping clone.")
                  return False # Gagal clone

        print(f"  [Sync] 📥 Cloning '{repo_url}'...")
        if not run_cmd(f"git clone --depth 1 \"{repo_url}\" \"{bot_path.name}\"", cwd=str(bot_path.parent)):
             print(f"  [Sync] 🔴 Clone failed for '{name}'.")
             return False # Gagal clone
        print(f"  [Sync] ✅ Cloned successfully.")

    # Verifikasi akhir apakah direktori bot ada
    if not bot_path.is_dir():
         print(f"  [Sync] 🔴 Bot directory '{bot_path.name}' not found after sync attempt.")
         return False
         
    return True # Sukses sync (atau setidaknya direktori ada)

def launch_in_tmux(name: str, bot_path: Path, executor: str, args: str):
    """Meluncurkan bot di window tmux baru."""
    # Buat nama window yang aman (hapus karakter aneh)
    safe_name = re.sub(r'[^a-zA-Z0-9_.-]', '', name)[:50] # Batasi panjang juga
    # Perintah tmux: buat window baru (-n nama), set direktori kerja (-c path), jalankan command
    # Gunakan path absolut untuk direktori kerja dan executor
    abs_bot_path = str(bot_path.resolve())
    abs_executor = executor # Asumsi executor ada di PATH atau sudah absolut
    # Escape quotes di dalam command jika perlu (misal jika path ada spasi)
    # Bash biasanya handle path dengan spasi jika di-quote benar
    tmux_cmd = f"tmux new-window -t {TMUX_SESSION} -n \"{safe_name}\" -c \"{abs_bot_path}\" '{abs_executor} {args}'"
    
    print(f"    [Tmux] 🚀 Launching '{safe_name}'...")
    if not run_cmd(tmux_cmd):
        print(f"    [Tmux] 🔴 Failed to launch '{safe_name}' in tmux.")
        return False
    print(f"    [Tmux] ✅ '{safe_name}' launched.")
    return True

def is_bot_running_in_tmux(name: str):
    """Mengecek apakah window dengan nama bot sudah ada di tmux."""
    safe_name = re.sub(r'[^a-zA-Z0-9_.-]', '', name)[:50]
    # Perintah: list windows (-t sesi), format output (-F nama window)
    stdout, stderr = run_cmd(f"tmux list-windows -t {TMUX_SESSION} -F '#{{window_name}}'", capture=True)
    if stdout is not None:
        running_windows = stdout.strip().split('\n')
        return safe_name in running_windows
    # Jika command gagal (misal sesi tidak ada), anggap tidak running
    # print(f"    [Tmux] Check failed for '{safe_name}': {stderr}") # Debugging
    return False

def main():
    """Fungsi utama orchestrator."""
    os.chdir(WORKDIR) # Pindah ke direktori kerja utama
    print("=" * 60)
    print("  🚀 AUTOMATION-HUB DEPLOYER (REMOTE ENVIRONMENT) 🚀")
    print(f"  WORKDIR: {WORKDIR}")
    print("=" * 60)

    # 1. Kill Sesi Tmux Lama (jika ada)
    print("\n[1/5] 🧹 Cleaning up old tmux session...")
    run_cmd(f"tmux kill-session -t {TMUX_SESSION}", capture=True) # capture=True biar tidak spam error jika sesi tidak ada

    # 2. Distribusi Proxy
    print("\n[2/5] 📡 Loading and Distributing Proxies...")
    master_proxies = load_proxies()
    target_paths = load_paths(PATHS_FILE)
    # Jalankan distribusi HANYA jika ada proxy DAN path target
    if master_proxies and target_paths:
        distribute_proxies(master_proxies, target_paths)
    else:
        print("  [Proxy] ⏭️  Skipping distribution due to missing proxies or paths.")

    # 3. Load Konfigurasi Bot
    print(f"\n[3/5] 📋 Loading Bot Configuration ('{CONFIG_FILE.relative_to(WORKDIR)}')...")
    try:
        with open(CONFIG_FILE, "r", encoding='utf-8') as f:
            config_data = json.load(f)
        bots_and_tools = config_data.get("bots_and_tools", [])
        if not bots_and_tools:
             print(f"  🟡 Config file loaded, but 'bots_and_tools' list is empty or missing.")
             # Mungkin tidak perlu exit, tapi proses bot akan dilewati
        else:
             print(f"       ✅ Found {len(bots_and_tools)} entries.")
    except FileNotFoundError:
        print(f"  🔴 FATAL: Config file '{CONFIG_FILE.name}' not found!")
        sys.exit(1)
    except json.JSONDecodeError as e:
        print(f"  🔴 FATAL: Error decoding JSON from '{CONFIG_FILE.name}': {e}")
        sys.exit(1)
    except Exception as e:
        print(f"  🔴 FATAL: Failed to load config '{CONFIG_FILE.name}': {e}")
        sys.exit(1)

    # 4. Buat Sesi Tmux Baru
    print(f"\n[4/5] 🎬 Creating new tmux session '{TMUX_SESSION}'...")
    if not run_cmd(f"tmux new-session -d -s {TMUX_SESSION} -n 'dashboard'"):
        print(f"  🔴 FATAL: Failed to create tmux session '{TMUX_SESSION}'. Check if tmux is installed.")
        sys.exit(1)

    # 5. Proses Setiap Bot/Tool
    print(f"\n[5/5] 🚀 Processing and Launching Bots...")
    success_count = 0
    fail_count = 0
    skip_count = 0

    for entry in bots_and_tools:
        name = entry.get("name")
        path_str = entry.get("path")
        repo_url = entry.get("repo_url")
        enabled = entry.get("enabled", False)
        bot_type = entry.get("type")

        # Validasi entri dasar
        if not all([name, path_str, repo_url, bot_type]):
            print(f"\n--- 🟡 Skipping Invalid Entry ---")
            print(f"  Entry data: {entry}")
            skip_count += 1
            continue
            
        # === PERBAIKAN: Skip total ProxySync di sini ===
        if name == "ProxySync-Tool":
            print(f"\n--- ⏭️ Skipping: {name} (Handled by auto-start.sh) ---")
            skip_count += 1
            continue
        # === AKHIR PERBAIKAN ===

        if not enabled:
            print(f"\n--- 🔵 Skipping Disabled: {name} ---")
            skip_count += 1
            continue
            
        # Cek apakah sudah running (mungkin dari run sebelumnya yg gagal sebagian)
        if is_bot_running_in_tmux(name):
            print(f"\n--- ✅ Already Running: {name} (Skipping re-launch) ---")
            success_count += 1 # Hitung sebagai sukses jika sudah running
            continue

        # Tentukan path absolut bot
        bot_path = (WORKDIR / path_str).resolve()

        # Sync Repo (Clone atau Pull)
        if not sync_bot_repo(name, bot_path, repo_url):
            fail_count += 1
            continue # Lanjut ke bot berikutnya jika sync gagal
            
        # Install Dependencies
        if not install_dependencies(bot_path, bot_type):
            fail_count += 1
            continue # Lanjut ke bot berikutnya jika deps gagal
        
        # Tentukan Perintah Run
        executor, args = get_run_command(bot_path, bot_type)
        if not executor:
            print(f"  🔴 Failed to determine run command for '{name}'. Skipping launch.")
            fail_count += 1
            continue # Lanjut ke bot berikutnya jika tidak tahu cara run
            
        # Launch di Tmux
        if launch_in_tmux(name, bot_path, executor, args):
            success_count += 1
        else:
            fail_count += 1
            # Tidak perlu continue, error sudah di log

    # Ringkasan Hasil
    print("\n" + "=" * 60)
    print("  📊 DEPLOYMENT SUMMARY 📊")
    print(f"  ✅ Success / Already Running: {success_count}")
    print(f"  🔴 Failed (Sync/Deps/Launch): {fail_count}")
    print(f"  ⏭️ Skipped (Disabled/Invalid/ProxySync): {skip_count}")
    print("=" * 60)
    
    if fail_count > 0:
        print(f"\n  ⚠️  {fail_count} bot(s) failed to deploy properly. Check logs above.")
        print(f"  💡 You might need to manually check/fix them in the tmux session.")

    print(f"\n  ✨ All processing complete. Use 'tmux a -t {TMUX_SESSION}' or Menu 4 in TUI to monitor.")
    print("=" * 60)

    # Exit code berdasarkan hasil
    if fail_count > 0:
        sys.exit(1) # Exit dengan error jika ada kegagalan
    else:
        sys.exit(0) # Exit sukses jika semua OK

if __name__ == "__main__":
    main()
