print("DEBUG: Starting main.py execution", flush=True)
import os
import random
import shutil
import time
import re
import sys
import json
import argparse # <-- Import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
import requests
import ui # Mengimpor semua fungsi UI dari file ui.py
from pathlib import Path # Import Path

# --- Konfigurasi ---
# === PERBAIKAN PATH: Definisikan SCRIPT_DIR dan buat path absolut/relatif dari sana ===
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, '..', 'config'))

PROXYLIST_SOURCE_FILE = os.path.join(SCRIPT_DIR, "proxylist.txt") # Input format asli
PROXY_SOURCE_FILE = os.path.join(SCRIPT_DIR, "proxy.txt")         # Input format http://

PATHS_SOURCE_FILE = os.path.join(CONFIG_DIR, "paths.txt")
APILIST_SOURCE_FILE = os.path.join(CONFIG_DIR, "apilist.txt")     # Daftar URL download
GITHUB_TOKENS_FILE = os.path.join(CONFIG_DIR, "github_tokens.txt")
WEBSHARE_APIKEYS_FILE = os.path.join(CONFIG_DIR, "apikeys.txt")   # API Keys Webshare

FAIL_PROXY_FILE = os.path.join(SCRIPT_DIR, "fail_proxy.txt")       # Output proxy gagal
SUCCESS_PROXY_FILE = os.path.join(SCRIPT_DIR, "success_proxy.txt") # Output proxy sukses
PROXY_BACKUP_FILE = os.path.join(SCRIPT_DIR, "proxy_backup.txt")   # Backup proxy.txt
# === AKHIR PERBAIKAN PATH ===

# --- Konfigurasi Webshare ---
WEBSHARE_AUTH_URL = "https://proxy.webshare.io/api/v2/proxy/ipauthorization/"
WEBSHARE_SUB_URL = "https://proxy.webshare.io/api/v2/subscription/"
WEBSHARE_CONFIG_URL = "https://proxy.webshare.io/api/v2/proxy/config/"
WEBSHARE_PROFILE_URL = "https://proxy.webshare.io/api/v2/profile/"
WEBSHARE_DOWNLOAD_URL_BASE = "https://proxy.webshare.io/api/v2/proxy/list/download/{token}/-/any/username/direct/-/"
WEBSHARE_DOWNLOAD_URL_FORMAT = WEBSHARE_DOWNLOAD_URL_BASE + "?plan_id={plan_id}"
IP_CHECK_SERVICE_URL = "https://api.ipify.org?format=json"

# --- Konfigurasi Tes Proxy ---
PROXY_TIMEOUT = 25
MAX_WORKERS = 10
GITHUB_API_TEST_URL = "https://api.github.com/zen"

# Timeout API Webshare
WEBSHARE_API_TIMEOUT = 60

# --- Variabel Global ---
GITHUB_TEST_TOKEN = None

# --- Fungsi Utility ---
def load_github_token(file_path):
    global GITHUB_TEST_TOKEN
    try:
        if not os.path.exists(file_path): ui.console.print(f"[bold red]Error: '{file_path}' tidak ada.[/bold red]"); return False
        with open(file_path, "r") as f: lines = f.readlines()
        if len(lines) < 3: ui.console.print(f"[bold red]Error: Format '{file_path}' salah.[/bold red]"); return False
        tokens_line = lines[2].strip()
        # Perbaikan kecil: Handle jika baris token kosong
        if not tokens_line: ui.console.print(f"[bold red]Error: Baris 3 (tokens) di '{file_path}' kosong.[/bold red]"); return False
        first_token = tokens_line.split(',')[0].strip()
        if not first_token or not (first_token.startswith("ghp_") or first_token.startswith("github_pat_")): ui.console.print(f"[bold red]Error: Token awal di baris 3 '{file_path}' invalid.[/bold red]"); return False
        GITHUB_TEST_TOKEN = first_token
        ui.console.print(f"[green]✓ Token GitHub untuk testing OK (dari baris 3).[/green]"); return True
    except IndexError:
         ui.console.print(f"[bold red]Error: Baris 3 (tokens) di '{file_path}' format salah.[/bold red]"); return False
    except Exception as e: ui.console.print(f"[bold red]Gagal load token GitHub: {e}[/bold red]"); return False

def load_webshare_apikeys(file_path):
    if not os.path.exists(file_path):
        try:
            os.makedirs(os.path.dirname(file_path), exist_ok=True)
            with open(file_path, "w") as f: f.write("# API key Webshare, 1 per baris\n")
            ui.console.print(f"[yellow]'{os.path.basename(file_path)}' dibuat di '{os.path.dirname(file_path)}'. Isi API key Webshare Anda.[/yellow]")
        except IOError as e:
            ui.console.print(f"[bold red]Gagal membuat file '{file_path}': {e}[/bold red]")
        return []
    try:
        with open(file_path, "r") as f:
            return [line.strip() for line in f if line.strip() and not line.strip().startswith("#")]
    except IOError as e:
        ui.console.print(f"[bold red]Gagal membaca file '{file_path}': {e}[/bold red]")
        return []


def get_current_public_ip():
    ui.console.print("1. Mengecek IP publik saat ini...")
    try:
        response = requests.get(IP_CHECK_SERVICE_URL, timeout=WEBSHARE_API_TIMEOUT)
        response.raise_for_status()
        current_ip = response.json()["ip"]
        ui.console.print(f"   -> [bold green]IP publik saat ini: {current_ip}[/bold green]")
        return current_ip
    except requests.RequestException as e:
        ui.console.print(f"   -> [bold red]ERROR: Gagal mengecek IP publik: {e}[/bold red]", file=sys.stderr)
        return None
    except (KeyError, json.JSONDecodeError) as e:
         ui.console.print(f"   -> [bold red]ERROR: Respons IP publik tidak valid: {e}[/bold red]", file=sys.stderr)
         return None

