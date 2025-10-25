#!/usr/bin/env python3
"""
PTY Helper for C# Orchestrator - Self-Healing Windows Handler
Auto-detects and fixes common Windows subprocess issues
"""

import sys
import os
import json
import subprocess
import threading
import time
import signal
import shutil
import re
from pathlib import Path

# Force UTF-8 encoding for stdout/stderr
if sys.platform == 'win32':
    import io
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')

# Global process untuk cleanup
_current_process = None
_retry_count = 0
MAX_RETRIES = 3

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

def log_warn(msg):
    """Warning logging"""
    print(f"[PTY-HELPER WARN] {msg}", file=sys.stderr, flush=True)

def resolve_command(command):
    """Resolve command menggunakan shutil.which() untuk handle Windows PATH"""
    if os.path.isabs(command) and os.path.isfile(command):
        return command
    
    resolved = shutil.which(command)
    
    if resolved:
        log_info(f"Resolved '{command}' -> '{resolved}'")
        return resolved
    
    if sys.platform == 'win32':
        for ext in ['.exe', '.cmd', '.bat']:
            resolved = shutil.which(command + ext)
            if resolved:
                log_info(f"Resolved '{command}' -> '{resolved}'")
                return resolved
    
    log_error(f"Command '{command}' not found in PATH")
    return command

