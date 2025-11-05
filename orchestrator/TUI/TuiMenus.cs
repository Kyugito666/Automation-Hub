using Spectre.Console;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Services;
using Orchestrator.Codespace;
using Orchestrator.Core;
using System.Linq; // <-- Pastikan ini ada

namespace Orchestrator.TUI
{
    internal static class TuiMenus
    {
        private static bool _isLoopActive = false;
        private static CancellationTokenSource? _loopCts = null;

        internal static async Task MainMenu()
        {
            var ghOk = await UpdateService.CheckGhCli();
            var gitOk = await UpdateService.CheckGitCli();

            if (!ghOk || !gitOk)
            {
                AnsiConsole.MarkupLine("[red]FATAL: 'gh' CLI or 'git' CLI not found in PATH.[/]");
                AnsiConsole.MarkupLine("[dim]Please install them and ensure they are accessible in your environment PATH.[/]");
                return;
            }

            try
            {
                TokenManager.ReloadAllConfigs();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine("[red]FATAL: Failed to load configuration files.[/]");
                AnsiConsole.MarkupLine("[dim]Ensure 'config/github_tokens.txt', 'config/bots_config.json', and 'config/localpath.txt' exist and are valid.[/]");
                AnsiConsole.MarkupLine($"[grey]Error: {ex.Message.EscapeMarkup()}[/]");
                return;
            }

            while (true)
            {
                AnsiConsole.Clear();
                var currentToken = TokenManager.GetCurrentToken();
                var state = TokenManager.GetState();
                var (tokenIndex, tokenTotal) = TokenManager.GetTokenIndexDisplay();
                var (proxyIndex, proxyTotal) = TokenManager.GetProxyIndexDisplay();
                bool isProxyEnabled = TokenManager.IsProxyGloballyEnabled();

                var header = new FigletText("Automation-Hub").Color(Color.Cyan);
                AnsiConsole.Write(header);

                var statusPanel = new Panel(
                    $"[cyan]Token:[/] {tokenIndex}/{tokenTotal} ([yellow]@{currentToken.Username ?? "N/A"}[/])\n" +
                    $"[cyan]Repo:[/]  {currentToken.Owner}/{currentToken.Repo}\n" +
                    $"[cyan]Proxy:[/] {proxyIndex}/{proxyTotal} ({ (isProxyEnabled ? "[green]ON" : "[red]OFF") })\n" +
                    $"[cyan]State:[/] { (string.IsNullOrEmpty(state.ActiveCodespaceName) ? "[grey]Idle" : $"[green]Active ({state.ActiveCodespaceName.EscapeMarkup()})") }"
                )
                {
                    Header = new PanelHeader("[white]Current Status[/]"),
                    Border = BoxBorder.Rounded,
                    Padding = new Padding(1, 1)
                };
                AnsiConsole.Write(statusPanel);

                string loopStatus = _isLoopActive ? "[bold red]Stop Orchestrator Loop (RUNNING)[/]" : "[bold green]Start Orchestrator Loop (STOPPED)[/]";

                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("\n[bold]Select an operation:[/]")
                        .PageSize(12)
                        .AddChoices(new[]
                        {
                            $"1. {loopStatus}",
                            "2. Manage Proxies (Test, IP Auth, Discover)",
                            "3. Manage Codespace (List, Stop, Delete)",
                            "4. Attach to Bot (tmux)",
                            "5. Local: Sync/Clone All Bot Repos",
                            "6. Local: Run ProxySync Tool (Test & Distribute)",
                            "7. Local: Validate Config Files",
                            "8. Switch GitHub Token",
                            "9. Check for Updates",
                            "10. Exit"
                        }));

                string action = choice.Split('.').First();
                AnsiConsole.Clear();
                switch (action)
                {
                    case "1": await HandleLoopToggle(); break;
                    case "2": await MenuProxyManagement(); break;
                    case "3": await MenuCodespaceManagement(); break;
                    case "4. Attach to Bot (tmux)": await MenuAttachToBot(); break; // <-- Perbaikan bug prompt
                    case "5": await MenuSyncBotRepos(); break;
                    case "6": await MenuRunProxySync(); break;
                    case "7": await MenuValidateConfigs(); break;
                    case "8": MenuSwitchToken(); break;
                    case "9": await MenuCheckForUpdates(); break;
                    case "10":
                        if (_isLoopActive)
                        {
                            AnsiConsole.MarkupLine("[yellow]Orchestrator loop is active. Stopping it first...[/]");
                            await HandleLoopToggle();
                        }
                        AnsiConsole.MarkupLine("[bold cyan]Exiting...[/]");
                        return;
                }

                if (action != "1" && action != "10")
                {
                    AnsiConsole.MarkupLine("\n[grey]Press Enter to return to menu...[/]");
                    Console.ReadLine();
                }
            }
        }

