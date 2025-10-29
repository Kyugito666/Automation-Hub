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

# --- Konfigurasi ---
PROXYLIST_SOURCE_FILE = "proxylist.txt"
PROXY_SOURCE_FILE = "proxy.txt"
# Sesuaikan path relatif ke folder config di root project
CONFIG_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', 'config'))
PATHS_SOURCE_FILE = os.path.join(CONFIG_DIR, "paths.txt")
APILIST_SOURCE_FILE = os.path.join(CONFIG_DIR, "apilist.txt") # <-- Path ke apilist.txt
GITHUB_TOKENS_FILE = os.path.join(CONFIG_DIR, "github_tokens.txt")
WEBSHARE_APIKEYS_FILE = os.path.join(CONFIG_DIR, "apikeys.txt")

FAIL_PROXY_FILE = "fail_proxy.txt"
SUCCESS_PROXY_FILE = "success_proxy.txt"
PROXY_BACKUP_FILE = "proxy_backup.txt"

# --- Konfigurasi Webshare ---
WEBSHARE_AUTH_URL = "https://proxy.webshare.io/api/v2/proxy/ipauthorization/"
WEBSHARE_SUB_URL = "https://proxy.webshare.io/api/v2/subscription/"
WEBSHARE_CONFIG_URL = "https://proxy.webshare.io/api/v2/proxy/config/"
WEBSHARE_PROFILE_URL = "https://proxy.webshare.io/api/v2/profile/"
WEBSHARE_DOWNLOAD_URL_BASE = "https://proxy.webshare.io/api/v2/proxy/list/download/{token}/-/any/username/direct/-/"
# Format URL pakai 'username' literal dan plan_id
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
# ... (load_github_token, load_webshare_apikeys, get_current_public_ip, get_account_email, get_target_plan_id, get_authorized_ips, remove_ip, add_ip - TIDAK BERUBAH) ...
def load_github_token(file_path):
    global GITHUB_TEST_TOKEN
    try:
        if not os.path.exists(file_path): ui.console.print(f"[bold red]Error: '{file_path}' tidak ada.[/bold red]"); return False
        with open(file_path, "r") as f: lines = f.readlines()
        if len(lines) < 3: ui.console.print(f"[bold red]Error: Format '{file_path}' salah.[/bold red]"); return False
        first_token = lines[2].strip().split(',')[0].strip()
        if not first_token or not (first_token.startswith("ghp_") or first_token.startswith("github_pat_")): ui.console.print(f"[bold red]Error: Token awal '{file_path}' invalid.[/bold red]"); return False
        GITHUB_TEST_TOKEN = first_token
        ui.console.print(f"[green]✓ Token GitHub OK.[/green]"); return True
    except Exception as e: ui.console.print(f"[bold red]Gagal load token GitHub: {e}[/bold red]"); return False

def load_webshare_apikeys(file_path):
    if not os.path.exists(file_path):
        with open(file_path, "w") as f: f.write("# API key Webshare, 1 per baris\n")
        ui.console.print(f"[yellow]'{file_path}' dibuat. Isi API key.[/yellow]"); return []
    with open(file_path, "r") as f: return [line.strip() for line in f if line.strip() and not line.strip().startswith("#")]

def get_current_public_ip():
    ui.console.print("1. Cek IP publik...")
    try:
        response = requests.get(IP_CHECK_SERVICE_URL, timeout=WEBSHARE_API_TIMEOUT)
        response.raise_for_status(); new_ip = response.json()["ip"]
        ui.console.print(f"   -> [bold green]IP baru: {new_ip}[/bold green]"); return new_ip
    except requests.RequestException as e: ui.console.print(f"   -> [bold red]ERROR Gagal cek IP: {e}[/bold red]", file=sys.stderr); return None

def get_account_email(session: requests.Session) -> str:
    try:
        response = session.get(WEBSHARE_PROFILE_URL, timeout=WEBSHARE_API_TIMEOUT)
        if response.status_code == 401: return "[bold red]API Key Invalid[/]"
        response.raise_for_status()
        data = response.json(); email = data.get("email")
        if email: return email
        else: return "[yellow]Email N/A[/]"
    except requests.exceptions.HTTPError as e: return f"[bold red]HTTP Err ({e.response.status_code})[/]"
    except requests.RequestException: return "[bold red]Koneksi Err[/]"
    except Exception: return "[bold red]Parsing Err[/]"

def get_target_plan_id(session: requests.Session):
    ui.console.print("2. Cek Plan ID (via /config/)...")
    try:
        response = session.get(WEBSHARE_CONFIG_URL, timeout=WEBSHARE_API_TIMEOUT)
        if response.status_code == 401: ui.console.print("   -> [bold red]ERROR: API Key invalid.[/bold red]"); return None
        response.raise_for_status()
        data = response.json(); plan_id = data.get("id")
        if plan_id: plan_id_str = str(plan_id); ui.console.print(f"   -> [green]OK: Plan ID: {plan_id_str}[/green]"); return plan_id_str
        else: ui.console.print("   -> [bold red]ERROR: /config/ tidak return 'id'.[/bold red]"); return None
    except requests.exceptions.HTTPError as e: ui.console.print(f"   -> [bold red]ERROR HTTP: {e.response.text}[/bold red]"); return None
    except requests.RequestException as e: ui.console.print(f"   -> [bold red]ERROR Koneksi: {e}[/bold red]"); return None