def get_account_email(session: requests.Session) -> str:
    try:
        response = session.get(WEBSHARE_PROFILE_URL, timeout=WEBSHARE_API_TIMEOUT)
        if response.status_code == 401: return "[bold red]API Key Invalid[/]"
        response.raise_for_status()
        data = response.json()
        email = data.get("email")
        if email: return email
        else: return "[yellow]Email tidak tersedia[/]"
    except requests.exceptions.HTTPError as e: return f"[bold red]HTTP Error ({e.response.status_code})[/]"
    except requests.RequestException: return "[bold red]Koneksi Error[/]"
    except Exception: return "[bold red]Parsing Error[/]"

def get_target_plan_id(session: requests.Session):
    ui.console.print("2. Mengecek Plan ID Webshare (via /config/)...")
    try:
        response = session.get(WEBSHARE_CONFIG_URL, timeout=WEBSHARE_API_TIMEOUT)
        if response.status_code == 401: ui.console.print("   -> [bold red]ERROR: API Key Webshare invalid.[/bold red]"); return None
        response.raise_for_status()
        data = response.json()
        plan_id = data.get("id")
        if plan_id:
            plan_id_str = str(plan_id)
            ui.console.print(f"   -> [green]OK: Plan ID ditemukan: {plan_id_str}[/green]")
            return plan_id_str
        else:
            ui.console.print("   -> [bold red]ERROR: Respons API /config/ tidak mengandung field 'id'.[/bold red]")
            return None
    except requests.exceptions.HTTPError as e:
        error_detail = ""
        try: error_detail = f" - {e.response.json().get('detail', e.response.text)}"
        except: error_detail = f" - {e.response.text}"
        ui.console.print(f"   -> [bold red]ERROR HTTP {e.response.status_code} saat akses /config/{error_detail}[/bold red]")
        return None
    except requests.RequestException as e:
        ui.console.print(f"   -> [bold red]ERROR Koneksi saat akses /config/: {e}[/bold red]")
        return None
    except Exception as e:
         ui.console.print(f"   -> [bold red]ERROR tak terduga saat mencari Plan ID: {e}[/bold red]")
         return None


def get_authorized_ips(session: requests.Session, plan_id: str):
    ui.console.print("3. Mengecek IP yang sudah terdaftar...")
    params = {"plan_id": plan_id}
    ip_to_id_map = {}
    try:
        response = session.get(WEBSHARE_AUTH_URL, params=params, timeout=WEBSHARE_API_TIMEOUT)
        response.raise_for_status()
        results = response.json().get("results", [])
        for item in results:
            ip = item.get("ip_address")
            auth_id = item.get("id")
            if ip and auth_id: ip_to_id_map[ip] = auth_id

        if not ip_to_id_map:
            ui.console.print("   -> Tidak ada IP lama yang terdaftar untuk plan ini.")
        else:
            ui.console.print(f"   -> IP lama yang terdaftar: {', '.join(ip_to_id_map.keys())}")
        return ip_to_id_map
    except requests.RequestException as e:
        ui.console.print(f"   -> [bold red]ERROR: Gagal mengecek IP lama: {e}[/bold red]")
        return {}

def remove_ip(session: requests.Session, ip: str, authorization_id: int, plan_id: str):
    ui.console.print(f"   -> Menghapus IP lama: {ip} (ID: {authorization_id})...", end=" ")
    params = {"plan_id": plan_id}
    delete_url = f"{WEBSHARE_AUTH_URL}{authorization_id}/"
    try:
        response = session.delete(delete_url, params=params, timeout=WEBSHARE_API_TIMEOUT)
        if response.status_code == 204:
            ui.console.print(f"[green]OK[/green]")
            return True
        else:
            error_detail = ""
            try: error_detail = f" - {response.json().get('detail', response.text)}"
            except: error_detail = f" - {response.text}"
            ui.console.print(f"[bold red]GAGAL ({response.status_code}){error_detail}[/bold red]")
            return False
    except requests.RequestException as e:
        ui.console.print(f"[bold red]GAGAL (Koneksi Error): {e}[/bold red]")
        return False

def add_ip(session: requests.Session, ip: str, plan_id: str):
    ui.console.print(f"   -> Menambahkan IP baru: {ip}...", end=" ")
    params = {"plan_id": plan_id}
    payload = {"ip_address": ip}
    try:
        response = session.post(WEBSHARE_AUTH_URL, json=payload, params=params, timeout=WEBSHARE_API_TIMEOUT)
        if response.status_code == 201:
            ui.console.print(f"[green]OK[/green]")
            return True
        else:
            error_detail = ""
            try: error_detail = f" - {response.json().get('detail', response.text)}"
            except: error_detail = f" - {response.text}"
            ui.console.print(f"[bold red]GAGAL ({response.status_code}){error_detail}[/bold red]")
            return False
    except requests.RequestException as e:
        ui.console.print(f"[bold red]GAGAL (Koneksi Error): {e}[/bold red]")
        return False

def load_apis_from_file(file_path):
    """Memuat daftar URL API dari file teks."""
    urls = set()
    if not os.path.exists(file_path):
        try:
            os.makedirs(os.path.dirname(file_path), exist_ok=True)
            with open(file_path, "w") as f:
                f.write("# Masukkan URL download proxy Webshare manual di sini, SATU per baris\n")
            ui.console.print(f"[yellow]'{os.path.basename(file_path)}' dibuat di '{os.path.dirname(file_path)}'. Anda bisa isi URL manual jika perlu.[/yellow]")
        except IOError as e:
            ui.console.print(f"[bold red]Gagal membuat file '{file_path}': {e}[/bold red]")
        return list(urls)

    try:
        with open(file_path, "r") as f:
            for line in f:
                line = line.strip()
                if line and not line.startswith("#"):
                    if "proxy.webshare.io/api/v2/proxy/list/download/" in line:
                         urls.add(line)
                    else:
                         ui.console.print(f"[yellow]   Format URL tidak valid di '{os.path.basename(file_path)}', dilewati: {line[:50]}...[/yellow]")
    except IOError as e:
        ui.console.print(f"[bold red]Gagal membaca file '{file_path}': {e}[/bold red]")

    return list(urls)

