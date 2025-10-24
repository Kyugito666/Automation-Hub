import asyncio
import sys
import os
import platform
import subprocess
import json
from pathlib import Path
from typing import Tuple, Optional
from rich.console import Console
import pexpect
import pexpect.popen_spawn

console = Console()

# ==============================================================================
# 1. STREAMING (Non-Interaktif, untuk git pull, pip install, dll)
# ==============================================================================

async def _read_stream(stream, prefix, style):
    """Membaca output dari subprocess stream."""
    while True:
        line = await stream.readline()
        if not line:
            break
        console.print(f"[{style}]{prefix}{line.decode().strip()}[/]")
    
async def run_stream_async(
    command: str,
    args: str,
    working_dir: Optional[Path] = None
):
    """Menjalankan perintah non-interaktif dan stream outputnya."""
    if platform.system() == "Windows":
        full_command = f'"{command}" {args}'
        shell_cmd = ["cmd.exe", "/c", full_command]
    else:
        full_command = f"{command} {args}"
        shell_cmd = ["/bin/bash", "-c", full_command]

    process = await asyncio.create_subprocess_exec(
        *shell_cmd,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
        cwd=working_dir or Path.cwd()
    )

    await asyncio.gather(
        _read_stream(process.stdout, "", "grey"),
        _read_stream(process.stderr, "[ERR] ", "yellow")
    )

    await process.wait()
    if process.returncode != 0:
        console.print(f"[red]Exit Code: {process.returncode}[/]")
        if "npm" in command:
            console.print("[red]Error menjalankan npm. Pastikan Node.js terinstall & ada di PATH.[/]")
        if "pip" in command:
            console.print("[red]Error menjalankan pip. Pastikan Python/venv ada di PATH.[/]")
    return process.returncode

# ==============================================================================
# 2. PTY (Interaktif Penuh, untuk bot run manual)
# ==============================================================================

def _interactive_task(
    command: str,
    args: str,
    working_dir: Path
):
    """Tugas blocking yang akan dijalankan di thread terpisah."""
    full_command = f'"{command}" {args}' if platform.system() == "Windows" else f"{command} {args}"
    
    console.print(f"[dim]Spawning PTY: {full_command} di {working_dir}[/]")
    
    child = None
    try:
        if platform.system() == "Windows":
            # pexpect.popen_spawn menggunakan winpty di Windows
            child = pexpect.popen_spawn.PopenSpawn(full_command, cwd=working_dir, encoding='utf-8', timeout=None)
        else:
            # pexpect.spawn untuk Linux/macOS
            child = pexpect.spawn(full_command, cwd=working_dir, encoding='utf-8', timeout=None)
        
        # 'interact()' mengambil alih STDIN/STDOUT dan memberikannya ke child process
        # Ini adalah solusi robust untuk raw input (y/n, dll)
        child.interact()
        
    except pexpect.exceptions.ExceptionPexpect as e:
        console.print(f"\n[red]PTY Error: {e}[/]")
    except Exception as e:
        console.print(f"\n[red]PTY Shell Error: {e}[/]")
    finally:
        if child:
            if child.isalive():
                child.terminate()
            console.print(f"\n[yellow]PTY process finished with exit code: {child.exitstatus}[/]")
        return child.exitstatus if child else 1


async def run_interactive_pty(
    command: str,
    args: str,
    working_dir: Path,
    cancel_event: asyncio.Event
):
    """Menjalankan PTY interaktif dengan monitoring pembatalan."""
    interact_task = asyncio.to_thread(_interactive_task, command, args, working_dir)
    cancel_task = asyncio.create_task(cancel_event.wait())

    done, pending = await asyncio.wait(
        {interact_task, cancel_task},
        return_when=asyncio.FIRST_COMPLETED
    )

    if cancel_task in done:
        # User menekan Ctrl+C (dari handler utama)
        console.print("[yellow]Mencoba membatalkan PTY...[/]")
        # pexpect.interact() menangani Ctrl+C-nya sendiri dan akan exit,
        # jadi kita hanya perlu menunggu interact_task selesai.
        pass
    
    # Menunggu task interaktif selesai
    result_code = await interact_task
    
    # Pastikan cancel_task juga dibatalkan jika belum selesai
    if cancel_task in pending:
        cancel_task.cancel()
        
    if cancel_event.is_set():
        raise asyncio.CancelledError()

