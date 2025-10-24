using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

/// <summary>
/// Mengelola validasi token, undangan kolaborator, dan penerimaan undangan.
/// Diterjemahkan dari logika setup.py Nexus-Orchestrator.
/// </summary>
public static class CollaboratorManager
{
    public static async Task ValidateAllTokens()
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 1. Validasi Token & Ambil Usernames ---[/]");
        var (tokens, owner, repo) = TokenManager.GetAllTokenEntries();
        if (!tokens.Any()) return;

        var cache = TokenManager.GetUsernameCache();
        int newUsers = 0;

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

                for (int i = 0; i < tokens.Count; i++)
                {
                    var entry = tokens[i];
                    var tokenDisplay = TokenManager.MaskToken(entry.Token);
                    task.Description = $"[green]Memvalidasi:[/] {tokenDisplay}";

                    if (cache.ContainsKey(entry.Token))
                    {
                        entry.Username = cache[entry.Token];
                        task.Increment(1);
                        continue;
                    }

                    try
                    {
                        using var client = TokenManager.CreateHttpClient(entry);
                        var response = await client.GetAsync("https://api.github.com/user");

                        if (response.IsSuccessStatusCode)
                        {
                            var user = await response.Content.ReadFromJsonAsync<GitHubUser>();
                            if (user?.Login != null)
                            {
                                AnsiConsole.MarkupLine($"[green]✓[/] Token {tokenDisplay} valid untuk [yellow]@{user.Login}[/]");
                                entry.Username = user.Login;
                                cache[entry.Token] = user.Login;
                                newUsers++;
                            }
                        }
                        else
                        {
                            var error = await response.Content.ReadAsStringAsync();
                            AnsiConsole.MarkupLine($"[red]✗[/] Token {tokenDisplay} [red]INVALID:[/] {response.StatusCode}");
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] Token {tokenDisplay} [red]ERROR:[/] {ex.Message}");
                    }
                    task.Increment(1);
                    await Task.Delay(500); // Rate limit ringan
                }
            });

        TokenManager.SaveUsernameCache(cache);
        AnsiConsole.MarkupLine($"[green]✓ Validasi selesai. {newUsers} username baru ditambahkan ke cache.[/]");
    }

    public static async Task InviteCollaborators()
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 2. Undang Kolaborator ---[/]");
        var (tokens, owner, repo) = TokenManager.GetAllTokenEntries();
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            AnsiConsole.MarkupLine("[red]Owner/Repo utama tidak di-set di github_tokens.txt[/]");
            return;
        }

        var mainTokenEntry = tokens.FirstOrDefault(t => t.Username?.Equals(owner, StringComparison.OrdinalIgnoreCase) ?? false);
        if (mainTokenEntry == null)
        {
            AnsiConsole.MarkupLine($"[red]Token untuk owner '{owner}' tidak ditemukan. Pastikan token owner ada di list & sudah divalidasi (Menu A).[/]");
            return;
        }
        
        AnsiConsole.MarkupLine($"[dim]Menggunakan token [yellow]@{owner}[/] untuk mengundang...[/]");
        using var client = TokenManager.CreateHttpClient(mainTokenEntry);

        var usersToInvite = tokens
            .Where(t => t.Username != null && !t.Username.Equals(owner, StringComparison.OrdinalIgnoreCase))
            .ToList();
            
        if (!usersToInvite.Any())
        {
            AnsiConsole.MarkupLine("[yellow]Tidak ada user (selain owner) untuk diundang.[/]");
            return;
        }

        int success = 0;
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
                foreach (var user in usersToInvite)
                {
                    task.Description = $"[green]Mengundang:[/] [yellow]@{user.Username}[/]";
                    string url = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{user.Username}";
                    var payload = new { permission = "push" };
                    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                    try
                    {
                        var response = await client.PutAsync(url, content);
                        if (response.IsSuccessStatusCode)
                        {
                            AnsiConsole.MarkupLine($"[green]✓[/] Undangan terkirim ke [yellow]@{user.Username}[/]");
                            success++;
                        }
                        else if (response.StatusCode == HttpStatusCode.NoContent)
                        {
                             AnsiConsole.MarkupLine($"[grey]✓[/] [yellow]@{user.Username}[/] sudah menjadi kolaborator.");
                             success++;
                        }
                        else
                        {
                            var error = await response.Content.ReadFromJsonAsync<GitHubError>();
                            AnsiConsole.MarkupLine($"[red]✗[/] Gagal mengundang [yellow]@{user.Username}[/]: {error?.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                         AnsiConsole.MarkupLine($"[red]✗[/] Greal mengundang [yellow]@{user.Username}[/]: {ex.Message}");
                    }
                    task.Increment(1);
                    await Task.Delay(1000); // Rate limit API invite
                }
            });
        
        AnsiConsole.MarkupLine($"[green]✓ Proses undangan selesai. {success}/{usersToInvite.Count} berhasil.[/]");
    }

    public static async Task AcceptInvitations()
    {
        AnsiConsole.MarkupLine("[bold cyan]--- 3. Terima Undangan Kolaborasi ---[/]");
        var (tokens, owner, repo) = TokenManager.GetAllTokenEntries();
        string targetRepo = $"{owner}/{repo}".ToLower();
        AnsiConsole.MarkupLine($"[dim]Target repo:[/] {targetRepo}");

        int accepted = 0;
        int notFound = 0;

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

                    try
                    {
                        using var client = TokenManager.CreateHttpClient(entry);
                        var response = await client.GetAsync("https://api.github.com/user/repository_invitations");

                        if (!response.IsSuccessStatusCode)
                        {
                            AnsiConsole.MarkupLine($"[red]✗[/] Gagal cek undangan untuk {tokenDisplay}");
                            continue;
                        }

                        var invitations = await response.Content.ReadFromJsonAsync<List<GitHubInvitation>>();
                        var targetInvite = invitations?.FirstOrDefault(inv => 
                            inv.Repository?.FullName?.Equals(targetRepo, StringComparison.OrdinalIgnoreCase) ?? false);

                        if (targetInvite != null)
                        {
                            AnsiConsole.MarkupLine($"[yellow]![/] Menemukan undangan {targetRepo} untuk {entry.Username ?? "user"}. Menerima...");
                            string acceptUrl = $"https://api.github.com/user/repository_invitations/{targetInvite.Id}";
                            var patchResponse = await client.PatchAsync(acceptUrl, null);

                            if (patchResponse.IsSuccessStatusCode)
                            {
                                AnsiConsole.MarkupLine($"[green]✓[/] [yellow]@{entry.Username}[/] berhasil menerima undangan.");
                                accepted++;
                            }
                            else
                            {
                                AnsiConsole.MarkupLine($"[red]✗[/] [yellow]@{entry.Username}[/] gagal menerima undangan: {patchResponse.StatusCode}");
                            }
                        }
                        else
                        {
                            notFound++;
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] Error pada {tokenDisplay}: {ex.Message}");
                    }
                    
                    task.Increment(1);
                    await Task.Delay(500); // Rate limit ringan
                }
            });
            
        AnsiConsole.MarkupLine($"[green]✓ Proses selesai. Undangan diterima: {accepted}. Tidak ditemukan: {notFound}.[/]");
    }
    
    // Helper class untuk deserialization
    private class GitHubUser
    {
        [JsonPropertyName("login")]
        public string? Login { get; set; }
    }
    
    private class GitHubError
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private class GitHubInvitation
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
        [JsonPropertyName("repository")]
        public GitHubRepo? Repository { get; set; }
    }
    
    private class GitHubRepo
    {
        [JsonPropertyName("full_name")]
        public string? FullName { get; set; }
    }
}
