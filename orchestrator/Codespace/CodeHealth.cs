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
        
        private const int SSH_PROBE_TIMEOUT_MS = 30000; 
        
        private const int HEALTH_CHECK_POLL_INTERVAL_SEC = 10;
        private const int HEALTH_CHECK_MAX_DURATION_MIN = 4; // Biarin 4 menit, tapi kita hapus logic asumsi
        private const string HEALTH_CHECK_FILE = "/tmp/auto_start_done";
        private const string HEALTH_CHECK_FAIL_PROXY = "/tmp/auto_start_failed_proxysync";
        private const string HEALTH_CHECK_FAIL_DEPLOY = "/tmp/auto_start_failed_deploy";


        internal static async Task<bool> WaitForState(TokenEntry token, string codespaceName, string targetState, TimeSpan timeout, CancellationToken cancellationToken, bool useFastPolling = false)
        {
            Stopwatch sw = Stopwatch.StartNew(); 
            int pollIntervalMs = useFastPolling ? STATE_POLL_INTERVAL_FAST_MS : STATE_POLL_INTERVAL_SLOW_SEC * 1000;
            
            bool result = false;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"[cyan]Waiting state '{targetState}'...[/]", async ctx => 
                {
                    while (sw.Elapsed < timeout) {
                        cancellationToken.ThrowIfCancellationRequested(); 
                        string? state = await CodeActions.GetCodespaceState(token, codespaceName); 
                        cancellationToken.ThrowIfCancellationRequested();
                        
                        if (state == targetState) { 
                            ctx.Status($"[green]✓ Reached '{targetState}'[/]");
                            result = true; 
                            return; 
                        } 
                        if (state == null || state == "Failed" || state == "Error" || state.Contains("Shutting") || state == "Deleted") { 
                            ctx.Status($"[red]✗ Failure state ('{state ?? "Unknown"}')[/]");
                            result = false; 
                            return; 
                        } 
                        ctx.Status($"[cyan]Waiting state '{targetState}'...[/] [dim]({state})[/]");
                        try { 
                            await Task.Delay(pollIntervalMs, cancellationToken); 
                        } catch (OperationCanceledException) { 
                            ctx.Status($"[yellow]Cancelled waiting state[/]"); 
                            throw; 
                        }
                    }
                    ctx.Status($"[yellow]Timeout waiting state '{targetState}'[/]");
                });

            await Task.Delay(500, CancellationToken.None);
            return result; 
        }

        internal static async Task<bool> WaitForSshReadyWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken, bool useFastPolling = false)
        {
            Stopwatch sw = Stopwatch.StartNew(); 
            int pollIntervalMs = useFastPolling ? SSH_READY_POLL_INTERVAL_FAST_MS : SSH_READY_POLL_INTERVAL_SLOW_SEC * 1000;
            
            bool result = false;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"[cyan]Waiting SSH (infinite ping)...[/]", async ctx => 
                {
                    while (true) 
                    {
                        cancellationToken.ThrowIfCancellationRequested(); 
                        try {
                            string args = $"codespace ssh -c \"{codespaceName}\" -- echo ready"; 
                            string res = await GhService.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); 
                            cancellationToken.ThrowIfCancellationRequested();
                            
                            if (res != null && res.Contains("ready")) { 
                                ctx.Status("[green]✓ SSH Ready[/]"); 
                                result = true;
                                return; // Keluar dari loop
                            } 
                            ctx.Status($"[cyan]Waiting SSH (pending)...[/] [dim]({sw.Elapsed:mm\\:ss})[/]");
                        } catch (OperationCanceledException) { 
                            ctx.Status($"[yellow]Cancelled waiting SSH[/]"); 
                            throw; 
                        }
                        catch (Exception ex) { 
                            ctx.Status($"[yellow]Waiting SSH (retry)...[/] [dim]({ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()})[/]");
                        }
                        
                        try { 
                            await Task.Delay(pollIntervalMs, cancellationToken); 
                        } catch (OperationCanceledException) { 
                            ctx.Status($"[yellow]Cancelled waiting SSH[/]"); 
                            throw; 
                        }
                    }
                });
                
            await Task.Delay(500, CancellationToken.None);
            return result; 
        }

        internal static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
        {
            Stopwatch sw = Stopwatch.StartNew(); 
            // Hapus 'successfulSshChecks', kita gak pake lagi
            
            bool result = false;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"[cyan]Checking health...[/]", async ctx => 
                {
                    while (sw.Elapsed.TotalMinutes < HEALTH_CHECK_MAX_DURATION_MIN) {
                        cancellationToken.ThrowIfCancellationRequested(); 
                        string cmdResult = "";
                        try {
                            string args = $"codespace ssh -c \"{codespaceName}\" -- \"if [ -f {HEALTH_CHECK_FAIL_PROXY} ] || [ -f {HEALTH_CHECK_FAIL_DEPLOY} ]; then echo FAILED; elif [ -f {HEALTH_CHECK_FILE} ]; then echo HEALTHY; else echo NOT_READY; fi\"";
                            
                            cmdResult = await GhService.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS); 
                            cancellationToken.ThrowIfCancellationRequested();
                            
                            if (cmdResult.Contains("FAILED")) { 
                                ctx.Status($"[red]✗ Script failed[/]"); 
                                result = false;
                                return;
                            } 
                            if (cmdResult.Contains("HEALTHY")) { 
                                ctx.Status("[green]✓ Healthy[/]"); 
                                result = true;
                                return;
                            } 
                            if (cmdResult.Contains("NOT_READY")) { 
                                ctx.Status($"[cyan]Checking health... (script not done) ({sw.Elapsed:mm\\:ss})[/]"); 
                                // === PERBAIKAN: HAPUS BLOK "ASSUMING OK" ===
                                // Logic 'successfulSshChecks' dihapus.
                                // Kita HARUS nunggu HEALTHY atau FAILED.
                                // === AKHIR PERBAIKAN ===
                            } 
                            else { 
                                ctx.Status("[yellow]Checking health... (unstable)[/]");
                            }
                        } catch (OperationCanceledException) { 
                            ctx.Status($"[yellow]Cancelled checking health[/]"); 
                            throw; 
                        }
                        catch (Exception ex) { 
                            ctx.Status($"[red]Checking health... (SSH fail)[/] [dim]({ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}) ({sw.Elapsed:mm\\:ss})[/]");
                        }
                        
                        try { 
                            await Task.Delay(HEALTH_CHECK_POLL_INTERVAL_SEC * 1000, cancellationToken); 
                        } catch (OperationCanceledException) { 
                            ctx.Status($"[yellow]Cancelled checking health[/]"); 
                            throw; 
                        }
                    }
                    ctx.Status($"[yellow]Timeout checking health (Loop > {HEALTH_CHECK_MAX_DURATION_MIN}min)[/]");
                });

            await Task.Delay(500, CancellationToken.None);
            return result; 
        }
    }
}
