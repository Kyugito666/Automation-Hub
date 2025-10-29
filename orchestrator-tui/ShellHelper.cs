using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;
using System.Net; // Tidak terpakai, bisa dihapus
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator
{
    public static class ShellHelper
    {
        private const int DEFAULT_TIMEOUT_MS = 120000;
        private const int SHORT_TIMEOUT_MS = 30000;
        private const int LONG_TIMEOUT_MS = 600000;
        private const int NETWORK_RETRY_DELAY_MS = 30000;
        private const int TIMEOUT_RETRY_DELAY_MS = 15000;

        private static bool _isAttemptingIpAuth = false; // Flag untuk mencegah IP Auth rekursif

        // --- RunGhCommand (Sudah diperbaiki sebelumnya, TIDAK BERUBAH) ---
        public static async Task<string> RunGhCommand(TokenEntry token, string args, int timeoutMilliseconds = DEFAULT_TIMEOUT_MS)
        {
            var startInfo = CreateStartInfo("gh", args, token);
            Exception? lastException = null;
            var globalCancelToken = Program.GetMainCancellationToken(); // Dapatkan token utama

            while (true) // Loop retry (hanya untuk network/timeout/proxy)
            {
                globalCancelToken.ThrowIfCancellationRequested(); // Cek cancel global
                using var commandTimeoutCts = new CancellationTokenSource(timeoutMilliseconds);
                string stdout = "", stderr = ""; int exitCode = -1;

                try {
                    // Gabungkan timeout command + cancel global
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(commandTimeoutCts.Token, globalCancelToken);
                    (stdout, stderr, exitCode) = await RunProcessAsync(startInfo, linkedCts.Token);

                    if (exitCode == 0) return stdout; // Sukses

                    // Log error jika gagal
                    AnsiConsole.MarkupLine($"[yellow]WARN: gh command failed (Exit {exitCode}). Analyzing error...[/]");
                    AnsiConsole.MarkupLine($"[grey]   CMD: gh {args.EscapeMarkup()}[/]");
                    AnsiConsole.MarkupLine($"[grey]   ERR: {stderr.Split('\n').FirstOrDefault()?.Trim().EscapeMarkup() ?? "No stderr"}[/]");
                }
                catch (OperationCanceledException) when (commandTimeoutCts.IsCancellationRequested && !globalCancelToken.IsCancellationRequested) {
                    // Timeout command -> Retry
                    AnsiConsole.MarkupLine($"[yellow]Command timed out ({timeoutMilliseconds / 1000}s). Retrying in {TIMEOUT_RETRY_DELAY_MS / 1000}s...[/]");
                    AnsiConsole.MarkupLine($"[grey]   CMD: gh {args.EscapeMarkup()}[/]");
                    try { await Task.Delay(TIMEOUT_RETRY_DELAY_MS, globalCancelToken); } catch (OperationCanceledException) { throw; } // Bisa di-cancel saat delay
                    continue; // Lanjut retry
                }
                catch (OperationCanceledException) when (globalCancelToken.IsCancellationRequested) {
                    // Cancel global (Ctrl+C) -> Stop & Throw
                    AnsiConsole.MarkupLine("[yellow]Command cancelled by user (Global Cancel).[/]");
                    throw; // Lempar cancel utama
                }
                catch (Exception ex) {
                    // Exception saat RunProcessAsync -> Log & Retry (dianggap network issue)
                    AnsiConsole.MarkupLine($"[red]ShellHelper Exception during RunProcessAsync: {ex.Message.Split('\n').FirstOrDefault()?.Trim().EscapeMarkup()}[/]");
                    AnsiConsole.MarkupLine($"[yellow]Retrying in {NETWORK_RETRY_DELAY_MS / 1000}s...[/]");
                    try { await Task.Delay(NETWORK_RETRY_DELAY_MS, globalCancelToken); } catch (OperationCanceledException) { throw; } // Bisa di-cancel saat delay
                    continue; // Lanjut retry
                }

                // --- Analisa Error (jika exitCode != 0) ---
                string lowerStderr = stderr.ToLowerInvariant();
                bool isRateLimit = lowerStderr.Contains("api rate limit exceeded") || lowerStderr.Contains("403 forbidden");
                bool isAuthError = lowerStderr.Contains("bad credentials") || lowerStderr.Contains("401 unauthorized");
                bool isProxyAuthError = lowerStderr.Contains("407 proxy authentication required");
                bool isNetworkError = lowerStderr.Contains("dial tcp") || lowerStderr.Contains("connection refused") ||
                                      lowerStderr.Contains("i/o timeout") || lowerStderr.Contains("error connecting") ||
                                      lowerStderr.Contains("wsarecv") || lowerStderr.Contains("forcibly closed") ||
                                      lowerStderr.Contains("resolve host") || lowerStderr.Contains("tls handshake timeout") ||
                                      lowerStderr.Contains("unreachable network") || lowerStderr.Contains("unexpected eof") ||
                                      lowerStderr.Contains("connection reset") || lowerStderr.Contains("handshake failed");
                bool isNotFoundError = lowerStderr.Contains("404 not found");

                // --- Error Handling Logic ---
                if (isProxyAuthError) {
                    AnsiConsole.MarkupLine($"[yellow]Proxy Auth Error (407). Rotating proxy...[/]");
                    if (TokenManager.RotateProxyForToken(token)) {
                        startInfo = CreateStartInfo("gh", args, token); // Update startInfo
                        AnsiConsole.MarkupLine($"[cyan]Proxy rotated. Retrying command...[/]");
                        try { await Task.Delay(1000, globalCancelToken); } catch (OperationCanceledException) { throw; }
                        continue; // Retry
                    }
                    AnsiConsole.MarkupLine("[yellow]Proxy rotation failed. Attempting IP Auth...[/]");
                    if (!_isAttemptingIpAuth) {
                        _isAttemptingIpAuth = true;
                        bool ipAuthSuccess = await ProxyManager.RunIpAuthorizationOnlyAsync(globalCancelToken);
                        _isAttemptingIpAuth = false;
                        if (ipAuthSuccess) { AnsiConsole.MarkupLine("[magenta]IP Auth successful. Retrying command...[/]"); continue; }
                        else { AnsiConsole.MarkupLine("[red]IP Auth failed. Treating as network error.[/]"); }
                    } else { AnsiConsole.MarkupLine("[yellow]IP Auth in progress. Treating as network error.[/]"); }
                    // Jatuh ke network error jika IP Auth gagal/jalan
                }

                if ((isNetworkError || isProxyAuthError) && !isNotFoundError) { // Retry network/proxy error (kecuali 404)
                    AnsiConsole.MarkupLine($"[magenta]Network/Proxy error. Retrying in {NETWORK_RETRY_DELAY_MS / 1000}s...[/]");
                    AnsiConsole.MarkupLine($"[grey]   (Detail: {stderr.Split('\n').FirstOrDefault()?.Trim().EscapeMarkup()})[/]");
                    try { await Task.Delay(NETWORK_RETRY_DELAY_MS, globalCancelToken); } catch (OperationCanceledException) { throw; }
                    continue; // Retry
                }

                if (isAuthError || isRateLimit || isNotFoundError) { // Error Fatal -> Break & Throw
                    string errorType = isAuthError ? "Auth (401)" : isRateLimit ? "Rate Limit/Forbidden (403)" : "Not Found (404)";
                    AnsiConsole.MarkupLine($"[red]FATAL GH Error: {errorType}. Command failed permanently.[/]");
                    lastException = new Exception($"GH Command Failed ({errorType}): {stderr.Split('\n').FirstOrDefault()?.Trim().EscapeMarkup()}");
                    break; // Keluar loop retry
                }

                // Error lain (Unhandled) -> Break & Throw (Tidak ada retry lagi)
                AnsiConsole.MarkupLine($"[red]FATAL Unhandled gh command error (Exit {exitCode}). Command failed permanently.[/]");
                lastException = new Exception($"Unhandled GH Command Failed (Exit {exitCode}): {stderr.Split('\n').FirstOrDefault()?.Trim().EscapeMarkup()}");
                break; // Keluar loop retry

            } // Akhir while(true) loop retry

            // Jika break dari loop, lempar exception
            throw lastException ?? new Exception("GH command failed unexpectedly after error handling.");
        }


        // --- RunCommandAsync --- (Tidak berubah signifikan, pastikan cek Exit Code)
        public static async Task RunCommandAsync(string command, string args, string? workingDir = null, TokenEntry? token = null)
        {
            var startInfo = CreateStartInfo(command, args, token);
            if (workingDir != null) startInfo.WorkingDirectory = workingDir;
            // Gunakan CancellationTokenSource terpisah untuk timeout spesifik command ini
            using var cts = new CancellationTokenSource(DEFAULT_TIMEOUT_MS);
            // Gabungkan dengan token utama dari Program.cs
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, Program.GetMainCancellationToken());
            try
            {
                var (_, stderr, exitCode) = await RunProcessAsync(startInfo, linkedCts.Token); // Pakai linked token
                // Periksa ExitCode setelah proses selesai
                if (exitCode != 0)
                {
                    // Lempar exception jika command gagal
                    throw new Exception($"Command '{command} {args.EscapeMarkup()}' failed (Exit Code: {exitCode}): {stderr.Split('\n').FirstOrDefault()?.Trim().EscapeMarkup()}");
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !Program.GetMainCancellationToken().IsCancellationRequested)
            {
                // Tangkap HANYA timeout spesifik command ini
                throw new TimeoutException($"Command '{command} {args.EscapeMarkup()}' timed out after {DEFAULT_TIMEOUT_MS / 1000} seconds.");
            }
            // Biarkan OperationCanceledException dari token utama (Ctrl+C)
            // atau Exception dari exitCode != 0 dilempar ke atas
        }

        // --- RunInteractive --- (Tidak berubah, handle cancel dasar)
        public static async Task RunInteractive(string command, string args, string? workingDir = null, TokenEntry? token = null, CancellationToken cancellationToken = default)
        {
            var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = false }; // Jangan redirect IO
            if (workingDir != null) startInfo.WorkingDirectory = workingDir;
            if (token != null) SetEnvironmentVariables(startInfo, token, command);
            SetFileNameAndArgs(startInfo, command, args); // Bungkus dengan shell
            using var process = new Process { StartInfo = startInfo };
            // Gabungkan token input dengan token utama
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Program.GetMainCancellationToken());
            try {
                AnsiConsole.MarkupLine($"[dim]Starting interactive: {startInfo.FileName} {startInfo.Arguments.EscapeMarkup()}[/]");
                if (!process.Start()) throw new InvalidOperationException("Failed to start interactive process.");
                // Registrasi cancel token untuk kill process jika di-cancel
                using var reg = linkedCts.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { /* Ignored */ } });
                await process.WaitForExitAsync(linkedCts.Token); // Tunggu proses selesai atau di-cancel
                // Log jika exit code non-zero dan tidak di-cancel
                if (!linkedCts.Token.IsCancellationRequested && process.ExitCode != 0 && process.ExitCode != -1) {
                    AnsiConsole.MarkupLine($"[yellow]Interactive process exited with code: {process.ExitCode}[/]");
                }
            } catch (OperationCanceledException) {
                // Tangkap cancel dari linkedCts (bisa dari input CancellationToken atau token utama)
                AnsiConsole.MarkupLine("[yellow]Interactive operation cancelled.[/]");
                try { if (!process.HasExited) process.Kill(true); } catch { /* Ignored */ }
                // Jangan throw lagi
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Error running interactive process: {ex.Message.EscapeMarkup()}[/]");
                try { if (!process.HasExited) process.Kill(true); } catch { /* Ignored */ }
                throw; // Lempar exception asli
            }
        }

        // === PERBAIKAN DI RunInteractiveWithFullInput ===
        public static async Task RunInteractiveWithFullInput(string command, string args, string? workingDir = null, TokenEntry? token = null, CancellationToken cancellationToken = default)
        {
            var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = false }; // Jangan redirect IO
            if (workingDir != null) startInfo.WorkingDirectory = workingDir;
            if (token != null) SetEnvironmentVariables(startInfo, token, command);
            SetFileNameAndArgs(startInfo, command, args); // Bungkus dengan shell
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            // Gabungkan token input (dari menu, misal _interactiveCts.Token) dengan token utama (_mainCts.Token)
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Program.GetMainCancellationToken());
            var processExitedTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.Exited += (s, e) => processExitedTcs.TrySetResult(process.ExitCode);

            try {
                AnsiConsole.MarkupLine($"[bold green]▶ Starting Full Interactive Session[/]");
                AnsiConsole.MarkupLine($"[dim]Cmd: {command} {args.EscapeMarkup()}[/]");
                AnsiConsole.MarkupLine($"[dim]Dir: {workingDir?.EscapeMarkup() ?? "current"}[/]");
                AnsiConsole.MarkupLine("[yellow]"+ new string('═', 60) +"[/]");

                if (!process.Start()) throw new InvalidOperationException("Failed to start full interactive process.");

                // Task yang selesai jika token di-cancel
                var cancellationTask = Task.Delay(Timeout.Infinite, linkedCts.Token);
                // Task yang selesai jika process exit
                var processTask = processExitedTcs.Task;

                // Tunggu mana yang selesai duluan
                var completedTask = await Task.WhenAny(processTask, cancellationTask);

                AnsiConsole.MarkupLine("\n[yellow]"+ new string('═', 60) +"[/]");

                // Jika cancellationTask yang selesai (berarti token di-cancel)
                if (completedTask == cancellationTask || linkedCts.Token.IsCancellationRequested) {
                    AnsiConsole.MarkupLine("[yellow]Cancellation requested during interactive session. Terminating process...[/]");
                    try {
                        if (!process.HasExited) {
                            process.Kill(true); // Kill process tree
                            // Tunggu sebentar agar exit event ter-trigger atau proses benar2 mati
                            await Task.WhenAny(processExitedTcs.Task, Task.Delay(1500));
                        }
                    } catch (InvalidOperationException) { /* Process already exited */ }
                    catch (Exception killEx) { AnsiConsole.MarkupLine($"[red]Error terminating process: {killEx.Message}[/]"); }

                    // === PENTING: Lempar OperationCanceledException ===
                    // Ini akan ditangkap oleh catch block di ShowAttachMenuAsync/ShowRemoteShellAsync
                    // agar TUI tahu sesi ini di-cancel dan bisa kembali ke menu.
                    linkedCts.Token.ThrowIfCancellationRequested();
                }

                // Jika processTask yang selesai (proses exit normal atau karena error internal)
                int exitCode = await processTask; // Dapatkan exit code dari TCS
                if (exitCode == 0) {
                    AnsiConsole.MarkupLine($"[green]✓ Process exited normally (Code: {exitCode})[/]");
                } else {
                    AnsiConsole.MarkupLine($"[yellow]Process exited with non-zero code: {exitCode}[/]");
                    // Tidak perlu throw exception di sini, anggap flow normal
                }

            }
            // === Tangkap HANYA OperationCanceledException ===
            catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine("[yellow]Interactive session cancelled.[/]");
                 // Kill lagi untuk memastikan (jika belum exit)
                 try { if (!process.HasExited) process.Kill(true); } catch { /* Ignored */ }
                 // JANGAN throw lagi, biarkan kembali ke pemanggil (menu)
            }
            // === Tangkap Exception lain ===
            catch (Exception ex) {
                AnsiConsole.MarkupLine("\n[yellow]"+ new string('═', 60) +"[/]");
                AnsiConsole.MarkupLine($"[red]✗ Error running full interactive process: {ex.Message.EscapeMarkup()}[/]");
                try { if (!process.HasExited) process.Kill(true); } catch { /* Ignored */ }
                 throw; // Lempar error lain ke pemanggil (menu)
            }
        }
        // === AKHIR PERBAIKAN ===


        // --- CreateStartInfo --- (Tidak berubah)
        private static ProcessStartInfo CreateStartInfo(string command, string args, TokenEntry? token) {
            var startInfo = new ProcessStartInfo {
                 RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            startInfo.Arguments = args; // Set arguments dulu
            if (token != null) SetEnvironmentVariables(startInfo, token, command);
            SetFileNameAndArgs(startInfo, command, args); // Bungkus dengan shell (akan override arguments)
            return startInfo;
        }

        // --- SetEnvironmentVariables --- (Tidak berubah)
        private static void SetEnvironmentVariables(ProcessStartInfo startInfo, TokenEntry token, string command) {
            bool isGhCommand = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? command.ToLower().EndsWith("gh.exe") || command.ToLower() == "gh"
                : command == "gh";
            if (isGhCommand && !string.IsNullOrEmpty(token.Token)) {
                startInfo.EnvironmentVariables["GH_TOKEN"] = token.Token;
            }
            // Hapus proxy lama
             startInfo.EnvironmentVariables.Remove("https_proxy"); startInfo.EnvironmentVariables.Remove("http_proxy");
             startInfo.EnvironmentVariables.Remove("HTTPS_PROXY"); startInfo.EnvironmentVariables.Remove("HTTP_PROXY");
             startInfo.EnvironmentVariables.Remove("NO_PROXY"); startInfo.EnvironmentVariables.Remove("no_proxy");
            // Set proxy baru jika ada
            if (!string.IsNullOrEmpty(token.Proxy)) {
                startInfo.EnvironmentVariables["https_proxy"] = token.Proxy;
                startInfo.EnvironmentVariables["http_proxy"] = token.Proxy;
                startInfo.EnvironmentVariables["HTTPS_PROXY"] = token.Proxy;
                startInfo.EnvironmentVariables["HTTP_PROXY"] = token.Proxy;
                // Kosongkan NO_PROXY untuk 'gh'
            }
        }

        // --- SetFileNameAndArgs --- (Tidak berubah)
        private static void SetFileNameAndArgs(ProcessStartInfo startInfo, string command, string args) {
             if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = $"/c \"\"{command}\" {args}\""; // Bungkus command dan args
            } else { // Linux, macOS
                startInfo.FileName = "/bin/bash";
                string escapedArgs = args.Replace("\"", "\\\""); // Escape quotes di args
                startInfo.Arguments = $"-c \"{command} {escapedArgs}\""; // Jalankan command + args di bash
            }
        }

        // --- RunProcessAsync --- (Tidak berubah)
        private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
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
                return await tcs.Task; // Tunggu exit atau cancel
            }
            catch (TaskCanceledException ex) { throw new OperationCanceledException("Process run was canceled.", ex, cancellationToken); }
            catch (OperationCanceledException) { throw; } // Jika token sudah cancel sebelum await
            catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Error in RunProcessAsync: {ex.Message.EscapeMarkup()}[/]");
                try { if (process != null && !process.HasExited) process.Kill(true); } catch { /* Ignored */ }
                return (stdoutBuilder.ToString().TrimEnd(), (stderrBuilder.ToString().TrimEnd() + "\n" + ex.Message).Trim(), process?.ExitCode ?? -1);
            }
        } // Akhir RunProcessAsync

    } // Akhir Class ShellHelper
} // Akhir Namespace Orchestrator
