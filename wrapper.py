#!/usr/bin/env python3
import pexpect
import sys
import json
import os
import time
import subprocess

# --- LOKASI LOG ---
LOG_DIR = os.path.join(os.path.dirname(__file__), 'logs')
os.makedirs(LOG_DIR, exist_ok=True) 

# --- KODE KEYBOARD ---
KEY_DOWN = '\x1B[B'
KEY_UP = '\x1B[A'
KEY_ENTER = '\r'

def get_bot_config(bot_name):
    """Membaca bots_config.json dan mencari config untuk bot ini."""
    config_path = os.path.join(os.path.dirname(__file__), 'bots_config.json')
    try:
        with open(config_path, 'r') as f:
            all_bots = json.load(f) # Ini JSON array [ ... ]
        
        for bot in all_bots:
            if bot.get('name') == bot_name:
                return bot
        return None
    except Exception as e:
        print(f"Error baca JSON: {e}")
        return None

def run_command_silently(cmd_list):
    """Helper buat ngejalanin 'pip install' / 'npm install'"""
    try:
        print(f"--- Wrapper: Menjalankan setup: {' '.join(cmd_list)}")
        subprocess.run(cmd_list, check=True, capture_output=True, text=True, timeout=300) # Timeout 5 menit
        return True
    except Exception as e:
        print(f"FATAL: Command setup gagal: {' '.join(cmd_list)}")
        if isinstance(e, FileNotFoundError):
            print(f"Error Detail: {e}")
        elif hasattr(e, 'stderr') and e.stderr:
            print(f"Error STDERR: {e.stderr}")
        elif hasattr(e, 'stdout') and e.stdout:
            print(f"Error STDOUT: {e.stdout}")
        else:
            print(f"Error Tipe: {type(e).__name__} - {e}")
        return False

