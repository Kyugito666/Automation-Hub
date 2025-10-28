using Spectre.Console;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestrator;

public static class SecretCleanup
{
    /// <summary>
    /// Hapus SEMUA GitHub Codespace Secrets yang dibuat oleh script lama
    /// </summary>
    public static async Task DeleteAllCodespaceSecrets()
    {
        AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("[cyan]   GitHub Codespace Secrets Cleanup[/]");
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

            // Step 1: List all secrets
            AnsiConsole.MarkupLine("\n[cyan]Step 1/2:[/] Listing secrets...");
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
                AnsiConsole.MarkupLine("[green]✓ No secrets found (already clean)[/]");
                return;
            }

            AnsiConsole.MarkupLine($"[yellow]Found:[/] {secretList.Secrets.Count} secrets");
            foreach (var secret in secretList.Secrets)
            {
                AnsiConsole.MarkupLine($"  [dim]- {secret.Name}[/]");
            }

            // Step 2: Delete each secret
            AnsiConsole.MarkupLine("\n[cyan]Step 2/2:[/] Deleting secrets...");
            int deleted = 0;
            int failed = 0;

            foreach (var secret in secretList.Secrets)
            {
                try
                {
                    var deleteUrl = $"https://api.github.com/repos/{owner}/{repo}/codespaces/secrets/{secret.Name}";
                    var deleteResponse = await client.DeleteAsync(deleteUrl);

                    if (deleteResponse.IsSuccessStatusCode)
                    {
                        AnsiConsole.MarkupLine($"[green]✓[/] Deleted: [dim]{secret.Name}[/]");
                        deleted++;
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] Failed: [dim]{secret.Name}[/] ({deleteResponse.StatusCode})");
                        failed++;
                    }

                    await Task.Delay(500); // Rate limit protection
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

    /// <summary>
    /// Hapus secret spesifik berdasarkan pattern (misalnya semua yang mulai dengan "BOT_")
    /// </summary>
    public static async Task DeleteSecretsByPattern(string pattern)
    {
        AnsiConsole.MarkupLine($"[cyan]Deleting secrets matching pattern: '{pattern}'[/]");

        var currentToken = TokenManager.GetCurrentToken();
        var owner = currentToken.Owner;
        var repo = currentToken.Repo;

        try
        {
            using var client = TokenManager.CreateHttpClient(currentToken);

            var listUrl = $"https://api.github.com/repos/{owner}/{repo}/codespaces/secrets";
            var listResponse = await client.GetAsync(listUrl);

            if (!listResponse.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]✗ Failed to list secrets[/]");
                return;
            }

            var json = await listResponse.Content.ReadAsStringAsync();
            var secretList = JsonSerializer.Deserialize<GitHubSecretList>(json);

            if (secretList?.Secrets == null) return;

            var matchingSecrets = secretList.Secrets
                .Where(s => s.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!matchingSecrets.Any())
            {
                AnsiConsole.MarkupLine($"[yellow]No secrets matching '{pattern}'[/]");
                return;
            }

            AnsiConsole.MarkupLine($"[yellow]Found {matchingSecrets.Count} matching secrets:[/]");
            foreach (var secret in matchingSecrets)
            {
                AnsiConsole.MarkupLine($"  [dim]- {secret.Name}[/]");
            }

            if (!AnsiConsole.Confirm($"\nDelete {matchingSecrets.Count} secrets?", false))
            {
                AnsiConsole.MarkupLine("[yellow]Cancelled[/]");
                return;
            }

            int deleted = 0;
            foreach (var secret in matchingSecrets)
            {
                try
                {
                    var deleteUrl = $"https://api.github.com/repos/{owner}/{repo}/codespaces/secrets/{secret.Name}";
                    var deleteResponse = await client.DeleteAsync(deleteUrl);

                    if (deleteResponse.IsSuccessStatusCode)
                    {
                        AnsiConsole.MarkupLine($"[green]✓[/] {secret.Name}");
                        deleted++;
                    }

                    await Task.Delay(500);
                }
                catch { }
            }

            AnsiConsole.MarkupLine($"\n[green]✓ Deleted {deleted}/{matchingSecrets.Count} secrets[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
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
