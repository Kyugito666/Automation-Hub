import os
import sys
import requests
import ui # Mengimpor semua fungsi UI dari file ui.py

# --- Konfigurasi Path ---
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, '..', 'config'))
GITHUB_TOKENS_FILE = os.path.join(CONFIG_DIR, "github_tokens.txt")

# --- Konfigurasi Tes Proxy ---
PROXY_TIMEOUT = 25
GITHUB_API_TEST_URL = "https://api.github.com/zen"

# --- Variabel Global ---
GITHUB_TEST_TOKEN = None

def load_github_token(file_path = GITHUB_TOKENS_FILE):
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
