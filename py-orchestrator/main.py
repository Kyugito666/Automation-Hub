import asyncio
import sys
from rich.console import Console
from rich.panel import Panel
from rich.text import Text
import gitmanager

console = Console()

def show_menu():
    console.clear()
    title = Text("GitHub Repo Orchestrator", style="bold magenta")
    console.print(Panel(title, expand=False))
    console.print("[bold yellow]SETUP & KONFIGURASI[/]")
    console.print("1. Validasi Token & Ambil Username")
    console.print("2. Undang Kolaborator")
    console.print("3. Terima Undangan Kolaborasi")
    console.print("4. Keluar")

async def main():
    while True:
        show_menu()
        try:
            choice = input("Pilih menu (1-4): ").strip()
            if choice == "1":
                await gitmanager.validate_all_tokens()
            elif choice == "2":
                await gitmanager.invite_collaborators()
            elif choice == "3":
                await gitmanager.accept_invitations()
            elif choice == "4":
                console.print("[green]Keluar...[/]")
                sys.exit(0)
            else:
                console.print("[red]Pilihan tidak valid.[/]")
        except KeyboardInterrupt:
            console.print("\n[yellow]Dibatalkan oleh user.[/]")
            sys.exit(0)
        except Exception as e:
            console.print(f"[red]Error: {e}[/]")
        
        # Tunggu input untuk lanjut, tanpa prompt yang berantakan
        input("Tekan Enter untuk lanjut...")

if __name__ == "__main__":
    asyncio.run(main())
