import asyncio
from rich.console import Console
# --- PERUBAHAN IMPORT DI SINI ---
from rich import progress # Import modulnya aja
# --------------------------------
from tokenmanager import TokenManager

console = Console()

async def validate_all_tokens():
    console.print("[bold cyan]--- 1. Validasi Token & Ambil Usernames ---[/]")
    tokens = TokenManager.get_all_token_entries()
    owner, repo = TokenManager.get_repo_info()
    if not tokens:
        return

    cache = TokenManager.get_username_cache()
    new_users = 0

    # --- PERUBAHAN BLOK 'with' PERTAMA ---
    with progress.Progress( # Tambah progress.
        progress.TextColumn("[progress.description]{task.description}"), # Tambah progress.
        progress.BarColumn(), # Tambah progress.
        progress.PercentageColumn(), # Tambah progress.
        progress.SpinnerColumn(), # Tambah progress.
        console=console,
        transient=True
    ) as prog: # Ganti nama variabel jadi 'prog'
        task = prog.add_task("[green]Memvalidasi token...[/]", total=len(tokens)) # Ganti progress -> prog

        for entry in tokens:
            token_display = TokenManager.mask_token(entry.token)
            prog.update(task, description=f"[green]Memvalidasi:[/] {token_display}") # Ganti progress -> prog

            if entry.token in cache:
                entry.username = cache[entry.token]
                prog.advance(task) # Ganti progress -> prog
                continue

            try:
                async with TokenManager.create_http_client(entry) as client:
                    response = await client.get("https://api.github.com/user")

                    if response.status_code == 200:
                        user = response.json()
                        if user and user.get("login"):
                            username = user["login"]
                            console.print(f"[green]✓[/] Token {token_display} valid untuk [yellow]@{username}[/]")
                            entry.username = username
                            cache[entry.token] = username
                            new_users += 1
                        else:
                             console.print(f"[red]✗[/] Token {token_display} [red]INVALID (respon aneh)[/]")
                    else:
                        console.print(f"[red]✗[/] Token {token_display} [red]INVALID:[/] {response.status_code}")

            except Exception as ex:
                console.print(f"[red]✗[/] Token {token_display} [red]ERROR:[/] {ex}")

            prog.advance(task) # Ganti progress -> prog
            await asyncio.sleep(0.5) # Rate limit ringan
    # --- AKHIR PERUBAHAN BLOK 'with' PERTAMA ---

    TokenManager.save_token_cache(cache)
    console.print(f"[green]✓ Validasi selesai. {new_users} username baru ditambahkan ke cache.[/]")


async def invite_collaborators():
    console.print("[bold cyan]--- 2. Undang Kolaborator ---[/]")
    tokens = TokenManager.get_all_token_entries()
    owner, repo = TokenManager.get_repo_info()
    if not owner or not repo:
        console.print("[red]Owner/Repo utama tidak di-set di github_tokens.txt[/]")
        return

    main_token_entry = next((t for t in tokens if t.username and t.username.lower() == owner.lower()), None)
    if not main_token_entry:
        console.print(f"[red]Token untuk owner '{owner}' tidak ditemukan. Validasi dulu (Menu 1).[/]")
        return

    console.print(f"[dim]Menggunakan token [yellow]@{owner}[/] untuk mengundang...[/]")

    users_to_invite = [
        t for t in tokens if t.username and t.username.lower() != owner.lower()
    ]

    if not users_to_invite:
        console.print("[yellow]Tidak ada user (selain owner) untuk diundang.[/]")
        return

    success = 0
    # --- PERUBAHAN BLOK 'with' KEDUA ---
    with progress.Progress( # Tambah progress.
        progress.TextColumn("[progress.description]{task.description}"), # Tambah progress.
        progress.BarColumn(), # Tambah progress.
        progress.PercentageColumn(), # Tambah progress.
        progress.SpinnerColumn(), # Tambah progress.
        console=console,
        transient=True
    ) as prog: # Ganti nama variabel jadi 'prog'
        task = prog.add_task("[green]Mengirim undangan...[/]", total=len(users_to_invite)) # Ganti progress -> prog

        async with TokenManager.create_http_client(main_token_entry) as client:
            for user in users_to_invite:
                prog.update(task, description=f"[green]Mengundang:[/] [yellow]@{user.username}[/]") # Ganti progress -> prog
                url = f"https://api.github.com/repos/{owner}/{repo}/collaborators/{user.username}"
                payload = {"permission": "push"}

                try:
                    response = await client.put(url, json=payload)

                    if 200 <= response.status_code < 300:
                        console.print(f"[green]✓[/] Undangan terkirim ke [yellow]@{user.username}[/]")
                        success += 1
                    elif response.status_code == 422: # Already collaborator
                        console.print(f"[grey]✓[/] [yellow]@{user.username}[/] sudah menjadi kolaborator.")
                        success += 1
                    else:
                        error = response.json()
                        console.print(f"[red]✗[/] Gagal mengundang [yellow]@{user.username}[/]: {error.get('message')}")

                except Exception as ex:
                    console.print(f"[red]✗[/] Gagal mengundang [yellow]@{user.username}[/]: {ex}")

                prog.advance(task) # Ganti progress -> prog
                await asyncio.sleep(1) # Rate limit API invite
    # --- AKHIR PERUBAHAN BLOK 'with' KEDUA ---

    console.print(f"[green]✓ Proses undangan selesai. {success}/{len(users_to_invite)} berhasil.[/]")


