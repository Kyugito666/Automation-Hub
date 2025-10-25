using Spectre.Console;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices; // Pastikan ini ada
using System.Text.Json; // Ditambahkan
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator;

internal static class Program
{
    private static CancellationTokenSource _mainCts = new CancellationTokenSource();

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromHours(3);
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(5);

    public static async Task Main(string[] args)
    {
        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true;
            if (!_mainCts.IsCancellationRequested) {
                AnsiConsole.MarkupLine("\n[bold yellow]Ctrl+C detected. Requesting shutdown...[/]");
                _mainCts.Cancel();
            } else { AnsiConsole.MarkupLine("[grey](Shutdown already requested...)[/]"); }
        };

        try {
            TokenManager.Initialize();
            if (args.Length > 0 && args[0].ToLower() == "--run") {
                await RunOrchestratorLoopAsync(_mainCts.Token);
            } else {
                await RunInteractiveMenuAsync(_mainCts.Token);
            }
        }
        catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Operation cancelled by user.[/]"); }
        catch (Exception ex) { AnsiConsole.MarkupLine($"\n[bold red]FATAL ERROR in Main:[/]"); AnsiConsole.WriteException(ex); }
        finally { AnsiConsole.MarkupLine("\n[cyan]Orchestrator shutting down.[/]"); }
    }

    private static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[grey]Codespace Orchestrator - Local Control, Remote Execution[/]");

            var prompt = new SelectionPrompt<string>()
                    .Title("\n[bold cyan]MAIN MENU[/]")
                    .PageSize(10).WrapAround()
                    .AddChoices(new[] {
                        "1. [[REMOTE]] Start/Manage Codespace Runner (Continuous Loop)",
                        "2. [[SETUP]] Token & Collaborator Management",
                        "3. [[LOCAL]] Proxy Management (Run ProxySync)",
                        "4. [[DEBUG]] Test Local Bot",
                        "5. [[SYSTEM]] Refresh All Configs",
                        "0. Exit" });
            var choice = AnsiConsole.Prompt(prompt);
            var selection = choice.Split('.')[0];

            try {
                switch (selection) {
                    case "1": await RunOrchestratorLoopAsync(cancellationToken); break;
                    case "2": await ShowSetupMenuAsync(cancellationToken); break;
                    case "3": await ShowLocalMenuAsync(cancellationToken); break;
                    case "4": await ShowDebugMenuAsync(cancellationToken); break;
                    case "5": TokenManager.ReloadAllConfigs(); Pause("...", cancellationToken); break;
                    case "0": return;
                }
            }
            catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Operation cancelled.[/]"); Pause("...", CancellationToken.None); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]"); AnsiConsole.WriteException(ex); Pause("...", CancellationToken.None); }
        }
    }

    private static async Task ShowSetupMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
             AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Setup").Centered().Color(Color.Yellow));
             var prompt = new SelectionPrompt<string>().Title("\n[bold yellow]TOKEN & COLLABORATOR SETUP[/]").PageSize(10).WrapAround()
                    .AddChoices(new[] { "1. Validate Tokens & Get Usernames", "2. Invite Collaborators", "3. Accept Invitations", "4. Show Token/Proxy Status", "0. [[Back]]" });
             var choice = AnsiConsole.Prompt(prompt);
             var sel = choice.Split('.')[0]; if (sel == "0") return;

             // --- PERBAIKAN DI SINI: Gunakan switch statement biasa ---
             switch (sel)
             {
                 case "1":
                     await CollaboratorManager.ValidateAllTokens(cancellationToken);
                     break;
                 case "2":
                     await CollaboratorManager.InviteCollaborators(cancellationToken);
                     break;
                 case "3":
                     await CollaboratorManager.AcceptInvitations(cancellationToken);
                     break;
                 case "4":
                     // Jalankan sync di thread pool agar tidak block UI jika ada delay
                     await Task.Run(() => TokenManager.ShowStatus(), cancellationToken);
                     break;
             }
             // --- AKHIR PERBAIKAN ---

             Pause("Tekan Enter...", cancellationToken);
        }
     }

    private static async Task ShowLocalMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
             AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Local").Centered().Color(Color.Green));
             var prompt = new SelectionPrompt<string>().Title("\n[bold green]LOCAL PROXY MANAGEMENT[/]").PageSize(10).WrapAround()
                    .AddChoices(new[] { "1. Run ProxySync (Download, Test, Generate proxy.txt)", "0. [[Back]]" });
             var choice = AnsiConsole.Prompt(prompt);
             var sel = choice.Split('.')[0]; if (sel == "0") return;
             if (sel == "1") await ProxyManager.DeployProxies(cancellationToken);
             Pause("Tekan Enter...", cancellationToken);
        }
    }

    private static async Task ShowDebugMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            AnsiConsole.Clear(); AnsiConsole.Write(new FigletText("Debug").Centered().Color(Color.Grey));
            var prompt = new SelectionPrompt<string>().Title("\n[bold grey]DEBUG & LOCAL TESTING[/]").PageSize(10).WrapAround()
                   .AddChoices(new[] { "1. Test Local Bot (Run Interactively)", "2. Update All Bots Locally", "0. [[Back]]" });
            var choice = AnsiConsole.Prompt(prompt);
            var sel = choice.Split('.')[0]; if (sel == "0") return;
            switch (sel) {
                case "1": await TestLocalBotAsync(cancellationToken); break;
                case "2": await BotUpdater.UpdateAllBotsLocally(); Pause("...", cancellationToken); break;
            }
        }
    }

    private static async Task TestLocalBotAsync(CancellationToken cancellationToken) {
        var config = BotConfig.Load();
        if (config == null || !config.BotsAndTools.Any()) { /* Pesan error */ Pause("...", cancellationToken); return; }
        var enabledBots = config.BotsAndTools.Where(b => b.Enabled).ToList();
        if (!enabledBots.Any()) { /* Pesan error */ Pause("...", cancellationToken); return; }

        var backOption = new BotEntry { Name = "[[Back]]", Path = "BACK" };
        var choices = enabledBots.OrderBy(b => b.Name).ToList(); choices.Add(backOption);
        var selectedBot = AnsiConsole.Prompt(new SelectionPrompt<BotEntry>().Title("[cyan]Pilih bot:[/]").PageSize(15).WrapAround().UseConverter(b => b.Name).AddChoices(choices));
        if (selectedBot == backOption) return;

        AnsiConsole.MarkupLine($"\n[cyan]Mempersiapkan {selectedBot.Name}...[/]");
        string projectRoot = GetProjectRoot(); string botPath = Path.Combine(projectRoot, selectedBot.Path);
        if (!Directory.Exists(botPath)) { /* Pesan error */ Pause("...", cancellationToken); return; }

        try { // Instal deps
            AnsiConsole.MarkupLine("[dim]   Menginstal dependensi lokal...[/]");
            if (selectedBot.Type == "python") { /* Logika instal python venv */
                 var reqFile = Path.Combine(botPath, "requirements.txt");
                 if (File.Exists(reqFile)) {
                      var venvDir = Path.Combine(botPath, ".venv"); string pipCmd = "pip";
                      if (!Directory.Exists(venvDir)) { await ShellHelper.RunCommandAsync("python", $"-m venv .venv", botPath); }
                      var winPip = Path.Combine(venvDir, "Scripts", "pip.exe"); var linPip = Path.Combine(venvDir, "bin", "pip");
                      if (File.Exists(winPip)) pipCmd = winPip; else if (File.Exists(linPip)) pipCmd = linPip;
                      await ShellHelper.RunCommandAsync(pipCmd, $"install --no-cache-dir -r requirements.txt", botPath);
                 }
            } else if (selectedBot.Type == "javascript") { /* Logika npm install */
                 var pkgFile = Path.Combine(botPath, "package.json");
                 if (File.Exists(pkgFile)) { await ShellHelper.RunCommandAsync("npm", "install --silent --no-progress", botPath); }
            }
            AnsiConsole.MarkupLine("[green]   ✓ Dependensi lokal OK.[/]");
        } catch (Exception ex) { /* Pesan error */ Pause("...", cancellationToken); return; }

        var (executor, args) = GetRunCommandLocal(botPath, selectedBot.Type); // Cari command run
        if (string.IsNullOrEmpty(executor)) { /* Pesan error */ Pause("...", cancellationToken); return; }

        AnsiConsole.MarkupLine($"\n[green]Menjalankan {selectedBot.Name}...[/]"); /* Info command & path */
        AnsiConsole.MarkupLine("[yellow]   Tekan Ctrl+C di sini untuk stop.[/]");
        try { await ShellHelper.RunInteractive(executor, args, botPath, null, cancellationToken); } // Jalankan interaktif
        catch (OperationCanceledException) { /* Sudah ditangani */ }
        catch (Exception ex) { /* Pesan error */ Pause("...", CancellationToken.None); }
    }

     private static (string executor, string args) GetRunCommandLocal(string botPath, string type) {
         if (type == "python") {
             string pythonExe = "python"; var venvDir = Path.Combine(botPath, ".venv");
             if (Directory.Exists(venvDir)) {
                 var winPath = Path.Combine(venvDir, "Scripts", "python.exe"); var linPath = Path.Combine(venvDir, "bin", "python");
                 if (File.Exists(winPath)) pythonExe = winPath; else if (File.Exists(linPath)) pythonExe = linPath;
             }
             foreach (var entry in new[] { "run.py", "main.py", "bot.py" }) { if (File.Exists(Path.Combine(botPath, entry))) return (pythonExe, $"\"{entry}\""); }
         } else if (type == "javascript") {
             var pkgFile = Path.Combine(botPath, "package.json");
             if (File.Exists(pkgFile)) { try { var content = File.ReadAllText(pkgFile); using var doc = JsonDocument.Parse(content); if (doc.RootElement.TryGetProperty("scripts", out var s) && s.TryGetProperty("start", out _)) return ("npm", "start"); } catch {} }
             foreach (var entry in new[] { "index.js", "main.js", "bot.js" }) { if (File.Exists(Path.Combine(botPath, entry))) return ("node", $"\"{entry}\""); }
         }
         AnsiConsole.MarkupLine($"[red]   Tidak ada entry point valid ditemukan untuk {Path.GetFileName(botPath)} ({type})[/]");
         // --- PERBAIKAN DI SINI ---
         return (string.Empty, string.Empty); // Return default jika tidak ketemu
         // --- AKHIR PERBAIKAN ---
     }

    private static string GetProjectRoot() {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory); int maxDepth = 10; int currentDepth = 0;
        while (currentDir != null && currentDepth < maxDepth) {
            var configDir = Path.Combine(currentDir.FullName, "config"); var gitignore = Path.Combine(currentDir.FullName, ".gitignore");
            if (Directory.Exists(configDir) && File.Exists(gitignore)) { return currentDir.FullName; }
            currentDir = currentDir.Parent; currentDepth++;
        }
        var fallbackPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        AnsiConsole.MarkupLine($"[yellow]Warning: Tidak bisa auto-detect project root. Menggunakan fallback: {fallbackPath}[/]");
        // --- PERBAIKAN DI SINI ---
        return fallbackPath; // Return fallback jika loop selesai
         // --- AKHIR PERBAIKAN ---
    }

    private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken) {
        // ... (Kode loop utama tetap sama seperti sebelumnya, termasuk fix SaveState) ...
         AnsiConsole.MarkupLine("[bold cyan]Starting Orchestrator Loop...[/]");
        AnsiConsole.MarkupLine($"[dim]Keep-Alive check every {KeepAliveInterval.TotalMinutes} minutes.[/]");
        AnsiConsole.MarkupLine($"[dim]Error retry delay: {ErrorRetryDelay.TotalMinutes} minutes.[/]");

        while (!cancellationToken.IsCancellationRequested)
        {
            TokenEntry currentToken = TokenManager.GetCurrentToken();
            TokenState currentState = TokenManager.GetState();
            string? activeCodespace = currentState.ActiveCodespaceName;

            AnsiConsole.Write(new Rule($"[yellow]Processing Token #{currentState.CurrentIndex + 1} (@{currentToken.Username ?? "???"})[/]").LeftJustified());

            try { /* ... Cek Billing ... */
                 AnsiConsole.MarkupLine("Checking billing quota...");
                var billingInfo = await BillingManager.GetBillingInfo(currentToken);
                BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "???");

                if (!billingInfo.IsQuotaOk) {
                    AnsiConsole.MarkupLine("[red]Quota insufficient. Rotating token...[/]");
                    if (!string.IsNullOrEmpty(activeCodespace)) { await CodespaceManager.DeleteCodespace(currentToken, activeCodespace); currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState); } // Pass state
                    currentToken = TokenManager.SwitchToNextToken(); await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); continue;
                }
                /* ... Ensure Codespace ... */
                 AnsiConsole.MarkupLine("Ensuring healthy codespace...");
                activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken);
                if (currentState.ActiveCodespaceName != activeCodespace) {
                    currentState.ActiveCodespaceName = activeCodespace; TokenManager.SaveState(currentState); // Pass state
                    AnsiConsole.MarkupLine($"[green]✓ Active codespace set to: {activeCodespace}[/]");
                    AnsiConsole.MarkupLine("New/Recreated codespace detected...");
                    await CodespaceManager.UploadConfigs(currentToken, activeCodespace);
                    await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                    AnsiConsole.MarkupLine("[green]✓ Initial startup complete.[/]");
                } else { AnsiConsole.MarkupLine("[green]✓ Codespace is healthy.[/]"); }
                /* ... Keep-Alive Delay & Check ... */
                 AnsiConsole.MarkupLine($"Sleeping for Keep-Alive ({KeepAliveInterval.TotalMinutes} minutes)...");
                await Task.Delay(KeepAliveInterval, cancellationToken);
                currentState = TokenManager.GetState(); activeCodespace = currentState.ActiveCodespaceName; // Re-fetch
                if (string.IsNullOrEmpty(activeCodespace)) { AnsiConsole.MarkupLine("[yellow]Keep-Alive: No active codespace. Skipping SSH check.[/]"); continue; }
                AnsiConsole.MarkupLine("Keep-Alive: Checking SSH health...");
                if (!await CodespaceManager.CheckSshHealth(currentToken, activeCodespace)) {
                    AnsiConsole.MarkupLine("[red]Keep-Alive: SSH check failed![/]");
                    currentState.ActiveCodespaceName = null; TokenManager.SaveState(currentState); // Pass state
                    AnsiConsole.MarkupLine("[yellow]Will recreate on next cycle.[/]");
                } else { AnsiConsole.MarkupLine("[green]Keep-Alive: SSH check OK.[/]"); }
             }
            catch (OperationCanceledException) { AnsiConsole.MarkupLine("\n[yellow]Loop cancelled.[/]"); break; }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[bold red]ERROR loop:[/]"); AnsiConsole.WriteException(ex); /* Retry logic */ }
        } // End while
    }

    private static void Pause(string message, CancellationToken cancellationToken) {
        // ... (Kode Pause tetap sama) ...
        AnsiConsole.MarkupLine($"\n[grey]{message} (Ctrl+C to cancel wait)[/]");
        try { while (true) { if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(); if (Console.KeyAvailable) break; Task.Delay(50).Wait(); } while(Console.KeyAvailable) Console.ReadKey(intercept: true); }
        catch (OperationCanceledException) { AnsiConsole.MarkupLine("[yellow]Wait cancelled.[/]"); throw; }
    }
}
