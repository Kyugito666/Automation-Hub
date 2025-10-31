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

# === PERUBAHAN: Ganti target tunggal jadi list yang lebih akurat ===
GITHUB_TEST_TARGETS = [
    ("API (zen)", "https://api.github.com/zen"),
    ("Main (web)", "https://github.com/"),
    ("Login (web)", "https://github.com/login")
]
# === AKHIR PERUBAHAN ===

# --- Variabel Global ---
GITHUB_TEST_TOKEN = None

def load_github_token(file_path = GITHUB_TOKENS_FILE):
    global GITHUB_TEST_TOKEN
    try:
        if not os.path.exists(file_path): ui.console.print(f"[bold red]Error: '{file_path}' tidak ada.[/bold red]"); return False
        with open(file_path, "r") as f: lines = f.readlines()
        if len(lines) < 3: ui.console.print(f"[bold red]Error: Format '{file_path}' salah.[/bold red]"); return False
        tokens_line = lines[2].strip()
        if not tokens_line: ui.console.print(f"[bold red]Error: Baris 3 (tokens) di '{file_path}' kosong.[/bold red]"); return False
        first_token = tokens_line.split(',')[0].strip()
        if not first_token or not (first_token.startswith("ghp_") or first_token.startswith("github_pat_")): ui.console.print(f"[bold red]Error: Token awal di baris 3 '{file_path}' invalid.[/bold red]"); return False
        GITHUB_TEST_TOKEN = first_token
        ui.console.print(f"[green]✓ Token GitHub untuk testing OK (dari baris 3).[/green]"); return True
    except IndexError:
         ui.console.print(f"[bold red]Error: Baris 3 (tokens) di '{file_path}' format salah.[/bold red]"); return False
    except Exception as e: ui.console.print(f"[bold red]Gagal load token GitHub: {e}[/bold red]"); return False

def check_proxy_final(proxy):
    if GITHUB_TEST_TOKEN is None: 
        return proxy, False, "Token GitHub?" 
        
    proxies_dict = {"http": proxy, "https": proxy}
    # Headers untuk API (pakai token)
    api_headers = {
        'User-Agent': 'ProxySync-Tester/3.1', 
        'Authorization': f'Bearer {GITHUB_TEST_TOKEN}', 
        'Accept': 'application/vnd.github.v3+json'
    }
    # Headers untuk Web (tanpa token, simulasi browser)
    web_headers = {
        'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36',
        'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9',
        'Accept-Language': 'en-US,en;q=0.9',
    }

    # === PERUBAHAN: Loop testing ke semua target ===
    for name, url in GITHUB_TEST_TARGETS:
        is_api = "api.github.com" in url
        headers = api_headers if is_api else web_headers

        try:
            response = requests.get(url, proxies=proxies_dict, timeout=PROXY_TIMEOUT, headers=headers)

            # Cek error spesifik
            if response.status_code == 401: return proxy, False, f"GitHub Auth ({name} 401)"
            if response.status_code == 403: return proxy, False, f"GitHub Forbidden ({name} 403)"
            if response.status_code == 407: return proxy, False, "Proxy Auth (407)"
            if response.status_code == 429: return proxy, False, f"GitHub Rate Limit ({name} 429)"
            response.raise_for_status() 

            # Kriteria lolos:
            # API (zen) harus ada respons > 5 char
            # Web (github.com / login) harus ada 'github' di 1000 char pertama
            content = response.text
            if is_api:
                if not (content and len(content) > 5):
                    return proxy, False, f"Respons {name}?"
            else:
                if not (content and "github" in content.lower()[:1000]):
                     return proxy, False, f"Respons {name}?"
            
            # Jika lolos 1 URL, lanjut ke URL berikutnya
            
        except requests.exceptions.Timeout: 
            return proxy, False, f"Timeout {name} ({PROXY_TIMEOUT}s)"
        except requests.exceptions.ProxyError as e: 
            reason = str(e).split(':')[-1].strip()
            return proxy, False, f"Proxy Error {name} ({reason[:30]})"
        except requests.exceptions.RequestException as e: 
            reason = str(e.__class__.__name__)
            return proxy, False, f"Koneksi Gagal {name} ({reason})"
    
    # Jika berhasil melewati SEMUA URL di loop:
    return proxy, True, "OK (All targets)"
    # === AKHIR PERUBAHAN ===