async def accept_invitations():
    console.print("[bold cyan]--- 3. Terima Undangan Kolaborasi ---[/]")
    tokens = TokenManager.get_all_token_entries()
    owner, repo = TokenManager.get_repo_info()
    target_repo = f"{owner}/{repo}".lower()
    console.print(f"[dim]Target repo:[/] {target_repo}")

    accepted = 0
    not_found = 0

    # --- PERUBAHAN BLOK 'with' KETIGA ---
    with progress.Progress( # Tambah progress.
        progress.TextColumn("[progress.description]{task.description}"), # Tambah progress.
        progress.BarColumn(), # Tambah progress.
        progress.PercentageColumn(), # Tambah progress.
        progress.SpinnerColumn(), # Tambah progress.
        console=console,
        transient=True
    ) as prog: # Ganti nama variabel jadi 'prog'
        task = prog.add_task("[green]Menerima undangan...[/]", total=len(tokens)) # Ganti progress -> prog

        for entry in tokens:
            token_display = TokenManager.mask_token(entry.token)
            prog.update(task, description=f"[green]Mengecek:[/] {entry.username or token_display}") # Ganti progress -> prog

            try:
                async with TokenManager.create_http_client(entry) as client:
                    response = await client.get("https://api.github.com/user/repository_invitations")

                    if response.status_code != 200:
                        console.print(f"[red]✗[/] Gagal cek undangan untuk {token_display}")
                        continue

                    invitations = response.json()
                    target_invite = next(
                        (inv for inv in invitations if inv.get("repository", {}).get("full_name", "").lower() == target_repo),
                        None
                    )

                    if target_invite:
                        console.print(f"[yellow]![/] Menemukan undangan {target_repo} untuk {entry.username or 'user'}. Menerima...")
                        accept_url = f"https://api.github.com/user/repository_invitations/{target_invite['id']}"
                        patch_response = await client.patch(accept_url)

                        if 200 <= patch_response.status_code < 300:
                            console.print(f"[green]✓[/] [yellow]@{entry.username}[/] berhasil menerima undangan.")
                            accepted += 1
                        else:
                            console.print(f"[red]✗[/] [yellow]@{entry.username}[/] gagal menerima: {patch_response.status_code}")
                    else:
                        not_found += 1

            except Exception as ex:
                console.print(f"[red]✗[/] Error pada {token_display}: {ex}")

            prog.advance(task) # Ganti progress -> prog
            await asyncio.sleep(0.5)
    # --- AKHIR PERUBAHAN BLOK 'with' KETIGA ---

    console.print(f"[green]✓ Proses selesai. Undangan diterima: {accepted}. Tidak ditemukan: {not_found}.[/]")
