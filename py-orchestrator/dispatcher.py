from rich.console import Console
from rich.table import Table
from datetime import datetime
import json
from tokenmanager import TokenManager
from botconfig import BotEntry

console = Console()

async def trigger_all_bots_workflow():
    console.print("[cyan]Triggering workflow 'run-all-bots.yml' di GitHub Actions...[/]")
    owner, repo = TokenManager.get_repo_info()
    if not owner or not repo:
        console.print("[red]Owner/Repo GitHub tidak dikonfigurasi.[/]")
        return
        
    url = f"https://api.github.com/repos/{owner}/{repo}/actions/workflows/run-all-bots.yml/dispatches"
    payload = {"ref": "main"}
    
    try:
        async with TokenManager.create_http_client() as client:
            response = await client.post(url, json=payload)
            
            if response.status_code == 204:
                console.print("[green]✓ Workflow triggered successfully![/]")
                console.print(f"[dim]Cek status: https://github.com/{owner}/{repo}/actions[/]")
            else:
                console.print(f"[red]✗ Gagal: {response.status_code}[/]")
                try:
                    console.print(f"[dim]{response.json()}[/dim]")
                except:
                    console.print(f"[dim]{response.text}[/dim]")
                    
    except Exception as ex:
        console.print(f"[red]✗ Exception: {ex}[/]")


async def get_workflow_runs():
    console.print("[cyan]Mengambil 10 workflow runs terakhir...[/]")
    owner, repo = TokenManager.get_repo_info()
    if not owner or not repo:
        console.print("[red]Owner/Repo GitHub tidak dikonfigurasi.[/]")
        return
        
    url = f"https://api.github.com/repos/{owner}/{repo}/actions/runs?per_page=10"
    
    try:
        async with TokenManager.create_http_client() as client:
            response = await client.get(url)
            response.raise_for_status() # Error jika non-200
            
            data = response.json()
            runs = data.get("workflow_runs")
            
            if not runs:
                console.print("[yellow]Tidak ada workflow runs.[/]")
                return

            table = Table(title="Recent Workflow Runs", border_style="rounded", expand=True)
            table.add_column("Status")
            table.add_column("Workflow")
            table.add_column("Dimulai")
            table.add_column("Durasi")

            for run in runs:
                status = run.get('status')
                conclusion = run.get('conclusion')
                
                if status == "completed":
                    status_icon = "[green]✓[/]" if conclusion == "success" else "[red]✗[/]"
                else:
                    status_icon = "[yellow]...[/]"
                    
                try:
                    created_at_str = run.get('created_at', '').replace("Z", "+00:00")
                    updated_at_str = run.get('updated_at', '').replace("Z", "+00:00")
                    
                    created = datetime.fromisoformat(created_at_str)
                    updated = datetime.fromisoformat(updated_at_str)
                    duration = updated - created
                    duration_str = str(duration).split('.')[0]
                    created_str = created.strftime("%Y-%m-%d %H:%M")
                except:
                    duration_str = "-"
                    created_str = "-"


                table.add_row(
                    status_icon,
                    run.get('name', 'Unknown'),
                    created_str,
                    duration_str
                )
            
            console.print(table)
            
    except Exception as ex:
        console.print(f"[red]✗ Exception: {ex}[/]")


async def trigger_bot_with_inputs(bot: BotEntry, captured_inputs: dict, duration_minutes: int = 340):
    console.print(f"[cyan]Triggering single bot: {bot.name}...[/]")
    
    owner, repo = TokenManager.get_repo_info()
    if not owner or not repo:
        console.print("[red]Owner/Repo GitHub tidak dikonfigurasi.[/]")
        return
        
    url = f"https://api.github.com/repos/{owner}/{repo}/actions/workflows/run-single-bots.yml/dispatches"
    
    inputs_json = json.dumps(captured_inputs or {})
    
    payload = {
        "ref": "main",
        "inputs": {
            "bot_name": bot.name,
            "bot_path": bot.path,
            "bot_repo": bot.repo_url,
            "bot_type": bot.type,
            "duration_minutes": str(duration_minutes),
            "bot_inputs": inputs_json # Input baru
        }
    }
    
    try:
        async with TokenManager.create_http_client() as client:
            response = await client.post(url, json=payload)
            
            if response.status_code == 204:
                console.print(f"[green]✓ {bot.name} triggered![/]")
            else:
                console.print(f"[red]✗ Gagal: {response.status_code}[/]")
                try:
                    console.print(f"[dim]{response.json()}[/dim]")
                except:
                    console.print(f"[dim]{response.text}[/dim]")

    except Exception as ex:
        console.print(f"[red]✗ Exception: {ex}[/]")
