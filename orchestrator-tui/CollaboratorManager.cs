using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

/// <summary>
/// Mengelola validasi token, undangan kolaborator, dan penerimaan undangan.
/// Penting untuk setup awal PAT sebelum bisa dipakai CodespaceManager.
/// </summary>
public static class CollaboratorManager
{
    public static async Task ValidateAllTokens()
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 1. Validasi Token & Ambil Usernames ---[/]");
        var tokens = TokenManager.GetAllTokenEntries(); // Hanya butuh list TokenEntry
        if (!tokens.Any()) 
        {
             AnsiConsole.MarkupLine("[yellow]Tidak ada token di config/github_tokens.txt.[/]");
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
                var task = ctx.AddTask("[green]Memvalidasi token...[/]", new ProgressTaskSettings { MaxValue = tokens.Count });

                foreach (var entry in tokens)
                {
                    var tokenDisplay = TokenManager.MaskToken(entry.Token);
                    task.Description = $"[green]Memvalidasi:[/] {tokenDisplay}";

                    // Jika username sudah ada di entry (dari cache saat init), skip API call
                    if (!string.IsNullOrEmpty(entry.Username))
                    {
                        task.Increment(1);
                        continue; 
                    }
                    
                    // Jika belum ada di entry TAPI ada di cache, update entry & skip API call
                     if (cache.TryGetValue(entry.Token, out var cachedUsername))
                     {
                        entry.Username = cachedUsername;
                        task.Increment(1);
                        continue;
                     }

                    // Jika tidak ada sama sekali, panggil API
                    try
                    {
                        // Penting: Gunakan HttpClient DENGAN proxy dari entry ini
                        using var client = TokenManager.CreateHttpClient(entry); 
                        var response = await client.GetAsync("https://api.github.com/user");

                        if (response.IsSuccessStatusCode)
                        {
                            var user = await response.Content.ReadFromJsonAsync<GitHubUser>();
                            if (user?.Login != null)
                            {
                                AnsiConsole.MarkupLine($"[green]✓[/] Token {tokenDisplay} valid untuk [yellow]@{user.Login}[/]");
                                entry.Username = user.Login;
                                cache[entry.Token] = user.Login; // Update cache
                                newUsers++;
                                cacheUpdated = true;
                            } else {
                                 AnsiConsole.MarkupLine($"[red]✗[/] Token {tokenDisplay} [red]INVALID:[/] Respons API tidak valid.");
                            }
                        }
                        else
                        {
                            var error = await response.Content.ReadAsStringAsync();
                            AnsiConsole.MarkupLine($"[red]✗[/] Token {tokenDisplay} [red]INVALID:[/] {response.StatusCode} - {error.Split('\n').FirstOrDefault()}");
                        }
                    }
                    catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.Unauthorized || httpEx.StatusCode == HttpStatusCode.Forbidden)
                    {
                         AnsiConsole.MarkupLine($"[red]✗[/] Token {tokenDisplay} [red]INVALID:[/] {httpEx.StatusCode}");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] Token {tokenDisplay} [red]ERROR:[/] {ex.Message.Split('\n').FirstOrDefault()}");
                    }
                    task.Increment(1);
                    await Task.Delay(200); // Rate limit ringan
                }
            });

        // Simpan cache jika ada perubahan
        if (cacheUpdated)
        {
            TokenManager.SaveTokenCache(cache);
        }
        
        AnsiConsole.MarkupLine($"[green]✓ Validasi selesai. {newUsers} username baru divalidasi/ditambahkan ke cache.[/]");
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
            AnsiConsole.MarkupLine("[red]Owner/Repo utama tidak di-set di config/github_tokens.txt (Baris 1 & 2)[/]");
            return;
        }

        // Cari token milik owner utama
        var mainTokenEntry = tokens.FirstOrDefault(t => t.Username?.Equals(owner, StringComparison.OrdinalIgnoreCase) ?? false);
        if (mainTokenEntry == null)
        {
            // Coba pakai token pertama jika owner tidak ada di list (misal token owner tidak di-list)
            mainTokenEntry = tokens.FirstOrDefault();
             if (mainTokenEntry == null) {
                  AnsiConsole.MarkupLine($"[red]Tidak ada token sama sekali untuk mengundang.[/]");
                  return;
             }
            AnsiConsole.MarkupLine($"[yellow]Token untuk owner '{owner}' tidak ditemukan. Mencoba mengundang pakai token pertama (@{mainTokenEntry.Username ?? "???"})...[/]");
        } else {
             AnsiConsole.MarkupLine($"[dim]Menggunakan token [yellow]@{owner}[/] untuk mengundang...[/]");
        }
        
        // Ambil SEMUA username valid dari token list (kecuali owner)
        var usersToInvite = tokens
            .Where(t => t.Username != null && !t.Username.Equals(owner, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Username!)
            .Distinct() // Hindari duplikasi jika username sama punya >1 token
            .ToList();
            
        if (!usersToInvite.Any())
        {
            AnsiConsole.MarkupLine("[yellow]Tidak ada user valid (selain owner) untuk diundang.[/]");
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
                var task = ctx.AddTask("[green]Mengirim undangan...[/]", new ProgressTaskSettings { MaxValue = usersToInvite.Count });
                
                // Gunakan HttpClient dari token owner
                using var client = TokenManager.CreateHttpClient(mainTokenEntry); 

                foreach (var username in usersToInvite)
                {
                    task.Description = $"[green]Mengundang:[/] [yellow]@{username}[/]";
                    string url = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{username}";
                    var payload = new { permission = "push" }; // Permission 'push'
                    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                    try
                    {
                        var response = await client.PutAsync(url, content);
                        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent) // 201 Created atau 204 No Content (sudah ada)
                        {
                            if (response.StatusCode == HttpStatusCode.Created) {
                                AnsiConsole.MarkupLine($"[green]✓[/] Undangan terkirim ke [yellow]@{username}[/]");
                                success++;
                            } else {
                                AnsiConsole.MarkupLine($"[grey]✓[/] [yellow]@{username}[/] sudah menjadi kolaborator.");
                                alreadyInvited++;
                            }
                        }
                        else
                        {
                            var error = await response.Content.ReadFromJsonAsync<GitHubError>();
                            AnsiConsole.MarkupLine($"[red]✗[/] Gagal mengundang [yellow]@{username}[/]: {response.StatusCode} - {error?.Message}");
                            failed++;
                        }
                    }
                    catch (Exception ex)
                    {
                         AnsiConsole.MarkupLine($"[red]✗[/] Exception saat mengundang [yellow]@{username}[/]: {ex.Message}");
                         failed++;
                    }
                    task.Increment(1);
                    await Task.Delay(500); // Rate limit API invite
                }
            });
        
        AnsiConsole.MarkupLine($"[green]✓ Proses undangan selesai.[/] Terkirim: {success}, Sudah ada: {alreadyInvited}, Gagal: {failed}.");
    }

    public static async Task AcceptInvitations()
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 3. Terima Undangan Kolaborasi ---[/]");
        var tokens = TokenManager.GetAllTokenEntries();
         if (!tokens.Any()) return;

        var owner = tokens.First().Owner;
        var repo = tokens.First().Repo;
        string targetRepo = $"{owner}/{repo}".ToLower();
        AnsiConsole.MarkupLine($"[dim]Target repo:[/] {targetRepo}");

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
                var task = ctx.AddTask("[green]Menerima undangan...[/]", new ProgressTaskSettings { MaxValue = tokens.Count });

                foreach (var entry in tokens)
                {
                    var tokenDisplay = TokenManager.MaskToken(entry.Token);
                    task.Description = $"[green]Mengecek:[/] {entry.Username ?? tokenDisplay}";

                    // Jangan proses token owner utama
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
                            AnsiConsole.MarkupLine($"[red]✗[/] Gagal cek undangan untuk {tokenDisplay}: {response.StatusCode}");
                            failed++;
                            continue;
                        }

                        var invitations = await response.Content.ReadFromJsonAsync<List<GitHubInvitation>>();
                        var targetInvite = invitations?.FirstOrDefault(inv => 
                            inv.Repository?.FullName?.Equals(targetRepo, StringComparison.OrdinalIgnoreCase) ?? false);

                        if (targetInvite != null)
                        {
                            AnsiConsole.Markup($"[yellow]![/] Undangan ditemukan untuk {entry.Username ?? "user"}. Menerima... ");
                            string acceptUrl = $"https://api.github.com/user/repository_invitations/{targetInvite.Id}";
                            // Kirim request PATCH KOSONG untuk accept
                            var patchResponse = await client.PatchAsync(acceptUrl, null); 

                            if (patchResponse.IsSuccessStatusCode)
                            {
                                AnsiConsole.MarkupLine("[green]OK[/]");
                                accepted++;
                            }
                            else
                            {
                                AnsiConsole.MarkupLine($"[red]Gagal ({patchResponse.StatusCode})[/]");
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
                        AnsiConsole.MarkupLine($"[red]✗[/] Exception pada {tokenDisplay}: {ex.Message}");
                        failed++;
                    }
                    
                    task.Increment(1);
                    await Task.Delay(200); // Rate limit ringan
                }
            });
            
        AnsiConsole.MarkupLine($"[green]✓ Proses selesai.[/] Diterima: {accepted}, Tidak ditemukan: {notFound}, Gagal Cek/Accept: {failed}.");
    }
    
    // --- Helper class untuk deserialization ---
    private class GitHubUser { [JsonPropertyName("login")] public string? Login { get; set; } }
    private class GitHubError { [JsonPropertyName("message")] public string? Message { get; set; } }
    private class GitHubInvitation { [JsonPropertyName("id")] public long Id { get; set; } [JsonPropertyName("repository")] public GitHubRepo? Repository { get; set; } }
    private class GitHubRepo { [JsonPropertyName("full_name")] public string? FullName { get; set; } }
}
