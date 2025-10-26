print("DEBUG: Starting ui.py execution", flush=True)
import time
import requests
import re
from concurrent.futures import ThreadPoolExecutor, as_completed
from rich.align import Align
from rich.console import Console
from rich.panel import Panel
from rich.prompt import Prompt
from rich.table import Table
from rich.text import Text
from rich.progress import (
    BarColumn,
    Progress,
    SpinnerColumn,
    TextColumn,
    TimeRemainingColumn,
)
from rich.live import Live

# Import questionary untuk arrow key navigation
try:
    import questionary
    QUESTIONARY_AVAILABLE = True
except ImportError:
    QUESTIONARY_AVAILABLE = False

console = Console()

def print_header():
    """Menampilkan header aplikasi."""
    console.clear()
    title = Text("ProxySync Pro - Accurate Test", style="bold green", justify="center")
    credits = Text("Created by Kyugito666 & Gemini AI", style="bold magenta", justify="center")
    header_table = Table.grid(expand=True)
    header_table.add_row(title)
    header_table.add_row(credits)
    console.print(Panel(header_table, border_style="green"))
    console.print()

def display_main_menu():
    """Menampilkan menu utama interaktif dengan arrow key navigation."""
    console.print(Align.center(Text("--- MAIN MENU ---", style="bold cyan")))
    console.print()  # Spacing
    
    # Menu tanpa angka untuk display
    menu_display = [
        "Sinkronisasi IP Otorisasi Webshare",
        "Unduh Proksi dari Daftar API",
        "Konversi 'proxylist.txt'",
        "Jalankan Tes Akurat & Distribusi",
        "Kelola Path Target",
        "Keluar",
    ]
    
    if QUESTIONARY_AVAILABLE:
        # Gunakan questionary untuk arrow key navigation
        selected_option = questionary.select(
            "Pilih opsi (gunakan ↑/↓, Enter untuk memilih):",
            choices=menu_display,
            use_arrow_keys=True,
            style=questionary.Style([
                ('qmark', 'fg:cyan bold'),
                ('question', 'bold'),
                ('answer', 'fg:green bold'),
                ('pointer', 'fg:yellow bold'),
                ('highlighted', 'fg:yellow bold'),
                ('selected', 'fg:green'),
            ])
        ).ask()
        
        if selected_option is None:  # User pressed Ctrl+C
            return "6"
        
        # Map pilihan ke nomor
        option_map = {
            "Sinkronisasi IP Otorisasi Webshare": "1",
            "Unduh Proksi dari Daftar API": "2",
            "Konversi 'proxylist.txt'": "3",
            "Jalankan Tes Akurat & Distribusi": "4",
            "Kelola Path Target": "5",
            "Keluar": "6",
        }
        return option_map.get(selected_option, "6")
    else:
        # Fallback ke mode text input jika questionary tidak tersedia
        console.print("[yellow]⚠️  Install 'questionary' untuk arrow key navigation:[/yellow]")
        console.print("[dim]   pip install questionary[/dim]\n")
        
        for idx, option in enumerate(menu_display, 1):
            console.print(f"[{idx}] {option}")
        
        choice = Prompt.ask(
            "\n[bold yellow]Pilih opsi[/bold yellow]",
            choices=["1", "2", "3", "4", "5", "6"],
            default="6"
        )
        return choice

