import asyncio
import signal
import sys
from typing import Optional

from rich.console import Console
from rich.panel import Panel # Hapus Figlet, pakai Panel
# from pyfiglet import Figlet # Hapus import Figlet
import questionary

from botconfig import BotConfig
from tokenmanager import TokenManager
import gitmanager
import updater
import proxymanager
import dispatcher
import hybridrunner
import botrunner

console = Console()

# Event untuk meng-handle Ctrl+C
main_cancel_event = asyncio.Event()
child_process_cancel_event = asyncio.Event()
# Flag untuk menandai jika kita sedang di dalam sub-proses (bot run)
child_process_active = False 


def handle_signal(sig, frame):
    """Handler untuk Ctrl+C."""
    global child_process_active
    # 1. Jika child process aktif & BELUM dibatalkan, batalkan child process
    if child_process_active and not child_process_cancel_event.is_set():
        console.print("\n[yellow]Ctrl+C terdeteksi. Membatalkan bot run...[/]")
        child_process_cancel_event.set()
    # 2. Jika tidak, batalkan menu utama
    elif not main_cancel_event.is_set():
        console.print("\n[yellow]Ctrl+C terdeteksi. Membatalkan menu...[/]")
        main_cancel_event.set()
    # 3. Jika semua sudah dibatalkan, informasikan
    else:
        console.print("[grey](Pembatalan sedang diproses...)[/]")

# === LOGIKA MONKEY-PATCHING LAMA DIHAPUS (baris 43-50 di file lama) ===


async def pause(cancel_event: asyncio.Event):
    """Gantinya Console.ReadLine(), tapi bisa di-cancel."""
    console.print("\n[grey]Tekan Enter untuk lanjut... (Ctrl+C untuk batal)[/]")
    try:
        # Menjalankan input() di thread terpisah agar tidak memblokir asyncio
        await asyncio.wait_for(asyncio.to_thread(sys.stdin.readline), timeout=None)
    except asyncio.CancelledError:
        console.print("[yellow]Pause dibatalkan.[/]")
        raise


async def pause_without_cancel():
    """Pause yang tidak bisa di-cancel (untuk error)."""
    console.print("\n[grey]Tekan Enter untuk lanjut...[/]")
    await asyncio.to_thread(sys.stdin.readline)


async def show_setup_menu(cancel_event: asyncio.Event):
    while not cancel_event.is_set():
        console.clear()
        # --- GANTI FIGLET JADI PANEL ---
        console.print(Panel("[bold yellow]Setup & Konfigurasi[/]", expand=False, border_style="yellow"), justify="center")
        # -------------------------------
        try:
            choice = await questionary.select(
                "[bold yellow]SETUP & KONFIGURASI[/]",
                choices=[
                    "1. Validasi Token & Ambil Username",
                    "2. Undang Kolaborator",
                    "3. Terima Undangan",
                    "4. Tampilkan Status Token/Proxy",
                    "5. [[SYSTEM]] Refresh Semua Config (Reload file)",
                    "0. [[Back]] Kembali ke Menu Utama",
                ],
                use_shortcuts=True,
            ).ask_async()

            if not choice or choice.startswith("0"):
                return

            selection = choice.split(".")[0]
            pause_needed = True

            if selection == "1":
                await gitmanager.validate_all_tokens()
            elif selection == "2":
                await gitmanager.invite_collaborators()
            elif selection == "3":
                await gitmanager.accept_invitations()
            elif selection == "4":
                TokenManager.show_status()
            elif selection == "5":
                TokenManager.reload_all_configs()
            else:
                pause_needed = False

            if pause_needed:
                await pause(cancel_event)

        except (asyncio.CancelledError, KeyboardInterrupt):
            main_cancel_event.set()
            return


