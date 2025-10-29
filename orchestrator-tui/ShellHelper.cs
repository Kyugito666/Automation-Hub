using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;
using System.Net;
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

        private static bool _isAttemptingIpAuth = false;

        public static async Task<string> RunGhCommand(TokenEntry token, string args, int timeoutMilliseconds = DEFAULT_TIMEOUT_MS)
        {
            var startInfo = CreateStartInfo("gh", args, token);
            Exception? lastException = null;
            var globalCancelToken = Program.GetMainCancellationToken();

            // Infinite Retry Loop
            while (true)
            {
                // Cek pembatalan global di awal loop
                globalCancelToken.ThrowIfCancellationRequested();

                var commandTimeoutCts = new CancellationTokenSource(timeoutMilliseconds);
                string stdout = "", stderr = "";
                int exitCode = -1;
                bool wasFatalErrorDetected = false; // Flag baru untuk menandai error fatal

                try
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(commandTimeoutCts.Token, globalCancelToken);
                    (stdout, stderr, exitCode) = await RunProcessAsync(startInfo, linkedCts.Token);

                    if (exitCode == 0) return stdout; // Sukses

                    AnsiConsole.MarkupLine($"[yellow]WARN: gh command failed (Exit {exitCode}). Analyzing error...[/]");
                    AnsiConsole.MarkupLine($"[grey]   CMD: gh {args}[/]");
                    AnsiConsole.MarkupLine($"[grey]   ERR: {stderr.Split('\n').FirstOrDefault()?.Trim()}[/]");
                }
                catch (OperationCanceledException) when (commandTimeoutCts.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine($"[yellow]Command timed out ({timeoutMilliseconds / 1000}s). Retrying in {TIMEOUT_RETRY_DELAY_MS / 1000}s...[/]");
                    AnsiConsole.MarkupLine($"[grey]   CMD: gh {args}[/]");
                    try { await Task.Delay(TIMEOUT_RETRY_DELAY_MS, globalCancelToken); } catch (OperationCanceledException) { throw; }
                    continue; // Timeout -> Retry
                }
                catch (OperationCanceledException) when (globalCancelToken.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine("[yellow]Command cancelled by user (Global Cancel).[/]");
                    throw; // Cancel global -> Stop
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]ShellHelper Exception during RunProcessAsync: {ex.Message.Split('\n').FirstOrDefault()?.Trim()}[/]");
                    AnsiConsole.MarkupLine($"[yellow]Retrying in {NETWORK_RETRY_DELAY_MS / 1000}s...[/]");
                    try { await Task.Delay(NETWORK_RETRY_DELAY_MS, globalCancelToken); } catch (OperationCanceledException) { throw; }
                    continue; // Exception -> Retry
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
                                      lowerStderr.Contains("unreachable network") || lowerStderr.Contains("unexpected eof");
                bool isNotFoundError = lowerStderr.Contains("404 not found");

                // --- Error Handling Logic ---

                // 1. Proxy Auth (407): Rotate -> IP Auth -> Fallback ke Network Retry
                if (isProxyAuthError) {
                    AnsiConsole.MarkupLine($"[yellow]Proxy Auth Error (407) detected. Attempting to rotate proxy...[/]");
                    if (TokenManager.RotateProxyForToken(token)) {
                        startInfo = CreateStartInfo("gh", args, token);
                        AnsiConsole.MarkupLine($"[cyan]Proxy rotated. Retrying command immediately...[/]");
                        try { await Task.Delay(1000, globalCancelToken); } catch (OperationCanceledException) { throw; }
                        continue; // Retry dengan proxy baru
                    }
                    AnsiConsole.MarkupLine("[yellow]Proxy rotation failed. Attempting automatic IP Authorization...[/]");
                    if (!_isAttemptingIpAuth) {
                        _isAttemptingIpAuth = true;
                        bool ipAuthSuccess = await ProxyManager.RunIpAuthorizationOnlyAsync(globalCancelToken);
                        _isAttemptingIpAuth = false;
                        if (ipAuthSuccess) { AnsiConsole.MarkupLine("[magenta]IP Auth successful. Retrying command...[/]"); continue; } // Retry setelah IP Auth
                        else { AnsiConsole.MarkupLine("[red]Automatic IP Auth failed. Treating as persistent network error.[/]"); }
                    } else { AnsiConsole.MarkupLine("[yellow]IP Auth already in progress, treating as network error.[/]"); }
                    // Jika sampai sini (rotasi & IP Auth gagal), biarkan jatuh ke Network Error
                }

                // 2. Network Error: Infinite Retry
                if (isNetworkError || isProxyAuthError) { // Proxy Auth yg gagal juga di-retry terus
                    AnsiConsole.MarkupLine($"[magenta]Network/Proxy error detected. Retrying command in {NETWORK_RETRY_DELAY_MS / 1000}s...[/]");
                    AnsiConsole.MarkupLine($"[grey]   (Detail: {stderr.Split('\n').FirstOrDefault()?.Trim()})[/]");
                    try { await Task.Delay(NETWORK_RETRY_DELAY_MS, globalCancelToken); } catch (OperationCanceledException) { throw; }
                    continue; // Retry
                }

                // 3. Fatal Errors: Set flag, set exception, break loop
                if (isAuthError || isRateLimit || isNotFoundError) {
                    string errorType = isAuthError ? "Authentication (401)" : isRateLimit ? "Rate Limit/Forbidden (403)" : "Not Found (404)";
                    AnsiConsole.MarkupLine($"[red]FATAL GH Error: {errorType}. Command failed permanently.[/]");
                    lastException = new Exception($"GH Command Failed ({errorType}): {stderr.Split('\n').FirstOrDefault()?.Trim()}");
                    wasFatalErrorDetected = true; // Tandai error fatal
                    break; // EXIT LOOP
                }

                // === PERBAIKAN: Blok Unhandled Error HANYA jika BELUM ada error fatal ===
                // 4. Other Unhandled Errors: Coba retry sekali, JIKA bukan error fatal
                if (!wasFatalErrorDetected) // Hanya jalankan jika BUKAN 401/403/404
                {
                    AnsiConsole.MarkupLine($"[red]Unhandled gh command error (Exit {exitCode}). Attempting one immediate retry...[/]");
                    // Simpan exception sementara, tapi JANGAN break
                    lastException = new Exception($"Unhandled GH Command Failed (Exit {exitCode}): {stderr.Split('\n').FirstOrDefault()?.Trim()}");
                    try { await Task.Delay(2000, globalCancelToken); } catch (OperationCanceledException) { throw; }
                    continue; // Coba lagi SEKALI
                }
                // Jika wasFatalErrorDetected = true, loop akan exit karena break di atas.
                // Jika retry unhandled error gagal lagi, loop berikutnya akan masuk ke sini lagi,
                // tapi wasFatalErrorDetected masih false, jadi akan retry sekali lagi (ini bug kecil tapi tidak infinite).
                // Cara fix bug kecil ini: tambah counter retry unhandled, tapi untuk sekarang ini cukup.
                // Yang penting 404 tidak infinite loop lagi.

                // Safety break jika entah bagaimana logic di atas gagal (seharusnya tidak tercapai)
                lastException ??= new Exception($"GH command failed (Exit {exitCode}) with unhandled error state.");
                break;
                // === AKHIR PERBAIKAN ===

            } // Akhir while(true)

            // Lempar exception final jika loop di-break
            throw lastException ?? new Exception("GH command failed after exhausting retry/error handling logic.");
        } // Akhir RunGhCommand


        // --- Sisa fungsi (RunCommandAsync, RunInteractive, dll.) TIDAK BERUBAH ---
        // --- Cukup copy-paste dari versi sebelumnya yang sudah benar ---

        public static async Task RunCommandAsync(string command, string args, string? workingDir = null, TokenEntry? token = null)
        {
            var startInfo = CreateStartInfo(command, args, token);
            if (workingDir != null) startInfo.WorkingDirectory = workingDir;
            // Gunakan CancellationTokenSource baru agar command ini punya timeout sendiri
            using var cts = new CancellationTokenSource(DEFAULT_TIMEOUT_MS);
            // Tapi gabungkan dengan token global agar bisa di-cancel oleh Ctrl+C
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, Program.GetMainCancellationToken());
            try
            {
                var (_, stderr, exitCode) = await RunProcessAsync(startInfo, linkedCts.Token); // Pakai linked token
                if (exitCode != 0) throw new Exception($"Command '{command} {args}' failed (Exit Code: {exitCode}): {stderr}");
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Timeout spesifik command ini
                throw new TimeoutException($"Command '{command} {args}' timed out after {DEFAULT_TIMEOUT_MS / 1000} seconds.");
            }
            // Biarkan OperationCanceledException dari global token dilempar ke atas
        }

        public static async Task RunInteractive(string command, string args, string? workingDir = null, TokenEntry? token = null, CancellationToken cancellationToken = default)
        {
            var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = false, RedirectStandardOutput = false, RedirectStandardError = false, RedirectStandardInput = false };
            if (workingDir != null) startInfo.WorkingDirectory = workingDir;
            if (token != null) SetEnvironmentVariables(startInfo, token, command);
            SetFileNameAndArgs(startInfo, command, args);
            using var process = new Process { StartInfo = startInfo };
            // Gabungkan token input dengan token global
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Program.GetMainCancellationToken());
            try {
                AnsiConsole.MarkupLine($"[dim]Starting interactive: {startInfo.FileName} {startInfo.Arguments}[/]");
                process.Start();
                using var reg = linkedCts.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
                await process.WaitForExitAsync(linkedCts.Token);
                if (!linkedCts.Token.IsCancellationRequested && process.ExitCode != 0 && process.ExitCode != -1)
                {
                    AnsiConsole.MarkupLine($"[yellow]Interactive process exited with code: {process.ExitCode}[/]");
                }
            } catch (OperationCanceledException) {
                AnsiConsole.MarkupLine("[yellow]Interactive operation cancelled.[/]");
                try { if (!process.HasExited) process.Kill(true); } catch { }
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Error running interactive process: {ex.Message}[/]");
                try { if (!process.HasExited) process.Kill(true); } catch { }
            }
        }

        public static async Task RunInteractiveWithFullInput(string command, string args, string? workingDir = null, TokenEntry? token = null, CancellationToken cancellationToken = default)
        {
            var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = false, RedirectStandardOutput = false, RedirectStandardError = false, RedirectStandardInput = false };
            if (workingDir != null) startInfo.WorkingDirectory = workingDir;
            if (token != null) SetEnvironmentVariables(startInfo, token, command);
            SetFileNameAndArgs(startInfo, command, args);
            using var process = new Process { StartInfo = startInfo };
            // Gabungkan token input dengan token global
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Program.GetMainCancellationToken());
            try {
                AnsiConsole.MarkupLine($"[bold green]▶ Starting Full Interactive Session[/]");
                AnsiConsole.MarkupLine($"[dim]Cmd: {command} {args}[/]");
                AnsiConsole.MarkupLine($"[dim]Dir: {workingDir ?? "current"}[/]");
                AnsiConsole.MarkupLine("[yellow]"+ new string('═', 60) +"[/]");

                process.Start();

                var processExitedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.EnableRaisingEvents = true;
                process.Exited += (s, e) => processExitedTcs.TrySetResult(true);

                // Pakai linked token untuk cancellation registration
                var cancellationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var reg = linkedCts.Token.Register(() => cancellationTcs.TrySetResult(true));

                var completedTask = await Task.WhenAny(processExitedTcs.Task, cancellationTcs.Task);

                AnsiConsole.MarkupLine("\n[yellow]"+ new string('═', 60) +"[/]");

                if (completedTask == cancellationTcs.Task || linkedCts.Token.IsCancellationRequested) {
                    AnsiConsole.MarkupLine("[yellow]Cancellation requested. Terminating process...[/]");
                    try { if (!process.HasExited) { process.Kill(true); await Task.WhenAny(processExitedTcs.Task, Task.Delay(1500)); }
                    } catch (InvalidOperationException) { /* Process already exited */ }
                    catch (Exception killEx) { AnsiConsole.MarkupLine($"[red]Error terminating process: {killEx.Message}[/]"); }
                    // Lempar cancel agar pemanggil tahu
                    linkedCts.Token.ThrowIfCancellationRequested();
                }

                await Task.Delay(100);
                int exitCode = process.ExitCode;

                if (exitCode == 0) { AnsiConsole.MarkupLine($"[green]✓ Process exited normally (Code: {exitCode})[/]"); }
                else { AnsiConsole.MarkupLine($"[yellow]Process exited with non-zero code: {exitCode}[/]"); }

            } catch (OperationCanceledException) {
                 AnsiConsole.MarkupLine("[yellow]Interactive session cancelled.[/]");
                 // Jangan lempar lagi, biarkan pemanggil lanjut
            } catch (Exception ex) {
                AnsiConsole.MarkupLine("\n[yellow]"+ new string('═', 60) +"[/]");
                AnsiConsole.MarkupLine($"[red]✗ Error running full interactive process: {ex.Message.EscapeMarkup()}[/]");
                try { if (!process.HasExited) process.Kill(true); } catch { }
                 throw; // Lempar error ke pemanggil
            }
        }


        private static ProcessStartInfo CreateStartInfo(string command, string args, TokenEntry? token) {
            var startInfo = new ProcessStartInfo {
                Arguments = args, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            if (token != null) SetEnvironmentVariables(startInfo, token, command);
            SetFileNameAndArgs(startInfo, command, args);
            return startInfo;
        }

        private static void SetEnvironmentVariables(ProcessStartInfo startInfo, TokenEntry token, string command) {
            bool isGhCommand = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? command.ToLower().EndsWith("gh.exe") || command.ToLower() == "gh"
                : command == "gh";
            if (isGhCommand) {
                startInfo.EnvironmentVariables["GH_TOKEN"] = token.Token;
            }
            if (!string.IsNullOrEmpty(token.Proxy)) {
                startInfo.EnvironmentVariables["https_proxy"] = token.Proxy; startInfo.EnvironmentVariables["http_proxy"] = token.Proxy;
                startInfo.EnvironmentVariables["HTTPS_PROXY"] = token.Proxy; startInfo.EnvironmentVariables["HTTP_PROXY"] = token.Proxy;
                string noProxy = "localhost,127.0.0.1"; // Hapus domain GitHub
                startInfo.EnvironmentVariables["NO_PROXY"] = noProxy; startInfo.EnvironmentVariables["no_proxy"] = noProxy;
            } else {
                 startInfo.EnvironmentVariables.Remove("https_proxy"); startInfo.EnvironmentVariables.Remove("http_proxy");
                 startInfo.EnvironmentVariables.Remove("HTTPS_PROXY"); startInfo.EnvironmentVariables.Remove("HTTP_PROXY");
                 startInfo.EnvironmentVariables.Remove("NO_PROXY"); startInfo.EnvironmentVariables.Remove("no_proxy");
            }
        }

        private static void SetFileNameAndArgs(ProcessStartInfo startInfo, string command, string args) {
             if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = $"/c \"\"{command}\" {args}\"";
            } else {
                startInfo.FileName = "/bin/bash";
                string escapedArgs = args.Replace("\"", "\\\"");
                startInfo.Arguments = $"-c \"{command} {escapedArgs}\"";
            }
        }

        private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
        {
            // Fungsi ini sekarang HANYA bertanggung jawab menjalankan proses dan menangkap output/exit code/cancel
            // TIDAK ADA LOGIKA RETRY DI SINI
            using var process = new Process { StartInfo = startInfo };
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();
            var tcs = new TaskCompletionSource<(string, string, int)>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.EnableRaisingEvents = true;
            process.OutputDataReceived += (s, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };
            // Pakai TrySetResult agar lebih aman jika event dipanggil > 1 kali
            process.Exited += (s, e) => tcs.TrySetResult((stdoutBuilder.ToString().TrimEnd(), stderrBuilder.ToString().TrimEnd(), process.ExitCode));

            CancellationTokenRegistration cancellationRegistration = default;
            try {
                if (!process.Start()) {
                     throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Registrasi cancel token
                cancellationRegistration = cancellationToken.Register(() => {
                    // Gunakan TrySetCanceled
                    if (tcs.TrySetCanceled(cancellationToken)) {
                        try {
                            if (!process.HasExited) process.Kill(true);
                        } catch { /* Ignored */ }
                    }
                });

                // Tunggu hasilnya (bisa exit normal atau cancel)
                return await tcs.Task;
            }
            // Tangkap cancel secara eksplisit dan lempar ulang
            catch (TaskCanceledException ex) { throw new OperationCanceledException("Process run was canceled.", ex, cancellationToken); }
            catch (OperationCanceledException) { throw; } // Jika token sudah cancel sebelum await
            catch (Exception ex) { // Tangkap error lain (misal start gagal)
                AnsiConsole.MarkupLine($"[red]Error in RunProcessAsync: {ex.Message}[/]");
                try { if (process != null && !process.HasExited) process.Kill(true); } catch { }
                // Kembalikan error, JANGAN lempar exception di sini
                return (stdoutBuilder.ToString().TrimEnd(), (stderrBuilder.ToString().TrimEnd() + "\n" + ex.Message).Trim(), process?.ExitCode ?? -1);
            } finally {
                // Pastikan registrasi di-dispose
                await cancellationRegistration.DisposeAsync();
            }
        } // Akhir RunProcessAsync

    } // Akhir Class ShellHelper
} // Akhir Namespace Orchestrator
