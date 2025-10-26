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
                AnsiConsole.MarkupLine("\n[bold yellow]⚠️  Ctrl+C detected. Requesting graceful shutdown...[/bold yellow]");
                _mainCts.Cancel();
            } else { 
                AnsiConsole.MarkupLine("[dim](Shutdown already in progress...)[/dim]"); 
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
            AnsiConsole.MarkupLine("\n[yellow]✓ Operation cancelled by user.[/yellow]"); 
        }
        catch (Exception ex) { 
            AnsiConsole.MarkupLine($"\n[bold red]💥 FATAL ERROR:[/bold red]"); 
            AnsiConsole.WriteException(ex); 
        }
        finally { 
            AnsiConsole.MarkupLine("\n[cyan]👋 Orchestrator shutting down gracefully...[/cyan]"); 
        }
    }

    private static void PrintMainHeader()
    {
        AnsiConsole.Clear();
        var logo = new FigletText("Automation Hub").Centered().Color(Color.Cyan1);
        AnsiConsole.Write(logo);
        
        var panel = new Panel(
            Align.Center(
                new Markup(
                    "[bold cyan]Enterprise Codespace Orchestrator[/bold cyan]\n" +
                    "[dim]Local Control • Remote Execution • Zero Maintenance[/dim]\n\n" +
                    "[yellow]⚡[/yellow] Multi-Token Rotation  [yellow]⚡[/yellow] Auto Quota Management  [yellow]⚡[/yellow] Self-Healing\n" +
                    "[dim]━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━[/dim]\n" +
                    "[magenta]Created by Kyugito666[/magenta] • [green]Powered by Spectre.Console[/green]"
                )
            )
        )
        {
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.Cyan1),
            Padding = new Padding(2, 1)
        };
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    private static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            PrintMainHeader();
            var rule = new Rule("[bold cyan]MAIN CONTROL PANEL[/bold cyan]")
            {
                Justification = Justify.Center,
                Style = Style.Parse("cyan")
            };
            AnsiConsole.Write(rule);
            AnsiConsole.WriteLine();

            var prompt = new SelectionPrompt<string>()
                .Title("[bold yellow]╭─ Select Operation:[/bold yellow]")
                .PageSize(10)
                .MoreChoicesText("[dim](Move up/down to reveal more options)[/dim]")
                .HighlightStyle(new Style(Color.Yellow, decoration: Decoration.Bold))
                .AddChoices(new[] {
                    "🚀 [REMOTE] Start/Manage Codespace Runner (Continuous Loop)",
                    "🔧 [SETUP] Token & Collaborator Management",
                    "📡 [LOCAL] Proxy Management (Run ProxySync)",
                    "🐛 [DEBUG] Test Local Bot",
                    "🔄 [SYSTEM] Refresh All Configs",
                    "🚪 Exit Application"
                });

            var choice = AnsiConsole.Prompt(prompt);
            var selection = choice.Split(']')[0].Trim('[', ' ');

            try {
                switch (selection) {
                    case "🚀 [REMOTE": 
                        await RunOrchestratorLoopAsync(cancellationToken); 
                        break;
                    case "🔧 [SETUP": 
                        await ShowSetupMenuAsync(cancellationToken); 
                        break;
                    case "📡 [LOCAL": 
                        await ShowLocalMenuAsync(cancellationToken); 
                        break;
                    case "🐛 [DEBUG": 
                        await ShowDebugMenuAsync(cancellationToken); 
                        break;
                    case "🔄 [SYSTEM": 
                        TokenManager.ReloadAllConfigs(); 
                        Pause("Press Enter to continue...", cancellationToken); 
                        break;
                    case "🚪 Exit": 
                        return;
                }
            }
            catch (OperationCanceledException) { 
                AnsiConsole.MarkupLine("\n[yellow]⚠️  Operation cancelled.[/yellow]"); 
                Pause("Press Enter...", CancellationToken.None); 
            }
            catch (Exception ex) { 
                AnsiConsole.MarkupLine($"[red]❌ Error: {ex.Message}[/red]"); 
                AnsiConsole.WriteException(ex); 
                Pause("Press Enter...", CancellationToken.None); 
            }
        }
    }

    private static async Task ShowSetupMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            AnsiConsole.Clear();
            var header = new FigletText("Setup").Centered().Color(Color.Yellow);
            AnsiConsole.Write(header);
            
            var panel = new Panel("[bold]TOKEN & COLLABORATOR CONFIGURATION[/bold]")
            {
                Border = BoxBorder.Heavy,
                BorderStyle = new Style(Color.Yellow),
                Padding = new Padding(1, 0)
            };
            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();

            var prompt = new SelectionPrompt<string>()
                .Title("[bold yellow]╭─ Setup Options:[/bold yellow]")
                .PageSize(10)
                .HighlightStyle(new Style(Color.Yellow, decoration: Decoration.Bold))
                .AddChoices(new[] { 
                    "✓ Validate Tokens & Get Usernames", 
                    "📨 Invite Collaborators", 
                    "✅ Accept Invitations", 
                    "📊 Show Token/Proxy Status", 
                    "⬅️  Back to Main Menu" 
                });

            var choice = AnsiConsole.Prompt(prompt);
            if (choice.StartsWith("⬅️")) return;

            if (choice.StartsWith("✓"))
                await CollaboratorManager.ValidateAllTokens(cancellationToken);
            else if (choice.StartsWith("📨"))
                await CollaboratorManager.InviteCollaborators(cancellationToken);
            else if (choice.StartsWith("✅"))
                await CollaboratorManager.AcceptInvitations(cancellationToken);
            else if (choice.StartsWith("📊"))
                await Task.Run(() => TokenManager.ShowStatus(), cancellationToken);
            
            Pause("Press Enter to continue...", cancellationToken);
        }
    }

    private static async Task ShowLocalMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            AnsiConsole.Clear();
            var header = new FigletText("Proxy").Centered().Color(Color.Green);
            AnsiConsole.Write(header);
            
            var panel = new Panel("[bold]LOCAL PROXY MANAGEMENT[/bold]")
            {
                Border = BoxBorder.Heavy,
                BorderStyle = new Style(Color.Green),
                Padding = new Padding(1, 0)
            };
            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();

            var prompt = new SelectionPrompt<string>()
                .Title("[bold green]╭─ Proxy Options:[/bold green]")
                .PageSize(10)
                .HighlightStyle(new Style(Color.Green, decoration: Decoration.Bold))
                .AddChoices(new[] { 
                    "🔄 Run ProxySync (Download, Test, Generate proxy.txt)", 
                    "⬅️  Back to Main Menu" 
                });

            var choice = AnsiConsole.Prompt(prompt);
            if (choice.StartsWith("⬅️")) return;
            if (choice.StartsWith("🔄")) await ProxyManager.DeployProxies(cancellationToken);
            
            Pause("Press Enter to continue...", cancellationToken);
        }
    }

    private static async Task ShowDebugMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            AnsiConsole.Clear();
            var header = new FigletText("Debug").Centered().Color(Color.Grey);
            AnsiConsole.Write(header);
            
            var panel = new Panel("[bold]DEBUG & LOCAL TESTING[/bold]")
            {
                Border = BoxBorder.Heavy,
                BorderStyle = new Style(Color.Grey),
                Padding = new Padding(1, 0)
            };
            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();

            var prompt = new SelectionPrompt<string>()
                .Title("[bold grey]╭─ Debug Options:[/bold grey]")
                .PageSize(10)
                .HighlightStyle(new Style(Color.Grey, decoration: Decoration.Bold))
                .AddChoices(new[] { 
                    "▶️  Test Local Bot (Run Interactively)", 
                    "⬇️  Update All Bots Locally", 
                    "⬅️  Back to Main Menu" 
                });

            var choice = AnsiConsole.Prompt(prompt);
            if (choice.StartsWith("⬅️")) return;

            if (choice.StartsWith("▶️"))
                await TestLocalBotAsync(cancellationToken);
            else if (choice.StartsWith("⬇️"))
            {
                await BotUpdater.UpdateAllBotsLocally();
                Pause("Press Enter...", cancellationToken);
            }
        }
    }

    private static async Task TestLocalBotAsync(CancellationToken cancellationToken) {
        var config = BotConfig.Load();
        if (config == null || !config.BotsAndTools.Any()) { 
            AnsiConsole.MarkupLine("[red]❌ No bots configured.[/red]"); 
            Pause("Press Enter...", cancellationToken); 
            return; 
        }
        
        var enabledBots = config.BotsAndTools.Where(b => b.Enabled).ToList();
        if (!enabledBots.Any()) { 
            AnsiConsole.MarkupLine("[red]❌ No enabled bots.[/red]"); 
            Pause("Press Enter...", cancellationToken); 
            return; 
        }
        
        var backOption = new BotEntry { Name = "⬅️  Back", Path = "BACK" };
        var choices = enabledBots.OrderBy(b => b.Name).ToList(); 
        choices.Add(backOption);
        
        var selectedBot = AnsiConsole.Prompt(
            new SelectionPrompt<BotEntry>()
                .Title("[cyan]╭─ Select bot to test:[/cyan]")
                .PageSize(15)
                .MoreChoicesText("[dim](Scroll for more bots)[/dim]")
                .HighlightStyle(new Style(Color.Cyan1, decoration: Decoration.Bold))
                .UseConverter(b => b.Name)
                .AddChoices(choices)
        );
        
        if (selectedBot == backOption) return;
        
        AnsiConsole.MarkupLine($"\n[cyan]⚙️  Preparing {selectedBot.Name}...[/cyan]");
        
        string projectRoot = GetProjectRoot(); 
        string botPath = Path.Combine(projectRoot, selectedBot.Path);
        
        if (!Directory.Exists(botPath)) { 
            AnsiConsole.MarkupLine($"[red]❌ Path not found: {botPath}[/red]"); 
            Pause("Press Enter...", cancellationToken); 
            return; 
        }
        
        try { 
            AnsiConsole.MarkupLine("[dim]   📦 Installing dependencies...[/dim]");
            
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
            
            AnsiConsole.MarkupLine("[green]   ✓ Dependencies installed.[/green]");
        } 
        catch (Exception ex) { 
            AnsiConsole.MarkupLine($"[red]❌ Dependency installation failed: {ex.Message}[/red]"); 
            Pause("Press Enter...", cancellationToken); 
            return; 
        }
        
        var (executor, args) = GetRunCommandLocal(botPath, selectedBot.Type);
        
        if (string.IsNullOrEmpty(executor)) { 
            AnsiConsole.MarkupLine($"[red]❌ No valid entry point found for {selectedBot.Name}[/red]"); 
            Pause("Press Enter...", cancellationToken); 
            return; 
        }
        
        var runPanel = new Panel(
            $"[green]▶️  Launching {selectedBot.Name}[/green]\n" +
            $"[dim]Command: {executor} {args}[/dim]\n" +
            $"[dim]Path: {botPath}[/dim]\n\n" +
            $"[yellow]⚠️  Press Ctrl+C here to stop the bot[/yellow]"
        )
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Green),
            Padding = new Padding(1)
        };
        
        AnsiConsole.Write(runPanel);
        
        try { 
            await ShellHelper.RunInteractive(executor, args, botPath, null, cancellationToken); 
        }
        catch (OperationCanceledException) { 
            AnsiConsole.MarkupLine("\n[yellow]✓ Bot stopped by user.[/yellow]"); 
        } 
        catch (Exception ex) { 
            AnsiConsole.MarkupLine($"\n[red]❌ Bot crashed: {ex.Message}[/red]"); 
            Pause("Press Enter...", CancellationToken.None); 
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
        
        AnsiConsole.MarkupLine($"[red]   ❌ No valid entry point found[/red]");
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
        AnsiConsole.MarkupLine($"[yellow]⚠️  Project root not detected. Using fallback: {fallbackPath}[/yellow]");
        return fallbackPath;
    }

    private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken) {
        AnsiConsole.Clear();
        
        var startPanel = new Panel(
            Align.Center(
                new Markup(
                    "[bold cyan]🚀 ORCHESTRATOR LOOP STARTING[/bold cyan]\n\n" +
                    "[dim]Mode: Continuous Monitoring[/dim]\n" +
                    "[dim]Keep-Alive Interval: 3 hours[/dim]\n" +
                    "[dim]Auto Token Rotation: Enabled[/dim]\n\n" +
                    "[yellow]Press Ctrl+C to stop gracefully[/yellow]"
                )
            )
        )
        {
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.Cyan1),
            Padding = new Padding(2, 1)
        };
        
        AnsiConsole.Write(startPanel);
        await Task.Delay(2000);
        
        while (!cancellationToken.IsCancellationRequested) {
            TokenEntry currentToken = TokenManager.GetCurrentToken(); 
            TokenState currentState = TokenManager.GetState(); 
            string? activeCodespace = currentState.ActiveCodespaceName;
            
            var cycleRule = new Rule($"[yellow]🔄 Cycle with Token #{currentState.CurrentIndex + 1} (@{currentToken.Username ?? "???"})[/yellow]")
            {
                Justification = Justify.Left,
                Style = Style.Parse("yellow")
            };
            AnsiConsole.Write(cycleRule);
            
            try { 
                AnsiConsole.MarkupLine("\n[cyan]📊 Checking billing quota...[/cyan]");
                var billingInfo = await BillingManager.GetBillingInfo(currentToken); 
                BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "???");
                
                if (!billingInfo.IsQuotaOk) { 
                    AnsiConsole.MarkupLine("\n[red]⚠️  Quota insufficient. Initiating token rotation...[/red]"); 
                    
                    if (!string.IsNullOrEmpty(activeCodespace)) { 
                        await CodespaceManager.DeleteCodespace(currentToken, activeCodespace); 
                        currentState.ActiveCodespaceName = null; 
                        TokenManager.SaveState(currentState); 
                    }
                    
                    currentToken = TokenManager.SwitchToNextToken(); 
                    await Task.Delay(5000, cancellationToken); 
                    continue; 
                }
                
                AnsiConsole.MarkupLine("\n[cyan]🔧 Ensuring codespace health...[/cyan]");
                activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken);
                
                if (currentState.ActiveCodespaceName != activeCodespace) { 
                    currentState.ActiveCodespaceName = activeCodespace; 
                    TokenManager.SaveState(currentState); 
                    
                    AnsiConsole.MarkupLine($"[green]✓ Active codespace: {activeCodespace}[/green]");
                    AnsiConsole.MarkupLine("\n[cyan]📤 Uploading configurations...[/cyan]");
                    
                    await CodespaceManager.UploadConfigs(currentToken, activeCodespace); 
                    await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace); 
                    
                    AnsiConsole.MarkupLine("[green]✓ Initial startup complete.[/green]"); 
                } 
                else { 
                    AnsiConsole.MarkupLine("[green]✓ Codespace is healthy.[/green]"); 
                }
                
                var sleepPanel = new Panel(
                    $"[dim]😴 Entering keep-alive sleep mode...[/dim]\n" +
                    $"[cyan]Duration: {KeepAliveInterval.TotalMinutes} minutes[/cyan]\n" +
                    $"[dim]Next check at: {DateTime.Now.Add(KeepAliveInterval):yyyy-MM-dd HH:mm:ss}[/dim]"
                )
                {
                    Border = BoxBorder.Rounded,
                    BorderStyle = new Style(Color.Cyan1),
                    Padding = new Padding(1)
                };
                
                AnsiConsole.Write(sleepPanel);
                await Task.Delay(KeepAliveInterval, cancellationToken);
                
                currentState = TokenManager.GetState(); 
                activeCodespace = currentState.ActiveCodespaceName; 
                
                if (string.IsNullOrEmpty(activeCodespace)) { 
                    AnsiConsole.MarkupLine("\n[yellow]⚠️  No active codespace in state. Will recreate next cycle.[/yellow]"); 
                    continue; 
                }
                
                AnsiConsole.MarkupLine("\n[cyan]🏥 Keep-Alive: Checking SSH health...[/cyan]");
                
                if (!await CodespaceManager.CheckSshHealth(currentToken, activeCodespace)) { 
                    AnsiConsole.MarkupLine("[red]❌ Keep-Alive: SSH check FAILED![/red]"); 
                    currentState.ActiveCodespaceName = null; 
                    TokenManager.SaveState(currentState); 
                    AnsiConsole.MarkupLine("[yellow]⚠️  Will recreate codespace in next cycle.[/yellow]"); 
                } 
                else { 
                    AnsiConsole.MarkupLine("[green]✓ Keep-Alive: SSH check PASSED.[/green]"); 
                }
            } 
            catch (OperationCanceledException) { 
                AnsiConsole.MarkupLine("\n[yellow]⚠️  Loop cancelled by user.[/yellow]"); 
                break; 
            } 
            catch (Exception ex) { 
                AnsiConsole.MarkupLine($"\n[bold red]💥 ERROR in orchestrator loop:[/bold red]"); 
                AnsiConsole.WriteException(ex); 
                
                var retryPanel = new Panel(
                    $"[yellow]⚠️  Error encountered. Retrying in {ErrorRetryDelay.TotalMinutes} minutes...[/yellow]"
                )
                {
                    Border = BoxBorder.Rounded,
                    BorderStyle = new Style(Color.Yellow)
                };
                
                AnsiConsole.Write(retryPanel);
                await Task.Delay(ErrorRetryDelay, cancellationToken);
            }
        }
        
        AnsiConsole.MarkupLine("\n[cyan]✓ Orchestrator loop stopped.[/cyan]");
    }

    private static void Pause(string message, CancellationToken cancellationToken) {
        var pausePanel = new Panel($"[dim]{message}[/dim]")
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Padding = new Padding(0, 0)
        };
        
        AnsiConsole.Write(pausePanel);
        
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
            AnsiConsole.MarkupLine("[yellow]⚠️  Wait cancelled.[/yellow]"); 
            throw; 
        }
    }
}
