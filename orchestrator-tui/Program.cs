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
                Console.WriteLine("\nCtrl+C detected. Requesting shutdown...");
                _mainCts.Cancel();
            } else { 
                Console.WriteLine("Shutdown already requested..."); 
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
            Console.WriteLine("\nOperation cancelled by user."); 
        }
        catch (Exception ex) { 
            Console.WriteLine("\nFATAL ERROR in Main:"); 
            Console.WriteLine(ex.ToString()); 
        }
        finally { 
            Console.WriteLine("\nOrchestrator shutting down."); 
        }
    }

    private static async Task RunInteractiveMenuAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            Console.WriteLine("Codespace Orchestrator - Local Control, Remote Execution");

            var prompt = new SelectionPrompt<string>()
                    .Title("\nMAIN MENU")
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
                        Pause("Press Enter to continue...", cancellationToken); 
                        break;
                    case "0": 
                        return;
                }
            }
            catch (OperationCanceledException) { 
                Console.WriteLine("\nOperation cancelled."); 
                Pause("Press Enter to continue...", CancellationToken.None); 
            }
            catch (Exception ex) { 
                Console.WriteLine($"Error: {ex.Message}"); 
                Console.WriteLine(ex.StackTrace); 
                Pause("Press Enter to continue...", CancellationToken.None); 
            }
        }
    }

    private static async Task ShowSetupMenuAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
             AnsiConsole.Clear(); 
             AnsiConsole.Write(new FigletText("Setup").Centered().Color(Color.Yellow));
             
             var prompt = new SelectionPrompt<string>()
                .Title("\nTOKEN & COLLABORATOR SETUP")
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
                .Title("\nLOCAL PROXY MANAGEMENT")
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
                .Title("\nDEBUG & LOCAL TESTING")
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
                    Pause("Press Enter to continue...", cancellationToken); 
                    break;
            }
        }
    }

    private static async Task TestLocalBotAsync(CancellationToken cancellationToken) {
        var config = BotConfig.Load();
        if (config == null || !config.BotsAndTools.Any()) { 
            Console.WriteLine("No bots configured."); 
            Pause("Press Enter to continue...", cancellationToken); 
            return; 
        }
        
        var enabledBots = config.BotsAndTools.Where(b => b.Enabled).ToList();
        if (!enabledBots.Any()) { 
            Console.WriteLine("No enabled bots."); 
            Pause("Press Enter to continue...", cancellationToken); 
            return; 
        }
        
        var backOption = new BotEntry { Name = "Back", Path = "BACK" };
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
        
        Console.WriteLine($"\nPreparing {selectedBot.Name}...");
        
        string projectRoot = GetProjectRoot(); 
        string botPath = Path.Combine(projectRoot, selectedBot.Path);
        
        if (!Directory.Exists(botPath)) { 
            Console.WriteLine($"Path not found: {botPath}"); 
            Pause("Press Enter to continue...", cancellationToken); 
            return; 
        }
        
        try { 
            Console.WriteLine("Installing dependencies locally...");
            
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
            
            Console.WriteLine("Local dependencies OK.");
        } 
        catch (Exception ex) { 
            Console.WriteLine($"Dependency installation failed: {ex.Message}"); 
            Pause("Press Enter to continue...", cancellationToken); 
            return; 
        }
        
        var (executor, args) = GetRunCommandLocal(botPath, selectedBot.Type);
        
        if (string.IsNullOrEmpty(executor)) { 
            Console.WriteLine($"No valid entry point found for {selectedBot.Name}"); 
            Pause("Press Enter to continue...", cancellationToken); 
            return; 
        }
        
        Console.WriteLine($"\nRunning {selectedBot.Name}...");
        Console.WriteLine("Press Ctrl+C here to stop.");
        
        try { 
            await ShellHelper.RunInteractive(executor, args, botPath, null, cancellationToken); 
        }
        catch (OperationCanceledException) { 
            Console.WriteLine("\nBot stopped by user."); 
        } 
        catch (Exception ex) { 
            Console.WriteLine($"\nBot crashed: {ex.Message}"); 
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
        
        Console.WriteLine("No valid entry point found");
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
        Console.WriteLine($"Warning: Project root not detected. Using fallback: {fallbackPath}");
        return fallbackPath;
    }

    private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken) {
        Console.WriteLine("Starting Orchestrator Loop...");
        
        while (!cancellationToken.IsCancellationRequested) {
            TokenEntry currentToken = TokenManager.GetCurrentToken(); 
            TokenState currentState = TokenManager.GetState(); 
            string? activeCodespace = currentState.ActiveCodespaceName;
            
            var username = currentToken.Username ?? "unknown";
            Console.WriteLine($"\n========== Token #{currentState.CurrentIndex + 1} (@{username}) ==========");
            
            try { 
                Console.WriteLine("Checking billing..."); 
                var billingInfo = await BillingManager.GetBillingInfo(currentToken); 
                BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "unknown");
                
                if (!billingInfo.IsQuotaOk) { 
                    Console.WriteLine("Quota insufficient. Rotating..."); 
                    
                    if (!string.IsNullOrEmpty(activeCodespace)) { 
                        await CodespaceManager.DeleteCodespace(currentToken, activeCodespace); 
                        currentState.ActiveCodespaceName = null; 
                        TokenManager.SaveState(currentState); 
                    }
                    
                    currentToken = TokenManager.SwitchToNextToken(); 
                    await Task.Delay(5000, cancellationToken); 
                    continue; 
                }
                
                Console.WriteLine("Ensuring codespace..."); 
                activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken);
                
                if (currentState.ActiveCodespaceName != activeCodespace) { 
                    currentState.ActiveCodespaceName = activeCodespace; 
                    TokenManager.SaveState(currentState); 
                    
                    Console.WriteLine($"Active CS: {activeCodespace}");
                    Console.WriteLine("New/Recreated CS detected..."); 
                    
                    await CodespaceManager.UploadConfigs(currentToken, activeCodespace); 
                    await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace); 
                    
                    Console.WriteLine("Initial startup complete."); 
                } 
                else { 
                    Console.WriteLine("Codespace healthy."); 
                }
                
                Console.WriteLine($"Sleeping for Keep-Alive ({KeepAliveInterval.TotalMinutes} min)..."); 
                await Task.Delay(KeepAliveInterval, cancellationToken);
                
                currentState = TokenManager.GetState(); 
                activeCodespace = currentState.ActiveCodespaceName; 
                
                if (string.IsNullOrEmpty(activeCodespace)) { 
                    Console.WriteLine("No active codespace in state. Will recreate next cycle."); 
                    continue; 
                }
                
                Console.WriteLine("Keep-Alive: Checking SSH..."); 
                
                if (!await CodespaceManager.CheckSshHealth(currentToken, activeCodespace)) { 
                    Console.WriteLine("Keep-Alive: SSH check FAILED!"); 
                    currentState.ActiveCodespaceName = null; 
                    TokenManager.SaveState(currentState); 
                    Console.WriteLine("Will recreate next cycle."); 
                } 
                else { 
                    Console.WriteLine("Keep-Alive: SSH check OK."); 
                }
            } 
            catch (OperationCanceledException) { 
                Console.WriteLine("Loop cancelled by user."); 
                break; 
            } 
            catch (Exception ex) { 
                Console.WriteLine("ERROR loop:"); 
                Console.WriteLine(ex.ToString()); 
                
                Console.WriteLine($"Retrying in {ErrorRetryDelay.TotalMinutes} minutes...");
                await Task.Delay(ErrorRetryDelay, cancellationToken);
            }
        }
    }

    private static void Pause(string message, CancellationToken cancellationToken) {
        Console.WriteLine($"\n{message}");
        
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
            Console.WriteLine("Wait cancelled."); 
            throw; 
        }
    }
}