def get_authorized_ips(session: requests.Session, plan_id: str):
    ui.console.print("3. Cek IP terdaftar...")
    params = {"plan_id": plan_id}; ip_to_id_map = {}
    try:
        response = session.get(WEBSHARE_AUTH_URL, params=params, timeout=WEBSHARE_API_TIMEOUT)
        response.raise_for_status(); results = response.json().get("results", [])
        for item in results:
            ip = item.get("ip_address"); auth_id = item.get("id")
            if ip and auth_id: ip_to_id_map[ip] = auth_id
        if not ip_to_id_map: ui.console.print("   -> Tidak ada IP lama.")
        else: ui.console.print(f"   -> IP lama: {', '.join(ip_to_id_map.keys())}")
        return ip_to_id_map
    except requests.RequestException as e: ui.console.print(f"   -> [bold red]ERROR Gagal cek IP lama: {e}[/bold red]"); return {}

def remove_ip(session: requests.Session, ip: str, authorization_id: int, plan_id: str):
    ui.console.print(f"   -> Hapus IP lama: {ip} (ID: {authorization_id})")
    params = {"plan_id": plan_id}
    delete_url = f"{WEBSHARE_AUTH_URL}{authorization_id}/"
    try:
        response = session.delete(delete_url, params=params, timeout=WEBSHARE_API_TIMEOUT)
        if response.status_code == 204: ui.console.print(f"   -> [green]OK Hapus: {ip}[/green]")
        else:
            ui.console.print(f"   -> [bold red]ERROR Gagal hapus {ip} ({response.status_code})[/bold red]")
            try: ui.console.print(f"      {response.json()}")
            except: ui.console.print(f"      {response.text}")
            response.raise_for_status()
    except requests.RequestException as e:
        ui.console.print(f"   -> [bold red]ERROR Gagal hapus {ip}[/bold red]")
        try: ui.console.print(f"      {e.response.text}")
        except: ui.console.print(f"      {e}")

def add_ip(session: requests.Session, ip: str, plan_id: str):
    ui.console.print(f"   -> Tambah IP baru: {ip}")
    params = {"plan_id": plan_id}; payload = {"ip_address": ip}
    try:
        response = session.post(WEBSHARE_AUTH_URL, json=payload, params=params, timeout=WEBSHARE_API_TIMEOUT)
        if response.status_code == 201: ui.console.print(f"   -> [green]OK Tambah: {ip}[/green]")
        else:
            ui.console.print(f"   -> [bold red]ERROR Gagal tambah {ip} ({response.status_code})[/bold red]")
            try: ui.console.print(f"      {response.json()}")
            except: ui.console.print(f"      {response.text}")
            response.raise_for_status()
    except requests.RequestException as e:
        ui.console.print(f"   -> [bold red]ERROR Gagal tambah {ip}[/bold red]")
        try: ui.console.print(f"      {e.response.text}")
        except: ui.console.print(f"      {e}")


# === PERBAIKAN: Load URL dari apilist.txt ===
def load_apis_from_file(file_path):
    """Memuat daftar URL API dari file teks."""
    urls = set()
    if not os.path.exists(file_path):
        # Buat file jika tidak ada
        try:
            with open(file_path, "w") as f:
                f.write("# Masukkan URL download proxy Webshare manual di sini, SATU per baris\n")
            ui.console.print(f"[yellow]'{file_path}' dibuat. Anda bisa isi URL manual jika perlu.[/yellow]")
        except IOError as e:
            ui.console.print(f"[bold red]Gagal membuat file '{file_path}': {e}[/bold red]")
        return list(urls) # Kembalikan list kosong

    try:
        with open(file_path, "r") as f:
            for line in f:
                line = line.strip()
                if line and not line.startswith("#"):
                    # Basic validation for Webshare download URL format
                    if "proxy.webshare.io/api/v2/proxy/list/download/" in line:
                         urls.add(line)
                    else:
                         ui.console.print(f"[yellow]   Format URL tidak valid di '{file_path}': {line[:50]}...[/yellow]")
    except IOError as e:
        ui.console.print(f"[bold red]Gagal membaca file '{file_path}': {e}[/bold red]")

    return list(urls) # Kembalikan sebagai list