def run_interactive(command, args, cwd):
    """Mode interaktif manual (real-time I/O passthrough)"""
    global _current_process
    
    resolved_cmd = resolve_command(command)
    
    log_info(f"Starting interactive: {resolved_cmd} {' '.join(args)}")
    log_info(f"Working directory: {cwd}")
    
    try:
        cmd_list = [resolved_cmd] + args
        use_shell = sys.platform == 'win32' and command in ['npm', 'node', 'python', 'py']
        
        _current_process = subprocess.Popen(
            cmd_list,
            cwd=cwd,
            stdin=sys.stdin,
            stdout=sys.stdout,
            stderr=sys.stderr,
            shell=use_shell
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
        return 127
        
    except Exception as e:
        log_error(f"Interactive mode failed: {e}")
        import traceback
        traceback.print_exc()
        return 1

def detect_error_type(error_msg):
    """Self-healing: Detect error type and suggest fix strategy"""
    error_lower = str(error_msg).lower()
    
    if 'winerror 6' in error_lower or 'handle is invalid' in error_lower:
        return 'stdin_handle_error'
    elif 'winerror 2' in error_lower or 'cannot find the file' in error_lower:
        return 'file_not_found'
    elif 'winerror 5' in error_lower or 'access is denied' in error_lower:
        return 'access_denied'
    elif 'eof when reading' in error_lower or 'unexpected eof' in error_lower:
        return 'premature_eof'
    elif 'brokenpipeerror' in error_lower:
        return 'broken_pipe'
    else:
        return 'unknown'

def wait_for_prompt(process, timeout=5.0):
    """
    Wait for prompt indicators before sending input
    Returns True if prompt detected, False if timeout
    """
    prompt_patterns = [
        r'pilih.*\[',        # "Pilih Antara [1/2/3]"
        r'enter.*:',         # "Enter your choice:"
        r'select.*:',        # "Select option:"
        r'\[.*\]\s*->\s*$',  # "[1/2/3] -> "
        r'>\s*$',            # Generic ">"
        r':\s*$',            # Generic ":"
    ]
    
    start_time = time.time()
    buffer = ""
    
    while time.time() - start_time < timeout:
        if process.poll() is not None:
            return False
        
        try:
            chunk = process.stdout.read(1)
            if not chunk:
                time.sleep(0.05)
                continue
            
            char = chunk.decode('utf-8', errors='replace')
            buffer += char
            print(char, end='', flush=True)
            
            # Keep only last 200 chars for pattern matching
            if len(buffer) > 200:
                buffer = buffer[-200:]
            
            # Check if any prompt pattern matches
            for pattern in prompt_patterns:
                if re.search(pattern, buffer, re.IGNORECASE):
                    log_info(f"Prompt detected: {pattern}")
                    return True
                    
        except Exception as e:
            log_warn(f"Error reading prompt: {e}")
            time.sleep(0.05)
    
    log_warn(f"Prompt wait timeout ({timeout}s)")
    return False

def run_with_script_strategy_1(input_file, command, args, cwd, inputs):
    """Strategy 1: Standard mode dengan prompt detection"""
    global _current_process
    
    log_info("Trying Strategy 1: Standard stdin pipe with prompt detection")
    
    resolved_cmd = resolve_command(command)
    cmd_list = [resolved_cmd] + args
    use_shell = sys.platform == 'win32' and command in ['npm', 'node', 'python', 'py']
    
    env = os.environ.copy()
    if sys.platform == 'win32':
        env['PYTHONIOENCODING'] = 'utf-8'
        env['PYTHONUNBUFFERED'] = '1'
    
    _current_process = subprocess.Popen(
        cmd_list,
        cwd=cwd,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        shell=use_shell,
        env=env,
        bufsize=0  # Unbuffered
    )
    
    input_lines = inputs.strip().split('\n')
    input_index = [0]  # Mutable container for closure
    
    def send_input():
        try:
            for i, line in enumerate(input_lines):
                # Wait for prompt before sending input
                if not wait_for_prompt(_current_process, timeout=8.0):
                    log_warn(f"No prompt detected before input {i+1}, sending anyway...")
                    time.sleep(0.5)
                
                if _current_process.poll() is not None:
                    log_info(f"Process ended before input {i+1}")
                    break
                
                log_info(f"Sending input {i+1}/{len(input_lines)}: {line[:50]}")
                _current_process.stdin.write((line + '\n').encode('utf-8'))
                _current_process.stdin.flush()
                time.sleep(0.2)
                
                input_index[0] = i + 1
            
            # Wait a bit for final output
            time.sleep(0.5)
            
            # Keep stdin alive until process ends
            while _current_process.poll() is None:
                time.sleep(0.1)
            
            try:
                _current_process.stdin.close()
            except:
                pass
                
        except BrokenPipeError:
            log_info("Process closed stdin (normal for some bots)")
        except Exception as e:
            log_warn(f"Input thread error: {e}")
    
    input_thread = threading.Thread(target=send_input, daemon=True)
    input_thread.start()
    
    output = []
    try:
        while True:
            chunk = _current_process.stdout.read(1024)
            if not chunk:
                break
            text = chunk.decode('utf-8', errors='replace')
            # Don't print here - already printed in wait_for_prompt
            output.append(text)
    except Exception as e:
        log_warn(f"Output read error: {e}")
    
    returncode = _current_process.wait()
    input_thread.join(timeout=2)
    _current_process = None
    
    full_output = ''.join(output)
    
    # Check for WinError 6 in output
    if 'winerror 6' in full_output.lower() or 'handle is invalid' in full_output.lower():
        log_warn("Detected WinError 6 in output, strategy failed")
        raise Exception("WinError 6: The handle is invalid")
    
    return returncode, full_output

def run_with_script_strategy_2(input_file, command, args, cwd, inputs):
    """Strategy 2: Temp file mode (untuk handle error Windows)"""
    global _current_process
    
    log_info("Trying Strategy 2: Temp file input redirect")
    
    import tempfile
    
    # Create temp input file
    with tempfile.NamedTemporaryFile(mode='w', delete=False, encoding='utf-8', suffix='.txt') as f:
        f.write(inputs)
        temp_input = f.name
    
    try:
        resolved_cmd = resolve_command(command)
        cmd_list = [resolved_cmd] + args
        use_shell = sys.platform == 'win32' and command in ['npm', 'node', 'python', 'py']
        
        env = os.environ.copy()
        if sys.platform == 'win32':
            env['PYTHONIOENCODING'] = 'utf-8'
            env['PYTHONUNBUFFERED'] = '1'
        
        # Redirect stdin from file instead of pipe
        with open(temp_input, 'r', encoding='utf-8') as input_f:
            _current_process = subprocess.Popen(
                cmd_list,
                cwd=cwd,
                stdin=input_f,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                shell=use_shell,
                env=env
            )
            
            output = []
            while True:
                chunk = _current_process.stdout.read(1024)
                if not chunk:
                    break
                text = chunk.decode('utf-8', errors='replace')
                print(text, end='', flush=True)
                output.append(text)
            
            returncode = _current_process.wait()
            _current_process = None
            
            return returncode, ''.join(output)
    finally:
        try:
            os.unlink(temp_input)
        except:
            pass

def run_with_script_strategy_3(input_file, command, args, cwd, inputs):
    """Strategy 3: Batch script wrapper (ultimate Windows fallback)"""
    global _current_process
    
    log_info("Trying Strategy 3: Batch script wrapper")
    
    import tempfile
    
    # Create temp files
    with tempfile.NamedTemporaryFile(mode='w', delete=False, encoding='utf-8', suffix='.txt') as f:
        f.write(inputs)
        temp_input = f.name
    
    resolved_cmd = resolve_command(command)
    
    # Create batch wrapper
    if sys.platform == 'win32':
        with tempfile.NamedTemporaryFile(mode='w', delete=False, suffix='.bat', encoding='utf-8') as f:
            f.write('@echo off\n')
            f.write('chcp 65001 >nul\n')  # UTF-8 codepage
            f.write(f'cd /d "{cwd}"\n')
            f.write(f'"{resolved_cmd}" {" ".join(args)} < "{temp_input}"\n')
            batch_file = f.name
    else:
        with tempfile.NamedTemporaryFile(mode='w', delete=False, suffix='.sh', encoding='utf-8') as f:
            f.write('#!/bin/bash\n')
            f.write(f'cd "{cwd}"\n')
            f.write(f'"{resolved_cmd}" {" ".join(args)} < "{temp_input}"\n')
            batch_file = f.name
        os.chmod(batch_file, 0o755)
    
    try:
        _current_process = subprocess.Popen(
            [batch_file],
            cwd=cwd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            shell=True
        )
        
        output = []
        while True:
            chunk = _current_process.stdout.read(1024)
            if not chunk:
                break
            text = chunk.decode('utf-8', errors='replace')
            print(text, end='', flush=True)
            output.append(text)
        
        returncode = _current_process.wait()
        _current_process = None
        
        return returncode, ''.join(output)
    finally:
        try:
            os.unlink(temp_input)
            os.unlink(batch_file)
        except:
            pass

def run_with_script(input_file, command, args, cwd):
    """
    Self-healing auto-answer mode
    Tries multiple strategies if one fails
    """
    global _retry_count
    
    log_info(f"Starting auto-answer mode (self-healing)")
    log_info(f"Input file: {input_file}")
    log_info(f"Command: {command} {' '.join(args)}")
    log_info(f"Working directory: {cwd}")
    
    try:
        input_path = Path(input_file)
        if not input_path.exists():
            log_error(f"Input file not found: {input_file}")
            return 1
        
        # Parse input
        if input_file.endswith('.json'):
            try:
                with open(input_path, 'r', encoding='utf-8') as f:
                    data = json.load(f)
                    
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
            with open(input_path, 'r', encoding='utf-8') as f:
                inputs = f.read()
                if not inputs.endswith('\n'):
                    inputs += '\n'
            
            log_info(f"Loaded {len(inputs.splitlines())} lines from text file")
        
        # Self-healing: Try multiple strategies
        strategies = [
            run_with_script_strategy_1,
            run_with_script_strategy_2,
            run_with_script_strategy_3
        ]
        
        last_error = None
        last_output = ""
        
        for i, strategy in enumerate(strategies, 1):
            try:
                returncode, output = strategy(input_file, command, args, cwd, inputs)
                last_output = output
                
                # Check if output contains error
                error_type = detect_error_type(output)
                
                if error_type == 'stdin_handle_error':
                    log_warn(f"Strategy {i} hit WinError 6, trying next strategy...")
                    if i < len(strategies):
                        time.sleep(1)
                        continue
                
                if error_type == 'unknown' or returncode == 0:
                    log_info(f"Strategy {i} succeeded with exit code {returncode}")
                    return returncode
                else:
                    log_warn(f"Strategy {i} completed but detected error: {error_type}")
                    if i < len(strategies):
                        log_info(f"Retrying with next strategy...")
                        time.sleep(1)
                        continue
                    else:
                        log_warn(f"All strategies exhausted, returning last exit code")
                        return returncode
                        
            except Exception as e:
                error_type = detect_error_type(str(e))
                log_error(f"Strategy {i} failed: {e}")
                log_info(f"Detected error type: {error_type}")
                last_error = e
                
                if i < len(strategies):
                    log_info(f"Attempting recovery with strategy {i+1}...")
                    time.sleep(1)
                else:
                    log_error(f"All {len(strategies)} strategies failed")
                    raise last_error
        
        return 1
        
    except FileNotFoundError:
        log_error(f"Command not found: {command}")
        log_error("Common causes:")
        log_error("  - npm/node not in PATH")
        log_error("  - Executable name typo")
        return 127
        
    except Exception as e:
        log_error(f"Auto-answer mode failed after all strategies: {e}")
        import traceback
        traceback.print_exc()
        return 1

def validate_environment():
    """Pre-flight checks"""
    issues = []
    
    if sys.version_info < (3, 6):
        issues.append(f"Python 3.6+ required, got {sys.version_info.major}.{sys.version_info.minor}")
    
    log_info("Checking common commands in PATH:")
    for cmd in ['python', 'node', 'npm', 'git']:
        result = shutil.which(cmd)
        if result:
            log_info(f"  ✓ {cmd}: {result}")
        else:
            log_info(f"  ✗ {cmd}: not found")
    
    return issues

def main():
    issues = validate_environment()
    if issues:
        log_error("Environment validation failed:")
        for issue in issues:
            log_error(f"  - {issue}")
        return 1
    
    if len(sys.argv) < 2:
        print("PTY Helper - Self-Healing Subprocess Automation", file=sys.stderr)
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
    
    input_file = None
    command = None
    args = []
    cwd = os.getcwd()
    
    if len(sys.argv) > 2:
        potential_file = sys.argv[1]
        
        if os.path.isfile(potential_file) and (
            potential_file.endswith('.json') or 
            potential_file.endswith('.txt') or
            '/' in potential_file or
            '\\' in potential_file
        ):
            input_file = potential_file
            command = sys.argv[2]
            args = sys.argv[3:] if len(sys.argv) > 3 else []
        else:
            command = sys.argv[1]
            args = sys.argv[2:]
    else:
        command = sys.argv[1]
        args = []
    
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