def save_discovered_url(file_path, url):
    """Menyimpan URL baru ke file apilist.txt jika belum ada."""
    try:
        # Baca ulang setiap kali untuk memastikan data terbaru
        existing_urls = set()
        if os.path.exists(file_path):
            with open(file_path, "r") as f_read:
                for line in f_read:
                    line = line.strip()
                    if line and not line.startswith("#"):
                        existing_urls.add(line)

        if url not in existing_urls:
                # Pastikan direktori ada sebelum menulis
                os.makedirs(os.path.dirname(file_path), exist_ok=True)
                with open(file_path, "a") as f_append:
                    # Tambah newline di awal jika file tidak kosong DAN tidak diakhiri newline
                    needs_newline = False
                    if os.path.getsize(file_path) > 0:
                         with open(file_path, 'rb') as f_check:
                              f_check.seek(-1, os.SEEK_END)
                              if f_check.read() != b'\n':
                                   needs_newline = True

                    if needs_newline:
                        f_append.write("\n")
                    f_append.write(f"{url}\n")
                ui.console.print(f"   -> [green]URL baru disimpan ke '{os.path.basename(file_path)}'[/green]")
                return True
        else:
             ui.console.print(f"   -> [dim]URL sudah ada di '{os.path.basename(file_path)}', simpan dilewati.[/dim]")
             return False # Return False jika sudah ada
    except IOError as e:
        ui.console.print(f"[bold red]   Gagal menyimpan URL ke '{os.path.basename(file_path)}': {e}[/bold red]")
        return False


def run_webshare_ip_sync():
    ui.print_header()
    ui.console.print("[bold cyan]--- Sinkronisasi IP Otorisasi Webshare ---[/bold cyan]")
    api_keys = load_webshare_apikeys(WEBSHARE_APIKEYS_FILE)
    if not api_keys: ui.console.print(f"[bold red]'{os.path.basename(WEBSHARE_APIKEYS_FILE)}' kosong atau tidak ditemukan.[/bold red]"); return False

    new_ip = get_current_public_ip()
    if not new_ip: ui.console.print("[bold red]Gagal mendapatkan IP publik saat ini. Proses dibatalkan.[/bold red]"); return False

    ui.console.print(f"\nSinkronisasi IP [bold]{new_ip}[/bold] ke [bold]{len(api_keys)}[/bold] akun Webshare...")
    overall_success = True

    for api_key in api_keys:
        account_email_info = "[grey]Mencoba mendapatkan email...[/]"
        try:
            with requests.Session() as email_session:
                email_session.headers.update({"Authorization": f"Token {api_key}", "Accept": "application/json"})
                account_email_info = get_account_email(email_session)
        except Exception: account_email_info = "[bold red]Error[/]"
        ui.console.print(f"\n--- Memproses Key: [...{api_key[-6:]}] (Email: {account_email_info}) ---")

        account_success = False
        with requests.Session() as session:
            session.headers.update({"Authorization": f"Token {api_key}", "Accept": "application/json"})
            try:
                plan_id = get_target_plan_id(session)
                if not plan_id:
                    ui.console.print(f"   -> [bold red]Gagal mendapatkan Plan ID. Akun ini dilewati.[/bold red]")
                    overall_success = False
                    continue

                authorized_ips_map = get_authorized_ips(session, plan_id)
                existing_ips = list(authorized_ips_map.keys())

                if new_ip in existing_ips:
                    ui.console.print(f"   -> [green]IP baru ({new_ip}) sudah terdaftar. Tidak perlu tindakan.[/green]")
                    account_success = True
                else:
                    ui.console.print("\n4. Menghapus IP lama (jika ada)...")
                    remove_success = True
                    if not existing_ips:
                        ui.console.print("   -> Tidak ada IP lama untuk dihapus.")
                    else:
                        ips_to_remove = list(authorized_ips_map.items())
                        for ip_to_delete, auth_id_to_delete in ips_to_remove:
                            if not remove_ip(session, ip_to_delete, auth_id_to_delete, plan_id):
                                remove_success = False
                                overall_success = False
                        if remove_success: ui.console.print("   -> Semua IP lama berhasil dihapus.")
                        else: ui.console.print("   -> [yellow]Beberapa IP lama gagal dihapus.[/yellow]")

                    ui.console.print("\n5. Menambahkan IP baru...")
                    add_success = add_ip(session, new_ip, plan_id)
                    if add_success:
                        ui.console.print("   -> Verifikasi penambahan IP...")
                        time.sleep(3)
                        updated_ips_map = get_authorized_ips(session, plan_id) # Baca ulang setelah add
                        if new_ip in updated_ips_map:
                            ui.console.print(f"   -> [green]Verifikasi OK: IP {new_ip} berhasil ditambahkan.[/green]")
                            account_success = True
                        else:
                             ui.console.print(f"   -> [bold red]Verifikasi GAGAL: IP {new_ip} tidak ditemukan setelah proses add![/bold red]")
                             overall_success = False
                    else:
                        overall_success = False

            except Exception as e:
                ui.console.print(f"   -> [bold red]!!! TERJADI ERROR tak terduga saat memproses akun ini: {e}. Lanjut ke akun berikutnya.[/bold red]")
                overall_success = False
                continue

        if not account_success:
             ui.console.print(f"   -> [yellow]Proses untuk key [...{api_key[-6:]}] tidak sepenuhnya berhasil.[/yellow]")

    ui.console.print("\n-------------------------------------------")
    if overall_success:
        ui.console.print("[bold green]✅ Sinkronisasi IP selesai tanpa error kritikal.[/bold green]")
    else:
        ui.console.print("[bold yellow]⚠️ Sinkronisasi IP selesai, namun terjadi beberapa kegagalan. Periksa log di atas.[/bold yellow]")
    return overall_success


