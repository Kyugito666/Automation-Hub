#!/usr/bin/env python3
"""
deploy_bots.py
Otak remote environment untuk Automation-Hub.

Tugas skrip ini:
1.  Membunuh sesi tmux lama (self-healing).
2.  Mendistribusikan proxy (dari TUI lokal) ke semua path bot.
3.  Membaca 'bots_config.json'.
4.  Melakukan 'git clone' atau 'git pull' untuk setiap bot.
5.  Membuat/mendeteksi venv Python dan menginstal dependensi (pip/npm).
6.  Meluncurkan setiap bot dalam sesi 'tmux' terpisah.
"""

import os
import json
import subprocess
import sys
import shutil
import random
from pathlib import Path

# --- KONSTANTA ---
CONFIG_FILE = "config/bots_config.json"
PATHS_FILE = "config/paths.txt"
MASTER_PROXY_FILE = "config/proxy.txt"
TMUX_SESSION = "automation_hub_bots"
WORKDIR = "/workspaces/automation-hub"

# Daftar nama venv yang mungkin
POSSIBLE_VENV_NAMES = [".venv", "venv", "myenv"]
DEFAULT_VENV_NAME = ".venv"

# --- HELPER EKSEKUSI CMD ---

def run_cmd(command, cwd=None, env=None, capture=False):
    """Helper untuk menjalankan subprocess."""
    try:
        process = subprocess.run(
            command,
            shell=True,
            check=True,
            cwd=cwd,
            env=env,
            text=True,
            capture_output=capture,
            encoding='utf-8'
        )
        if capture:
            return process.stdout.strip(), process.stderr.strip()
        return True
    except subprocess.CalledProcessError as e:
        print(f"  ❌ ERROR executing: {command}")
        if capture:
            return None, str(e)
        print(f"     {e.stderr or e.stdout or e}")
        return False
    except Exception as e:
        print(f"  ❌ FATAL executing: {command}")
        print(f"     {e}")
        return False

# --- LOGIKA PROXYSYNC (DISTRIBUSI) ---

def load_proxies(file_path):
    """Memuat daftar proxy utama."""
    if not Path(file_path).exists():
        print(f"  [Proxy] WARNING: File proxy master '{file_path}' tidak ditemukan.")
        return []
    with open(file_path, "r") as f:
        proxies = [line.strip() for line in f if line.strip()]
    print(f"  [Proxy] Berhasil memuat {len(proxies)} proxy dari master list.")
    return proxies

def load_paths(file_path):
    """Memuat semua path target distribusi."""
    if not Path(file_path).exists():
        print(f"  [Proxy] WARNING: File path '{file_path}' tidak ditemukan.")
        return []
    with open(file_path, "r") as f:
        # Pastikan path adalah absolut di dalam Codespace
        paths = [
            str(Path(WORKDIR) / line.strip()) 
            for line in f if line.strip() and not line.startswith("#")
        ]
    print(f"  [Proxy] Berhasil memuat {len(paths)} path distribusi.")
    return paths

def distribute_proxies(proxies, paths):
    """
    Distribusi proxy (diadaptasi dari proxysync/main.py).
    """
    if not proxies or not paths:
        print("  [Proxy] Tidak ada proxy atau path, distribusi dilewati.")
        return
        
    print(f"  [Proxy] Mendistribusikan {len(proxies)} proxy ke {len(paths)} path...")
    for path_str in paths:
        path = Path(path_str)
        if not path.is_dir():
            print(f"    [Proxy] 🟡 Lewati: Path tidak valid atau bukan direktori: {path_str}")
            continue
        
        # Logika dari ProxySync: cari proxies.txt atau proxy.txt
        proxy_file_path = path / "proxies.txt"
        if not proxy_file_path.exists():
            proxy_file_path = path / "proxy.txt"
            
        # Acak proxy untuk setiap path (penting!)
        proxies_shuffled = random.sample(proxies, len(proxies))
        try:
            with open(proxy_file_path, "w") as f:
                for proxy in proxies_shuffled:
                    f.write(proxy + "\n")
            print(f"    [Proxy] 🟢 Berhasil menulis ke {proxy_file_path}")
        except IOError as e:
            print(f"    [Proxy] 🔴 Gagal menulis ke {proxy_file_path}: {e}")

# --- LOGIKA BOT RUNNER (VENV & DEPS) ---

def get_active_venv_path(bot_path_str):
    """Mendeteksi venv yang ada (diadaptasi dari BotRunner.cs)."""
    bot_path = Path(bot_path_str)
    for name in POSSIBLE_VENV_NAMES:
        venv_path = bot_path / name
        if venv_path.is_dir():
            return str(venv_path)
    return None

