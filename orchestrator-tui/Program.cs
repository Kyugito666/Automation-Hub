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
            AnsiConsole.MarkupLine("\n[dim]Orchestrator shutting down.[/]"); 
        }
    }

    private static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[dim]Codespace Orchestrator - Local Control, Remote Execution[/]");

            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold white]MAIN MENU[/]")
                    .PageSize(10)
                    .WrapAround(true)
                    .AddChoices(new[] {
                        "1. Start/Manage Codespace Runner (Continuous Loop)",
                        "2. Token & Collaborator Management",
                        "3. Proxy Management (Run ProxySync)",
                        "4. Test Local Bot",
                        "5. Refresh All Configs",
                        "0. Exit"
                    }));

            var choice = selection[0].ToString();
            
            try {
                switch (choice) {
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
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]"); 
                AnsiConsole.WriteException(ex); 
                Pause("Press Enter to continue...", CancellationToken.None); 
            }
        }
    }

    private static async Task ShowSetupMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
             AnsiConsole.Clear(); 
             AnsiConsole.Write(new FigletText("Setup").Centered().Color(Color.Yellow));
             
             var selection = AnsiConsole.Prompt(
                 new SelectionPrompt<string>()
                     .Title("\n[bold white]TOKEN & COLLABORATOR SETUP[/]")
                     .PageSize(10)
                     .AddChoices(new[] {
                         "1. Validate Tokens & Get Usernames",
                         "2. Invite Collaborators",
                         "3. Accept Invitations",
                         "4. Show Token/Proxy Status",
                         "0. Back to Main Menu"
                     }));
             
             var sel = selection[0].ToString();
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
             
             var selection = AnsiConsole.Prompt(
                 new SelectionPrompt<string>()
                     .Title("\n[bold white]LOCAL PROXY MANAGEMENT[/]")
                     .PageSize(10)
                     .AddChoices(new[] {
                         "1. Run ProxySync (Download, Test, Generate proxy.txt)",
                         "0. Back to Main Menu"
                     }));
             
             var sel = selection[0].ToString();
             if (sel == "0") return;
             
             if (sel == "1") await ProxyManager.DeployProxies(cancellationToken);
             
             Pause("Press Enter to continue...", cancellationToken);
        }
    }
    
    private static async Task ShowDebugMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            AnsiConsole.Clear(); 
            AnsiConsole.Write(new FigletText("Debug").Centered().Color(Color.Grey));
            
            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold white]DEBUG & LOCAL TESTING[/]")
                    .PageSize(10)
                    .AddChoices(new[] {
                        "1. Test Local Bot (Run Interactively)",
                        "0. Back to Main Menu"
                    }));
            
            var sel = selection[0].ToString();
            if (sel == "0") return;
            
            if (sel == "1") await TestLocalBotAsync(cancellationToken);
        }
    }

    private static async Task TestLocalBotAsync(CancellationToken cancellationToken) {
        var config = BotConfig.Load();
        if (config == null || !config.BotsAndTools.Any()) { 
            AnsiConsole.MarkupLine("[red]No bots configured.[/]"); 
            Pause("Press Enter to continue...", cancellationToken); 
            return; 
        }
        
        var enabledBots = config.BotsAndTools.Where(b => b.Enabled && b.IsBot).ToList();
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
        
        AnsiConsole.MarkupLine($"\n[cyan]Preparing {selectedBot.Name.EscapeMarkup()}...[/]");
        
        string botPath = GetLocalBotPathForTest(selectedBot.Path);
        
        if (!Directory.Exists(botPath)) { 
            AnsiConsole.MarkupLine($"[red]Path not found: {botPath.EscapeMarkup()}[/]");
            AnsiConsole.MarkupLine("[yellow]Bot belum ada di D:\\SC\\PrivateKey atau D:\\SC\\Token[/yellow]");
            Pause("Press Enter to continue...", cancellationToken); 
            return; 
        }

        // === PERBAIKAN: HAPUS BLOK INSTALASI DEPENDENSI ===
        // Sesuai permintaan, blok 'try-catch' untuk 'npm install'
        // dan 'pip install' dihapus total.
        // === AKHIR PERBAIKAN ===
        
        var (executor, args) = GetRunCommandLocal(botPath, selectedBot.Type);
        
        if (string.IsNullOrEmpty(executor)) { 
            AnsiConsole.MarkupLine($"[red]   ✗ No valid entry point found for {selectedBot.Name.EscapeMarkup()}[/]"); 
            Pause("Press Enter to continue...", cancellationToken); 
            return; 
        }
        
        AnsiConsole.MarkupLine($"\n[cyan]Running {selectedBot.Name.EscapeMarkup()}...[/]");
        AnsiConsole.MarkupLine($"[dim]   CMD: {executor.EscapeMarkup()} {args.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine("[yellow]Press Ctrl+C to stop the bot and return to menu.[/]");
        AnsiConsole.MarkupLine("[dim]Full keyboard interaction enabled (arrow keys, enter, y/n, etc.)[/]");
        
        try { 
            await ShellHelper.RunInteractiveWithFullInput(executor, args, botPath, null, cancellationToken); 
        }
        catch (OperationCanceledException) { 
            AnsiConsole.MarkupLine("\n[yellow]Bot stopped by user.[/]"); 
        } 
        catch (Exception ex) { 
            AnsiConsole.MarkupLine($"\n[red]Bot crashed: {ex.Message.EscapeMarkup()}[/]"); 
            Pause("Press Enter to continue...", CancellationToken.None); 
        }
    }

    /// <summary>
    /// Konversi path relatif dari config ke path absolut D:\SC
    /// Contoh: "bots/privatekey/TurnAutoBot-NTE" -> "D:\SC\PrivateKey\TurnAutoBot-NTE"
    /// </summary>
    private static string GetLocalBotPathForTest(string configPath)
    {
        // Normalize path separator
        configPath = configPath.Replace('/', '\\');
        
        // Ambil nama bot (bagian terakhir)
        var botName = Path.GetFileName(configPath);
        
        // Tentukan folder target (PrivateKey atau Token)
        if (configPath.Contains("privatekey", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(@"D:\SC\PrivateKey", botName);
        }
        else if (configPath.Contains("token", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(@"D:\SC\Token", botName);
        }
        else
        {
            // Fallback jika format path tidak dikenali
            AnsiConsole.MarkupLine($"[yellow]Warning: Path format tidak dikenali: {configPath}[/yellow]");
            return Path.Combine(@"D:\SC", botName);
        }
    }

    private static (string executor, string args) GetRunCommandLocal(string botPath, string type) {
        if (type == "python") {
            string pythonExe = "python"; // Fallback ke python global
            
            // === PERBAIKAN: Cari venv yang ada ===
            var venvNames = new[] { ".venv", "venv", "myenv" };
            string? foundVenvPath = null;
            
            foreach (var venvName in venvNames)
            {
                var venvDir = Path.Combine(botPath, venvName);
                if (Directory.Exists(venvDir))
                {
                    foundVenvPath = venvDir;
                    AnsiConsole.MarkupLine($"[dim]   Found existing venv: [yellow]{venvName}[/][/]");
                    break;
                }
            }

            if (foundVenvPath != null) {
                var winPath = Path.Combine(foundVenvPath, "Scripts", "python.exe"); 
                var linPath = Path.Combine(foundVenvPath, "bin", "python"); 
                
                if (File.Exists(winPath)) {
                    pythonExe = $"\"{winPath}\""; 
                } else if (File.Exists(linPath)) {
                    pythonExe = $"\"{linPath}\"";
                } else {
                    AnsiConsole.MarkupLine($"[yellow]   Venv found but no python.exe/python. Fallback to global 'python'[/]");
                }
            } else {
                 AnsiConsole.MarkupLine("[dim]   No venv found. Using global 'python'[/]");
            }
            // === AKHIR PERBAIKAN ===
            
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
        AnsiConsole.MarkupLine($"[yellow]Warning: Project root not detected. Using fallback: {fallbackPath.EscapeMarkup()}[/]");
        return fallbackPath;
    }
    
    private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken) 
    {
        Console.WriteLine("Starting Orchestrator Loop...");
        
        const int MAX_CONSECUTIVE_ERRORS = 3;
        int consecutiveErrors = 0;
        
        while (!cancellationToken.IsCancellationRequested) 
        {
            TokenEntry currentToken = TokenManager.GetCurrentToken(); 
            TokenState currentState = TokenManager.GetState(); 
            string? activeCodespace = currentState.ActiveCodespaceName;
            
            var username = currentToken.Username ?? "unknown";
            Console.WriteLine($"\n========== Token #{currentState.CurrentIndex + 1} (@{username}) ==========");
            
            try 
            { 
                Console.WriteLine("Checking billing..."); 
                var billingInfo = await BillingManager.GetBillingInfo(currentToken); 
                BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "unknown");
                
                if (!billingInfo.IsQuotaOk) 
                { 
                    Console.WriteLine("Quota insufficient. Rotating..."); 
                    
                    if (!string.IsNullOrEmpty(activeCodespace)) 
                    { 
                        await CodespaceManager.DeleteCodespace(currentToken, activeCodespace); 
                        currentState.ActiveCodespaceName = null; 
                        TokenManager.SaveState(currentState); 
                    }
                    
                    currentToken = TokenManager.SwitchToNextToken(); 
                    await Task.Delay(5000, cancellationToken); 
                    consecutiveErrors = 0;
                    continue; 
                }
                
                Console.WriteLine("Ensuring codespace..."); 
                activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken);
                
                bool isNewCodespace = currentState.ActiveCodespaceName != activeCodespace;
                
                if (isNewCodespace) 
                { 
                    currentState.ActiveCodespaceName = activeCodespace; 
                    TokenManager.SaveState(currentState); 
                    
                    Console.WriteLine($"Active CS: {activeCodespace}");
                    Console.WriteLine("New/Recreated CS detected..."); 
                    
                    bool uploadSuccess = false;
                    for (int uploadAttempt = 1; uploadAttempt <= 3; uploadAttempt++)
                    {
                        try
                        {
                            await CodespaceManager.UploadConfigs(currentToken, activeCodespace);
                            uploadSuccess = true;
                            break;
                        }
                        catch (Exception uploadEx)
                        {
                            AnsiConsole.MarkupLine($"[red]Upload attempt {uploadAttempt}/3 failed: {uploadEx.Message.EscapeMarkup()}[/]");
                            if (uploadAttempt < 3)
                            {
                                AnsiConsole.MarkupLine("[yellow]Retrying in 10 seconds...[/]");
                                await Task.Delay(10000, cancellationToken);
                            }
                        }
                    }
                    
                    if (!uploadSuccess)
                    {
                        throw new Exception("Failed to upload configs after 3 attempts");
                    }
                    
                    bool startupSuccess = false;
                    for (int startupAttempt = 1; startupAttempt <= 3; startupAttempt++)
                    {
                        try
                        {
                            await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                            startupSuccess = true;
                            break;
                        }
                        catch (Exception startupEx)
                        {
                            AnsiConsole.MarkupLine($"[red]Startup attempt {startupAttempt}/3 failed: {startupEx.Message.EscapeMarkup()}[/]");
                            if (startupAttempt < 3)
                            {
                                AnsiConsole.MarkupLine("[yellow]Retrying in 15 seconds...[/]");
                                await Task.Delay(15000, cancellationToken);
                            }
                        }
                    }
                    
                    if (!startupSuccess)
                    {
                        throw new Exception("Failed to trigger startup script after 3 attempts");
                    }
                    
                    Console.WriteLine("Initial startup complete."); 
                } 
                else 
                { 
                    Console.WriteLine("Codespace healthy (reusing existing)."); 
                }
                
                consecutiveErrors = 0;
                
                Console.WriteLine($"Sleeping for Keep-Alive ({KeepAliveInterval.TotalMinutes} min)..."); 
                await Task.Delay(KeepAliveInterval, cancellationToken);
                
                currentState = TokenManager.GetState(); 
                activeCodespace = currentState.ActiveCodespaceName; 
                
                if (string.IsNullOrEmpty(activeCodespace)) 
                { 
                    Console.WriteLine("No active codespace in state. Will recreate next cycle."); 
                    continue; 
                }
                
                Console.WriteLine("Keep-Alive: Checking SSH..."); 
                
                if (!await CodespaceManager.CheckSshHealthWithRetry(currentToken, activeCodespace)) 
                { 
                    Console.WriteLine("Keep-Alive: SSH check FAILED!"); 
                    currentState.ActiveCodespaceName = null; 
                    TokenManager.SaveState(currentState); 
                    Console.WriteLine("Will recreate next cycle."); 
                } 
                else 
                { 
                    Console.WriteLine("Keep-Alive: SSH check OK.");
                    
                    try
                    {
                        Console.WriteLine("Keep-Alive: Re-triggering startup script...");
                        await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Keep-alive startup trigger failed: {ex.Message}");
                    }
                }
            } 
            catch (OperationCanceledException) 
            { 
                Console.WriteLine("Loop cancelled by user."); 
                break; 
            } 
            catch (Exception ex) 
            { 
                consecutiveErrors++;
                Console.WriteLine("ERROR loop:"); 
                Console.WriteLine(ex.ToString());
                
                if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
                {
                    AnsiConsole.MarkupLine($"[red]CRITICAL: {MAX_CONSECUTIVE_ERRORS} consecutive errors detected![/]");
                    AnsiConsole.MarkupLine("[yellow]Attempting token rotation and full reset...[/]");
                    
                    if (!string.IsNullOrEmpty(currentState.ActiveCodespaceName))
                    {
                        try
                        {
                            await CodespaceManager.DeleteCodespace(currentToken, currentState.ActiveCodespaceName);
                        }
                        catch { }
                    }
                    
                    currentState.ActiveCodespaceName = null;
                    TokenManager.SaveState(currentState);
                    
                    currentToken = TokenManager.SwitchToNextToken();
                    consecutiveErrors = 0;
                    
                    AnsiConsole.MarkupLine("[cyan]Waiting 30 seconds before retry with new token...[/]");
                    await Task.Delay(30000, cancellationToken);
                }
                else
                {
                    Console.WriteLine($"Retrying in {ErrorRetryDelay.TotalMinutes} minutes... (Error {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS})");
                    await Task.Delay(ErrorRetryDelay, cancellationToken);
                }
            }
        }
    }

    private static void Pause(string message, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;
        
        Console.WriteLine();
        Console.Write(message);
        
        try
        {
            while (!Console.KeyAvailable)
            {
                if (cancellationToken.IsCancellationRequested) return;
                System.Threading.Thread.Sleep(100);
            }
            Console.ReadKey(true);
        }
        catch (InvalidOperationException)
        {
            System.Threading.Thread.Sleep(2000);
        }
    }
}