def get_webshare_download_url(session: requests.Session, plan_id: str):
    ui.console.print("   -> Mencari URL download proxy (via /config/)...")
    params = {"plan_id": plan_id}
    try:
        response = session.get(WEBSHARE_CONFIG_URL, params=params, timeout=WEBSHARE_API_TIMEOUT)
        response.raise_for_status()
        data = response.json()
        token = data.get("proxy_list_download_token")
        if not token:
            ui.console.print("   -> [bold red]ERROR: 'proxy_list_download_token' tidak ditemukan dalam respons API.[/bold red]")
            return None
        download_url = WEBSHARE_DOWNLOAD_URL_FORMAT.format(token=token, plan_id=plan_id)
        ui.console.print(f"   -> [green]OK: URL download ditemukan.[/green]")
        return download_url
    except requests.exceptions.HTTPError as e:
        error_detail = ""
        try: error_detail = f" - {e.response.json().get('detail', e.response.text)}"
        except: error_detail = f" - {e.response.text}"
        ui.console.print(f"   -> [bold red]ERROR HTTP {e.response.status_code} saat akses /config/{error_detail}[/bold red]")
        return None
    except requests.RequestException as e:
        ui.console.print(f"   -> [bold red]ERROR Koneksi saat akses /config/: {e}[/bold red]")
        return None
    except Exception as e:
        ui.console.print(f"   -> [bold red]ERROR tak terduga saat mencari URL: {e}[/bold red]")
        return None

def download_proxies_from_api(is_auto=False, get_urls_only=False):
    ui.print_header()
    if get_urls_only:
        ui.console.print("[bold cyan]--- Discover & Simpan URL Download Proxy ---[/bold cyan]")
    else:
        ui.console.print("[bold cyan]--- Unduh Proksi dari API ---[/bold cyan]")

    existing_api_urls = load_apis_from_file(APILIST_SOURCE_FILE)
    ui.console.print(f"Memuat URL dari '{os.path.basename(APILIST_SOURCE_FILE)}': {len(existing_api_urls)} URL ditemukan.")

    discovered_urls_from_keys = {}
    urls_to_process = set(existing_api_urls) # Mulai dengan URL dari file
    newly_saved_count = 0
    discovery_failed = False

    if get_urls_only or is_auto:
        ui.console.print(f"\n[bold]Mencoba discover URL baru dari '{os.path.basename(WEBSHARE_APIKEYS_FILE)}'...[/bold]")
        api_keys = load_webshare_apikeys(WEBSHARE_APIKEYS_FILE)
        if not api_keys:
            ui.console.print(f"[yellow]'{os.path.basename(WEBSHARE_APIKEYS_FILE)}' kosong atau tidak ditemukan.[/yellow]")
            if is_auto: discovery_failed = True
        else:
            ui.console.print(f"Ditemukan {len(api_keys)} API Key untuk dicek.")
            processed_keys_count = 0
            for api_key in api_keys:
                account_email_info = "[grey]Mencoba mendapatkan email...[/]"
                try:
                    with requests.Session() as email_session:
                        email_session.headers.update({"Authorization": f"Token {api_key}", "Accept": "application/json"})
                        account_email_info = get_account_email(email_session)
                except Exception: account_email_info = "[bold red]Error[/]"
                ui.console.print(f"\n--- Mengecek Key: [...{api_key[-6:]}] (Email: {account_email_info}) ---")

                with requests.Session() as session:
                    session.headers.update({"Authorization": f"Token {api_key}", "Accept": "application/json"})
                    try:
                        plan_id = get_target_plan_id(session)
                        if not plan_id:
                            ui.console.print(f"   -> [bold red]Gagal mendapatkan Plan ID. Akun ini dilewati.[/bold red]")
                            discovery_failed = True
                            continue
                        download_url = get_webshare_download_url(session, plan_id)
                        if download_url:
                            discovered_urls_from_keys[api_key] = download_url
                            # Coba simpan SEKARANG, dan update urls_to_process JIKA berhasil disimpan
                            if save_discovered_url(APILIST_SOURCE_FILE, download_url):
                                 urls_to_process.add(download_url) # Tambahkan ke set yg akan di-download HANYA jika baru
                                 newly_saved_count += 1
                            elif download_url in existing_api_urls: # Jika sudah ada, tetap tambahkan ke set download
                                 urls_to_process.add(download_url)

                        else:
                            ui.console.print("   -> [yellow]Gagal mendapatkan URL download untuk key ini. Dilewati.[/yellow]")
                            discovery_failed = True
                    except Exception as e:
                        ui.console.print(f"   -> [bold red]!!! TERJADI ERROR saat discover URL: {e}[/bold red]")
                        discovery_failed = True
                processed_keys_count += 1
                if processed_keys_count < len(api_keys):
                     time.sleep(1)

            ui.console.print(f"\nSelesai discover URL dari API keys. {len(discovered_urls_from_keys)} URL ditemukan.")
            if newly_saved_count > 0:
                 ui.console.print(f"[green]{newly_saved_count} URL baru disimpan ke '{os.path.basename(APILIST_SOURCE_FILE)}'.[/green]")
    
    elif not get_urls_only and not is_auto:
        ui.console.print(f"\n[dim]Mode Unduh: Melewati discovery API Key.[/dim]")
        ui.console.print(f"[dim]Hanya akan mengunduh dari URL di '{os.path.basename(APILIST_SOURCE_FILE)}'.[/dim]")

    if get_urls_only:
        ui.console.print("\n-------------------------------------------")
        if not discovery_failed:
             ui.console.print("[bold green]✅ Proses discover dan simpan URL selesai.[/bold green]")
             return True
        else:
             ui.console.print("[bold yellow]⚠️ Proses discover URL selesai, namun terjadi beberapa kegagalan.[/bold yellow]")
             return False


    if not urls_to_process:
        ui.console.print("\n[bold red]Tidak ada URL API (baik dari file atau hasil discovery baru) untuk mengunduh proxy.[/bold red]")
        return False

    if not is_auto and os.path.exists(PROXYLIST_SOURCE_FILE) and os.path.getsize(PROXYLIST_SOURCE_FILE) > 0:
        choice = ui.Prompt.ask(f"[bold yellow]File '{os.path.basename(PROXYLIST_SOURCE_FILE)}' sudah ada isinya. Hapus dan timpa? (y/n)[/bold yellow]", choices=["y", "n"], default="y").lower()
        if choice == 'n': ui.console.print("[cyan]Operasi unduh dibatalkan.[/cyan]"); return False
    try:
        # Pastikan direktori ada sebelum menulis
        os.makedirs(os.path.dirname(PROXYLIST_SOURCE_FILE), exist_ok=True)
        with open(PROXYLIST_SOURCE_FILE, "w") as f: pass
        ui.console.print(f"\n[green]File '{os.path.basename(PROXYLIST_SOURCE_FILE)}' siap diisi.[/green]")
    except IOError as e: ui.console.print(f"[bold red]Gagal mengosongkan '{PROXYLIST_SOURCE_FILE}': {e}[/bold red]"); return False

    download_targets_final = [(url, None) for url in urls_to_process]
    ui.console.print(f"\n[bold cyan]Siap mengunduh dari {len(download_targets_final)} URL...[/bold cyan]")

    all_downloaded_proxies = ui.run_sequential_api_downloads(download_targets_final)
    if not all_downloaded_proxies:
        ui.console.print("\n[bold yellow]Tidak ada proxy yang berhasil diunduh.[/bold yellow]")
        return False

    try:
        unique_proxies = sorted(list(set(all_downloaded_proxies)))
        duplicates_removed = len(all_downloaded_proxies) - len(unique_proxies)
        with open(PROXYLIST_SOURCE_FILE, "w") as f:
            for proxy in unique_proxies: f.write(proxy + "\n")
        ui.console.print(f"\n[bold green]✅ {len(unique_proxies)} proxy unik berhasil diunduh dan disimpan ke '{os.path.basename(PROXYLIST_SOURCE_FILE)}'[/bold green]")
        if duplicates_removed > 0: ui.console.print(f"[dim]   ({duplicates_removed} duplikat dihapus)[/dim]")
        return True
    except IOError as e:
        ui.console.print(f"\n[bold red]Gagal menulis hasil ke '{PROXYLIST_SOURCE_FILE}': {e}[/bold red]")
        return False

