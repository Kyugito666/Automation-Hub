using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions; 
using System; 
using System.Threading.Tasks;
using Orchestrator.Services; // Menggunakan GhService & TokenManager

namespace Orchestrator.Codespace
{
    internal static class CodeActions
    {
        // Konstanta timeout
        private const int SSH_COMMAND_TIMEOUT_MS = 120000;
        private const int STOP_TIMEOUT_MS = 120000;
        private const int START_TIMEOUT_MS = 300000;
        private const int SSH_PROBE_TIMEOUT_MS = 30000;

        internal static async Task DeleteCodespace(TokenEntry token, string codespaceName)
        {
            AnsiConsole.MarkupLine($"[yellow]Attempting delete codespace '{codespaceName.EscapeMarkup()}'...[/]");
            try { 
                string args = $"codespace delete -c \"{codespaceName}\" --force"; 
                await GhService.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS); 
                AnsiConsole.MarkupLine($"[green]✓ Delete command sent for '{codespaceName.EscapeMarkup()}'.[/]"); 
            }
            catch (Exception ex) { 
                if (ex.Message.Contains("404") || ex.Message.Contains("find")) 
                    AnsiConsole.MarkupLine($"[dim]Codespace '{codespaceName.EscapeMarkup()}' already gone.[/]"); 
                else 
                    AnsiConsole.MarkupLine($"[yellow]Warn: Delete failed: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); 
            }
            await Task.Delay(3000);
        }

        internal static async Task StopCodespace(TokenEntry token, string codespaceName)
        {
            AnsiConsole.Markup($"[dim]Attempting stop codespace '{codespaceName.EscapeMarkup()}'... [/]");
            try { 
                string args = $"codespace stop --codespace \"{codespaceName}\""; 
                await GhService.RunGhCommand(token, args, STOP_TIMEOUT_MS); 
                AnsiConsole.MarkupLine("[green]OK[/]"); 
            }
            catch (Exception ex) { 
                if (ex.Message.Contains("stopped", StringComparison.OrdinalIgnoreCase)) 
                    AnsiConsole.MarkupLine("[dim]Already stopped.[/]"); 
                else 
                    AnsiConsole.MarkupLine($"[yellow]Warn: Stop failed: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); 
            }
            await Task.Delay(2000);
        }

        internal static async Task StartCodespace(TokenEntry token, string codespaceName)
        {
            AnsiConsole.Markup($"[dim]Attempting start codespace '{codespaceName.EscapeMarkup()}'... [/]");
            try { 
                string args = $"codespace start --codespace \"{codespaceName}\""; 
                await GhService.RunGhCommand(token, args, START_TIMEOUT_MS); 
                AnsiConsole.MarkupLine("[green]OK[/]"); 
            }
            catch (Exception ex) { 
                if (ex.Message.Contains("available", StringComparison.OrdinalIgnoreCase)) 
                    AnsiConsole.MarkupLine($"[dim]Already available.[/]"); 
                else 
                    AnsiConsole.MarkupLine($"[yellow]Warn: Start failed: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); 
            }
        }

        internal static async Task TriggerStartupScript(TokenEntry token, string codespaceName)
        {
            AnsiConsole.MarkupLine("[cyan]Triggering remote auto-start.sh script...[/]");
            string repo = token.Repo.ToLower(); string scriptPath = $"/workspaces/{repo}/auto-start.sh";
            AnsiConsole.Markup("[dim]Executing command in background (nohup)... [/]");
            string command = $"nohup bash \"{scriptPath.Replace("\"", "\\\"")}\" > /tmp/startup.log 2>&1 &";
            string args = $"codespace ssh -c \"{codespaceName}\" -- {command}";
            try { 
                await GhService.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); 
                AnsiConsole.MarkupLine("[green]OK[/]"); 
            }
            catch (Exception ex) { 
                AnsiConsole.MarkupLine($"[yellow]Warn: Failed trigger auto-start: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); 
            }
        }

