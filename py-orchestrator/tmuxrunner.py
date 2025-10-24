import asyncio
from pathlib import Path
import json
import platform
from rich.console import Console

from botconfig import BotConfig
import shell
from botrunner import get_run_command

console = Console()
CONFIG_FILE = Path("../config/bots_config.json")
SESSION_NAME = "automation-hub"

async def run_all_bots_in_tmux():
    if platform.system() != "Linux":
        console.print("[red]Tmux mode hanya tersedia di Linux.[/]")
        return
        
    # Cek tmux
    if await shell.run_stream_async("which", "tmux", None) != 0:
        console.print("[red]tmux tidak terinstall. Install: sudo apt install tmux[/]")
        return
        
    config = BotConfig.load()
    if not config:
        return
        
    bots_only = [
        b for b in config.bots_and_tools if b.is_bot and b.enabled
    ]
    
    if not bots_only:
        console.print("[yellow]Tidak ada bot aktif.[/]")
        return
        
    console.print(f"[cyan]Membuat tmux session '{SESSION_NAME}'...[/]")
    
    # Kill session lama
    await shell.run_stream_async("tmux", f"kill-session -t {SESSION_NAME}", None)
    
    # Buat session baru
    first_bot = bots_only[0]
    first_path = Path("..", first_bot.path).resolve()
    executor, args = get_run_command(first_path, first_bot.type)
    
    if executor:
        await shell.run_stream_async(
            "tmux",
            f"new-session -d -s {SESSION_NAME} -n {first_bot.name} -c \"{first_path}\" '{executor} {args}'",
            None
        )
        console.print(f"[green]✓ {first_bot.name}[/]")
    else:
        console.print(f"[red]✗ {first_bot.name}: No run file[/]")
        
    # Tambah bot lain di window baru
    for bot in bots_only[1:]:
        bot_path = Path("..", bot.path).resolve()
        executor, args = get_run_command(bot_path, bot.type)
        
        if executor:
            await shell.run_stream_async(
                "tmux",
                f"new-window -t {SESSION_NAME} -n {bot.name} -c \"{bot_path}\" '{executor} {args}'",
                None
            )
            console.print(f"[green]✓ {bot.name}[/]")
        else:
            console.print(f"[red]✗ {bot.name}: No run file[/]")
            
    console.print(f"\n[bold green]✅ Semua bot berjalan di tmux session '{SESSION_NAME}'[/]")
    console.print("[yellow]Perintah berguna:[/]")
    console.print(f"[dim]  tmux attach -t {SESSION_NAME}     # Attach ke session[/]")
    console.print(f"[dim]  tmux ls                          # List sessions[/]")
    console.print(f"[dim]  tmux kill-session -t {SESSION_NAME}  # Stop semua bot[/]")