# === PERBAIKAN: Fungsi untuk menyimpan URL baru ===
def save_discovered_url(file_path, url):
    """Menyimpan URL baru ke file apilist.txt jika belum ada."""
    existing_urls = set(load_apis_from_file(file_path))
    if url not in existing_urls:
        try:
            with open(file_path, "a") as f:
                f.write(f"\n{url}\n") # Tambah baris baru sebelum dan sesudah
            ui.console.print(f"   -> [green]URL baru disimpan ke '{os.path.basename(file_path)}'[/green]")
            return True
        except IOError as e:
            ui.console.print(f"[bold red]   Gagal menyimpan URL ke '{os.path.basename(file_path)}': {e}[/bold red]")
            return False
    # else:
    #     ui.console.print(f"   -> [dim]URL sudah ada di '{os.path.basename(file_path)}'[/dim]")
    return False

def run_webshare_ip_sync():
    ui.print_header()
    ui.console.print("[bold cyan]--- Sinkronisasi IP Otorisasi Webshare ---[/bold cyan]")
    api_keys = load_webshare_apikeys(WEBSHARE_APIKEYS_FILE)
    if not api_keys: ui.console.print(f"[bold red]'{WEBSHARE_APIKEYS_FILE}' kosong.[/bold red]"); return False # Return False on failure

    new_ip = get_current_public_ip()
    if not new_ip: ui.console.print("[bold red]Gagal mendapatkan IP publik saat ini. Batal.[/bold red]"); return False # Return False on failure

    ui.console.print(f"\nSinkronisasi IP [bold]{new_ip}[/bold] ke [bold]{len(api_keys)}[/bold] akun Webshare...")
    success_count = 0
    fail_count = 0

    for api_key in api_keys:
        account_email_info = "[grey]Mencoba mendapatkan email...[/]"
        try:
            with requests.Session() as email_session:
                email_session.headers.update({"Authorization": f"Token {api_key}", "Accept": "application/json"})
                account_email_info = get_account_email(email_session)
        except Exception: account_email_info = "[bold red]Error[/]"
        ui.console.print(f"\n--- Memproses Key: [...{api_key[-6:]}] (Email: {account_email_info}) ---")

        with requests.Session() as session:
            session.headers.update({"Authorization": f"Token {api_key}", "Accept": "application/json"})
            try:
                plan_id = get_target_plan_id(session)
                if not plan_id: ui.console.print(f"   -> [bold red]Gagal mendapatkan Plan ID. Akun ini dilewati.[/bold red]"); fail_count+=1; continue

                authorized_ips_map = get_authorized_ips(session, plan_id)
                existing_ips = list(authorized_ips_map.keys())

                if new_ip in existing_ips:
                    ui.console.print(f"   -> [green]IP baru ({new_ip}) sudah terdaftar. Tidak perlu tindakan.[/green]")
                    success_count +=1 # Anggap sukses jika IP sudah ada
                    continue

                ui.console.print("\n4. Menghapus IP lama (jika ada)...")
                if not existing_ips: ui.console.print("   -> Tidak ada IP lama untuk dihapus.")
                else:
                    ips_to_remove = list(authorized_ips_map.items()) # Buat list agar bisa di-iterate meski map berubah
                    for ip_to_delete, auth_id_to_delete in ips_to_remove:
                        remove_ip(session, ip_to_delete, auth_id_to_delete, plan_id) # Fungsi remove_ip sudah handle error

                ui.console.print("\n5. Menambahkan IP baru...")
                add_ip(session, new_ip, plan_id) # Fungsi add_ip sudah handle error
                # Verifikasi setelah add (opsional tapi bagus)
                time.sleep(2) # Beri waktu API
                updated_ips_map = get_authorized_ips(session, plan_id)
                if new_ip in updated_ips_map:
                    ui.console.print(f"   -> [green]Verifikasi OK: IP {new_ip} berhasil ditambahkan.[/green]")
                    success_count +=1
                else:
                     ui.console.print(f"   -> [bold red]Verifikasi GAGAL: IP {new_ip} tidak ditemukan setelah proses add![/bold red]")
                     fail_count += 1

            except Exception as e:
                ui.console.print(f"   -> [bold red]!!! TERJADI ERROR saat memproses akun ini: {e}. Lanjut ke akun berikutnya.[/bold red]")
                fail_count += 1
                continue # Lanjut ke API key berikutnya

    ui.console.print("\n[bold green]✅ Sinkronisasi IP selesai.[/bold green]")
    ui.console.print(f"   Berhasil: {success_count}, Gagal/Skip: {fail_count}")
    return fail_count == 0 # Return True jika tidak ada kegagalan


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

        # Format URL download menggunakan 'username' literal dan plan_id
        download_url = WEBSHARE_DOWNLOAD_URL_FORMAT.format(token=token, plan_id=plan_id)
        ui.console.print(f"   -> [green]OK: URL download ditemukan.[/green]")
        return download_url
    except requests.exceptions.HTTPError as e:
        ui.console.print(f"   -> [bold red]ERROR HTTP saat akses /config/: {e.response.status_code} - {e.response.text}[/bold red]")
        return None
    except requests.exceptions.RequestException as e:
        ui.console.print(f"   -> [bold red]ERROR Koneksi saat akses /config/: {e}[/bold red]")
        return None
    except Exception as e:
        ui.console.print(f"   -> [bold red]ERROR tak terduga saat mencari URL: {e}[/bold red]")
        return None


