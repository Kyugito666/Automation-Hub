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
        
        private const string HEALTH_CHECK_FILE = "/tmp/auto_start_done";
        private const string HEALTH_CHECK_FAIL_PROXY = "/tmp/auto_start_failed_proxysync";
        private const string HEALTH_CHECK_FAIL_DEPLOY = "/tmp/auto_start_failed_deploy";

        private const string MAGIC_STRING_HEALTHY = "[ORCHESTRATOR_HEALTH_CHECK:HEALTHY]";
        private const string MAGIC_STRING_FAILED_PROXY = "[ORCHESTRATOR_HEALTH_CHECK:FAILED_PROXY]";
        private const string MAGIC_STRING_FAILED_DEPLOY = "[ORCHESTRATOR_HEALTH_CHECK:FAILED_DEPLOY]";


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
            AnsiConsole.MarkupLine("[cyan]Attaching to remote log stream...[/]");
            AnsiConsole.MarkupLine("[dim]   (Waiting for auto-start.sh to finish... this might take 10-15 mins)[/]");

            string remoteLogFile = $"/workspaces/{token.Repo.ToLowerInvariant()}/startup.log";
            
            string remoteCommand = $@"
touch {remoteLogFile}
# === INI FIX-NYA: 'stdbuf -o0' (unbuffered output) ===
stdbuf -o0 tail -f {remoteLogFile} &
# === AKHIR FIX ===
tail_pid=$!
echo ""[ORCHESTRATOR_MONITOR:STREAM_STARTED]""
while true; do
    if [ -f {HEALTH_CHECK_FAIL_PROXY} ]; then
        echo ""{MAGIC_STRING_FAILED_PROXY}""
        kill $tail_pid 2>/dev/null
        break
    elif [ -f {HEALTH_CHECK_FAIL_DEPLOY} ]; then
        echo ""{MAGIC_STRING_FAILED_DEPLOY}""
        kill $tail_pid 2>/dev/null
        break
    elif [ -f {HEALTH_CHECK_FILE} ]; then
        echo ""{MAGIC_STRING_HEALTHY}""
        kill $tail_pid 2>/dev/null
        break
    fi
    sleep 5
done
";
            
            string args = $"codespace ssh -c \"{codespaceName}\" -- \"{remoteCommand}\"";
            bool healthResult = false;
            bool streamStarted = false;

            Func<string, bool> onStdOut = (line) => {
                string trimmedLine = line.Trim();
                
                if (trimmedLine.Contains(MAGIC_STRING_HEALTHY)) {
                    AnsiConsole.MarkupLine($"[bold green]✓ Remote script finished successfully.[/]");
                    healthResult = true;
                    return true; // Stop streaming
                }
                if (trimmedLine.Contains(MAGIC_STRING_FAILED_PROXY)) {
                    AnsiConsole.MarkupLine($"[bold red]✗ Remote script FAILED (ProxySync).[/]");
                    healthResult = false;
                    return true; // Stop streaming
                }
                if (trimmedLine.Contains(MAGIC_STRING_FAILED_DEPLOY)) {
                    AnsiConsole.MarkupLine($"[bold red]✗ Remote script FAILED (Bot Deploy).[/]");
                    healthResult = false;
                    return true; // Stop streaming
                }
                
                if (trimmedLine.Contains("[ORCHESTRATOR_MONITOR:STREAM_STARTED]")) {
                    streamStarted = true;
                    return false; // Lanjut streaming
                }

                if (streamStarted) {
                    AnsiConsole.MarkupLine($"[grey]   [REMOTE] {line.EscapeMarkup()}[/]");
                }
                return false; // Lanjut streaming
            };

            try
            {
                await GhService.RunGhCommandAndStreamOutputAsync(token, args, cancellationToken, onStdOut);
                
                if (!healthResult && !cancellationToken.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine("[yellow]Stream finished but health status unknown (no magic string detected). Assuming failure.[/]");
                    healthResult = false;
                }
            }
            catch (OperationCanceledException) {
                AnsiConsole.MarkupLine("\n[yellow]Log streaming cancelled by user.[/]");
                throw; 
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"\n[red]FATAL: Log streaming failed: {ex.Message.EscapeMarkup()}[/]");
                healthResult = false; 
            }

            AnsiConsole.MarkupLine($"[cyan]Log stream finished. Health Status: {(healthResult ? "OK" : "Failed")}[/]");
            return healthResult;
        }
    }
}
