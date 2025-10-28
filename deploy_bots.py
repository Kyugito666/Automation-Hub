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
from pathlib import Path

# --- KONSTANTA ---
CONFIG_FILE = "config/bots_config.json"
PATHS_FILE = "config/paths.txt"

# === PERUBAHAN: Gunakan proxy yang sudah lolos tes ===
MASTER_PROXY_FILE = "proxysync/success_proxy.txt"
FALLBACK_PROXY_FILE = "config/proxy.txt" # Jika success_proxy.txt gagal
# === AKHIR PERUBAHAN ===

TMUX_SESSION = "automation_hub_bots"
WORKDIR = "/workspaces/automation-hub"

POSSIBLE_VENV_NAMES = [".venv", "venv", "myenv"]
DEFAULT_VENV_NAME = ".venv"

# --- HELPER EKSEKUSI ---

def run_cmd(command, cwd=None, env=None, capture=False):
    """Helper untuk subprocess dengan error handling."""
    try:
        process = subprocess.run(
            command, shell=True, check=True, cwd=cwd, env=env,
            text=True, capture_output=capture, encoding='utf-8'
        )
        return (process.stdout.strip(), process.stderr.strip()) if capture else True
    except subprocess.CalledProcessError as e:
        print(f"  ❌ ERROR: {command}")
        if capture:
            return None, str(e)
        print(f"     {e.stderr or e.stdout or e}")
        return False
    except Exception as e:
        print(f"  ❌ FATAL: {command}")
        print(f"     {e}")
        return False

# --- PROXY DISTRIBUSI ---

# === PERUBAHAN: Logika load proxy baru ===
def load_proxies():
    """Memuat proxy master list (prioritaskan success_proxy.txt)."""
    
    # Prioritas 1: success_proxy.txt
    if Path(MASTER_PROXY_FILE).exists() and Path(MASTER_PROXY_FILE).stat().st_size > 0:
        file_to_load = MASTER_PROXY_FILE
    # Prioritas 2: fallback (jika success N/A tapi proxy.txt lama ada)
    elif Path(FALLBACK_PROXY_FILE).exists() and Path(FALLBACK_PROXY_FILE).stat().st_size > 0:
        file_to_load = FALLBACK_PROXY_FILE
        print(f"  [Proxy] ⚠️  '{MASTER_PROXY_FILE}' tidak ada/kosong. Pakai fallback '{FALLBACK_PROXY_FILE}'.")
    else:
        print(f"  [Proxy] ⚠️  Tidak ada file proxy ditemukan ('{MASTER_PROXY_FILE}' atau '{FALLBACK_PROXY_FILE}').")
        return []

    try:
        with open(file_to_load, "r") as f:
            proxies = [line.strip() for line in f if line.strip() and not line.startswith("#")]
        print(f"  [Proxy] ✅ {len(proxies)} proxy dimuat dari '{file_to_load}'.")
        return proxies
    except Exception as e:
        print(f"  [Proxy] 🔴 Gagal baca '{file_to_load}': {e}")
        return []
# === AKHIR PERUBAHAN ===

def load_paths(file_path):
    """Memuat daftar path target distribusi."""
    if not Path(file_path).exists():
        print(f"  [Proxy] ⚠️  '{file_path}' tidak ditemukan.")
        return []
    with open(file_path, "r") as f:
        paths = [
            str(Path(WORKDIR) / line.strip()) 
            for line in f if line.strip() and not line.startswith("#")
        ]
    print(f"  [Proxy] ✅ {len(paths)} path dimuat.")
    return paths

def distribute_proxies(proxies, paths):
    """Distribusi proxy ke semua path (acak)."""
    if not proxies or not paths:
        print("  [Proxy] ⏭️  Tidak ada data, distribusi dilewati.")
        return
        
    print(f"  [Proxy] 📤 Distribusi {len(proxies)} proxy → {len(paths)} path...")
    for path_str in paths:
        path = Path(path_str)
        if not path.is_dir():
            print(f"    🟡 Lewati (path invalid): {path_str}")
            continue
        
        # Tentukan file proxy target (proxies.txt atau proxy.txt)
        proxy_file = path / "proxies.txt"
        target_filename = "proxies.txt"
        if not proxy_file.exists():
            # Cek jika targetnya pakai 'proxy.txt'
            if (path / "proxy.txt").exists():
                 proxy_file = path / "proxy.txt"
                 target_filename = "proxy.txt"
            # Jika tidak ada keduanya, default buat 'proxies.txt'
            
        proxies_shuffled = random.sample(proxies, len(proxies))
        try:
            with open(proxy_file, "w") as f:
                for proxy in proxies_shuffled:
                    f.write(proxy + "\n")
            print(f"    🟢 {path.name}/{target_filename}")
        except IOError as e:
            print(f"    🔴 Gagal: {path.name}/{target_filename} → {e}")

