import os
import random
import shutil
import time
import re
import sys
import json 
from concurrent.futures import ThreadPoolExecutor, as_completed
import requests
import ui  # Mengimpor semua fungsi UI dari file ui.py

# --- Konfigurasi ---
PROXYLIST_SOURCE_FILE = "proxylist.txt"
PROXY_SOURCE_FILE = "proxy.txt"
PATHS_SOURCE_FILE = "../config/paths.txt" 
APILIST_SOURCE_FILE = "../config/apilist.txt" 
GITHUB_TOKENS_FILE = "../config/github_tokens.txt" 
WEBSHARE_APIKEYS_FILE = "../config/apikeys.txt" 

FAIL_PROXY_FILE = "fail_proxy.txt"
SUCCESS_PROXY_FILE = "success_proxy.txt"
PROXY_BACKUP_FILE = "proxy_backup.txt" 

# --- Konfigurasi Webshare ---
WEBSHARE_AUTH_URL = "https://proxy.webshare.io/api/v2/proxy/ipauthorization/"
WEBSHARE_SUB_URL = "https://proxy.webshare.io/api/v2/subscription/" 
WEBSHARE_CONFIG_URL = "https://proxy.webshare.io/api/v2/proxy/config/" 
WEBSHARE_DOWNLOAD_URL_BASE = "https://proxy.webshare.io/api/v2/proxy/list/download/{token}/-/any/{username}/direct/-/"
WEBSHARE_DOWNLOAD_URL_FORMAT = WEBSHARE_DOWNLOAD_URL_BASE + "?plan_id={plan_id}"
IP_CHECK_SERVICE_URL = "https://api.ipify.org?format=json"

# --- Konfigurasi Tes Proxy ---
PROXY_TIMEOUT = 25
MAX_WORKERS = 10
GITHUB_API_TEST_URL = "https://api.github.com/zen" 

API_DOWNLOAD_WORKERS = 1
RETRY_COUNT = 2
WEBSHARE_API_TIMEOUT = 30 

# --- Variabel Global ---
GITHUB_TEST_TOKEN = None

# --- Fungsi Utility ---
def load_github_token(file_path):
    global GITHUB_TEST_TOKEN
    try:
        if not os.path.exists(file_path): ui.console.print(f"[bold red]Error: File token '{file_path}' tidak ditemukan.[/bold red]"); return False
        with open(file_path, "r") as f: lines = f.readlines()
        if len(lines) < 3: ui.console.print(f"[bold red]Error: Format '{file_path}' salah.[/bold red]"); return False
        first_token = lines[2].strip().split(',')[0].strip()
        if not first_token or not (first_token.startswith("ghp_") or first_token.startswith("github_pat_")): ui.console.print(f"[bold red]Error: Token pertama di '{file_path}' tidak valid.[/bold red]"); return False
        GITHUB_TEST_TOKEN = first_token
        ui.console.print(f"[green]✓ Token GitHub untuk tes proxy berhasil dimuat.[/green]"); return True
    except Exception as e: ui.console.print(f"[bold red]Gagal memuat token GitHub: {e}[/bold red]"); return False

def load_apis(file_path):
    if not os.path.exists(file_path):
        with open(file_path, "w") as f: f.write("# Masukkan URL API Anda di sini, satu per baris\n"); return []
    with open(file_path, "r") as f: return [line.strip() for line in f if line.strip() and not line.strip().startswith("#")]

def load_webshare_apikeys(file_path):
    if not os.path.exists(file_path):
        with open(file_path, "w") as f: f.write("# Masukkan API key Webshare Anda di sini, SATU per baris\n")
        ui.console.print(f"[yellow]File '{file_path}' dibuat. Harap isi API key Webshare Anda.[/yellow]"); return []
    with open(file_path, "r") as f: return [line.strip() for line in f if line.strip() and not line.strip().startswith("#")]

