using Spectre.Console;

namespace Orchestrator;

internal static class Program
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromHours(3);
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(5);

    public static async Task Main(string[] args)
    {
        try
        {
            TokenManager.Initialize();

            if (args.Length > 0 && args[0].ToLower() == "--run")
            {
                AnsiConsole.MarkupLine("[bold cyan]Starting Orchestrator in non-interactive mode...[/]");
                await RunOrchestratorLoopAsync();
            }
            else
            {
                await RunInteractiveMenuAsync();
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[bold red]FATAL ERROR:[/]");
            AnsiConsole.WriteException(ex);
        }
    }

    private static async Task RunInteractiveMenuAsync()
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[grey]Codespace Orchestrator - Remote Bot Management[/]");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold cyan]MAIN MENU[/]")
                    .PageSize(10)
                    .AddChoices(new[]
                    {
                        "1. [[REMOTE]] Start/Manage Codespace Runner",
                        "2. [[SETUP]] Token Validation",
                        "3. [[SETUP]] Invite Collaborators",
                        "4. [[SETUP]] Accept Invitations",
                        "5. [[SETUP]] Set Secrets (PRIVATEKEY + TOKEN + APILIST)",
                        "6. [[SETUP]] Full Setup (2→3→4→5)",
                        "0. Exit"
                    }));

            var sel = choice.Split('.')[0];

            try
            {
                switch (sel)
                {
                    case "1":
                        await RunOrchestratorLoopAsync();
                        break;
                    case "2":
                        await CollaboratorManager.ValidateAllTokens();
                        Pause();
                        break;
                    case "3":
                        await CollaboratorManager.InviteCollaborators();
                        Pause();
                        break;
                    case "4":
                        await CollaboratorManager.AcceptInvitations();
                        Pause();
                        break;
                    case "5":
                        await SecretManager.SetSecretsForAll();
                        Pause();
                        break;
                    case "6":
                        await RunFullSetup();
                        Pause();
                        break;
                    case "0":
                        return;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
                Pause();
            }
        }
    }

    private static async Task RunFullSetup()
    {
        AnsiConsole.MarkupLine("[bold cyan]Starting Full Setup...[/]\n");

        AnsiConsole.MarkupLine("[cyan]Step 1/4: Validating tokens...[/]");
        await CollaboratorManager.ValidateAllTokens();
        await Task.Delay(2000);

        AnsiConsole.MarkupLine("\n[cyan]Step 2/4: Inviting collaborators...[/]");
        await CollaboratorManager.InviteCollaborators();
        await Task.Delay(2000);

        AnsiConsole.MarkupLine("\n[yellow]⏸️  Wait 5-10 minutes for email notifications, then press Enter...[/]");
        Console.ReadLine();

        AnsiConsole.MarkupLine("[cyan]Step 3/4: Accepting invitations...[/]");
        await CollaboratorManager.AcceptInvitations();
        await Task.Delay(2000);

        AnsiConsole.MarkupLine("\n[cyan]Step 4/4: Setting secrets...[/]");
        await SecretManager.SetSecretsForAll();

        AnsiConsole.MarkupLine("\n[bold green]✅ Full setup complete![/]");
    }

    private static async Task RunOrchestratorLoopAsync()
    {
        AnsiConsole.MarkupLine("[bold cyan]Starting Orchestrator Loop...[/]");
        AnsiConsole.MarkupLine($"[dim]Keep-Alive check every {KeepAliveInterval.TotalMinutes} minutes.[/]");

        while (true)
        {
            TokenEntry currentToken = TokenManager.GetCurrentToken();
            TokenState currentState = TokenManager.GetState();
            string? activeCodespace = currentState.ActiveCodespaceName;

            AnsiConsole.Write(new Rule($"[yellow]Processing Token #{currentState.CurrentIndex + 1} (@{currentToken.Username ?? "???"})[/]").LeftJustified());

            try
            {
                AnsiConsole.MarkupLine("Checking billing quota...");
                var billingInfo = await BillingManager.GetBillingInfo(currentToken);
                BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "???");

                if (!billingInfo.IsQuotaOk)
                {
                    AnsiConsole.MarkupLine("[red]Quota insufficient. Rotating token...[/]");
                    if (!string.IsNullOrEmpty(activeCodespace))
                    {
                        await CodespaceManager.DeleteCodespace(currentToken, activeCodespace);
                        currentState.ActiveCodespaceName = null;
                        TokenManager.SaveState();
                    }
                    TokenManager.SwitchToNextToken();
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    continue;
                }

                AnsiConsole.MarkupLine("Ensuring healthy codespace...");
                activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken);

                if (currentState.ActiveCodespaceName != activeCodespace)
                {
                    currentState.ActiveCodespaceName = activeCodespace;
                    TokenManager.SaveState();
                    AnsiConsole.MarkupLine($"[green]✓ Active codespace set to: {activeCodespace}[/]");

                    AnsiConsole.MarkupLine("New codespace detected. Uploading configs and triggering startup...");
                    await CodespaceManager.UploadConfigs(currentToken, activeCodespace);
                    await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                    AnsiConsole.MarkupLine("[green]✓ Initial startup complete. Entering Keep-Alive mode.[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[green]✓ Codespace is healthy and running.[/]");
                }

                AnsiConsole.MarkupLine($"Sleeping for Keep-Alive interval ({KeepAliveInterval.TotalMinutes} minutes)...");
                await Task.Delay(KeepAliveInterval);

                AnsiConsole.MarkupLine("Keep-Alive: Checking SSH health...");
                if (string.IsNullOrEmpty(activeCodespace))
                {
                    AnsiConsole.MarkupLine("[yellow]Keep-Alive: No active codespace in state. Skipping SSH check.[/]");
                    currentState.ActiveCodespaceName = null;
                    TokenManager.SaveState();
                    continue;
                }

                if (!await CodespaceManager.CheckSshHealth(currentToken, activeCodespace))
                {
                    AnsiConsole.MarkupLine("[red]Keep-Alive: SSH check failed! Codespace might be stopped.[/]");
                    currentState.ActiveCodespaceName = null;
                    TokenManager.SaveState();
                    AnsiConsole.MarkupLine("[yellow]Will attempt to recreate on next cycle.[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[green]Keep-Alive: SSH check OK.[/]");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[bold red]ERROR in orchestrator loop:[/]");
                AnsiConsole.WriteException(ex);

                if (!ex.Message.Contains("Triggering token rotation"))
                {
                    AnsiConsole.MarkupLine($"[yellow]Retrying after {ErrorRetryDelay.TotalMinutes} minutes...[/]");
                    await Task.Delay(ErrorRetryDelay);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
            }
        }
    }

    private static void Pause()
    {
        AnsiConsole.MarkupLine("\n[grey]Press Enter to continue...[/]");
        Console.ReadLine();
    }
}