# === PERBAIKAN: Fungsi download diubah untuk prioritaskan apilist.txt dan simpan URL baru ===
def download_proxies_from_api(is_auto=False, get_urls_only=False):
    """
    Mengunduh daftar proxy.
    Prioritaskan URL dari APILIST_SOURCE_FILE.
    Jika tidak ada atau gagal, coba discover dari WEBSHARE_APIKEYS_FILE dan simpan URL baru.
    Jika get_urls_only=True, hanya discover dan simpan URL, tidak download proxy list.
    """
    ui.print_header()
    if get_urls_only:
        ui.console.print("[bold cyan]--- Discover & Simpan URL Download Proxy ---[/bold cyan]")
    else:
        ui.console.print("[bold cyan]--- Unduh Proksi dari API ---[/bold cyan]")

    # Selalu pastikan file apilist.txt ada
    existing_api_urls = load_apis_from_file(APILIST_SOURCE_FILE)
    ui.console.print(f"Memuat URL dari '{os.path.basename(APILIST_SOURCE_FILE)}': {len(existing_api_urls)} URL ditemukan.")

    discovered_urls_from_keys = {} # Simpan URL yang ditemukan dari API keys {api_key: url}
    urls_to_process = set(existing_api_urls) # Mulai dengan URL yang sudah ada
    newly_saved_count = 0

    ui.console.print(f"\n[bold]Mencoba discover URL baru dari '{os.path.basename(WEBSHARE_APIKEYS_FILE)}'...[/bold]")
    api_keys = load_webshare_apikeys(WEBSHARE_APIKEYS_FILE)
    if not api_keys:
        ui.console.print(f"[yellow]'{os.path.basename(WEBSHARE_APIKEYS_FILE)}' kosong atau tidak ditemukan.[/yellow]")
    else:
        ui.console.print(f"Ditemukan {len(api_keys)} API Key untuk dicek.")
        processed_keys_count = 0
        for api_key in api_keys:
            # Cek apakah URL untuk key ini sudah ada di existing_api_urls
            # (Ini asumsi kasar bahwa 1 key = 1 plan = 1 URL download unik, mungkin perlu penyesuaian)
            # Untuk simplifikasi, kita coba get URL untuk semua key
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
                        continue

                    download_url = get_webshare_download_url(session, plan_id)
                    if download_url:
                        discovered_urls_from_keys[api_key] = download_url
                        if download_url not in urls_to_process:
                             urls_to_process.add(download_url)
                             # Simpan URL baru ke file
                             if save_discovered_url(APILIST_SOURCE_FILE, download_url):
                                 newly_saved_count += 1
                        # else:
                        #      ui.console.print(f"   -> [dim]URL sudah ada dalam daftar proses.[/dim]")
                    else:
                        ui.console.print("   -> [yellow]Gagal mendapatkan URL download untuk key ini. Dilewati.[/yellow]")
                except Exception as e:
                    ui.console.print(f"   -> [bold red]!!! TERJADI ERROR saat discover URL: {e}[/bold red]")
            processed_keys_count += 1
            if processed_keys_count < len(api_keys):
                 time.sleep(1) # Jeda antar key

        ui.console.print(f"\nSelesai discover URL dari API keys. {len(discovered_urls_from_keys)} URL ditemukan.")
        if newly_saved_count > 0:
             ui.console.print(f"[green]{newly_saved_count} URL baru disimpan ke '{os.path.basename(APILIST_SOURCE_FILE)}'.[/green]")

    # Jika hanya diminta get URL, berhenti di sini
    if get_urls_only:
        ui.console.print("\n[bold green]✅ Proses discover dan simpan URL selesai.[/bold green]")
        return True # Anggap sukses jika berhasil discover tanpa error fatal

    # --- Lanjut ke proses download proxy list ---
    if not urls_to_process:
        ui.console.print("\n[bold red]Tidak ada URL API (baik dari file atau discovery) untuk mengunduh proxy.[/bold red]")
        return False # Gagal karena tidak ada URL

    # Hanya hapus proxylist.txt jika kita akan download
    if not is_auto and os.path.exists(PROXYLIST_SOURCE_FILE) and os.path.getsize(PROXYLIST_SOURCE_FILE) > 0:
        choice = ui.Prompt.ask(f"[bold yellow]File '{PROXYLIST_SOURCE_FILE}' sudah ada isinya. Hapus dan timpa dengan hasil download baru? (y/n)[/bold yellow]", choices=["y", "n"], default="y").lower()
        if choice == 'n': ui.console.print("[cyan]Operasi unduh dibatalkan oleh pengguna.[/cyan]"); return False
    try:
        # Kosongkan file sebelum download
        with open(PROXYLIST_SOURCE_FILE, "w") as f: pass
        ui.console.print(f"\n[green]File '{PROXYLIST_SOURCE_FILE}' siap untuk diisi hasil download.[/green]")
    except IOError as e: ui.console.print(f"[bold red]Gagal mengosongkan file '{PROXYLIST_SOURCE_FILE}': {e}[/bold red]"); return False

    # Buat list target untuk download (url, api_key=None karena URL sudah fix)
    download_targets_final = [(url, None) for url in urls_to_process]

    ui.console.print(f"\n[bold cyan]Siap mengunduh proxy list dari {len(download_targets_final)} URL...[/bold cyan]")

    # Gunakan fungsi ui yang sudah ada untuk download sekuensial
    all_downloaded_proxies = ui.run_sequential_api_downloads(download_targets_final)

    if not all_downloaded_proxies:
        ui.console.print("\n[bold yellow]Tidak ada proxy yang berhasil diunduh dari semua URL.[/bold yellow]")
        return False # Gagal download

    try:
        with open(PROXYLIST_SOURCE_FILE, "w") as f:
            for proxy in all_downloaded_proxies: f.write(proxy + "\n")
        ui.console.print(f"\n[bold green]✅ {len(all_downloaded_proxies)} proxy berhasil diunduh dan disimpan ke '{PROXYLIST_SOURCE_FILE}'[/bold green]")
        return True # Sukses download
    except IOError as e:
        ui.console.print(f"\n[bold red]Gagal menulis hasil download ke '{PROXYLIST_SOURCE_FILE}': {e}[/bold red]")
        return False # Gagal tulis file

