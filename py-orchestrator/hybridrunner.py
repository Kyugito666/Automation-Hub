import asyncio
import json
from pathlib import Path
from rich.console import Console
from rich.rule import Rule
import questionary
import re

from botconfig import BotConfig, BotEntry, BACK_OPTION
from botrunner import install_dependencies, get_run_command
import dispatcher
import shell

console = Console()
BOT_ANSWERS_DIR = Path("../.bot-inputs") # Sesuai .gitignore

def sanitize_bot_name(name: str) -> str:
    """Membersihkan nama bot untuk nama file."""
    name = re.sub(r'[^\w\s-]', '_', name)
    name = re.sub(r'[-\s]+', '_', name)
    return name

def create_default_answer_file(bot: BotEntry, answer_file: Path):
    """Membuat file jawaban default."""
    answers = {}
    if "aster" in bot.name.lower():
        answers["proxy_question"] = "y"
    else:
        answers["proxy_question"] = "y"
        answers["continue_question"] = "y"
        
    try:
        BOT_ANSWERS_DIR.mkdir(exist_ok=True, parents=True)
        with open(answer_file, "w") as f:
            json.dump(answers, f, indent=2)
        console.print(f"[green]✓ Membuat file jawaban default: {answer_file.name}[/]")
    except Exception as e:
        console.print(f"[red]Gagal membuat file jawaban: {e}[/]")


async def run_with_auto_answers(
    bot: BotEntry, 
    answer_file: Path, 
    bot_path: Path, 
    cancel_event: asyncio.Event
):
    """Menjalankan bot menggunakan PTY mode scripted."""
    try:
        with open(answer_file, "r") as f:
            answers = json.load(f)
        console.print("[green]Jawaban dimuat:[/]")
        for key, value in answers.items():
            console.print(f"  [dim]{key}:[/] [yellow]{value}[/]")
    except Exception as e:
        console.print(f"[red]Error memuat jawaban: {e}[/]")
        return

    await install_dependencies(bot_path, bot.type)
    
    if cancel_event.is_set():
        raise asyncio.CancelledError()
        
    executor, args = get_run_command(bot_path, bot.type)
    if not executor:
        console.print(f"[red]Gagal menemukan run command untuk {bot.name}[/]")
        return
        
    console.print("[dim]Menjalankan bot dengan auto-answers via PTY...[/]")
    console.print(Rule(style="grey"))
    
    await shell.run_pty_with_script(
        answer_file, executor, args, bot_path, cancel_event
    )
    
    console.print(Rule(style="grey"))
    console.print("[green]Auto-answer run selesai.[/]")


async def run_manual_capture(
    bot: BotEntry, 
    bot_path: Path, 
    cancel_event: asyncio.Event
):
    """Menjalankan bot menggunakan PTY mode interaktif."""
    console.print("[yellow]Menjalankan dalam mode interaktif manual via PTY...[/]")
    
    await install_dependencies(bot_path, bot.type)
    
    if cancel_event.is_set():
        raise asyncio.CancelledError()
        
    executor, args = get_run_command(bot_path, bot.type)
    if not executor:
        console.print(f"[red]Gagal menemukan run command untuk {bot.name}[/]")
        return
        
    console.print(Rule(style="grey"))
    
    await shell.run_interactive_pty(
        executor, args, bot_path, cancel_event
    )
    
    console.print(Rule(style="grey"))
    console.print("[green]Manual run selesai.[/]")


async def prompt_and_trigger_remote(
    bot: BotEntry, 
    answer_file: Path,
    cancel_event: asyncio.Event
):
    """Tanya user dan trigger GitHub Actions."""
    console.print("\n[yellow]Step 2: Trigger remote execution di GitHub Actions?[/]")
    
    try:
        proceed = await questionary.confirm("Lanjutkan trigger remote?", default=True).ask_async()
    except asyncio.CancelledError:
        console.print("[yellow]Input dibatalkan.[/]")
        return
        
    if cancel_event.is_set() or not proceed:
        console.print("[yellow]Remote trigger dilewati.[/]")
        return
        
    captured_inputs = {}
    try:
        if answer_file.exists():
            with open(answer_file, "r") as f:
                captured_inputs = json.load(f)
            console.print(f"[dim]Menggunakan {len(captured_inputs)} input dari {answer_file.name} untuk remote trigger...[/]")
    except Exception as e:
        console.print(f"[red]Gagal baca answer file, kirim input kosong: {e}[/]")

    await dispatcher.trigger_bot_with_inputs(bot, captured_inputs)


