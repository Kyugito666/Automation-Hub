using Spectre.Console;
using System.Diagnostics; 
using System.Threading;
using System.Threading.Tasks;
using System; 
using Orchestrator.Services; 
using Orchestrator.Core; 

namespace Orchestrator.Codespace
{
    internal static class CodeHealth
    {
        private const int STATE_POLL_INTERVAL_FAST_MS = 500;
        private const int STATE_POLL_INTERVAL_SLOW_SEC = 3;
        private const int SSH_READY_POLL_INTERVAL_FAST_MS = 500;
        private const int SSH_READY_POLL_INTERVAL_SLOW_SEC = 2;
        private const int SSH_READY_MAX_DURATION_MIN = 8;
        private const int SSH_PROBE_TIMEOUT_MS = 30000;
        private const int HEALTH_CHECK_POLL_INTERVAL_SEC = 10;
        private const int HEALTH_CHECK_MAX_DURATION_MIN = 4;
        private const string HEALTH_CHECK_FILE = "/tmp/auto_start_done";
        private const string HEALTH_CHECK_FAIL_PROXY = "/tmp/auto_start_failed_proxysync";
        private const string HEALTH_CHECK_FAIL_DEPLOY = "/tmp/auto_start_failed_deploy";

        // API Call -> Pake Proxy
        internal static async Task<bool> WaitForState(TokenEntry token, string codespaceName, string targetState, TimeSpan timeout, CancellationToken cancellationToken, bool useFastPolling = false)
        {
            Stopwatch sw = Stopwatch.StartNew(); 
            AnsiConsole.Markup($"[cyan]Waiting state '{targetState}'...[/]");
            int pollIntervalMs = useFastPolling ? STATE_POLL_INTERVAL_FAST_MS : STATE_POLL_INTERVAL_SLOW_SEC * 1000;
            
            while (sw.Elapsed < timeout) {
                cancellationToken.ThrowIfCancellationRequested(); 
                string? state = await CodeActions.GetCodespaceState(token, codespaceName); 
                cancellationToken.ThrowIfCancellationRequested();
                
                if (state == targetState) { 
                    AnsiConsole.MarkupLine($"[green]✓ Reached '{targetState}'[/]"); 
                    return true; 
                } 
                if (state == null || state == "Failed" || state == "Error" || state.Contains("Shutting") || state == "Deleted") { 
                    AnsiConsole.MarkupLine($"[red]✗ Failure state ('{state ?? "Unknown"}')[/]"); 
                    return false; 
                } 
                AnsiConsole.Markup($"[dim].[/]"); 
                try { 
                    await Task.Delay(pollIntervalMs, cancellationToken); 
                } catch (OperationCanceledException) { 
                    AnsiConsole.MarkupLine($"[yellow]Cancelled waiting state[/]"); 
                    throw; 
                }
            }
            AnsiConsole.MarkupLine($"[yellow]Timeout waiting state '{targetState}'[/]");
            return false; 
        }

        // --- PERUBAHAN DI SINI (SSH Call -> No Proxy) ---
        internal static async Task<bool> WaitForSshReadyWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken, bool useFastPolling = false)
        {
            Stopwatch sw = Stopwatch.StartNew(); 
            AnsiConsole.Markup($"[cyan]Waiting SSH...[/]");
            int pollIntervalMs = useFastPolling ? SSH_READY_POLL_INTERVAL_FAST_MS : SSH_READY_POLL_INTERVAL_SLOW_SEC * 1000;
            
            while (sw.Elapsed.TotalMinutes < SSH_READY_MAX_DURATION_MIN) {
                cancellationToken.ThrowIfCancellationRequested(); 
                try {
                    string args = $"codespace ssh -c \"{codespaceName}\" -- echo ready"; 
                    
                    // Panggil NoProxy
                    string res = await GhService.RunGhCommandNoProxyAsync(token, args, SSH_PROBE_TIMEOUT_MS); 
                    
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    if (res != null && res.Contains("ready")) { 
                        AnsiConsole.MarkupLine("[green]✓ SSH Ready[/]"); 
                        return true; 
                    } 
                    AnsiConsole.Markup($"[dim]?[/] ");
                } catch (OperationCanceledException) { 
                    AnsiConsole.MarkupLine($"[yellow]Cancelled waiting SSH[/]"); 
                    throw; 
                }
                catch { AnsiConsole.Markup($"[dim]x[/]"); }
                
                try { 
                    await Task.Delay(pollIntervalMs, cancellationToken); 
                } catch (OperationCanceledException) { 
                    AnsiConsole.MarkupLine($"[yellow]Cancelled waiting SSH[/]"); 
                    throw; 
                }
            }
            AnsiConsole.MarkupLine($"[yellow]Timeout waiting SSH[/]");
            return false; 
        }

        // --- PERUBAHAN DI SINI (SSH Call -> No Proxy) ---
        internal static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
        {
            Stopwatch sw = Stopwatch.StartNew(); 
            AnsiConsole.Markup($"[cyan]Checking health...[/]");
            int successfulSshChecks = 0; 
            const int SSH_STABILITY_THRESHOLD = 2;
            
            while (sw.Elapsed.TotalMinutes < HEALTH_CHECK_MAX_DURATION_MIN) {
                cancellationToken.ThrowIfCancellationRequested(); 
                string result = "";
                try {
                    string args = $"codespace ssh -c \"{codespaceName}\" -- \"if [ -f {HEALTH_CHECK_FAIL_PROXY} ] || [ -f {HEALTH_CHECK_FAIL_DEPLOY} ]; then echo FAILED; elif [ -f {HEALTH_CHECK_FILE} ]; then echo HEALTHY; else echo NOT_READY; fi\"";
                    
                    // Panggil NoProxy
                    result = await GhService.RunGhCommandNoProxyAsync(token, args, SSH_PROBE_TIMEOUT_MS); 
                    
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    if (result.Contains("FAILED")) { 
                        AnsiConsole.MarkupLine($"[red]✗ Script failed[/]"); 
                        return false; 
                    } 
                    if (result.Contains("HEALTHY")) { 
                        AnsiConsole.MarkupLine("[green]✓ Healthy[/]"); 
                        return true; 
                    } 
                    if (result.Contains("NOT_READY")) { 
                        AnsiConsole.Markup($"[dim]_[/]"); 
                        successfulSshChecks++; 
                        if (successfulSshChecks >= SSH_STABILITY_THRESHOLD && sw.Elapsed.TotalMinutes >= 1) { 
                            AnsiConsole.MarkupLine($"[cyan]✓ SSH stable, assuming OK[/]"); 
                            return true; 
                        } 
                    } 
                    else { AnsiConsole.Markup($"[yellow]?[/]"); successfulSshChecks = 0; }
                } catch (OperationCanceledException) { 
                    AnsiConsole.MarkupLine($"[yellow]Cancelled checking health[/]"); 
                    throw; 
                }
                catch { AnsiConsole.Markup($"[red]x[/]"); successfulSshChecks = 0; }
                
                try { 
                    await Task.Delay(HEALTH_CHECK_POLL_INTERVAL_SEC * 1000, cancellationToken); 
                } catch (OperationCanceledException) { 
                    AnsiConsole.MarkupLine($"[yellow]Cancelled checking health[/]"); 
                    throw; 
                }
            }
            AnsiConsole.MarkupLine($"[yellow]Timeout checking health[/]");
            return false; 
        }
    }
}
