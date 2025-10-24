using Spectre.Console;
using System.Linq;
using System;
using System.Threading; // <-- Tambahkan using
using System.Threading.Tasks; // <-- Tambahkan using

namespace Orchestrator;

internal static class Program
{
    public static bool InteractiveBotCancelled = false;
    private static CancellationTokenSource _cts = new CancellationTokenSource(); // Token source

    public static async Task Main(string[] args)
    {
        Console.CancelKeyPress += (sender, e) =>
        {
            if (!_cts.IsCancellationRequested) // Hanya cancel sekali
            {
                AnsiConsole.MarkupLine("\n[yellow]Ctrl+C terdeteksi. Meminta pembatalan...[/]");
                e.Cancel = true; // Mencegah TUI langsung exit
                InteractiveBotCancelled = true; // Set flag global (masih berguna)
                _cts.Cancel(); // Batalkan token
            } else {
                 AnsiConsole.MarkupLine("[grey](Pembatalan sedang diproses...)[/]");
                 e.Cancel = true; // Tetap cegah exit jika ditekan lagi
            }
        };

        TokenManager.Initialize();

        if (args.Length > 0)
        {
            await RunTask(args[0]);
            return;
        }

        await RunInteractive();
    }

    private static async Task RunTask(string task)
    {
        // ... (Tidak berubah) ...
        switch (task.ToLower())
        {
            case "--update-bots":
                await BotUpdater.UpdateAllBots();
                break;
            case "--deploy-proxies":
                await ProxyManager.DeployProxies();
                break;
            case "--trigger-all":
                await GitHubDispatcher.TriggerAllBotsWorkflow();
                break;
            case "--status":
                await GitHubDispatcher.GetWorkflowRuns();
                break;
            default:
                AnsiConsole.MarkupLine($"[red]Unknown task: {task}[/]");
                break;
        }
    }

    // =================================================================
    // NAVIGASI MENU UTAMA
    // =================================================================