# --- VENV & DEPS ---

def get_active_venv_path(bot_path_str):
    """Deteksi venv yang ada."""
    bot_path = Path(bot_path_str)
    for name in POSSIBLE_VENV_NAMES:
        venv_path = bot_path / name
        if venv_path.is_dir():
            return str(venv_path)
    return None

def get_venv_executable(venv_path, exe_name):
    """Cari executable di venv."""
    venv_path = Path(venv_path)
    for scripts_dir in ["bin", "Scripts"]:
        exe_path = venv_path / scripts_dir / exe_name
        if exe_path.exists():
            return str(exe_path)
        exe_path_win = venv_path / scripts_dir / f"{exe_name}.exe"
        if exe_path_win.exists():
            return str(exe_path_win)
    return None

# === PERUBAHAN: Smart Install ===
def install_dependencies(bot_path, bot_type):
    """Install dependencies (pip/npm) HANYA JIKA DIPERLUKAN."""
    bot_name = Path(bot_path).name
    print(f"    [Deps] 🔧 Cek dependensi '{bot_name}'...")
    
    if bot_type == "python":
        req_file = Path(bot_path) / "requirements.txt"
        if not req_file.exists():
            print("    [Deps] ⏭️  requirements.txt tidak ada. Skip.")
            return

        venv_path = get_active_venv_path(bot_path)
        
        if not venv_path:
            print(f"    [Deps] 🆕 Venv tidak ditemukan. Buat '{DEFAULT_VENV_NAME}'...")
            venv_path = str(Path(bot_path) / DEFAULT_VENV_NAME)
            run_cmd(f"python3 -m venv \"{venv_path}\"", cwd=bot_path)
            
            # Karena venv baru, kita WAJIB install
            pip_exe = get_venv_executable(venv_path, "pip") or "pip"
            print(f"    [Deps] 📦 pip install (Run Pertama)...")
            run_cmd(f"\"{pip_exe}\" install --no-cache-dir -r requirements.txt", cwd=bot_path)
        else:
            print(f"    [Deps] ✅ Venv ditemukan di '{Path(venv_path).name}'. Skip install.")
            # Opsi: jalankan 'pip install' jika requirements.txt lebih baru dari venv?
            # Untuk saat ini, kita skip demi kecepatan.

    elif bot_type == "javascript":
        pkg_file = Path(bot_path) / "package.json"
        if not pkg_file.exists():
            print("    [Deps] ⏭️  package.json tidak ada. Skip.")
            return
            
        node_modules_dir = Path(bot_path) / "node_modules"
        if not node_modules_dir.is_dir():
            print(f"    [Deps] 🆕 node_modules tidak ditemukan. Menjalankan 'npm install'...")
            run_cmd("npm install --silent --no-progress", cwd=bot_path)
        else:
            print(f"    [Deps] ✅ node_modules ditemukan. Skip install.")
# === AKHIR PERUBAHAN ===

def get_run_command(bot_path, bot_type):
    """Deteksi perintah eksekusi."""
    bot_path = Path(bot_path)
    
    if bot_type == "python":
        venv_path = get_active_venv_path(str(bot_path))
        python_exe = "python3"
        
        if venv_path:
            python_exe_in_venv = get_venv_executable(venv_path, "python") or get_venv_executable(venv_path, "python3")
            if python_exe_in_venv:
                python_exe = python_exe_in_venv
                print(f"    [Run] 🟢 Venv python: {python_exe}")
            else:
                print(f"    [Run] 🟡 Venv ada tapi python tidak, fallback global.")
        
        for entry in ["run.py", "main.py", "bot.py"]:
            if (bot_path / entry).exists():
                return f"\"{python_exe}\"", entry
        print(f"    [Run] 🔴 Entry point tidak ditemukan.")
        return None, None

    elif bot_type == "javascript":
        pkg_file = bot_path / "package.json"
        if pkg_file.exists():
            try:
                with open(pkg_file, "r") as f:
                    pkg_data = json.load(f)
                    if "scripts" in pkg_data and "start" in pkg_data["scripts"]:
                        print("    [Run] 🟢 npm start.")
                        return "npm", "start"
            except Exception as e:
                print(f"    [Run] 🟡 Gagal baca package.json: {e}")
        
        for entry in ["index.js", "main.js", "bot.js"]:
            if (bot_path / entry).exists():
                return "node", entry
        print(f"    [Run] 🔴 Entry point tidak ditemukan.")
        return None, None

    return None, None