def get_venv_executable(venv_path, executable_name):
    """Mencari executable (python/pip) di dalam venv."""
    venv_path = Path(venv_path)
    # Cek 'bin' (Linux/macOS) dan 'Scripts' (Windows)
    for scripts_dir in ["bin", "Scripts"]:
        exe_path = venv_path / scripts_dir / executable_name
        if exe_path.exists():
            return str(exe_path)
        # Cek juga .exe untuk Windows
        exe_path_win = venv_path / scripts_dir / f"{executable_name}.exe"
        if exe_path_win.exists():
            return str(exe_path_win)
    return None

def install_dependencies(bot_path, bot_type):
    """
    Menginstal dependensi pip atau npm (diadaptasi dari BotRunner.cs).
    """
    bot_name = Path(bot_path).name
    print(f"    [Deps] Memeriksa dependensi for '{bot_name}'...")
    
    if bot_type == "python":
        req_file = Path(bot_path) / "requirements.txt"
        if not req_file.exists():
            print("    [Deps] requirements.txt tidak ditemukan, dilewati.")
            return

        venv_path = get_active_venv_path(bot_path)
        pip_exe = "pip"
        
        if not venv_path:
            print(f"    [Deps] Venv tidak ditemukan. Membuat '{DEFAULT_VENV_NAME}' baru...")
            venv_path = str(Path(bot_path) / DEFAULT_VENV_NAME)
            run_cmd(f"python3 -m venv \"{venv_path}\"", cwd=bot_path)
        
        pip_exe = get_venv_executable(venv_path, "pip") or "pip"
        
        print(f"    [Deps] Menjalankan 'pip install' menggunakan '{pip_exe}'...")
        run_cmd(f"\"{pip_exe}\" install --no-cache-dir -r requirements.txt", cwd=bot_path)

    elif bot_type == "javascript":
        pkg_file = Path(bot_path) / "package.json"
        if not pkg_file.exists():
            print("    [Deps] package.json tidak ditemukan, dilewati.")
            return
            
        print("    [Deps] Menjalankan 'npm install'...")
        run_cmd("npm install --silent --no-progress", cwd=bot_path)

def get_run_command(bot_path, bot_type):
    """
    Mencari perintah eksekusi (diadaptasi dari BotRunner.cs).
    """
    bot_path = Path(bot_path)
    
    if bot_type == "python":
        venv_path = get_active_venv_path(str(bot_path))
        python_exe = "python3" # Default global
        
        if venv_path:
            python_exe = get_venv_executable(venv_path, "python") or get_venv_executable(venv_path, "python3") or "python3"
            if python_exe == "python3":
                print(f"    [Run] 🟡 Venv ditemukan, tapi 'python' tidak. Fallback ke global.")
            else:
                 print(f"    [Run] 🟢 Venv 'python' ditemukan: {python_exe}")
        
        # Cek file entry point
        for entry in ["run.py", "main.py", "bot.py"]:
            if (bot_path / entry).exists():
                return f"\"{python_exe}\"", entry
        print(f"    [Run] 🔴 Gagal menemukan entry point (run.py/main.py) untuk {bot_path.name}")
        return None, None

    elif bot_type == "javascript":
        # Cek package.json "start" script
        pkg_file = bot_path / "package.json"
        if pkg_file.exists():
            try:
                with open(pkg_file, "r") as f:
                    pkg_data = json.load(f)
                    if "scripts" in pkg_data and "start" in pkg_data["scripts"]:
                        print("    [Run] 🟢 Ditemukan 'npm start'.")
                        return "npm", "start"
            except Exception as e:
                print(f"    [Run] 🟡 Gagal baca package.json: {e}")
        
        # Cek file entry point
        for entry in ["index.js", "main.js", "bot.js"]:
            if (bot_path / entry).exists():
                return "node", entry
        print(f"    [Run] 🔴 Gagal menemukan entry point (npm start/index.js) untuk {bot_path.name}")
        return None, None

    return None, None

# --- LOGIKA BOT UPDATER & TMUX ---