# ==============================================================================
# 3. PTY (Scripted, untuk bot run auto-answer)
# ==============================================================================

async def run_pty_with_script(
    input_file: Path,
    command: str,
    args: str,
    working_dir: Path,
    cancel_event: asyncio.Event
):
    """Menjalankan PTY dan mengirim input dari file secara otomatis."""
    
    # 1. Baca input
    try:
        with open(input_file, "r") as f:
            answers_dict = json.load(f)
        answers = list(answers_dict.values())
    except Exception as e:
        console.print(f"[red]Gagal membaca answer file {input_file}: {e}[/]")
        return
        
    full_command = f'"{command}" {args}' if platform.system() == "Windows" else f"{command} {args}"
    console.print(f"[dim]Spawning PTY (Scripted): {full_command}[/]")

    child = None
    try:
        if platform.system() == "Windows":
            child = pexpect.popen_spawn.PopenSpawn(full_command, cwd=working_dir, encoding='utf-8', timeout=30)
        else:
            child = pexpect.spawn(full_command, cwd=working_dir, encoding='utf-8', timeout=30)
        
        # Task untuk membaca output
        async def read_output():
            try:
                while not cancel_event.is_set():
                    line = await asyncio.to_thread(child.readline)
                    if not line:
                        break
                    console.print(f"[grey]{line.strip()}[/]")
            except pexpect.exceptions.EOF:
                console.print("[dim]PTY script run: End of file.[/]")
            except Exception as e:
                if not cancel_event.is_set():
                    console.print(f"[yellow]PTY read error: {e}[/]")

        # Task untuk mengirim input
        async def send_input():
            try:
                for answer in answers:
                    if cancel_event.is_set():
                        break
                    await asyncio.sleep(0.5) # Beri waktu bot untuk siap
                    child.sendline(answer)
                    console.print(f"[cyan]>[/][dim] (auto-sent: '{answer}')[/]")
                
                # Kirim EOF untuk sinyal selesai
                await asyncio.sleep(1)
                if child.isalive():
                    child.sendeof()
            except Exception as e:
                if not cancel_event.is_set():
                    console.print(f"[yellow]PTY write error: {e}[/]")
        
        # Jalankan kedua task
        read_task = asyncio.create_task(read_output())
        send_task = asyncio.create_task(send_input())
        
        # Tunggu sampai selesai atau di-cancel
        while not read_task.done() and not cancel_event.is_set():
            await asyncio.sleep(0.1)

        if cancel_event.is_set():
            read_task.cancel()
            send_task.cancel()
            console.print("[yellow]PTY script run dibatalkan.[/]")
            raise asyncio.CancelledError()
            
        await read_task
        await send_task

    finally:
        if child and child.isalive():
            child.terminate(force=True)
        console.print(f"\n[green]PTY auto-run finished.[/]")


# ==============================================================================
# 4. Terminal Baru (untuk "Run All")
# ==============================================================================

def run_in_new_terminal(command: str, args: str, working_dir: Path):
    """Membuka terminal OS baru (bukan PTY)."""
    full_command = f'"{command}" {args}' if platform.system() == "Windows" else f"{command} {args}"
    
    try:
        if platform.system() == "Windows":
            subprocess.Popen(
                ["cmd.exe", "/c", f"start cmd.exe /k \"cd /d \"{working_dir}\" && {full_command}\""],
                cwd=working_dir,
                shell=True
            )
        elif platform.system() == "Linux":
            # Coba gnome-terminal, lalu xterm
            try:
                subprocess.Popen(
                    ["gnome-terminal", "--", "bash", "-c", f"cd \"{working_dir}\" && {full_command}; exec bash"],
                    cwd=working_dir
                )
            except FileNotFoundError:
                 subprocess.Popen(
                    ["xterm", "-e", f"bash -c \"cd \\\"{working_dir}\\\" && {full_command}; exec bash\""],
                    cwd=working_dir
                )
        elif platform.system() == "Darwin": # macOS
             subprocess.Popen(
                ["osascript", "-e", f'tell application "Terminal" to do script "cd \\\"{working_dir}\\\" && {full_command}"'],
                cwd=working_dir
            )
    except Exception as e:
        console.print(f"[red]Gagal membuka terminal baru: {e}[/]")