def convert_proxylist_to_http():
    if not os.path.exists(PROXYLIST_SOURCE_FILE):
        ui.console.print(f"[bold red]Error: '{os.path.basename(PROXYLIST_SOURCE_FILE)}' tidak ditemukan.[/bold red]")
        return False
    try:
        with open(PROXYLIST_SOURCE_FILE, "r") as f: lines = f.readlines()
    except Exception as e:
        ui.console.print(f"[bold red]Gagal membaca '{PROXYLIST_SOURCE_FILE}': {e}[/bold red]")
        return False
    cleaned_proxies_input = [line.strip() for line in lines if line.strip() and not line.strip().startswith("#")]
    if not cleaned_proxies_input:
        ui.console.print(f"[yellow]'{os.path.basename(PROXYLIST_SOURCE_FILE)}' kosong atau hanya komentar.[/yellow]")
        if os.path.exists(PROXY_SOURCE_FILE):
            try: os.remove(PROXY_SOURCE_FILE)
            except OSError as e: ui.console.print(f"[yellow] Gagal menghapus '{os.path.basename(PROXY_SOURCE_FILE)}': {e}[/yellow]")
        return True
    ui.console.print(f"Mengonversi {len(cleaned_proxies_input)} proksi dari '{os.path.basename(PROXYLIST_SOURCE_FILE)}'...")
    converted_proxies, skipped_count, skipped_examples = [], 0, []
    host_pattern = r"((?:[0-9]{1,3}\.){3}[0-9]{1,3}|(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,})"
    port_pattern = r"[0-9]{1,5}"
    for p in cleaned_proxies_input:
        p = p.strip()
        if not p: continue
        if p.startswith("http://") or p.startswith("https://"):
            converted_proxies.append(p); continue
        converted = None
        match_user_pass_host_port = re.match(rf"^(?P<user_pass>.+)@(?P<host>{host_pattern}):(?P<port>{port_pattern})$", p)
        if match_user_pass_host_port:
            user_pass = match_user_pass_host_port.group("user_pass")
            host = match_user_pass_host_port.group("host")
            port = match_user_pass_host_port.group("port")
            try:
                if 1 <= int(port) <= 65535: converted = f"http://{user_pass}@{host}:{port}"
            except ValueError: pass
        if not converted:
            parts = p.split(':')
            if len(parts) == 4:
                ip, port, user, password = parts
                if re.match(rf"^{host_pattern}$", ip) and re.match(rf"^{port_pattern}$", port):
                    try:
                        if 1 <= int(port) <= 65535: converted = f"http://{user}:{password}@{ip}:{port}"
                    except ValueError: pass
            elif len(parts) == 2:
                ip, port = parts
                if re.match(rf"^{host_pattern}$", ip) and re.match(rf"^{port_pattern}$", port):
                     try:
                         if 1 <= int(port) <= 65535: converted = f"http://{ip}:{port}"
                     except ValueError: pass
        if converted: converted_proxies.append(converted)
        else:
            skipped_count += 1
            if len(skipped_examples) < 5: skipped_examples.append(p)
    if skipped_count > 0:
        ui.console.print(f"[yellow]{skipped_count} baris dilewati (format tidak dikenali/port invalid).[/yellow]")
        if skipped_examples:
            ui.console.print("[yellow]Contoh:[/yellow]")
            for ex in skipped_examples: ui.console.print(f"  - {ex}")
    if not converted_proxies:
        ui.console.print("[bold red]Tidak ada proksi yang berhasil dikonversi.[/bold red]")
        if os.path.exists(PROXY_SOURCE_FILE):
             try: os.remove(PROXY_SOURCE_FILE)
             except OSError as e: ui.console.print(f"[yellow] Gagal menghapus '{os.path.basename(PROXY_SOURCE_FILE)}': {e}[/yellow]")
        return False
    try:
        # Pastikan direktori ada sebelum menulis
        os.makedirs(os.path.dirname(PROXY_SOURCE_FILE), exist_ok=True)
        with open(PROXY_SOURCE_FILE, "w") as f:
            for proxy in converted_proxies: f.write(proxy + "\n")
        # Kosongkan proxylist.txt HANYA jika konversi berhasil dan file ada
        if os.path.exists(PROXYLIST_SOURCE_FILE):
             open(PROXYLIST_SOURCE_FILE, "w").close()
             ui.console.print(f"[bold cyan]   '{os.path.basename(PROXYLIST_SOURCE_FILE)}' dikosongkan.[/bold cyan]")

        ui.console.print(f"[bold green]✅ {len(converted_proxies)} proksi dikonversi -> '{os.path.basename(PROXY_SOURCE_FILE)}'.[/bold green]")
        return True
    except Exception as e:
        ui.console.print(f"[bold red]Gagal menulis ke file: {e}[/bold red]")
        return False