# ... (convert_proxylist_to_http, load_and_deduplicate_proxies, load_paths, backup_file, check_proxy_final, distribute_proxies, save_good_proxies, run_automated_test_and_save, run_full_process - TIDAK BERUBAH) ...
def convert_proxylist_to_http():
    """Konversi proxy dari proxylist.txt ke format http dan simpan ke proxy.txt."""
    if not os.path.exists(PROXYLIST_SOURCE_FILE):
        ui.console.print(f"[bold red]Error: '{PROXYLIST_SOURCE_FILE}' tidak ditemukan.[/bold red]")
        return False

    try:
        with open(PROXYLIST_SOURCE_FILE, "r") as f: lines = f.readlines()
    except Exception as e:
        ui.console.print(f"[bold red]Gagal membaca '{PROXYLIST_SOURCE_FILE}': {e}[/bold red]")
        return False

    cleaned_proxies_input = [line.strip() for line in lines if line.strip() and not line.strip().startswith("#")]
    if not cleaned_proxies_input:
        ui.console.print(f"[yellow]'{PROXYLIST_SOURCE_FILE}' kosong atau hanya berisi komentar.[/yellow]")
        # Hapus proxy.txt jika input kosong
        if os.path.exists(PROXY_SOURCE_FILE): os.remove(PROXY_SOURCE_FILE)
        return True # Anggap sukses jika input kosong

    ui.console.print(f"Mengonversi {len(cleaned_proxies_input)} proksi dari '{PROXYLIST_SOURCE_FILE}'...")
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
            if 1 <= int(port) <= 65535: converted = f"http://{user_pass}@{host}:{port}"
        if not converted:
            parts = p.split(':')
            if len(parts) == 4:
                ip, port, user, password = parts
                if re.match(rf"^{host_pattern}$", ip) and re.match(rf"^{port_pattern}$", port):
                    if 1 <= int(port) <= 65535: converted = f"http://{user}:{password}@{ip}:{port}"
            elif len(parts) == 2:
                ip, port = parts
                if re.match(rf"^{host_pattern}$", ip) and re.match(rf"^{port_pattern}$", port):
                     if 1 <= int(port) <= 65535: converted = f"http://{ip}:{port}"
        if converted: converted_proxies.append(converted)
        else:
            skipped_count += 1
            if len(skipped_examples) < 5: skipped_examples.append(p)

    if skipped_count > 0:
        ui.console.print(f"[yellow]{skipped_count} baris dilewati karena format tidak dikenali/valid.[/yellow]")
        if skipped_examples:
            ui.console.print("[yellow]Contoh yang dilewati:[/yellow]")
            for ex in skipped_examples: ui.console.print(f"  - {ex}")

    if not converted_proxies:
        ui.console.print("[bold red]Tidak ada proksi yang berhasil dikonversi.[/bold red]")
        if os.path.exists(PROXY_SOURCE_FILE): os.remove(PROXY_SOURCE_FILE) # Hapus jika hasil konversi kosong
        return False

    try:
        with open(PROXY_SOURCE_FILE, "w") as f:
            for proxy in converted_proxies: f.write(proxy + "\n")
        # Kosongkan proxylist.txt setelah konversi berhasil
        open(PROXYLIST_SOURCE_FILE, "w").close()
        ui.console.print(f"[bold green]✅ {len(converted_proxies)} proksi dikonversi dan disimpan ke '{PROXY_SOURCE_FILE}'.[/bold green]")
        ui.console.print(f"[bold cyan]'{PROXYLIST_SOURCE_FILE}' telah dikosongkan.[/bold cyan]")
        return True
    except Exception as e:
        ui.console.print(f"[bold red]Gagal menulis ke file: {e}[/bold red]")
        return False

