using Spectre.Console;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator;

internal static class Program
{
    private static CancellationTokenSource _mainCts = new CancellationTokenSource();
    
    // Interval keep-alive (cek SSH) saat Codespace berjalan
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromHours(3);
    // Delay antar iterasi loop utama jika terjadi error
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(5); 

    public static async Task Main(string[] args)
    {
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true; // Mencegah app langsung terminate
            if (!_mainCts.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("\n[bold yellow]Ctrl+C detected. Requesting shutdown...[/]");
                _mainCts.Cancel();
            } else {
                 AnsiConsole.MarkupLine("[grey](Shutdown already requested...)[/]");
            }
        };

        try
        {
            TokenManager.Initialize(); // Load token & proxy dulu

            if (args.Length > 0 && args[0].ToLower() == "--run")
            {
                // Mode non-interaktif, langsung jalankan loop orchestrator
                AnsiConsole.MarkupLine("[bold cyan]Starting Orchestrator in non-interactive mode...[/]");
                await RunOrchestratorLoopAsync(_mainCts.Token);
            }
            else
            {
                // Mode interaktif (menu)
                await RunInteractiveMenuAsync(_mainCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("\n[yellow]Operation cancelled by user.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[bold red]FATAL ERROR in Main:[/]");
            AnsiConsole.WriteException(ex);
        }
        finally
        {
             AnsiConsole.MarkupLine("\n[cyan]Orchestrator shutting down.[/]");
        }
    }

    // ==========================================================
    // MENU INTERAKTIF BARU
    // ==========================================================

    private static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[grey]Codespace Orchestrator - Local Control, Remote Execution[/]");
             
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold cyan]MAIN MENU[/]")
                    .PageSize(10)
                    .WrapAround()
                    .AddChoices(new[]
                    {
                        "1. [[REMOTE]] Start/Manage Codespace Runner (Continuous Loop)",
                        "2. [[SETUP]] Token & Collaborator Management",
                        "3. [[LOCAL]] Proxy Management (Run ProxySync)",
                        "4. [[DEBUG]] Test Local Bot (PTY)", // Membutuhkan PTY helper
                        "5. [[SYSTEM]] Refresh All Configs",
                        "0. Exit"
                    }));

            var selection = choice.Split('.')[0];
            
            try
            {
                switch (selection)
                {
                    case "1": 
                        await RunOrchestratorLoopAsync(cancellationToken); 
                        break; // Loop ini akan berjalan sampai di-cancel
                    case "2": 
                        await ShowSetupMenuAsync(cancellationToken); 
                        break;
                    case "3": 
                        await ShowLocalMenuAsync(cancellationToken); 
                        break;
                    case "4": 
                        await ShowDebugMenuAsync(cancellationToken); 
                        break;
                     case "5":
                        TokenManager.ReloadAllConfigs();
                        Pause("Konfigurasi di-refresh. Tekan Enter...", cancellationToken);
                        break;
                    case "0": 
                        return; // Keluar dari menu
                }
            }
            catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine("\n[yellow]Operation cancelled. Returning to main menu.[/]");
                 Pause("Tekan Enter...", CancellationToken.None); // Pause tanpa cancel
            }
            catch (Exception ex) {
                 AnsiConsole.MarkupLine($"[red]Unexpected Error in menu handler: {ex.Message}[/]");
                 AnsiConsole.WriteException(ex);
                 Pause("Error terjadi. Tekan Enter...", CancellationToken.None); // Pause tanpa cancel
            }
        }
    }
    
    // Sub-menu (mirip struktur lama tapi isinya beda)
    private static async Task ShowSetupMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested)
        {
             AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Setup").Centered().Color(Color.Yellow));
             var choice = AnsiConsole.Prompt(
                  new SelectionPrompt<string>()
                    .Title("\n[bold yellow]TOKEN & COLLABORATOR SETUP[/]")
                    .PageSize(10).WrapAround()
                    .AddChoices(new[] {
                        "1. Validate Tokens & Get Usernames",
                        "2. Invite Collaborators",
                        "3. Accept Invitations",
                        "4. Show Token/Proxy Status",
                        "0. [[Back]]" }));
            var sel = choice.Split('.')[0]; if (sel == "0") return;

            switch (sel) {
                case "1": await CollaboratorManager.ValidateAllTokens(); break;
                case "2": await CollaboratorManager.InviteCollaborators(); break;
                case "3": await CollaboratorManager.AcceptInvitations(); break;
                case "4": TokenManager.ShowStatus(); break;
            }
            Pause("Tekan Enter...", cancellationToken);
        }
     }
    
    private static async Task ShowLocalMenuAsync(CancellationToken cancellationToken) { 
        while (!cancellationToken.IsCancellationRequested)
        {
             AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Local").Centered().Color(Color.Green));
             var choice = AnsiConsole.Prompt(
                  new SelectionPrompt<string>()
                    .Title("\n[bold green]LOCAL PROXY MANAGEMENT[/]")
                    .PageSize(10).WrapAround()
                    .AddChoices(new[] {
                        "1. Run ProxySync (Download, Test, Generate proxy.txt)",
                        "0. [[Back]]" }));
            var sel = choice.Split('.')[0]; if (sel == "0") return;
            
            if (sel == "1") await ProxyManager.DeployProxies();
            Pause("Tekan Enter...", cancellationToken);
        }
    }

