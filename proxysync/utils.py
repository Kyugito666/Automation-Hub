import os
import re
import shutil
import random
import ui # Mengimpor semua fungsi UI dari file ui.py
from pathlib import Path

# --- Konfigurasi Path ---
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, '..', 'config'))

PROXYLIST_SOURCE_FILE = os.path.join(SCRIPT_DIR, "proxylist.txt") # Input format asli
PROXY_SOURCE_FILE = os.path.join(SCRIPT_DIR, "proxy.txt")         # Input format http://
PATHS_SOURCE_FILE = os.path.join(CONFIG_DIR, "paths.txt")
APILIST_SOURCE_FILE = os.path.join(CONFIG_DIR, "apilist.txt")     # Daftar URL download
WEBSHARE_APIKEYS_FILE = os.path.join(CONFIG_DIR, "apikeys.txt")   # API Keys Webshare
SUCCESS_PROXY_FILE = os.path.join(SCRIPT_DIR, "success_proxy.txt") # Output proxy sukses
PROXY_BACKUP_FILE = os.path.join(SCRIPT_DIR, "proxy_backup.txt")   # Backup proxy.txt

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