async def capture_and_trigger_bot(
    bot: BotEntry,
    main_cancel_event: asyncio.Event,
    child_cancel_event: asyncio.Event
):
    """Fungsi utama untuk satu bot (auto atau manual)."""
    console.print(f"[bold cyan]=== Proxy Mode: {bot.name} ===[/]")
    
    answer_file = BOT_ANSWERS_DIR / f"{sanitize_bot_name(bot.name)}.json"
    
    if not answer_file.exists():
        create_default_answer_file(bot, answer_file)
        
    bot_path = Path("..", bot.path).resolve()
    if not bot_path.is_dir():
        console.print(f"[red]Path bot tidak ditemukan: {bot_path}[/]")
        return

    try:
        if answer_file.exists():
            console.print("[yellow]Mode: AUTO-ANSWER (Menggunakan jawaban tersimpan)[/]")
            await run_with_auto_answers(bot, answer_file, bot_path, child_cancel_event)
        else:
            console.print("[yellow]Mode: MANUAL (Tidak ada file jawaban)[/]")
            await run_manual_capture(bot, bot_path, child_cancel_event)
        
        # Jika run lokal selesai tanpa cancel, trigger remote
        await prompt_and_trigger_remote(bot, answer_file, child_cancel_event)
        
    except asyncio.CancelledError:
        if main_cancel_event.is_set():
            raise # Propagate ke main loop
        console.print("[yellow]Local run dibatalkan, skipping remote trigger.[/]")
        

async def run_single_interactive_bot(
    main_cancel_event: asyncio.Event,
    child_cancel_event: asyncio.Event
):
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
        "Pilih bot untuk local run & remote trigger:",
        choices=choices,
        use_shortcuts=True
    ).ask_async()
    
    if not choice_str or choice_str == BACK_OPTION.name:
        return
        
    selected_bot = next(b for b in bots if f"{b.name} ({b.type})" == choice_str)
    
    await capture_and_trigger_bot(selected_bot, main_cancel_event, child_cancel_event)


async def run_all_interactive_bots(
    main_cancel_event: asyncio.Event,
    child_cancel_event: asyncio.Event
):
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
        
    console.print(f"[cyan]Ditemukan {len(bots)} bot aktif (Diurutkan)[/]")
    console.print("[yellow]WARNING: Ini akan menjalankan SEMUA bot lokal (capture mode), lalu trigger remote.[/]")
    console.print("[grey]Tekan Ctrl+C saat bot jalan untuk skip bot itu.[/]")
    
    try:
        confirm = await questionary.confirm("Lanjutkan?", default=True).ask_async()
        if not confirm:
            return
    except asyncio.CancelledError:
        return

    success_count = 0
    fail_count = 0
    
    for i, bot in enumerate(bots):
        if main_cancel_event.is_set():
            console.print("\n[yellow]Loop dibatalkan oleh token utama.[/]")
            fail_count += (len(bots) - i)
            break
            
        console.print(Rule(f"[cyan]{bot.name}[/]", align="center"))
        
        # Reset child event untuk bot ini
        child_cancel_event.clear()
        
        try:
            await capture_and_trigger_bot(bot, main_cancel_event, child_cancel_event)
            success_count += 1
        except asyncio.CancelledError:
            fail_count += 1
            if main_cancel_event.is_set():
                console.print("[yellow]Operasi utama dibatalkan. Menghentikan loop.[/]")
                break
            elif child_cancel_event.is_set():
                console.print("[yellow]Bot run di-skip oleh user (Ctrl+C).[/]")
                try:
                    confirm_continue = await questionary.confirm("Lanjut ke bot berikutnya?", default=True).ask_async()
                    if not confirm_continue:
                        console.print("[yellow]Loop dibatalkan oleh user.[/]")
                        break
                except asyncio.CancelledError:
                    console.print("[yellow]Loop dibatalkan.[/]")
                    break
        except Exception as ex:
            console.print(f"[red]Error memproses {bot.name}: {ex}[/]")
            fail_count += 1
            
        if main_cancel_event.is_set():
            break
            
        if i < len(bots) - 1:
            try:
                await asyncio.sleep(2) # Jeda singkat
            except asyncio.CancelledError:
                break

    console.print(Rule("Summary"))
    console.print(f"[green]✓ Bot diproses: {success_count}[/]")
    console.print(f"[yellow]✗ Bot di-skip/gagal: {fail_count}[/]")