def get_current_public_ip():
    ui.console.print("1. Mengambil IP publik saat ini...")
    try:
        response = requests.get(IP_CHECK_SERVICE_URL, timeout=WEBSHARE_API_TIMEOUT)
        response.raise_for_status()
        new_ip = response.json()["ip"]
        ui.console.print(f"   -> [bold green]IP publik baru: {new_ip}[/bold green]"); return new_ip
    except requests.RequestException as e: ui.console.print(f"   -> [bold red]ERROR: Gagal mendapatkan IP publik: {e}[/bold red]", file=sys.stderr); return None

def get_target_plan_id(session: requests.Session):
    ui.console.print("2. Auto-discover Plan ID (via /config/)...")
    try:
        response = session.get(WEBSHARE_CONFIG_URL, timeout=WEBSHARE_API_TIMEOUT)
        response.raise_for_status()
        data = response.json()
        plan_id = data.get("id") 
        if plan_id:
            plan_id_str = str(plan_id)
            ui.console.print(f"   -> [green]Sukses: Ditemukan Plan ID: {plan_id_str}[/green]"); return plan_id_str
        else: ui.console.print("   -> [bold red]ERROR: /config/ tidak mengembalikan 'id' plan.[/bold red]"); return None
    except requests.exceptions.HTTPError as e:
        if e.response.status_code == 401: ui.console.print("   -> [bold red]ERROR: API Key tidak valid.[/bold red]")
        else: ui.console.print(f"   -> [bold red]ERROR: HTTP Error: {e.response.text}[/bold red]")
        return None
    except requests.RequestException as e: ui.console.print(f"   -> [bold red]ERROR: Gagal koneksi ke Webshare: {e}[/bold red]"); return None

# === PERUBAHAN KRITIS v6 ===
def get_authorized_ips(session: requests.Session, plan_id: str):
    """Mengambil IP terotorisasi DAN ID internalnya."""
    ui.console.print("3. Mengambil IP terotorisasi yang ada...")
    params = {"plan_id": plan_id} 
    ip_to_id_map = {} # <-- Kita return dictionary sekarang
    try:
        response = session.get(WEBSHARE_AUTH_URL, params=params, timeout=WEBSHARE_API_TIMEOUT)
        response.raise_for_status()
        results = response.json().get("results", [])
        for item in results:
            ip = item.get("ip_address")
            auth_id = item.get("id")
            if ip and auth_id:
                ip_to_id_map[ip] = auth_id # Simpan pasangan IP -> ID

        if not ip_to_id_map:
            ui.console.print("   -> Tidak ada IP lama yang terotorisasi.")
        else:
            ui.console.print(f"   -> Ditemukan IP lama: {', '.join(ip_to_id_map.keys())}")
        return ip_to_id_map # Return map
    except requests.RequestException as e:
        ui.console.print(f"   -> [bold red]ERROR: Gagal mengambil IP lama: {e}[/bold red]"); return {}
# === AKHIR PERUBAHAN v6 ===

# === PERUBAHAN KRITIS v6 ===
def remove_ip(session: requests.Session, ip: str, authorization_id: int, plan_id: str):
    """Menghapus satu IP dari otorisasi Webshare MENGGUNAKAN ID OTORISASI."""
    ui.console.print(f"   -> Menghapus IP lama: {ip} (ID: {authorization_id})")
    params = {"plan_id": plan_id} 
    
    # URL sekarang SPESIFIK ke ID otorisasi
    delete_url = f"{WEBSHARE_AUTH_URL}{authorization_id}/" 
    
    try:
        # Panggil DELETE ke URL spesifik, TANPA body/payload
        response = session.delete(
            delete_url, 
            params=params, 
            timeout=WEBSHARE_API_TIMEOUT
        )

        # Status sukses adalah 204 (No Content)
        if response.status_code == 204:
            ui.console.print(f"   -> [green]Sukses hapus: {ip}[/green]")
        else:
            ui.console.print(f"   -> [bold red]ERROR: Gagal hapus {ip} (Status: {response.status_code})[/bold red]")
            try: ui.console.print(f"      {response.json()}")
            except: ui.console.print(f"      {response.text}")
            response.raise_for_status() 
            
    except requests.RequestException as e:
        ui.console.print(f"   -> [bold red]ERROR: Gagal hapus {ip}[/bold red]")
        try: ui.console.print(f"      {e.response.text}")
        except: ui.console.print(f"      {e}")