def fetch_from_api(url: str, api_key: str | None):
    """Fungsi pembantu untuk mengunduh dari satu URL API dengan mekanisme backoff."""
    max_retries = 3
    headers = {} 
    if api_key:
        headers['Authorization'] = f"Token {api_key}" 

    for attempt in range(max_retries):
        try:
            response = requests.get(url, headers=headers, timeout=60) 
            if response.status_code == 429:
                wait_time = 15 * (attempt + 1) 
                console.print(f"[bold yellow]Rate limit. Tunggu {wait_time}d...[/bold yellow]")
                time.sleep(wait_time)
                error_message = f"Rate limited (429) attempt {attempt + 1}"
                continue 
            response.raise_for_status() 
            content = response.text.strip()
            if content:
                if '\n' in content or re.match(r"^\d{1,3}(\.\d{1,3}){3}:\d+", content.splitlines()[0]):
                    return url, content.splitlines(), None
                else:
                    error_message = "Respons tidak valid (bukan proxy list?)"
                    break 
            else:
                error_message = "Respons kosong"
                break 
        except requests.exceptions.HTTPError as e:
             error_message = f"{e.response.status_code} Error: {str(e)}"
             break 
        except requests.exceptions.RequestException as e:
            error_message = f"Koneksi Gagal: {str(e)}"
            if attempt < max_retries - 1:
                console.print(f"[yellow]Koneksi gagal, coba lagi 5d... ({attempt+1}/{max_retries})[/yellow]")
                time.sleep(5) 
    return url, [], error_message

def run_sequential_api_downloads(download_targets: list[tuple[str, str | None]]):
    """Menjalankan unduhan API satu per satu."""
    all_proxies = []
    progress = Progress( SpinnerColumn(), TextColumn("[progress.description]{task.description}"), BarColumn(), TextColumn("[progress.percentage]{task.percentage:>3.0f}%"), console=console )
    total_targets = len(download_targets)
    with Live(progress):
        task = progress.add_task("[cyan]Mengunduh satu per satu...[/cyan]", total=total_targets)
        for i, (url, api_key) in enumerate(download_targets):
            progress.update(task, description=f"[cyan]Unduh ({i+1}/{total_targets})...[/cyan]")
            _, proxies, error = fetch_from_api(url, api_key) 
            if error:
                error_msg = str(error).splitlines()[0] 
                console.print(f"[bold red]✖ GAGAL[/bold red] {url[:50]}... [dim]({error_msg})[/dim]")
            else:
                console.print(f"[green]✔ Sukses[/green] {url[:50]}... ({len(proxies)} proksi)")
                all_proxies.extend(proxies)
            progress.update(task, advance=1)
            if i < total_targets - 1:
                jeda = 5; console.print(f"[grey]   Jeda {jeda}d...[/]"); time.sleep(jeda) 
    return all_proxies

def run_concurrent_checks_display(proxies, check_function, max_workers, fail_file):
    """Menampilkan progress bar dan laporan diagnostik."""
    good_proxies, failed_proxies_with_reason = [], []
    progress = Progress( SpinnerColumn(), TextColumn("[progress.description]{task.description}"), BarColumn(), TextColumn("[progress.percentage]{task.percentage:>3.0f}%"), TimeRemainingColumn(), console=console )
    with Live(progress):
        task = progress.add_task("[cyan]Tes Akurat...[/cyan]", total=len(proxies))
        with ThreadPoolExecutor(max_workers=max_workers) as executor:
            future_to_proxy = {executor.submit(check_function, p): p for p in proxies}
            for future in as_completed(future_to_proxy):
                proxy, is_good, message = future.result()
                if is_good: good_proxies.append(proxy)
                else: failed_proxies_with_reason.append((proxy, message))
                progress.update(task, advance=1)
    if failed_proxies_with_reason:
        with open(fail_file, "w") as f:
            for p, _ in failed_proxies_with_reason: f.write(p + "\n")
        console.print(f"\n[yellow]Simpan {len(failed_proxies_with_reason)} proksi gagal ke '{fail_file}'[/yellow]")
        error_table = Table(title="Laporan Gagal (Contoh)")
        error_table.add_column("Proksi", style="cyan"); error_table.add_column("Alasan", style="red")
        for proxy, reason in failed_proxies_with_reason[:10]:
            proxy_display = proxy.split('@')[1] if '@' in proxy else proxy
            error_table.add_row(proxy_display, reason)
        console.print(error_table)
    return good_proxies

def manage_paths_menu_display():
    console.print("[yellow]Fitur 'Kelola Path' belum ada.[/yellow]")
    time.sleep(2)
