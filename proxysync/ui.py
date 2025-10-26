import time
import requests
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
    """Menampilkan menu utama."""
    menu_table = Table(title="Main Menu", show_header=False, border_style="magenta")
    menu_table.add_column("Option", style="cyan", width=5)
    menu_table.add_column("Description")
    menu_table.add_row("[1]", "Sinkronisasi IP Otorisasi Webshare")
    menu_table.add_row("[2]", "Unduh Proksi dari Daftar API")
    menu_table.add_row("[3]", "Konversi 'proxylist.txt'")
    menu_table.add_row("[4]", "Jalankan Tes Akurat & Distribusi")
    menu_table.add_row("[5]", "Kelola Path Target")
    menu_table.add_row("[6]", "Keluar")
    console.print(Align.center(menu_table))
    return Prompt.ask("Pilih opsi", choices=["1", "2", "3", "4", "5", "6"], default="6")

# === PERUBAHAN DOWNLOAD v2 ===
def fetch_from_api(url: str, api_key: str | None):
    """Fungsi pembantu untuk mengunduh dari satu URL API dengan mekanisme backoff."""
    max_retries = 3
    headers = {'Accept': 'text/plain'} # Header dasar untuk download
    if api_key:
        headers['Authorization'] = f"Token {api_key}" # Tambahkan Auth jika ada key

    for attempt in range(max_retries):
        try:
            # Gunakan header yang sudah disiapkan
            response = requests.get(url, headers=headers, timeout=60) # Timeout dinaikkan ke 60s
            
            # Cek Rate Limit dulu
            if response.status_code == 429:
                 # PERPANJANG JEDA: 15s, 30s, 45s
                wait_time = 15 * (attempt + 1) 
                console.print(f"[bold yellow]Rate limit terdeteksi. Menunggu {wait_time} detik...[/bold yellow]")
                time.sleep(wait_time)
                error_message = f"Rate limited (429) on attempt {attempt + 1}"
                continue # Coba lagi setelah jeda
            
            # Jika bukan 429, baru cek error lain atau sukses
            response.raise_for_status() 
            content = response.text.strip()
            if content:
                return url, content.splitlines(), None
            else:
                error_message = "API tidak mengembalikan konten (respons kosong)"
                break # Jangan retry jika respons kosong

        except requests.exceptions.HTTPError as e:
            # Error HTTP selain 429 (misal 400 Bad Request, 401 Unauthorized, 5xx Server Error)
            error_message = f"{e.response.status_code} Client/Server Error: {str(e)}"
            break # Jangan retry untuk error ini, kemungkinan URL/Key salah

        except requests.exceptions.RequestException as e:
            # Error koneksi (timeout, DNS, etc.)
            error_message = f"Koneksi Gagal: {str(e)}"
            if attempt < max_retries - 1:
                console.print(f"[yellow]Koneksi gagal, mencoba lagi dalam 5 detik... ({attempt+1}/{max_retries})[/yellow]")
                time.sleep(5) # Jeda singkat untuk error koneksi
            # Biarkan loop lanjut ke attempt berikutnya

    # Jika loop selesai tanpa return sukses
    return url, [], error_message

def run_sequential_api_downloads(download_targets: list[tuple[str, str | None]]):
    """Menjalankan unduhan API satu per satu."""
    all_proxies = []
    progress = Progress(
        SpinnerColumn(),
        TextColumn("[progress.description]{task.description}"),
        BarColumn(),
        TextColumn("[progress.percentage]{task.percentage:>3.0f}%"),
        console=console
    )
    total_targets = len(download_targets)
    with Live(progress):
        task = progress.add_task("[cyan]Mengunduh satu per satu...[/cyan]", total=total_targets)
        for i, (url, api_key) in enumerate(download_targets):
            progress.update(task, description=f"[cyan]Mengunduh ({i+1}/{total_targets})...[/cyan]")
            
            # Panggil fetch_from_api dengan URL dan API key
            _, proxies, error = fetch_from_api(url, api_key) 
            
            if error:
                error_msg = str(error).splitlines()[0] # Ambil baris pertama error
                console.print(f"[bold red]✖ GAGAL[/bold red] dari {url[:50]}... [dim]({error_msg})[/dim]")
            else:
                console.print(f"[green]✔ Berhasil[/green] dari {url[:50]}... ({len(proxies)} proksi)")
                all_proxies.extend(proxies)
                
            progress.update(task, advance=1)
            
            # JEDA ANTAR REQUEST (di luar retry)
            # Jika bukan request terakhir, beri jeda
            if i < total_targets - 1:
                # PERPANJANG JEDA ANTAR AKUN
                jeda = 5 
                console.print(f"[grey]   Jeda {jeda} detik sebelum akun berikutnya...[/]")
                time.sleep(jeda) 

    return all_proxies
# === AKHIR PERUBAHAN DOWNLOAD v2 ===


def run_concurrent_checks_display(proxies, check_function, max_workers, fail_file):
    """Menampilkan progress bar dan laporan diagnostik."""
    good_proxies, failed_proxies_with_reason = [], []
    progress = Progress(
        SpinnerColumn(),
        TextColumn("[progress.description]{task.description}"),
        BarColumn(),
        TextColumn("[progress.percentage]{task.percentage:>3.0f}%"),
        TimeRemainingColumn(),
        console=console,
    )
    with Live(progress):
        task = progress.add_task("[cyan]Menjalankan Tes Akurat...[/cyan]", total=len(proxies))
        with ThreadPoolExecutor(max_workers=max_workers) as executor:
            future_to_proxy = {executor.submit(check_function, p): p for p in proxies}
            for future in as_completed(future_to_proxy):
                proxy, is_good, message = future.result()
                if is_good:
                    good_proxies.append(proxy)
                else:
                    failed_proxies_with_reason.append((proxy, message))
                progress.update(task, advance=1)

    if failed_proxies_with_reason:
        with open(fail_file, "w") as f:
            for p, _ in failed_proxies_with_reason: f.write(p + "\n")
        console.print(f"\n[yellow]Menyimpan {len(failed_proxies_with_reason)} proksi gagal ke '{fail_file}'[/yellow]")

        error_table = Table(title="Laporan Diagnostik Kegagalan (Contoh)")
        error_table.add_column("Proksi (IP:Port)", style="cyan")
        error_table.add_column("Alasan Kegagalan", style="red")
        for proxy, reason in failed_proxies_with_reason[:10]:
            proxy_display = proxy.split('@')[1] if '@' in proxy else proxy
            error_table.add_row(proxy_display, reason)
        console.print(error_table)
    return good_proxies

def manage_paths_menu_display():
    """UI untuk mengelola paths.txt."""
    console.print("[yellow]Fitur 'Kelola Path' belum diimplementasikan.[/yellow]")
    time.sleep(2)