// ... Bagian using dan class Program ...

    private static async Task ShowDebugMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Debug").Centered().Color(Color.Grey));
            var choice = AnsiConsole.Prompt(
                 new SelectionPrompt<string>()
                   .Title("\n[bold grey]DEBUG & LOCAL TESTING[/]")
                   .PageSize(10).WrapAround()
                   .AddChoices(new[] {
                        "1. Test Local Bot (Run Interactively)",
                        "2. Update All Bots Locally", // Tambahkan opsi ini
                        "0. [[Back]]" }));
            var sel = choice.Split('.')[0]; if (sel == "0") return;

            switch (sel)
            {
                case "1":
                    await TestLocalBotAsync(cancellationToken);
                    break;
                case "2":
                    await BotUpdater.UpdateAllBotsLocally(); // Panggil fungsi update lokal
                    Pause("Tekan Enter...", cancellationToken);
                    break;
            }
            // Tidak perlu pause di sini jika case 1, karena TestLocalBotAsync sudah pause
        }
    }

    private static async Task TestLocalBotAsync(CancellationToken cancellationToken)
    {
        var config = BotConfig.Load();
        if (config == null || !config.BotsAndTools.Any())
        {
            AnsiConsole.MarkupLine("[yellow]Tidak ada bot di konfigurasi.[/]");
            Pause("Tekan Enter...", cancellationToken);
            return;
        }

        var enabledBots = config.BotsAndTools.Where(b => b.Enabled).ToList();
        if (!enabledBots.Any())
        {
             AnsiConsole.MarkupLine("[yellow]Tidak ada bot yang aktif (enabled) di konfigurasi.[/]");
             Pause("Tekan Enter...", cancellationToken);
             return;
        }

        // Tambahkan opsi Back
        var backOption = new BotEntry { Name = "[[Back]]", Path = "BACK" };
        var choices = enabledBots.OrderBy(b => b.Name).ToList();
        choices.Add(backOption);

        var selectedBot = AnsiConsole.Prompt(
            new SelectionPrompt<BotEntry>()
                .Title("[cyan]Pilih bot untuk dijalankan secara lokal:[/]")
                .PageSize(15).WrapAround()
                .UseConverter(b => b.Name)
                .AddChoices(choices));

        if (selectedBot == backOption) return;

        AnsiConsole.MarkupLine($"\n[cyan]Mempersiapkan {selectedBot.Name} untuk run lokal...[/]");

        string projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string botPath = Path.Combine(projectRoot, selectedBot.Path);

        if (!Directory.Exists(botPath))
        {
             AnsiConsole.MarkupLine($"[red]Error: Folder bot tidak ditemukan di: {botPath}[/]");
             Pause("Tekan Enter...", cancellationToken);
             return;
        }

        // 1. Instal dependensi lokal (adaptasi dari deploy_bots.py)
        try
        {
            AnsiConsole.MarkupLine("[dim]   Menginstal dependensi lokal...[/]");
            if (selectedBot.Type == "python")
            {
                 // Logika venv dan pip install (disederhanakan, tanpa deteksi venv kompleks)
                 var reqFile = Path.Combine(botPath, "requirements.txt");
                 if (File.Exists(reqFile))
                 {
                      // Coba pakai python -m venv jika ada
                      var venvDir = Path.Combine(botPath, ".venv");
                      string pipCmd = "pip";
                      if (!Directory.Exists(venvDir)) {
                           await ShellHelper.RunCommandAsync("python", $"-m venv .venv", botPath);
                      }
                      pipCmd = Path.Combine(venvDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Scripts" : "bin", "pip");
                      await ShellHelper.RunCommandAsync(pipCmd, $"install --no-cache-dir -r requirements.txt", botPath);
                 }
            }
            else if (selectedBot.Type == "javascript")
            {
                 var pkgFile = Path.Combine(botPath, "package.json");
                 if (File.Exists(pkgFile))
                 {
                      await ShellHelper.RunCommandAsync("npm", "install --silent --no-progress", botPath);
                 }
            }
            AnsiConsole.MarkupLine("[green]   ✓ Dependensi lokal OK.[/]");
        }
        catch (Exception ex)
        {
             AnsiConsole.MarkupLine($"[red]   Gagal instal dependensi lokal: {ex.Message}[/]");
             Pause("Tekan Enter...", cancellationToken);
             return;
        }

        // 2. Dapatkan perintah run (adaptasi dari deploy_bots.py)
         var (executor, args) = GetRunCommandLocal(botPath, selectedBot.Type); // Fungsi helper baru

        if (string.IsNullOrEmpty(executor))
        {
            AnsiConsole.MarkupLine("[red]Error: Tidak dapat menemukan perintah eksekusi (run.py/main.py/index.js/npm start).[/]");
            Pause("Tekan Enter...", cancellationToken);
            return;
        }

        // 3. Jalankan secara interaktif
        AnsiConsole.MarkupLine($"\n[green]Menjalankan {selectedBot.Name} secara interaktif...[/]");
        AnsiConsole.MarkupLine($"[dim]   Command: {executor} {args}[/]");
        AnsiConsole.MarkupLine($"[dim]   Di folder: {botPath}[/]");
        AnsiConsole.MarkupLine("[yellow]   Tekan Ctrl+C di sini untuk menghentikan bot dan kembali ke menu.[/]");

        try
        {
            await ShellHelper.RunInteractive(executor, args, botPath, null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Sudah ditangani di RunInteractive dan loop menu utama
        }
        catch (Exception ex)
        {
             AnsiConsole.MarkupLine($"[red]Error saat menjalankan bot lokal: {ex.Message}[/]");
             Pause("Tekan Enter...", CancellationToken.None); // Jangan cancel jika hanya error run
        }
        // Jangan pause di sini, biarkan kembali ke loop menu debug
    }

     // Helper untuk Get Run Command Lokal (mirip deploy_bots.py tapi path beda)
     private static (string executor, string args) GetRunCommandLocal(string botPath, string type)
     {
         if (type == "python")
         {
             string pythonExe = "python"; // Default ke global
             var venvDir = Path.Combine(botPath, ".venv"); // Asumsi nama venv .venv
             if (Directory.Exists(venvDir)) {
                 pythonExe = Path.Combine(venvDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Scripts" : "bin", "python");
             }

             foreach (var entry in new[] { "run.py", "main.py", "bot.py" }) {
                 if (File.Exists(Path.Combine(botPath, entry))) return (pythonExe, $"\"{entry}\"");
             }
         }
         else if (type == "javascript")
         {
             var pkgFile = Path.Combine(botPath, "package.json");
             if (File.Exists(pkgFile)) {
                 // Cek 'start' script (basic check)
                 var content = File.ReadAllText(pkgFile);
                 if (content.Contains("\"start\":")) return ("npm", "start");
             }
             foreach (var entry in new[] { "index.js", "main.js", "bot.js" }) {
                 if (File.Exists(Path.Combine(botPath, entry))) return ("node", $"\"{entry}\"");
             }
         }
         return (string.Empty, string.Empty);
     }


    // ... Sisa kode Program.cs (loop utama, menu lain, pause) tetap sama ...

    // ==========================================================
    // LOOP ORCHESTRATOR UTAMA (NON-INTERAKTIF)
    // ==========================================================

    private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken)
    {
        // ... (Kode loop utama dari Part 4) ...
        AnsiConsole.MarkupLine("[bold cyan]Starting Orchestrator Loop...[/]");
        AnsiConsole.MarkupLine($"[dim]Keep-Alive check every {KeepAliveInterval.TotalMinutes} minutes.[/]");
        AnsiConsole.MarkupLine($"[dim]Error retry delay: {ErrorRetryDelay.TotalMinutes} minutes.[/]");

        while (!cancellationToken.IsCancellationRequested)
        {
            TokenEntry currentToken = TokenManager.GetCurrentToken();
            TokenState currentState = TokenManager.GetState();
            string? activeCodespace = currentState.ActiveCodespaceName;

            AnsiConsole.Write(new Rule($"[yellow]Processing Token #{currentState.CurrentIndex + 1} (@{currentToken.Username ?? "???"})[/]").LeftJustified());

            try
            {
                // 1. Cek Billing
                AnsiConsole.MarkupLine("Checking billing quota...");
                var billingInfo = await BillingManager.GetBillingInfo(currentToken);
                BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "???");

                if (!billingInfo.IsQuotaOk)
                {
                    AnsiConsole.MarkupLine("[red]Quota insufficient. Rotating token...[/]");
                    // Hapus codespace jika ada sebelum rotasi
                    if (!string.IsNullOrEmpty(activeCodespace))
                    {
                        await CodespaceManager.DeleteCodespace(currentToken, activeCodespace);
                        currentState.ActiveCodespaceName = null;
                        TokenManager.SaveState(currentState);
                    }
                    TokenManager.SwitchToNextToken();
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); // Delay sedikit setelah rotasi
                    continue; // Lanjut ke iterasi berikutnya dengan token baru
                }

                // 2. Pastikan Codespace Sehat
                AnsiConsole.MarkupLine("Ensuring healthy codespace...");
                activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken);

                // Simpan nama codespace yang aktif ke state
                if (currentState.ActiveCodespaceName != activeCodespace)
                {
                    currentState.ActiveCodespaceName = activeCodespace;
                    TokenManager.SaveState(currentState);
                    AnsiConsole.MarkupLine($"[green]✓ Active codespace set to: {activeCodespace}[/]");

                    // Jika codespace baru atau di-recreate, upload config & trigger startup
                    AnsiConsole.MarkupLine("New/Recreated codespace detected. Uploading configs and triggering startup...");
                    await CodespaceManager.UploadConfigs(currentToken, activeCodespace);
                    await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                    AnsiConsole.MarkupLine("[green]✓ Initial startup complete. Entering Keep-Alive mode.[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[green]✓ Codespace is healthy and running.[/]");
                }

                // 3. Masuk Mode Keep-Alive (delay lalu cek SSH)
                AnsiConsole.MarkupLine($"Sleeping for Keep-Alive interval ({KeepAliveInterval.TotalMinutes} minutes)...");
                await Task.Delay(KeepAliveInterval, cancellationToken);

                // Cek SSH lagi setelah delay
                AnsiConsole.MarkupLine("Keep-Alive: Checking SSH health...");
                // Pastikan activeCodespace tidak null sebelum cek SSH
                if (string.IsNullOrEmpty(activeCodespace))
                {
                     AnsiConsole.MarkupLine("[yellow]Keep-Alive: No active codespace found in state. Skipping SSH check.[/]");
                     // Hapus state agar di-recreate di siklus berikutnya
                     currentState.ActiveCodespaceName = null;
                     TokenManager.SaveState(currentState);
                     continue; // Langsung ke siklus berikutnya
                }

                if (!await CodespaceManager.CheckSshHealth(currentToken, activeCodespace))
                {
                    AnsiConsole.MarkupLine("[red]Keep-Alive: SSH check failed! Codespace might be stopped or unresponsive.[/]");
                    // Hapus state codespace aktif agar iterasi berikutnya recreate
                    currentState.ActiveCodespaceName = null;
                    TokenManager.SaveState(currentState);
                    AnsiConsole.MarkupLine("[yellow]Will attempt to recreate on next cycle.[/]");
                    // Jangan rotasi token, biarkan loop coba lagi dengan token sama
                } else {
                     AnsiConsole.MarkupLine("[green]Keep-Alive: SSH check OK.[/]");
                     // Opsional: Bisa trigger auto-start.sh lagi di sini jika perlu restart periodik
                     // AnsiConsole.MarkupLine("Keep-Alive: Re-triggering startup script...");
                     // await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                }

            }
            catch (OperationCanceledException) {
                AnsiConsole.MarkupLine("\n[yellow]Orchestrator loop cancelled.[/]");
                break; // Keluar dari loop while
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[bold red]ERROR in orchestrator loop:[/]");
                AnsiConsole.WriteException(ex);

                // Jika error terkait GH command (misal timeout, auth), rotasi mungkin sudah terjadi di ShellHelper
                // Jika error lain, coba delay sebelum lanjut
                if (!ex.Message.Contains("Triggering token rotation"))
                {
                     AnsiConsole.MarkupLine($"[yellow]Retrying after {ErrorRetryDelay.TotalMinutes} minutes...[/]");
                     try {
                          await Task.Delay(ErrorRetryDelay, cancellationToken);
                     } catch (OperationCanceledException) { break; } // Batal saat delay
                } else {
                     // Jika rotasi sudah terjadi, delay sebentar saja
                     try {
                          await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                     } catch (OperationCanceledException) { break; }
                }
            }
        }
    }


    // Helper Pause
    private static void Pause(string message, CancellationToken cancellationToken) {
        AnsiConsole.MarkupLine($"\n[grey]{message} (Ctrl+C to cancel wait)[/]");
        try
        {
            // Loop ini memungkinkan Ctrl+C membatalkan penungguan
            while (true)
            {
                 if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();
                 if (Console.KeyAvailable) break; // Ada input, keluar loop
                 Task.Delay(50).Wait(); // Cek sebentar
            }
            // Bersihkan buffer key setelah loop selesai
            while(Console.KeyAvailable) Console.ReadKey(intercept: true);
        }
        catch (OperationCanceledException)
        {
             AnsiConsole.MarkupLine("[yellow]Wait cancelled.[/]");
             throw; // Lemparkan lagi agar loop menu berhenti jika perlu
        }
    }
} // Akhir class Program
