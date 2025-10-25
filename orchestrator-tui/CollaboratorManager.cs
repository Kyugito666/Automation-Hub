using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public static class CollaboratorManager
{
    private const int MAX_RETRY = 3;
    private const int RETRY_DELAY_MS = 30000;
    private const int TIMEOUT_SEC = 30;

    public static async Task ValidateAllTokens(CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 1. Validasi Token & Username ---[/]");

        // --- PERBAIKAN DI SINI ---
        // Salah: var tokens = TokenManager.GetAllTokens();
        // Benar:
        var tokens = TokenManager.GetAllTokenEntries();
        // --- AKHIR PERBAIKAN ---

        if (!tokens.Any())
        {
             AnsiConsole.MarkupLine("[yellow]⚠️  Tidak ada token.[/]");
             return;
        }

        // --- PERBAIKAN DI SINI ---
        // Salah: var cache = TokenManager.GetCache();
        // Benar:
        var cache = TokenManager.GetUsernameCache();
        // --- AKHIR PERBAIKAN ---

        int newUsers = 0;
        bool cacheUpdated = false;

        // ... (sisa kode ValidateAllTokens tidak berubah) ...
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
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    var tokenDisplay = TokenManager.MaskToken(entry.Token);
                    task.Description = $"[green]Cek:[/] {tokenDisplay}";

                    if (!string.IsNullOrEmpty(entry.Username))
                    {
                        AnsiConsole.MarkupLine($"[dim]⏭️  {tokenDisplay} → @{entry.Username} (cached)[/]");
                        task.Increment(1);
                        continue;
                    }
                    
                    if (cache.TryGetValue(entry.Token, out var cachedUsername))
                    {
                        entry.Username = cachedUsername;
                        AnsiConsole.MarkupLine($"[dim]⏭️  {tokenDisplay} → @{cachedUsername} (cache file)[/]");
                        task.Increment(1);
                        continue;
                    }

                    bool success = false;
                    for (int retry = 0; retry < MAX_RETRY && !success; retry++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        
                        try
                        {
                            using var client = CreateHttpClientWithTimeout(entry);
                            var response = await client.GetAsync("https://api.github.com/user", cancellationToken);

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
                                } 
                                else 
                                {
                                     AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay} INVALID RESPONSE");
                                     break;
                                }
                            }
                            else if (response.StatusCode == HttpStatusCode.Unauthorized)
                            {
                                AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay}: INVALID TOKEN (401)");
                                break;
                            }
                            else if (response.StatusCode == HttpStatusCode.Forbidden)
                            {
                                AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay}: RATE LIMIT (403)");
                                break;
                            }
                            else if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                            {
                                AnsiConsole.MarkupLine($"[yellow]🔁 407 (retry {retry + 1}/{MAX_RETRY})[/]");
                                if (!TokenManager.RotateProxyForToken(entry))
                                {
                                    break;
                                }
                                await Task.Delay(1500);
                                // JANGAN break, lanjut retry
                            }
                            else
                            {
                                AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay}: {response.StatusCode}");
                                break;
                            }
                        }
                        catch (HttpRequestException httpEx)
                        {
                            string errorMsg = httpEx.Message.ToLower();
                            
                            bool isConnectionError = errorMsg.Contains("connecting to api.github.com") ||
                                                    errorMsg.Contains("could not resolve host") ||
                                                    errorMsg.Contains("tls handshake timeout") ||
                                                    errorMsg.Contains("connection reset") ||
                                                    errorMsg.Contains("connection timed out") ||
                                                    errorMsg.Contains("network is unreachable") ||
                                                    errorMsg.Contains("temporary failure");
                            
                            bool isProxyError = errorMsg.Contains("407") || 
                                               errorMsg.Contains("proxy") ||
                                               errorMsg.Contains("tunnel") ||
                                               httpEx.InnerException is System.Net.Sockets.SocketException;
                            
                            if (isConnectionError || isProxyError)
                            {
                                AnsiConsole.MarkupLine($"[yellow]🔁 Network error (retry {retry + 1}/{MAX_RETRY})[/]");
                                
                                if (isProxyError)
                                {
                                    if (!TokenManager.RotateProxyForToken(entry))
                                    {
                                        AnsiConsole.MarkupLine($"[red]✗ No more proxies[/]");
                                        break;
                                    }
                                }
                                
                                await Task.Delay(retry < MAX_RETRY - 1 ? RETRY_DELAY_MS : 1000, cancellationToken);
                                // JANGAN break, lanjut retry
                            }
                            else
                            {
                                AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay}: {httpEx.Message.Split('\n').FirstOrDefault()}");
                                break;
                            }
                        }
                        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            AnsiConsole.MarkupLine($"[yellow]🔁 Timeout (retry {retry + 1}/{MAX_RETRY})[/]");
                            
                            TokenManager.RotateProxyForToken(entry);
                            
                            await Task.Delay(RETRY_DELAY_MS, cancellationToken);
                            // JANGAN break, lanjut retry
                        }
                        catch (OperationCanceledException)
                        {
                            AnsiConsole.MarkupLine("[yellow]⏹️  Dibatalkan user[/]");
                            throw;
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay}: {ex.Message.Split('\n').FirstOrDefault()}");
                            break;
                        }
                    }

                    if (!success)
                    {
                        AnsiConsole.MarkupLine($"[red]💀 {tokenDisplay} GAGAL setelah {MAX_RETRY} retry[/]");
                    }

                    task.Increment(1);
                    await Task.Delay(300, cancellationToken);
                }
            });

        if (cacheUpdated)
        {
            // --- PERBAIKAN DI SINI ---
            // Salah: TokenManager.SaveCache(cache);
            // Benar:
            TokenManager.SaveTokenCache(cache);
            // --- AKHIR PERBAIKAN ---
        }

        AnsiConsole.MarkupLine($"[green]✓ Selesai. {newUsers} username baru.[/]");
    }

    public static async Task InviteCollaborators(CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 2. Undang Kolaborator ---[/]");

        // --- PERBAIKAN DI SINI ---
        // Salah: var tokens = TokenManager.GetAllTokens();
        // Benar:
        var tokens = TokenManager.GetAllTokenEntries();
        // --- AKHIR PERBAIKAN ---

        if (!tokens.Any()) return;

        // ... (sisa kode InviteCollaborators tidak berubah) ...
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
            AnsiConsole.MarkupLine($"[yellow]⚠️  Token owner '{owner}' not found. Using first token...[/]");
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
                
                using var client = CreateHttpClientWithTimeout(mainTokenEntry);

                foreach (var username in usersToInvite)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    task.Description = $"[green]Undang:[/] [yellow]@{username}[/]";
                    
                    string checkUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{username}";
                    var checkResponse = await client.GetAsync(checkUrl);
                    
                    if (checkResponse.IsSuccessStatusCode)
                    {
                        AnsiConsole.MarkupLine($"[grey]✓[/] [yellow]@{username}[/] sudah kolaborator.");
                        alreadyInvited++;
                        task.Increment(1);
                        await Task.Delay(500);
                        continue;
                    }

                    string inviteUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{username}";
                    var payload = new { permission = "push" };
                    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                    try
                    {
                        var response = await client.PutAsync(inviteUrl, content);
                        
                        if (response.StatusCode == HttpStatusCode.Created)
                        {
                            AnsiConsole.MarkupLine($"[green]✓[/] Undangan → [yellow]@{username}[/]");
                            success++;
                        }
                        else if (response.StatusCode == HttpStatusCode.NoContent)
                        {
                            AnsiConsole.MarkupLine($"[grey]✓[/] [yellow]@{username}[/] sudah kolaborator.");
                            alreadyInvited++;
                        }
                        else
                        {
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
                    await Task.Delay(1000);
                }
            });
        
        AnsiConsole.MarkupLine($"[green]✓ Selesai.[/] Terkirim: {success}, Sudah: {alreadyInvited}, Gagal: {failed}");
    }

    public static async Task AcceptInvitations(CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 3. Terima Undangan ---[/]");

        // --- PERBAIKAN DI SINI ---
        // Salah: var tokens = TokenManager.GetAllTokens();
        // Benar:
        var tokens = TokenManager.GetAllTokenEntries();
        // --- AKHIR PERBAIKAN ---

         if (!tokens.Any()) return;

        // ... (sisa kode AcceptInvitations tidak berubah) ...
        var owner = tokens.First().Owner;
        var repo = tokens.First().Repo;
        string targetRepo = $"{owner}/{repo}".ToLower();
        AnsiConsole.MarkupLine($"[dim]Target: {targetRepo}[/]");

        int accepted = 0;
        int alreadyMember = 0;
        int noInvitation = 0;
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
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    var tokenDisplay = TokenManager.MaskToken(entry.Token);
                    task.Description = $"[green]Cek:[/] {entry.Username ?? tokenDisplay}";

                    if (entry.Username?.Equals(owner, StringComparison.OrdinalIgnoreCase) ?? false)
                    {
                        task.Increment(1);
                        continue;
                    }

                    try
                    {
                        using var client = CreateHttpClientWithTimeout(entry);
                        
                        string checkUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{entry.Username}";
                        var checkResponse = await client.GetAsync(checkUrl);
                        
                        if (checkResponse.IsSuccessStatusCode)
                        {
                            AnsiConsole.MarkupLine($"[green]✓[/] {entry.Username ?? "user"} sudah kolaborator");
                            alreadyMember++;
                            task.Increment(1);
                            await Task.Delay(500);
                            continue;
                        }

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
                            noInvitation++;
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay}: {ex.Message}");
                        failed++;
                    }
                    
                    task.Increment(1);
                    await Task.Delay(1000);
                }
            });
            
        AnsiConsole.MarkupLine($"[green]✓ Selesai.[/] Accepted: {accepted}, Sudah: {alreadyMember}, Tidak ada: {noInvitation}, Gagal: {failed}");
    }

    // ... (CreateHttpClientWithTimeout dan kelas DTO GitHub* tetap sama) ...
     private static HttpClient CreateHttpClientWithTimeout(TokenEntry token)
    {
        var handler = new HttpClientHandler
        {
            Proxy = !string.IsNullOrEmpty(token.Proxy) ? new WebProxy(token.Proxy) : null,
            UseProxy = !string.IsNullOrEmpty(token.Proxy)
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TIMEOUT_SEC)
        };
        
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");
        client.DefaultRequestHeaders.Add("User-Agent", "Automation-Hub-Orchestrator/3.1");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        
        return client;
    }
}

// DTO Classes (GitHubUser, GitHubError, GitHubInvitation, GitHubRepository)
// ... (Kode class ini sama seperti sebelumnya) ...
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
