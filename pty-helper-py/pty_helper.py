#!/usr/bin/env python3
"""
PTY Helper for C# Orchestrator - Windows PATH Fix
Cross-platform subprocess automation with robust error handling
"""

import sys
import os
import json
import subprocess
import threading
import time
import signal
import shutil
from pathlib import Path

# Global process untuk cleanup
_current_process = None

def cleanup_handler(signum, frame):
    """Handle Ctrl+C gracefully"""
    global _current_process
    if _current_process and _current_process.poll() is None:
        _current_process.terminate()
        _current_process.wait(timeout=3)
    sys.exit(130)

signal.signal(signal.SIGINT, cleanup_handler)

def log_error(msg):
    """Consistent error logging"""
    print(f"[PTY-HELPER ERROR] {msg}", file=sys.stderr, flush=True)

def log_info(msg):
    """Info logging"""
    print(f"[PTY-HELPER] {msg}", file=sys.stderr, flush=True)

def resolve_command(command):
    """
    Resolve command menggunakan shutil.which() untuk handle Windows PATH
    CRITICAL FIX: npm/node/python tidak ditemukan tanpa ini
    """
    # Jika sudah absolute path, langsung return
    if os.path.isabs(command) and os.path.isfile(command):
        return command
    
    # Cek apakah command ada di PATH
    resolved = shutil.which(command)
    
    if resolved:
        log_info(f"Resolved '{command}' -> '{resolved}'")
        return resolved
    
    # Fallback: coba dengan .exe/.cmd/.bat untuk Windows
    if sys.platform == 'win32':
        for ext in ['.exe', '.cmd', '.bat']:
            resolved = shutil.which(command + ext)
            if resolved:
                log_info(f"Resolved '{command}' -> '{resolved}'")
                return resolved
    
    # Tidak ditemukan, return original (akan error nanti)
    log_error(f"Command '{command}' not found in PATH")
    return command

def run_interactive(command, args, cwd):
    """
    Mode interaktif manual (real-time I/O passthrough)
    Untuk testing dan debugging
    """
    global _current_process
    
    # CRITICAL FIX: Resolve command path
    resolved_cmd = resolve_command(command)
    
    log_info(f"Starting interactive: {resolved_cmd} {' '.join(args)}")
    log_info(f"Working directory: {cwd}")
    
    try:
        cmd_list = [resolved_cmd] + args
        
        # CRITICAL FIX: Use shell=True di Windows untuk npm/node
        use_shell = sys.platform == 'win32' and command in ['npm', 'node', 'python', 'py']
        
        _current_process = subprocess.Popen(
            cmd_list,
            cwd=cwd,
            stdin=sys.stdin,
            stdout=sys.stdout,
            stderr=sys.stderr,
            text=True,
            shell=use_shell,
            encoding='utf-8',
            errors='replace'
        )
        
        returncode = _current_process.wait()
        _current_process = None
        
        return returncode
        
    except KeyboardInterrupt:
        log_info("KeyboardInterrupt received")
        if _current_process:
            _current_process.terminate()
            _current_process.wait(timeout=3)
        return 130
        
    except FileNotFoundError:
        log_error(f"Command not found: {command}")
        log_error("Make sure the executable is in PATH")
        log_error(f"Current PATH: {os.environ.get('PATH', 'NOT SET')[:200]}...")
        return 127
        
    except Exception as e:
        log_error(f"Interactive mode failed: {e}")
        import traceback
        traceback.print_exc()
        return 1

