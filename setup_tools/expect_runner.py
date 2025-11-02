import os
import sys
import json
import subprocess
import time
import pexpect

# --- Konfigurasi ---
# File untuk menyimpan rekaman input/output.
EXPECT_CONFIG_FILE = "autostart.json"

def run_expect_replay(bot_path, bot_exec_command):
    """
    Menjalankan bot dengan mereplay interaksi yang tersimpan.
    """
    bot_name = os.path.basename(bot_path)
    print(f"[{bot_name}] 🔄 Starting automated setup (Replay Mode)...", flush=True)

    config_path = os.path.join(bot_path, EXPECT_CONFIG_FILE)
    if not os.path.exists(config_path):
        print(f"[{bot_name}] ❌ ERROR: Expect config not found: {EXPECT_CONFIG_FILE}", flush=True)
        return False

    try:
        with open(config_path, 'r') as f:
            expect_script = json.load(f)
    except Exception as e:
        print(f"[{bot_name}] ❌ ERROR: Failed to load config: {e}", flush=True)
        return False

    try:
        # Kita jalankan bot-nya, dan pexpect akan mengontrol I/O-nya.
        # Catatan: Kita tidak menggunakan shell=True di spawn karena deployer sudah menyediakan
        # perintah eksekusi penuh (misal: node index.js atau python3 run.py).
        child = pexpect.spawn(bot_exec_command, cwd=bot_path, timeout=None)
        
        # Logika Replay
        for step in expect_script:
            expected_prompt = step.get('expect')
            send_value = step.get('send')

            if not expected_prompt or not send_value:
                 print(f"[{bot_name}] ⚠️  Skip invalid step: {step}", flush=True)
                 continue

            try:
                # Menunggu prompt yang diharapkan (dengan timeout)
                child.expect(expected_prompt, timeout=120) 
                
                # Mengirim nilai input. sendline otomatis menambahkan newline.
                child.sendline(send_value.strip())
                
                print(f"[{bot_name}] ✅ REPLAY: Expected '{expected_prompt[:40]}...', Sent: '{send_value.strip()}'", flush=True)

            except pexpect.exceptions.EOF:
                print(f"[{bot_name}] ⚠️  WARNING: Bot finished unexpectedly (EOF).", flush=True)
                break
            except pexpect.exceptions.TIMEOUT:
                print(f"[{bot_name}] ❌ ERROR: Timeout waiting for prompt: '{expected_prompt[:40]}...' ", flush=True)
                return False

        print(f"[{bot_name}] ✅ Automated setup complete. Bot is now expected to be running in Tmux.", flush=True)
        
        child.close()
        return True

    except Exception as e:
        print(f"[{bot_name}] ❌ FATAL Expect Runner Error: {e}", file=sys.stderr, flush=True)
        return False

if __name__ == '__main__':
    if len(sys.argv) < 3:
        print("Usage: python expect_runner.py <bot_path> <bot_exec_command>", file=sys.stderr, flush=True)
        sys.exit(1)

    bot_path = sys.argv[1]
    bot_exec_command = " ".join(sys.argv[2:])
    
    if run_expect_replay(bot_path, bot_exec_command):
        sys.exit(0)
    else:
        sys.exit(1)