async def show_local_menu(cancel_event: asyncio.Event):
    while not cancel_event.is_set():
        console.clear()
        # --- GANTI FIGLET JADI PANEL ---
        console.print(Panel("[bold green]Local Management[/]", expand=False, border_style="green"), justify="center")
        # -------------------------------
        try:
            choice = await questionary.select(
                "[bold green]LOCAL BOT & PROXY MANAGEMENT[/]",
                choices=[
                    "1. Update Semua Bot & Tools",
                    "2. Deploy Proxy (via ProxySync)",
                    "3. Tampilkan Konfig Bot",
                    "0. [[Back]] Kembali ke Menu Utama",
                ],
                use_shortcuts=True,
            ).ask_async()

            if not choice or choice.startswith("0"):
                return

            selection = choice.split(".")[0]
            pause_needed = True

            if selection == "1":
                await updater.update_all_bots()
            elif selection == "2":
                await proxymanager.deploy_proxies()
            elif selection == "3":
                updater.show_config()
            else:
                pause_needed = False

            if pause_needed:
                await pause(cancel_event)

        except (asyncio.CancelledError, KeyboardInterrupt):
            main_cancel_event.set()
            return


async def show_hybrid_menu(cancel_event: asyncio.Event):
    global child_process_active # <-- TAMBAH INI
    while not cancel_event.is_set():
        console.clear()
        # --- GANTI FIGLET JADI PANEL ---
        console.print(Panel("[bold blue]Hybrid: Local Run & Remote Trigger[/]", expand=False, border_style="blue"), justify="center")
        # -------------------------------
        try:
            choice = await questionary.select(
                "[bold blue]HYBRID: LOCAL RUN & REMOTE TRIGGER[/]",
                choices=[
                    "1. Run Single Bot Locally -> Trigger Remote",
                    "2. Run SEMUA Bot Locally -> Trigger Remote",
                    "0. [[Back]] Kembali ke Menu Utama",
                ],
                use_shortcuts=True,
            ).ask_async()

            if not choice or choice.startswith("0"):
                return

            selection = choice.split(".")[0]
            pause_needed = True

            # Reset child cancel event sebelum menjalankan
            child_process_cancel_event.clear()
            child_process_active = True # <-- SET FLAG

            if selection == "1":
                await hybridrunner.run_single_interactive_bot(
                    cancel_event, child_process_cancel_event
                )
            elif selection == "2":
                await hybridrunner.run_all_interactive_bots(
                    cancel_event, child_process_cancel_event
                )
            else:
                pause_needed = False

            if pause_needed and not cancel_event.is_set():
                await pause(cancel_event)

        except (asyncio.CancelledError, KeyboardInterrupt):
            main_cancel_event.set()
            return
        finally:
            child_process_active = False # <-- PASTIKAN FLAG DI-UNSET


async def show_remote_menu(cancel_event: asyncio.Event):
    while not cancel_event.is_set():
        console.clear()
        # --- GANTI FIGLET JADI PANEL ---
        console.print(Panel("[bold red]GitHub Actions Control[/]", expand=False, border_style="red"), justify="center")
        # -------------------------------
        try:
            choice = await questionary.select(
                "[bold red]GITHUB ACTIONS CONTROL[/]",
                choices=[
                    "1. Trigger SEMUA Bot (Workflow)",
                    "2. Lihat Status Workflow",
                    "0. [[Back]] Kembali ke Menu Utama",
                ],
                use_shortcuts=True,
            ).ask_async()

            if not choice or choice.startswith("0"):
                return

            selection = choice.split(".")[0]
            pause_needed = True

            if selection == "1":
                await dispatcher.trigger_all_bots_workflow()
            elif selection == "2":
                await dispatcher.get_workflow_runs()
            else:
                pause_needed = False

            if pause_needed:
                await pause(cancel_event)

        except (asyncio.CancelledError, KeyboardInterrupt):
            main_cancel_event.set()
            return