    private static async Task RunInteractive()
    {
        while (true)
        {
            // === RESET TOKEN DI SETIAP ITERASI MENU UTAMA ===
            if (_cts.IsCancellationRequested)
            {
                _cts.Dispose(); // Buang yang lama
                _cts = new CancellationTokenSource(); // Buat baru
            }
            InteractiveBotCancelled = false; // Reset flag juga
            // ===============================================

            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[grey]Interactive Proxy Orchestrator - Local Control, Remote Execution[/]");

            var choice = AnsiConsole.Prompt( /* ... (Menu tidak berubah) ... */
                 new SelectionPrompt<string>()
                    .Title("\n[bold cyan]MAIN MENU[/]")
                    .PageSize(10)
                    .WrapAround()
                    .AddChoices(new[]
                    {
                        "1. [[SETUP]] Configuration & Token Management",
                        "2. [[LOCAL]] Bot & Proxy Management",
                        "3. [[HYBRID]] Interactive Remote Execution",
                        "4. [[REMOTE]] GitHub Actions Control",
                        "5. [[DEBUG]] Local Bot Testing",
                        "0. Exit"
                    }));

            if (choice.StartsWith("0"))
            {
                AnsiConsole.MarkupLine("[green]Goodbye![/]");
                return;
            }

            try // Tangkap cancel di level menu jika terjadi saat menunggu Pause()
            {
                 // Reset flag cancel sebelum masuk submenu
                 InteractiveBotCancelled = false;
                 // Jika token sudah dicancel sebelumnya (misal dari Pause), buat baru
                 if (_cts.IsCancellationRequested) {
                      _cts.Dispose();
                      _cts = new CancellationTokenSource();
                 }

                switch (choice.Split('.')[0])
                {
                    case "1": await ShowSetupMenu(); break;
                    case "2": await ShowLocalMenu(); break;
                    case "3": await ShowHybridMenu(); break;
                    case "4": await ShowRemoteMenu(); break;
                    case "5": await ShowDebugMenu(); break;
                }
            }
            catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine("[yellow]Operasi dibatalkan. Kembali ke menu utama.[/]");
                 // Token sudah dicancel, akan direset di iterasi berikutnya
            }
        }
    }

    // =================================================================
    // SUB-MENU HANDLERS
    // =================================================================

    private static async Task ShowSetupMenu()
    {
        while (true)
        {
            // ... (Tampilan menu tidak berubah) ...
             AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Setup").Centered().Color(Color.Yellow));

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold yellow]SETUP & CONFIGURATION[/]")
                    .PageSize(10)
                    .WrapAround()
                    .AddChoices(new[]
                    {
                        "1. Validate Tokens & Get Usernames",
                        "2. Invite Collaborators",
                        "3. Accept Invitations",
                        "4. Show Token/Proxy Status",
                        "5. [[SYSTEM]] Refresh All Configs (Reload files)",
                        "0. [[Back]] Kembali ke Menu Utama"
                    }));


            var selection = choice.Split('.')[0];
            if (selection == "0") return;

            bool pause = true;
            try
            {
                 // Reset flag & token source sebelum eksekusi
                 InteractiveBotCancelled = false;
                 if (_cts.IsCancellationRequested) { _cts.Dispose(); _cts = new CancellationTokenSource(); }

                switch (selection)
                {
                    case "1": await CollaboratorManager.ValidateAllTokens(); break;
                    case "2": await CollaboratorManager.InviteCollaborators(); break;
                    case "3": await CollaboratorManager.AcceptInvitations(); break;
                    case "4": TokenManager.ShowStatus(); break;
                    case "5": TokenManager.ReloadAllConfigs(); break;
                    default: pause = false; break;
                }
            }
            catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine("[yellow]Operasi dibatalkan.[/]");
                 // Kembali ke loop menu setup
                 continue; // Skip Pause()
            }

            if (pause) Pause(); // Bisa dibatalkan juga
        }
    }

    private static async Task ShowLocalMenu()
    {
         // ... (Sama seperti ShowSetupMenu, tambahkan try-catch dan reset token) ...
         while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Local").Centered().Color(Color.Green));

            var choice = AnsiConsole.Prompt( /* ... (menu tidak berubah) ... */
                 new SelectionPrompt<string>()
                    .Title("\n[bold green]LOCAL BOT & PROXY MANAGEMENT[/]")
                    .PageSize(10)
                    .WrapAround()
                    .AddChoices(new[]
                    {
                        "1. Update All Bots & Tools",
                        "2. Deploy Proxies",
                        "3. Show Bot Config",
                        "0. [[Back]] Kembali ke Menu Utama"
                    }));

            var selection = choice.Split('.')[0];
            if (selection == "0") return;

            bool pause = true;
            try
            {
                 InteractiveBotCancelled = false;
                 if (_cts.IsCancellationRequested) { _cts.Dispose(); _cts = new CancellationTokenSource(); }

                switch (selection)
                {
                    case "1": await BotUpdater.UpdateAllBots(); break;
                    case "2": await ProxyManager.DeployProxies(); break;
                    case "3": BotUpdater.ShowConfig(); break;
                    default: pause = false; break;
                }
            }
             catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine("[yellow]Operasi dibatalkan.[/]");
                 continue;
            }

            if (pause) Pause();
        }
    }

    private static async Task ShowHybridMenu()
    {
         // ... (Sama seperti ShowSetupMenu, tambahkan try-catch dan reset token) ...
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Hybrid").Centered().Color(Color.Blue));

            var choice = AnsiConsole.Prompt( /* ... (menu tidak berubah) ... */
                 new SelectionPrompt<string>()
                    .Title("\n[bold blue]HYBRID INTERACTIVE EXECUTION[/]")
                    .PageSize(10)
                    .WrapAround()
                    .AddChoices(new[]
                    {
                        "1. Run Interactive Bot -> Remote",
                        "2. Run ALL Interactive Bots -> Remote",
                        "0. [[Back]] Kembali ke Menu Utama"
                    }));

            var selection = choice.Split('.')[0];
            if (selection == "0") return;

            bool pause = true;
            try
            {
                 InteractiveBotCancelled = false;
                 if (_cts.IsCancellationRequested) { _cts.Dispose(); _cts = new CancellationTokenSource(); }

                switch (selection)
                {
                    // === KIRIM TOKEN KE METHOD ===
                    case "1": await RunSingleInteractiveBot(_cts.Token); break;
                    case "2": await RunAllInteractiveBots(_cts.Token); break;
                    // ==============================
                    default: pause = false; break;
                }
            }
             catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine("[yellow]Operasi dibatalkan.[/]");
                 // Flag InteractiveBotCancelled sudah di-set oleh handler
                 // Biarkan loop RunAllInteractiveBots handle ini jika perlu
                 // Continue untuk kembali ke menu Hybrid
                 continue;
            }


            // Pause hanya jika tidak cancel dan tidak kembali
            if (pause && !InteractiveBotCancelled) Pause();
            // Reset flag setelah pause atau skip (jika cancel terjadi di dalam method tapi tidak throw ke sini)
             InteractiveBotCancelled = false;
        }
    }

    private static async Task ShowRemoteMenu()
    {
         // ... (Sama seperti ShowSetupMenu, tambahkan try-catch dan reset token) ...
         while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Remote").Centered().Color(Color.Red));

            var choice = AnsiConsole.Prompt( /* ... (menu tidak berubah) ... */
                new SelectionPrompt<string>()
                    .Title("\n[bold red]GITHUB ACTIONS CONTROL[/]")
                    .PageSize(10)
                    .WrapAround()
                    .AddChoices(new[]
                    {
                        "1. Trigger ALL Bots (Workflow)",
                        "2. View Workflow Status",
                        "0. [[Back]] Kembali ke Menu Utama"
                    }));

            var selection = choice.Split('.')[0];
            if (selection == "0") return;

            bool pause = true;
            try
            {
                 InteractiveBotCancelled = false;
                 if (_cts.IsCancellationRequested) { _cts.Dispose(); _cts = new CancellationTokenSource(); }

                switch (selection)
                {
                    case "1": await GitHubDispatcher.TriggerAllBotsWorkflow(); break;
                    case "2": await GitHubDispatcher.GetWorkflowRuns(); break;
                    default: pause = false; break;
                }
            }
            catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine("[yellow]Operasi dibatalkan.[/]");
                 continue;
            }

            if (pause) Pause();
        }
    }

    private static async Task ShowDebugMenu()
    {
         // ... (Sama seperti ShowSetupMenu, tambahkan try-catch dan reset token) ...
         while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Debug").Centered().Color(Color.Grey));

            var choice = AnsiConsole.Prompt( /* ... (menu tidak berubah) ... */
                 new SelectionPrompt<string>()
                    .Title("\n[bold grey]DEBUG & LOCAL TESTING[/]")
                    .PageSize(10)
                    .WrapAround()
                    .AddChoices(new[]
                    {
                        "1. Test Local Bot (No Remote)",
                        "0. [[Back]] Kembali ke Menu Utama"
                    }));

            var selection = choice.Split('.')[0];
            if (selection == "0") return;

            bool pause = true;
            try
            {
                 InteractiveBotCancelled = false;
                 if (_cts.IsCancellationRequested) { _cts.Dispose(); _cts = new CancellationTokenSource(); }

                switch (selection)
                {
                     // === KIRIM TOKEN KE METHOD ===
                    case "1": await TestLocalBot(_cts.Token); break;
                     // =============================
                    default: pause = false; break;
                }
            }
             catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine("[yellow]Operasi dibatalkan.[/]");
                 continue;
            }

            // Pause hanya jika tidak cancel dan tidak kembali
            if (pause && !InteractiveBotCancelled) Pause();
             InteractiveBotCancelled = false;
        }
    }

    private static void Pause()
    {
        AnsiConsole.MarkupLine("\n[grey]Press Enter to continue... (Ctrl+C to cancel wait)[/]");
         try
         {
             // Console.ReadLine() tidak bisa dibatalkan, gunakan Task.Delay
             // Tapi kita ingin block sampai user tekan Enter.
             // Workaround: Loop cek key atau cara lain?
             // Simplifikasi: Biarkan ReadLine, Ctrl+C akan ditangkap handler utama
             Console.ReadLine();
         }
         catch (OperationCanceledException) { /* Abaikan cancel saat pause */ }

    }

    // =================================================================
    // METHOD HELPER
    // =================================================================

    private static readonly BotEntry BackOption = new() { Name = "[[Back]] Kembali", Type = "SYSTEM" };

    // === MODIFIKASI: Tambahkan CancellationToken ===
    private static async Task RunSingleInteractiveBot(CancellationToken cancellationToken)
    {
        // ... (Bagian awal tidak berubah) ...
        var config = BotConfig.Load();
        if (config == null) return;
        var bots = config.BotsAndTools.Where(b => b.Enabled && b.IsBot).OrderBy(b => b.Name).ToList();
        if (!bots.Any()) { AnsiConsole.MarkupLine("[yellow]No active bots found.[/]"); return; }
        var choices = bots.ToList();
        choices.Add(BackOption);

        var selectedBot = AnsiConsole.Prompt( /* ... (prompt tidak berubah) ... */
             new SelectionPrompt<BotEntry>()
                .Title("[cyan]Select bot for interactive run:[/]")
                .PageSize(20)
                .WrapAround()
                .UseConverter(b => b == BackOption ? b.Name : $"{b.Name} ({b.Type})")
                .AddChoices(choices));

        if (selectedBot == BackOption) return;

        // Kirim cancellation token ke CaptureAndTriggerBot
        await InteractiveProxyRunner.CaptureAndTriggerBot(selectedBot, cancellationToken);
        // Exception OperationCanceledException akan ditangkap di ShowHybridMenu
    }

     // === MODIFIKASI: Tambahkan CancellationToken ===
    private static async Task RunAllInteractiveBots(CancellationToken cancellationToken)
    {
        // ... (Bagian awal tidak berubah) ...
        var config = BotConfig.Load();
        if (config == null) return;
        var bots = config.BotsAndTools.Where(b => b.Enabled && b.IsBot).OrderBy(b => b.Name).ToList();
        if (!bots.Any()) { AnsiConsole.MarkupLine("[yellow]No active bots found.[/]"); return; }

        AnsiConsole.MarkupLine($"[cyan]Found {bots.Count} active bots (Sorted Alphabetically)[/]");
        AnsiConsole.MarkupLine("[yellow]WARNING: This will run all bots locally for input capture.[/]");
        AnsiConsole.MarkupLine("[grey]You can press Ctrl+C during a bot's run to skip to the next.[/]");

        if (!AnsiConsole.Confirm("Continue?")) return;


        var successCount = 0;
        var failCount = 0;

        foreach (var bot in bots)
        {
            // Cek cancel *sebelum* memulai bot berikutnya
            if (cancellationToken.IsCancellationRequested) {
                 AnsiConsole.MarkupLine("[yellow]Loop dibatalkan.[/]");
                 InteractiveBotCancelled = true; // Pastikan flag ter-set
                 break; // Keluar dari loop foreach
            }
             // Reset flag global hanya jika token TIDAK dicancel
            InteractiveBotCancelled = false;


            AnsiConsole.Write(new Rule($"[cyan]{bot.Name}[/]").Centered());

            try
            {
                // === KIRIM CancellationToken ===
                await InteractiveProxyRunner.CaptureAndTriggerBot(bot, cancellationToken);

                // Cek flag *setelah* selesai (jika tidak ada exception)
                // Flag ini di-set oleh CancelKeyPress handler ATAU jika exception cancel terjadi di CaptureAndTriggerBot
                 if (InteractiveBotCancelled || cancellationToken.IsCancellationRequested)
                {
                    // Dianggap skip/fail jika dibatalkan
                    failCount++;
                    AnsiConsole.MarkupLine("[yellow]Skipped due to cancellation.[/]");
                     // Jika token sudah dicancel, langsung break loop
                     if (cancellationToken.IsCancellationRequested) break;
                } else {
                    successCount++;
                }
            }
            catch (OperationCanceledException)
            {
                 // Tangkap cancel exception dari CaptureAndTriggerBot
                 failCount++;
                 AnsiConsole.MarkupLine("[yellow]Skipped due to cancellation (exception).[/]");
                 // Set flag global juga untuk konsistensi
                 InteractiveBotCancelled = true;
                 break; // Keluar dari loop foreach jika ada cancel exception
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error processing {bot.Name}: {ex.Message}[/]");
                failCount++;
                InteractiveBotCancelled = false; // Error bukan cancel
            }

            // Tampilkan prompt 'next' hanya jika TIDAK di-cancel DAN bukan bot terakhir
            if (!InteractiveBotCancelled && !cancellationToken.IsCancellationRequested && bot != bots.Last())
            {
                AnsiConsole.MarkupLine("\n[dim]Press Enter for next bot...[/]");
                 // ReadLine tidak bisa dicancel, jika user tekan Ctrl+C di sini, handler akan jalan
                 Console.ReadLine();
                 // Cek lagi setelah ReadLine jika user menekan Ctrl+C saat menunggu
                 if (cancellationToken.IsCancellationRequested) {
                      AnsiConsole.MarkupLine("[yellow]Loop dibatalkan saat menunggu.[/]");
                      InteractiveBotCancelled = true; // Pastikan flag ter-set
                      break;
                 }
            }
            // Jika dibatalkan, loop akan otomatis lanjut ke cek cancel di awal iterasi berikutnya atau break
        }

        AnsiConsole.MarkupLine($"\n[bold]Summary:[/]");
        AnsiConsole.MarkupLine($"[green]✓ Completed/Attempted: {successCount}[/]");
        AnsiConsole.MarkupLine($"[yellow]✗ Skipped/Failed: {failCount}[/]");
    }

     // === MODIFIKASI: Tambahkan CancellationToken ===
    private static async Task TestLocalBot(CancellationToken cancellationToken)
    {
       // ... (Bagian awal tidak berubah) ...
        var config = BotConfig.Load();
        if (config == null) return;
        var bots = config.BotsAndTools.Where(b => b.Enabled && b.IsBot).OrderBy(b => b.Name).ToList();
        if (!bots.Any()) { AnsiConsole.MarkupLine("[yellow]No active bots found.[/]"); return; }
        var choices = bots.ToList();
        choices.Add(BackOption);

        var selectedBot = AnsiConsole.Prompt( /* ... (prompt tidak berubah) ... */
             new SelectionPrompt<BotEntry>()
                .Title("[cyan]Select bot for local test:[/]")
                .PageSize(20)
                .WrapAround()
                .UseConverter(b => b == BackOption ? b.Name : $"{b.Name} ({b.Type})")
                .AddChoices(choices));


        if (selectedBot == BackOption) return;

        var botPath = Path.Combine("..", selectedBot.Path);
        if (!Directory.Exists(botPath)) { /*...*/ return; }

        await BotRunner.InstallDependencies(botPath, selectedBot.Type);
        var (executor, args) = BotRunner.GetRunCommand(botPath, selectedBot.Type);
        if (string.IsNullOrEmpty(executor)) { /*...*/ return; }

        AnsiConsole.MarkupLine($"[green]Running {selectedBot.Name} locally...[/]");
        AnsiConsole.MarkupLine("[dim]This is a test run. No remote execution.[/]\n");

        // === KIRIM CancellationToken ===
        await ShellHelper.RunInteractive(executor, args, botPath, cancellationToken);
        // Exception OperationCanceledException akan ditangkap di ShowDebugMenu
    }
}
