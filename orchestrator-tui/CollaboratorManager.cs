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
    // Hapus TIMEOUT_SEC dari sini, pakai dari TokenManager

    public static async Task ValidateAllTokens(CancellationToken cancellationToken = default)
    {
        // ... (Kode ValidateAllTokens tetap sama, tapi panggilannya ke CreateHttpClient sudah otomatis handle timeout) ...
        AnsiConsole.MarkupLine("[bold cyan]--- 1. Validasi Token & Username ---[/]");
        var tokens = TokenManager.GetAllTokenEntries();
        if (!tokens.Any()) { /* Log */ return; }
        var cache = TokenManager.GetUsernameCache();
        int newUsers = 0; bool cacheUpdated = false;

        await AnsiConsole.Progress().Columns(/* Progress columns */).StartAsync(async ctx => {
            var task = ctx.AddTask("[green]Validasi...[/]", new ProgressTaskSettings { MaxValue = tokens.Count });
            foreach (var entry in tokens) {
                cancellationToken.ThrowIfCancellationRequested(); var tokenDisplay = TokenManager.MaskToken(entry.Token); task.Description = $"[green]Cek:[/] {tokenDisplay}";
                if (!string.IsNullOrEmpty(entry.Username)) { /* Log skip */ task.Increment(1); continue; }
                if (cache.TryGetValue(entry.Token, out var cachedUsername)) { entry.Username = cachedUsername; /* Log skip */ task.Increment(1); continue; }

                bool success = false;
                for (int retry = 0; retry < MAX_RETRY && !success; retry++) {
                    cancellationToken.ThrowIfCancellationRequested(); try {
                        // --- PERUBAHAN DI SINI: Panggil TokenManager.CreateHttpClient langsung ---
                        using var client = TokenManager.CreateHttpClient(entry); // Timeout sudah di-handle
                        // --- AKHIR PERUBAHAN ---
                        var response = await client.GetAsync("https://api.github.com/user", cancellationToken);
                        if (response.IsSuccessStatusCode) { var user = await response.Content.ReadFromJsonAsync<GitHubUser>(); if (user?.Login != null) { /* Handle success */ success = true; } else { /* Handle invalid resp */ break; } }
                        else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { /* Handle auth/rate limit */ break; }
                        else if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired) { /* Handle 407 + rotate */ if (!TokenManager.RotateProxyForToken(entry)) break; await Task.Delay(2000, cancellationToken); }
                        else { /* Handle other errors + rotate */ if (!TokenManager.RotateProxyForToken(entry)) break; await Task.Delay(RETRY_DELAY_MS / 2, cancellationToken); }
                    } catch (HttpRequestException httpEx) { /* Handle network error + rotate */ if (!TokenManager.RotateProxyForToken(entry)) break; await Task.Delay(retry < MAX_RETRY - 1 ? RETRY_DELAY_MS : 1000, cancellationToken); }
                      catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { /* Handle timeout + rotate */ if (!TokenManager.RotateProxyForToken(entry)) break; await Task.Delay(RETRY_DELAY_MS, cancellationToken); }
                      catch (OperationCanceledException) { /* Handle user cancel */ throw; } catch (Exception ex) { /* Handle other ex */ break; }
                } if (!success) { /* Log final fail */ } task.Increment(1); await Task.Delay(500, cancellationToken);
            } // End foreach
        }); // End Progress
        if (cacheUpdated) { TokenManager.SaveTokenCache(cache); } /* Log summary */
    }

    public static async Task InviteCollaborators(CancellationToken cancellationToken = default)
    {
        // ... (Kode InviteCollaborators tetap sama, panggilannya ke CreateHttpClient sudah otomatis handle timeout) ...
        AnsiConsole.MarkupLine("[bold cyan]--- 2. Undang Kolaborator ---[/]");
        var tokens = TokenManager.GetAllTokenEntries(); /* ... null checks ... */
        var owner = tokens.First().Owner; var repo = tokens.First().Repo; /* ... null checks ... */
        var mainTokenEntry = tokens.FirstOrDefault(/* ... find owner ... */) ?? tokens.FirstOrDefault(); /* ... null check ... */
        var usersToInvite = tokens.Where(/* ... filter ... */).Select(t => t.Username!).Distinct().ToList(); /* ... empty check ... */

        int success = 0, alreadyInvited = 0, failed = 0;
        await AnsiConsole.Progress().Columns(/* ... */).StartAsync(async ctx => {
            var task = ctx.AddTask("[green]Undang...[/]", new ProgressTaskSettings { MaxValue = usersToInvite.Count });
            // --- PERUBAHAN DI SINI ---
            using var client = TokenManager.CreateHttpClient(mainTokenEntry); // Timeout sudah di-handle
            // --- AKHIR PERUBAHAN ---
            foreach (var username in usersToInvite) {
                /* ... Logika cek & invite ... */
                 cancellationToken.ThrowIfCancellationRequested(); task.Description = $"[green]Undang:[/] [yellow]@{username}[/]"; string checkUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{username}";
                try { var checkResponse = await client.GetAsync(checkUrl, cancellationToken); if (checkResponse.IsSuccessStatusCode) { /* Log sudah collab */ alreadyInvited++; task.Increment(1); await Task.Delay(500, cancellationToken); continue; } } catch (Exception ex) { /* Log error check */ failed++; task.Increment(1); await Task.Delay(500, cancellationToken); continue; }
                string inviteUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{username}"; var payload = new { permission = "push" }; var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                try { var response = await client.PutAsync(inviteUrl, content, cancellationToken); if (response.StatusCode == HttpStatusCode.Created) { /* Log sukses invite */ success++; } else if (response.StatusCode == HttpStatusCode.NoContent) { /* Log sudah collab */ alreadyInvited++; } else { /* Log gagal invite */ failed++; } }
                catch (OperationCanceledException) { throw; } catch (Exception ex) { /* Log exception invite */ failed++; }
                task.Increment(1); await Task.Delay(1000, cancellationToken);
            }
        }); /* Log summary */
    }

    public static async Task AcceptInvitations(CancellationToken cancellationToken = default)
    {
        // ... (Kode AcceptInvitations tetap sama, panggilannya ke CreateHttpClient sudah otomatis handle timeout) ...
        AnsiConsole.MarkupLine("[bold cyan]--- 3. Terima Undangan ---[/]");
        var tokens = TokenManager.GetAllTokenEntries(); /* ... null checks ... */
        var owner = tokens.First().Owner; var repo = tokens.First().Repo; string targetRepo = $"{owner}/{repo}".ToLower(); /* ... Log target ... */
        int accepted = 0, alreadyMember = 0, noInvitation = 0, failed = 0;
        await AnsiConsole.Progress().Columns(/* ... */).StartAsync(async ctx => {
            var task = ctx.AddTask("[green]Accept...[/]", new ProgressTaskSettings { MaxValue = tokens.Count });
            foreach (var entry in tokens) {
                /* ... skip owner ... */
                try {
                    // --- PERUBAHAN DI SINI ---
                    using var client = TokenManager.CreateHttpClient(entry); // Timeout sudah di-handle
                    // --- AKHIR PERUBAHAN ---
                    /* ... Logika cek & accept ... */
                    string checkUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{entry.Username}";
                    try { var checkResponse = await client.GetAsync(checkUrl, cancellationToken); if (checkResponse.IsSuccessStatusCode) { /* Log sudah member */ alreadyMember++; task.Increment(1); await Task.Delay(500, cancellationToken); continue; } } catch { /* Abaikan */ }
                    var response = await client.GetAsync("https://api.github.com/user/repository_invitations", cancellationToken); if (!response.IsSuccessStatusCode) { /* Log fail cek */ failed++; task.Increment(1); continue; }
                    var invitations = await response.Content.ReadFromJsonAsync<List<GitHubInvitation>>(); var targetInvite = invitations?.FirstOrDefault(/* ... find invite ... */);
                    if (targetInvite != null) { /* Log accepting */ string acceptUrl = $"https://api.github.com/user/repository_invitations/{targetInvite.Id}"; var patchResponse = await client.PatchAsync(acceptUrl, null, cancellationToken); if (patchResponse.IsSuccessStatusCode) { /* Log OK */ accepted++; } else { /* Log fail */ failed++; } } else { noInvitation++; }
                } catch (OperationCanceledException) { throw; } catch (Exception ex) { /* Log exception */ failed++; }
                task.Increment(1); await Task.Delay(1000, cancellationToken);
            }
        }); /* Log summary */
    }

    // HAPUS method CreateHttpClientWithTimeout
    // private static HttpClient CreateHttpClientWithTimeout(TokenEntry token) { ... }

    // --- Helper DTOs ---
    private class GitHubUser { /* ... */ }
    private class GitHubError { /* ... */ }
    private class GitHubInvitation { /* ... */ }
    private class GitHubRepository { /* ... */ }
}
