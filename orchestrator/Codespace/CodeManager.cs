using Spectre.Console;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System; 
using System.Threading.Tasks; 
using Orchestrator.Services; 
using Orchestrator.Core; 

namespace Orchestrator.Codespace
{
    public static class CodeManager
    {
        private const string CODESPACE_DISPLAY_NAME = "automation-hub-runner";
        private const string MACHINE_TYPE = "standardLinux32gb";
        private const int CREATE_TIMEOUT_MS = 600000;
        private const int STATE_POLL_MAX_DURATION_MIN = 8;
        private const int STATE_POLL_INTERVAL_SLOW_SEC = 3;
        
        public static Task DeleteCodespace(TokenEntry token, string codespaceName) 
            => CodeActions.DeleteCodespace(token, codespaceName);

        public static Task StopCodespace(TokenEntry token, string codespaceName) 
            => CodeActions.StopCodespace(token, codespaceName);

        public static Task TriggerStartupScript(TokenEntry token, string codespaceName)
            => CodeActions.TriggerStartupScript(token, codespaceName);

        // === PERBAIKAN: Hapus referensi ke fungsi polling ===
        // (CheckHealthWithRetry dihapus)
        // === AKHIR PERBAIKAN ===
            
        public static Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName)
            => CodeActions.GetTmuxSessions(token, codespaceName);