def load_and_deduplicate_proxies(file_path):
    if not os.path.exists(file_path): return []
    try:
        with open(file_path, "r") as f: proxies = [line.strip() for line in f if line.strip()]
    except Exception as e: ui.console.print(f"[bold red]Gagal baca '{file_path}': {e}[/bold red]"); return []
    unique_proxies = sorted(list(set(proxies))); duplicates_removed = len(proxies) - len(unique_proxies)
    if duplicates_removed > 0: ui.console.print(f"[yellow]Hapus {duplicates_removed} duplikat.[/yellow]")
    try:
        with open(file_path, "w") as f:
            for proxy in unique_proxies: f.write(proxy + "\n")
    except Exception as e: ui.console.print(f"[bold red]Gagal tulis '{file_path}' (dedup): {e}[/bold red]"); return proxies # Return original if write fails
    return unique_proxies

def load_paths(file_path):
    if not os.path.exists(file_path): ui.console.print(f"[bold red]'{file_path}' N/A.[/bold red]"); return []
    try:
        with open(file_path, "r") as f:
            # Asumsi file ini ada di proxysync/, jadi '../' adalah root project
            project_root = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
            raw_paths = [line.strip() for line in f if line.strip() and not line.startswith("#")]
            absolute_paths = []; invalid_paths = 0
            for p in raw_paths:
                # Normalisasi path separator
                p_normalized = p.replace('/', os.sep).replace('\\', os.sep)
                abs_p = os.path.join(project_root, p_normalized)
                # Cek apakah direktori benar-benar ada
                if os.path.isdir(abs_p): absolute_paths.append(abs_p)
                else: invalid_paths += 1; ui.console.print(f"[yellow]Path target tidak valid atau tidak ditemukan: {abs_p} (dari '{p}') [/yellow]")
            if invalid_paths > 0: ui.console.print(f"[yellow]{invalid_paths} path dilewati.[/yellow]")
            return absolute_paths
    except Exception as e: ui.console.print(f"[bold red]Gagal baca '{file_path}': {e}[/bold red]"); return []


def backup_file(file_path, backup_path):
    if os.path.exists(file_path):
        try: shutil.copy(file_path, backup_path); ui.console.print(f"[green]Backup: '{backup_path}'[/green]")
        except Exception as e: ui.console.print(f"[bold red]Gagal backup '{backup_path}': {e}[/bold red]")

def check_proxy_final(proxy):
    if GITHUB_TEST_TOKEN is None: return proxy, False, "Token GitHub?"
    proxies_dict = {"http": proxy, "https": proxy}
    headers = {'User-Agent': 'ProxySync-Tester/1.0', 'Authorization': f'Bearer {GITHUB_TEST_TOKEN}', 'Accept': 'application/vnd.github.v3+json'}
    try:
        response = requests.get(GITHUB_API_TEST_URL, proxies=proxies_dict, timeout=PROXY_TIMEOUT, headers=headers)
        if response.status_code == 401: return proxy, False, "GitHub Auth (401)"
        if response.status_code == 403: return proxy, False, "GitHub Forbidden (403)"
        if response.status_code == 407: return proxy, False, "Proxy Auth (407)"
        if response.status_code == 429: return proxy, False, "GitHub Rate Limit (429)"
        response.raise_for_status()
        if response.text and len(response.text) > 5: return proxy, True, "OK"
        else: return proxy, False, "Respons GitHub?"
    except requests.exceptions.Timeout: return proxy, False, f"Timeout ({PROXY_TIMEOUT}s)"
    except requests.exceptions.ProxyError as e: reason = str(e).split(':')[-1].strip(); return proxy, False, f"Proxy Error ({reason[:30]})"
    except requests.exceptions.RequestException as e: reason = str(e.__class__.__name__); return proxy, False, f"Koneksi Gagal ({reason})"