def load_and_deduplicate_proxies(file_path):
    if not os.path.exists(file_path): ui.console.print(f"[yellow]File proxy '{os.path.basename(file_path)}' tidak ditemukan.[/yellow]"); return []
    try:
        with open(file_path, "r") as f: proxies = [line.strip() for line in f if line.strip() and not line.startswith("#")]
    except Exception as e: ui.console.print(f"[bold red]Gagal membaca '{file_path}': {e}[/bold red]"); return []

    if not proxies: ui.console.print(f"[yellow]File proxy '{os.path.basename(file_path)}' kosong.[/yellow]"); return []

    unique_proxies = sorted(list(set(proxies)))
    duplicates_removed = len(proxies) - len(unique_proxies)
    if duplicates_removed > 0: ui.console.print(f"[dim]   ({duplicates_removed} duplikat dihapus dari '{os.path.basename(file_path)}') [/dim]")
    return unique_proxies

def load_paths(file_path):
    if not os.path.exists(file_path): ui.console.print(f"[bold red]Error: File path target '{file_path}' tidak ditemukan.[/bold red]"); return []
    try:
        with open(file_path, "r") as f:
            # === PERBAIKAN: Gunakan SCRIPT_DIR sebagai basis ===
            project_root = os.path.abspath(os.path.join(SCRIPT_DIR, '..'))
            raw_paths = [line.strip() for line in f if line.strip() and not line.startswith("#")]

        absolute_paths = []
        invalid_paths_count = 0
        ui.console.print(f"Memvalidasi {len(raw_paths)} path target dari '{os.path.basename(file_path)}'...")
        for p in raw_paths:
            p_normalized = p.replace('/', os.sep).replace('\\', os.sep)
            abs_p = os.path.normpath(os.path.join(project_root, p_normalized))

            if os.path.isdir(abs_p):
                absolute_paths.append(abs_p)
            else:
                invalid_paths_count += 1
                ui.console.print(f"  [yellow]✖ Lewati:[/yellow] Path target tidak valid atau tidak ditemukan: {abs_p} (dari '{p}')")

        if invalid_paths_count > 0:
            ui.console.print(f"[yellow]{invalid_paths_count} path target dilewati.[/yellow]")
        if not absolute_paths:
             ui.console.print(f"[yellow]Tidak ada path target yang valid ditemukan.[/yellow]")
        else:
             ui.console.print(f"[green]{len(absolute_paths)} path target valid siap untuk distribusi.[/green]")
        return absolute_paths
    except Exception as e:
        ui.console.print(f"[bold red]Gagal memproses file '{file_path}': {e}[/bold red]")
        return []

def backup_file(file_path, backup_path):
    if os.path.exists(file_path):
        try:
            # Pastikan direktori backup ada (sekarang backup_path sudah absolut)
            os.makedirs(os.path.dirname(backup_path), exist_ok=True)
            shutil.copy(file_path, backup_path)
            ui.console.print(f"[green]Backup '{os.path.basename(file_path)}' -> '{os.path.basename(backup_path)}'[/green]")
        except Exception as e: ui.console.print(f"[bold red]Gagal backup '{os.path.basename(file_path)}': {e}[/bold red]")
    else:
       ui.console.print(f"[dim]File '{os.path.basename(file_path)}' tidak ada, backup dilewati.[/dim]")


def check_proxy_final(proxy):
    if GITHUB_TEST_TOKEN is None: return proxy, False, "Token GitHub?" # Gagal kalo token gak ada
    proxies_dict = {"http": proxy, "https": proxy}
    headers = {'User-Agent': 'ProxySync-Tester/3.0', 'Authorization': f'Bearer {GITHUB_TEST_TOKEN}', 'Accept': 'application/vnd.github.v3+json'}
    try:
        # INI BAGIAN PENTINGNYA: Cuma GET ke GitHub API
        response = requests.get(GITHUB_API_TEST_URL, proxies=proxies_dict, timeout=PROXY_TIMEOUT, headers=headers)

        # Cek error standar
        if response.status_code == 401: return proxy, False, "GitHub Auth (401)"
        if response.status_code == 403: return proxy, False, "GitHub Forbidden (403)"
        if response.status_code == 407: return proxy, False, "Proxy Auth (407)"
        if response.status_code == 429: return proxy, False, "GitHub Rate Limit (429)"
        response.raise_for_status() # Gagal kalo status >= 400 (selain yg udah dicek)

        # INI KRITERIA LOLOSNYA: Asal response ada isinya (lebih dari 5 char)
        if response.text and len(response.text) > 5: return proxy, True, "OK"
        else: return proxy, False, "Respons GitHub?" # Gagal kalo respons aneh/kosong

    except requests.exceptions.Timeout: return proxy, False, f"Timeout ({PROXY_TIMEOUT}s)"
    except requests.exceptions.ProxyError as e: reason = str(e).split(':')[-1].strip(); return proxy, False, f"Proxy Error ({reason[:30]})"
    except requests.exceptions.RequestException as e: reason = str(e.__class__.__name__); return proxy, False, f"Koneksi Gagal ({reason})"


