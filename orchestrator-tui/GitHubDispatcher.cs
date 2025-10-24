using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public static class GitHubDispatcher
{
    // Dihapus: Client, _token, _owner, _repo, Initialize()
    // Semua state sekarang di-manage oleh TokenManager

    public static async Task TriggerAllBotsWorkflow()
    {
        AnsiConsole.MarkupLine("[cyan]Triggering workflow 'run-all-bots.yml' di GitHub Actions...[/]");

        bool success = false;
        for (int i = 0; i < 5; i++) // Coba retry 5x (jika ada rate limit)
        {
            try
            {
                var (_, _, owner, repo) = TokenManager.GetCurrentToken();
                if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
                {
                    AnsiConsole.MarkupLine("[red]Owner/Repo GitHub tidak dikonfigurasi di tokens.txt[/]");
                    return;
                }

                using var client = TokenManager.CreateHttpClient();
                var url = $"https://api.github.com/repos/{owner}/{repo}/actions/workflows/run-all-bots.yml/dispatches";
                
                var payload = new { @ref = "main" }; // branch default
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await client.PostAsync(url, content);
                
                if (response.IsSuccessStatusCode)
                {
                    AnsiConsole.MarkupLine("[green]✓ Workflow triggered successfully![/]");
                    AnsiConsole.MarkupLine($"[dim]Cek status: https://github.com/{owner}/{repo}/actions[/]");
                    success = true;
                    break;
                }
                
                var error = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]✗ Failed: {response.StatusCode}[/]");
                AnsiConsole.MarkupLine($"[dim]{error}[/]");

                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (TokenManager.HandleRateLimitError(new Exception("Rate limit"))) continue;
                }
                
                break; // Error non-rate-limit
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Exception: {ex.Message}[/]");
                if (TokenManager.HandleRateLimitError(ex)) continue;
                break;
            }
        }

        if (!success)
        {
            AnsiConsole.MarkupLine("[red]Gagal trigger workflow setelah beberapa kali percobaan.[/]");
        }
    }

    public static async Task TriggerSingleBot(BotEntry bot, int durationMinutes = 340)
    {
        AnsiConsole.MarkupLine($"[cyan]Triggering single bot: {bot.Name}...[/]");

        bool success = false;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                var (_, _, owner, repo) = TokenManager.GetCurrentToken();
                if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
                {
                    AnsiConsole.MarkupLine("[red]Owner/Repo GitHub tidak dikonfigurasi di tokens.txt[/]");
                    return;
                }

                using var client = TokenManager.CreateHttpClient();
                var url = $"https://api.github.com/repos/{owner}/{repo}/actions/workflows/run-single-bots.yml/dispatches";
                
                var payload = new
                {
                    @ref = "main",
                    inputs = new
                    {
                        bot_name = bot.Name,
                        bot_path = bot.Path,
                        bot_repo = bot.RepoUrl,
                        bot_type = bot.Type,
                        duration_minutes = durationMinutes.ToString()
                    }
                };

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, content);
                
                if (response.IsSuccessStatusCode)
                {
                    AnsiConsole.MarkupLine($"[green]✓ {bot.Name} triggered![/]");
                    success = true;
                    break;
                }

                var error = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]✗ Failed: {response.StatusCode}[/]");
                AnsiConsole.MarkupLine($"[dim]{error}[/]");

                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (TokenManager.HandleRateLimitError(new Exception("Rate limit"))) continue;
                }

                break;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Exception: {ex.Message}[/]");
                if (TokenManager.HandleRateLimitError(ex)) continue;
                break;
            }
        }
        
        if (!success)
        {
            AnsiConsole.MarkupLine($"[red]Gagal trigger bot {bot.Name}.[/]");
        }
    }

    public static async Task GetWorkflowRuns()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                var (_, _, owner, repo) = TokenManager.GetCurrentToken();
                if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
                {
                    AnsiConsole.MarkupLine("[red]Owner/Repo GitHub tidak dikonfigurasi di tokens.txt[/]");
                    return;
                }

                using var client = TokenManager.CreateHttpClient();
                var url = $"https://api.github.com/repos/{owner}/{repo}/actions/runs?per_page=10";
                
                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode(); // Akan throw jika error

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
                return; // Sukses
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Exception: {ex.Message}[/]");
                if (TokenManager.HandleRateLimitError(ex)) continue;
                return; // Gagal
            }
        }
    }

    // Method yang hilang, dibutuhkan oleh InteractiveProxyRunner
    public static async Task TriggerBotWithInputs(BotEntry bot, Dictionary<string, string> capturedInputs)
    {
        AnsiConsole.MarkupLine("[yellow]Mode 'WithInputs' belum diimplementasikan di CI.[/]");
        AnsiConsole.MarkupLine("[dim]Input yang di-capture (jika ada) disimpan di '.bot-inputs/'.[/]");
        AnsiConsole.MarkupLine("[dim]Menjalankan bot di remote TANPA input...[/]");
        
        // Cukup panggil TriggerSingleBot, karena CI tidak di-setup untuk menerima input JSON
        await TriggerSingleBot(bot);
    }
}

// Definisi GitHubConfig dipindah ke TokenManager.cs
// Hapus definisi duplikat dari sini.
// public class GitHubConfig { ... } 

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