def run_with_script(input_file, command, args, cwd):
    """
    Mode auto-answer dengan input dari file JSON/TXT
    Untuk automasi CI/CD
    """
    global _current_process
    
    log_info(f"Starting auto-answer mode")
    log_info(f"Input file: {input_file}")
    log_info(f"Command: {command} {' '.join(args)}")
    log_info(f"Working directory: {cwd}")
    
    try:
        # CRITICAL FIX: Resolve command path
        resolved_cmd = resolve_command(command)
        
        # Validasi & baca input file
        input_path = Path(input_file)
        if not input_path.exists():
            log_error(f"Input file not found: {input_file}")
            return 1
        
        # Parse input berdasarkan format
        if input_file.endswith('.json'):
            try:
                with open(input_path, 'r', encoding='utf-8') as f:
                    data = json.load(f)
                    
                # Ambil values dan join dengan newline
                if isinstance(data, dict):
                    inputs = '\n'.join(str(v) for v in data.values()) + '\n'
                elif isinstance(data, list):
                    inputs = '\n'.join(str(v) for v in data) + '\n'
                else:
                    log_error(f"Invalid JSON format. Expected dict or list, got {type(data)}")
                    return 1
                    
                log_info(f"Loaded {len(data)} inputs from JSON")
                
            except json.JSONDecodeError as e:
                log_error(f"Invalid JSON file: {e}")
                return 1
        else:
            # Plain text format
            with open(input_path, 'r', encoding='utf-8') as f:
                inputs = f.read()
                if not inputs.endswith('\n'):
                    inputs += '\n'
            
            log_info(f"Loaded {len(inputs.splitlines())} lines from text file")
        
        # Setup subprocess dengan pipes
        cmd_list = [resolved_cmd] + args
        
        # CRITICAL FIX: Use shell=True di Windows untuk npm/node
        use_shell = sys.platform == 'win32' and command in ['npm', 'node', 'python', 'py']
        
        log_info(f"Shell mode: {use_shell}")
        
        _current_process = subprocess.Popen(
            cmd_list,
            cwd=cwd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            bufsize=1,
            universal_newlines=True,
            shell=use_shell,
            encoding='utf-8',
            errors='replace'
        )
        
        # Thread untuk kirim input secara bertahap
        input_sent = threading.Event()
        
        def send_input():
            try:
                # Small delay agar child process siap menerima input
                time.sleep(0.5)
                
                # Kirim semua input sekaligus
                _current_process.stdin.write(inputs)
                _current_process.stdin.flush()
                
                # Close stdin untuk signal EOF
                _current_process.stdin.close()
                
                input_sent.set()
                log_info("Input sent successfully")
                
            except BrokenPipeError:
                log_error("BrokenPipeError: Process terminated before input finished")
            except Exception as e:
                log_error(f"Error sending input: {e}")
        
        input_thread = threading.Thread(target=send_input, daemon=True)
        input_thread.start()
        
        # Stream output real-time ke parent process
        output_lines = 0
        try:
            for line in iter(_current_process.stdout.readline, ''):
                if line:
                    # Langsung print tanpa prefix agar C# bisa parse
                    print(line, end='', flush=True)
                    output_lines += 1
        except KeyboardInterrupt:
            log_info("KeyboardInterrupt during output streaming")
            _current_process.terminate()
        
        # Wait for process completion
        returncode = _current_process.wait()
        _current_process = None
        
        log_info(f"Process completed with exit code {returncode}")
        log_info(f"Output lines: {output_lines}")
        
        # Timeout check untuk input thread
        if not input_sent.wait(timeout=5):
            log_error("WARNING: Input thread did not complete in time")
        
        return returncode
        
    except FileNotFoundError:
        log_error(f"Command not found: {command}")
        log_error("Common causes:")
        log_error("  - npm/node not in PATH")
        log_error("  - Executable name typo")
        log_error(f"Current PATH: {os.environ.get('PATH', 'NOT SET')[:200]}...")
        return 127
        
    except Exception as e:
        log_error(f"Auto-answer mode failed: {e}")
        import traceback
        traceback.print_exc()
        return 1

def validate_environment():
    """Pre-flight checks"""
    issues = []
    
    # Check Python version
    if sys.version_info < (3, 6):
        issues.append(f"Python 3.6+ required, got {sys.version_info.major}.{sys.version_info.minor}")
    
    # Check common commands availability (non-blocking)
    log_info("Checking common commands in PATH:")
    for cmd in ['python', 'node', 'npm', 'git']:
        result = shutil.which(cmd)
        if result:
            log_info(f"  ✓ {cmd}: {result}")
        else:
            log_info(f"  ✗ {cmd}: not found")
    
    return issues

def main():
    # Pre-flight validation
    issues = validate_environment()
    if issues:
        log_error("Environment validation failed:")
        for issue in issues:
            log_error(f"  - {issue}")
        return 1
    
    # Parse arguments
    if len(sys.argv) < 2:
        print("PTY Helper - Subprocess Automation for C# Orchestrator", file=sys.stderr)
        print("", file=sys.stderr)
        print("Usage (manual interactive):", file=sys.stderr)
        print("  pty_helper.py <command> [args...]", file=sys.stderr)
        print("", file=sys.stderr)
        print("Usage (auto-answer with input file):", file=sys.stderr)
        print("  pty_helper.py <input_file.json> <command> [args...]", file=sys.stderr)
        print("", file=sys.stderr)
        print("Examples:", file=sys.stderr)
        print("  pty_helper.py python bot.py", file=sys.stderr)
        print("  pty_helper.py bot_answers.json npm start", file=sys.stderr)
        return 1
    
    # Deteksi mode berdasarkan arguments
    input_file = None
    command = None
    args = []
    cwd = os.getcwd()
    
    # Cek apakah arg pertama adalah file
    if len(sys.argv) > 2:
        potential_file = sys.argv[1]
        
        # Deteksi file dengan ekstensi atau path check
        if os.path.isfile(potential_file) and (
            potential_file.endswith('.json') or 
            potential_file.endswith('.txt') or
            '/' in potential_file or
            '\\' in potential_file
        ):
            # Mode: auto-answer
            input_file = potential_file
            command = sys.argv[2]
            args = sys.argv[3:] if len(sys.argv) > 3 else []
        else:
            # Mode: interactive
            command = sys.argv[1]
            args = sys.argv[2:]
    else:
        # Mode: interactive (single command)
        command = sys.argv[1]
        args = []
    
    # Execute dengan mode yang sesuai
    try:
        if input_file:
            return run_with_script(input_file, command, args, cwd)
        else:
            return run_interactive(command, args, cwd)
    except Exception as e:
        log_error(f"Fatal error: {e}")
        import traceback
        traceback.print_exc()
        return 1

if __name__ == '__main__':
    sys.exit(main())
