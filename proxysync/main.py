import os
import random
import shutil
import time
import re
from concurrent.futures import ThreadPoolExecutor, as_completed
import requests
import ui # Mengimpor semua fungsi UI dari file ui.py

# --- Konfigurasi ---
PROXYLIST_SOURCE_FILE = "proxylist.txt"
PROXY_SOURCE_FILE = "proxy.txt"
PATHS_SOURCE_FILE = "../config/paths.txt" # Path relatif ke config utama
APILIST_SOURCE_FILE = "../config/apilist.txt" # Path relatif ke config utama
GITHUB_TOKENS_FILE = "../config/github_tokens.txt" # Path relatif ke config utama

FAIL_PROXY_FILE = "fail_proxy.txt"
SUCCESS_PROXY_FILE = "success_proxy.txt"
PROXY_BACKUP_FILE = "proxy_backup.txt" # Backup tetap di folder proxysync

# --- PERUBAHAN UTAMA UNTUK TES PROXY ---
PROXY_TIMEOUT = 25 # Waktu tunggu dinaikkan sedikit lagi
MAX_WORKERS = 10 # Jumlah tes simultan dikurangi lagi untuk GitHub API
GITHUB_API_TEST_URL = "https://api.github.com/zen" # Target tes baru
# --- AKHIR PERUBAHAN ---

API_DOWNLOAD_WORKERS = 1
RETRY_COUNT = 2

# --- Variabel Global untuk Token GitHub ---
GITHUB_TEST_TOKEN = None

# --- FUNGSI LOGIKA INTI ---

def load_github_token(file_path):
    """Memuat token GitHub pertama dari file github_tokens.txt."""
    global GITHUB_TEST_TOKEN
    try:
        if not os.path.exists(file_path):
            ui.console.print(f"[bold red]Error: File token '{file_path}' tidak ditemukan.[/bold red]")
            return False
        with open(file_path, "r") as f:
            lines = f.readlines()
        if len(lines) < 3:
            ui.console.print(f"[bold red]Error: Format '{file_path}' salah.[/bold red]")
            return False
        
        # Ambil token pertama dari baris ketiga
        first_token = lines[2].strip().split(',')[0].strip()
        if not first_token or not (first_token.startswith("ghp_") or first_token.startswith("github_pat_")):
             ui.console.print(f"[bold red]Error: Token pertama di '{file_path}' tidak valid.[/bold red]")
             return False
             
        GITHUB_TEST_TOKEN = first_token
        ui.console.print(f"[green]✓ Token GitHub untuk tes proxy berhasil dimuat.[/green]")
        return True
    except Exception as e:
        ui.console.print(f"[bold red]Gagal memuat token GitHub: {e}[/bold red]")
        return False

# ... (fungsi load_apis, download_proxies_from_api, convert_proxylist_to_http tetap sama) ...
def load_apis(file_path):
    """Memuat daftar URL API dari file."""
    if not os.path.exists(file_path):
        with open(file_path, "w") as f:
            f.write("# Masukkan URL API Anda di sini, satu per baris\n")
        return []
    with open(file_path, "r") as f:
        return [line.strip() for line in f if line.strip() and not line.strip().startswith("#")]

