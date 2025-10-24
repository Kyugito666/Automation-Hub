using Spectre.Console;
using System.Linq;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator;

internal static class Program
{
    // HAPUS FLAG STATIC: public static bool InteractiveBotCancelled = false;
    private static CancellationTokenSource _cts = new CancellationTokenSource();

    public static async Task Main(string[] args)
    {
        Console.CancelKeyPress += (sender, e) =>
        {
            if (!_cts.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("\n[yellow]Ctrl+C terdeteksi. Meminta pembatalan...[/]");
                e.Cancel = true;
                // InteractiveBotCancelled = true; // HAPUS FLAG
                _cts.Cancel();
            } else {
                 AnsiConsole.MarkupLine("[grey](Pembatalan sedang diproses...)[/]");
                 e.Cancel = true;
            }
        };

        // ... (TokenManager.Initialize(), RunTask tidak berubah) ...
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
        // Tetap sama, tapi mungkin perlu CancellationToken jika task panjang
        // Untuk sekarang biarkan dulu
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


    private static async Task RunInteractive()
    {
        while (true)
        {
            // Reset token jika sudah pernah dibatalkan
            if (_cts.IsCancellationRequested)
            {
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }
            // InteractiveBotCancelled = false; // Hapus flag

            AnsiConsole.Clear();
            // ... (Tampilan menu utama tidak berubah) ...
            AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[grey]Interactive Proxy Orchestrator - Local Control, Remote Execution[/]");

            var choice = AnsiConsole.Prompt(
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


            if (choice.StartsWith("0")) { /* Keluar */ return; }

            try
            {
                // Reset token lagi SEBELUM masuk submenu, antisipasi cancel di Pause()
                if (_cts.IsCancellationRequested) { _cts.Dispose(); _cts = new CancellationTokenSource(); }
                // InteractiveBotCancelled = false; // Hapus flag

                switch (choice.Split('.')[0])
                {
                    // Kirim token ke submenu yang mungkin butuh cancel (misal ada ReadLine/Pause)
                    case "1": await ShowSetupMenu(_cts.Token); break;
                    case "2": await ShowLocalMenu(_cts.Token); break;
                    case "3": await ShowHybridMenu(_cts.Token); break;
                    case "4": await ShowRemoteMenu(_cts.Token); break;
                    case "5": await ShowDebugMenu(_cts.Token); break;
                }
            }
            catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine("[yellow]Operasi dibatalkan. Kembali ke menu utama.[/]");
                 // Token sudah dicancel, akan direset di iterasi berikutnya
            }
            // Tangkap exception lain jika perlu
            catch (Exception ex) {
                 AnsiConsole.MarkupLine($"[red]Error tak terduga: {ex.Message}[/]");
                 Pause(); // Beri waktu user baca error
            }
        }
    }

    // === MODIFIKASI SEMUA SUBMENU: Tambah CancellationToken ===
    private static async Task ShowSetupMenu(CancellationToken cancellationToken)
    {
        while (true)
        {
             cancellationToken.ThrowIfCancellationRequested(); // Cek di awal loop
             AnsiConsole.Clear();
             // ... (Tampilan menu tidak berubah) ...
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
            // Tidak perlu try-catch di sini, biarkan exception propagate ke RunInteractive
            switch (selection)
            {
                case "1": await CollaboratorManager.ValidateAllTokens(); break; // Asumsi method ini tidak perlu token
                case "2": await CollaboratorManager.InviteCollaborators(); break; // Asumsi method ini tidak perlu token
                case "3": await CollaboratorManager.AcceptInvitations(); break; // Asumsi method ini tidak perlu token
                case "4": TokenManager.ShowStatus(); break;
                case "5": TokenManager.ReloadAllConfigs(); break;
                default: pause = false; break;
            }

            if (pause) Pause(cancellationToken); // Kirim token ke Pause
        }
    }

    private static async Task ShowLocalMenu(CancellationToken cancellationToken)
    {
       // ... (Sama seperti ShowSetupMenu) ...
       while (true)
        {
             cancellationToken.ThrowIfCancellationRequested();
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Local").Centered().Color(Color.Green));
             var choice = AnsiConsole.Prompt(
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
            switch (selection)
            {
                 // Asumsikan method ini tidak perlu token cancel,
                 // tapi jika prosesnya panjang, sebaiknya ditambahkan
                case "1": await BotUpdater.UpdateAllBots(); break;
                case "2": await ProxyManager.DeployProxies(); break;
                case "3": BotUpdater.ShowConfig(); break;
                default: pause = false; break;
            }

            if (pause) Pause(cancellationToken);
        }
    }

     private static async Task ShowHybridMenu(CancellationToken cancellationToken)
    {
        while (true)
        {
             cancellationToken.ThrowIfCancellationRequested();
            AnsiConsole.Clear();
             AnsiConsole.Write(new FigletText("Hybrid").Centered().Color(Color.Blue));
             var choice = AnsiConsole.Prompt(
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
            switch (selection)
            {
                // Kirim token ke method yang menjalankan bot
                case "1": await RunSingleInteractiveBot(cancellationToken); break;
                case "2": await RunAllInteractiveBots(cancellationToken); break;
                default: pause = false; break;
            }

            // Pause HANYA jika operasi selesai TANPA dibatalkan
             // Cek token lagi setelah operasi selesai
             if (pause && !cancellationToken.IsCancellationRequested)
             {
                  Pause(cancellationToken);
             }
        }
    }

    private static async Task ShowRemoteMenu(CancellationToken cancellationToken)
    {
        // ... (Sama seperti ShowSetupMenu) ...
         while (true)
        {
             cancellationToken.ThrowIfCancellationRequested();
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Remote").Centered().Color(Color.Red));
             var choice = AnsiConsole.Prompt(
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
            switch (selection)
            {
                 // Asumsikan method ini cepat dan tidak perlu token
                case "1": await GitHubDispatcher.TriggerAllBotsWorkflow(); break;
                case "2": await GitHubDispatcher.GetWorkflowRuns(); break;
                default: pause = false; break;
            }

            if (pause) Pause(cancellationToken);
        }
    }

     private static async Task ShowDebugMenu(CancellationToken cancellationToken)
    {
        while (true)
        {
             cancellationToken.ThrowIfCancellationRequested();
            AnsiConsole.Clear();
             AnsiConsole.Write(new FigletText("Debug").Centered().Color(Color.Grey));
             var choice = AnsiConsole.Prompt(
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
            switch (selection)
            {
                // Kirim token ke method yang menjalankan bot
                case "1": await TestLocalBot(cancellationToken); break;
                default: pause = false; break;
            }

             if (pause && !cancellationToken.IsCancellationRequested)
             {
                  Pause(cancellationToken);
             }
        }
    }

    // === MODIFIKASI: Tambahkan CancellationToken ===
    private static void Pause(CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine("\n[grey]Press Enter to continue... (Ctrl+C to cancel wait & return)[/]");
        try
        {
            // Cara sederhana menunggu Enter atau Cancel
            while (!Console.KeyAvailable)
            {
                cancellationToken.ThrowIfCancellationRequested(); // Cek cancel
                Task.Delay(100, cancellationToken).Wait(cancellationToken); // Tunggu sebentar
            }
            // Jika ada key, baca (buang)
            Console.ReadKey(intercept: true);
        }
        catch (OperationCanceledException)
        {
             // Jangan tampilkan pesan di sini, biarkan pemanggil yang handle
             throw; // Re-throw agar pemanggil tahu
        }
         // Jika user tekan Enter, exception tidak terjadi, method selesai
    }


    // === METHOD HELPER (RunSingle, RunAll, TestLocal diupdate) ===

    private static readonly BotEntry BackOption = new() { Name = "[[Back]] Kembali", Type = "SYSTEM" };

    // === MODIFIKASI: Tambahkan CancellationToken ===
    private static async Task RunSingleInteractiveBot(CancellationToken cancellationToken)
    {
        // ... (Bagian awal tidak berubah, load config, pilih bot) ...
        var config = BotConfig.Load();
        if (config == null) return;
        var bots = config.BotsAndTools.Where(b => b.Enabled && b.IsBot).OrderBy(b => b.Name).ToList();
        if (!bots.Any()) { AnsiConsole.MarkupLine("[yellow]No active bots found.[/]"); return; }
        var choices = bots.ToList(); choices.Add(BackOption);

        var selectedBot = AnsiConsole.Prompt( /* ... prompt ... */
              new SelectionPrompt<BotEntry>()
                .Title("[cyan]Select bot for interactive run:[/]")
                .PageSize(20)
                .WrapAround()
                .UseConverter(b => b == BackOption ? b.Name : $"{b.Name} ({b.Type})")
                .AddChoices(choices));

        if (selectedBot == BackOption) return;

        // === KIRIM CancellationToken ===
        await InteractiveProxyRunner.CaptureAndTriggerBot(selectedBot, cancellationToken);
        // Exception OperationCanceledException akan ditangkap oleh ShowHybridMenu
    }

     // === MODIFIKASI: Tambahkan CancellationToken ===
    private static async Task RunAllInteractiveBots(CancellationToken cancellationToken)
    {
        // ... (Bagian awal tidak berubah) ...
        var config = BotConfig.Load();
        if (config == null) return;
        var bots = config.BotsAndTools.Where(b => b.Enabled && b.IsBot).OrderBy(b => b.Name).ToList();
        if (!bots.Any()) { /*...*/ return; }
        AnsiConsole.MarkupLine($"[cyan]Found {bots.Count} active bots (Sorted Alphabetically)[/]");
        AnsiConsole.MarkupLine("[yellow]WARNING: This will run all bots locally for input capture.[/]");
        AnsiConsole.MarkupLine("[grey]You can press Ctrl+C during a bot's run to skip to the next.[/]");
        if (!AnsiConsole.Confirm("Continue?")) return;

        var successCount = 0;
        var failCount = 0;

        foreach (var bot in bots)
        {
            // Cek cancel di awal setiap loop
            if (cancellationToken.IsCancellationRequested) {
                 AnsiConsole.MarkupLine("\n[yellow]Loop dibatalkan.[/]");
                 failCount += (bots.Count - (successCount + failCount)); // Sisa bot dianggap gagal/skip
                 break; // Keluar dari loop
            }

            AnsiConsole.Write(new Rule($"[cyan]{bot.Name}[/]").Centered());

            try
            {
                // === KIRIM CancellationToken ===
                await InteractiveProxyRunner.CaptureAndTriggerBot(bot, cancellationToken);
                // Jika sampai sini tanpa exception, anggap sukses
                successCount++;
            }
            catch (OperationCanceledException)
            {
                 // Tangkap cancel, anggap skip/fail, dan break loop
                 failCount++;
                 AnsiConsole.MarkupLine("[yellow]Skipped due to cancellation.[/]");
                 break; // Keluar dari loop foreach
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error processing {bot.Name}: {ex.Message}[/]");
                failCount++;
                // Lanjut ke bot berikutnya jika error biasa
            }

            // JANGAN tampilkan "Press Enter" jika ini bot terakhir
             if (bot != bots.Last())
             {
                 // Pause hanya jika tidak ada cancel yang terjadi SEBELUMNYA di loop ini
                 // (Tidak perlu cek token lagi karena sudah dicek di awal loop)
                 Pause(cancellationToken); // Kirim token ke Pause, bisa di-cancel di sini juga
             }
        } // End foreach

        AnsiConsole.MarkupLine($"\n[bold]Summary:[/]");
        AnsiConsole.MarkupLine($"[green]✓ Completed: {successCount}[/]");
        AnsiConsole.MarkupLine($"[yellow]✗ Skipped/Failed: {failCount}[/]");
    }

     // === MODIFIKASI: Tambahkan CancellationToken ===
    private static async Task TestLocalBot(CancellationToken cancellationToken)
    {
        // ... (Bagian awal tidak berubah) ...
        var config = BotConfig.Load();
        if (config == null) return;
        var bots = config.BotsAndTools.Where(b => b.Enabled && b.IsBot).OrderBy(b => b.Name).ToList();
        if (!bots.Any()) { /*...*/ return; }
        var choices = bots.ToList(); choices.Add(BackOption);

        var selectedBot = AnsiConsole.Prompt(/* ... prompt ... */
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

        AnsiConsole.MarkupLine($"[green]Running {selectedBot.Name} locally... (Press Ctrl+C to stop)[/]"); // Tambah hint
        AnsiConsole.MarkupLine("[dim]This is a test run. No remote execution.[/]\n");

        // === KIRIM CancellationToken ===
        await ShellHelper.RunInteractive(executor, args, botPath, cancellationToken);
        // Exception akan ditangkap oleh ShowDebugMenu
    }
} // End class Program