# --- GIT SYNC ---

def sync_bot_repo(name, path_str, repo_url):
    """Clone atau pull repository."""
    print(f"\n--- 🤖 {name} ---")
    bot_path = Path(path_str)
    
    if WORKDIR not in str(bot_path.resolve()):
        print(f"  🔴 Path berbahaya: {path_str}")
        return False, None

    bot_path.parent.mkdir(parents=True, exist_ok=True)
    git_dir = bot_path / ".git"
    
    if git_dir.is_dir():
        print(f"  [Sync] 🔄 git pull...")
        # Coba reset dulu jika ada file lokal (seperti config.json)
        # Tapi .gitignore bot harusnya handle ini. Kita coba pull standar.
        run_cmd("git pull --rebase", cwd=str(bot_path))
    else:
        print(f"  [Sync] 📥 git clone...")
        run_cmd(f"git clone --depth 1 \"{repo_url}\" \"{path_str}\"")
    
    return bot_path.is_dir(), str(bot_path)

# --- TMUX LAUNCHER ---

def launch_in_tmux(name, bot_path, executor, args):
    """Launch bot di tmux window."""
    print(f"    [Tmux] 🚀 Launch '{name}'...")
    # Pastikan nama tidak mengandung karakter aneh untuk tmux
    safe_name = re.sub(r'[^a-zA-Z0-9_-]', '', name)
    cmd = f"tmux new-window -t {TMUX_SESSION} -n \"{safe_name}\" -c \"{bot_path}\" \"{executor} {args}\""
    run_cmd(cmd)

# --- MAIN ---

def main():
    os.chdir(WORKDIR)
    print("=" * 50)
    print("  AUTOMATION-HUB DEPLOYER (REMOTE)")
    print("=" * 50)

    # 1. Self-Healing: Kill tmux lama
    print("\n[1/4] 🧹 Bersihkan tmux lama...")
    run_cmd(f"tmux kill-session -t {TMUX_SESSION}", capture=True)

    # 2. Distribusi Proxy
    print("\n[2/4] 📡 Distribusi Proxy...")
    # === PERUBAHAN: Panggil load_proxies() tanpa arg ===
    master_proxies = load_proxies() 
    target_paths = load_paths(PATHS_FILE)
    distribute_proxies(master_proxies, target_paths)

    # 3. Load Config
    print(f"\n[3/4] 📋 Load config: {CONFIG_FILE}...")
    try:
        with open(CONFIG_FILE, "r") as f:
            config_data = json.load(f)
        bots = config_data.get("bots_and_tools", [])
        print(f"       ✅ {len(bots)} entri.")
    except Exception as e:
        print(f"  🔴 FATAL: {e}")
        sys.exit(1)

    # 4. Buat Tmux & Launch Bot
    print(f"\n[4/4] 🎬 Buat tmux '{TMUX_SESSION}'...")
    run_cmd(f"tmux new-session -d -s {TMUX_SESSION} -n 'dashboard'")
    
    for bot in bots:
        name = bot.get("name")
        path_str = bot.get("path")
        repo_url = bot.get("repo_url")
        enabled = bot.get("enabled", False)
        bot_type = bot.get("type")

        if not all([name, path_str, repo_url, bot_type]):
            print(f"🟡 Entri tidak valid, skip.")
            continue
            
        if not enabled:
            print(f"🔵 '{name}' disabled, skip.")
            continue
            
        if name == "ProxySync-Tool":
            print(f"🔵 '{name}' sudah dieksekusi, skip.")
            continue

        # Sync
        success, bot_path = sync_bot_repo(name, path_str, repo_url)
        if not success:
            continue
            
        # Install Deps (Sekarang 'Smart')
        install_dependencies(bot_path, bot_type)
        
        # Get Run Command
        executor, args = get_run_command(bot_path, bot_type)
        if not executor:
            print(f"  🔴 Gagal launch '{name}': No command.")
            continue
            
        # Launch di Tmux
        launch_in_tmux(name, bot_path, executor, args)
        print(f"  🟢 SUKSES: '{name}' diluncurkan.")

    print("\n" + "=" * 50)
    print("  ✅ DEPLOYER SELESAI")
    print(f"  💡 'tmux a -t {TMUX_SESSION}' atau Menu 4 TUI untuk monitor")
    print("=" * 50)

if __name__ == "__main__":
    main()
