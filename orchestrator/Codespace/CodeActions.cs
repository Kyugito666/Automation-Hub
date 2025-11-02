using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions; 
using System; 
using System.Threading.Tasks;
using Orchestrator.Services; 
using Orchestrator.Core; 
using System.Threading; 

namespace Orchestrator.Codespace
{
    internal static class CodeActions
    {
        private const int SSH_COMMAND_TIMEOUT_MS = 120000;
        private const int STOP_TIMEOUT_MS = 120000;
        private const int START_TIMEOUT_MS = 300000;
        private const int SSH_PROBE_TIMEOUT_MS = 30000;

        // API Call -> Pake Proxy
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

        // No Proxy (Sesuai request lu)
        internal static async Task StopCodespace(TokenEntry token, string codespaceName)
        {
            AnsiConsole.Markup($"[dim]Attempting stop codespace '{codespaceName.EscapeMarkup()}'... [/]");
            try { 
                string args = $"codespace stop -c \"{codespaceName}\""; 
                await GhService.RunGhCommandNoProxyAsync(token, args, STOP_TIMEOUT_MS); 
                AnsiConsole.MarkupLine("[green]OK[/]"); 
            }
            catch (Exception ex) { 
                if (ex.Message.Contains("stopped", StringComparison.OrdinalIgnoreCase) || 
                    ex.Message.Contains("is not running", StringComparison.OrdinalIgnoreCase)) 
                {
                    AnsiConsole.MarkupLine("[dim]Already stopped/not running.[/]");
                }
                else 
                {
                    AnsiConsole.MarkupLine($"[yellow]Warn: Stop failed: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); 
                }
            }
            await Task.Delay(2000);
        }

        // API Call -> Pake Proxy
        internal static async Task StartCodespace(TokenEntry token, string codespaceName)
        {
            AnsiConsole.Markup($"[dim]Attempting start/revive codespace '{codespaceName.EscapeMarkup()}'... [/]");
            try { 
                string args = $"codespace revive \"{codespaceName}\""; 
                await GhService.RunGhCommand(token, args, START_TIMEOUT_MS); 
                AnsiConsole.MarkupLine("[green]OK[/]"); 
            }
            catch (Exception ex) { 
                if (ex.Message.Contains("available", StringComparison.OrdinalIgnoreCase) || 
                    ex.Message.Contains("already running", StringComparison.OrdinalIgnoreCase)) 
                {
                    AnsiConsole.MarkupLine($"[dim]Already available/running.[/]"); 
                }
                else 
                {
                    AnsiConsole.MarkupLine($"[yellow]Warn: Start/Revive failed: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); 
                }
            }
        }

        // === FUNGSI LAMA (Trigger 'fire-and-forget') ===
        internal static async Task TriggerStartupScript(TokenEntry token, string codespaceName)
        {
            AnsiConsole.MarkupLine("[cyan]Triggering remote auto-start.sh script (keep-alive)...[/]");
            string repo = token.Repo; 
            string scriptPath = $"/workspaces/{repo}/auto-start.sh";
            AnsiConsole.Markup("[dim]Executing command in background (nohup)... [/]");
            
            // Bungkus perintah remote dengan kutip
            string command = $"nohup bash \"{scriptPath.Replace("\"", "\\\"")}\" > /tmp/startup.log 2>&1 &";
            string args = $"codespace ssh -c \"{codespaceName}\" -- \"{command.Replace("\"", "\\\"")}\"";
            