# === AKHIR PERUBAHAN v6 ===


def add_ip(session: requests.Session, ip: str, plan_id: str):
    """Menambahkan satu IP ke otorisasi Webshare."""
    ui.console.print(f"   -> Menambahkan IP baru: {ip}")
    params = {"plan_id": plan_id} 
    payload = {"ip_address": ip}
    try:
        response = session.post(WEBSHARE_AUTH_URL, json=payload, params=params, timeout=WEBSHARE_API_TIMEOUT)
        if response.status_code == 201: ui.console.print(f"   -> [green]Sukses tambah: {ip}[/green]")
        else:
            ui.console.print(f"   -> [bold red]ERROR: Gagal tambah {ip} (Status: {response.status_code})[/bold red]")
            try: ui.console.print(f"      {response.json()}")
            except: ui.console.print(f"      {response.text}")
            response.raise_for_status() 
    except requests.RequestException as e:
        ui.console.print(f"   -> [bold red]ERROR: Gagal tambah {ip}[/bold red]")
        try: ui.console.print(f"      {e.response.text}")
        except: ui.console.print(f"      {e}")

def run_webshare_ip_sync():
    """Fungsi orkestrasi untuk sinkronisasi IP Webshare (Menu 1)."""
    ui.print_header()
    ui.console.print("[bold cyan]--- Sinkronisasi IP Otorisasi Webshare ---[/bold cyan]")
    api_keys = load_webshare_apikeys(WEBSHARE_APIKEYS_FILE)
    if not api_keys: ui.console.print(f"[bold red]File '{WEBSHARE_APIKEYS_FILE}' kosong atau tidak ditemukan.[/bold red]"); return
    new_ip = get_current_public_ip()
    if not new_ip: ui.console.print("[bold red]Gagal mendapatkan IP publik. Proses dibatalkan.[/bold red]"); return
    ui.console.print(f"\nAkan menyinkronkan IP [bold]{new_ip}[/bold] ke [bold]{len(api_keys)}[/bold] akun...")

    for api_key in api_keys:
        ui.console.print(f"\n--- Memproses API Key: [...{api_key[-6:]}] ---")
        with requests.Session() as session:
            session.headers.update({"Authorization": f"Token {api_key}", "Accept": "application/json"})
            try:
                plan_id = get_target_plan_id(session) 
                if not plan_id: ui.console.print(f"   -> [bold red]Akun dilewati.[/bold red]"); continue
                
                # === PERUBAHAN KRITIS v6 ===
                authorized_ips_map = get_authorized_ips(session, plan_id) # Dapat map IP -> ID
                existing_ips = list(authorized_ips_map.keys()) # Ambil list IP saja
                # === AKHIR PERUBAHAN v6 ===

                if new_ip in existing_ips:
                    ui.console.print(f"   -> [green]IP baru ({new_ip}) sudah terotorisasi. Tidak ada perubahan.[/green]")
                    continue 

                ui.console.print("\n4. Memeriksa IP lama untuk dihapus...")
                if not existing_ips:
                    ui.console.print("   -> Tidak ada IP lama yang perlu dihapus.")
                else:
                    # === PERUBAHAN KRITIS v6 ===
                    # Loop pakai map untuk dapet ID nya
                    for ip_to_delete, auth_id_to_delete in authorized_ips_map.items():
                        remove_ip(session, ip_to_delete, auth_id_to_delete, plan_id) # Kirim ID nya
                    # === AKHIR PERUBAHAN v6 ===

                ui.console.print("\n5. Memeriksa IP baru untuk ditambahkan...")
                add_ip(session, new_ip, plan_id) 
            
            except Exception as e:
                ui.console.print(f"   -> [bold red]!!! ERROR saat proses Hapus/Tambah. Lanjut ke akun berikutnya.[/bold red]")
    
    ui.console.print("\n[bold green]✅ Proses sinkronisasi IP Webshare selesai.[/bold green]")