def distribute_proxies(proxies, paths):
    if not proxies or not paths: ui.console.print("[yellow]Distribusi proxy dilewati (tidak ada proxy valid atau path target).[/yellow]"); return
    ui.console.print(f"\n[cyan]Mendistribusikan {len(proxies)} proksi valid ke {len(paths)} path target...[/cyan]")
    project_root_abs = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
    success_count = 0
    fail_count = 0
    for path_str in paths:
        # Pastikan path adalah direktori yang valid
        path = Path(path_str)
        if not path.is_dir():
            ui.console.print(f"  [yellow]✖ Lewati:[/yellow] Path target tidak valid: {path_str}")
            fail_count += 1
            continue

        # Cek prioritas nama file: proxies.txt dulu, baru proxy.txt
        proxy_file_path = path / "proxies.txt"
        target_filename = "proxies.txt"
        if not proxy_file_path.exists():
             proxy_file_path_alt = path / "proxy.txt"
             if proxy_file_path_alt.exists():
                 proxy_file_path = proxy_file_path_alt
                 target_filename = "proxy.txt"
             # else:
                 # Jika keduanya tidak ada, default ke proxies.txt
                 # proxy_file_path = path / "proxies.txt"
                 # target_filename = "proxies.txt"

        # Shuffle proxy untuk distribusi acak
        proxies_shuffled = random.sample(proxies, len(proxies))
        try:
            with open(proxy_file_path, "w") as f:
                for proxy in proxies_shuffled: f.write(proxy + "\n")
            # Tampilkan path relatif biar lebih pendek
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
        # Pastikan direktori ada
        os.makedirs(os.path.dirname(file_path), exist_ok=True)
        with open(file_path, "w") as f:
            for proxy in proxies: f.write(proxy + "\n")
        ui.console.print(f"\n[bold green]✅ {len(proxies)} proksi valid berhasil disimpan ke '{file_path}'[/bold green]")
        return True
    except IOError as e:
        ui.console.print(f"\n[bold red]✖ Gagal menyimpan proksi valid ke '{file_path}': {e}[/bold red]")
        return False

def run_automated_test_and_save():
    """Versi non-interaktif: Hanya tes & simpan ke success_proxy.txt."""
    ui.print_header()
    ui.console.print("[bold cyan]Mode Auto: Tes Akurat & Simpan Hasil...[/bold cyan]")

    if not load_github_token(GITHUB_TOKENS_FILE):
        ui.console.print("[bold red]Tes proxy dibatalkan: Gagal memuat token GitHub.[/bold red]")
        return False

    ui.console.print("-" * 40)
    ui.console.print("[bold cyan]Langkah 1: Memuat & Membersihkan Proxy Input...[/bold cyan]")
    proxies = load_and_deduplicate_proxies(PROXY_SOURCE_FILE)
    if not proxies:
        ui.console.print("[bold red]Berhenti: File input 'proxy.txt' kosong atau tidak ditemukan.[/bold red]")
        # Pastikan success_proxy.txt kosong jika input kosong
        if os.path.exists(SUCCESS_PROXY_FILE): os.remove(SUCCESS_PROXY_FILE)
        return False # Gagal

    ui.console.print(f"Siap menguji {len(proxies)} proksi unik dari '{PROXY_SOURCE_FILE}'.")
    ui.console.print("-" * 40)
    ui.console.print("[bold cyan]Langkah 2: Menjalankan Tes Akurat via GitHub API...[/bold cyan]")

    # Jalankan tes concurrent
    good_proxies = ui.run_concurrent_checks_display(proxies, check_proxy_final, MAX_WORKERS, FAIL_PROXY_FILE)

    if not good_proxies:
        ui.console.print("[bold red]Berhenti: Tidak ada proksi yang lolos tes.[/bold red]")
        # Pastikan success_proxy.txt kosong jika tidak ada yang lolos
        if os.path.exists(SUCCESS_PROXY_FILE): os.remove(SUCCESS_PROXY_FILE)
        return False # Gagal

    ui.console.print(f"[bold green]{len(good_proxies)} proksi lolos tes.[/bold green]")
    ui.console.print("-" * 40)
    ui.console.print("[bold cyan]Langkah 3: Menyimpan Proksi Valid...[/bold cyan]")

    # Simpan hasil ke success_proxy.txt
    save_success = save_good_proxies(good_proxies, SUCCESS_PROXY_FILE)

    if save_success:
        ui.console.print("\n[bold green]✅ Tes otomatis dan penyimpanan selesai![/bold green]")
        return True # Sukses
    else:
        ui.console.print("\n[bold red]❌ Tes otomatis selesai, namun GAGAL menyimpan hasil.[/bold red]")
        return False # Gagal

