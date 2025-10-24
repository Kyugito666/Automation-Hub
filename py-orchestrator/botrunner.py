import asyncio
from pathlib import Path
import json
from typing import List, Tuple, Optional
from rich.console import Console
import questionary
import platform

from botconfig import BotConfig, BotEntry, BACK_OPTION
import shell

console = Console()

CONFIG_FILE = Path("../config/bots_config.json")
POSSIBLE_VENV_NAMES = [".venv", "venv", "myenv"]
DEFAULT_VENV_NAME = ".venv"

def get_active_venv_path(bot_path: Path) -> Optional[Path]:
    """Mendeteksi folder venv yang ada."""
    for name in POSSIBLE_VENV_NAMES:
        venv_path = bot_path / name
        if venv_path.is_dir():
            return venv_path
    return None

def get_python_exe_in_venv(venv_path: Path) -> Optional[str]:
    """Mendapatkan path python executable di dalam venv."""
    if platform.system() == "Windows":
        exe_path = venv_path / "Scripts" / "python.exe"
    else:
        exe_path = venv_path / "bin" / "python"
        if not exe_path.exists():
            exe_path = venv_path / "bin" / "python3"
    
    return str(exe_path.resolve()) if exe_path.exists() else None

def get_pip_exe_in_venv(venv_path: Path) -> Optional[str]:
    """Mendapatkan path pip executable di dalam venv."""
    if platform.system() == "Windows":
        exe_path = venv_path / "Scripts" / "pip.exe"
    else:
        exe_path = venv_path / "bin" / "pip"
        if not exe_path.exists():
            exe_path = venv_path / "bin" / "pip3"
            
    return str(exe_path.resolve()) if exe_path.exists() else None

async def install_dependencies(bot_path: Path, bot_type: str):
    """Install dependencies (venv python atau npm)."""
    bot_name = bot_path.name
    
    if bot_type == "python" and (bot_path / "requirements.txt").exists():
        venv_path = get_active_venv_path(bot_path)
        pip_exe = None
        
        if not venv_path:
            console.print(f"[dim]  Venv ({', '.join(POSSIBLE_VENV_NAMES)}) tidak ditemukan. Membuat '{DEFAULT_VENV_NAME}'...[/]")
            venv_path = bot_path / DEFAULT_VENV_NAME
            # Menjalankan 'python -m venv'
            await shell.run_stream_async(f"{sys.executable}", f"-m venv \"{DEFAULT_VENV_NAME}\"", bot_path)
            pip_exe = get_pip_exe_in_venv(venv_path)
        else:
            console.print(f"[dim]  Menggunakan venv yang ada: '[yellow]{venv_path.name}[/]'[/]")
            pip_exe = get_pip_exe_in_venv(venv_path)
            
        if pip_exe:
            console.print(f"[dim]  Installing Python deps for '{bot_name}' using venv...[/]")
            await shell.run_stream_async(f'"{pip_exe}"', "install --no-cache-dir -q -r requirements.txt", bot_path)
        else:
            console.print(f"[red]  ✗ Tidak bisa menemukan pip di venv '{venv_path.name}' untuk '{bot_name}'.[/]")

    elif bot_type == "javascript" and (bot_path / "package.json").exists():
        console.print(f"[dim]  Installing Node deps for '{bot_name}'...[/]")
        if (bot_path / "node_modules").is_dir() and (bot_path / "package-lock.json").exists():
            await shell.run_stream_async("npm", "ci --silent --no-progress", bot_path)
        else:
            await shell.run_stream_async("npm", "install --silent --no-progress", bot_path)

def get_run_command(bot_path: Path, bot_type: str) -> Tuple[str, str]:
    """Mendapatkan perintah (executor, args) untuk menjalankan bot."""
    if bot_type == "python":
        venv_path = get_active_venv_path(bot_path)
        python_in_venv = get_python_exe_in_venv(venv_path) if venv_path else None
        
        exe = python_in_venv or sys.executable # Fallback ke python yang sedang jalan
        if not python_in_venv:
             console.print(f"[yellow]Warning: Venv tidak ditemukan untuk '{bot_path.name}'. Mencoba python global.[/]")

        if (bot_path / "run.py").exists():
            return exe, "run.py"
        if (bot_path / "main.py").exists():
            return exe, "main.py"
        if (bot_path / "bot.py").exists():
            return exe, "bot.py"
        
        if python_in_venv:
             console.print(f"[yellow]Warning: Venv ditemukan di '{venv_path.name}' tapi tidak ada run.py/main.py/bot.py.[/]")

    elif bot_type == "javascript":
        # Cek package.json untuk "start" script
        pkg_json_path = bot_path / "package.json"
        if pkg_json_path.exists():
            try:
                with open(pkg_json_path, "r") as f:
                    pkg_data = json.load(f)
                    if pkg_data.get("scripts", {}).get("start"):
                        return "npm", "start"
            except Exception as e:
                console.print(f"[yellow]Warning: Gagal parse package.json di {bot_path}: {e}[/]")
        
        # Fallback ke file JS umum
        if (bot_path / "index.js").exists():
            return "node", "index.js"
        if (bot_path / "main.js").exists():
            return "node", "main.js"
        if (bot_path / "bot.js").exists():
            return "node", "bot.js"

    return "", ""


async def test_local_bot(
    main_cancel_event: asyncio.Event,
    child_cancel_event: asyncio.Event
):
    """Menu untuk 'Test Local Bot' (Debug Menu)."""
    config = BotConfig.load()
    if not config:
        return
        
    bots = sorted(
        [b for b in config.bots_and_tools if b.enabled and b.is_bot],
        key=lambda b: b.name
    )
    if not bots:
        console.print("[yellow]Tidak ada bot aktif.[/]")
        return
        
    choices = [f"{b.name} ({b.type})" for b in bots] + [BACK_OPTION.name]
    choice_str = await questionary.select(
        "Pilih bot untuk test lokal (PTY):",
        choices=choices,
        use_shortcuts=True
    ).ask_async()
    
    if not choice_str or choice_str == BACK_OPTION.name:
        return
        
    selected_bot = next(b for b in bots if f"{b.name} ({b.type})" == choice_str)
    
    bot_path = Path("..", selected_bot.path).resolve()
    if not bot_path.is_dir():
        console.print(f"[red]Path bot tidak ditemukan: {bot_path}[/]")
        return
        
    await install_dependencies(bot_path, selected_bot.type)
    executor, args = get_run_command(bot_path, selected_bot.type)
    
    if not executor:
        console.print("[red]Gagal menemukan perintah eksekusi bot (run.py/main.py/index.js/etc.)[/]")
        return

    console.print(f"[green]Running {selected_bot.name} locally... (Press Ctrl+C to stop)[/]")
    console.print("[dim]Ini adalah test run (via PTY). Tidak ada remote execution.[/]\n")

    try:
        await shell.run_interactive_pty(
            executor, args, bot_path, child_cancel_event
        )
    except asyncio.CancelledError:
        if main_cancel_event.is_set():
            raise # Propagate ke main loop
        console.print("[yellow]Test run dibatalkan oleh user.[/]")