# --- LOGIKA WEBSHARE PROXY DOWNLOAD ---
def get_webshare_download_url(session: requests.Session, plan_id: str):
    ui.console.print("   -> Memanggil 'proxy/config/' untuk URL download...")
    params = {"plan_id": plan_id} 
    try:
        response = session.get(WEBSHARE_CONFIG_URL, params=params, timeout=WEBSHARE_API_TIMEOUT)
        response.raise_for_status()
        data = response.json()
        username = data.get("username"); token = data.get("proxy_list_download_token")
        if not username or not token: ui.console.print("   -> [bold red]ERROR: 'username' atau 'token' tidak ditemukan.[/bold red]"); return None
        download_url = WEBSHARE_DOWNLOAD_URL_FORMAT.format(token=token, username=username, plan_id=plan_id)
        ui.console.print(f"   -> [green]Sukses generate URL download.[/green]"); return download_url
    except requests.exceptions.HTTPError as e: ui.console.print(f"   -> [bold red]ERROR: Gagal config proxy: {e.response.text}[/bold red]"); return None
    except requests.RequestException as e: ui.console.print(f"   -> [bold red]ERROR: Gagal koneksi (config): {e}[/bold red]"); return None

def download_proxies_from_api():
    ui.print_header()
    ui.console.print("[bold cyan]--- Unduh Proksi dari Daftar API ---[/bold cyan]")
    if os.path.exists(PROXYLIST_SOURCE_FILE) and os.path.getsize(PROXYLIST_SOURCE_FILE) > 0:
        choice = ui.Prompt.ask(f"[bold yellow]'{PROXYLIST_SOURCE_FILE}' berisi data. Hapus konten lama?[/bold yellow]", choices=["y", "n"], default="y").lower()
        if choice == 'n': ui.console.print("[cyan]Operasi dibatalkan.[/cyan]"); return
    try:
        with open(PROXYLIST_SOURCE_FILE, "w") as f: pass
        ui.console.print(f"[green]'{PROXYLIST_SOURCE_FILE}' siap diisi data baru.[/green]\n")
    except IOError as e: ui.console.print(f"[bold red]Gagal membersihkan file: {e}[/bold red]"); return

    all_download_urls = []
    ui.console.print(f"[bold]Memulai Auto-Discover dari '{WEBSHARE_APIKEYS_FILE}'...[/bold]")
    api_keys = load_webshare_apikeys(WEBSHARE_APIKEYS_FILE)
    if not api_keys: ui.console.print(f"[yellow]'{WEBSHARE_APIKEYS_FILE}' kosong. Lanjut...[/yellow]")
    for api_key in api_keys:
        ui.console.print(f"\n--- Memproses API Key: [...{api_key[-6:]}] ---")
        with requests.Session() as session:
            session.headers.update({"Authorization": f"Token {api_key}", "Accept": "application/json"})
            try:
                plan_id = get_target_plan_id(session)
                if not plan_id: ui.console.print(f"   -> [bold red]Akun dilewati.[/bold red]"); continue
                download_url = get_webshare_download_url(session, plan_id) 
                if download_url: all_download_urls.append(download_url)
                else: ui.console.print("   -> [yellow]Gagal generate URL download. Dilewati.[/yellow]")
            except Exception as e: ui.console.print(f"   -> [bold red]!!! FATAL ERROR: {e}[/bold red]")

    ui.console.print(f"\n[bold]Memuat URL manual dari '{APILIST_SOURCE_FILE}'...[/bold]")
    manual_urls = load_apis(APILIST_SOURCE_FILE)
    if not manual_urls: ui.console.print(f"[yellow]'{APILIST_SOURCE_FILE}' kosong.[/yellow]")
    else: ui.console.print(f"[green]Ditemukan {len(manual_urls)} URL manual.[/green]"); all_download_urls.extend(manual_urls)

    if not all_download_urls: ui.console.print("\n[bold red]Tidak ada URL API untuk diunduh.[/bold red]"); return
    ui.console.print(f"\n[bold cyan]Siap mengunduh dari total {len(all_download_urls)} URL...[/bold cyan]")
    all_downloaded_proxies = ui.run_sequential_api_downloads(all_download_urls)
    if not all_downloaded_proxies: ui.console.print("\n[bold yellow]Tidak ada proksi yang berhasil diunduh.[/bold yellow]"); return

    try:
        with open(PROXYLIST_SOURCE_FILE, "w") as f:
            for proxy in all_downloaded_proxies: f.write(proxy + "\n")
        ui.console.print(f"\n[bold green]✅ {len(all_downloaded_proxies)} proksi baru disimpan ke '{PROXYLIST_SOURCE_FILE}'[/bold green]")
    except IOError as e: ui.console.print(f"\n[bold red]Gagal menulis ke '{PROXYLIST_SOURCE_FILE}': {e}[/bold red]")