        public static async Task<string> EnsureHealthyCodespace(TokenEntry token, string repoFullName, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("\n[cyan]Ensuring Codespace...[/]");
            CodespaceInfo? codespace = null; 
            Stopwatch stopwatch = Stopwatch.StartNew();
            try {
                AnsiConsole.Markup("[dim]Checking repo commit... [/]");
                var repoLastCommit = await CodeActions.GetRepoLastCommitDate(token); 
                cancellationToken.ThrowIfCancellationRequested();
                if (repoLastCommit.HasValue) AnsiConsole.MarkupLine($"[green]OK ({repoLastCommit.Value:yyyy-MM-dd HH:mm} UTC)[/]"); 
                else AnsiConsole.MarkupLine("[yellow]Fetch failed[/]");

                while (stopwatch.Elapsed.TotalMinutes < STATE_POLL_MAX_DURATION_MIN) {
                    cancellationToken.ThrowIfCancellationRequested();
                    AnsiConsole.Markup($"[dim]({stopwatch.Elapsed:mm\\:ss}) Finding CS '{CODESPACE_DISPLAY_NAME}'... [/]");
                    var codespaceList = await CodeActions.ListAllCodespaces(token); 
                    cancellationToken.ThrowIfCancellationRequested();
                    codespace = codespaceList.FirstOrDefault(cs => cs.DisplayName == CODESPACE_DISPLAY_NAME && cs.State != "Deleted");

                    if (codespace == null) { 
                        AnsiConsole.MarkupLine("[yellow]Not found.[/]"); 
                        return await CreateNewCodespace(token, repoFullName, cancellationToken); 
                    }
                    AnsiConsole.MarkupLine($"[green]Found:[/] [blue]{codespace.Name.EscapeMarkup()}[/] [dim]({codespace.State.EscapeMarkup()})[/]");
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    if (repoLastCommit.HasValue && !string.IsNullOrEmpty(codespace.CreatedAt)) {
                        if (DateTime.TryParse(codespace.CreatedAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var csCreated)) {
                            if (repoLastCommit.Value > csCreated) { 
                                AnsiConsole.MarkupLine($"[yellow]⚠ Outdated CS. Deleting...[/]"); 
                                await CodeActions.DeleteCodespace(token, codespace.Name); 
                                codespace = null; 
                                AnsiConsole.MarkupLine("[dim]Waiting 5s...[/]"); 
                                await Task.Delay(5000, cancellationToken); 
                                continue; 
                            }
                        } else AnsiConsole.MarkupLine($"[yellow]Warn: Could not parse CS date '{codespace.CreatedAt.EscapeMarkup()}'[/]");
                    }
                    
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (codespace.State) {
                        case "Available":
                            AnsiConsole.MarkupLine("[cyan]State: Available. Verifying SSH & Uploading...[/]");
                            if (!await CodeHealth.WaitForSshReadyWithRetry(token, codespace.Name, cancellationToken, useFastPolling: false)) { 
                                AnsiConsole.MarkupLine($"[red]SSH failed for {codespace.Name.EscapeMarkup()}. Deleting...[/]"); 
                                await CodeActions.DeleteCodespace(token, codespace.Name); 
                                codespace = null; 
                                break; 
                            }
                            await CodeUpload.UploadCredentialsToCodespace(token, codespace.Name, cancellationToken);
                            
                            // === PERBAIKAN: Ganti Polling jadi Streaming ===
                            AnsiConsole.MarkupLine("[cyan]Triggering startup & streaming logs...[/]");
                            // Kita panggil fungsi streaming yang baru
                            if (await CodeActions.RunStartupScriptAndStreamLogs(token, codespace.Name, cancellationToken)) { 
                                AnsiConsole.MarkupLine("[green]✓ Health OK (script success). Ready.[/]"); 
                                stopwatch.Stop(); 
                                return codespace.Name; 
                            }
                            else { 
                                var lastState = await CodeActions.GetCodespaceState(token, codespace.Name); 
                                AnsiConsole.MarkupLine($"[red]Health failed (script error) & state '{lastState?.EscapeMarkup() ?? "Unknown"}'. Deleting...[/]"); 
                                await CodeActions.DeleteCodespace(token, codespace.Name); 
                                codespace = null; 
                                break; 
                            }
                            // === AKHIR PERBAIKAN ===

                        case "Stopped": case "Shutdown":
                            AnsiConsole.MarkupLine($"[yellow]State: {codespace.State}. Starting...[/]"); 
                            await CodeActions.StartCodespace(token, codespace.Name); 
                            if (!await CodeHealth.WaitForState(token, codespace.Name, "Available", TimeSpan.FromMinutes(4), cancellationToken, useFastPolling: false)) { 
                                AnsiConsole.MarkupLine("[red]Failed start. Deleting...[/]"); 
                                await CodeActions.DeleteCodespace(token, codespace.Name); 
                                codespace = null; 
                                break; 
                            }
                            AnsiConsole.MarkupLine("[green]Started. Re-checking...[/]"); 
                            await Task.Delay(STATE_POLL_INTERVAL_SLOW_SEC * 1000, cancellationToken); 
                            continue;
                        case "Starting": case "Queued": case "Rebuilding": case "Creating":
                            AnsiConsole.MarkupLine($"[yellow]State: {codespace.State}. Waiting {STATE_POLL_INTERVAL_SLOW_SEC}s...[/]"); 
                            await Task.Delay(STATE_POLL_INTERVAL_SLOW_SEC * 1000, cancellationToken); 
                            continue;
                        default: 
                            AnsiConsole.MarkupLine($"[red]Unhealthy state: '{codespace.State.EscapeMarkup()}'. Deleting...[/]"); 
                            await CodeActions.DeleteCodespace(token, codespace.Name); 
                            codespace = null; 
                            break;
                    }
                    if (codespace == null) { 
                        AnsiConsole.MarkupLine("[dim]Waiting 5s...[/]"); 
                        await Task.Delay(5000, cancellationToken); 
                    }
                } 
            } catch (OperationCanceledException) { 
                AnsiConsole.MarkupLine("\n[yellow]EnsureHealthy cancelled.[/]"); 
                stopwatch.Stop(); 
                throw; 
            }
            catch (Exception ex) { 
                stopwatch.Stop(); 
                AnsiConsole.MarkupLine($"\n[red]FATAL EnsureHealthy:[/]"); 
                AnsiConsole.WriteException(ex); 
                if (codespace != null && !string.IsNullOrEmpty(codespace.Name)) { 
                    AnsiConsole.MarkupLine($"[yellow]Deleting broken CS {codespace.Name.EscapeMarkup()}...[/]"); 
                    try { await CodeActions.DeleteCodespace(token, codespace.Name); } catch { } 
                } 
                throw; 
            }
            
            stopwatch.Stop(); 
            AnsiConsole.MarkupLine($"\n[red]FATAL: Reached end of EnsureHealthyCodespace loop unexpectedly.[/]");
            throw new Exception("Reached end of EnsureHealthyCodespace loop unexpectedly."); 
        }

