using Spectre.Console;
using System.Diagnostics; 
using System.Threading;
using System.Threading.Tasks;
using System; 
using Orchestrator.Services; 
using Orchestrator.Core; 
using System.Collections.Generic;
using System.Linq;

namespace Orchestrator.Codespace
{
    internal static class CodeHealth
    {
        private const int STATE_POLL_INTERVAL_FAST_MS = 500;
        private const int STATE_POLL_INTERVAL_SLOW_SEC = 3;
        private const int SSH_READY_POLL_INTERVAL_FAST_MS = 500;
        private const int SSH_READY_POLL_INTERVAL_SLOW_SEC = 2;
        
        private const int SSH_PROBE_TIMEOUT_MS = 30000; 
        
        // Flag file di /tmp
        private const string HEALTH_CHECK_FILE = "/tmp/auto_start_done";
        private const string HEALTH_CHECK_FAIL_PROXY = "/tmp/auto_start_failed_proxysync";
        private const string HEALTH_CHECK_FAIL_DEPLOY = "/tmp/auto_start_failed_deploy";
        
        // Interval polling log
        private const int LOG_POLL_INTERVAL_SEC = 5;


        // (Fungsi WaitForState dan WaitForSshReadyWithRetry tidak berubah)
        #region "Fungsi Lama (Tidak Berubah)"
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
        #endregion

        // === PERBAIKAN: GANTI STREAMING JADI LOG POLLING ===
        internal static async Task<bool> CheckHealthWithRetry(TokenEntry token, string codespaceName, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("[cyan]Attaching to remote log stream...[/]");
            AnsiConsole.MarkupLine($"[dim]   (Polling log every {LOG_POLL_INTERVAL_SEC} seconds... this might take 10-15 mins)[/]");

            // === PERBAIKAN: Hapus .ToLowerInvariant() ===
            string remoteLogFile = $"/workspaces/{token.Repo}/startup.log";
            // === AKHIR PERBAIKAN ===
            
            // Command ini nge-dump semua status dalam satu kali jalan
            string remoteCommand = $@"
cat {remoteLogFile} 2>/dev/null;
echo ""[ORCHESTRATOR_STATUS_CHECK]""
if [ -f {HEALTH_CHECK_FAIL_PROXY} ]; then
    echo ""FAILED_PROXY""
elif [ -f {HEALTH_CHECK_FAIL_DEPLOY} ]; then
    echo ""FAILED_DEPLOY""
elif [ -f {HEALTH_CHECK_FILE} ]; then
    echo ""HEALTHY""
else
    echo ""NOT_READY""
fi
";
            
            string args = $"codespace ssh -c \"{codespaceName}\" -- \"{remoteCommand}\"";
            bool healthResult = false;
            
            // Simpan berapa baris log yang udah kita print
            int linesPrinted = 0;
            bool scriptFinished = false;

            try
            {
                while (!scriptFinished && !cancellationToken.IsCancellationRequested)
                {
                    string fullOutput = "";
                    try
                    {
                        // Panggil GhService versi biasa (bukan streaming)
                        fullOutput = await GhService.RunGhCommand(token, args, SSH_PROBE_TIMEOUT_MS);
                    }
                    catch (OperationCanceledException) { throw; } // Biar ditangkep di luar
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Log poll failed (SSH Error): {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
                        AnsiConsole.MarkupLine($"[dim]   (Retrying in {LOG_POLL_INTERVAL_SEC}s...)[/]");
                    }

                    if (!string.IsNullOrEmpty(fullOutput))
                    {
                        var parts = fullOutput.Split(new[] { "[ORCHESTRATOR_STATUS_CHECK]" }, StringSplitOptions.None);
                        string logContent = parts[0];
                        string status = parts.Length > 1 ? parts[1].Trim() : "NOT_READY";

                        // Logika nge-print log baru
                        var allLines = logContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        if (allLines.Length > linesPrinted)
                        {
                            var newLines = allLines.Skip(linesPrinted);
                            foreach (var line in newLines)
                            {
                                AnsiConsole.MarkupLine($"[grey]   [REMOTE] {line.EscapeMarkup()}[/]");
                            }
                            linesPrinted = allLines.Length; // Update counter
                        }

                        // Logika cek status
                        if (status == "HEALTHY")
                        {
                            AnsiConsole.MarkupLine($"[bold green]✓ Remote script finished successfully.[/]");
                            healthResult = true;
                            scriptFinished = true;
                        }
                        else if (status == "FAILED_PROXY")
                        {
                            AnsiConsole.MarkupLine($"[bold red]✗ Remote script FAILED (ProxySync).[/]");
                            healthResult = false;
                            scriptFinished = true;
                        }
                        else if (status == "FAILED_DEPLOY")
                        {
                            AnsiConsole.MarkupLine($"[bold red]✗ Remote script FAILED (Bot Deploy).[/]");
                            healthResult = false;
                            scriptFinished = true;
                        }
                        else // NOT_READY
                        {
                            // Diem aja, lanjut polling
                        }
                    }
                    
                    if (!scriptFinished)
                    {
                        await Task.Delay(LOG_POLL_INTERVAL_SEC * 1000, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) {
                AnsiConsole.MarkupLine("\n[yellow]Log polling cancelled by user.[/]");
                throw; 
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"\n[red]FATAL: Log polling failed: {ex.Message.EscapeMarkup()}[/]");
                healthResult = false; 
            }

            AnsiConsole.MarkupLine($"[cyan]Log polling finished. Health Status: {(healthResult ? "OK" : "Failed")}[/]");
            return healthResult;
        }
        // === AKHIR PERBAIKAN ===
    }
}