def download_proxies_from_api():
    """Mengosongkan proxylist.txt lalu mengunduh proksi dari semua API satu per satu."""
    if os.path.exists(PROXYLIST_SOURCE_FILE) and os.path.getsize(PROXYLIST_SOURCE_FILE) > 0:
        choice = ui.Prompt.ask(
            f"[bold yellow]File '{PROXYLIST_SOURCE_FILE}' berisi data. Hapus konten lama sebelum mengunduh?[/bold yellow]",
            choices=["y", "n"],
            default="y"
        ).lower()
        if choice == 'n':
            ui.console.print("[cyan]Operasi dibatalkan. Proksi tidak diunduh.[/cyan]")
            return

    try:
        # Kosongkan file proxylist.txt di folder proxysync
        with open(PROXYLIST_SOURCE_FILE, "w") as f:
            pass
        ui.console.print(f"[green]'{PROXYLIST_SOURCE_FILE}' telah siap untuk diisi data baru.[/green]\n")
    except IOError as e:
        ui.console.print(f"[bold red]Gagal membersihkan file: {e}[/bold red]")
        return

    api_urls = load_apis(APILIST_SOURCE_FILE)
    if not api_urls:
        ui.console.print(f"[bold red]File '{APILIST_SOURCE_FILE}' kosong atau tidak ditemukan.[/bold red]")
        ui.console.print(f"[yellow]Silakan isi file tersebut dengan URL API Anda.[/yellow]")
        return

    all_downloaded_proxies = ui.run_sequential_api_downloads(api_urls)

    if not all_downloaded_proxies:
        ui.console.print("\n[bold yellow]Tidak ada proksi yang berhasil diunduh dari semua API.[/bold yellow]")
        return

    try:
        # Tulis hasil download ke proxylist.txt di folder proxysync
        with open(PROXYLIST_SOURCE_FILE, "w") as f:
            for proxy in all_downloaded_proxies:
                f.write(proxy + "\n")

        ui.console.print(f"\n[bold green]✅ {len(all_downloaded_proxies)} proksi baru berhasil disimpan ke '{PROXYLIST_SOURCE_FILE}'[/bold green]")
    except IOError as e:
        ui.console.print(f"\n[bold red]Gagal menulis ke file '{PROXYLIST_SOURCE_FILE}': {e}[/bold red]")


def convert_proxylist_to_http():
    # File input tetap proxylist.txt di folder proxysync
    if not os.path.exists(PROXYLIST_SOURCE_FILE):
        ui.console.print(f"[bold red]Error: '{PROXYLIST_SOURCE_FILE}' tidak ditemukan.[/bold red]")
        return

    try:
        with open(PROXYLIST_SOURCE_FILE, "r") as f:
            lines = f.readlines()
    except Exception as e:
        ui.console.print(f"[bold red]Gagal membaca file '{PROXYLIST_SOURCE_FILE}': {e}[/bold red]")
        return

    cleaned_proxies = []
    for line in lines:
        line = line.strip()
        if not line:
            continue

        # Simple split, assumes http(s):// prefix exists or ip:port:user:pass or ip:port
        cleaned_proxies.append(line)


    if not cleaned_proxies:
        ui.console.print(f"[yellow]'{PROXYLIST_SOURCE_FILE}' kosong.[/yellow]")
        return

    ui.console.print(f"Mengonversi {len(cleaned_proxies)} proksi...")

    converted_proxies = []
    skipped_count = 0
    for p in cleaned_proxies:
        p = p.strip()
        if not p: continue

        # Cek jika sudah ada http:// atau https://
        if p.startswith("http://") or p.startswith("https://"):
            converted_proxies.append(p)
            continue

        parts = p.split(':')
        # Format ip:port
        if len(parts) == 2:
            # Basic IP/port validation (not perfect but better than nothing)
            if re.match(r"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$", parts[0]) and parts[1].isdigit():
                 converted_proxies.append(f"http://{parts[0]}:{parts[1]}")
            else:
                 # Check if domain:port
                 if '.' in parts[0] and parts[1].isdigit():
                      converted_proxies.append(f"http://{parts[0]}:{parts[1]}")
                 else:
                      skipped_count += 1
                      # ui.console.print(f"[dim]Format ip:port tidak valid: {p}[/dim]")
        # Format user:pass@ip:port
        elif len(parts) == 4 and '@' not in parts[0] and '@' not in parts[1]:
             # Cek format user:pass@ip:port
             # parts[0]=user, parts[1]=pass, parts[2]=ip, parts[3]=port
             if re.match(r"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$", parts[2]) and parts[3].isdigit():
                  converted_proxies.append(f"http://{parts[0]}:{parts[1]}@{parts[2]}:{parts[3]}")
             else:
                  # Check if user:pass@domain:port
                  if '.' in parts[2] and parts[3].isdigit():
                       converted_proxies.append(f"http://{parts[0]}:{parts[1]}@{parts[2]}:{parts[3]}")
                  else:
                       skipped_count += 1
                       # ui.console.print(f"[dim]Format user:pass@ip:port tidak valid: {p}[/dim]")
        # Format user:pass@host:port (jika @ sudah ada) - ini agak ambigu
        elif len(parts) >= 3 and '@' in p:
             # Asumsikan format sudah benar, tambahkan http:// jika belum ada
             converted_proxies.append(f"http://{p}")
        else:
            skipped_count += 1
            # ui.console.print(f"[yellow]Format tidak dikenali: {p}[/yellow]")

    if skipped_count > 0:
         ui.console.print(f"[yellow]{skipped_count} baris dilewati karena format tidak dikenali/valid.[/yellow]")


    if not converted_proxies:
        ui.console.print("[bold red]Tidak ada proksi yang dikonversi.[/bold red]")
        return

    try:
        # File output tetap proxy.txt di folder proxysync
        with open(PROXY_SOURCE_FILE, "w") as f:
            for proxy in converted_proxies:
                f.write(proxy + "\n")

        # Kosongkan proxylist.txt setelah konversi
        open(PROXYLIST_SOURCE_FILE, "w").close()

        ui.console.print(f"[bold green]✅ {len(converted_proxies)} proksi dikonversi dan disimpan ke '{PROXY_SOURCE_FILE}'.[/bold green]")
        ui.console.print(f"[bold cyan]'{PROXYLIST_SOURCE_FILE}' telah dikosongkan.[/bold cyan]")

    except Exception as e:
        ui.console.print(f"[bold red]Gagal menulis ke file: {e}[/bold red]")


