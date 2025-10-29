using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;
using System.Net; // <-- Pastikan using System.Net ada

namespace Orchestrator;

public static class ShellHelper
{
    private const int DEFAULT_TIMEOUT_MS = 120000;
    private const int MAX_RETRY_ON_PROXY_ERROR = 2; // Coba rotasi proxy max 2x
    private const int MAX_RETRY_ON_NETWORK_ERROR = 2; // Coba retry koneksi max 2x
    private const int MAX_RETRY_ON_TIMEOUT = 1;

    // === PERBAIKAN: Flag untuk mencegah rekursi IP Auth ===
    private static bool _isAttemptingIpAuth = false;

    public static async Task<string> RunGhCommand(TokenEntry token, string args, int timeoutMilliseconds = DEFAULT_TIMEOUT_MS)
    {
        var startInfo = CreateStartInfo("gh", args, token); // <- Perbaikan: Pake CreateStartInfo yg benar

        int proxyRetryCount = 0;
        int networkRetryCount = 0;
        int timeoutRetryCount = 0;
        Exception? lastException = null;

        while (true)
        {
            try
            {
                var (stdout, stderr, exitCode) = await RunProcessAsync(startInfo, timeoutMilliseconds);

                if (exitCode != 0)
                {
                    stderr = stderr.ToLowerInvariant(); // Normalisasi error message
                    bool isRateLimit = stderr.Contains("api rate limit exceeded") || stderr.Contains("403");
                    bool isAuthError = stderr.Contains("bad credentials") || stderr.Contains("401");
                    bool isProxyAuthError = stderr.Contains("407") || stderr.Contains("proxy authentication required");
                    bool isNetworkError = stderr.Contains("dial tcp") ||
                                          stderr.Contains("connection refused") ||
                                          stderr.Contains("i/o timeout") ||
                                          stderr.Contains("error connecting to http") ||
                                          stderr.Contains("wsarecv") ||
                                          stderr.Contains("forcibly closed") ||
                                          stderr.Contains("could not resolve host") || // <-- Tambah deteksi resolve host
                                          stderr.Contains("tls handshake timeout"); // <-- Tambah deteksi TLS timeout

                    bool isNotFoundError = stderr.Contains("404") || stderr.Contains("could not find");

                    // 1. Handle Proxy Auth Error (407) - Coba rotasi dulu
                    if (isProxyAuthError && proxyRetryCount < MAX_RETRY_ON_PROXY_ERROR)
                    {
                        proxyRetryCount++;
                        AnsiConsole.MarkupLine($"[yellow]Proxy Auth error (407). Rotating... (Retry {proxyRetryCount}/{MAX_RETRY_ON_PROXY_ERROR})[/]");
                        if (TokenManager.RotateProxyForToken(token)) {
                            startInfo = CreateStartInfo("gh", args, token); // <-- Perbaikan: Update startInfo setelah rotasi
                            await Task.Delay(2000); continue;
                        } else {
                            AnsiConsole.MarkupLine("[red]No more proxies to rotate.[/]");
                            // Lanjut ke logic auto IP auth jika rotasi gagal
                        }
                    }

                    // 2. Handle Network Error - Coba retry dulu
                    if (isNetworkError && networkRetryCount < MAX_RETRY_ON_NETWORK_ERROR)
                    {
                        networkRetryCount++;
                        AnsiConsole.MarkupLine($"[yellow]Network error. Retrying... ({networkRetryCount}/{MAX_RETRY_ON_NETWORK_ERROR})[/]");
                        await Task.Delay(4000); continue;
                    }

                    // === PERBAIKAN: Auto IP Auth ===
                    // Trigger jika error jaringan ATAU error proxy auth setelah retry/rotasi gagal,
                    // dan belum pernah coba IP Auth di siklus ini
                    if ((isNetworkError || isProxyAuthError) && !_isAttemptingIpAuth)
                    {
                        AnsiConsole.MarkupLine("[magenta]Persistent network/proxy error detected. Attempting automatic IP Authorization...[/]");
                        _isAttemptingIpAuth = true; // Set flag biar nggak rekursif
                        bool ipAuthSuccess = await ProxyManager.RunIpAuthorizationOnlyAsync();
                        _isAttemptingIpAuth = false; // Reset flag

                        if (ipAuthSuccess)
                        {
                            AnsiConsole.MarkupLine("[magenta]IP Authorization finished. Retrying original command one last time...[/]");
                            // Reset retry counts biar coba lagi dari awal setelah IP Auth
                            proxyRetryCount = 0;
                            networkRetryCount = 0;
                            timeoutRetryCount = 0;
                            // Update startInfo lagi, siapa tau proxy berubah (meski IP Auth nggak ganti proxy)
                            startInfo = CreateStartInfo("gh", args, token);
                            await Task.Delay(2000);
                            continue; // Coba lagi command asli
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[red]Automatic IP Authorization failed. Proceeding to fail the command.[/]");
                            // Biarkan error dilempar ke bawah
                        }
                    }
                    // === AKHIR PERBAIKAN ===


                    // Handle error fatal lainnya (Rate Limit, Auth Token, Not Found)
                    if (isRateLimit || isAuthError) {
                        string errorType = isRateLimit ? "Rate Limit/403" : "Auth/401";
                        AnsiConsole.MarkupLine($"[red]GH Error ({errorType}). Token rotation needed? Check PAT validity/scopes.[/]");
                        lastException = new Exception($"GH Fail ({errorType}): {stderr.Split('\n').FirstOrDefault()?.Trim()}");
                        break; // Keluar loop
                    }

                     if (isNotFoundError) {
                        AnsiConsole.MarkupLine($"[red]GH Error (Not Found/404). Check repo/codespace name?[/]");
                        lastException = new Exception($"GH Not Found (404): {stderr.Split('\n').FirstOrDefault()?.Trim()}");
                        break; // Keluar loop
                    }

                    // Jika sampai sini, berarti error jenis lain atau retry habis
                    lastException = new Exception($"gh command failed (Exit Code: {exitCode}): {stderr.Split('\n').FirstOrDefault()?.Trim()}");
                    break; // Keluar loop
                }

                // Jika exitCode == 0 (sukses)
                return stdout;
            }
            catch (TaskCanceledException ex) // Timeout
            {
                if (timeoutRetryCount < MAX_RETRY_ON_TIMEOUT) {
                    timeoutRetryCount++;
                    AnsiConsole.MarkupLine($"[yellow]Command timeout ({timeoutMilliseconds / 1000}s). Retrying... ({timeoutRetryCount}/{MAX_RETRY_ON_TIMEOUT})[/]");
                    await Task.Delay(5000); continue;
                }
                lastException = new Exception($"Command timed out after {timeoutMilliseconds}ms and {MAX_RETRY_ON_TIMEOUT} retry.", ex);
                break; // Keluar loop
            }
            catch (Exception ex) // Error tak terduga lainnya
            {
                lastException = ex;
                // Coba retry sekali untuk error tak terduga, siapa tau cuma glitch
                if (networkRetryCount == 0 && proxyRetryCount == 0 && timeoutRetryCount == 0) {
                    networkRetryCount++; // Hitung sebagai network retry
                     AnsiConsole.MarkupLine($"[yellow]Unexpected command fail: {ex.Message.Split('\n').FirstOrDefault()?.Trim()}. Retrying once...[/]");
                     await Task.Delay(3000); continue;
                }
                break; // Keluar loop jika sudah pernah retry atau bukan attempt pertama
            }
        } // Akhir while loop

        // Jika keluar dari loop karena error
        throw lastException ?? new Exception("GH command failed after retries or due to unhandled error.");
    }