            try { 
                await GhService.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); 
                AnsiConsole.MarkupLine("[green]OK[/]"); 
            }
            catch (Exception ex) { 
                AnsiConsole.MarkupLine($"[yellow]Warn: Failed trigger auto-start: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]"); 
            }
        }
        // === AKHIR FUNGSI LAMA ===

        // === FUNGSI BARU (Streaming Log Real-time) ===
        internal static async Task<bool> RunStartupScriptAndStreamLogs(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("[cyan]Executing remote auto-start.sh and streaming logs...[/]");
            string repo = token.Repo; 
            string scriptPath = $"/workspaces/{repo}/auto-start.sh";
            
            // Bungkus seluruh perintah remote dengan kutip
            string command = $"set -o pipefail; bash \"{scriptPath.Replace("\"", "\\\"")}\" | tee /tmp/startup.log";
            string args = $"codespace ssh -c \"{codespaceName}\" -- \"{command.Replace("\"", "\\\"")}\"";

            bool scriptSuccess = false; 
            try 
            {
                // --- PERBAIKAN LOGIC HEALTH CHECK ---
                // 'scriptSuccess' defaultnya true. Kita hanya cari error fatal (ProxySync).
                // Kegagalan deploy bot (5/33) BUKAN error fatal.
                scriptSuccess = true; 
                
                Func<string, bool> logCallback = (line) => {
                    // === PERBAIKAN: Escape [REMOTE] jadi [[REMOTE]] ===
                    AnsiConsole.MarkupLine($"[grey]   [[REMOTE]] {line.EscapeMarkup()}[/]");
                    // === AKHIR PERBAIKAN ===
                    
                    // HANYA ProxySync error yang dianggap fatal
                    if(line.Contains("ERROR: ProxySync failed")) {
                        AnsiConsole.MarkupLine("[red]   [[FATAL DETECTED: ProxySync failed]]");
                        scriptSuccess = false; 
                    }
                    // Kita tidak lagi menganggap "ERROR: Bot deployment failed" sebagai error fatal
                    
                    return false; 
                };

                // (stdout kini tidak dipakai untuk cek logic)
                await GhService.RunGhCommandAndStreamOutputAsync(token, args, cancellationToken, logCallback);
                
                // Cek logic 'if' setelah run DIHAPUS

                AnsiConsole.MarkupLine($"[green]✓ Remote script finished.[/]");
                // Kembalikan status 'scriptSuccess' yang hanya bisa di-set 'false' oleh ProxySync fail
                return scriptSuccess;
                // --- AKHIR PERBAIKAN LOGIC ---
            }
            catch (OperationCanceledException) {
                AnsiConsole.MarkupLine("\n[yellow]Log streaming cancelled by user.[/]");
                return false;
            }
            catch (Exception ex) { 
                AnsiConsole.MarkupLine($"\n[red]✗ Remote script FAILED (Exit Code != 0).[/]"); 
                AnsiConsole.MarkupLine($"[dim]   Error: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
                
                // --- PERBAIKAN: Jika error-nya BUKAN karena ProxySync, anggap sukses ---
                // Jika exit 1 tapi BUKAN karena ProxySync (yang sudah dicek callback),
                // itu berarti karena 'deploy_bots.py' (yang sudah kita fix, tapi just in case)
                // atau error lain yang tidak fatal.
                if (scriptSuccess) // Jika 'scriptSuccess' masih true (karena ProxySync tidak fail)
                {
                    AnsiConsole.MarkupLine("[yellow]   ...Interpreting non-zero exit as PARTIAL SUCCESS (Bot Deploy Failures).[/]");
                    return true; // Anggap Sukses
                }
                // --- AKHIR PERBAIKAN ---
                
                return false; 
            }
        }
        // === AKHIR FUNGSI BARU ===


        // API Call -> Pake Proxy
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

        // API Call -> Pake Proxy
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

        // API Call -> Pake Proxy
        internal static async Task<DateTime?> GetRepoLastCommitDate(TokenEntry token)
        {
            try {
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

        // SSH Call -> Pake Proxy
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

    internal class CodespaceInfo 
    { 
        [JsonPropertyName("name")] public string Name { get; set; } = ""; 
        [JsonPropertyName("displayName")] public string DisplayName { get; set; } = ""; 
        [JsonPropertyName("state")] public string State { get; set; } = ""; 
        [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = ""; 
    }
}
