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
    // Timeout diambil dari TokenManager.DEFAULT_HTTP_TIMEOUT_SEC

    public static async Task ValidateAllTokens(CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 1. Validasi Token & Username ---[/]");
        var tokens = TokenManager.GetAllTokenEntries(); // Nama method yg benar
        if (!tokens.Any()) { /* Log */ return; }
        var cache = TokenManager.GetUsernameCache(); // Nama method yg benar
        int newUsers = 0; bool cacheUpdated = false; // newUsers dipakai di log akhir

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[] { // Pastikan kolom didefinisikan
                new TaskDescriptionColumn(), new ProgressBarColumn(),
                new PercentageColumn(), new RemainingTimeColumn(), new SpinnerColumn(),
             })
            .StartAsync(async ctx => {
            var task = ctx.AddTask("[green]Validasi...[/]", new ProgressTaskSettings { MaxValue = tokens.Count });
            foreach (var entry in tokens) {
                cancellationToken.ThrowIfCancellationRequested(); var tokenDisplay = TokenManager.MaskToken(entry.Token); task.Description = $"[green]Cek:[/] {tokenDisplay}";
                if (!string.IsNullOrEmpty(entry.Username)) { /* Log skip */ task.Increment(1); continue; }
                if (cache.TryGetValue(entry.Token, out var cachedUsername)) { entry.Username = cachedUsername; /* Log skip */ task.Increment(1); continue; }

                bool success = false;
                for (int retry = 0; retry < MAX_RETRY && !success; retry++) {
                    cancellationToken.ThrowIfCancellationRequested(); try {
                        using var client = TokenManager.CreateHttpClient(entry); // Pakai factory TokenManager
                        var response = await client.GetAsync("https://api.github.com/user", cancellationToken);
                        if (response.IsSuccessStatusCode) {
                            // --- PERBAIKAN DI SINI (CS1061) ---
                            var user = await response.Content.ReadFromJsonAsync<GitHubUser>(cancellationToken: cancellationToken); // Baca sbg GitHubUser
                            if (user?.Login != null) { // Akses property Login
                            // --- AKHIR PERBAIKAN ---
                                AnsiConsole.MarkupLine($"[green]✓[/] {tokenDisplay} → [yellow]@{user.Login}[/]"); entry.Username = user.Login; cache[entry.Token] = user.Login; newUsers++; cacheUpdated = true; success = true;
                            } else { AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay} INVALID RESPONSE"); break; }
                        }
                        else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay}: {(response.StatusCode == HttpStatusCode.Unauthorized ? "INVALID TOKEN (401)" : "RATE LIMIT/FORBIDDEN (403)")}"); break; }
                        else if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired) { AnsiConsole.MarkupLine($"[yellow]🔁 Proxy Auth (407) (retry {retry + 1}/{MAX_RETRY})[/]"); if (!TokenManager.RotateProxyForToken(entry)) break; await Task.Delay(2000, cancellationToken); }
                        else { AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay}: {response.StatusCode} (retry {retry + 1}/{MAX_RETRY})"); if (!TokenManager.RotateProxyForToken(entry)) break; await Task.Delay(RETRY_DELAY_MS / 2, cancellationToken); }
                    } catch (HttpRequestException httpEx) { AnsiConsole.MarkupLine($"[dim]   Detail Error: Status={httpEx.StatusCode}, Msg={httpEx.Message.Split('\n').FirstOrDefault()}[/]"); AnsiConsole.MarkupLine($"[yellow]🔁 Network error (retry {retry + 1}/{MAX_RETRY})[/]"); if (!TokenManager.RotateProxyForToken(entry)) { AnsiConsole.MarkupLine($"[red]✗ No more proxies[/]"); break; } await Task.Delay(retry < MAX_RETRY - 1 ? RETRY_DELAY_MS : 1000, cancellationToken); }
                      catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { AnsiConsole.MarkupLine($"[yellow]🔁 Timeout (retry {retry + 1}/{MAX_RETRY})[/]"); if (!TokenManager.RotateProxyForToken(entry)) { AnsiConsole.MarkupLine($"[red]✗ No more proxies[/]"); break; } await Task.Delay(RETRY_DELAY_MS, cancellationToken); }
                      catch (OperationCanceledException) { AnsiConsole.MarkupLine("[yellow]⏹️  Dibatalkan user[/]"); throw; }
                      catch (Exception ex) { AnsiConsole.MarkupLine($"[red]✗[/] {tokenDisplay} Unhandled Ex: {ex.Message.Split('\n').FirstOrDefault()}"); break; }
                } if (!success) { AnsiConsole.MarkupLine($"[red]💀 {tokenDisplay} GAGAL setelah {MAX_RETRY} retry[/]"); } task.Increment(1); await Task.Delay(500, cancellationToken);
            } // End foreach
        }); // End Progress
        if (cacheUpdated) { TokenManager.SaveTokenCache(cache); } // Nama method yg benar
        AnsiConsole.MarkupLine($"[green]✓ Selesai. {newUsers} username baru.[/]"); // newUsers dipakai di sini
    }

    public static async Task InviteCollaborators(CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 2. Undang Kolaborator ---[/]");
        var tokens = TokenManager.GetAllTokenEntries(); // Nama method yg benar
        if (!tokens.Any()) return; var owner = tokens.First().Owner; var repo = tokens.First().Repo; if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo)) { /* Error */ return; }
        var mainTokenEntry = tokens.FirstOrDefault(t => t.Username?.Equals(owner, StringComparison.OrdinalIgnoreCase) ?? false) ?? tokens.FirstOrDefault(); if (mainTokenEntry == null) { /* Error */ return; } if (!mainTokenEntry.Username?.Equals(owner, StringComparison.OrdinalIgnoreCase) ?? true) { /* Warning */ }

        // --- PERBAIKAN DI SINI (CS1501) ---
        // Tambahkan lambda expression ke Where
        var usersToInvite = tokens
            .Where(t => t.Username != null && !t.Username.Equals(owner, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Username!)
            .Distinct()
            .ToList();
         // --- AKHIR PERBAIKAN ---

        if (!usersToInvite.Any()) { /* Info */ return; }

        int success = 0, alreadyInvited = 0, failed = 0;
        await AnsiConsole.Progress().Columns(/* ... */).StartAsync(async ctx => {
            // --- PERBAIKAN DI SINI (CS0428) ---
            // Panggil Count() sebagai method
            var task = ctx.AddTask("[green]Undang...[/]", new ProgressTaskSettings { MaxValue = usersToInvite.Count() }); // Tambah ()
            // --- AKHIR PERBAIKAN ---
            using var client = TokenManager.CreateHttpClient(mainTokenEntry); // Pakai factory TokenManager
            foreach (var username in usersToInvite) {
                cancellationToken.ThrowIfCancellationRequested(); task.Description = $"[green]Undang:[/] [yellow]@{username}[/]"; string checkUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{username}";
                try { var checkResponse = await client.GetAsync(checkUrl, cancellationToken); if (checkResponse.IsSuccessStatusCode) { /* Log */ alreadyInvited++; task.Increment(1); await Task.Delay(500, cancellationToken); continue; } } catch (Exception ex) { /* Log err check */ failed++; task.Increment(1); await Task.Delay(500, cancellationToken); continue; }
                string inviteUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{username}"; var payload = new { permission = "push" }; var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                try { var response = await client.PutAsync(inviteUrl, content, cancellationToken); if (response.StatusCode == HttpStatusCode.Created) { /* Log invite ok */ success++; } else if (response.StatusCode == HttpStatusCode.NoContent) { /* Log sudah collab */ alreadyInvited++; } else { /* Log fail invite */ failed++; } }
                catch (OperationCanceledException) { throw; } catch (Exception ex) { /* Log exception invite */ failed++; }
                task.Increment(1); await Task.Delay(1000, cancellationToken);
            }
        }); AnsiConsole.MarkupLine($"[green]✓ Selesai.[/] Terkirim: {success}, Sudah: {alreadyInvited}, Gagal: {failed}");
    }

    public static async Task AcceptInvitations(CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 3. Terima Undangan ---[/]");
        var tokens = TokenManager.GetAllTokenEntries(); // Nama method yg benar
        if (!tokens.Any()) return; var owner = tokens.First().Owner; var repo = tokens.First().Repo; string targetRepo = $"{owner}/{repo}".ToLower(); AnsiConsole.MarkupLine($"[dim]Target: {targetRepo}[/]");

        int accepted = 0, alreadyMember = 0, noInvitation = 0, failed = 0;
        await AnsiConsole.Progress().Columns(/* ... */).StartAsync(async ctx => { // Progress
            var task = ctx.AddTask("[green]Accept...[/]", new ProgressTaskSettings { MaxValue = tokens.Count });
            foreach (var entry in tokens) {
                cancellationToken.ThrowIfCancellationRequested(); var tokenDisplay = TokenManager.MaskToken(entry.Token); task.Description = $"[green]Cek:[/] {entry.Username ?? tokenDisplay}"; if (entry.Username?.Equals(owner, StringComparison.OrdinalIgnoreCase) ?? false) { task.Increment(1); continue; }
                try {
                    using var client = TokenManager.CreateHttpClient(entry); // Pakai factory TokenManager
                    string checkUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{entry.Username}";
                    try { var checkResponse = await client.GetAsync(checkUrl, cancellationToken); if (checkResponse.IsSuccessStatusCode) { /* Log sudah member */ alreadyMember++; task.Increment(1); await Task.Delay(500, cancellationToken); continue; } } catch { /* Abaikan */ }
                    var response = await client.GetAsync("https://api.github.com/user/repository_invitations", cancellationToken); if (!response.IsSuccessStatusCode) { /* Log fail cek */ failed++; task.Increment(1); continue; }
                    var invitations = await response.Content.ReadFromJsonAsync<List<GitHubInvitation>>(); var targetInvite = invitations?.FirstOrDefault(inv => inv.Repository?.FullName?.Equals(targetRepo, StringComparison.OrdinalIgnoreCase) ?? false);
                    if (targetInvite != null) {
                        AnsiConsole.Markup($"[yellow]![/] {entry.Username ?? "user"} accepting... ");
                        // --- PERBAIKAN DI SINI (CS1061) ---
                        string acceptUrl = $"https://api.github.com/user/repository_invitations/{targetInvite.Id}"; // Akses property Id
                        // --- AKHIR PERBAIKAN ---
                        var patchResponse = await client.PatchAsync(acceptUrl, null, cancellationToken); if (patchResponse.IsSuccessStatusCode) { AnsiConsole.MarkupLine("[green]OK[/]"); accepted++; } else { AnsiConsole.MarkupLine($"[red]FAIL ({patchResponse.StatusCode})[/]"); failed++; }
                    } else { noInvitation++; }
                }
                catch (OperationCanceledException) { throw; } catch (Exception ex) { /* Log exception */ failed++; }
                task.Increment(1); await Task.Delay(1000, cancellationToken);
            }
        }); AnsiConsole.MarkupLine($"[green]✓ Selesai.[/] Accepted: {accepted}, Sudah: {alreadyMember}, Tidak ada: {noInvitation}, Gagal: {failed}");
    }

     // HAPUS method CreateHttpClientWithTimeout
     // private static HttpClient CreateHttpClientWithTimeout(TokenEntry token) { ... }

    // --- Helper DTOs ---
    // --- PERBAIKAN DI SINI (CS1061) ---
    // Pastikan property ditulis dengan huruf besar di awal (PascalCase)
    private class GitHubUser { [JsonPropertyName("login")] public string? Login { get; set; } }
    private class GitHubError { [JsonPropertyName("message")] public string? Message { get; set; } }
    private class GitHubInvitation { [JsonPropertyName("id")] public long Id { get; set; } [JsonPropertyName("repository")] public GitHubRepository? Repository { get; set; } }
    private class GitHubRepository { [JsonPropertyName("full_name")] public string? FullName { get; set; } }
    // --- AKHIR PERBAIKAN ---
}