    // ... (RunCommandAsync, RunInteractive, RunInteractiveWithFullInput - TIDAK BERUBAH) ...
     public static async Task RunCommandAsync(string command, string args, string? workingDir = null, TokenEntry? token = null)
    {
        // Perbaikan: Gunakan CreateStartInfo yang benar
        var startInfo = CreateStartInfo(command, args, token);
        if (workingDir != null) startInfo.WorkingDirectory = workingDir;

        // Panggil RunProcessAsync
        var (_, stderr, exitCode) = await RunProcessAsync(startInfo);
        if (exitCode != 0) throw new Exception($"Command '{command}' failed (Exit Code: {exitCode}): {stderr}");
    }

    public static async Task RunInteractive(string command, string args, string? workingDir = null, TokenEntry? token = null, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = false, RedirectStandardOutput = false, RedirectStandardError = false, RedirectStandardInput = false };
        if (workingDir != null) startInfo.WorkingDirectory = workingDir;
        if (token != null) SetEnvironmentVariables(startInfo, token);
        SetFileNameAndArgs(startInfo, command, args);

        using var process = new Process { StartInfo = startInfo };
        try {
            AnsiConsole.MarkupLine($"[dim]Starting interactive: {startInfo.FileName} {startInfo.Arguments}[/]");
            process.Start();
            using var reg = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 && process.ExitCode != -1 && !cancellationToken.IsCancellationRequested) AnsiConsole.MarkupLine($"[yellow]Interactive process exited with code: {process.ExitCode}[/]");
        } catch (OperationCanceledException) { AnsiConsole.MarkupLine("[yellow]Interactive process cancelled.[/]"); try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error running interactive process: {ex.Message}[/]"); try { if (!process.HasExited) process.Kill(true); } catch { } }
    }
     public static async Task RunInteractiveWithFullInput(string command, string args, string? workingDir = null, TokenEntry? token = null, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = false, RedirectStandardOutput = false, RedirectStandardError = false, RedirectStandardInput = false };
        if (workingDir != null) startInfo.WorkingDirectory = workingDir;
        if (token != null) SetEnvironmentVariables(startInfo, token);
        SetFileNameAndArgs(startInfo, command, args);

        using var process = new Process { StartInfo = startInfo };
        try {
            AnsiConsole.MarkupLine($"[bold green]▶ Starting bot FULL INTERACTIVE[/]"); AnsiConsole.MarkupLine($"[dim]Cmd: {command} {args}[/]"); AnsiConsole.MarkupLine($"[dim]Dir: {workingDir ?? "current"}[/]"); AnsiConsole.MarkupLine("[yellow]"+ new string('═', 60) +"[/]");
            process.Start();
            var processExitedTcs = new TaskCompletionSource<bool>(); process.EnableRaisingEvents = true; process.Exited += (s, e) => processExitedTcs.TrySetResult(true);
            var cancellationTcs = new TaskCompletionSource<bool>(); using var reg = cancellationToken.Register(() => cancellationTcs.TrySetResult(true));
            var completedTask = await Task.WhenAny(processExitedTcs.Task, cancellationTcs.Task);

            if (completedTask == cancellationTcs.Task) { // Jika dibatalkan
                try { if (!process.HasExited) { AnsiConsole.MarkupLine("\n[yellow]Sending termination signal...[/]"); process.Kill(true); await Task.Delay(1500); } } catch { /* Ignore errors during kill */ }
                AnsiConsole.MarkupLine("[yellow]"+ new string('═', 60) +"[/]"); AnsiConsole.MarkupLine("[yellow]✓ Bot stopped by user (Ctrl+C).[/]");
                throw new OperationCanceledException(); // Lempar exception cancel
            }
            // Jika proses selesai sendiri
            await Task.Delay(500); // Tunggu sebentar biar output terakhir keluar
            AnsiConsole.MarkupLine("[yellow]"+ new string('═', 60) +"[/]");
            if (process.ExitCode == 0) AnsiConsole.MarkupLine($"[green]✓ Bot exited cleanly (Code: {process.ExitCode})[/]");
            else if (process.ExitCode == -1 || cancellationToken.IsCancellationRequested) AnsiConsole.MarkupLine($"[yellow]⚠ Bot terminated (Code: {process.ExitCode})[/]");
            else AnsiConsole.MarkupLine($"[red]✗ Bot exited with error (Code: {process.ExitCode})[/]");
            AnsiConsole.MarkupLine("\n[dim]Press Enter to return to menu...[/]"); Console.ReadLine(); // Tunggu user sebelum kembali
        } catch (OperationCanceledException) {
             // Tangkap OperationCanceledException yang dilempar di atas
             try { if (!process.HasExited) {process.Kill(true); await Task.Delay(1000);} } catch { }
             AnsiConsole.MarkupLine("\n[dim]Press Enter to return to menu...[/]"); Console.ReadLine();
             throw; // Lempar lagi biar menu utama tau
        } catch (Exception ex) {
             AnsiConsole.MarkupLine("\n[yellow]"+ new string('═', 60) +"[/]"); AnsiConsole.MarkupLine($"[red]✗ Error running bot: {ex.Message.EscapeMarkup()}[/]");
             try { if (!process.HasExited) process.Kill(true); } catch { }
             AnsiConsole.MarkupLine("\n[dim]Press Enter to return to menu...[/]"); Console.ReadLine();
             throw; // Lempar error
        }
    }


    // === PERBAIKAN: CreateStartInfo diubah untuk handle command non-'gh' ===
    private static ProcessStartInfo CreateStartInfo(string command, string args, TokenEntry? token) {
        var startInfo = new ProcessStartInfo {
            // FileName diatur di SetFileNameAndArgs
            Arguments = args, // Argumen saja
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // Set environment (termasuk proxy) HANYA jika token disediakan
        if (token != null) SetEnvironmentVariables(startInfo, token);
        // Atur FileName dan format Arguments berdasarkan OS dan command
        SetFileNameAndArgs(startInfo, command, args);
        return startInfo;
    }
    // === AKHIR PERBAIKAN ===


    private static void SetEnvironmentVariables(ProcessStartInfo startInfo, TokenEntry token) {
        // Hanya set GH_TOKEN jika command nya 'gh'
        if (startInfo.FileName.Contains("gh")) {
             startInfo.EnvironmentVariables["GH_TOKEN"] = token.Token;
        }
        // Selalu set proxy jika ada
        if (!string.IsNullOrEmpty(token.Proxy)) {
            startInfo.EnvironmentVariables["https_proxy"] = token.Proxy; startInfo.EnvironmentVariables["http_proxy"] = token.Proxy;
            startInfo.EnvironmentVariables["HTTPS_PROXY"] = token.Proxy; startInfo.EnvironmentVariables["HTTP_PROXY"] = token.Proxy;
            startInfo.EnvironmentVariables["NO_PROXY"] = "localhost,127.0.0.1"; startInfo.EnvironmentVariables["no_proxy"] = "localhost,127.0.0.1";
        } else {
             // Hapus variabel proxy jika token tidak punya proxy (penting!)
             startInfo.EnvironmentVariables.Remove("https_proxy"); startInfo.EnvironmentVariables.Remove("http_proxy");
             startInfo.EnvironmentVariables.Remove("HTTPS_PROXY"); startInfo.EnvironmentVariables.Remove("HTTP_PROXY");
        }
    }

    // === PERBAIKAN: SetFileNameAndArgs diubah untuk handle command non-'gh' ===
    private static void SetFileNameAndArgs(ProcessStartInfo startInfo, string command, string args) {
         if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            startInfo.FileName = "cmd.exe";
            // Gabungkan command dan args untuk /c
            startInfo.Arguments = $"/c \"{command} {args}\""; // Pastikan di-quote
        } else {
            // Di Linux/Mac, kita bisa coba panggil command langsung via shell
            startInfo.FileName = "/bin/bash";
             // Escape double quotes di dalam args sebelum membungkusnya
            string escapedArgs = args.Replace("\"", "\\\"");
            startInfo.Arguments = $"-c \"{command} {escapedArgs}\"";
        }
    }
    // === AKHIR PERBAIKAN ===


    private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(ProcessStartInfo startInfo, int timeoutMilliseconds = DEFAULT_TIMEOUT_MS) {
        using var process = new Process { StartInfo = startInfo };
        var stdoutBuilder = new StringBuilder(); var stderrBuilder = new StringBuilder();
        var tcs = new TaskCompletionSource<(string, string, int)>();
        process.EnableRaisingEvents = true;

        // Handler untuk data stdout
        process.OutputDataReceived += (s, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
        // Handler untuk data stderr
        process.ErrorDataReceived += (s, e) => {
            if (e.Data != null) {
                stderrBuilder.AppendLine(e.Data);
                // Optional: Tampilkan stderr secara realtime jika tidak kosong
                // if (!string.IsNullOrWhiteSpace(e.Data)) AnsiConsole.MarkupLine($"[grey]stderr: {e.Data.EscapeMarkup()}[/]");
            }
         };
         // Handler saat proses selesai
        process.Exited += (s, e) => {
            // Beri sedikit waktu agar semua output/error terbaca sebelum set result
            Task.Delay(200).ContinueWith(_ => tcs.TrySetResult((stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim(), process.ExitCode)));
        };

        CancellationTokenSource? timeoutCts = null;
        try {
            if (!process.Start()) {
                 // Gagal start proses
                 return ("", $"Failed to start process: {startInfo.FileName}", -1);
            }
            // Mulai baca output/error secara async
            process.BeginOutputReadLine(); process.BeginErrorReadLine();

            // Setup timeout
            timeoutCts = new CancellationTokenSource(timeoutMilliseconds);
            // Tunggu proses selesai ATAU timeout
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));

            if (completedTask != tcs.Task) {
                 // Jika timeout tercapai sebelum proses selesai
                 throw new TaskCanceledException($"Process timed out after {timeoutMilliseconds / 1000}s");
             }
             // Jika proses selesai sebelum timeout
             return await tcs.Task;

        } catch (TaskCanceledException ex) { // Tangkap timeout dari Task.WhenAny
            AnsiConsole.MarkupLine($"[red]Timeout ({timeoutMilliseconds / 1000}s): {startInfo.FileName} {startInfo.Arguments}[/]");
            try { if (!process.HasExited) process.Kill(true); } catch { /* Ignore kill error */ }
            throw; // Lempar ulang exception timeout
        } catch (Exception ex) {
            // Tangkap error lain saat start/menjalankan proses
            AnsiConsole.MarkupLine($"[red]Failed to run '{startInfo.FileName}': {ex.Message.Split('\n').FirstOrDefault()}[/]");
            try { if (!process.HasExited) process.Kill(true); } catch { /* Ignore kill error */ }
            // Kembalikan error di stderr, exit code -1
            return (stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim() + "\n" + ex.Message, process.HasExited ? process.ExitCode : -1);
        } finally {
            timeoutCts?.Dispose(); // Pastikan CancellationTokenSource di-dispose
        }
    }

} // Akhir class ShellHelper
