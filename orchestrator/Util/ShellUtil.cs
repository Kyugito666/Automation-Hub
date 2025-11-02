using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Core; 
using System.IO; 
using System; 

namespace Orchestrator.Util 
{
    public static class ShellUtil
    {
        private const int DEFAULT_TIMEOUT_MS = 120000;

        // (Fungsi RunCommandAsync, RunInteractive, RunInteractiveWithFullInput tidak berubah)
        #region "Fungsi Lama (Tidak Berubah)"
        public static async Task RunCommandAsync(string command, string args, string? workingDir = null, TokenEntry? token = null)
        {
            var startInfo = CreateStartInfo(command, args, token, useProxy: true);
            if (workingDir != null) startInfo.WorkingDirectory = workingDir;
            
            using var cts = new CancellationTokenSource(DEFAULT_TIMEOUT_MS);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, Program.GetMainCancellationToken());
            try
            {
                var (_, stderr, exitCode) = await RunProcessAsync(startInfo, linkedCts.Token); 
                if (exitCode != 0)
                {
                    throw new Exception($"Command '{command} {args.EscapeMarkup()}' failed (Exit Code: {exitCode}): {stderr.Split('\n').FirstOrDefault()?.Trim().EscapeMarkup()}");
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !Program.GetMainCancellationToken().IsCancellationRequested)
            {
                throw new TimeoutException($"Command '{command} {args.EscapeMarkup()}' timed out after {DEFAULT_TIMEOUT_MS / 1000} seconds.");
            }
        }
        public static async Task RunInteractive(string command, string args, string? workingDir = null, TokenEntry? token = null, CancellationToken cancellationToken = default)
        {
            var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = false }; 
            if (workingDir != null) startInfo.WorkingDirectory = workingDir;
            if (token != null) SetEnvironmentVariables(startInfo, token, command, useProxy: true);
            SetFileNameAndArgs(startInfo, command, args); 
            using var process = new Process { StartInfo = startInfo };
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Program.GetMainCancellationToken());
            try {
                AnsiConsole.MarkupLine($"[dim]Starting interactive: {startInfo.FileName} {startInfo.Arguments.EscapeMarkup()}[/]");
                if (!process.Start()) throw new InvalidOperationException("Failed to start interactive process.");
                using var reg = linkedCts.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { /* Ignored */ } });
                await process.WaitForExitAsync(linkedCts.Token); 
                if (!linkedCts.Token.IsCancellationRequested && process.ExitCode != 0 && process.ExitCode != -1) {
                    AnsiConsole.MarkupLine($"[yellow]Interactive process exited with code: {process.ExitCode}[/]");
                }
            } catch (OperationCanceledException) {
                AnsiConsole.MarkupLine("[yellow]Interactive operation cancelled.[/]");
                try { if (!process.HasExited) process.Kill(true); } catch { /* Ignored */ }
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Error running interactive process: {ex.Message.EscapeMarkup()}[/]");
                try { if (!process.HasExited) process.Kill(true); } catch { /* Ignored */ }
                throw; 
            }
        }
        public static async Task RunInteractiveWithFullInput(string command, string args, string? workingDir = null, TokenEntry? token = null, CancellationToken cancellationToken = default, bool useProxy = true)
        {
            var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = false }; 
            if (workingDir != null) startInfo.WorkingDirectory = workingDir;
            if (token != null) SetEnvironmentVariables(startInfo, token, command, useProxy);
            SetFileNameAndArgs(startInfo, command, args); 
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Program.GetMainCancellationToken());
            var processExitedTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (s, e) => processExitedTcs.TrySetResult(process.ExitCode);
            try {
                AnsiConsole.MarkupLine($"[bold green]▶ Starting Full Interactive Session[/]");
                AnsiConsole.MarkupLine($"[dim]Cmd: {command} {args.EscapeMarkup()}[/]");
                AnsiConsole.MarkupLine($"[dim]Dir: {workingDir?.EscapeMarkup() ?? "current"}[/]");
                if (!TokenManager.IsProxyGloballyEnabled()) 
                    AnsiConsole.MarkupLine($"[dim]Proxy: [bold yellow]OFF (Global)[/]");
                else if (!useProxy)
                    AnsiConsole.MarkupLine($"[dim]Proxy: [bold yellow]OFF (NoProxy Call)[/]");
                AnsiConsole.MarkupLine("[yellow]"+ new string('═', 60) +"[/]");
                if (!process.Start()) throw new InvalidOperationException("Failed to start full interactive process.");
                var cancellationTask = Task.Delay(Timeout.Infinite, linkedCts.Token);
                var processTask = processExitedTcs.Task;
                var completedTask = await Task.WhenAny(processTask, cancellationTask);
                AnsiConsole.MarkupLine("\n[yellow]"+ new string('═', 60) +"[/]");
                if (completedTask == cancellationTask || linkedCts.Token.IsCancellationRequested) {
                    AnsiConsole.MarkupLine("[yellow]Cancellation requested during interactive session. Terminating process...[/]");
                    try {
                        if (!process.HasExited) {
                            process.Kill(true); 
                            await Task.WhenAny(processExitedTcs.Task, Task.Delay(1500));
                        }
                    } catch (InvalidOperationException) { /* Process already exited */ }
                    catch (Exception killEx) { AnsiConsole.MarkupLine($"[red]Error terminating process: {killEx.Message}[/]"); }
                    
                    linkedCts.Token.ThrowIfCancellationRequested();
                }
                int exitCode = await processTask; 
                if (exitCode == 0) {
                    AnsiConsole.MarkupLine($"[green]✓ Process exited normally (Code: {exitCode})[/]");
                } else {
                    AnsiConsole.MarkupLine($"[yellow]Process exited with non-zero code: {exitCode}[/]");
                }
            }
            catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine("[yellow]Interactive session cancelled.[/]");
                 try { if (!process.HasExited) process.Kill(true); } catch { /* Ignored */ }
            }
            catch (Exception ex) {
                AnsiConsole.MarkupLine("\n[yellow]"+ new string('═', 60) +"[/]");
                AnsiConsole.MarkupLine($"[red]✗ Error running full interactive process: {ex.Message.EscapeMarkup()}[/]");
                try { if (!process.HasExited) process.Kill(true); } catch { /* Ignored */ }
                 throw; 
            }
        }
        public static async Task RunProcessWithFileStdinAsync(ProcessStartInfo startInfo, string localFilePath, CancellationToken cancellationToken)
        {
            startInfo.RedirectStandardInput = true; 
            startInfo.RedirectStandardOutput = true; 
            startInfo.RedirectStandardError = true;  
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (s, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };
            process.Exited += (s, e) => tcs.TrySetResult(process.ExitCode);
            using var cancellationRegistration = cancellationToken.Register(() => {
                if (tcs.TrySetCanceled(cancellationToken)) {
                    try { if (!process.HasExited) process.Kill(true); } catch { /* Ignored */ }
                }
            });
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                using (var fileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                {
                    await fileStream.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
                }
                process.StandardInput.Close();
                int exitCode = await tcs.Task; 
                if (exitCode != 0)
                {
                    string stderr = stderrBuilder.ToString().TrimEnd();
                    throw new Exception($"Command '{startInfo.FileName} {startInfo.Arguments.EscapeMarkup()}' failed (Exit Code: {exitCode}): {stderr.Split('\n').FirstOrDefault()?.Trim().EscapeMarkup()}");
                }
            }
            catch (TaskCanceledException ex) { throw new OperationCanceledException("Process run (stdin) was canceled.", ex, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error in RunProcessWithFileStdinAsync: {ex.Message.EscapeMarkup()}[/]");
                try { if (process != null && !process.HasExited) process.Kill(true); } catch { /* Ignored */ }
                throw;
            }
        }
        internal static ProcessStartInfo CreateStartInfo(string command, string args, TokenEntry? token, bool useProxy = true) {
            var startInfo = new ProcessStartInfo {
                 RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            startInfo.Arguments = args; 
            if (token != null) SetEnvironmentVariables(startInfo, token, command, useProxy);
            SetFileNameAndArgs(startInfo, command, args); 
            return startInfo;
        }
        internal static void SetEnvironmentVariables(ProcessStartInfo startInfo, TokenEntry token, string command, bool useProxy = true) {
            bool isGhCommand = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? command.ToLower().EndsWith("gh.exe") || command.ToLower() == "gh"
                : command == "gh";
            if (isGhCommand && !string.IsNullOrEmpty(token.Token)) {
                startInfo.EnvironmentVariables["GH_TOKEN"] = token.Token;
            }
             startInfo.EnvironmentVariables.Remove("https_proxy"); startInfo.EnvironmentVariables.Remove("http_proxy");
             startInfo.EnvironmentVariables.Remove("HTTPS_PROXY"); startInfo.EnvironmentVariables.Remove("HTTP_PROXY");
             startInfo.EnvironmentVariables.Remove("NO_PROXY"); startInfo.EnvironmentVariables.Remove("no_proxy");
            
            if (useProxy && TokenManager.IsProxyGloballyEnabled() && !string.IsNullOrEmpty(token.Proxy)) {
                startInfo.EnvironmentVariables["https_proxy"] = token.Proxy;
                startInfo.EnvironmentVariables["http_proxy"] = token.Proxy;
                startInfo.EnvironmentVariables["HTTPS_PROXY"] = token.Proxy;
                startInfo.EnvironmentVariables["HTTP_PROXY"] = token.Proxy;
            }
        }
        internal static void SetFileNameAndArgs(ProcessStartInfo startInfo, string command, string args) {
             if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = $"/c \"\"{command}\" {args}\""; 
            } else { 
                startInfo.FileName = "/bin/bash";
                string escapedArgs = args.Replace("\"", "\\\""); 
                startInfo.Arguments = $"-c \"{command} {escapedArgs}\""; 
            }
        }
        #endregion

        // === FUNGSI BARU: STREAMING OUTPUT ===
        internal static async Task<(string stdout, string stderr, int exitCode)> RunProcessAndStreamOutputAsync(
            ProcessStartInfo startInfo, 
            CancellationToken cancellationToken,
            Func<string, bool> onStdOutLine)
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdoutBuilder = new StringBuilder(); 
            var stderrBuilder = new StringBuilder();
            var tcs = new TaskCompletionSource<(string, string, int)>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (s, e) => { 
                if (e.Data != null) {
                    bool stop = onStdOutLine(e.Data);
                    stdoutBuilder.AppendLine(e.Data); 
                    if (stop)
                    {
                        try { if (!process.HasExited) process.Kill(true); } catch {}
                    }
                } 
            };
            // GANTI KE:
process.ErrorDataReceived += (s, e) => { 
    if (e.Data != null) {
        // Selalu stream error
        AnsiConsole.MarkupLine($"[red]   [dim]REMOTE_ERR:[/] {e.Data.EscapeMarkup()}[/]"); // <-- PERBAIKAN DI SINI
        stderrBuilder.AppendLine(e.Data);
    } 
};
            process.Exited += (s, e) => tcs.TrySetResult((stdoutBuilder.ToString().TrimEnd(), stderrBuilder.ToString().TrimEnd(), process.ExitCode));
            
            using var cancellationRegistration = cancellationToken.Register(() => { 
                if (tcs.TrySetCanceled(cancellationToken)) { 
                    try { if (!process.HasExited) process.Kill(true); } catch { /* Ignored */ } 
                } 
            });

            try {
                if (!process.Start()) throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
                process.BeginOutputReadLine(); 
                process.BeginErrorReadLine();
                return await tcs.Task; 
            }
            catch (TaskCanceledException ex) { throw new OperationCanceledException("Process run was canceled.", ex, cancellationToken); }
            catch (OperationCanceledException) { throw; } 
            catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Error in RunProcessAndStreamOutputAsync: {ex.Message.EscapeMarkup()}[/]");
                try { if (process != null && !process.HasExited) process.Kill(true); } catch { /* Ignored */ }
                return (stdoutBuilder.ToString().TrimEnd(), (stderrBuilder.ToString().TrimEnd() + "\n" + ex.Message).Trim(), process?.ExitCode ?? -1);
            }
        } 
        // === AKHIR FUNGSI BARU ===

        internal static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdoutBuilder = new StringBuilder(); var stderrBuilder = new StringBuilder();
            var tcs = new TaskCompletionSource<(string, string, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (s, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };
            process.Exited += (s, e) => tcs.TrySetResult((stdoutBuilder.ToString().TrimEnd(), stderrBuilder.ToString().TrimEnd(), process.ExitCode));
            using var cancellationRegistration = cancellationToken.Register(() => { if (tcs.TrySetCanceled(cancellationToken)) { try { if (!process.HasExited) process.Kill(true); } catch { /* Ignored */ } } });
            try {
                if (!process.Start()) throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                return await tcs.Task; 
            }
            catch (TaskCanceledException ex) { throw new OperationCanceledException("Process run was canceled.", ex, cancellationToken); }
            catch (OperationCanceledException) { throw; } 
            catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Error in RunProcessAsync: {ex.Message.EscapeMarkup()}[/]");
                try { if (process != null && !process.HasExited) process.Kill(true); } catch { /* Ignored */ }
                return (stdoutBuilder.ToString().TrimEnd(), (stderrBuilder.ToString().TrimEnd() + "\n" + ex.Message).Trim(), process?.ExitCode ?? -1);
            }
        } 
    } 
}
