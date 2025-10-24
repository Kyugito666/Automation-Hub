#!/usr/bin/env python3
"""
PTY Helper for C# Orchestrator
Cross-platform alternative to node-pty
"""

import sys
import os
import json
import subprocess
import threading
import time
from pathlib import Path

def run_interactive(command, args, cwd):
    """Mode interaktif manual (pakai subprocess biasa)"""
    try:
        cmd_list = [command] + args
        process = subprocess.Popen(
            cmd_list,
            cwd=cwd,
            stdin=sys.stdin,
            stdout=sys.stdout,
            stderr=sys.stderr,
            text=True
        )
        process.wait()
        return process.returncode
    except KeyboardInterrupt:
        process.terminate()
        return 130
    except Exception as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1

def run_with_script(input_file, command, args, cwd):
    """Mode auto-answer dengan input dari file"""
    try:
        # Baca input file
        input_path = Path(input_file)
        if not input_path.exists():
            print(f"ERROR: Input file not found: {input_file}", file=sys.stderr)
            return 1
        
        if input_file.endswith('.json'):
            with open(input_path, 'r', encoding='utf-8') as f:
                data = json.load(f)
                # Ambil values dan gabung dengan newline
                inputs = '\n'.join(str(v) for v in data.values()) + '\n'
        else:
            with open(input_path, 'r', encoding='utf-8') as f:
                inputs = f.read()
                if not inputs.endswith('\n'):
                    inputs += '\n'
        
        # Jalankan command dengan input otomatis
        cmd_list = [command] + args
        process = subprocess.Popen(
            cmd_list,
            cwd=cwd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            bufsize=1,
            universal_newlines=True
        )
        
        # Thread untuk kirim input
        def send_input():
            try:
                time.sleep(0.5)  # Delay kecil agar command siap
                process.stdin.write(inputs)
                process.stdin.flush()
                process.stdin.close()
            except:
                pass
        
        input_thread = threading.Thread(target=send_input, daemon=True)
        input_thread.start()
        
        # Print output real-time
        try:
            for line in iter(process.stdout.readline, ''):
                if line:
                    print(line, end='', flush=True)
        except KeyboardInterrupt:
            process.terminate()
        
        process.wait()
        return process.returncode
        
    except Exception as e:
        print(f"ERROR: {e}", file=sys.stderr)
        import traceback
        traceback.print_exc()
        return 1

def main():
    if len(sys.argv) < 2:
        print("Usage (manual):   pty_helper.py <command> [args...]", file=sys.stderr)
        print("Usage (scripted): pty_helper.py <input_file> <command> [args...]", file=sys.stderr)
        return 1
    
    # Deteksi mode
    input_file = None
    command = None
    args = []
    cwd = os.getcwd()
    
    # Cek apakah arg pertama adalah file
    if len(sys.argv) > 2:
        potential_file = sys.argv[1]
        if os.path.isfile(potential_file) and (potential_file.endswith('.json') or potential_file.endswith('.txt')):
            input_file = potential_file
            command = sys.argv[2]
            args = sys.argv[3:]
        else:
            command = sys.argv[1]
            args = sys.argv[2:]
    else:
        command = sys.argv[1]
        args = sys.argv[2:] if len(sys.argv) > 2 else []
    
    # Execute
    if input_file:
        return run_with_script(input_file, command, args, cwd)
    else:
        return run_interactive(command, args, cwd)

if __name__ == '__main__':
    sys.exit(main())