def distribute_proxies(proxies, paths):
    if not proxies or not paths: ui.console.print("[yellow]Distribusi proxy dilewati (tidak ada proxy valid atau path target).[/yellow]"); return
    ui.console.print(f"\n[cyan]Mendistribusikan {len(proxies)} proksi valid ke {len(paths)} path target...[/cyan]")
    # === PERBAIKAN: Gunakan SCRIPT_DIR sebagai basis ===
    project_root_abs = os.path.abspath(os.path.join(SCRIPT_DIR, '..'))
    success_count = 0
    fail_count = 0
    for path_str in paths:
        path = Path(path_str)
        if not path.is_dir():
            ui.console.print(f"  [yellow]✖ Lewati:[/yellow] Path target tidak valid: {path_str}")
            fail_count += 1
            continue
        proxy_file_path = path / "proxies.txt"
        target_filename = "proxies.txt"
        if not proxy_file_path.exists():
             proxy_file_path_alt = path / "proxy.txt"
             if proxy_file_path_alt.exists():
                 proxy_file_path = proxy_file_path_alt
                 target_filename = "proxy.txt"
        proxies_shuffled = random.sample(proxies, len(proxies))
        try:
            with open(proxy_file_path, "w") as f:
                for proxy in proxies_shuffled: f.write(proxy + "\n")
            rel_path_display = os.path.relpath(proxy_file_path, project_root_abs)
            ui.console.print(f"  [green]✔[/green] Berhasil menulis ke [bold]{rel_path_display}[/bold]")
            success_count += 1
        except IOError as e:
            rel_path_display = os.path.relpath(proxy_file_path, project_root_abs)
            ui.console.print(f"  [red]✖[/red] Gagal menulis ke [bold]{rel_path_display}[/bold]: {e}")
            fail_count += 1
    ui.console.print(f"Distribusi selesai. Berhasil: {success_count}, Gagal/Lewati: {fail_count}")

def save_good_proxies(proxies, file_path):
    try:
        # Pastikan direktori ada (sekarang file_path sudah absolut)
        os.makedirs(os.path.dirname(file_path), exist_ok=True)
        with open(file_path, "w") as f:
            for proxy in proxies: f.write(proxy + "\n")
        ui.console.print(f"\n[bold green]✅ {len(proxies)} proksi valid berhasil disimpan ke '{os.path.basename(file_path)}'[/bold green]")
        return True
    except IOError as e:
        ui.console.print(f"\n[bold red]✖ Gagal menyimpan proksi valid ke '{os.path.basename(file_path)}': {e}[/bold red]")
        return False

def run_automated_test_and_save():
    ui.print_header()
    ui.console.print("[bold cyan]Mode Auto: Tes Akurat & Simpan Hasil...[/bold cyan]")
    if not load_github_token(GITHUB_TOKENS_FILE):
        ui.console.print("[bold red]Tes proxy dibatalkan: Gagal memuat token GitHub.[/bold red]")
        return False
    ui.console.print("-" * 40)
    ui.console.print("[bold cyan]Langkah 1: Memuat & Membersihkan Proxy Input...[/bold cyan]")
    proxies = load_and_deduplicate_proxies(PROXY_SOURCE_FILE)
    if not proxies:
        ui.console.print(f"[bold red]Berhenti: File input '{os.path.basename(PROXY_SOURCE_FILE)}' kosong atau tidak ditemukan.[/bold red]")
        if os.path.exists(SUCCESS_PROXY_FILE):
            try: os.remove(SUCCESS_PROXY_FILE)
            except OSError as e: ui.console.print(f"[yellow] Gagal menghapus '{os.path.basename(SUCCESS_PROXY_FILE)}': {e}[/yellow]")
        return False
    ui.console.print(f"Siap menguji {len(proxies)} proksi unik dari '{os.path.basename(PROXY_SOURCE_FILE)}'.")
    ui.console.print("-" * 40)
    ui.console.print("[bold cyan]Langkah 2: Menjalankan Tes Akurat via GitHub API...[/bold cyan]")
    good_proxies = ui.run_concurrent_checks_display(proxies, check_proxy_final, MAX_WORKERS, FAIL_PROXY_FILE)
    if not good_proxies:
        ui.console.print("[bold red]Berhenti: Tidak ada proksi yang lolos tes.[/bold red]")
        if os.path.exists(SUCCESS_PROXY_FILE):
            try: os.remove(SUCCESS_PROXY_FILE)
            except OSError as e: ui.console.print(f"[yellow] Gagal menghapus '{os.path.basename(SUCCESS_PROXY_FILE)}': {e}[/yellow]")
        return False
    ui.console.print(f"[bold green]{len(good_proxies)} proksi lolos tes.[/bold green]")
    ui.console.print("-" * 40)
    ui.console.print("[bold cyan]Langkah 3: Menyimpan Proksi Valid...[/bold cyan]")
    save_success = save_good_proxies(good_proxies, SUCCESS_PROXY_FILE)
    if save_success:
        ui.console.print("\n[bold green]✅ Tes otomatis dan penyimpanan selesai![/bold green]")
        return True
    else:
        ui.console.print("\n[bold red]❌ Tes otomatis selesai, namun GAGAL menyimpan hasil.[/bold red]")
        return False

def run_full_process():
    ui.print_header()
    if not load_github_token(GITHUB_TOKENS_FILE): ui.console.print("[bold red]Tes proxy dibatalkan (token GitHub?).[/bold red]"); return
    distribute_choice = ui.Prompt.ask("[bold yellow]Distribusi proksi valid ke folder bot (sesuai paths.txt)? (y/n)[/bold yellow]", choices=["y", "n"], default="y").lower()
    ui.console.print("-" * 40); ui.console.print("[bold cyan]Langkah 1: Backup & Clean...[/bold cyan]")
    backup_file(PROXY_SOURCE_FILE, PROXY_BACKUP_FILE)
    proxies = load_and_deduplicate_proxies(PROXY_SOURCE_FILE)
    if not proxies: ui.console.print(f"[bold red]Stop: '{os.path.basename(PROXY_SOURCE_FILE)}' kosong.[/bold red]"); return
    ui.console.print(f"Siap tes {len(proxies)} proksi unik."); ui.console.print("-" * 40)
    ui.console.print("[bold cyan]Langkah 2: Tes Akurat GitHub...[/bold cyan]")
    good_proxies = ui.run_concurrent_checks_display(proxies, check_proxy_final, MAX_WORKERS, FAIL_PROXY_FILE)
    if not good_proxies: ui.console.print("[bold red]Stop: Tidak ada proksi lolos.[/bold red]"); return
    ui.console.print(f"[bold green]{len(good_proxies)} proksi lolos.[/bold green]"); ui.console.print("-" * 40)
    ui.console.print("[bold cyan]Langkah 3: Menyimpan Proksi Valid...[/bold cyan]") # Pindahkan log ini
    save_success = save_good_proxies(good_proxies, SUCCESS_PROXY_FILE)
    if not save_success:
        ui.console.print("[bold red]Gagal menyimpan hasil tes utama. Distribusi dibatalkan.[/bold red]")
        return
    if distribute_choice == 'y':
        ui.console.print("-" * 40) # Tambah separator
        ui.console.print("[bold cyan]Langkah 4: Distribusi...[/bold cyan]") # Update nomor langkah
        paths = load_paths(PATHS_SOURCE_FILE)
        if not paths: ui.console.print("[bold red]Stop: 'paths.txt' kosong/invalid. Distribusi dibatalkan.[/bold red]"); return
        distribute_proxies(good_proxies, paths)
    else:
        ui.console.print("-" * 40) # Tambah separator
        ui.console.print("[bold cyan]Langkah 4: Distribusi dilewati (sesuai pilihan).[/bold cyan]") # Update nomor langkah
    ui.console.print("\n[bold green]✅ Semua langkah selesai![/bold green]")

