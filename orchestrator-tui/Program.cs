using Spectre.Console;
using System.Linq;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator;

internal static class Program
{
    // Hapus flag static
    private static CancellationTokenSource _cts = new CancellationTokenSource();

    public static async Task Main(string[] args)
    {
        Console.CancelKeyPress += (sender, e) =>
        {
            if (!_cts.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("\n[yellow]Ctrl+C detected. Requesting cancellation...[/]");
                e.Cancel = true;
                _cts.Cancel();
            } else {
                 AnsiConsole.MarkupLine("[grey](Cancellation requested...)[/]");
                 e.Cancel = true;
            }
        };
        // ... (sisanya sama) ...
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
        // Tetap sama
        switch (task.ToLower()) { /* ... */ }
    }


    private static async Task RunInteractive()
    {
        while (true)
        {
            if (_cts.IsCancellationRequested) { _cts.Dispose(); _cts = new CancellationTokenSource(); }

            AnsiConsole.Clear();
            // ... (Tampilan menu utama tidak berubah) ...
             AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[grey]Interactive Proxy Orchestrator - Local Control, Remote Execution[/]");
             var choice = AnsiConsole.Prompt(/* ... */
                new SelectionPrompt<string>()
                    .Title("\n[bold cyan]MAIN MENU[/]")
                    .PageSize(10)
                    .WrapAround()
                    .AddChoices(new[]
                    {
                        "1. [[SETUP]] Configuration & Token Management",
                        "2. [[LOCAL]] Bot & Proxy Management",
                        "3. [[HYBRID]] Local Run & Remote Trigger", // <-- Label diubah
                        "4. [[REMOTE]] GitHub Actions Control",
                        "5. [[DEBUG]] Local Bot Testing",
                        "0. Exit"
                    }));

            if (choice.StartsWith("0")) { return; }

            try
            {
                if (_cts.IsCancellationRequested) { _cts.Dispose(); _cts = new CancellationTokenSource(); }

                switch (choice.Split('.')[0])
                {
                    case "1": await ShowSetupMenu(_cts.Token); break;
                    case "2": await ShowLocalMenu(_cts.Token); break;
                    case "3": await ShowHybridMenu(_cts.Token); break;
                    case "4": await ShowRemoteMenu(_cts.Token); break;
                    case "5": await ShowDebugMenu(_cts.Token); break;
                }
            }
            catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine("\n[yellow]Operation cancelled. Returning to main menu.[/]");
                 PauseWithoutCancel();
            }
            catch (Exception ex) {
                 AnsiConsole.MarkupLine($"[red]Unexpected Error: {ex.Message}[/]");
                 AnsiConsole.WriteException(ex);
                 PauseWithoutCancel();
            }
        }
    }

    // === SUBMENU TETAP SAMA ===
    private static async Task ShowSetupMenu(CancellationToken cancellationToken) { /* ... implementasi ... */
        while (true)
        {
             cancellationToken.ThrowIfCancellationRequested();
             AnsiConsole.Clear();
             AnsiConsole.Write(new FigletText("Setup").Centered().Color(Color.Yellow));
             var choice = AnsiConsole.Prompt(/* ... menu ... */
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
    private static async Task ShowLocalMenu(CancellationToken cancellationToken) { /* ... implementasi ... */
        while (true)
        {
             cancellationToken.ThrowIfCancellationRequested();
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Local").Centered().Color(Color.Green));
             var choice = AnsiConsole.Prompt(/* ... menu ... */
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
    private static async Task ShowHybridMenu(CancellationToken cancellationToken) { /* ... implementasi ... */
         while (true)
        {
             cancellationToken.ThrowIfCancellationRequested();
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Hybrid").Centered().Color(Color.Blue));
             var choice = AnsiConsole.Prompt(
                 new SelectionPrompt<string>()
                    .Title("\n[bold blue]HYBRID: LOCAL RUN & REMOTE TRIGGER[/]") // <-- Label diubah
                    .PageSize(10)
                    .WrapAround()
                    .AddChoices(new[]
                    {
                        "1. Run Single Bot Locally -> Trigger Remote", // <-- Label diubah
                        "2. Run ALL Bots Locally -> Trigger Remote", // <-- Label diubah
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
             if (pause && !cancellationToken.IsCancellationRequested)
             {
                  Pause(cancellationToken);
             }
        }
    }
    private static async Task ShowRemoteMenu(CancellationToken cancellationToken) { /* ... implementasi ... */
         while (true)
        {
             cancellationToken.ThrowIfCancellationRequested();
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Remote").Centered().Color(Color.Red));
             var choice = AnsiConsole.Prompt(/* ... menu ... */
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
    private static async Task ShowDebugMenu(CancellationToken cancellationToken) { /* ... implementasi ... */
         while (true)
        {
             cancellationToken.ThrowIfCancellationRequested();
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Debug").Centered().Color(Color.Grey));
             var choice = AnsiConsole.Prompt(/* ... menu ... */
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
                case "1": await TestLocalBot(cancellationToken); break;
                default: pause = false; break;
            }
             if (pause && !cancellationToken.IsCancellationRequested)
             {
                  Pause(cancellationToken);
             }
        }
    }

    private static void Pause(CancellationToken cancellationToken = default) { /* ... implementasi ... */
         AnsiConsole.MarkupLine("\n[grey]Press Enter to continue... (Ctrl+C to cancel wait & return)[/]");
        try
        {
            while (!Console.KeyAvailable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task.Delay(50, cancellationToken).Wait(cancellationToken);
            }
            while(Console.KeyAvailable) Console.ReadKey(intercept: true);
        }
        catch (OperationCanceledException)
        {
             AnsiConsole.MarkupLine("[yellow]Wait cancelled.[/]");
             throw;
        }
    }
    private static void PauseWithoutCancel() { /* ... implementasi ... */
         AnsiConsole.MarkupLine("\n[grey]Press Enter to continue...[/]");
        Console.ReadLine();
     }


    // === METHOD HELPER (RunSingle, RunAll, TestLocal diupdate) ===

    private static readonly BotEntry BackOption = new() { Name = "[[Back]] Kembali", Type = "SYSTEM" };

    private static async Task RunSingleInteractiveBot(CancellationToken cancellationToken)
    {
        // ... (Bagian awal tidak berubah) ...
        var config = BotConfig.Load(); if (config == null) return;
        var bots = config.BotsAndTools.Where(b => b.Enabled && b.IsBot).OrderBy(b => b.Name).ToList();
        if (!bots.Any()) { /*...*/ return; }
        var choices = bots.ToList(); choices.Add(BackOption);
        var selectedBot = AnsiConsole.Prompt(/*...*/
            new SelectionPrompt<BotEntry>()
                .Title("[cyan]Select bot for local run & remote trigger:[/]")
                .PageSize(20).WrapAround()
                .UseConverter(b => b == BackOption ? b.Name : $"{b.Name} ({b.Type})")
                .AddChoices(choices));
        if (selectedBot == BackOption) return;

        // Panggil versi baru
        await InteractiveProxyRunner.CaptureAndTriggerBot(selectedBot, cancellationToken);
        // Exception akan ditangkap oleh ShowHybridMenu
    }

     private static async Task RunAllInteractiveBots(CancellationToken cancellationToken)
    {
        var config = BotConfig.Load(); if (config == null) return;
        var bots = config.BotsAndTools.Where(b => b.Enabled && b.IsBot).OrderBy(b => b.Name).ToList();
        if (!bots.Any()) { /*...*/ return; }
        AnsiConsole.MarkupLine($"[cyan]Found {bots.Count} active bots (Sorted Alphabetically)[/]");
        AnsiConsole.MarkupLine("[yellow]WARNING: This will run all bots locally first (capture mode), then trigger remote.[/]"); // Update label
        AnsiConsole.MarkupLine("[grey]You can press Ctrl+C during a bot's local run to skip it.[/]");
        if (!AnsiConsole.Confirm("Continue?")) return;

        var successCount = 0;
        var failCount = 0;

        foreach (var bot in bots)
        {
            // === Cek cancel di awal loop ===
            if (cancellationToken.IsCancellationRequested) {
                 AnsiConsole.MarkupLine("\n[yellow]Loop cancelled.[/]");
                 failCount += (bots.Count - (successCount + failCount)); // Sisa bot dianggap gagal/skip
                 break;
            }

            AnsiConsole.Write(new Rule($"[cyan]{bot.Name}[/]").Centered());

            try
            {
                // === Panggil versi baru ===
                await InteractiveProxyRunner.CaptureAndTriggerBot(bot, cancellationToken);
                // Jika sampai sini tanpa exception, anggap sukses
                successCount++;
            }
            catch (OperationCanceledException)
            {
                 // Tangkap cancel, anggap skip/fail
                 failCount++;
                 AnsiConsole.MarkupLine("[yellow]Skipped due to cancellation.[/]");
                 // Lanjut ke bot berikutnya (tidak break loop kecuali token global dicancel)
                 if (cancellationToken.IsCancellationRequested) break;
                 else continue;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error processing {bot.Name}: {ex.Message}[/]");
                failCount++;
                // Lanjut ke bot berikutnya
            }

             // Pause HANYA jika TIDAK dicancel dan bukan bot terakhir
             if (!cancellationToken.IsCancellationRequested && bot != bots.Last())
             {
                 Pause(cancellationToken);
             }
        } // End foreach

        AnsiConsole.MarkupLine($"\n[bold]Summary:[/]");
        AnsiConsole.MarkupLine($"[green]✓ Bots processed: {successCount}[/]");
        AnsiConsole.MarkupLine($"[yellow]✗ Bots skipped/failed: {failCount}[/]");
    }

     private static async Task TestLocalBot(CancellationToken cancellationToken)
    {
        // ... (Bagian awal tidak berubah) ...
        var config = BotConfig.Load(); if (config == null) return;
        var bots = config.BotsAndTools.Where(b => b.Enabled && b.IsBot).OrderBy(b => b.Name).ToList();
        if (!bots.Any()) { /*...*/ return; }
        var choices = bots.ToList(); choices.Add(BackOption);
        var selectedBot = AnsiConsole.Prompt(/*...*/
            new SelectionPrompt<BotEntry>()
                .Title("[cyan]Select bot for local test:[/]")
                .PageSize(20).WrapAround()
                .UseConverter(b => b == BackOption ? b.Name : $"{b.Name} ({b.Type})")
                .AddChoices(choices));
        if (selectedBot == BackOption) return;
        var botPath = Path.Combine("..", selectedBot.Path); if (!Directory.Exists(botPath)) { /*...*/ return; }
        await BotRunner.InstallDependencies(botPath, selectedBot.Type);
        var (executor, args) = BotRunner.GetRunCommand(botPath, selectedBot.Type); if (string.IsNullOrEmpty(executor)) { /*...*/ return; }

        AnsiConsole.MarkupLine($"[green]Running {selectedBot.Name} locally... (Press Ctrl+C to stop)[/]");
        AnsiConsole.MarkupLine("[dim]This is a test run. No remote execution.[/]\n");

        await ShellHelper.RunInteractive(executor, args, botPath, cancellationToken);
        // Exception akan ditangkap oleh ShowDebugMenu
    }
} // End class Program
