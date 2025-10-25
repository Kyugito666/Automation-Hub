#!/usr/bin/env python3
"""
deploy_bots.py - Remote Environment Orchestrator
Fitur: Self-healing, Proxy distribution, Git sync, Dependency install, Tmux launch
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
MASTER_PROXY_FILE = "config/proxy.txt"
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

def load_proxies(file_path):
    """Memuat proxy master list."""
    if not Path(file_path).exists():
        print(f"  [Proxy] ⚠️  '{file_path}' tidak ditemukan.")
        return []
    with open(file_path, "r") as f:
        proxies = [line.strip() for line in f if line.strip() and not line.startswith("#")]
    print(f"  [Proxy] ✅ {len(proxies)} proxy dimuat.")
    return proxies

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
            print(f"    🟡 Lewati: {path_str}")
            continue
        
        proxy_file = path / "proxies.txt"
        if not proxy_file.exists():
            proxy_file = path / "proxy.txt"
            
        proxies_shuffled = random.sample(proxies, len(proxies))
        try:
            with open(proxy_file, "w") as f:
                for proxy in proxies_shuffled:
                    f.write(proxy + "\n")
            print(f"    🟢 {proxy_file}")
        except IOError as e:
            print(f"    🔴 Gagal: {proxy_file} → {e}")

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

def install_dependencies(bot_path, bot_type):
    """Install dependencies (pip/npm)."""
    bot_name = Path(bot_path).name
    print(f"    [Deps] 🔧 Cek dependensi '{bot_name}'...")
    
    if bot_type == "python":
        req_file = Path(bot_path) / "requirements.txt"
        if not req_file.exists():
            print("    [Deps] ⏭️  requirements.txt tidak ada.")
            return

        venv_path = get_active_venv_path(bot_path)
        
        if not venv_path:
            print(f"    [Deps] 🆕 Buat venv '{DEFAULT_VENV_NAME}'...")
            venv_path = str(Path(bot_path) / DEFAULT_VENV_NAME)
            run_cmd(f"python3 -m venv \"{venv_path}\"", cwd=bot_path)
        
        pip_exe = get_venv_executable(venv_path, "pip") or "pip"
        print(f"    [Deps] 📦 pip install...")
        run_cmd(f"\"{pip_exe}\" install --no-cache-dir -r requirements.txt", cwd=bot_path)

    elif bot_type == "javascript":
        pkg_file = Path(bot_path) / "package.json"
        if not pkg_file.exists():
            print("    [Deps] ⏭️  package.json tidak ada.")
            return
            
        print("    [Deps] 📦 npm install...")
        run_cmd("npm install --silent --no-progress", cwd=bot_path)

def get_run_command(bot_path, bot_type):
    """Deteksi perintah eksekusi."""
    bot_path = Path(bot_path)
    
    if bot_type == "python":
        venv_path = get_active_venv_path(str(bot_path))
        python_exe = "python3"
        
        if venv_path:
            python_exe = get_venv_executable(venv_path, "python") or get_venv_executable(venv_path, "python3") or "python3"
            if python_exe == "python3":
                print(f"    [Run] 🟡 Venv ada tapi python tidak, fallback global.")
            else:
                print(f"    [Run] 🟢 Venv python: {python_exe}")
        
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
        run_cmd("git pull --rebase", cwd=str(bot_path))
    else:
        print(f"  [Sync] 📥 git clone...")
        run_cmd(f"git clone --depth 1 \"{repo_url}\" \"{path_str}\"")
    
    return bot_path.is_dir(), str(bot_path)

# --- TMUX LAUNCHER ---

def launch_in_tmux(name, bot_path, executor, args):
    """Launch bot di tmux window."""
    print(f"    [Tmux] 🚀 Launch '{name}'...")
    cmd = f"tmux new-window -t {TMUX_SESSION} -n \"{name}\" -c \"{bot_path}\" \"{executor} {args}\""
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
    master_proxies = load_proxies(MASTER_PROXY_FILE)
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
            
        # Install Deps
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
    print(f"  💡 'tmux a -t {TMUX_SESSION}' untuk monitor")
    print("=" * 50)

if __name__ == "__main__":
    main()