async def show_debug_menu(cancel_event: asyncio.Event):
    global child_process_active # <-- TAMBAH INI
    while not cancel_event.is_set():
        console.clear()
        # --- GANTI FIGLET JADI PANEL ---
        console.print(Panel("[bold grey]Debug & Local Testing[/]", expand=False, border_style="grey"), justify="center")
        # -------------------------------
        try:
            choice = await questionary.select(
                "[bold grey]DEBUG & LOCAL TESTING[/]",
                choices=[
                    "1. Test Local Bot (PTY, No Remote)",
                    "0. [[Back]] Kembali ke Menu Utama",
                ],
                use_shortcuts=True,
            ).ask_async()

            if not choice or choice.startswith("0"):
                return

            selection = choice.split(".")[0]
            pause_needed = True

            # Reset child cancel event
            child_process_cancel_event.clear()
            child_process_active = True # <-- SET FLAG

            if selection == "1":
                await botrunner.test_local_bot(
                    cancel_event, child_process_cancel_event
                )
            else:
                pause_needed = False

            if pause_needed and not cancel_event.is_set():
                await pause(cancel_event)

        except (asyncio.CancelledError, KeyboardInterrupt):
            main_cancel_event.set()
            return
        finally:
            child_process_active = False # <-- PASTIKAN FLAG DI-UNSET


async def main_loop():
    # Setup Ctrl+C handler
    signal.signal(signal.SIGINT, handle_signal)

    # Inisialisasi
    try:
        TokenManager.initialize()
    except Exception as e:
        console.print(f"[red]Fatal error saat inisialisasi: {e}[/]")
        return

    while True:
        # Reset event
        main_cancel_event.clear()
        child_process_cancel_event.clear()
        # Pastikan flag utama mati
        global child_process_active
        child_process_active = False

        console.clear()
        # --- GANTI FIGLET JADI PANEL UTAMA ---
        console.print(Panel("[bold cyan]Automation Hub[/]", title="🚀", subtitle="v2.0 Python", expand=False, border_style="cyan"), justify="center")
        # ------------------------------------
        console.print(
            "[grey]Interactive Proxy Orchestrator - Local Control, Remote Execution[/]",
            justify="center",
        )

        try:
            choice = await questionary.select(
                "\n[bold cyan]MAIN MENU[/]",
                choices=[
                    "1. [[SETUP]] Konfigurasi & Manajemen Token",
                    "2. [[LOCAL]] Manajemen Bot & Proxy",
                    "3. [[HYBRID]] Local Run & Remote Trigger",
                    "4. [[REMOTE]] Kontrol GitHub Actions",
                    "5. [[DEBUG]] Local Bot Testing",
                    "0. Exit",
                ],
                use_shortcuts=True,
            ).ask_async()

            if not choice or choice.startswith("0"):
                console.print("[yellow]Exiting...[/]")
                return

            selection = choice.split(".")[0]

            if selection == "1":
                await show_setup_menu(main_cancel_event)
            elif selection == "2":
                await show_local_menu(main_cancel_event)
            elif selection == "3":
                await show_hybrid_menu(main_cancel_event)
            elif selection == "4":
                await show_remote_menu(main_cancel_event)
            elif selection == "5":
                await show_debug_menu(main_cancel_event)

        except (asyncio.CancelledError, KeyboardInterrupt):
            # Ini akan ditangkap jika Ctrl+C ditekan di menu utama
            if main_cancel_event.is_set():
                console.print("\n[yellow]Operasi dibatalkan. Kembali ke menu utama.[/]")
                await pause_without_cancel()
            else:
                # Jika user spam Ctrl+C, keluar
                console.print("[red]Exiting...[/]")
                return
        except Exception as ex:
            console.print(Panel(f"[red]Unexpected Error: {ex}[/]", title="Error", border_style="red"))
            console.print_exception(show_locals=True)
            await pause_without_cancel()


if __name__ == "__main__":
    try:
        # Menjalankan event loop
        asyncio.run(main_loop())
    except (KeyboardInterrupt):
        # Handle jika Ctrl+C ditekan saat asyncio.run() sedang setup
        print("\n[red]Exit paksa.[/]")
    finally:
        pass