def main_interactive():
    while True:
        ui.print_header()
        choice = ui.display_main_menu()
        result = False
        operation_name = ""
        if choice == "1":
            operation_name = "Sinkronisasi IP"
            result = run_webshare_ip_sync()
        elif choice == "2":
            sub_choice = ui.questionary.select(
                "Pilih operasi API:",
                choices=[
                    {'name': "a) Discover & Simpan URL Download (dari API Keys)", 'value': 'a'},
                    {'name': "b) Unduh Proxy List (HANYA dari URL tersimpan)", 'value': 'b'}, # Perjelas deskripsi
                ],
                 pointer=">",
                 use_shortcuts=True
            ).ask()

            if sub_choice is None:
                ui.console.print("[yellow]Operasi dibatalkan.[/yellow]")
                continue
            elif sub_choice == 'a':
                 operation_name = "Discover URL"
                 result = download_proxies_from_api(get_urls_only=True)
            else: # sub_choice == 'b'
                 operation_name = "Unduh Proxy"
                 result = download_proxies_from_api(get_urls_only=False) # Pastikan get_urls_only=False

        elif choice == "3":
            operation_name = "Konversi Proxy"
            result = convert_proxylist_to_http()
        elif choice == "4":
            operation_name = "Tes & Distribusi"
            run_full_process()
            result = True
        elif choice == "5":
            ui.manage_paths_menu_display()
            result = True
        elif choice == "6":
            ui.console.print("[bold cyan]Keluar dari aplikasi...[/bold cyan]")
            break

        if operation_name and choice not in ["4", "5"]:
            if result: ui.console.print(f"\n[bold green]✅ Operasi '{operation_name}' Selesai.[/bold green]")
            else: ui.console.print(f"\n[bold red]❌ Operasi '{operation_name}' Gagal atau Dibatalkan.[/bold red]")

        if choice != "6":
            ui.Prompt.ask("\n[bold]Tekan Enter untuk kembali ke menu...[/bold]")

if __name__ == "__main__":
    # === PERBAIKAN: Pindahkan os.chdir ke sini ===
    # Pastikan CWD adalah direktori skrip SEBELUM path lain didefinisikan
    os.chdir(os.path.dirname(os.path.abspath(__file__)))
    # === AKHIR PERBAIKAN ===

    parser = argparse.ArgumentParser(description="ProxySync v3.0 - Proxy Management Tool")
    parser.add_argument('--full-auto', action='store_true', help='Run IP Sync, Download, Convert, Test & Save (non-interactive)')
    parser.add_argument('--ip-auth-only', action='store_true', help='Only run Webshare IP Authorization sync (non-interactive)')
    parser.add_argument('--get-urls-only', action='store_true', help='Only discover and save Webshare download URLs (non-interactive)')
    parser.add_argument('--test-and-save-only', action='store_true', help='Only run proxy test and save results (non-interactive)')
    args = parser.parse_args()
    exit_code = 0
    try:
        if args.full_auto:
            ui.console.print("[bold cyan]--- PROXYSYNC FULL AUTO MODE ---[/bold cyan]")
            success = run_webshare_ip_sync()
            if success: success = download_proxies_from_api(is_auto=True)
            if success: success = convert_proxylist_to_http()
            if success: success = run_automated_test_and_save()
            if success: ui.console.print("\n[bold green]✅ FULL AUTO MODE SELESAI.[/bold green]")
            else: ui.console.print("\n[bold red]❌ FULL AUTO MODE GAGAL PADA SALAH SATU LANGKAH.[/bold red]"); exit_code = 1
        elif args.ip_auth_only:
            ui.console.print("[bold cyan]--- PROXYSYNC IP AUTH ONLY MODE ---[/bold cyan]")
            success = run_webshare_ip_sync()
            if success: ui.console.print("\n[bold green]✅ IP AUTH ONLY SELESAI.[/bold green]")
            else: ui.console.print("\n[bold red]❌ IP AUTH ONLY GAGAL.[/bold red]"); exit_code = 1
        elif args.get_urls_only:
             ui.console.print("[bold cyan]--- PROXYSYNC GET URLS ONLY MODE ---[/bold cyan]")
             success = download_proxies_from_api(get_urls_only=True)
             if success: ui.console.print("\n[bold green]✅ GET URLS ONLY SELESAI.[/bold green]")
             else: ui.console.print("\n[bold red]❌ GET URLS ONLY GAGAL.[/bold red]"); exit_code = 1
        elif args.test_and_save_only:
             ui.console.print("[bold cyan]--- PROXYSYNC TEST AND SAVE ONLY MODE ---[/bold cyan]")
             success = run_automated_test_and_save()
             if success: ui.console.print("\n[bold green]✅ TEST AND SAVE ONLY SELESAI.[/bold green]")
             else: ui.console.print("\n[bold red]❌ TEST AND SAVE ONLY GAGAL.[/bold red]"); exit_code = 1
        else:
            main_interactive()
    except Exception as e:
         ui.console.print(f"\n[bold red]!!! TERJADI ERROR FATAL !!![/bold red]")
         import traceback
         traceback.print_exc()
         exit_code = 1
    finally:
         sys.exit(exit_code)
