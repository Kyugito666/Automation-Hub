using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public class GitHubUser
{
    [JsonPropertyName("login")]
    public string? Login { get; set; }
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

public static class CollaboratorManager
{
    private const int TIMEOUT_SEC = 30;
    private const int MAX_RETRY = 3;
    private const int RETRY_DELAY_MS = 30000;

    public static async Task ValidateAllTokens()
    {
        AnsiConsole.MarkupLine("[cyan]--- Validating Tokens & Getting Usernames ---[/]");
        var tokens = TokenManager.GetAllTokens();
        var cache = TokenManager.GetCache();
        int newUsers = 0;

        foreach (var token in tokens)
        {
            var display = TokenManager.MaskToken(token.Token);

            if (!string.IsNullOrEmpty(token.Username))
            {
                AnsiConsole.MarkupLine($"[dim]⏭️  {display} → @{token.Username} (cached)[/]");
                continue;
            }

            if (cache.TryGetValue(token.Token, out var cachedUsername))
            {
                token.Username = cachedUsername;
                AnsiConsole.MarkupLine($"[dim]⏭️  {display} → @{cachedUsername} (cache file)[/]");
                continue;
            }

            bool success = false;
            for (int retry = 0; retry < MAX_RETRY && !success; retry++)
            {
                try
                {
                    using var client = TokenManager.CreateHttpClient(token);
                    var response = await client.GetAsync("https://api.github.com/user");

                    if (response.IsSuccessStatusCode)
                    {
                        var user = await response.Content.ReadFromJsonAsync<GitHubUser>();
                        if (user?.Login != null)
                        {
                            AnsiConsole.MarkupLine($"[green]✓[/] {display} → [yellow]@{user.Login}[/]");
                            token.Username = user.Login;
                            cache[token.Token] = user.Login;
                            newUsers++;
                            success = true;
                        }
                    }
                    else if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] {display}: INVALID TOKEN (401)");
                        break;
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] {display}: {response.StatusCode}");
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                    if (retry < MAX_RETRY - 1)
                    {
                        AnsiConsole.MarkupLine($"[yellow]🔁 Network error (retry {retry + 1}/{MAX_RETRY})[/]");
                        await Task.Delay(RETRY_DELAY_MS);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]💀 {display} FAILED after {MAX_RETRY} retries[/]");
                    }
                }
                catch (TaskCanceledException)
                {
                    if (retry < MAX_RETRY - 1)
                    {
                        AnsiConsole.MarkupLine($"[yellow]🔁 Timeout (retry {retry + 1}/{MAX_RETRY})[/]");
                        await Task.Delay(RETRY_DELAY_MS);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]💀 {display} TIMEOUT[/]");
                    }
                }
            }

            await Task.Delay(300);
        }

        if (newUsers > 0) TokenManager.SaveCache();
        AnsiConsole.MarkupLine($"[green]✓ Done. {newUsers} new usernames.[/]");
    }

    public static async Task InviteCollaborators()
    {
        AnsiConsole.MarkupLine("[cyan]--- Inviting Collaborators ---[/]");
        var tokens = TokenManager.GetAllTokens();
        if (!tokens.Any()) return;

        var owner = tokens.First().Owner;
        var repo = tokens.First().Repo;

        var mainToken = tokens.FirstOrDefault(t => t.Username?.Equals(owner, StringComparison.OrdinalIgnoreCase) ?? false)
                        ?? tokens.First();

        var usersToInvite = tokens
            .Where(t => t.Username != null && !t.Username.Equals(owner, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Username!)
            .Distinct()
            .ToList();

        if (!usersToInvite.Any())
        {
            AnsiConsole.MarkupLine("[yellow]⚠️  No users to invite.[/]");
            return;
        }

        int success = 0, alreadyInvited = 0, failed = 0;

        using var client = TokenManager.CreateHttpClient(mainToken);

        foreach (var username in usersToInvite)
        {
            string checkUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{username}";
            var checkResponse = await client.GetAsync(checkUrl);

            if (checkResponse.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[grey]✓[/] @{username} already collaborator");
                alreadyInvited++;
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
                    AnsiConsole.MarkupLine($"[green]✓[/] Invitation sent → @{username}");
                    success++;
                }
                else if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    AnsiConsole.MarkupLine($"[grey]✓[/] @{username} already collaborator");
                    alreadyInvited++;
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] @{username}: {response.StatusCode}");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] @{username}: {ex.Message}");
                failed++;
            }

            await Task.Delay(1000);
        }

        AnsiConsole.MarkupLine($"[green]✓ Done.[/] Sent: {success}, Already: {alreadyInvited}, Failed: {failed}");
    }

    public static async Task AcceptInvitations()
    {
        AnsiConsole.MarkupLine("[cyan]--- Accepting Invitations ---[/]");
        var tokens = TokenManager.GetAllTokens();
        if (!tokens.Any()) return;

        var owner = tokens.First().Owner;
        var repo = tokens.First().Repo;
        string targetRepo = $"{owner}/{repo}".ToLower();

        int accepted = 0, alreadyMember = 0, noInvitation = 0, failed = 0;

        foreach (var token in tokens)
        {
            var display = token.Username ?? TokenManager.MaskToken(token.Token);

            if (token.Username?.Equals(owner, StringComparison.OrdinalIgnoreCase) ?? false)
                continue;

            try
            {
                using var client = TokenManager.CreateHttpClient(token);

                string checkUrl = $"https://api.github.com/repos/{owner}/{repo}/collaborators/{token.Username}";
                var checkResponse = await client.GetAsync(checkUrl);

                if (checkResponse.IsSuccessStatusCode)
                {
                    AnsiConsole.MarkupLine($"[green]✓[/] {display} already collaborator");
                    alreadyMember++;
                    await Task.Delay(500);
                    continue;
                }

                var response = await client.GetAsync("https://api.github.com/user/repository_invitations");
                if (!response.IsSuccessStatusCode)
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] {display}: {response.StatusCode}");
                    failed++;
                    continue;
                }

                var invitations = await response.Content.ReadFromJsonAsync<List<GitHubInvitation>>();
                var targetInvite = invitations?.FirstOrDefault(inv =>
                    inv.Repository?.FullName?.Equals(targetRepo, StringComparison.OrdinalIgnoreCase) ?? false);

                if (targetInvite != null)
                {
                    string acceptUrl = $"https://api.github.com/user/repository_invitations/{targetInvite.Id}";
                    var patchResponse = await client.PatchAsync(acceptUrl, null);

                    if (patchResponse.IsSuccessStatusCode)
                    {
                        AnsiConsole.MarkupLine($"[green]✓[/] {display} accepted invitation");
                        accepted++;
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] {display}: {patchResponse.StatusCode}");
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
                AnsiConsole.MarkupLine($"[red]✗[/] {display}: {ex.Message}");
                failed++;
            }

            await Task.Delay(1000);
        }

        AnsiConsole.MarkupLine($"[green]✓ Done.[/] Accepted: {accepted}, Already: {alreadyMember}, None: {noInvitation}, Failed: {failed}");
    }
}