def sync_bot_repo(name, path_str, repo_url):
    """
    Melakukan 'git clone' atau 'git pull' (diadaptasi dari BotUpdater.cs).
    """
    print(f"\n--- Memproses Bot: {name} ---")
    bot_path = Path(path_str)
    
    # Pastikan path ada di dalam WORKDIR
    if WORKDIR not in str(bot_path.resolve()):
        print(f"  🔴 FATAL: Path '{path_str}' berbahaya (di luar {WORKDIR}). Dilewati.")
        return False, None

    # Pastikan parent directory ada
    bot_path.parent.mkdir(parents=True, exist_ok=True)
    
    git_dir = bot_path / ".git"
    
    if git_dir.is_dir():
        print(f"  [Sync] Path ditemukan. Menjalankan 'git pull' di {path_str}...")
        run_cmd("git pull --rebase", cwd=str(bot_path))
    else:
        print(f"  [Sync] Path tidak ditemukan. Menjalankan 'git clone' ke {path_str}...")
        run_cmd(f"git clone --depth 1 \"{repo_url}\" \"{path_str}\"")
    
    return bot_path.is_dir(), str(bot_path)

def launch_in_tmux(name, bot_path, executor, args):
    """
    Meluncurkan bot di window tmux baru (diadaptasi dari TmuxRunner.cs).
    """
    print(f"    [Tmux] Meluncurkan '{name}' di tmux...")
    cmd = f"tmux new-window -t {TMUX_SESSION} -n \"{name}\" -c \"{bot_path}\" \"{executor} {args}\""
    run_cmd(cmd)

# --- FUNGSI UTAMA (MAIN) ---

def main():
    os.chdir(WORKDIR)
    print("==========================================")
    print("  AUTOMATION-HUB DEPLOYER SCRIPT (REMOTE)")
    print("==========================================")

    # 1. Self-Healing: Bunuh sesi tmux lama
    print("\n[1/4] Membersihkan sesi tmux lama...")
    run_cmd(f"tmux kill-session -t {TMUX_SESSION}", capture=True) # Abaikan error jika tidak ada

    # 2. Distribusi Proxy (Logika ProxySync)
    print("\n[2/4] Mendistribusikan proxy (Logika ProxySync)...")
    master_proxies = load_proxies(MASTER_PROXY_FILE)
    target_paths = load_paths(PATHS_FILE)
    distribute_proxies(master_proxies, target_paths)

    # 3. Memuat Konfigurasi Bot
    print(f"\n[3/4] Memuat konfigurasi bot dari {CONFIG_FILE}...")
    try:
        with open(CONFIG_FILE, "r") as f:
            config_data = json.load(f)
        bots = config_data.get("bots_and_tools", [])
        print(f"       Ditemukan {len(bots)} entri.")
    except Exception as e:
        print(f"  🔴 FATAL: Gagal memuat atau parse {CONFIG_FILE}: {e}")
        sys.exit(1)

    # 4. Buat Sesi Tmux & Luncurkan Bot
    print(f"\n[4/4] Membuat sesi tmux '{TMUX_SESSION}' dan meluncurkan bot...")
    # Buat sesi dengan window pertama (dummy)
    run_cmd(f"tmux new-session -d -s {TMUX_SESSION} -n 'dashboard'")
    
    for bot in bots:
        name = bot.get("name")
        path_str = bot.get("path")
        repo_url = bot.get("repo_url")
        enabled = bot.get("enabled", False)
        bot_type = bot.get("type")

        if not all([name, path_str, repo_url, bot_type]):
            print(f"🟡 LEWATI: Entri tidak valid (kurang name, path, repo_url, atau type).")
            continue
            
        if not enabled:
            print(f"🔵 LEWATI: '{name}' tidak diaktifkan (disabled).")
            continue
            
        # Jangan jalankan proxysync lagi
        if name == "ProxySync-Tool":
            print(f"🔵 LEWATI: '{name}' (sudah dieksekusi).")
            continue

        # Tahap 1: Sync (Clone/Pull)
        success, bot_path = sync_bot_repo(name, path_str, repo_url)
        if not success:
            continue
            
        # Tahap 2: Instal Dependensi
        install_dependencies(bot_path, bot_type)
        
        # Tahap 3: Cari Perintah Run
        executor, args = get_run_command(bot_path, bot_type)
        if not executor:
            print(f"  🔴 Gagal meluncurkan '{name}': Tidak ada perintah eksekusi.")
            continue
            
        # Tahap 4: Luncurkan di Tmux
        launch_in_tmux(name, bot_path, executor, args)
        print(f"  🟢 SUKSES: '{name}' diluncurkan.")

    print("\n==========================================")
    print("  DEPLOYER SELESAI.")
    print(f"  Gunakan 'tmux a -t {TMUX_SESSION}' untuk memantau.")
    print("==========================================")

if __name__ == "__main__":
    main()
