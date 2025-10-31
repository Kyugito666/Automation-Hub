using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using Orchestrator.Core; // Menggunakan Core.TokenManager

namespace Orchestrator.Services // Namespace baru
{
    // Ganti nama kelas
    public static class CollabService
    {
        private const int MAX_RETRY = 3;
        private const int RETRY_DELAY_MS = 30000;

        public static async Task ValidateAllTokens(CancellationToken cancellationToken = default)
        {
            Console.WriteLine("--- 1. Validasi Token & Username ---");
            var tokens = TokenManager.GetAllTokenEntries();
            if (!tokens.Any()) { 
                Console.WriteLine("Tidak ada token yang dikonfigurasi."); 
                return; 
            }
            
            var cache = TokenManager.GetUsernameCache();
            int newUsers = 0;
            bool cacheUpdated = false;

            await AnsiConsole.Progress()
                .Columns(new ProgressColumn[] {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn(),
                })
                .StartAsync(async ctx => {
                    var task = ctx.AddTask("[green]Validasi...[/]", new ProgressTaskSettings { MaxValue = tokens.Count });
                    
                    foreach (var entry in tokens) {
                        cancellationToken.ThrowIfCancellationRequested();
                        var tokenDisplay = TokenManager.MaskToken(entry.Token);
                        task.Description = $"[green]Cek:[/] {tokenDisplay}";
                        
                        if (!string.IsNullOrEmpty(entry.Username)) {
                            Console.WriteLine($"Skip {tokenDisplay} - sudah ada username");
                            task.Increment(1);
                            continue;
                        }
                        
                        if (cache.TryGetValue(entry.Token, out var cachedUsername)) {
                            entry.Username = cachedUsername;
                            Console.WriteLine($"Skip {tokenDisplay} - dari cache");
                            task.Increment(1);
                            continue;
                        }

                        bool success = false;
                        for (int retry = 0; retry < MAX_RETRY && !success; retry++) {
                            cancellationToken.ThrowIfCancellationRequested();
                            try {
                                using var client = TokenManager.CreateHttpClient(entry);
                                var response = await client.GetAsync("https://api.github.com/user", cancellationToken);
                                
                                if (response.IsSuccessStatusCode) {
                                    var user = await response.Content.ReadFromJsonAsync<GitHubUser>(cancellationToken: cancellationToken);
                                    if (user?.Login != null) {
                                        Console.WriteLine($"OK {tokenDisplay} -> @{user.Login}");
                                        entry.Username = user.Login;
                                        cache[entry.Token] = user.Login;
                                        newUsers++;
                                        cacheUpdated = true;
                                        success = true;
                                    } else {
                                        Console.WriteLine($"FAIL {tokenDisplay} - Invalid response");
                                        break;
                                    }
                                }
                                else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) {
                                    Console.WriteLine($"FAIL {tokenDisplay} - {(response.StatusCode == HttpStatusCode.Unauthorized ? "Invalid token (401)" : "Rate limit/Forbidden (403)")}");
                                    break;
                                }
                                else if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired) {
                                    Console.WriteLine($"Retry {tokenDisplay} - Proxy Auth (407) ({retry + 1}/{MAX_RETRY})");
                                    if (!TokenManager.RotateProxyForToken(entry)) break;
                                    await Task.Delay(2000, cancellationToken);
                                }
                                else {
                                    Console.WriteLine($"Retry {tokenDisplay} - {response.StatusCode} ({retry + 1}/{MAX_RETRY})");
                                    if (!TokenManager.RotateProxyForToken(entry)) break;
                                    await Task.Delay(RETRY_DELAY_MS / 2, cancellationToken);
                                }
                            } catch (HttpRequestException httpEx) {
                                Console.WriteLine($"Network error: {httpEx.Message}");
                                Console.WriteLine($"Retry ({retry + 1}/{MAX_RETRY})");
                                if (!TokenManager.RotateProxyForToken(entry)) {
                                    Console.WriteLine("No more proxies");
                                    break;
                                }
                                await Task.Delay(retry < MAX_RETRY - 1 ? RETRY_DELAY_MS : 1000, cancellationToken);
                            }
                            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) {
                                Console.WriteLine($"Timeout - Retry ({retry + 1}/{MAX_RETRY})");
                                if (!TokenManager.RotateProxyForToken(entry)) {
                                    Console.WriteLine("No more proxies");
                                    break;
                                }
                                await Task.Delay(RETRY_DELAY_MS, cancellationToken);
                            }
                            catch (OperationCanceledException) {
                                Console.WriteLine("Dibatalkan user");
                                throw;
                            }
                            catch (Exception ex) {
                                Console.WriteLine($"FAIL {tokenDisplay} - Unhandled: {ex.Message}");
                                break;
                            }
                        }
                        
                        if (!success) {
                            Console.WriteLine($"GAGAL {tokenDisplay} setelah {MAX_RETRY} retry");
                        }
                        
                        task.Increment(1);
                        await Task.Delay(500, cancellationToken);
                    }
                });
            
            if (cacheUpdated) {
                TokenManager.SaveTokenCache(cache);
            }
            
            Console.WriteLine($"Selesai. {newUsers} username baru.");
        }

        public static async Task InviteCollaborators(CancellationToken cancellationToken = default)
        {
            Console.WriteLine("--- 2. Undang Kolaborator ---");
            var tokens = TokenManager.GetAllTokenEntries();
            if (!tokens.Any()) {
                Console.WriteLine("Tidak ada token.");
                return;
            }
            
            var owner = tokens.First().Owner;
            var repo = tokens.First().Repo;
            
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo)) {
                Console.WriteLine("Owner/Repo tidak valid.");
                return;
            }
            
            var mainTokenEntry = tokens.FirstOrDefault(t => t.Username?.Equals(owner, StringComparison.OrdinalIgnoreCase) ?? false) ?? tokens.FirstOrDefault();
            if (mainTokenEntry == null) {
                Console.WriteLine("Tidak ada token utama.");
                return;
            }
            
            if (!mainTokenEntry.Username?.Equals(owner, StringComparison.OrdinalIgnoreCase) ?? true) {
                Console.WriteLine($"Warning: Token utama bukan owner (@{mainTokenEntry.Username} != @{owner})");
            }

            var usersToInvite = tokens
                .Where(t => t.Username != null && !t.Username.Equals(owner, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Username!)
                .Distinct()
                .ToList();

            if (!usersToInvite.Any()) {
                Console.WriteLine("Tidak ada user untuk diundang.");
                return;
            }

            int success = 0, alreadyInvited = 0, failed = 0;
            
            await AnsiConsole.Progress()
                .Columns(new ProgressColumn[] {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn(),
                })
                .StartAsync(async ctx => {
                    var task = ctx.AddTask("[green]Undang...[/]", new ProgressTaskSettings { MaxValue = usersToInvite.Count });
                    using var client = TokenManager.CreateHttpClient(mainTokenEntry);
                    
                    foreach (var username in usersToInvite) {
                        cancellationToken.ThrowIfCancellationRequested();
                        task.Description = $"[green]Undang:[/] @{username}";
                        
                        string checkUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{username}";
                        try {
                            var checkResponse = await client.GetAsync(checkUrl, cancellationToken);
                            if (checkResponse.IsSuccessStatusCode) {
                                Console.WriteLine($"@{username} sudah kolaborator");
                                alreadyInvited++;
                                task.Increment(1);
                                await Task.Delay(500, cancellationToken);
                                continue;
                            }
                        } catch (Exception ex) {
                            Console.WriteLine($"Error cek @{username}: {ex.Message}");
                            failed++;
                            task.Increment(1);
                            await Task.Delay(500, cancellationToken);
                            continue;
                        }
                        
                        string inviteUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{username}";
                        var payload = new { permission = "push" };
                        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                        
                        try {
                            var response = await client.PutAsync(inviteUrl, content, cancellationToken);
                            if (response.StatusCode == HttpStatusCode.Created) {
                                Console.WriteLine($"OK - Undang @{username}");
                                success++;
                            } else if (response.StatusCode == HttpStatusCode.NoContent) {
                                Console.WriteLine($"@{username} sudah kolaborator");
                                alreadyInvited++;
                            } else {
                                Console.WriteLine($"FAIL - Undang @{username} ({response.StatusCode})");
                                failed++;
                            }
                        }
                        catch (OperationCanceledException) {
                            throw;
                        }
                        catch (Exception ex) {
                            Console.WriteLine($"Exception - Undang @{username}: {ex.Message}");
                            failed++;
                        }
                        
                        task.Increment(1);
                        await Task.Delay(1000, cancellationToken);
                    }
                });
            
            Console.WriteLine($"Selesai. Terkirim: {success}, Sudah: {alreadyInvited}, Gagal: {failed}");
        }

        public static async Task AcceptInvitations(CancellationToken cancellationToken = default)
        {
            Console.WriteLine("--- 3. Terima Undangan ---");
            var tokens = TokenManager.GetAllTokenEntries();
            if (!tokens.Any()) {
                Console.WriteLine("Tidak ada token.");
                return;
            }
            
            var owner = tokens.First().Owner;
            var repo = tokens.First().Repo;
            string targetRepo = $"{owner}/{repo}".ToLower();
            Console.WriteLine($"Target: {targetRepo}");

            int accepted = 0, alreadyMember = 0, noInvitation = 0, failed = 0;
            
            await AnsiConsole.Progress()
                .Columns(new ProgressColumn[] {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn(),
                })
                .StartAsync(async ctx => {
                    var task = ctx.AddTask("[green]Accept...[/]", new ProgressTaskSettings { MaxValue = tokens.Count });
                    
                    foreach (var entry in tokens) {
                        cancellationToken.ThrowIfCancellationRequested();
                        var tokenDisplay = TokenManager.MaskToken(entry.Token);
                        task.Description = $"[green]Cek:[/] {entry.Username ?? tokenDisplay}";
                        
                        if (entry.Username?.Equals(owner, StringComparison.OrdinalIgnoreCase) ?? false) {
                            task.Increment(1);
                            continue;
                        }
                        
                        try {
                            using var client = TokenManager.CreateHttpClient(entry);
                            string checkUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{entry.Username}";
                            
                            try {
                                var checkResponse = await client.GetAsync(checkUrl, cancellationToken);
                                if (checkResponse.IsSuccessStatusCode) {
                                    Console.WriteLine($"{entry.Username ?? "user"} sudah member");
                                    alreadyMember++;
                                    task.Increment(1);
                                    await Task.Delay(500, cancellationToken);
                                    continue;
                                }
                            } catch {
                                // Abaikan error cek
                            }
                            
                            var response = await client.GetAsync("https://api.github.com/user/repository_invitations", cancellationToken);
                            if (!response.IsSuccessStatusCode) {
                                Console.WriteLine($"FAIL cek invitation {entry.Username ?? "user"}");
                                failed++;
                                task.Increment(1);
                                continue;
                            }
                            
                            var invitations = await response.Content.ReadFromJsonAsync<List<GitHubInvitation>>(cancellationToken: cancellationToken);
                            var targetInvite = invitations?.FirstOrDefault(inv => inv.Repository?.FullName?.Equals(targetRepo, StringComparison.OrdinalIgnoreCase) ?? false);
                            
                            if (targetInvite != null) {
                                Console.Write($"{entry.Username ?? "user"} accepting... ");
                                string acceptUrl = $"https://api.github.com/user/repository_invitations/{targetInvite.Id}";
                                var patchResponse = await client.PatchAsync(acceptUrl, null, cancellationToken);
                                
                                if (patchResponse.IsSuccessStatusCode) {
                                    Console.WriteLine("OK");
                                    accepted++;
                                } else {
                                    Console.WriteLine($"FAIL ({patchResponse.StatusCode})");
                                    failed++;
                                }
                            } else {
                                noInvitation++;
                            }
                        }
                        catch (OperationCanceledException) {
                            throw;
                        }
                        catch (Exception ex) {
                            Console.WriteLine($"Exception {entry.Username ?? "user"}: {ex.Message}");
                            failed++;
                        }
                        
                        task.Increment(1);
                        await Task.Delay(1000, cancellationToken);
                    }
                });
            
            Console.WriteLine($"Selesai. Accepted: {accepted}, Sudah: {alreadyMember}, Tidak ada: {noInvitation}, Gagal: {failed}");
        }

        // Kelas DTO (tetap sama)
        private class GitHubUser {
            [JsonPropertyName("login")]
            public string? Login { get; set; }
        }
        
        private class GitHubError {
            [JsonPropertyName("message")]
            public string? Message { get; set; }
        }
        
        private class GitHubInvitation {
            [JsonPropertyName("id")]
            public long Id { get; set; }
            
            [JsonPropertyName("repository")]
            public GitHubRepository? Repository { get; set; }
        }
        
        private class GitHubRepository {
            [JsonPropertyName("full_name")]
            public string? FullName { get; set; }
        }
    }
}