# --- SISA FUNGSI (Konversi, Tes, Distribusi) ---
def convert_proxylist_to_http():
    if not os.path.exists(PROXYLIST_SOURCE_FILE): ui.console.print(f"[bold red]Error: '{PROXYLIST_SOURCE_FILE}' tidak ditemukan.[/bold red]"); return
    try:
        with open(PROXYLIST_SOURCE_FILE, "r") as f: lines = f.readlines()
    except Exception as e: ui.console.print(f"[bold red]Gagal baca '{PROXYLIST_SOURCE_FILE}': {e}[/bold red]"); return
    cleaned_proxies = [line.strip() for line in lines if line.strip()]
    if not cleaned_proxies: ui.console.print(f"[yellow]'{PROXYLIST_SOURCE_FILE}' kosong.[/yellow]"); return
    ui.console.print(f"Mengonversi {len(cleaned_proxies)} proksi...")
    converted_proxies = []; skipped_count = 0
    for p in cleaned_proxies:
        p = p.strip();
        if not p: continue
        if p.startswith("http://") or p.startswith("https://"): converted_proxies.append(p); continue
        parts = p.split(':')
        if len(parts) == 2:
            if re.match(r"^\d{1,3}(\.\d{1,3}){3}$", parts[0]) and parts[1].isdigit(): converted_proxies.append(f"http://{parts[0]}:{parts[1]}")
            elif '.' in parts[0] and parts[1].isdigit(): converted_proxies.append(f"http://{parts[0]}:{parts[1]}")
            else: skipped_count += 1
        elif len(parts) == 4 and '@' not in parts[0] and '@' not in parts[1]:
             if re.match(r"^\d{1,3}(\.\d{1,3}){3}$", parts[2]) and parts[3].isdigit(): converted_proxies.append(f"http://{parts[0]}:{parts[1]}@{parts[2]}:{parts[3]}")
             elif '.' in parts[2] and parts[3].isdigit(): converted_proxies.append(f"http://{parts[0]}:{parts[1]}@{parts[2]}:{parts[3]}")
             else: skipped_count += 1
        elif len(parts) >= 3 and '@' in p: converted_proxies.append(f"http://{p}")
        else: skipped_count += 1
    if skipped_count > 0: ui.console.print(f"[yellow]{skipped_count} baris dilewati (format tidak dikenali).[/yellow]")
    if not converted_proxies: ui.console.print("[bold red]Tidak ada proksi yang dikonversi.[/bold red]"); return
    try:
        with open(PROXY_SOURCE_FILE, "w") as f:
            for proxy in converted_proxies: f.write(proxy + "\n")
        open(PROXYLIST_SOURCE_FILE, "w").close()
        ui.console.print(f"[bold green]✅ {len(converted_proxies)} proksi dikonversi ke '{PROXY_SOURCE_FILE}'.[/bold green]")
        ui.console.print(f"[bold cyan]'{PROXYLIST_SOURCE_FILE}' dikosongkan.[/bold cyan]")
    except Exception as e: ui.console.print(f"[bold red]Gagal menulis file: {e}[/bold red]")

