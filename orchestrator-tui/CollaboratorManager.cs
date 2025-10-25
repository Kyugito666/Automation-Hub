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
    private const int RETRY_DELAY_MS = 30000; // 30 detik
    private const int TIMEOUT_SEC = 45; // Naikkan timeout sedikit

    public static async Task ValidateAllTokens(CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 1. Validasi Token & Username ---[/]");

        // --- PERBAIKAN DI SINI ---
        var tokens = TokenManager.GetAllTokenEntries(); // Nama method yang benar
        // --- AKHIR PERBAIKAN ---

        if (!tokens.Any()) { AnsiConsole.MarkupLine("[yellow]⚠️  Tidak ada token.[/]"); return; }

        // --- PERBAIKAN DI SINI ---
        var cache = TokenManager.GetUsernameCache(); // Nama method yang benar
        // --- AKHIR PERBAIKAN ---

        int newUsers = 0; bool cacheUpdated = false;

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[] { /*...*/ }) // Kolom progress bar
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Validasi...[/]", new ProgressTaskSettings { MaxValue = tokens.Count });

                foreach (var entry in tokens)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tokenDisplay = TokenManager.MaskToken(entry.Token);
                    task.Description = $"[green]Cek:[/] {tokenDisplay}";

                    // Cek cache dulu
                    if (!string.IsNullOrEmpty(entry.Username)) { /* Log skip */ task.Increment(1); continue; }
                    if (cache.TryGetValue(entry.Token, out var cachedUsername)) { entry.Username = cachedUsername; /* Log skip */ task.Increment(1); continue; }

                    // Jika tidak ada di cache, panggil API dengan retry & rotasi proxy
                    bool success = false;
                    for (int retry = 0; retry < MAX_RETRY && !success; retry++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            using var client = CreateHttpClientWithTimeout(entry); // Pakai HTTP client dengan timeout & proxy
                            var response = await client.GetAsync("https://api.github.com/user", cancellationToken);

                            // Handle response
                            if (response.IsSuccessStatusCode) {
                                var user = await response.Content.ReadFromJsonAsync<GitHubUser>();
                                if (user?.Login != null) {
                                    AnsiConsole.MarkupLine($"[green]✓[/] {tokenDisplay} → [yellow]@{user.Login}[/]");
                                    entry.Username = user.Login; cache[entry.Token] = user.Login;
                                    newUsers++; cacheUpdated = true; success = true;
                                } else { AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay} INVALID RESPONSE"); break; } // Gagal permanen jika response aneh
                            }
                            // Handle error spesifik
                            else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) {
                                AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay}: {(response.StatusCode == HttpStatusCode.Unauthorized ? "INVALID TOKEN (401)" : "RATE LIMIT/FORBIDDEN (403)")}");
                                break; // Gagal permanen
                            }
                            else if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired) {
                                AnsiConsole.MarkupLine($"[yellow]🔁 Proxy Auth (407) (retry {retry + 1}/{MAX_RETRY})[/]");
                                if (!TokenManager.RotateProxyForToken(entry)) break; // Coba proxy lain, jika gagal = stop retry
                                await Task.Delay(2000, cancellationToken); // Jeda sebelum retry
                            }
                            else { // Error lain
                                AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay}: {response.StatusCode} (retry {retry + 1}/{MAX_RETRY})");
                                if (!TokenManager.RotateProxyForToken(entry)) break; // Coba proxy lain
                                await Task.Delay(RETRY_DELAY_MS / 2, cancellationToken); // Jeda lebih lama
                            }
                        }
                        // Handle exception koneksi/proxy/timeout
                        catch (HttpRequestException httpEx) {
                           AnsiConsole.MarkupLine($"[dim]   Detail Error: Status={httpEx.StatusCode}, Msg={httpEx.Message.Split('\n').FirstOrDefault()}[/]");
                           AnsiConsole.MarkupLine($"[yellow]🔁 Network error (retry {retry + 1}/{MAX_RETRY})[/]");
                           if (!TokenManager.RotateProxyForToken(entry)) { AnsiConsole.MarkupLine($"[red]✗ No more proxies[/]"); break; } // Rotasi, jika gagal = stop
                           await Task.Delay(retry < MAX_RETRY - 1 ? RETRY_DELAY_MS : 1000, cancellationToken); // Jeda retry
                        }
                        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { // Timeout
                           AnsiConsole.MarkupLine($"[yellow]🔁 Timeout (retry {retry + 1}/{MAX_RETRY})[/]");
                           if (!TokenManager.RotateProxyForToken(entry)) { AnsiConsole.MarkupLine($"[red]✗ No more proxies[/]"); break; } // Rotasi, jika gagal = stop
                           await Task.Delay(RETRY_DELAY_MS, cancellationToken); // Jeda retry
                        }
                        catch (OperationCanceledException) { AnsiConsole.MarkupLine("[yellow]⏹️  Dibatalkan user[/]"); throw; } // User cancel
                        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay} Unhandled Ex: {ex.Message.Split('\n').FirstOrDefault()}"); break; } // Error tak terduga = stop
                    } // End retry loop

                    if (!success) { AnsiConsole.MarkupLine($"[red]💀 {tokenDisplay} GAGAL setelah {MAX_RETRY} retry[/]"); }
                    task.Increment(1);
                    await Task.Delay(500, cancellationToken); // Jeda antar token
                } // End foreach token
            }); // End Progress

        if (cacheUpdated)
        {
            // --- PERBAIKAN DI SINI ---
            TokenManager.SaveTokenCache(cache); // Nama method yang benar
            // --- AKHIR PERBAIKAN ---
        }
        AnsiConsole.MarkupLine($"[green]✓ Selesai. {newUsers} username baru.[/]");
    }

    public static async Task InviteCollaborators(CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 2. Undang Kolaborator ---[/]");

        // --- PERBAIKAN DI SINI ---
        var tokens = TokenManager.GetAllTokenEntries(); // Nama method yang benar
        // --- AKHIR PERBAIKAN ---

        if (!tokens.Any()) return;
        var owner = tokens.First().Owner; var repo = tokens.First().Repo;
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo)) { /* Error msg */ return; }

        var mainTokenEntry = tokens.FirstOrDefault(t => t.Username?.Equals(owner, StringComparison.OrdinalIgnoreCase) ?? false) ?? tokens.FirstOrDefault();
        if (mainTokenEntry == null) { /* Error msg */ return; }
        if (!mainTokenEntry.Username?.Equals(owner, StringComparison.OrdinalIgnoreCase) ?? true) { /* Warning msg */ }

        var usersToInvite = tokens.Where(t => t.Username != null && !t.Username.Equals(owner, StringComparison.OrdinalIgnoreCase)).Select(t => t.Username!).Distinct().ToList();
        if (!usersToInvite.Any()) { /* Info msg */ return; }

        int success = 0, alreadyInvited = 0, failed = 0;
        await AnsiConsole.Progress().Columns(new ProgressColumn[] { /*...*/ }).StartAsync(async ctx => { // Progress bar
            var task = ctx.AddTask("[green]Undang...[/]", new ProgressTaskSettings { MaxValue = usersToInvite.Count });
            using var client = CreateHttpClientWithTimeout(mainTokenEntry); // Pakai token owner
            foreach (var username in usersToInvite) {
                cancellationToken.ThrowIfCancellationRequested();
                task.Description = $"[green]Undang:[/] [yellow]@{username}[/]";
                string checkUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{username}";
                try {
                    var checkResponse = await client.GetAsync(checkUrl, cancellationToken);
                    if (checkResponse.IsSuccessStatusCode) { /* Log sudah collab */ alreadyInvited++; task.Increment(1); await Task.Delay(500, cancellationToken); continue; }
                } catch (Exception ex) { /* Log error check */ failed++; task.Increment(1); await Task.Delay(500, cancellationToken); continue; }

                string inviteUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{username}";
                var payload = new { permission = "push" }; var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                try {
                    var response = await client.PutAsync(inviteUrl, content, cancellationToken);
                    if (response.StatusCode == HttpStatusCode.Created) { /* Log sukses invite */ success++; }
                    else if (response.StatusCode == HttpStatusCode.NoContent) { /* Log sudah collab */ alreadyInvited++; }
                    else { /* Log gagal invite */ failed++; }
                } catch (OperationCanceledException) { throw; }
                  catch (Exception ex) { /* Log exception invite */ failed++; }
                task.Increment(1); await Task.Delay(1000, cancellationToken); // Jeda API invite
            }
        });
        AnsiConsole.MarkupLine($"[green]✓ Selesai.[/] Terkirim: {success}, Sudah: {alreadyInvited}, Gagal: {failed}");
    }

    public static async Task AcceptInvitations(CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 3. Terima Undangan ---[/]");

        // --- PERBAIKAN DI SINI ---
        var tokens = TokenManager.GetAllTokenEntries(); // Nama method yang benar
        // --- AKHIR PERBAIKAN ---

        if (!tokens.Any()) return;
        var owner = tokens.First().Owner; var repo = tokens.First().Repo;
        string targetRepo = $"{owner}/{repo}".ToLower();
        AnsiConsole.MarkupLine($"[dim]Target: {targetRepo}[/]");

        int accepted = 0, alreadyMember = 0, noInvitation = 0, failed = 0;
        await AnsiConsole.Progress().Columns(new ProgressColumn[] { /*...*/ }).StartAsync(async ctx => { // Progress bar
            var task = ctx.AddTask("[green]Accept...[/]", new ProgressTaskSettings { MaxValue = tokens.Count });
            foreach (var entry in tokens) {
                cancellationToken.ThrowIfCancellationRequested();
                var tokenDisplay = TokenManager.MaskToken(entry.Token);
                task.Description = $"[green]Cek:[/] {entry.Username ?? tokenDisplay}";
                if (entry.Username?.Equals(owner, StringComparison.OrdinalIgnoreCase) ?? false) { task.Increment(1); continue; } // Skip owner

                try {
                    using var client = CreateHttpClientWithTimeout(entry); // Pakai token user ybs
                    // Cek dulu apakah sudah member
                    string checkUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{entry.Username}";
                    try { // Try-catch untuk check
                        var checkResponse = await client.GetAsync(checkUrl, cancellationToken);
                        if (checkResponse.IsSuccessStatusCode) { /* Log sudah member */ alreadyMember++; task.Increment(1); await Task.Delay(500, cancellationToken); continue; }
                    } catch { /* Abaikan error check, mungkin memang belum member */ }

                    // Cek undangan
                    var response = await client.GetAsync("https://api.github.com/user/repository_invitations", cancellationToken);
                    if (!response.IsSuccessStatusCode) { /* Log gagal cek invite */ failed++; task.Increment(1); continue; }

                    var invitations = await response.Content.ReadFromJsonAsync<List<GitHubInvitation>>();
                    var targetInvite = invitations?.FirstOrDefault(inv => inv.Repository?.FullName?.Equals(targetRepo, StringComparison.OrdinalIgnoreCase) ?? false);
                    if (targetInvite != null) {
                        AnsiConsole.Markup($"[yellow]![/] {entry.Username ?? "user"} accepting... ");
                        string acceptUrl = $"https://api.github.com/user/repository_invitations/{targetInvite.Id}";
                        var patchResponse = await client.PatchAsync(acceptUrl, null, cancellationToken); // Method PATCH
                        if (patchResponse.IsSuccessStatusCode) { AnsiConsole.MarkupLine("[green]OK[/]"); accepted++; }
                        else { AnsiConsole.MarkupLine($"[red]FAIL ({patchResponse.StatusCode})[/]"); failed++; }
                    } else { noInvitation++; } // Undangan tidak ditemukan
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { /* Log exception */ failed++; }
                task.Increment(1); await Task.Delay(1000, cancellationToken); // Jeda antar token
            }
        });
        AnsiConsole.MarkupLine($"[green]✓ Selesai.[/] Accepted: {accepted}, Sudah: {alreadyMember}, Tidak ada: {noInvitation}, Gagal: {failed}");
    }

     private static HttpClient CreateHttpClientWithTimeout(TokenEntry token)
    {
        // ... (Fungsi ini tetap sama) ...
         var handler = new HttpClientHandler();
         if (!string.IsNullOrEmpty(token.Proxy))
         {
             try { handler.Proxy = new WebProxy(token.Proxy); handler.UseProxy = true; }
             catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Proxy invalid {TokenManager.MaskProxy(token.Proxy)}: {ex.Message}[/]"); } // Gunakan MaskProxy public
         }
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TIMEOUT_SEC) };
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");
        client.DefaultRequestHeaders.Add("User-Agent", "Automation-Hub-Orchestrator/3.1");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return client;
    }

    // --- Helper class DTOs ---
    private class GitHubUser { [JsonPropertyName("login")] public string? Login { get; set; } }
    private class GitHubError { [JsonPropertyName("message")] public string? Message { get; set; } }
    private class GitHubInvitation { [JsonPropertyName("id")] public long Id { get; set; } [JsonPropertyName("repository")] public GitHubRepository? Repository { get; set; } }
    private class GitHubRepository { [JsonPropertyName("full_name")] public string? FullName { get; set; } }
}