        internal static async Task<List<CodespaceInfo>> ListAllCodespaces(TokenEntry token)
        {
            string args = "codespace list --json name,displayName,state,createdAt";
            try {
                string jsonResult = await GhService.RunGhCommand(token, args);
                if (string.IsNullOrWhiteSpace(jsonResult) || jsonResult == "[]") return new List<CodespaceInfo>();
                try { 
                    return JsonSerializer.Deserialize<List<CodespaceInfo>>(jsonResult, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<CodespaceInfo>(); 
                }
                catch (JsonException jEx) { 
                    AnsiConsole.MarkupLine($"[yellow]Warn: Parse list JSON failed: {jEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); 
                    return new List<CodespaceInfo>(); 
                } 
            } catch (Exception ex) { 
                AnsiConsole.MarkupLine($"[red]Error listing codespaces: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); 
                return new List<CodespaceInfo>(); 
            } 
        }

        internal static async Task<string?> GetCodespaceState(TokenEntry token, string codespaceName)
        {
            try {
                string args = $"codespace view --json state -c \"{codespaceName}\"";
                string json = await GhService.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("state", out var stateProp) ? stateProp.GetString() : null;
            } catch (JsonException jEx) { 
                AnsiConsole.MarkupLine($"[yellow]Warn: Parse state JSON failed: {jEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); 
                return null; 
            } 
            catch (Exception) { return null; } 
        }

        internal static async Task<DateTime?> GetRepoLastCommitDate(TokenEntry token)
        {
            try {
                // Menggunakan TokenManager (file ini belum dipindah, jadi masih OK)
                using var client = TokenManager.CreateHttpClient(token); 
                client.Timeout = TimeSpan.FromSeconds(30);
                var response = await client.GetAsync($"https://api.github.com/repos/{token.Owner}/{token.Repo}/commits?per_page=1");
                if (!response.IsSuccessStatusCode) { 
                    AnsiConsole.MarkupLine($"[yellow]Warn: Fetch commit failed ({response.StatusCode}).[/]"); 
                    return null; 
                } 
                var json = await response.Content.ReadAsStringAsync(); using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) { 
                    AnsiConsole.MarkupLine($"[yellow]Warn: No commits found?[/]"); 
                    return null; 
                } 
                var dateString = doc.RootElement[0].GetProperty("commit").GetProperty("committer").GetProperty("date").GetString();
                if (DateTime.TryParse(dateString, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)) 
                    return dt;
                else { 
                    AnsiConsole.MarkupLine($"[yellow]Warn: Parse commit date failed: {dateString?.EscapeMarkup()}[/]"); 
                    return null; 
                } 
            } catch (JsonException jEx) { 
                AnsiConsole.MarkupLine($"[red]Error parse commit JSON: {jEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); 
                return null; 
            } 
            catch (HttpRequestException httpEx) { 
                AnsiConsole.MarkupLine($"[red]Error fetch commit (Network): {httpEx.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); 
                return null; 
            } 
            catch (Exception ex) { 
                AnsiConsole.MarkupLine($"[red]Error fetch commit: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); 
                return null; 
            } 
        }

        internal static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName)
        {
            AnsiConsole.MarkupLine($"[dim]Fetching tmux sessions...[/]");
            string args = $"codespace ssh -c \"{codespaceName}\" -- tmux list-windows -t automation_hub_bots -F \"#{{window_name}}\"";
            try {
                string result = await GhService.RunGhCommand(token, args, SSH_COMMAND_TIMEOUT_MS);
                return result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(s => s != "dashboard" && s != "bash").OrderBy(s => s).ToList(); 
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Failed fetch tmux: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); AnsiConsole.MarkupLine($"[dim](Normal if new/stopped)[/]");
                return new List<string>(); 
            }
        }
    }

    // Class DTO JSON (dipindah ke sini karena hanya dipakai oleh CodeActions)
    internal class CodespaceInfo 
    { 
        [JsonPropertyName("name")] public string Name { get; set; } = ""; 
        [JsonPropertyName("displayName")] public string DisplayName { get; set; } = ""; 
        [JsonPropertyName("state")] public string State { get; set; } = ""; 
        [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = ""; 
    }
}