def load_and_deduplicate_proxies(file_path):
    if not os.path.exists(file_path): return []
    try:
        with open(file_path, "r") as f: proxies = [line.strip() for line in f if line.strip()]
    except Exception as e: ui.console.print(f"[bold red]Gagal baca '{file_path}': {e}[/bold red]"); return []
    unique_proxies = sorted(list(set(proxies))); duplicates_removed = len(proxies) - len(unique_proxies)
    if duplicates_removed > 0: ui.console.print(f"[yellow]Menghapus {duplicates_removed} duplikat.[/yellow]")
    try:
        with open(file_path, "w") as f:
            for proxy in unique_proxies: f.write(proxy + "\n")
    except Exception as e: ui.console.print(f"[bold red]Gagal tulis '{file_path}' (dedup): {e}[/bold red]"); return proxies 
    return unique_proxies

def load_paths(file_path):
    if not os.path.exists(file_path): ui.console.print(f"[bold red]Error: '{file_path}' tidak ditemukan.[/bold red]"); return []
    try:
        with open(file_path, "r") as f:
            project_root = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
            raw_paths = [line.strip() for line in f if line.strip() and not line.startswith("#")]
            absolute_paths = []; invalid_paths = 0
            for p in raw_paths:
                abs_p = os.path.join(project_root, p)
                if os.path.isdir(abs_p): absolute_paths.append(abs_p)
                else: invalid_paths += 1; ui.console.print(f"[yellow]Path tidak valid: {abs_p} (dari '{p}') [/yellow]")
            if invalid_paths > 0: ui.console.print(f"[yellow]{invalid_paths} path dilewati.[/yellow]")
            return absolute_paths
    except Exception as e: ui.console.print(f"[bold red]Gagal baca '{file_path}': {e}[/bold red]"); return []

def backup_file(file_path, backup_path):
    if os.path.exists(file_path):
        try: shutil.copy(file_path, backup_path); ui.console.print(f"[green]Backup dibuat: '{backup_path}'[/green]")
        except Exception as e: ui.console.print(f"[bold red]Gagal backup '{backup_path}': {e}[/bold red]")

def check_proxy_final(proxy):
    if GITHUB_TEST_TOKEN is None: return proxy, False, "Token GitHub tidak dimuat"
    proxies_dict = {"http": proxy, "https": proxy}
    headers = {'User-Agent': 'ProxySync-Tester/1.0', 'Authorization': f'Bearer {GITHUB_TEST_TOKEN}', 'Accept': 'application/vnd.github.v3+json'}
    try:
        response = requests.get(GITHUB_API_TEST_URL, proxies=proxies_dict, timeout=PROXY_TIMEOUT, headers=headers)
        if response.status_code == 401: return proxy, False, "GitHub Auth Gagal (401)"
        if response.status_code == 403: return proxy, False, "GitHub Forbidden (403)"
        if response.status_code == 407: return proxy, False, "Proxy Auth Gagal (407)"
        if response.status_code == 429: return proxy, False, "GitHub Rate Limit (429)"
        response.raise_for_status()
        if response.text and len(response.text) > 5: return proxy, True, "OK"
        else: return proxy, False, "Respons GitHub tidak valid"
    except requests.exceptions.Timeout: return proxy, False, f"Timeout ({PROXY_TIMEOUT}s)"
    except requests.exceptions.ProxyError as e: reason = str(e).split(':')[-1].strip(); return proxy, False, f"Proxy Error ({reason[:30]})"
    except requests.exceptions.RequestException as e: reason = str(e.__class__.__name__); return proxy, False, f"Koneksi Gagal ({reason})"

def distribute_proxies(proxies, paths):
    if not proxies or not paths: ui.console.print("[yellow]Distribusi dilewati (tidak ada data).[/yellow]"); return
    ui.console.print(f"\n[cyan]Mendistribusikan {len(proxies)} proksi valid ke {len(paths)} path...[/cyan]")
    project_root_abs = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
    for path in paths:
        if not os.path.isdir(path): ui.console.print(f"  [yellow]✖ Lewati:[/yellow] Path tidak valid: {path}"); continue
        file_name = "proxies.txt"; file_path = os.path.join(path, file_name)
        if not os.path.exists(file_path): file_name = "proxy.txt"; file_path = os.path.join(path, file_name)
        rel_path_display = os.path.relpath(file_path, project_root_abs)
        proxies_shuffled = random.sample(proxies, len(proxies))
        try:
            with open(file_path, "w") as f:
                for proxy in proxies_shuffled: f.write(proxy + "\n")
            ui.console.print(f"  [green]✔[/green] Berhasil menulis ke [bold]{rel_path_display}[/bold]")
        except IOError as e: ui.console.print(f"  [red]✖[/red] Gagal menulis ke [bold]{rel_path_display}[/bold]: {e}")

