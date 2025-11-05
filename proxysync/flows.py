import os
import time
import requests
import ui
import webshare
import utils
import tester

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, '..', 'config'))

PROXYLIST_SOURCE_FILE = os.path.join(SCRIPT_DIR, "proxylist.txt")
PROXY_SOURCE_FILE = os.path.join(SCRIPT_DIR, "proxy.txt")
APILIST_SOURCE_FILE = os.path.join(CONFIG_DIR, "apilist.txt")
WEBSHARE_APIKEYS_FILE = os.path.join(CONFIG_DIR, "apikeys.txt")
FAIL_PROXY_FILE = os.path.join(SCRIPT_DIR, "fail_proxy.txt")
SUCCESS_PROXY_FILE = os.path.join(SCRIPT_DIR, "success_proxy.txt")

MAX_WORKERS = 10

def download_proxies_from_api(is_auto=False, get_urls_only=False):
    ui.print_header()
    if get_urls_only:
        ui.console.print("[bold cyan]--- Discover & Simpan URL Download Proxy ---[/bold cyan]")
    else:
        ui.console.print("[bold cyan]--- Unduh Proksi dari API ---[/bold cyan]")

    existing_api_urls = utils.load_apis_from_file(APILIST_SOURCE_FILE)
    ui.console.print(f"Memuat URL dari '{os.path.basename(APILIST_SOURCE_FILE)}': {len(existing_api_urls)} URL ditemukan.")

    urls_to_process = set(existing_api_urls)
    newly_saved_count = 0
    discovery_failed = False

    if not existing_api_urls:
        ui.console.print(f"\n[bold]'{os.path.basename(APILIST_SOURCE_FILE)}' kosong. Memulai discovery dari API Keys...[/bold]")
        api_keys = utils.load_webshare_apikeys(WEBSHARE_APIKEYS_FILE)
        if not api_keys:
            ui.console.print(f"[yellow]'{os.path.basename(WEBSHARE_APIKEYS_FILE)}' kosong atau tidak ditemukan.[/yellow]")
            if is_auto: discovery_failed = True
        else:
            ui.console.print(f"Ditemukan {len(api_keys)} API Key untuk dicek.")
            for api_key in api_keys:
                account_email_info = "[grey]Mencoba mendapatkan email...[/]"
                try:
                    with requests.Session() as email_session:
                        email_session.headers.update({"Authorization": f"Token {api_key}", "Accept": "application/json"})
                        account_email_info = webshare.get_account_email(email_session)
                except Exception: account_email_info = "[bold red]Error[/]"
                ui.console.print(f"\n--- Mengecek Key: [...{api_key[-6:]}] (Email: {account_email_info}) ---")

                with requests.Session() as session:
                    session.headers.update({"Authorization": f"Token {api_key}", "Accept": "application/json"})
                    try:
                        plan_id = webshare.get_target_plan_id(session)
                        if not plan_id:
                            ui.console.print(f"   -> [bold red]Gagal mendapatkan Plan ID. Akun ini dilewati.[/bold red]")
                            discovery_failed = True
                            continue
                        download_url = webshare.get_webshare_download_url(session, plan_id)
                        if download_url:
                            if utils.save_discovered_url(APILIST_SOURCE_FILE, download_url):
                                 urls_to_process.add(download_url)
                                 newly_saved_count += 1
                            elif download_url in existing_api_urls:
                                 urls_to_process.add(download_url)
                        else:
                            ui.console.print("   -> [yellow]Gagal mendapatkan URL download untuk key ini. Dilewati.[/yellow]")
                            discovery_failed = True
                    except Exception as e:
                        ui.console.print(f"   -> [bold red]!!! TERJADI ERROR saat discover URL: {e}[/bold red]")
                        discovery_failed = True
                time.sleep(1)

            ui.console.print(f"\nSelesai discover URL dari API keys.")
            if newly_saved_count > 0:
                 ui.console.print(f"[green]{newly_saved_count} URL baru disimpan ke '{os.path.basename(APILIST_SOURCE_FILE)}'.[/green]")
    else:
        ui.console.print(f"\n[green]URL dari '{os.path.basename(APILIST_SOURCE_FILE)}' akan digunakan langsung (skip discovery).[/green]")

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
            for proxy in unique_proxies: f.write (proxy + "\n")
        ui.console.print(f"\n[bold green]✅ {len(unique_proxies)} proxy unik berhasil diunduh dan disimpan ke '{os.path.basename(PROXYLIST_SOURCE_FILE)}'[/bold green]")
        if duplicates_removed > 0: ui.console.print(f"[dim]   ({duplicates_removed} duplikat dihapus)[/dim]")
        return True
    except IOError as e:
        ui.console.print(f"\n[bold red]Gagal menulis hasil ke '{PROXYLIST_SOURCE_FILE}': {e}[/bold red]")
        return False

