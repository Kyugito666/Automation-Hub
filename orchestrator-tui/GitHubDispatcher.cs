using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public static class GitHubDispatcher
{
    public static async Task TriggerAllBotsWorkflow()
    {
        AnsiConsole.MarkupLine("[cyan]Triggering workflow 'run-all-bots.yml' di GitHub Actions...[/]");

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
                var url = $"https://api.github.com/repos/{owner}/{repo}/actions/workflows/run-all-bots.yml/dispatches";
                
                var payload = new { @ref = "main" };
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
            AnsiConsole.MarkupLine("[red]Gagal trigger workflow setelah beberapa kali percobaan.[/]");
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
                response.EnsureSuccessStatusCode();

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
