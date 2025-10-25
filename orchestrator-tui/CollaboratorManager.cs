using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public static class CollaboratorManager
{
    private const int MAX_PROXY_RETRY = 3;

    public static async Task ValidateAllTokens()
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 1. Validasi Token & Username ---[/]");
        var tokens = TokenManager.GetAllTokenEntries();
        if (!tokens.Any()) 
        {
             AnsiConsole.MarkupLine("[yellow]⚠️  Tidak ada token.[/]");
             return;
        }

        var cache = TokenManager.GetUsernameCache();
        int newUsers = 0;
        bool cacheUpdated = false;

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(),
            })
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Validasi...[/]", new ProgressTaskSettings { MaxValue = tokens.Count });

                foreach (var entry in tokens)
                {
                    var tokenDisplay = TokenManager.MaskToken(entry.Token);
                    task.Description = $"[green]Cek:[/] {tokenDisplay}";

                    if (!string.IsNullOrEmpty(entry.Username))
                    {
                        task.Increment(1);
                        continue;
                    }
                    
                     if (cache.TryGetValue(entry.Token, out var cachedUsername))
                     {
                        entry.Username = cachedUsername;
                        task.Increment(1);
                        continue;
                     }

                    bool success = false;
                    for (int retry = 0; retry < MAX_PROXY_RETRY && !success; retry++)
                    {
                        try
                        {
                            using var client = TokenManager.CreateHttpClient(entry);
                            var response = await client.GetAsync("https://api.github.com/user");

                            if (response.IsSuccessStatusCode)
                            {
                                var user = await response.Content.ReadFromJsonAsync<GitHubUser>();
                                if (user?.Login != null)
                                {
                                    AnsiConsole.MarkupLine($"[green]✓[/] {tokenDisplay} → [yellow]@{user.Login}[/]");
                                    entry.Username = user.Login;
                                    cache[entry.Token] = user.Login;
                                    newUsers++;
                                    cacheUpdated = true;
                                    success = true;
                                } else {
                                     AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay} [red]INVALID[/]");
                                     break;
                                }
                            }
                            else if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                            {
                                AnsiConsole.MarkupLine($"[red]✗[/] Proxy 407. Retry {retry + 1}/{MAX_PROXY_RETRY}");
                                if (!TokenManager.RotateProxyForToken(entry))
                                {
                                    break;
                                }
                                await Task.Delay(1000);
                            }
                            else
                            {
                                var error = await response.Content.ReadAsStringAsync();
                                AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay}: {response.StatusCode}");
                                break;
                            }
                        }
                        catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                        {
                             AnsiConsole.MarkupLine($"[red]✗[/] Proxy 407. Retry {retry + 1}/{MAX_PROXY_RETRY}");
                             if (!TokenManager.RotateProxyForToken(entry))
                             {
                                 break;
                             }
                             await Task.Delay(1000);
                        }
                        catch (HttpRequestException httpEx) when (httpEx.InnerException is System.Net.Sockets.SocketException)
                        {
                            AnsiConsole.MarkupLine($"[red]✗[/] Proxy timeout. Retry {retry + 1}/{MAX_PROXY_RETRY}");
                            if (!TokenManager.RotateProxyForToken(entry))
                            {
                                break;
                            }
                            await Task.Delay(1000);
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay}: {ex.Message.Split('\n').FirstOrDefault()}");
                            break;
                        }
                    }

                    if (!success)
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay} GAGAL ({MAX_PROXY_RETRY} retry)");
                    }

                    task.Increment(1);
                    await Task.Delay(200);
                }
            });

        if (cacheUpdated)
        {
            TokenManager.SaveTokenCache(cache);
        }
        
        AnsiConsole.MarkupLine($"[green]✓ Selesai. {newUsers} username baru.[/]");
    }

    public static async Task InviteCollaborators()
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 2. Undang Kolaborator ---[/]");
        var tokens = TokenManager.GetAllTokenEntries();
        if (!tokens.Any()) return;

        var owner = tokens.First().Owner;
        var repo = tokens.First().Repo;
        
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            AnsiConsole.MarkupLine("[red]❌ Owner/Repo tidak di-set![/]");
            return;
        }

        var mainTokenEntry = tokens.FirstOrDefault(t => t.Username?.Equals(owner, StringComparison.OrdinalIgnoreCase) ?? false);
        if (mainTokenEntry == null)
        {
            mainTokenEntry = tokens.FirstOrDefault();
             if (mainTokenEntry == null) {
                  AnsiConsole.MarkupLine($"[red]❌ Tidak ada token![/]");
                  return;
             }
            AnsiConsole.MarkupLine($"[yellow]⚠️  Token owner '{owner}' not found. Using token 1 (@{mainTokenEntry.Username ?? "???"})...[/]");
        } else {
             AnsiConsole.MarkupLine($"[dim]Using [yellow]@{owner}[/] token...[/]");
        }
        
        var usersToInvite = tokens
            .Where(t => t.Username != null && !t.Username.Equals(owner, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Username!)
            .Distinct()
            .ToList();
            
        if (!usersToInvite.Any())
        {
            AnsiConsole.MarkupLine("[yellow]⚠️  Tidak ada user untuk diundang.[/]");
            return;
        }

        int success = 0;
        int alreadyInvited = 0;
        int failed = 0;

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(),
            })
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Undang...[/]", new ProgressTaskSettings { MaxValue = usersToInvite.Count });
                
                using var client = TokenManager.CreateHttpClient(mainTokenEntry);

                foreach (var username in usersToInvite)
                {
                    task.Description = $"[green]Undang:[/] [yellow]@{username}[/]";
                    string url = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{username}";
                    var payload = new { permission = "push" };
                    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                    try
                    {
                        var response = await client.PutAsync(url, content);
                        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent)
                        {
                            if (response.StatusCode == HttpStatusCode.Created) {
                                AnsiConsole.MarkupLine($"[green]✓[/] Undangan → [yellow]@{username}[/]");
                                success++;
                            } else {
                                AnsiConsole.MarkupLine($"[grey]✓[/] [yellow]@{username}[/] sudah kolaborator.");
                                alreadyInvited++;
                            }
                        }
                        else
                        {
                            var error = await response.Content.ReadFromJsonAsync<GitHubError>();
                            AnsiConsole.MarkupLine($"[red]✗[/] [yellow]@{username}[/]: {response.StatusCode}");
                            failed++;
                        }
                    }
                    catch (Exception ex)
                    {
                         AnsiConsole.MarkupLine($"[red]✗[/] [yellow]@{username}[/]: {ex.Message}");
                         failed++;
                    }
                    task.Increment(1);
                    await Task.Delay(500);
                }
            });
        
        AnsiConsole.MarkupLine($"[green]✓ Selesai.[/] Terkirim: {success}, Sudah: {alreadyInvited}, Gagal: {failed}");
    }

    public static async Task AcceptInvitations()
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 3. Terima Undangan ---[/]");
        var tokens = TokenManager.GetAllTokenEntries();
         if (!tokens.Any()) return;

        var owner = tokens.First().Owner;
        var repo = tokens.First().Repo;
        string targetRepo = $"{owner}/{repo}".ToLower();
        AnsiConsole.MarkupLine($"[dim]Target: {targetRepo}[/]");

        int accepted = 0;
        int notFound = 0;
        int failed = 0;

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(),
            })
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Accept...[/]", new ProgressTaskSettings { MaxValue = tokens.Count });

                foreach (var entry in tokens)
                {
                    var tokenDisplay = TokenManager.MaskToken(entry.Token);
                    task.Description = $"[green]Cek:[/] {entry.Username ?? tokenDisplay}";

                    if (entry.Username?.Equals(owner, StringComparison.OrdinalIgnoreCase) ?? false) {
                        task.Increment(1);
                        continue;
                    }

                    try
                    {
                        using var client = TokenManager.CreateHttpClient(entry);
                        var response = await client.GetAsync("https://api.github.com/user/repository_invitations");

                        if (!response.IsSuccessStatusCode)
                        {
                            AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay}: {response.StatusCode}");
                            failed++;
                            task.Increment(1);
                            continue;
                        }

                        var invitations = await response.Content.ReadFromJsonAsync<List<GitHubInvitation>>();
                        var targetInvite = invitations?.FirstOrDefault(inv => 
                            inv.Repository?.FullName?.Equals(targetRepo, StringComparison.OrdinalIgnoreCase) ?? false);

                        if (targetInvite != null)
                        {
                            AnsiConsole.Markup($"[yellow]![/] {entry.Username ?? "user"} accepting... ");
                            string acceptUrl = $"https://api.github.com/user/repository_invitations/{targetInvite.Id}";
                            var patchResponse = await client.PatchAsync(acceptUrl, null);

                            if (patchResponse.IsSuccessStatusCode)
                            {
                                AnsiConsole.MarkupLine("[green]OK[/]");
                                accepted++;
                            }
                            else
                            {
                                AnsiConsole.MarkupLine($"[red]FAIL ({patchResponse.StatusCode})[/]");
                                failed++;
                            }
                        }
                        else
                        {
                            notFound++;
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay}: {ex.Message}");
                        failed++;
                    }
                    
                    task.Increment(1);
                    await Task.Delay(200);
                }
            });
            
        AnsiConsole.MarkupLine($"[green]✓ Selesai.[/] Accepted: {accepted}, Not Found: {notFound}, Failed: {failed}");
    }
}

// --- Helper Models ---

public class GitHubUser
{
    [JsonPropertyName("login")]
    public string? Login { get; set; }
}

public class GitHubError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public class GitHubInvitation
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("repository")]
    public GitHubRepository? Repository { get; set; }
}

public class GitHubRepository
{
    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }
}