def save_good_proxies(proxies, file_path):
    try:
        with open(file_path, "w") as f:
            for proxy in proxies: f.write(proxy + "\n")
        ui.console.print(f"\n[bold green]✅ {len(proxies)} proksi valid disimpan ke '{file_path}'[/bold green]")
    except IOError as e: ui.console.print(f"\n[bold red]✖ Gagal simpan proksi ke '{file_path}': {e}[/bold red]")

def run_full_process():
    ui.print_header()
    if not load_github_token(GITHUB_TOKENS_FILE): ui.console.print("[bold red]Tes proxy dibatalkan (token GitHub?)[/bold red]"); return
    distribute_choice = ui.Prompt.ask("[bold yellow]Distribusikan proksi valid ke path target?[/bold yellow]", choices=["y", "n"], default="y").lower()
    ui.console.print("-" * 40); ui.console.print("[bold cyan]Langkah 1: Backup & Bersihkan Proksi...[/bold cyan]")
    backup_file(PROXY_SOURCE_FILE, PROXY_BACKUP_FILE)
    proxies = load_and_deduplicate_proxies(PROXY_SOURCE_FILE)
    if not proxies: ui.console.print("[bold red]Berhenti: 'proxy.txt' kosong.[/bold red]"); return
    ui.console.print(f"Siap menguji {len(proxies)} proksi unik ke GitHub API."); ui.console.print("-" * 40)
    ui.console.print("[bold cyan]Langkah 2: Tes Akurat ke GitHub API...[/bold cyan]")
    good_proxies = ui.run_concurrent_checks_display(proxies, check_proxy_final, MAX_WORKERS, FAIL_PROXY_FILE)
    if not good_proxies: ui.console.print("[bold red]Berhenti: Tidak ada proksi lolos tes GitHub.[/bold red]"); return
    ui.console.print(f"[bold green]Ditemukan {len(good_proxies)} proksi berfungsi.[/bold green]"); ui.console.print("-" * 40)
    if distribute_choice == 'y':
        ui.console.print("[bold cyan]Langkah 3: Distribusi...[/bold cyan]")
        paths = load_paths(PATHS_SOURCE_FILE)
        if not paths: ui.console.print("[bold red]Berhenti: 'paths.txt' kosong/invalid.[/bold red]"); return
        distribute_proxies(good_proxies, paths)
        save_good_proxies(good_proxies, SUCCESS_PROXY_FILE)
    else: ui.console.print("[bold cyan]Langkah 3: Menyimpan proksi valid...[/bold cyan]"); save_good_proxies(good_proxies, SUCCESS_PROXY_FILE)
    ui.console.print("\n[bold green]✅ Semua tugas selesai![/bold green]")

def main():
    os.chdir(os.path.dirname(os.path.abspath(__file__)))
    while True:
        ui.print_header(); choice = ui.display_main_menu()
        if choice == "1": run_webshare_ip_sync(); ui.Prompt.ask("\n[bold]Tekan Enter...[/bold]")
        elif choice == "2": download_proxies_from_api(); ui.Prompt.ask("\n[bold]Tekan Enter...[/bold]")
        elif choice == "3": convert_proxylist_to_http(); ui.Prompt.ask("\n[bold]Tekan Enter...[/bold]")
        elif choice == "4": run_full_process(); ui.Prompt.ask("\n[bold]Tekan Enter...[/bold]")
        elif choice == "5": ui.manage_paths_menu_display() # Placeholder
        elif choice == "6": ui.console.print("[bold cyan]Sampai jumpa![/bold cyan]"); break

if __name__ == "__main__":
    main()
