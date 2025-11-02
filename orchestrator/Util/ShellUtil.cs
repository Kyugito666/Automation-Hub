using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Core; 
using System.IO; 
using System; 
using System.Linq; // Pastikan ini ada

namespace Orchestrator.Util 
{
    public static class ShellUtil
    {
        private const int DEFAULT_TIMEOUT_MS = 120000;

        // === FUNGSI LAMA (DIBALIKIN BIAR BUILD SUKSES) ===
        #region "Fungsi Lama (Wajib Ada)"
        
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
            SetFileNameAndArgs(startInfo, command, args); // Pake helper lama buat ini
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
                
                // === PERBAIKAN: Lempar lagi biar TUI tahu ===
                throw;
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
            SetFileNameAndArgs(startInfo, command, args); // Pake helper lama buat ini
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
                 
                 // === PERBAIKAN: Lempar lagi biar TUI tahu ===
                 throw;
            }
            catch (Exception ex) {
                AnsiConsole.MarkupLine("\n[yellow]"+ new string('═', 60) +"[/]");
                
                // === PERBAIKAN (LOG 1 & 3): WAJIB ESCAPE EX.MESSAGE ===
                AnsiConsole.MarkupLine($"[red]✗ Error running full interactive process: {ex.Message.EscapeMarkup()}[/]");
                // === AKHIR PERBAIKAN ===
                
                try { if (!process.HasExited) process.Kill(true); } catch { /* Ignored */ }
                 throw; 
            }
        }
        
        // Helper LAMA yang dipake sama RunInteractive dkk.
        internal static void SetEnvironmentVariables(ProcessStartInfo startInfo, TokenEntry token, string command, bool useProxy = true) {
            bool isGhCommand = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? command.ToLower().EndsWith("gh.exe") || command.ToLower() == "gh"
                : command == "gh";
            if (isGhCommand && !string.IsNullOrEmpty(token.Token)) {
                startInfo.EnvironmentVariables["GH_TOKEN"] = token.Token;
            }
             startInfo.EnvironmentVariables.Remove("httpsS_proxy"); 
             startInfo.EnvironmentVariables.Remove("http_proxy");
             startInfo.EnvironmentVariables.Remove("HTTPS_PROXY");
             startInfo.EnvironmentVariables.Remove("HTTP_PROXY");
             startInfo.EnvironmentVariables.Remove("NO_PROXY");
             startInfo.EnvironmentVariables.Remove("no_proxy");
            
            if (useProxy && TokenManager.IsProxyGloballyEnabled() && !string.IsNullOrEmpty(token.Proxy)) {
                startInfo.EnvironmentVariables["https_proxy"] = token.Proxy; 
                startInfo.EnvironmentVariables["http_proxy"] = token.Proxy;
                startInfo.EnvironmentVariables["HTTPS_PROXY"] = token.Proxy;
                startInfo.EnvironmentVariables["HTTP_PROXY"] = token.Proxy;
            }
        }
        
        // Helper LAMA yang dipake sama RunInteractive dkk.
        internal static void SetFileNameAndArgs(ProcessStartInfo startInfo, string command, string args) {
             if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = $"/c \"{command} {args}\"";
            } else { 
                startInfo.FileName = "/bin/bash";
                string escapedArgs = args.Replace("\"", "\\\""); 
                startInfo.Arguments = $"-c \"{command} {escapedArgs}\""; 
            }
        }
        #endregion
        // === AKHIR FUNGSI LAMA ===


        // === FUNGSI BARU (Buat Upload/Streaming) ===
        
        // Helper baru untuk nyari path 'gh'
        private static string? FindExecutablePath(string exeName)
        {
            if (OperatingSystem.IsWindows() && !exeName.EndsWith(".exe"))
            {
                exeName += ".exe";
            }
            string appDir = AppContext.BaseDirectory;
            string appDirPath = Path.Combine(appDir, exeName);
            if (File.Exists(appDirPath))
            {
                return appDirPath;
            }
            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (pathVar != null)
            {
                foreach (var path in pathVar.Split(Path.PathSeparator))
                {
                    try
                    {
                        string fullPath = Path.Combine(path, exeName);
                        if (File.Exists(fullPath))
                        {
                            return fullPath;
                        }
                    }
                    catch (Exception) { /* Abaikan path invalid */ }
                }
            }
            if (OperatingSystem.IsWindows())
            {
                string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string ghStdPath = Path.Combine(programFiles, "GitHub CLI", "gh.exe");
                if (File.Exists(ghStdPath))
                {
                    return ghStdPath;
                }
            }
            return null; 
        }

        // CreateStartInfo versi baru yang lebih pinter
        internal static ProcessStartInfo CreateStartInfo(string command, string args, TokenEntry? token, bool useProxy = true) {
            var startInfo = new ProcessStartInfo {
                 RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            
            string ghPath = FindExecutablePath(command) ?? command; 
            
            if (token != null) SetEnvironmentVariables(startInfo, token, command, useProxy);
            
             if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                startInfo.FileName = "cmd.exe";
                // === PERBAIKAN: Kutip di sekitar ghPath ===
                string escapedExe = $"\"{ghPath}\"";
                startInfo.Arguments = $"/c \"{escapedExe} {args}\"";
            } else { 
                startInfo.FileName = ghPath; // Langsung panggil 'gh'
                startInfo.Arguments = args; // Dan kasih argumennya
            }
            return startInfo;
        }

        // Upload file (buat CodeUpload.cs)
        public static async Task RunProcessWithFileStdinAsync(ProcessStartInfo startInfo, string localFilePath, CancellationToken cancellationToken)
        {
            startInfo.RedirectStandardInput = true; 
            
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            var outputWaitHandle = new TaskCompletionSource<bool>();
            var errorWaitHandle = new TaskCompletionSource<bool>();

            process.OutputDataReceived += (s, e) => { 
                if (e.Data == null) {
                    outputWaitHandle.TrySetResult(true);
                } else {
                    stdoutBuilder.AppendLine(e.Data); 
                }
            };
            process.ErrorDataReceived += (s, e) => { 
                if (e.Data == null) {
                    errorWaitHandle.TrySetResult(true);
                } else {
                    stderrBuilder.AppendLine(e.Data); 
                }
            };
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
                await Task.WhenAll(outputWaitHandle.Task, errorWaitHandle.Task); 

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
        
        // Streaming log (buat CodeActions.cs)
        internal static async Task<(string stdout, string stderr, int exitCode)> RunProcessAndStreamOutputAsync(
            ProcessStartInfo startInfo, 
            CancellationToken cancellationToken,
            Func<string, bool> onStdOutLine)
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdoutBuilder = new StringBuilder(); 
            var stderrBuilder = new StringBuilder();
            var tcs = new TaskCompletionSource<(string, string, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
            
            var outputWaitHandle = new TaskCompletionSource<bool>(); 
            var errorWaitHandle = new TaskCompletionSource<bool>(); 
            bool stopStreaming = false; 

            process.OutputDataReceived += (s, e) => { 
                if (e.Data == null) {
                    outputWaitHandle.TrySetResult(true); 
                } else if (!stopStreaming) {
                    stdoutBuilder.AppendLine(e.Data); 
                    try {
                        if (onStdOutLine(e.Data)) {
                            stopStreaming = true;
                        }
                    } catch (Exception ex) {
                        AnsiConsole.MarkupLine($"[red]Error in StdOut callback: {ex.Message.EscapeMarkup()}[/]");
                    }
                } 
            };
            process.ErrorDataReceived += (s, e) => { 
                if (e.Data == null) {
                    errorWaitHandle.TrySetResult(true); 
                } else if (!stopStreaming) {
                    stderrBuilder.AppendLine(e.Data);
                    try {
                        // === PERBAIKAN: Escape [REMOTE_ERR] jadi [[REMOTE_ERR]] ===
                        AnsiConsole.MarkupLine($"[red][[REMOTE_ERR]][/] {e.Data.EscapeMarkup()}");
                        // === AKHIR PERBAIKAN ===
                    } catch (Exception ex) {
                        AnsiConsole.MarkupLine($"[red]Error in StdErr callback: {ex.Message.EscapeMarkup()}[/]");
                    }
                } 
            };
            process.Exited += (s, e) => tcs.TrySetResult((stdoutBuilder.ToString().TrimEnd(), stderrBuilder.ToString().TrimEnd(), process.ExitCode));
            
            using var cancellationRegistration = cancellationToken.Register(() => { 
                if (tcs.TrySetCanceled(cancellationToken)) { 
                    try { 
                        if (!process.HasExited) process.Kill(true); 
                        stopStreaming = true;
                    } 
                    catch { /* Ignored */ } 
                } 
            });

            try {
                if (!process.Start()) throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
                process.BeginOutputReadLine(); 
                process.BeginErrorReadLine();
                
                var result = await tcs.Task; 
                await Task.WhenAll(outputWaitHandle.Task, errorWaitHandle.Task); 
                return result;

            }
            catch (TaskCanceledException ex) { throw new OperationCanceledException("Process run was canceled.", ex, cancellationToken); }
            catch (OperationCanceledException) { throw; } 
            catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Error in RunProcessAndStreamOutputAsync: {ex.Message.EscapeMarkup()}[/]");
                try { if (process != null && !process.HasExited) process.Kill(true); } catch { /* Ignored */ }
                return (stdoutBuilder.ToString().TrimEnd(), (stderrBuilder.ToString().TrimEnd() + "\n" + ex.Message).Trim(), process?.ExitCode ?? -1);
            }
        } 

        // Fungsi standar (buat GhService.cs)
        internal static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdoutBuilder = new StringBuilder(); var stderrBuilder = new StringBuilder();
            var tcs = new TaskCompletionSource<(string, string, int)>(TaskCreationOptions.RunContinuationsAsynchronously);

            var outputWaitHandle = new TaskCompletionSource<bool>(); 
            var errorWaitHandle = new TaskCompletionSource<bool>(); 

            process.OutputDataReceived += (s, e) => { 
                if (e.Data == null) {
                    outputWaitHandle.TrySetResult(true); 
                } else {
                    stdoutBuilder.AppendLine(e.Data); 
                }
            };
            process.ErrorDataReceived += (s, e) => { 
                if (e.Data == null) {
                    errorWaitHandle.TrySetResult(true); 
                } else {
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
                
                var result = await tcs.Task; 
                await Task.WhenAll(outputWaitHandle.Task, errorWaitHandle.Task); 
                return result;

            }
            catch (TaskCanceledException ex) { throw new OperationCanceledException("Process run was canceled.", ex, cancellationToken); }
            catch (OperationCanceledException) { throw; } 
            catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Error in RunProcessAsync: {ex.Message.EscapeMarkup()}[/]");
                try { if (process != null && !process.HasExited) process.Kill(true); } catch { /* Ignored */ }
                return (stdoutBuilder.ToString().TrimEnd(), (stderrBuilder.ToString().TrimEnd() + "\n" + ex.Message).Trim(), process?.ExitCode ?? -1);
            }
        }
        // === AKHIR FUNGSI BARU ===
    } 
}