def run_automated_test_and_save(is_auto=False):
    ui.print_header()
    ui.console.print("[bold cyan]Mode Auto: Tes Akurat & Simpan Hasil...[/bold cyan]")
    
    if not is_auto:
        ui.console.print("[dim]   (Mode Manual/Lokal terdeteksi, memuat token GitHub...)[/dim]")
        if not tester.load_github_token():
            ui.console.print("[bold red]Tes proxy dibatalkan: Gagal memuat token GitHub.[/bold red]")
            return False
    else:
        ui.console.print("[dim]   (Mode Auto/Codespace terdeteksi, token GitHub dilewati. Tes ke ipify.org...)[/dim]")
        
    ui.console.print("-" * 40)
    ui.console.print("[bold cyan]Langkah 1: Memuat & Membersihkan Proxy Input...[/bold cyan]")
    proxies = utils.load_and_deduplicate_proxies(PROXY_SOURCE_FILE)
    if not proxies:
        ui.console.print(f"[bold red]Berhenti: File input '{os.path.basename(PROXY_SOURCE_FILE)}' kosong atau tidak ditemukan.[/bold red]")
        if os.path.exists(SUCCESS_PROXY_FILE):
            try: os.remove(SUCCESS_PROXY_FILE)
            except OSError as e: ui.console.print(f"[yellow] Gagal menghapus '{os.path.basename(SUCCESS_PROXY_FILE)}': {e}[/yellow]")
        return False
    ui.console.print(f"Siap menguji {len(proxies)} proksi unik dari '{os.path.basename(PROXY_SOURCE_FILE)}'.")
    ui.console.print("-" * 40)
    
    if is_auto:
        ui.console.print("[bold cyan]Langkah 2: Menjalankan Tes Cepat via ipify.org...[/bold cyan]")
    else:
        ui.console.print("[bold cyan]Langkah 2: Menjalankan Tes Akurat via GitHub API...[/bold cyan]")
    
    check_func_auto = lambda p: tester.check_proxy_final(p, is_auto=is_auto)
    good_proxies = ui.run_concurrent_checks_display(proxies, check_func_auto, MAX_WORKERS, FAIL_PROXY_FILE)
    
    if not good_proxies:
        ui.console.print("[bold red]Berhenti: Tidak ada proksi yang lolos tes.[/bold red]")
        if os.path.exists(SUCCESS_PROXY_FILE):
            try: os.remove(SUCCESS_PROXY_FILE)
            except OSError as e: ui.console.print(f"[yellow] Gagal menghapus '{os.path.basename(SUCCESS_PROXY_FILE)}': {e}[/yellow]")
        return False
    ui.console.print(f"[bold green]{len(good_proxies)} proksi lolos tes.[/bold green]")
    ui.console.print("-" * 40)
    ui.console.print("[bold cyan]Langkah 3: Menyimpan Proksi Valid...[/bold cyan]")
    save_success = utils.save_good_proxies(good_proxies, SUCCESS_PROXY_FILE)
    if save_success:
        ui.console.print("\n[bold green]✅ Tes otomatis dan penyimpanan selesai![/bold green]")
        return True
    else:
        ui.console.print("\n[bold red]❌ Tes otomatis selesai, namun GAGAL menyimpan hasil.[/bold red]")
        return False
