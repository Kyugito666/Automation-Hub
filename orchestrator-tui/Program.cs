using Spectre.Console;

namespace Orchestrator;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length > 0)
        {
            await RunTask(args[0]);
            return;
        }

        await RunInteractive();
    }

    private static async Task RunTask(string task)
    {
        switch (task.ToLower())
        {
            case "--update-bots":
                AnsiConsole.MarkupLine("[cyan]Running task: Update Bots...[/]");
                await BotUpdater.UpdateAllBots();
                break;
            case "--deploy-proxies":
                AnsiConsole.MarkupLine("[cyan]Running task: Deploy Proxies...[/]");
                await ProxyManager.DeployProxies();
                break;
            default:
                AnsiConsole.MarkupLine($"[red]Error: Unknown task '{task}'[/]");
                break;
        }
    }

    private static async Task RunInteractive()
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText("Automation Hub").Centered().Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[grey]TUI Orchestrator (C# Control Plane)[/]");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("\n[bold cyan]MENU UTAMA[/]")
                    .PageSize(10)
                    .AddChoices(new[]
                    {
                        "1. (SETUP) Clone/Update Semua Bot & Tools",
                        "2. (SETUP) Deploy Proxies (Jalankan ProxySync)",
                        "3. (INFO) Tampilkan Konfigurasi Bot",
                        "4. Keluar"
                    }));

            switch (choice.Split('.')[0])
            {
                case "1":
                    await BotUpdater.UpdateAllBots();
                    break;
                case "2":
                    await ProxyManager.DeployProxies();
                    break;
                case "3":
                    BotUpdater.ShowConfig();
                    break;
                case "4":
                    AnsiConsole.MarkupLine("[green]Sampai jumpa![/]");
                    return;
            }

            AnsiConsole.MarkupLine("\n[grey]Tekan Enter untuk kembali ke menu...[/]");
            Console.ReadLine();
        }
    }
}