        private static async Task<string> CreateNewCodespace(TokenEntry token, string repoFullName, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine($"\n[cyan]Attempting create new codespace...[/]");

            string createArgs = $"codespace create -R {repoFullName} -m {MACHINE_TYPE} --display-name {CODESPACE_DISPLAY_NAME} --idle-timeout 240m"; 
            Stopwatch createStopwatch = Stopwatch.StartNew(); 
            string newName = "";
            try {
                AnsiConsole.MarkupLine("[dim]Running 'gh codespace create'...[/]"); 
                newName = await GhService.RunGhCommand(token, createArgs, CREATE_TIMEOUT_MS); 
                cancellationToken.ThrowIfCancellationRequested();
                
                if (string.IsNullOrWhiteSpace(newName) || !newName.Contains(CODESPACE_DISPLAY_NAME)) { 
                    AnsiConsole.MarkupLine($"[yellow]WARN: Unexpected 'gh create' output. Fallback list...[/]"); 
                    newName = ""; 
                }
                else { 
                    newName = newName.Trim(); 
                    AnsiConsole.MarkupLine($"[green]✓ Create command OK: {newName.EscapeMarkup()}[/] ({createStopwatch.Elapsed:mm\\:ss})"); 
                }
                
                if (string.IsNullOrWhiteSpace(newName)) { 
                    AnsiConsole.MarkupLine("[dim]Waiting 3s before listing...[/]"); 
                    await Task.Delay(3000, cancellationToken); 
                    var list = await CodeActions.ListAllCodespaces(token); 
                    var found = list.Where(cs => cs.DisplayName == CODESPACE_DISPLAY_NAME).OrderByDescending(cs => cs.CreatedAt).FirstOrDefault(); 
                    if (found == null || string.IsNullOrWhiteSpace(found.Name)) 
                        throw new Exception("gh create failed & fallback list empty"); 
                    newName = found.Name; 
                    AnsiConsole.MarkupLine($"[green]✓ Fallback found: {newName.EscapeMarkup()}[/]"); 
                }
                
                AnsiConsole.MarkupLine("[cyan]Waiting SSH ready...[/]"); 
                if (!await CodeHealth.WaitForSshReadyWithRetry(token, newName, cancellationToken, useFastPolling: true)) 
                    throw new Exception($"SSH to '{newName}' failed"); 
                
                AnsiConsole.MarkupLine("[cyan]Uploading credentials...[/]"); 
                await CodeUpload.UploadCredentialsToCodespace(token, newName, cancellationToken);
                
                // === PERBAIKAN: Ganti Polling jadi Streaming ===
                AnsiConsole.MarkupLine("[cyan]Waiting for remote script to finish (streaming logs)...[/]");
                if (!await CodeActions.RunStartupScriptAndStreamLogs(token, newName, cancellationToken))
                {
                    throw new Exception("Remote script health check failed after create. Check remote logs.");
                }
                AnsiConsole.MarkupLine("[green]✓ Remote script health check passed.[/]");
                // === AKHIR PERBAIKAN ===
                
                createStopwatch.Stop(); 
                AnsiConsole.MarkupLine($"[bold green]✓ New CS '{newName.EscapeMarkup()}' created & initialized.[/] ({createStopwatch.Elapsed:mm\\:ss})"); 
                return newName; 
            } catch (OperationCanceledException) { 
                AnsiConsole.MarkupLine("[yellow]Create cancelled.[/]"); 
                if (!string.IsNullOrWhiteSpace(newName)) { 
                    AnsiConsole.MarkupLine($"[yellow]Cleaning up {newName.EscapeMarkup()}...[/]"); 
                    try { await CodeActions.StopCodespace(token, newName); } catch { } 
                    try { await CodeActions.DeleteCodespace(token, newName); } catch { } 
                } 
                throw; 
            } 
            catch (Exception ex) { 
                createStopwatch.Stop(); 
                AnsiConsole.MarkupLine($"\n[red]ERROR CREATING CODESPACE[/]"); 
                AnsiConsole.WriteException(ex); 
                if (!string.IsNullOrWhiteSpace(newName)) { 
                    AnsiConsole.MarkupLine($"[yellow]Deleting failed CS {newName.EscapeMarkup()}...[/]"); 
                    try { await CodeActions.DeleteCodespace(token, newName); } catch { } 
                } 
                string info = ""; 
                if (ex.Message.Contains("quota")) info = " (Quota?)"; 
                else if (ex.Message.Contains("401") || ex.Message.Contains("credentials")) info = " (Token/Perms?)"; 
                else if (ex.Message.Contains("403")) info = " (Forbidden?)"; 
                throw new Exception($"FATAL: Create failed{info}. Err: {ex.Message}"); 
            }
        }
    }
}