def run_wrapper(bot_name):
    """Fungsi 'REPLAY' utama. Sekarang pake logic 'DevOps'."""
    
    print(f"--- Wrapper: Mencari config untuk '{bot_name}' ---")
    config = get_bot_config(bot_name)
    if not config:
        print(f"FATAL: Config untuk '{bot_name}' tidak ditemukan di JSON.")
        return

    bot_type = config.get('type')
    entrypoint = config.get('entrypoint') or ""
    
    command_to_spawn = [] 

    # --- SETUP & GET COMMAND (Python) ---
    if bot_type == 'python':
        # ==============================================================
        # INI FIX 'bin' vs 'Scripts' (v1.6)
        # ==============================================================
        venv_dir = None
        venv_type = None # 'Scripts' atau 'bin'
        
        # 1. Cek 'venv' (Windows dulu, baru Linux)
        if os.path.isdir('venv/Scripts'):
            venv_dir = 'venv'
            venv_type = 'Scripts'
        elif os.path.isdir('venv/bin'):
            venv_dir = 'venv'
            venv_type = 'bin'
        # 2. Cek 'myenv' (Windows dulu, baru Linux)
        elif os.path.isdir('myenv/Scripts'):
            venv_dir = 'myenv'
            venv_type = 'Scripts'
        elif os.path.isdir('myenv/bin'):
            venv_dir = 'myenv'
            venv_type = 'bin'

        # 3. Venv Gak Ada? BIKIN (versi Linux/WSL).
        if not venv_dir:
            print(f"--- Wrapper: Venv ('venv'/'myenv') gak nemu, bikin 'venv' baru (versi WSL)...")
            if not run_command_silently(["python3", "-m", "venv", "venv"]):
                return 
            venv_dir = 'venv'
            venv_type = 'bin' # Karena kita bikin dari WSL, pasti 'bin'
        
        print(f"--- Wrapper: Menggunakan Venv: '{venv_dir}' (Tipe: '{venv_type}') ---")
        
        # 4. Install dependencies (pake path yg bener)
        if os.path.isfile('requirements.txt'):
            pip_path = f"./{venv_dir}/{venv_type}/pip3" # Coba 'pip3' dulu
            if not os.path.isfile(pip_path):
                pip_path = f"./{venv_dir}/{venv_type}/pip" # Fallback ke 'pip'
            
            # Kalo di Windows (Scripts), namanya 'pip.exe'
            if venv_type == 'Scripts' and not os.path.isfile(pip_path):
                pip_path = f"./{venv_dir}/{venv_type}/pip.exe" 
            
            if not os.path.isfile(pip_path):
                print(f"FATAL: Gak nemu 'pip'/'pip3'/'pip.exe' di dalem '{venv_dir}/{venv_type}/'")
                return # Gagal

            print(f"--- Wrapper: 'requirements.txt' nemu, menjalankan '{pip_path} install'...")
            if not run_command_silently([pip_path, "install", "-r", "requirements.txt"]):
                return 
            print(f"--- Wrapper: 'pip install' selesai. ---")
        
        # 5. Bikin command 'spawn' terakhir (pake path yg bener)
        py_script = entrypoint if entrypoint else "main.py"
        
        # BASH (WSL) gak bisa 'source' file Windows ('activate'), jadi kita panggil python-nya LANGSUNG
        python_path = f"./{venv_dir}/{venv_type}/python3" # Coba 'python3'
        if not os.path.isfile(python_path):
            python_path = f"./{venv_dir}/{venv_type}/python" # Fallback 'python'
        
        if venv_type == 'Scripts' and not os.path.isfile(python_path):
            python_path = f"./{venv_dir}/{venv_type}/python.exe" # Fallback 'python.exe'

        if not os.path.isfile(python_path):
            print(f"FATAL: Gak nemu 'python'/'python3'/'python.exe' di dalem '{venv_dir}/{venv_type}/'")
            return # Gagal

        command_to_spawn = [python_path, py_script]

    # --- SETUP & GET COMMAND (Javascript) ---
    elif bot_type == 'javascript':
        if os.path.isfile('package.json') and not os.path.isdir('node_modules'):
            print("--- Wrapper: 'package.json' nemu tapi 'node_modules' gak ada. Menjalankan 'npm install'...")
            if not run_command_silently(["npm", "install"]):
                return
            print(f"--- Wrapper: 'npm install' selesai. ---")

        if entrypoint:
            command_to_spawn = ["node", entrypoint]
        else:
            command_to_spawn = ["npm", "start"]
    
    else:
        print(f"FATAL: Tipe bot tidak dikenal: {bot_type}")
        return
        
    # ======================================================
    # PEXPECT SPAWN (Sekarang udah bersih)
    # ======================================================
    print(f"--- Wrapper: Setup selesai. Menjalankan: {' '.join(command_to_spawn)} ---")
    log_file_path = os.path.join(LOG_DIR, f"{bot_name}.log")
    log_file_handle = open(log_file_path, 'wb') 
    
    child = pexpect.spawn(command_to_spawn[0], command_to_spawn[1:], timeout=30) 
    child.logfile = log_file_handle

    try:
        # ==============================================================
        # INI ADALAH "RECORDING" LU (PR LU)
        # ==============================================================
        if bot_name == "NitroAutoBot-NTE":
            child.expect(b"Do You Want Use Proxy? (y/n):")
            child.sendline("n")

        # ... (Tambahin bot lain) ...

        else:
            print(f"--- Wrapper: '{bot_name}' tidak punya script replay, jalan normal. ---")
            pass 

    except pexpect.TIMEOUT:
        print(f"FATAL TIMEOUT: Bot '{bot_name}' gak nampilin TUI yang diharapkan.")
    except pexpect.EOF:
        print(f"FATAL EOF: Bot '{bot_name}' CRASH INSTAN.")
        print("--- OUTPUT TERAKHIR BOT ---")
        try:
            print(child.before.decode('utf-8', errors='ignore'))
        except Exception:
            pass 
        
    finally:
        print(f"--- Wrapper: Setup '{bot_name}' selesai. Menyerahkan kontrol... ---")
        child.interact()
        log_file_handle.close()
        print(f"--- Wrapper: Bot '{bot_name}' mati. Log ditutup. ---")

# --- Main ---
if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python3 wrapper.py \"<Nama Bot>\"")
        sys.exit(1)
        
    bot_name_arg = sys.argv[1]
    run_wrapper(bot_name_arg)