def load_and_deduplicate_proxies(file_path):
    # File input tetap proxy.txt di folder proxysync
    if not os.path.exists(file_path): return []
    try:
        with open(file_path, "r") as f:
            proxies = [line.strip() for line in f if line.strip()]
    except Exception as e:
        ui.console.print(f"[bold red]Gagal membaca '{file_path}': {e}[/bold red]")
        return []
        
    unique_proxies = sorted(list(set(proxies)))
    duplicates_removed = len(proxies) - len(unique_proxies)
    if duplicates_removed > 0:
        ui.console.print(f"[yellow]Menghapus {duplicates_removed} duplikat.[/yellow]")
    
    # Tulis kembali hasil deduplikasi ke file yang sama
    try:
        with open(file_path, "w") as f:
            for proxy in unique_proxies: f.write(proxy + "\n")
    except Exception as e:
         ui.console.print(f"[bold red]Gagal menulis kembali '{file_path}' setelah deduplikasi: {e}[/bold red]")
         # Kembalikan list lama jika gagal nulis ulang, meskipun ada duplikat
         return proxies 
         
    return unique_proxies


def load_paths(file_path):
    # File input paths.txt dari ../config/
    if not os.path.exists(file_path):
        ui.console.print(f"[bold red]Error: File path '{file_path}' tidak ditemukan.[/bold red]")
        return []
    try:
        with open(file_path, "r") as f:
            # Menggunakan ../../ untuk path absolut dari folder proxysync ke root project
            project_root = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
            raw_paths = [line.strip() for line in f if line.strip() and not line.startswith("#")]
            absolute_paths = []
            invalid_paths = 0
            for p in raw_paths:
                # Coba resolve path relatif terhadap root project
                abs_p = os.path.join(project_root, p)
                if os.path.isdir(abs_p):
                    absolute_paths.append(abs_p)
                else:
                    invalid_paths += 1
                    ui.console.print(f"[yellow]Path tidak valid atau bukan direktori: {abs_p} (dari '{p}') [/yellow]")
            if invalid_paths > 0:
                 ui.console.print(f"[yellow]{invalid_paths} path dilewati karena tidak valid.[/yellow]")
            return absolute_paths
    except Exception as e:
        ui.console.print(f"[bold red]Gagal membaca '{file_path}': {e}[/bold red]")
        return []

def backup_file(file_path, backup_path):
    # File input proxy.txt di folder proxysync
    if os.path.exists(file_path):
        try:
            shutil.copy(file_path, backup_path)
            ui.console.print(f"[green]Backup dibuat: '{backup_path}'[/green]")
        except Exception as e:
             ui.console.print(f"[bold red]Gagal membuat backup '{backup_path}': {e}[/bold red]")


