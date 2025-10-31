import os
import sys
import json
import time
import requests
import ui # Mengimpor semua fungsi UI dari file ui.py

# --- Konfigurasi Path ---
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, '..', 'config'))
WEBSHARE_APIKEYS_FILE = os.path.join(CONFIG_DIR, "apikeys.txt")

# --- Konfigurasi Webshare ---
WEBSHARE_AUTH_URL = "https://proxy.webshare.io/api/v2/proxy/ipauthorization/"
WEBSHARE_SUB_URL = "https://proxy.webshare.io/api/v2/subscription/"
WEBSHARE_CONFIG_URL = "https://proxy.webshare.io/api/v2/proxy/config/"
WEBSHARE_PROFILE_URL = "https://proxy.webshare.io/api/v2/profile/"
WEBSHARE_DOWNLOAD_URL_BASE = "https://proxy.webshare.io/api/v2/proxy/list/download/{token}/-/any/username/direct/-/"
WEBSHARE_DOWNLOAD_URL_FORMAT = WEBSHARE_DOWNLOAD_URL_BASE + "?plan_id={plan_id}"
IP_CHECK_SERVICE_URL = "https://api.ipify.org?format=json"

# Timeout API Webshare
WEBSHARE_API_TIMEOUT = 60

# Import utilitas load API keys (dipindah ke utils.py)
from utils import load_webshare_apikeys

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