def run_full_process():
    """Versi INTERAKTIF (dari menu TUI)"""
    ui.print_header()
    if not load_github_token(GITHUB_TOKENS_FILE): ui.console.print("[bold red]Tes proxy dibatalkan (token GitHub?).[/bold red]"); return

    distribute_choice = ui.Prompt.ask("[bold yellow]Distribusi proksi valid ke folder bot (sesuai paths.txt)? (y/n)[/bold yellow]", choices=["y", "n"], default="y").lower()

    ui.console.print("-" * 40); ui.console.print("[bold cyan]Langkah 1: Backup & Clean...[/bold cyan]")
    backup_file(PROXY_SOURCE_FILE, PROXY_BACKUP_FILE)
    proxies = load_and_deduplicate_proxies(PROXY_SOURCE_FILE)
    if not proxies: ui.console.print("[bold red]Stop: 'proxy.txt' kosong.[/bold red]"); return

    ui.console.print(f"Siap tes {len(proxies)} proksi unik."); ui.console.print("-" * 40)
    ui.console.print("[bold cyan]Langkah 2: Tes Akurat GitHub...[/bold cyan]")
    good_proxies = ui.run_concurrent_checks_display(proxies, check_proxy_final, MAX_WORKERS, FAIL_PROXY_FILE)
    if not good_proxies: ui.console.print("[bold red]Stop: Tidak ada proksi lolos.[/bold red]"); return

    ui.console.print(f"[bold green]{len(good_proxies)} proksi lolos.[/bold green]"); ui.console.print("-" * 40)

    # Selalu simpan ke success_proxy.txt
    save_success = save_good_proxies(good_proxies, SUCCESS_PROXY_FILE)
    if not save_success:
        ui.console.print("[bold red]Gagal menyimpan hasil tes utama. Distribusi dibatalkan.[/bold red]")
        return

    if distribute_choice == 'y':
        ui.console.print("[bold cyan]Langkah 3: Distribusi...[/bold cyan]")
        paths = load_paths(PATHS_SOURCE_FILE)
        if not paths: ui.console.print("[bold red]Stop: 'paths.txt' kosong/invalid. Distribusi dibatalkan.[/bold red]"); return
        distribute_proxies(good_proxies, paths)
    else:
        ui.console.print("[bold cyan]Langkah 3: Distribusi dilewati (sesuai pilihan).[/bold cyan]")

    ui.console.print("\n[bold green]✅ Semua langkah selesai![/bold green]")


def main_interactive():
    """Fungsi main untuk menu INTERAKTIF"""
    while True:
        ui.print_header()
        choice = ui.display_main_menu()
        result = False # Untuk tracking hasil operasi
        operation_name = ""

        if choice == "1":
            operation_name = "Sinkronisasi IP"
            result = run_webshare_ip_sync()
        elif choice == "2":
            # Tanya dulu mau discover URL atau download proxy
            sub_choice = ui.Prompt.ask(
                "Pilih operasi API:",
                choices=["a", "b"],
                default="b",
                console=ui.console
            ).lower()
            if sub_choice == 'a':
                 operation_name = "Discover URL"
                 result = download_proxies_from_api(get_urls_only=True)
            else:
                 operation_name = "Unduh Proxy"
                 result = download_proxies_from_api()
        elif choice == "3":
            operation_name = "Konversi Proxy"
            result = convert_proxylist_to_http()
        elif choice == "4":
            operation_name = "Tes & Distribusi"
            run_full_process() # Fungsi ini sudah print hasil sendiri
            result = True # Anggap selalu "selesai" (meski mungkin ada error internal)
        elif choice == "5":
            ui.manage_paths_menu_display() # Placeholder
            result = True # Anggap selesai
        elif choice == "6":
            ui.console.print("[bold cyan]Keluar dari aplikasi...[/bold cyan]")
            break # Keluar loop

        # Tampilkan status hasil operasi (kecuali untuk Tes&Distribusi dan Kelola Path)
        if operation_name and choice not in ["4", "5"]:
            if result:
                 ui.console.print(f"\n[bold green]✅ Operasi '{operation_name}' Selesai.[/bold green]")
            else:
                 ui.console.print(f"\n[bold red]❌ Operasi '{operation_name}' Gagal atau Dibatalkan.[/bold red]")

        if choice != "6": # Jangan pause jika keluar
            ui.Prompt.ask("\n[bold]Tekan Enter untuk kembali ke menu...[/bold]")

# === LOGIKA UTAMA dengan Argument Parsing ===
if __name__ == "__main__":
    # Pindah ke direktori skrip berada
    os.chdir(os.path.dirname(os.path.abspath(__file__)))

    parser = argparse.ArgumentParser(description="ProxySync v3.0 - Proxy Management Tool")
    parser.add_argument('--full-auto', action='store_true', help='Run IP Sync, Download, Convert, Test & Save (non-interactive)')
    parser.add_argument('--ip-auth-only', action='store_true', help='Only run Webshare IP Authorization sync (non-interactive)')
    parser.add_argument('--get-urls-only', action='store_true', help='Only discover and save Webshare download URLs (non-interactive)')

    args = parser.parse_args()

    # Logika berdasarkan flag
    exit_code = 0 # Default sukses
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

        else:
            # Jalankan menu interaktif jika tidak ada flag
            main_interactive()

    except Exception as e:
         ui.console.print(f"\n[bold red]!!! TERJADI ERROR FATAL !!![/bold red]")
         import traceback
         traceback.print_exc()
         exit_code = 1 # Set exit code error
    finally:
         sys.exit(exit_code) # Keluar dengan exit code yang sesuai
