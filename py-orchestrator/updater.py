from pathlib import Path
from rich.console import Console
from rich.table import Table

from botconfig import BotConfig
import shell

console = Console()
CONFIG_FILE = Path("../config/bots_config.json")

def show_config():
    """Menampilkan konfigurasi bot dari JSON."""
    config = BotConfig.load()
    if not config:
        return
        
    table = Table(title="Konfigurasi Bot & Tools", expand=True)
    table.add_column("Nama")
    table.add_column("Path (Tujuan)")
    table.add_column("URL Repository")

    for bot in config.bots_and_tools:
        table.add_row(bot.name, bot.path, bot.repo_url)
    
    console.print(table)

async def update_all_bots():
    """Menjalankan git clone atau git pull untuk semua entri."""
    config = BotConfig.load()
    if not config:
        return
        
    console.print(f"[cyan]Mulai proses update untuk {len(config.bots_and_tools)} entri...[/]")
    
    for bot in config.bots_and_tools:
        console.print(f"\n[bold cyan]--- Memproses: {bot.name} ---[/]")
        
        if not bot.path or not bot.repo_url:
            console.print("[yellow]Entri tidak valid, skipping...[/]")
            continue
            
        target_path = Path("..", bot.path).resolve()
        
        if (target_path / ".git").is_dir():
            console.print(f"Folder [yellow]{target_path.name}[/] ditemukan. Menjalankan 'git pull'...")
            await shell.run_stream_async("git", "pull --rebase", target_path)
        else:
            console.print(f"Folder [yellow]{target_path.name}[/] tidak ditemukan. Menjalankan 'git clone'...")
            # Clone ke parent dir
            await shell.run_stream_async("git", f"clone --depth 1 {bot.repo_url} \"{target_path}\"", target_path.parent)
            
    console.print("\n[bold green]✅ Semua bot & tools berhasil di-clone/update.[/]")