        private static async Task HandleLoopToggle()
        {
            if (_isLoopActive)
            {
                AnsiConsole.MarkupLine("[bold red]Stop signal sent.[/] Waiting for loop to finish current cycle...");
                _loopCts?.Cancel();
                _isLoopActive = false;
                // Loop akan handle sisanya
            }
            else
            {
                _isLoopActive = true;
                _loopCts = new CancellationTokenSource();
                Program.SetMainCancellationToken(_loopCts.Token); // Daftarkan token global
                try
                {
                    await TuiLoop.RunOrchestratorLoopAsync(_loopCts.Token);
                }
                finally
                {
                    _isLoopActive = false; // Pastikan flag di-reset
                    _loopCts.Dispose();
                    _loopCts = null;
                    Program.ClearMainCancellationToken(); // Hapus token global
                    AnsiConsole.MarkupLine("[bold cyan]Orchestrator loop has stopped.[/]");
                    AnsiConsole.MarkupLine("\n[grey]Press Enter to return to menu...[/]");
                    Console.ReadLine();
                }
            }
        }

        private static async Task MenuAttachToBot()
        {
            var token = TokenManager.GetCurrentToken();
            var state = TokenManager.GetState();
            if (string.IsNullOrEmpty(state.ActiveCodespaceName))
            {
                AnsiConsole.MarkupLine("[red]No active codespace stored in state. Run loop (Menu 1) first.[/]");
                return;
            }

            AnsiConsole.MarkupLine($"[cyan]Checking active codespace: {state.ActiveCodespaceName.EscapeMarkup()}[/]");
            var sessions = await CodeManager.GetTmuxSessions(token, state.ActiveCodespaceName);
            if (sessions.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No running bot sessions found in tmux.[/]");
                return;
            }

            sessions.Add("[[CANCEL]]");
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select bot session to attach:")
                    .PageSize(15)
                    .AddChoices(sessions));

            if (choice == "[[CANCEL]]") return;

