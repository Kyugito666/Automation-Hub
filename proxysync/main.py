print("DEBUG: Starting main.py execution", flush=True)
import os
import sys
import argparse
import traceback

# Mengimpor modul-modul baru
import ui
import webshare
import flows
import utils
import tester

# --- Konfigurasi Path (Hanya yang diperlukan main.py) ---
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, '..', 'config'))

PROXY_SOURCE_FILE = os.path.join(SCRIPT_DIR, "proxy.txt")         # Input format http://
PATHS_SOURCE_FILE = os.path.join(CONFIG_DIR, "paths.txt")
FAIL_PROXY_FILE = os.path.join(SCRIPT_DIR, "fail_proxy.txt")       # Output proxy gagal
SUCCESS_PROXY_FILE = os.path.join(SCRIPT_DIR, "success_proxy.txt") # Output proxy sukses
PROXY_BACKUP_FILE = os.path.join(SCRIPT_DIR, "proxy_backup.txt")   # Backup proxy.txt

# --- Konfigurasi Tes Proxy ---
MAX_WORKERS = 10


def run_full_process():
    ui.print_header()
    if not tester.load_github_token(): # Panggil dari tester
        ui.console.print("[bold red]Tes proxy dibatalkan (token GitHub?).[/bold red]"); return
    
    distribute_choice = ui.Prompt.ask("[bold yellow]Distribusi proksi valid ke folder bot (sesuai paths.txt)? (y/n)[/bold yellow]", choices=["y", "n"], default="y").lower()
    ui.console.print("-" * 40); ui.console.print("[bold cyan]Langkah 1: Backup & Clean...[/bold cyan]")
    
    utils.backup_file(PROXY_SOURCE_FILE, PROXY_BACKUP_FILE) # Panggil dari utils
    proxies = utils.load_and_deduplicate_proxies(PROXY_SOURCE_FILE) # Panggil dari utils
    
    if not proxies: ui.console.print(f"[bold red]Stop: '{os.path.basename(PROXY_SOURCE_FILE)}' kosong.[/bold red]"); return
    
    ui.console.print(f"Siap tes {len(proxies)} proksi unik."); ui.console.print("-" * 40)
    ui.console.print("[bold cyan]Langkah 2: Tes Akurat GitHub...[/bold cyan]")
    
    # === PERBAIKAN: Kirim lambda dengan is_auto=False untuk mode Manual ===
    check_func_manual = lambda p: tester.check_proxy_final(p, is_auto=False)
    good_proxies = ui.run_concurrent_checks_display(proxies, check_func_manual, MAX_WORKERS, FAIL_PROXY_FILE) # Panggil dari tester
    # === AKHIR PERBAIKAN ===
    
    if not good_proxies: ui.console.print("[bold red]Stop: Tidak ada proksi lolos.[/bold red]"); return
    
    ui.console.print(f"[bold green]{len(good_proxies)} proksi lolos.[/bold green]"); ui.console.print("-" * 40)
    ui.console.print("[bold cyan]Langkah 3: Menyimpan Proksi Valid...[/bold cyan]") # Pindahkan log ini
    
    save_success = utils.save_good_proxies(good_proxies, SUCCESS_PROXY_FILE) # Panggil dari utils
    
    if not save_success:
        ui.console.print("[bold red]Gagal menyimpan hasil tes utama. Distribusi dibatalkan.[/bold red]")
        return
        
    if distribute_choice == 'y':
        ui.console.print("-" * 40) # Tambah separator
        ui.console.print("[bold cyan]Langkah 4: Distribusi...[/bold cyan]") # Update nomor langkah
        paths = utils.load_paths(PATHS_SOURCE_FILE) # Panggil dari utils
        if not paths: ui.console.print("[bold red]Stop: 'paths.txt' kosong/invalid. Distribusi dibatalkan.[/bold red]"); return
        utils.distribute_proxies(good_proxies, paths) # Panggil dari utils
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
            result = webshare.run_webshare_ip_sync() # Panggil dari webshare
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
                 result = flows.download_proxies_from_api(get_urls_only=True) # Panggil dari flows
            else: # sub_choice == 'b'
                 operation_name = "Unduh Proxy"
                 result = flows.download_proxies_from_api(get_urls_only=False) # Panggil dari flows

        elif choice == "3":
            operation_name = "Konversi Proxy"
            result = utils.convert_proxylist_to_http() # Panggil dari utils
        elif choice == "4S":
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
            success = webshare.run_webshare_ip_sync() # Panggil dari webshare
            if success: success = flows.download_proxies_from_api(is_auto=True) # Panggil dari flows
            if success: success = utils.convert_proxylist_to_http() # Panggil dari utils
            
            # === PERBAIKAN: Kirim is_auto=True ===
            if success: success = flows.run_automated_test_and_save(is_auto=True) # Panggil dari flows
            # === AKHIR PERBAIKAN ===
            
            if success: ui.console.print("\n[bold green]✅ FULL AUTO MODE SELESAI.[/bold green]")
            else: ui.console.print("\n[bold red]❌ FULL AUTO MODE GAGAL PADA SALAH SATU LANGKAH.[/bold red]"); exit_code = 1
        
        elif args.ip_auth_only:
            ui.console.print("[bold cyan]--- PROXYSYNC IP AUTH ONLY MODE ---[/bold cyan]")
            success = webshare.run_webshare_ip_sync() # Panggil dari webshare
            if success: ui.console.print("\n[bold green]✅ IP AUTH ONLY SELESAI.[/bold green]")
            else: ui.console.print("\n[bold red]❌ IP AUTH ONLY GAGAL.[/bold red]"); exit_code = 1
        
        elif args.get_urls_only:
             ui.console.print("[bold cyan]--- PROXYSYNC GET URLS ONLY MODE ---[/bold cyan]")
             success = flows.download_proxies_from_api(get_urls_only=True) # Panggil dari flows
             if success: ui.console.print("\n[bold green]✅ GET URLS ONLY SELESAI.[/bold green]")
             else: ui.console.print("\n[bold red]❌ GET URLS ONLY GAGAL.[/bold red]"); exit_code = 1
        
        elif args.test_and_save_only:
             ui.console.print("[bold cyan]--- PROXYSYNC TEST AND SAVE ONLY MODE ---[/bold cyan]")
             # === PERBAIKAN: Kirim is_auto=True (karena --test-and-save-only juga otomatis) ===
             success = flows.run_automated_test_and_save(is_auto=True) # Panggil dari flows
             # === AKHIR PERBAIKAN ===
             if success: ui.console.print("\n[bold green]✅ TEST AND SAVE ONLY SELESAI.[/bold green]")
             else: ui.console.print("\n[bold red]❌ TEST AND SAVE ONLY GAGAL.[/bold red]"); exit_code = 1
        
        else:
            main_interactive()
    except Exception as e:
         ui.console.print(f"\n[bold red]!!! TERJADI ERROR FATAL !!![/bold red]")
         traceback.print_exc()
         exit_code = 1
    finally:
         sys.exit(exit_code)
