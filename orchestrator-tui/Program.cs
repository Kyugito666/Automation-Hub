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
                AnsiConsole.MarkupLine("\n[bold yellow]Ctrl+C detected. Requesting shutdown...[/bold yellow]");
                _mainCts.Cancel();
            } else { 
                AnsiConsole.MarkupLine("[grey](Shutdown already requested...)[/]"); 
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
            AnsiConsole.MarkupLine("\n[yellow]Operation cancelled by user.[/yellow]"); 
        }
        catch (Exception ex) { 
            AnsiConsole.MarkupLine($"\n[bold red]FATAL ERROR in Main:[/bold red]"); 
            AnsiConsole.WriteException(ex); 
        }
        finally { 
            AnsiConsole.MarkupLine("\n[cyan]Orchestrator shutting down.[/cyan]"); 
        }
    }

    private static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[grey]Codespace Orchestrator - Local Control, Remote Execution[/grey]");

            var prompt = new SelectionPrompt<string>()
                    .Title("\n[bold cyan]MAIN MENU[/bold cyan]")
                    .PageSize(10).WrapAround()
                    .AddChoices(new[] {
                        "1. Start/Manage Codespace Runner (Continuous Loop)",
                        "2. Token & Collaborator Management",
                        "3. Proxy Management (Run ProxySync)",
                        "4. Test Local Bot",
                        "5. Refresh All Configs",
                        "0. Exit" });
            
            var choice = AnsiConsole.Prompt(prompt);
            var selection = choice.Split('.')[0];

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
                        Pause("...", cancellationToken); 
                        break;
                    case "0": 
                        return;
                }
            }
            catch (OperationCanceledException) { 
                AnsiConsole.MarkupLine("\n[yellow]Operation cancelled.[/yellow]"); 
                Pause("...", CancellationToken.None); 
            }
            catch (Exception ex) { 
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/red]"); 
                AnsiConsole.WriteException(ex); 
                Pause("...", CancellationToken.None); 
            }
        }
    }

    private static async Task ShowSetupMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
             AnsiConsole.Clear(); 
             AnsiConsole.Write(new FigletText("Setup").Centered().Color(Color.Yellow));
             
             var prompt = new SelectionPrompt<string>()
                .Title("\n[bold yellow]TOKEN & COLLABORATOR SETUP[/bold yellow]")
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
             Pause("Tekan Enter...", cancellationToken);
        }
     }

    private static async Task ShowLocalMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
             AnsiConsole.Clear(); 
             AnsiConsole.Write(new FigletText("Proxy").Centered().Color(Color.Green));
             
             var prompt = new SelectionPrompt<string>()
                .Title("\n[bold green]LOCAL PROXY MANAGEMENT[/bold green]")
                .PageSize(10).WrapAround()
                .AddChoices(new[] { 
                    "1. Run ProxySync (Download, Test, Generate proxy.txt)", 
                    "0. Back to Main Menu" 
                });
             
             var choice = AnsiConsole.Prompt(prompt);
             var sel = choice.Split('.')[0]; 
             if (sel == "0") return;
             
             if (sel == "1") await ProxyManager.DeployProxies(cancellationToken);
             
             Pause("Tekan Enter...", cancellationToken);
        }
    }

    private static async Task ShowDebugMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            AnsiConsole.Clear(); 
            AnsiConsole.Write(new FigletText("Debug").Centered().Color(Color.Grey));
            
            var prompt = new SelectionPrompt<string>()
                .Title("\n[bold grey]DEBUG & LOCAL TESTING[/bold grey]")
                .PageSize(10).WrapAround()
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
                    Pause("...", cancellationToken); 
                    break;
            }
        }
    }

    private static async Task TestLocalBotAsync(CancellationToken cancellationToken) {
        var config = BotConfig.Load();
        if (config == null || !config.BotsAndTools.Any()) { 
            AnsiConsole.MarkupLine("[red]No bots configured.[/red]"); 
            Pause("...", cancellationToken); 
            return; 
        }
        
        var enabledBots = config.BotsAndTools.Where(b => b.Enabled).ToList();
        if (!enabledBots.Any()) { 
            AnsiConsole.MarkupLine("[red]No enabled bots.[/red]"); 
            Pause("...", cancellationToken); 
            return; 
        }
        
        var backOption = new BotEntry { Name = "Back", Path = "BACK" };
        var choices = enabledBots.OrderBy(b => b.Name).ToList(); 
        choices.Add(backOption);
        
        var selectedBot = AnsiConsole.Prompt(
            new SelectionPrompt<BotEntry>()
                .Title("[cyan]Pilih bot:[/cyan]")
                .PageSize(15)
                .UseConverter(b => b.Name)
                .AddChoices(choices)
        );
        
        if (selectedBot == backOption) return;
        
        AnsiConsole.MarkupLine($"\n[cyan]Mempersiapkan {selectedBot.Name}...[/cyan]");
        
        string projectRoot = GetProjectRoot(); 
        string botPath = Path.Combine(projectRoot, selectedBot.Path);
        
        if (!Directory.Exists(botPath)) { 
            AnsiConsole.MarkupLine($"[red]Path not found: {botPath}[/red]"); 
            Pause("...", cancellationToken); 
            return; 
        }
        
        try { 
            AnsiConsole.MarkupLine("[dim]   Menginstal dependensi lokal...[/dim]");
            
            if (selectedBot.Type == "python") { 
                var reqFile = Path.Combine(botPath, "requirements.txt");
                if (File.Exists(reqFile)) {
                    var venvDir = Path.Combine(botPath, ".venv"); 
                    string pipCmd = "pip";
                    
                    if (!Directory.Exists(venvDir)) { 
                        await ShellHelper.RunCommandAsync("python", $"-m venv .venv", botPath); 
                    }
                    
                    var winPip = Path.Combine(venvDir, "Scripts", "pip.exe"); 
                    var linPip = Path.Combine(venvDir, "bin", "pip"); 
                    
                    if (File.Exists(winPip)) pipCmd = winPip; 
                    else if (File.Exists(linPip)) pipCmd = linPip;
                    
                    await ShellHelper.RunCommandAsync(pipCmd, $"install --no-cache-dir -r requirements.txt", botPath);
                }
            } 
            else if (selectedBot.Type == "javascript") { 
                var pkgFile = Path.Combine(botPath, "package.json"); 
                if (File.Exists(pkgFile)) { 
                    await ShellHelper.RunCommandAsync("npm", "install --silent --no-progress", botPath); 
                }
            } 
            
            AnsiConsole.MarkupLine("[green]   Dependensi lokal OK.[/green]");
        } 
        catch (Exception ex) { 
            AnsiConsole.MarkupLine($"[red]Dependency installation failed: {ex.Message}[/red]"); 
            Pause("...", cancellationToken); 
            return; 
        }
        
        var (executor, args) = GetRunCommandLocal(botPath, selectedBot.Type);
        
        if (string.IsNullOrEmpty(executor)) { 
            AnsiConsole.MarkupLine($"[red]No valid entry point found for {selectedBot.Name}[/red]"); 
            Pause("...", cancellationToken); 
            return; 
        }
        
        AnsiConsole.MarkupLine($"\n[green]Menjalankan {selectedBot.Name}...[/green]");
        AnsiConsole.MarkupLine("[yellow]   Tekan Ctrl+C di sini untuk stop.[/yellow]");
        
        try { 
            await ShellHelper.RunInteractive(executor, args, botPath, null, cancellationToken); 
        }
        catch (OperationCanceledException) { 
            AnsiConsole.MarkupLine("\n[yellow]Bot stopped by user.[/yellow]"); 
        } 
        catch (Exception ex) { 
            AnsiConsole.MarkupLine($"\n[red]Bot crashed: {ex.Message}[/red]"); 
            Pause("...", CancellationToken.None); 
        }
    }

    private static (string executor, string args) GetRunCommandLocal(string botPath, string type) {
        if (type == "python") {
            string pythonExe = "python"; 
            var venvDir = Path.Combine(botPath, ".venv");
            
            if (Directory.Exists(venvDir)) {
                var winPath = Path.Combine(venvDir, "Scripts", "python.exe"); 
                var linPath = Path.Combine(venvDir, "bin", "python"); 
                
                if (File.Exists(winPath)) pythonExe = winPath; 
                else if (File.Exists(linPath)) pythonExe = linPath;
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
        
        AnsiConsole.MarkupLine($"[red]   No valid entry point found[/red]");
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
        AnsiConsole.MarkupLine($"[yellow]Warning: Project root not detected. Using fallback: {fallbackPath}[/yellow]");
        return fallbackPath;
    }

    private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken) {
        AnsiConsole.MarkupLine("[bold cyan]Starting Orchestrator Loop...[/bold cyan]");
        
        while (!cancellationToken.IsCancellationRequested) {
            TokenEntry currentToken = TokenManager.GetCurrentToken(); 
            TokenState currentState = TokenManager.GetState(); 
            string? activeCodespace = currentState.ActiveCodespaceName;
            
            AnsiConsole.Write(new Rule($"[yellow]Token #{currentState.CurrentIndex + 1} (@{currentToken.Username ?? "???"})[/yellow]").LeftJustified());
            
            try { 
                AnsiConsole.MarkupLine("Checking billing..."); 
                var billingInfo = await BillingManager.GetBillingInfo(currentToken); 
                BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "???");
                
                if (!billingInfo.IsQuotaOk) { 
                    AnsiConsole.MarkupLine("[red]Quota insufficient. Rotating...[/red]"); 
                    
                    if (!string.IsNullOrEmpty(activeCodespace)) { 
                        await CodespaceManager.DeleteCodespace(currentToken, activeCodespace); 
                        currentState.ActiveCodespaceName = null; 
                        TokenManager.SaveState(currentState); 
                    }
                    
                    currentToken = TokenManager.SwitchToNextToken(); 
                    await Task.Delay(5000, cancellationToken); 
                    continue; 
                }
                
                AnsiConsole.MarkupLine("Ensuring codespace..."); 
                activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken);
                
                if (currentState.ActiveCodespaceName != activeCodespace) { 
                    currentState.ActiveCodespaceName = activeCodespace; 
                    TokenManager.SaveState(currentState); 
                    
                    AnsiConsole.MarkupLine($"[green]Active CS: {activeCodespace}[/green]");
                    AnsiConsole.MarkupLine("New/Recreated CS detected..."); 
                    
                    await CodespaceManager.UploadConfigs(currentToken, activeCodespace); 
                    await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace); 
                    
                    AnsiConsole.MarkupLine("[green]Initial startup complete.[/green]"); 
                } 
                else { 
                    AnsiConsole.MarkupLine("[green]Codespace healthy.[/green]"); 
                }
                
                AnsiConsole.MarkupLine($"Sleeping for Keep-Alive ({KeepAliveInterval.TotalMinutes} min)..."); 
                await Task.Delay(KeepAliveInterval, cancellationToken);
                
                currentState = TokenManager.GetState(); 
                activeCodespace = currentState.ActiveCodespaceName; 
                
                if (string.IsNullOrEmpty(activeCodespace)) { 
                    AnsiConsole.MarkupLine("[yellow]No active codespace in state. Will recreate next cycle.[/yellow]"); 
                    continue; 
                }
                
                AnsiConsole.MarkupLine("Keep-Alive: Checking SSH..."); 
                
                if (!await CodespaceManager.CheckSshHealth(currentToken, activeCodespace)) { 
                    AnsiConsole.MarkupLine("[red]Keep-Alive: SSH check FAILED![/red]"); 
                    currentState.ActiveCodespaceName = null; 
                    TokenManager.SaveState(currentState); 
                    AnsiConsole.MarkupLine("[yellow]Will recreate next cycle.[/yellow]"); 
                } 
                else { 
                    AnsiConsole.MarkupLine("[green]Keep-Alive: SSH check OK.[/green]"); 
                }
            } 
            catch (OperationCanceledException) { 
                AnsiConsole.MarkupLine("[yellow]Loop cancelled by user.[/yellow]"); 
                break; 
            } 
            catch (Exception ex) { 
                AnsiConsole.MarkupLine($"[bold red]ERROR loop:[/bold red]"); 
                AnsiConsole.WriteException(ex); 
                
                AnsiConsole.MarkupLine($"[yellow]Retrying in {ErrorRetryDelay.TotalMinutes} minutes...[/yellow]");
                await Task.Delay(ErrorRetryDelay, cancellationToken);
            }
        }
    }

    private static void Pause(string message, CancellationToken cancellationToken) {
        AnsiConsole.MarkupLine($"\n[grey]{message} (Ctrl+C to cancel wait)[/]");
        
        try { 
            while (true) { 
                if (cancellationToken.IsCancellationRequested) 
                    throw new OperationCanceledException(); 
                
                if (Console.KeyAvailable) break; 
                
                Task.Delay(50).Wait(); 
            } 
            
            while (Console.KeyAvailable) Console.ReadKey(intercept: true); 
        }
        catch (OperationCanceledException) { 
            AnsiConsole.MarkupLine("[yellow]Wait cancelled.[/yellow]"); 
            throw; 
        }
    }
}
