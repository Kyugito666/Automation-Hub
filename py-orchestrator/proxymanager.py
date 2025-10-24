import asyncio
from pathlib import Path
import shutil
import sys
from rich.console import Console
import shell

console = Console()

PROXY_SYNC_PATH = Path("../proxysync").resolve()
CONFIG_SOURCE_PATH = Path("../config").resolve()

async def deploy_proxies():
    console.print("[bold cyan]--- Memulai Proses Deploy Proxy ---[/]")
    
    if not PROXY_SYNC_PATH.is_dir():
        console.print(f"[red]Error: Folder '{PROXY_SYNC_PATH}' tidak ditemukan.[/]")
        console.print("[yellow]Harap jalankan 'Update Semua Bot & Tools' (Menu 2) terlebih dahulu.[/]")
        return
        
    console.print("1. Menyalin file config (apilist.txt, paths.txt)...")
    try:
        shutil.copy(CONFIG_SOURCE_PATH / "apilist.txt", PROXY_SYNC_PATH / "apilist.txt")
        shutil.copy(CONFIG_SOURCE_PATH / "paths.txt", PROXY_SYNC_PATH / "paths.txt")
        console.print("[green]   Salin config berhasil.[/]")
    except Exception as e:
        console.print(f"[red]   Gagal menyalin config: {e}[/]")
        return
        
    console.print("\n2. Menjalankan 'pip install -r requirements.txt' untuk ProxySync...")
    await shell.run_stream_async(
        "pip", "install -r requirements.txt", PROXY_SYNC_PATH
    )
    
    auto_deploy_script = PROXY_SYNC_PATH / "auto_deploy.py"
    
    if auto_deploy_script.exists():
        console.print("\n3. Menjalankan ProxySync (Auto Mode)...")
        console.print("[dim]Download → Convert → Test → Distribute[/]")
        await shell.run_stream_async(
            "python", "auto_deploy.py", PROXY_SYNC_PATH
        )
    else:
        console.print("\n3. Menjalankan ProxySync (Manual Mode)...")
        console.print("[yellow]auto_deploy.py tidak ditemukan, membuka terminal interaktif...[/]")
        shell.run_in_new_terminal(
            "python", "main.py", PROXY_SYNC_PATH
        )
        console.print("[green]Terminal eksternal dibuka.[/]")
        console.print("[dim]Tekan Enter setelah selesai...[/]")
        await asyncio.to_thread(sys.stdin.readline)
        
    console.print("\n[bold green]✅ Proses deploy proxy selesai.[/]")
