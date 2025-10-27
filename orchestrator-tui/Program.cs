using Spectre.Console;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
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
                AnsiConsole.MarkupLine("\n[red]Ctrl+C detected. Requesting shutdown...[/]");
                _mainCts.Cancel();
            } else { 
                AnsiConsole.MarkupLine("[yellow]Shutdown already requested...[/]"); 
            }
        };

        try {
            TokenManager.Initialize();
            if (args.Length > 0 && args[0].ToLower() == "--run") {
                await RunOrchestratorLoopAsync(_mainCts.Token);
            } else {
                await RunInteractiveMenuAsync(_mainCts.Token);
            }
        }
        catch (OperationCanceledException) { 
            AnsiConsole.MarkupLine("\n[yellow]Operation cancelled by user.[/]"); 
        }
        catch (Exception ex) { 
            AnsiConsole.MarkupLine("\n[red]FATAL ERROR in Main:[/]"); 
            AnsiConsole.WriteException(ex);
        }
        finally { 
            AnsiConsole.MarkupLine("\n[dim]Orchestrator shutting down.[/dim]"); 
        }
    }

    private static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[dim]Codespace Orchestrator - Local Control, Remote Execution[/]");

            var prompt = new SelectionPrompt<string>()
                    .Title("\n[bold white]MAIN MENU[/]")
                    .PageSize(10).WrapAround()
                    .AddChoices(new[] {
                        "[green]1. Start/Manage Codespace Runner (Continuous Loop)[/]",
                        "[cyan]2. Token & Collaborator Management[/]",
                        "[yellow]3. Proxy Management (Run ProxySync)[/]",
                        "[grey]4. Test Local Bot[/]",
                        "5. Refresh All Configs",
                        "0. Exit" });
            
            var choice = AnsiConsole.Prompt(prompt);
            var selection = choice.Split('.')[0].Trim('[');

            try {
                switch (selection) {
                    case "1": 
                        await RunOrchestratorLoopAsync(cancellationToken); 
                        break;
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
                        Pause("Press Enter to continue...", cancellationToken); 
                        break;
                    case "0": 
                        return;
                }
            }
            catch (OperationCanceledException) { 
                AnsiConsole.MarkupLine("\n[yellow]Operation cancelled.[/]"); 
                Pause("Press Enter to continue...", CancellationToken.None); 
            }
            catch (Exception ex) { 
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]"); 
                AnsiConsole.WriteException(ex); 
                Pause("Press Enter to continue...", CancellationToken.None); 
            }
        }
    }

    private static async Task ShowSetupMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
             AnsiConsole.Clear(); 
             AnsiConsole.Write(new FigletText("Setup").Centered().Color(Color.Yellow));
             
             var prompt = new SelectionPrompt<string>()
                .Title("\n[bold white]TOKEN & COLLABORATOR SETUP[/]")
                .PageSize(10).WrapAround()
                .AddChoices(new[] { 
                    "1. Validate Tokens & Get Usernames", 
                    "2. Invite Collaborators", 
                    "3. Accept Invitations", 
                    "4. Show Token/Proxy Status", 
                    "0. Back to Main Menu" 
                });
             
             var choice = AnsiConsole.Prompt(prompt);
             var sel = choice.Split('.')[0]; 
             if (sel == "0") return;

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
                     await Task.Run(() => TokenManager.ShowStatus(), cancellationToken);
                     break;
             }
             Pause("Press Enter to continue...", cancellationToken);
        }
     }

    private static async Task ShowLocalMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
             AnsiConsole.Clear(); 
             AnsiConsole.Write(new FigletText("Proxy").Centered().Color(Color.Green));
             
             var prompt = new SelectionPrompt<string>()
                .Title("\n[bold white]LOCAL PROXY MANAGEMENT[/]")
                .PageSize(10).WrapAround()
                .AddChoices(new[] { 
                    "1. Run ProxySync (Download, Test, Generate proxy.txt)", 
                    "0. Back to Main Menu" 
                });
             
             var choice = AnsiConsole.Prompt(prompt);
             var sel = choice.Split('.')[0]; 
             if (sel == "0") return;
             
             if (sel == "1") await ProxyManager.DeployProxies(cancellationToken);
             
             Pause("Press Enter to continue...", cancellationToken);
        }
    }

    private static async Task ShowDebugMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            AnsiConsole.Clear(); 
            AnsiConsole.Write(new FigletText("Debug").Centered().Color(Color.Grey));
            
            var prompt = new SelectionPrompt<string>()
                .Title("\n[bold white]DEBUG & LOCAL TESTING[/]")
                .PageSize(10)
                .WrapAround(true)
                .AddChoices(new[] { 
                    "1. Test Local Bot (Run Interactively)", 
                    "2. Update All Bots Locally", 
                    "0. Back to Main Menu" 
                });
            
            var choice = AnsiConsole.Prompt(prompt);
            var sel = choice.Split('.')[0]; 
            if (sel == "0") return;
            
            switch (sel) {
                case "1": 
                    await TestLocalBotAsync(cancellationToken); 
                    break;
                case "2": 
                    await BotUpdater.UpdateAllBotsLocally(); 
                    Pause("Press Enter to continue...", cancellationToken); 
                    break;
            }
        }
    }

    private static async Task TestLocalBotAsync(CancellationToken cancellationToken) {
        var config = BotConfig.Load();
        if (config == null || !config.BotsAndTools.Any()) { 
            AnsiConsole.MarkupLine("[red]No bots configured.[/]"); 
            Pause("Press Enter to continue...", cancellationToken); 
            return; 
        }
        
        var enabledBots = config.BotsAndTools.Where(b => b.Enabled).ToList();
        if (!enabledBots.Any()) { 
            AnsiConsole.MarkupLine("[yellow]No enabled bots.[/]"); 
            Pause("Press Enter to continue...", cancellationToken); 
            return; 
        }
        
        var backOption = new BotEntry { Name = "[Back]", Path = "BACK" };
        var choices = enabledBots.OrderBy(b => b.Name).ToList(); 
        choices.Add(backOption);
        
        var selectedBot = AnsiConsole.Prompt(
            new SelectionPrompt<BotEntry>()
                .Title("Select bot:")
                .PageSize(15)
                .UseConverter(b => b.Name)
                .AddChoices(choices)
        );
        
        if (selectedBot == backOption) return;
        
        AnsiConsole.MarkupLine($"\n[cyan]Preparing {selectedBot.Name}...[/]");
        
        string projectRoot = GetProjectRoot(); 
        string botPath = Path.Combine(projectRoot, selectedBot.Path);
        
        if (!Directory.Exists(botPath)) { 
            AnsiConsole.MarkupLine($"[red]Path not found: {botPath}[/]");
            AnsiConsole.MarkupLine("[yellow]Tip: Run 'Update All Bots Locally' first.[/]");
            Pause("Press Enter to continue...", cancellationToken); 
            return; 
        }
        
        try { 
            AnsiConsole.MarkupLine("[cyan]Installing dependencies locally...[/]");
            
            if (selectedBot.Type == "python") { 
                var reqFile = Path.Combine(botPath, "requirements.txt");
                if (File.Exists(reqFile)) {
                    var venvDir = Path.Combine(botPath, ".venv"); 
                    string pipCmd = "pip";
                    
                    if (!Directory.Exists(venvDir)) { 
                        AnsiConsole.MarkupLine("[dim]   Creating python venv...[/]");
                        await ShellHelper.RunCommandAsync("python", $"-m venv .venv", botPath); 
                    }
                    
                    var winPip = Path.Combine(venvDir, "Scripts", "pip.exe"); 
                    var linPip = Path.Combine(venvDir, "bin", "pip"); 
                    
                    if (File.Exists(winPip)) pipCmd = $"\"{winPip}\""; 
                    else if (File.Exists(linPip)) pipCmd = $"\"{linPip}\"";
                    
                    await ShellHelper.RunCommandAsync(pipCmd, $"install --no-cache-dir -r requirements.txt", botPath);
                }
            } 
            else if (selectedBot.Type == "javascript") { 
                var pkgFile = Path.Combine(botPath, "package.json"); 
                if (File.Exists(pkgFile)) { 
                    await ShellHelper.RunCommandAsync("npm", "install --silent --no-progress", botPath); 
                }
            } 
            
            AnsiConsole.MarkupLine("[green]   ✓ Local dependencies OK.[/]");
        } 
        catch (Exception ex) { 
            AnsiConsole.MarkupLine($"[red]   ✗ Dependency installation failed: {ex.Message}[/]"); 
            Pause("Press Enter to continue...", cancellationToken); 
            return; 
        }
        
        var (executor, args) = GetRunCommandLocal(botPath, selectedBot.Type);
        
        if (string.IsNullOrEmpty(executor)) { 
            AnsiConsole.MarkupLine($"[red]   ✗ No valid entry point found for {selectedBot.Name}[/]"); 
            Pause("Press Enter to continue...", cancellationToken); 
            return; 
        }
        
        AnsiConsole.MarkupLine($"\n[cyan]Running {selectedBot.Name}...[/]");
        AnsiConsole.MarkupLine($"[dim]   CMD: {executor} {args}[/]");
        AnsiConsole.MarkupLine("[yellow]Press Ctrl+C here to stop the bot and return to menu.[/]");
        
        try { 
            await ShellHelper.RunInteractive(executor, args, botPath, null, cancellationToken); 
        }
        catch (OperationCanceledException) { 
            AnsiConsole.MarkupLine("\n[yellow]Bot stopped by user.[/]"); 
        } 
        catch (Exception ex) { 
            AnsiConsole.MarkupLine($"\n[red]Bot crashed: {ex.Message}[/]"); 
            Pause("Press Enter to continue...", CancellationToken.None); 
        }
    }

    private static (string executor, string args) GetRunCommandLocal(string botPath, string type) {
        if (type == "python") {
            string pythonExe = "python"; 
            var venvDir = Path.Combine(botPath, ".venv");
            
            if (Directory.Exists(venvDir)) {
                var winPath = Path.Combine(venvDir, "Scripts", "python.exe"); 
                var linPath = Path.Combine(venvDir, "bin", "python"); 
                
                if (File.Exists(winPath)) pythonExe = $"\"{winPath}\""; 
                else if (File.Exists(linPath)) pythonExe = $"\"{linPath}\"";
            }
            
            foreach (var entry in new[] {"run.py", "main.py", "bot.py"}) { 
                if (File.Exists(Path.Combine(botPath, entry))) 
                    return (pythonExe, $"\"{entry}\""); 
            }
        } 
        else if (type == "javascript") {
            var pkgFile = Path.Combine(botPath, "package.json");
            
            if (File.Exists(pkgFile)) { 
                try { 
                    var content = File.ReadAllText(pkgFile); 
                    using var doc = JsonDocument.Parse(content); 
                    
                    if (doc.RootElement.TryGetProperty("scripts", out var s) && 
                        s.TryGetProperty("start", out _)) 
                        return ("npm", "start"); 
                } 
                catch { }
            }
            
            foreach (var entry in new[] {"index.js", "main.js", "bot.js"}) { 
                if (File.Exists(Path.Combine(botPath, entry))) 
                    return ("node", $"\"{entry}\""); 
            }
        }
        
        AnsiConsole.MarkupLine("[red]No valid entry point found[/]");
        return (string.Empty, string.Empty);
    }

    private static string GetProjectRoot() {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory); 
        int maxDepth = 10; 
        int currentDepth = 0;
        
        while (currentDir != null && currentDepth < maxDepth) {
            var cfgDir = Path.Combine(currentDir.FullName, "config"); 
            var gitignore = Path.Combine(currentDir.FullName, ".gitignore");
            
            if (Directory.Exists(cfgDir) && File.Exists(gitignore)) { 
                return currentDir.FullName; 
            }
            
            currentDir = currentDir.Parent; 
            currentDepth++;
        }
        
        var fallbackPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        AnsiConsole.MarkupLine($"[yellow]Warning: Project root not detected. Using fallback: {fallbackPath}[/]");
        return fallbackPath;
    }

    private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken) {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold green]Starting Orchestrator Loop...[/]");
        AnsiConsole.MarkupLine("[dim]Tekan Ctrl+C untuk kembali ke menu utama.[/dim]");
        
        while (!cancellationToken.IsCancellationRequested) {
            TokenEntry currentToken = TokenManager.GetCurrentToken(); 
            TokenState currentState = TokenManager.GetState(); 
            string? activeCodespace = currentState.ActiveCodespaceName;
            
            var username = currentToken.Username ?? "validating...";
            AnsiConsole.MarkupLine($"\n[bold white]========== Token #{currentState.CurrentIndex + 1} (@{username.EscapeMarkup()}) ==========[/]");
            
            try {
                // 0. Validasi username jika belum ada
                if (string.IsNullOrEmpty(currentToken.Username))
                {
                    AnsiConsole.MarkupLine("[cyan]Validating token username...[/]");
                    await CollaboratorManager.ValidateAllTokens(cancellationToken);
                    currentToken = TokenManager.GetCurrentToken(); // Ambil lagi info terupdate
                    if (string.IsNullOrEmpty(currentToken.Username))
                    {
                        AnsiConsole.MarkupLine("[red]Token validation failed. Rotating...[/]");
                        TokenManager.SwitchToNextToken();
                        await Task.Delay(5000, cancellationToken);
                        continue;
                    }
                }
                 
                // 1. Cek Billing
                AnsiConsole.MarkupLine("Checking billing..."); 
                var billingInfo = await BillingManager.GetBillingInfo(currentToken); 
                BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "unknown");
                
                if (!billingInfo.IsQuotaOk) { 
                    AnsiConsole.MarkupLine("[red]Quota insufficient. Rotating...[/]"); 
                    
                    if (!string.IsNullOrEmpty(activeCodespace)) { 
                        AnsiConsole.MarkupLine("[dim]Deleting old codespace...[/]");
                        await CodespaceManager.DeleteCodespace(currentToken, activeCodespace); 
                        currentState.ActiveCodespaceName = null; 
                        TokenManager.SaveState(currentState); 
                    }
                    
                    currentToken = TokenManager.SwitchToNextToken(); 
                    await Task.Delay(5000, cancellationToken); 
                    continue; 
                }
                
                // 2. Pastikan Codespace Sehat
                AnsiConsole.MarkupLine("Ensuring codespace..."); 
                string newOrExistingCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken);
                
                // 3. Jika Codespace baru atau berubah, lakukan setup
                if (currentState.ActiveCodespaceName != newOrExistingCodespace) { 
                    AnsiConsole.MarkupLine($"[yellow]Active CS changed:[/][dim] {currentState.ActiveCodespaceName ?? "None"} ->[/] [bold]{newOrExistingCodespace}[/]");
                    currentState.ActiveCodespaceName = newOrExistingCodespace; 
                    TokenManager.SaveState(currentState); 
                    
                    AnsiConsole.MarkupLine("[cyan]New/Recreated CS detected...[/]"); 
                    
                    await CodespaceManager.UploadConfigs(currentToken, newOrExistingCodespace); 
                    await CodespaceManager.TriggerStartupScript(currentToken, newOrExistingCodespace); 
                    
                    AnsiConsole.MarkupLine("[green]Initial startup complete.[/]"); 
                } 
                else { 
                    AnsiConsole.MarkupLine("[green]Codespace healthy and unchanged.[/]"); 
                }

                // Set activeCodespace untuk keep-alive check
                activeCodespace = currentState.ActiveCodespaceName;

                // 4. Tidur (Keep-Alive Interval)
                AnsiConsole.MarkupLine($"[dim]Sleeping for Keep-Alive ({KeepAliveInterval.TotalMinutes} min)...[/]"); 
                await Task.Delay(KeepAliveInterval, cancellationToken);
                
                // 5. Keep-Alive Check
                currentState = TokenManager.GetState(); // Re-load state
                activeCodespace = currentState.ActiveCodespaceName; 
                
                if (string.IsNullOrEmpty(activeCodespace)) { 
                    AnsiConsole.MarkupLine("[yellow]No active codespace in state. Will recreate next cycle.[/]"); 
                    continue; 
                }
                
                AnsiConsole.MarkupLine("Keep-Alive: Checking SSH..."); 
                
                if (!await CodespaceManager.CheckSshHealth(currentToken, activeCodespace)) { 
                    AnsiConsole.MarkupLine("[red]Keep-Alive: SSH check FAILED![/]"); 
                    currentState.ActiveCodespaceName = null; 
                    TokenManager.SaveState(currentState); 
                    AnsiConsole.MarkupLine("[yellow]Will recreate next cycle.[/]"); 
                } 
                else { 
                    // === PERBAIKAN 3: Trigger startup script sebagai keep-alive ===
                    AnsiConsole.MarkupLine("[green]Keep-Alive: SSH check OK.[/]");
                    AnsiConsole.MarkupLine("[cyan]Keep-Alive: Re-triggering startup script to ensure bots are running...[/]");
                    await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                }
            } 
            catch (OperationCanceledException) { 
                AnsiConsole.MarkupLine("[yellow]Loop cancelled by user. Returning to menu...[/]"); 
                break; 
            } 
            catch (Exception ex) { 
                AnsiConsole.MarkupLine("[red]ERROR in orchestrator loop:[/]"); 
                AnsiConsole.WriteException(ex); 
                
                // Cek jika error karena auth/rate limit, rotasi token
                if (ex.Message.Contains("401") || ex.Message.Contains("403") || ex.Message.Contains("Bad credentials"))
                {
                    AnsiConsole.MarkupLine("[red]Auth/Rate limit error detected. Forcing token rotation...[/]");
                    TokenManager.SwitchToNextToken();
                }

                AnsiConsole.MarkupLine($"[dim]Retrying loop in {ErrorRetryDelay.TotalMinutes} minutes...[/]");
                await Task.Delay(ErrorRetryDelay, cancellationToken);
            }
        }
    }

    private static void Pause(string message, CancellationToken cancellationToken) {
        AnsiConsole.Markup($"\n[dim]{message}[/]");
        
        try { 
            while (true) { 
                if (cancellationToken.IsCancellationRequested) 
                    throw new OperationCanceledException(); 
                
                if (Console.KeyAvailable) break; 
                
                Task.Delay(50).Wait(cancellationToken); 
            } 
            
            while (Console.KeyAvailable) Console.ReadKey(intercept: true); 
        }
        catch (OperationCanceledException) { 
            AnsiConsole.MarkupLine("\n[yellow]Wait cancelled.[/]"); 
            throw; 
        }
        catch(Exception)
        {
            // Handle task cancelled during delay
        }
    }
}
