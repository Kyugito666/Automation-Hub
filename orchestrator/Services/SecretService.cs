using Spectre.Console;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http; 
using System.Threading.Tasks; 
using System.Collections.Generic; 
using System.Linq; 
using Orchestrator.Core; // <-- PERBAIKAN: Ditambahkan

namespace Orchestrator.Services 
{
    public static class SecretService
    {
        public static async Task<bool> AutoCleanupBeforeCreate(TokenEntry token)
        {
            AnsiConsole.MarkupLine("\n[yellow]⚠ Pre-flight: Checking for existing secrets...[/]");
            
            using var client = TokenManager.CreateHttpClient(token);
            int totalDeleted = 0;

            totalDeleted += await DeleteSecretsFromEndpoint(client, 
                "https://api.github.com/user/codespaces/secrets", 
                "user/codespaces/secrets", 
                silent: true);

            totalDeleted += await DeleteSecretsFromEndpoint(client, 
                $"https://api.github.com/repos/{token.Owner}/{token.Repo}/codespaces/secrets", 
                $"repos/{token.Owner}/{token.Repo}/codespaces/secrets",
                silent: true);

            totalDeleted += await DeleteSecretsFromEndpoint(client, 
                $"https://api.github.com/repos/{token.Owner}/{token.Repo}/actions/secrets", 
                $"repos/{token.Owner}/{token.Repo}/actions/secrets",
                silent: true);

            if (totalDeleted > 0)
            {
                AnsiConsole.MarkupLine($"[green]✓ Cleaned {totalDeleted} old secrets[/]");
                await Task.Delay(2000);
            }
            else
            {
                AnsiConsole.MarkupLine("[dim]✓ No old secrets found[/]");
            }

            return true;
        }

        public static async Task DeleteAllSecrets()
        {
            AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════════════════════════[/]");
            AnsiConsole.MarkupLine("[red]   DELETE ALL GITHUB SECRETS (FIX 200KB ERROR)[/]");
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

            AnsiConsole.MarkupLine($"[yellow]User:[/] [cyan]@{currentToken.Username}[/]");
            AnsiConsole.MarkupLine($"[yellow]Repo:[/] [cyan]{owner}/{repo}[/]");
            AnsiConsole.MarkupLine($"[dim]Proxy: {TokenManager.MaskProxy(currentToken.Proxy)}[/]");
            AnsiConsole.MarkupLine("\n[yellow]Will delete:[/]");
            AnsiConsole.MarkupLine("[dim]  1. User Codespace Secrets (causes 200KB error)[/]");
            AnsiConsole.MarkupLine("[dim]  2. Repository Action Secrets[/]");
            AnsiConsole.MarkupLine("[dim]  3. Repository Codespace Secrets[/]");

            if (!AnsiConsole.Confirm("\n[red]⚠️  Delete ALL secrets?[/]", false))
            {
                AnsiConsole.MarkupLine("[yellow]✗ Cancelled by user.[/]");
                return;
            }

            using var client = TokenManager.CreateHttpClient(currentToken);
            int totalDeleted = 0;

            AnsiConsole.MarkupLine("\n[cyan]═══ [[1/3]] User Codespace Secrets ═══[/]");
            totalDeleted += await DeleteSecretsFromEndpoint(client, 
                "https://api.github.com/user/codespaces/secrets", 
                "user/codespaces/secrets");

            AnsiConsole.MarkupLine("\n[cyan]═══ [[2/3]] Repository Action Secrets ═══[/]");
            totalDeleted += await DeleteSecretsFromEndpoint(client, 
                $"https://api.github.com/repos/{owner}/{repo}/actions/secrets", 
                $"repos/{owner}/{repo}/actions/secrets");

            AnsiConsole.MarkupLine("\n[cyan]═══ [[3/3]] Repository Codespace Secrets ═══[/]");
            totalDeleted += await DeleteSecretsFromEndpoint(client, 
                $"https://api.github.com/repos/{owner}/{repo}/codespaces/secrets", 
                $"repos/{token.Owner}/{token.Repo}/codespaces/secrets");

            AnsiConsole.MarkupLine("\n[cyan]═══════════════════════════════════════════════════════════════[/]");
            if (totalDeleted > 0)
            {
                AnsiConsole.MarkupLine($"[green]✓ Cleanup complete! Deleted {totalDeleted} secrets total[/]");
                AnsiConsole.MarkupLine("[yellow]Now try creating codespace again (Menu 1)[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[green]✓ No secrets found (already clean)[/]");
            }
            AnsiConsole.MarkupLine("[cyan]═══════════════════════════════════════════════════════════════[/]");
        }

        private static async Task<int> DeleteSecretsFromEndpoint(HttpClient client, string listUrl, string deleteUrlBase, bool silent = false)
        {
            try
            {
                if (!silent) AnsiConsole.Markup($"[dim]Checking {listUrl.Split('/').Last()}... [/]");
                
                var listResponse = await client.GetAsync(listUrl);

                if (!listResponse.IsSuccessStatusCode)
                {
                    if (!silent) AnsiConsole.MarkupLine($"[yellow]SKIP ({listResponse.StatusCode})[/]");
                    return 0;
                }

                var json = await listResponse.Content.ReadAsStringAsync();
                var secretList = JsonSerializer.Deserialize<GitHubSecretList>(json);

                if (secretList?.Secrets == null || !secretList.Secrets.Any())
                {
                    if (!silent) AnsiConsole.MarkupLine("[dim]None found[/]");
                    return 0;
                }

                if (!silent) AnsiConsole.MarkupLine($"[yellow]{secretList.Secrets.Count} found[/]");

                int deleted = 0;
                foreach (var secret in secretList.Secrets)
                {
                    try
                    {
                        var deleteUrl = $"https://api.github.com/{deleteUrlBase}/{secret.Name}";
                        var deleteResponse = await client.DeleteAsync(deleteUrl);

                        if (deleteResponse.IsSuccessStatusCode || deleteResponse.StatusCode == System.Net.HttpStatusCode.NoContent)
                        {
                            if (!silent) AnsiConsole.MarkupLine($"  [green]✓[/] [dim]{secret.Name}[/]");
                            deleted++;
                        }
                        else
                        {
                            if (!silent) AnsiConsole.MarkupLine($"  [red]✗[/] [dim]{secret.Name} ({deleteResponse.StatusCode})[/]");
                        }

                        await Task.Delay(silent ? 100 : 300);
                    }
                    catch (Exception ex)
                    {
                        if (!silent) AnsiConsole.MarkupLine($"  [red]✗[/] [dim]{secret.Name}: {ex.Message}[/]");
                    }
                }

                return deleted;
            }
            catch (Exception ex)
            {
                if (!silent) AnsiConsole.MarkupLine($"[red]ERROR: {ex.Message}[/]");
                return 0;
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
}