            string tmuxCmd = $"tmux attach-session -t automation_hub_bots -c \"{choice.Replace("\"", "\\\"")}\"";
            string args = $"codespace ssh -c \"{state.ActiveCodespaceName}\" -- {tmuxCmd}";

            AnsiConsole.MarkupLine($"[green]Attempting to attach to [bold]{choice.EscapeMarkup()}[/bold]...[/]");
            try
            {
                // Menggunakan RunInteractiveWithFullInput untuk sesi interaktif penuh
                await ShellUtil.RunInteractiveWithFullInput("gh", args, null, token, default);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Failed to attach: {ex.Message.EscapeMarkup()}[/]");
            }
        }

        private static async Task MenuSyncBotRepos()
        {
            AnsiConsole.MarkupLine("[cyan]Starting local sync/clone of all enabled bot repos...[/]");
            string localPath = TokenManager.GetLocalPath();
            var config = BotConfig.Load();
            if (config == null)
            {
                AnsiConsole.MarkupLine("[red]Failed to load bots_config.json.[/]");
                return;
            }

            var enabledBots = config.BotsAndTools.Where(b => b.Enabled && b.Name != "ProxySync-Tool").ToList();
            if (!enabledBots.Any())
            {
                AnsiConsole.MarkupLine("[yellow]No enabled bots found in config.[/]");
                return;
            }

            await AnsiConsole.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[green]Syncing bots...[/]", new ProgressTaskSettings { MaxValue = enabledBots.Count });
                    foreach (var bot in enabledBots)
                    {
                        task.Description = $"[cyan]Syncing:[/] {bot.Name.EscapeMarkup()}";
                        string botDir = Path.Combine(localPath, bot.Path);
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(botDir) ?? localPath);
                            if (Directory.Exists(Path.Combine(botDir, ".git")))
                            {
                                await ShellUtil.RunInteractiveWithFullInput("git", "pull --rebase --autostash", botDir, null, default, false);
                            }
                            else
                            {
                                await ShellUtil.RunInteractiveWithFullInput("git", $"clone \"{bot.RepoUrl}\" \"{botDir}\"", localPath, null, default, false);
                            }
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]✗ FAILED sync for {bot.Name.EscapeMarkup()}: {ex.Message.EscapeMarkup()}[/]");
                        }
                        task.Increment(1);
                    }
                });

            AnsiConsole.MarkupLine("[green]✓ Local sync complete.[/]");
        }

        private static async Task MenuRunProxySync()
        {
            AnsiConsole.MarkupLine("[cyan]Running ProxySync tool locally...[/]");
            try
            {
                await ProxyService.RunProxySyncFullAutoAsync(Program.GetMainCancellationToken());
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ ProxySync failed: {ex.Message.EscapeMarkup()}[/]");
            }
        }

        private static Task MenuValidateConfigs()
        {
            AnsiConsole.MarkupLine("[cyan]Validating configuration files...[/]");

            // 1. TokenManager
            try
            {
                TokenManager.ReloadAllConfigs(validateOnly: true);
                AnsiConsole.MarkupLine("[green]✓ TokenManager:[/green] 'github_tokens.txt', 'localpath.txt' loaded.");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ TokenManager:[/red] {ex.Message.EscapeMarkup()}");
            }

            // 2. BotConfig
            var config = BotConfig.Load(validateOnly: true);
            if (config != null)
            {
                AnsiConsole.MarkupLine($"[green]✓ BotConfig:[/green] 'bots_config.json' loaded ({config.BotsAndTools.Count} entries).");
            }

            // 3. ProxyService (cek file python)
            if (ProxyService.ValidateProxySyncExecutable())
            {
                AnsiConsole.MarkupLine("[green]✓ ProxyService:[/green] 'proxysync/main.py' found.");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]✗ ProxyService:[/red] 'proxysync/main.py' not found.");
            }

            // 4. UpdateService (cek file python)
            if (UpdateService.ValidateUpdateScript())
            {
                AnsiConsole.MarkupLine("[green]✓ UpdateService:[/green] 'update.py' found.");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]✗ UpdateService:[/red] 'update.py' not found.");
            }

            return Task.CompletedTask;
        }

        private static void MenuSwitchToken()
        {
            var (tokenIndex, tokenTotal) = TokenManager.GetTokenIndexDisplay();
            AnsiConsole.MarkupLine($"Current token: {tokenIndex}/{tokenTotal}");
            TokenManager.SwitchToNextToken();
            var (newIndex, newTotal) = TokenManager.GetTokenIndexDisplay();
            AnsiConsole.MarkupLine($"[green]✓ Switched to token: {newIndex}/{newTotal} (@{TokenManager.GetCurrentToken().Username ?? "N/A"})[/]");
            AnsiConsole.MarkupLine("[dim]State file updated. Loop (if running) will use new token on next cycle.[/]");
        }
        
        private static async Task MenuProxyManagement()
        {
             var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("\n[bold]Proxy Management[/]")
                        .PageSize(10)
                        .AddChoices(new[]
                        {
                            "1. Test All Proxies (Lokal)",
                            "2. Run IP Authorization (Webshare)",
                            "3. Discover Download URLs (Webshare)",
                            "4. Toggle Proxy ON/OFF",
                            "5. Back to Main Menu"
                        }));
            
            string action = choice.Split('.').First();
            switch(action)
            {
                case "1":
                    await ProxyService.RunProxyTestAndSaveAsync(Program.GetMainCancellationToken());
                    break;
                case "2":
                    await ProxyService.RunIpAuthorizationOnlyAsync(Program.GetMainCancellationToken());
                    break;
                case "3":
                    await ProxyService.RunDiscoverUrlsOnlyAsync(Program.GetMainCancellationToken());
                    break;
                case "4":
                    bool newState = TokenManager.ToggleProxy();
                    AnsiConsole.MarkupLine($"[green]✓ Proxy state toggled. New state: {(newState ? "ON" : "OFF")}[/]");
                    break;
                case "5":
                    return;
            }
        }
        
        private static async Task MenuCodespaceManagement()
        {
            var token = TokenManager.GetCurrentToken();
            AnsiConsole.MarkupLine("[cyan]Fetching codespaces...[/]");
            var codespaces = await CodeActions.ListAllCodespaces(token);
            if(codespaces.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No active codespaces found for this token.[/]");
                return;
            }

            var choices = codespaces.Select(cs => $"{cs.Name} ({cs.State})").ToList();
            choices.Add("[[CANCEL]]");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select a codespace to manage:")
                    .PageSize(10)
                    .AddChoices(choices));

            if(choice == "[[CANCEL]]") return;

            string codespaceName = choice.Split(' ')[0];

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"Action for [blue]{codespaceName.EscapeMarkup()}[/]?")
                    .AddChoices(new[] { "Stop", "Delete", "Cancel" }));

            if(action == "Stop")
            {
                if(AnsiConsole.Confirm($"[yellow]Are you sure you want to STOP {codespaceName.EscapeMarkup()}?[/]"))
                {
                    await CodeActions.StopCodespace(token, codespaceName);
                }
            }
            else if (action == "Delete")
            {
                 if(AnsiConsole.Confirm($"[bold red]ARE YOU SURE you want to DELETE {codespaceName.EscapeMarkup()}?[/] This is irreversible."))
                {
                    await CodeActions.DeleteCodespace(token, codespaceName);
                }
            }
        }
        
        private static async Task MenuCheckForUpdates()
        {
            AnsiConsole.MarkupLine("[cyan]Checking for updates...[/]");
            try
            {
                await UpdateService.CheckForUpdates(manualCheck: true);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Update check failed: {ex.Message.EscapeMarkup()}[/]");
            }
        }
    }
}
