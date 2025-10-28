using Spectre.Console;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestrator;

public static class SecretCleanup
{
    /// <summary>
    /// Hapus SEMUA GitHub Repository Secrets (Actions secrets)
    /// INI YANG BIKIN ERROR "payload exceed 50KB"
    /// </summary>
    public static async Task DeleteAllRepositorySecrets()
    {
        AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("[cyan]   GitHub Repository Secrets Cleanup[/]");
        AnsiConsole.MarkupLine("[cyan]   (Actions Secrets - Causing 50KB error)[/]");
        AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════════════════════════[/]");

        var currentToken = TokenManager.GetCurrentToken();
        
        if (string.IsNullOrEmpty(currentToken.Username))
        {
            AnsiConsole.MarkupLine("[red]✗ Active token has no username![/]");
            AnsiConsole.MarkupLine("[yellow]→ Run Menu 2 -> Validate Tokens first.[/]");
            return;
        }

        var owner = currentToken.Owner;
        var repo = currentToken.Repo;

        AnsiConsole.MarkupLine($"[yellow]Target Repo:[/] [cyan]{owner}/{repo}[/]");
        AnsiConsole.MarkupLine($"[yellow]Token User:[/] [cyan]@{currentToken.Username}[/]");
        AnsiConsole.MarkupLine($"[dim]Proxy: {TokenManager.MaskProxy(currentToken.Proxy)}[/]");

        if (!AnsiConsole.Confirm("\n[red]⚠️  Delete ALL Repository Secrets from this repo?[/]", false))
        {
            AnsiConsole.MarkupLine("[yellow]✗ Cancelled by user.[/]");
            return;
        }

        try
        {
            using var client = TokenManager.CreateHttpClient(currentToken);

            AnsiConsole.MarkupLine("\n[cyan]Step 1/2:[/] Listing repository secrets...");
            var listUrl = $"https://api.github.com/repos/{owner}/{repo}/actions/secrets";
            var listResponse = await client.GetAsync(listUrl);

            if (!listResponse.IsSuccessStatusCode)
            {
                var error = await listResponse.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]✗ Failed to list secrets ({listResponse.StatusCode})[/]");
                AnsiConsole.MarkupLine($"[dim]{error}[/]");
                return;
            }

            var json = await listResponse.Content.ReadAsStringAsync();
            var secretList = JsonSerializer.Deserialize<GitHubSecretList>(json);

            if (secretList?.Secrets == null || !secretList.Secrets.Any())
            {
                AnsiConsole.MarkupLine("[green]✓ No repository secrets found (already clean)[/]");
                return;
            }

            AnsiConsole.MarkupLine($"[yellow]Found:[/] {secretList.Secrets.Count} repository secrets");
            foreach (var secret in secretList.Secrets)
            {
                AnsiConsole.MarkupLine($"  [dim]- {secret.Name}[/]");
            }
            AnsiConsole.MarkupLine($"[red]⚠️  These secrets are causing 'payload exceed 50KB' error![/]");

            AnsiConsole.MarkupLine("\n[cyan]Step 2/2:[/] Deleting repository secrets...");
            int deleted = 0;
            int failed = 0;

            foreach (var secret in secretList.Secrets)
            {
                try
                {
                    var deleteUrl = $"https://api.github.com/repos/{owner}/{repo}/actions/secrets/{secret.Name}";
                    var deleteResponse = await client.DeleteAsync(deleteUrl);

                    if (deleteResponse.IsSuccessStatusCode || deleteResponse.StatusCode == System.Net.HttpStatusCode.NoContent)
                    {
                        AnsiConsole.MarkupLine($"[green]✓[/] Deleted: [dim]{secret.Name}[/]");
                        deleted++;
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] Failed: [dim]{secret.Name}[/] ({deleteResponse.StatusCode})");
                        failed++;
                    }

                    await Task.Delay(500);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] Error deleting [dim]{secret.Name}[/]: {ex.Message}");
                    failed++;
                }
            }

            AnsiConsole.MarkupLine("\n[cyan]═══════════════════════════════════════════════════════════════[/]");
            AnsiConsole.MarkupLine($"[green]✓ Cleanup complete![/]");
            AnsiConsole.MarkupLine($"[dim]Deleted: {deleted} | Failed: {failed} | Total: {secretList.Secrets.Count}[/]");
            AnsiConsole.MarkupLine("\n[yellow]Now try creating codespace again (Menu 1)[/]");
            AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════════════════════════[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[red]✗ Cleanup failed: {ex.Message}[/]");
            AnsiConsole.WriteException(ex);
        }
    }

    /// <summary>
    /// Hapus SEMUA GitHub Codespace Secrets (environment variables)
    /// </summary>
    public static async Task DeleteAllCodespaceSecrets()
    {
        AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("[cyan]   GitHub Codespace Secrets Cleanup[/]");
        AnsiConsole.MarkupLine("[cyan]   (Codespace Environment Variables)[/]");
        AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════════════════════════[/]");

        var currentToken = TokenManager.GetCurrentToken();
        
        if (string.IsNullOrEmpty(currentToken.Username))
        {
            AnsiConsole.MarkupLine("[red]✗ Active token has no username![/]");
            AnsiConsole.MarkupLine("[yellow]→ Run Menu 2 -> Validate Tokens first.[/]");
            return;
        }

        var owner = currentToken.Owner;
        var repo = currentToken.Repo;

        AnsiConsole.MarkupLine($"[yellow]Target Repo:[/] [cyan]{owner}/{repo}[/]");
        AnsiConsole.MarkupLine($"[yellow]Token User:[/] [cyan]@{currentToken.Username}[/]");
        AnsiConsole.MarkupLine($"[dim]Proxy: {TokenManager.MaskProxy(currentToken.Proxy)}[/]");

        if (!AnsiConsole.Confirm("\n[red]⚠️  Delete ALL Codespace Secrets from this repo?[/]", false))
        {
            AnsiConsole.MarkupLine("[yellow]✗ Cancelled by user.[/]");
            return;
        }

        try
        {
            using var client = TokenManager.CreateHttpClient(currentToken);

            AnsiConsole.MarkupLine("\n[cyan]Step 1/2:[/] Listing codespace secrets...");
            var listUrl = $"https://api.github.com/repos/{owner}/{repo}/codespaces/secrets";
            var listResponse = await client.GetAsync(listUrl);

            if (!listResponse.IsSuccessStatusCode)
            {
                var error = await listResponse.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]✗ Failed to list secrets ({listResponse.StatusCode})[/]");
                AnsiConsole.MarkupLine($"[dim]{error}[/]");
                return;
            }

            var json = await listResponse.Content.ReadAsStringAsync();
            var secretList = JsonSerializer.Deserialize<GitHubSecretList>(json);

            if (secretList?.Secrets == null || !secretList.Secrets.Any())
            {
                AnsiConsole.MarkupLine("[green]✓ No codespace secrets found (already clean)[/]");
                return;
            }

            AnsiConsole.MarkupLine($"[yellow]Found:[/] {secretList.Secrets.Count} codespace secrets");
            foreach (var secret in secretList.Secrets)
            {
                AnsiConsole.MarkupLine($"  [dim]- {secret.Name}[/]");
            }

            AnsiConsole.MarkupLine("\n[cyan]Step 2/2:[/] Deleting codespace secrets...");
            int deleted = 0;
            int failed = 0;

            foreach (var secret in secretList.Secrets)
            {
                try
                {
                    var deleteUrl = $"https://api.github.com/repos/{owner}/{repo}/codespaces/secrets/{secret.Name}";
                    var deleteResponse = await client.DeleteAsync(deleteUrl);

                    if (deleteResponse.IsSuccessStatusCode || deleteResponse.StatusCode == System.Net.HttpStatusCode.NoContent)
                    {
                        AnsiConsole.MarkupLine($"[green]✓[/] Deleted: [dim]{secret.Name}[/]");
                        deleted++;
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] Failed: [dim]{secret.Name}[/] ({deleteResponse.StatusCode})");
                        failed++;
                    }

                    await Task.Delay(500);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] Error deleting [dim]{secret.Name}[/]: {ex.Message}");
                    failed++;
                }
            }

            AnsiConsole.MarkupLine("\n[cyan]═══════════════════════════════════════════════════════════════[/]");
            AnsiConsole.MarkupLine($"[green]✓ Cleanup complete![/]");
            AnsiConsole.MarkupLine($"[dim]Deleted: {deleted} | Failed: {failed} | Total: {secretList.Secrets.Count}[/]");
            AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════════════════════════[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[red]✗ Cleanup failed: {ex.Message}[/]");
            AnsiConsole.WriteException(ex);
        }
    }

    private class GitHubSecretList
    {
        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }

        [JsonPropertyName("secrets")]
        public List<GitHubSecret> Secrets { get; set; } = new();
    }

    private class GitHubSecret
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = string.Empty;
    }
}