def check_proxy_final(proxy):
    """Fungsi pengetesan proksi baru yang menargetkan GitHub API."""
    if GITHUB_TEST_TOKEN is None:
        return proxy, False, "Token GitHub tidak dimuat"

    proxies_dict = {"http": proxy, "https": proxy}
    headers = {
        'User-Agent': 'ProxySync-Tester/1.0',
        'Authorization': f'Bearer {GITHUB_TEST_TOKEN}',
        'Accept': 'application/vnd.github.v3+json'
    }

    try:
        response = requests.get(GITHUB_API_TEST_URL, proxies=proxies_dict, timeout=PROXY_TIMEOUT, headers=headers)

        # Handle error spesifik GitHub
        if response.status_code == 401:
            # Jika token-nya yang salah, semua tes akan gagal dengan alasan ini
            # Kita bisa hentikan lebih awal jika mau, tapi biarkan jalan untuk log alasan
            return proxy, False, "GitHub Auth Gagal (401)"
        if response.status_code == 403:
            # Bisa jadi rate limit token, atau IP proxy diblokir GitHub
            return proxy, False, "GitHub Forbidden (403)"
        if response.status_code == 407:
            return proxy, False, "Proxy Membutuhkan Otentikasi (407)"
        if response.status_code == 429:
             return proxy, False, "GitHub Rate Limit (429)"

        # Cek jika sukses (200 OK)
        response.raise_for_status()

        # Cek konten (endpoint /zen mengembalikan string)
        if response.text and len(response.text) > 5: # Asumsi string Zen > 5 char
            return proxy, True, "OK"
        else:
            return proxy, False, "Respons GitHub tidak valid"

    except requests.exceptions.Timeout:
        return proxy, False, f"Timeout ({PROXY_TIMEOUT}s)"
    except requests.exceptions.ProxyError as e:
         # Log error proxy yang lebih spesifik jika bisa
         reason = str(e).split(':')[-1].strip() # Coba ambil bagian akhir error
         return proxy, False, f"Proxy Error ({reason[:30]})" # Batasi panjang error
    except requests.exceptions.RequestException as e:
        # Error koneksi umum
        reason = str(e.__class__.__name__) # Nama kelas exception (misal ConnectionError)
        return proxy, False, f"Koneksi Gagal ({reason})"


def distribute_proxies(proxies, paths):
    if not proxies or not paths:
        ui.console.print("[yellow]Tidak ada proksi valid atau path target. Distribusi dilewati.[/yellow]")
        return
        
    ui.console.print(f"\n[cyan]Mendistribusikan {len(proxies)} proksi valid ke {len(paths)} path...[/cyan]")
    for path in paths:
        # Path sudah absolut dari fungsi load_paths
        if not os.path.isdir(path):
            ui.console.print(f"  [yellow]✖ Lewati:[/yellow] Path tidak valid atau bukan direktori: {path}")
            continue
            
        # Logika cari proxies.txt atau proxy.txt
        file_name = "proxies.txt"
        file_path = os.path.join(path, file_name)
        if not os.path.exists(file_path):
             # Jika proxies.txt tidak ada, coba cari proxy.txt
             file_name = "proxy.txt"
             file_path = os.path.join(path, file_name)
             # Jika proxy.txt juga tidak ada, default ke proxy.txt
             # ui.console.print(f"   [dim]'{os.path.basename(path)}': Menggunakan 'proxy.txt'[/dim]")

        # Acak proxy untuk setiap path (penting!)
        proxies_shuffled = random.sample(proxies, len(proxies))
        try:
            with open(file_path, "w") as f:
                for proxy in proxies_shuffled: f.write(proxy + "\n")
            ui.console.print(f"  [green]✔[/green] Berhasil menulis ke [bold]{os.path.relpath(file_path, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))}[/bold]")
        except IOError as e:
            ui.console.print(f"  [red]✖[/red] Gagal menulis ke [bold]{os.path.relpath(file_path, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))}[/bold]: {e}")


