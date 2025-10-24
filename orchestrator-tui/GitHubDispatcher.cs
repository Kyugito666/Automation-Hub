using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace Orchestrator;

public static class GitHubDispatcher
{
    private static readonly HttpClient Client = new();
    
    // Config dari environment atau file
    private static string? _token;
    private static string? _owner;
    private static string? _repo;

    public static void Initialize()
    {
        // Baca dari environment variable atau file config
        _token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        _owner = Environment.GetEnvironmentVariable("GITHUB_OWNER");
        _repo = Environment.GetEnvironmentVariable("GITHUB_REPO");

        // Fallback: baca dari file
        var configPath = "../config/github_config.json";
        if (string.IsNullOrEmpty(_token) && File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<GitHubConfig>(json);
            _token = config?.Token;
            _owner = config?.Owner;
            _repo = config?.Repo;
        }

        if (string.IsNullOrEmpty(_token))
        {
            AnsiConsole.MarkupLine("[red]ERROR: GITHUB_TOKEN tidak ditemukan![/]");
            AnsiConsole.MarkupLine("[yellow]Set environment variable atau buat config/github_config.json:[/]");
            AnsiConsole.MarkupLine("[dim]{[/]");
            AnsiConsole.MarkupLine("[dim]  \"token\": \"ghp_xxxxx\",[/]");
            AnsiConsole.MarkupLine("[dim]  \"owner\": \"username\",[/]");
            AnsiConsole.MarkupLine("[dim]  \"repo\": \"automation-hub\"[/]");
            AnsiConsole.MarkupLine("[dim]}[/]");
            return;
        }

        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Automation-Hub-Orchestrator/1.0");
        Client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static async Task TriggerAllBotsWorkflow()
    {
        if (string.IsNullOrEmpty(_token))
        {
            AnsiConsole.MarkupLine("[red]GitHub tidak dikonfigurasi. Jalankan Initialize() dulu.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[cyan]Triggering workflow 'run-all-bots.yml' di GitHub Actions...[/]");

        var url = $"https://api.github.com/repos/{_owner}/{_repo}/actions/workflows/run-all-bots.yml/dispatches";
        
        var payload = new
        {
            @ref = "main",  // atau branch lain
            inputs = new { }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        try
        {
            var response = await Client.PostAsync(url, content);
            
            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine("[green]✓ Workflow triggered successfully![/]");
                AnsiConsole.MarkupLine($"[dim]Cek status: https://github.com/{_owner}/{_repo}/actions[/]");
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]✗ Failed: {response.StatusCode}[/]");
                AnsiConsole.MarkupLine($"[dim]{error}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Exception: {ex.Message}[/]");
        }
    }

    public static async Task TriggerSingleBot(string botName, string botPath, string botRepo, string botType, int durationMinutes = 340)
    {
        if (string.IsNullOrEmpty(_token))
        {
            AnsiConsole.MarkupLine("[red]GitHub tidak dikonfigurasi.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]Triggering single bot: {botName}...[/]");

        var url = $"https://api.github.com/repos/{_owner}/{_repo}/actions/workflows/run-single-bot.yml/dispatches";
        
        var payload = new
        {
            @ref = "main",
            inputs = new
            {
                bot_name = botName,
                bot_path = botPath,
                bot_repo = botRepo,
                bot_type = botType,
                duration_minutes = durationMinutes.ToString()
            }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        try
        {
            var response = await Client.PostAsync(url, content);
            
            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓ {botName} triggered![/]");
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]✗ Failed: {response.StatusCode}[/]");
                AnsiConsole.MarkupLine($"[dim]{error}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Exception: {ex.Message}[/]");
        }
    }

    public static async Task GetWorkflowRuns()
    {
        if (string.IsNullOrEmpty(_token))
        {
            AnsiConsole.MarkupLine("[red]GitHub tidak dikonfigurasi.[/]");
            return;
        }

        var url = $"https://api.github.com/repos/{_owner}/{_repo}/actions/runs?per_page=10";

        try
        {
            var response = await Client.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<WorkflowRunsResponse>(json);

            if (data?.WorkflowRuns == null || !data.WorkflowRuns.Any())
            {
                AnsiConsole.MarkupLine("[yellow]Tidak ada workflow runs.[/]");
                return;
            }

            var table = new Table().Title("Recent Workflow Runs").Border(TableBorder.Rounded);
            table.AddColumn("Status");
            table.AddColumn("Workflow");
            table.AddColumn("Started");
            table.AddColumn("Duration");

            foreach (var run in data.WorkflowRuns.Take(10))
            {
                var status = run.Status == "completed" 
                    ? (run.Conclusion == "success" ? "[green]✓[/]" : "[red]✗[/]")
                    : "[yellow]...[/]";

                var duration = run.UpdatedAt.HasValue && run.CreatedAt.HasValue
                    ? (run.UpdatedAt.Value - run.CreatedAt.Value).ToString(@"hh\:mm\:ss")
                    : "-";

                table.AddRow(
                    status,
                    run.Name ?? "Unknown",
                    run.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-",
                    duration
                );
            }

            AnsiConsole.Write(table);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Exception: {ex.Message}[/]");
        }
    }
}

public class GitHubConfig
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("owner")]
    public string? Owner { get; set; }

    [JsonPropertyName("repo")]
    public string? Repo { get; set; }
}

public class WorkflowRunsResponse
{
    [JsonPropertyName("workflow_runs")]
    public List<WorkflowRun>? WorkflowRuns { get; set; }
}

public class WorkflowRun
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
