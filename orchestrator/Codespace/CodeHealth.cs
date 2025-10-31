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
        // private const int SSH_READY_MAX_DURATION_MIN = 8; // <-- DIBUANG
        
        // Timeout ini *diteruskan* ke GhService, tapi GhService akan mengabaikannya
        // untuk command 'ssh' (sesuai fix di atas).
        private const int SSH_PROBE_TIMEOUT_MS = 30000; 
        
        private const int HEALTH_CHECK_POLL_INTERVAL_SEC = 10;
        private const int HEALTH_CHECK_MAX_DURATION_MIN = 4;
        private const string HEALTH_CHECK_FILE = "/tmp/auto_start_done";
        private const string HEALTH_CHECK_FAIL_PROXY = "/tmp/auto_start_failed_proxysync";
        private const string HEALTH_CHECK_FAIL_DEPLOY = "/tmp/auto_start_failed_deploy";


        // --- PERBAIKAN: Ganti "..." dengan Spinner ---
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
        // --- AKHIR PERBAIKAN SPINNER ---


        // --- PERBAIKAN: (SSH Call -> HARUS Pake Proxy) ---
        internal static async Task<bool> WaitForSshReadyWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken, bool useFastPolling = false)
        {
            Stopwatch sw = Stopwatch.StartNew(); 
            int pollIntervalMs = useFastPolling ? SSH_READY_POLL_INTERVAL_FAST_MS : SSH_READY_POLL_INTERVAL_SLOW_SEC * 1000;
            
            bool result = false;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"[cyan]Waiting SSH (infinite ping)...[/]", async ctx => 
                {
                    // === INI PERBAIKANNYA (Infinite Loop) ===
                    // Loop ini hanya akan berhenti jika 'ready' atau di-cancel (Ctrl+C)
                    while (true) 
                    // === AKHIR PERBAIKAN ===
                    {
                        cancellationToken.ThrowIfCancellationRequested(); 
                        try {
                            string args = $"codespace ssh -c \"{codespaceName}\" -- echo ready"; 
                            
                            // Panggil RunGhCommand. 
                            // GhService (sesuai fix di atas) tidak akan memberlakukan
                            // timeout pada command 'ssh' ini.
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
                    // Baris ini tidak akan pernah tercapai
                    // ctx.Status($"[yellow]Timeout waiting SSH[/]");
                });
                
            await Task.Delay(500, CancellationToken.None);
            return result; 
        }
        // --- AKHIR PERBAIKAN ---


        // --- PERBAIKAN: (SSH Call -> HARUS Pake Proxy) ---
        internal static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
        {
            Stopwatch sw = Stopwatch.StartNew(); 
            int successfulSshChecks = 0; 
            const int SSH_STABILITY_THRESHOLD = 2;
            
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
                            
                            // GhService tidak akan men-timeout command ini
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
                                ctx.Status("[cyan]Checking health... (script not done)[/]"); 
                                successfulSshChecks++; 
                                if (successfulSshChecks >= SSH_STABILITY_THRESHOLD && sw.Elapsed.TotalMinutes >= 1) { 
                                    ctx.Status($"[cyan]✓ SSH stable, assuming OK[/]"); 
                                    result = true;
                                    return;
                                } 
                            } 
                            else { 
                                ctx.Status("[yellow]Checking health... (unstable)[/]");
                                successfulSshChecks = 0; 
                            }
                        } catch (OperationCanceledException) { 
                            ctx.Status($"[yellow]Cancelled checking health[/]"); 
                            throw; 
                        }
                        catch (Exception ex) { 
                            ctx.Status($"[red]Checking health... (SSH fail)[/] [dim]({ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()})[/]");
                            successfulSshChecks = 0; 
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
        // --- AKHIR PERBAIKAN ---
    }
}