def save_good_proxies(proxies, file_path):
    # File output success_proxy.txt di folder proxysync
    try:
        with open(file_path, "w") as f:
            for proxy in proxies:
                f.write(proxy + "\n")
        ui.console.print(f"\n[bold green]✅ {len(proxies)} proksi valid (lolos tes GitHub) berhasil disimpan ke '{file_path}'[/bold green]")
    except IOError as e:
        ui.console.print(f"\n[bold red]✖ Gagal menyimpan proksi ke '{file_path}': {e}[/bold red]")


def run_full_process():
    ui.print_header()

    # PERIKSA TOKEN DULU SEBELUM LANJUT
    if not load_github_token(GITHUB_TOKENS_FILE):
        ui.console.print("[bold red]Tes proxy tidak dapat dilanjutkan tanpa token GitHub yang valid.[/bold red]")
        return

    distribute_choice = ui.Prompt.ask(
        "[bold yellow]Distribusikan proksi yang valid ke semua path target?[/bold yellow]",
        choices=["y", "n"], default="y"
    ).lower()

    ui.console.print("-" * 40)
    ui.console.print("[bold cyan]Langkah 1: Backup & Bersihkan Proksi...[/bold cyan]")
    backup_file(PROXY_SOURCE_FILE, PROXY_BACKUP_FILE) # Backup proxy.txt
    proxies = load_and_deduplicate_proxies(PROXY_SOURCE_FILE) # Baca & dedup proxy.txt
    if not proxies:
        ui.console.print("[bold red]Proses berhenti: 'proxy.txt' kosong atau gagal dibaca.[/bold red]"); return
    ui.console.print(f"Siap menguji {len(proxies)} proksi unik ke GitHub API.")
    ui.console.print("-" * 40)

    ui.console.print("[bold cyan]Langkah 2: Menjalankan Tes Akurat ke GitHub API...[/bold cyan]")
    # Jalankan tes dengan fungsi baru
    good_proxies = ui.run_concurrent_checks_display(proxies, check_proxy_final, MAX_WORKERS, FAIL_PROXY_FILE)
    if not good_proxies:
        ui.console.print("[bold red]Proses berhenti: Tidak ada proksi yang berfungsi dengan GitHub API.[/bold red]"); return
    ui.console.print(f"[bold green]Ditemukan {len(good_proxies)} proksi yang berfungsi dengan GitHub API.[/bold green]")
    ui.console.print("-" * 40)

    if distribute_choice == 'y':
        ui.console.print("[bold cyan]Langkah 3: Distribusi...[/bold cyan]")
        paths = load_paths(PATHS_SOURCE_FILE) # Baca paths.txt dari ../config/
        if not paths:
            ui.console.print("[bold red]Proses berhenti: 'paths.txt' kosong atau path tidak valid.[/bold red]"); return
        distribute_proxies(good_proxies, paths) # Distribusikan proxy bagus
        # Simpan juga salinan proxy bagus ke success_proxy.txt
        save_good_proxies(good_proxies, SUCCESS_PROXY_FILE)
        ui.console.print("\n[bold green]✅ Semua tugas selesai![/bold green]")
    else:
        ui.console.print("[bold cyan]Langkah 3: Menyimpan proksi valid...[/bold cyan]")
        save_good_proxies(good_proxies, SUCCESS_PROXY_FILE) # Simpan proxy bagus


def main():
    # Pindah ke direktori skrip agar path relatif bekerja
    os.chdir(os.path.dirname(os.path.abspath(__file__)))

    while True:
        ui.print_header()
        choice = ui.display_main_menu()
        if choice == "1":
            download_proxies_from_api()
            ui.Prompt.ask("\n[bold]Tekan Enter untuk kembali...[/bold]")
        elif choice == "2":
            convert_proxylist_to_http()
            ui.Prompt.ask("\n[bold]Tekan Enter untuk kembali...[/bold]")
        elif choice == "3":
            run_full_process()
            ui.Prompt.ask("\n[bold]Tekan Enter untuk kembali...[/bold]")
        elif choice == "4":
            ui.manage_paths_menu_display() # Ini masih placeholder di ui.py
        elif choice == "5":
            ui.console.print("[bold cyan]Sampai jumpa![/bold cyan]"); break

if __name__ == "__main__":
    main()
