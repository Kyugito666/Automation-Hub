using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;

namespace Orchestrator;

public static class ShellHelper
{
    private const int DEFAULT_TIMEOUT_MS = 120000;
    private const int MAX_RETRY_ON_PROXY_ERROR = 2;
    private const int MAX_RETRY_ON_NETWORK_ERROR = 2;
    private const int MAX_RETRY_ON_TIMEOUT = 1;

    public static async Task<string> RunGhCommand(TokenEntry token, string args, int timeoutMilliseconds = DEFAULT_TIMEOUT_MS)
    {
        // === FIX: Gunakan "gh" langsung, jangan hardcode path ===
        var startInfo = CreateStartInfo("gh", args, token);
        // === AKHIR FIX ===

        int proxyRetryCount = 0;
        int networkRetryCount = 0;
        int timeoutRetryCount = 0;
        Exception? lastException = null;

        while (true) // Loop until success or fatal error
        {
            try
            {
                var (stdout, stderr, exitCode) = await RunProcessAsync(startInfo, timeoutMilliseconds);

                if (exitCode != 0)
                {
                    bool isRateLimit = stderr.Contains("API rate limit exceeded") || stderr.Contains("403");
                    bool isAuthError = stderr.Contains("Bad credentials") || stderr.Contains("401");
                    bool isProxyError = stderr.Contains("407") || stderr.Contains("Proxy Authentication Required");
                    bool isNetworkError = stderr.Contains("dial tcp") || stderr.Contains("connection refused") || stderr.Contains("i/o timeout") || stderr.Contains("error connecting to http");
                    bool isNotFoundError = stderr.Contains("404") || stderr.Contains("Could not find");

                    if (isProxyError && proxyRetryCount < MAX_RETRY_ON_PROXY_ERROR)
                    {
                        proxyRetryCount++;
                        AnsiConsole.MarkupLine($"[yellow]Proxy error (407). Rotating... (Retry {proxyRetryCount}/{MAX_RETRY_ON_PROXY_ERROR})[/]");
                        if (TokenManager.RotateProxyForToken(token)) { startInfo = CreateStartInfo("gh", args, token); await Task.Delay(3000); continue; }
                        else AnsiConsole.MarkupLine("[red]No more proxies.[/]");
                    }

                    if (isNetworkError && networkRetryCount < MAX_RETRY_ON_NETWORK_ERROR)
                    {
                        networkRetryCount++;
                        AnsiConsole.MarkupLine($"[yellow]Network error. Retrying... ({networkRetryCount}/{MAX_RETRY_ON_NETWORK_ERROR})[/]");
                        await Task.Delay(5000); continue;
                    }

                    if (isRateLimit || isAuthError) {
                        string errorType = isRateLimit ? "Rate Limit/403" : "Auth/401";
                        AnsiConsole.MarkupLine($"[red]Error ({errorType}). Token rotation needed?[/]");
                        lastException = new Exception($"GH Fail ({errorType}): {stderr.Split('\n').FirstOrDefault()?.Trim()}");
                        break; // Exit loop to throw
                    }

                     if (isNotFoundError) {
                        lastException = new Exception($"GH Not Found (404): {stderr.Split('\n').FirstOrDefault()?.Trim()}");
                        break; // Exit loop to throw 404
                    }

                    // Other errors, do not retry
                    lastException = new Exception($"gh command failed (Exit Code: {exitCode}): {stderr.Split('\n').FirstOrDefault()?.Trim()}");
                    break; // Exit loop to throw other errors
                }
                return stdout; // Success, exit loop
            }
            catch (TaskCanceledException ex) // Timeout
            {
                if (timeoutRetryCount < MAX_RETRY_ON_TIMEOUT) {
                    timeoutRetryCount++;
                    AnsiConsole.MarkupLine($"[yellow]Command timeout ({timeoutMilliseconds / 1000}s). Retrying... ({timeoutRetryCount}/{MAX_RETRY_ON_TIMEOUT})[/]");
                    await Task.Delay(5000); continue;
                }
                lastException = new Exception($"Command timed out after {timeoutMilliseconds}ms and {MAX_RETRY_ON_TIMEOUT} retry.", ex);
                break; // Exit loop to throw timeout
            }
            catch (Exception ex)
            {
                lastException = ex; // Store the exception
                // Retry once for unexpected errors (like gh not found initially)
                if (networkRetryCount == 0 && proxyRetryCount == 0 && timeoutRetryCount == 0) {
                    networkRetryCount++; // Count as a network retry attempt
                     AnsiConsole.MarkupLine($"[yellow]Unexpected command fail: {ex.Message.Split('\n').FirstOrDefault()?.Trim()}. Retrying once...[/]");
                     await Task.Delay(3000); continue;
                }
                break; // Exit loop if already retried or other fatal error
            }
        } // End While

        // Throw the last recorded exception if loop exited via break
        throw lastException ?? new Exception("GH command failed after retries.");
    }


    public static async Task RunCommandAsync(string command, string args, string? workingDir = null, TokenEntry? token = null)
    {
        var startInfo = CreateStartInfo(command, args, token);
        if (workingDir != null) startInfo.WorkingDirectory = workingDir;
        var (_, stderr, exitCode) = await RunProcessAsync(startInfo);
        if (exitCode != 0) throw new Exception($"Command failed (Exit Code: {exitCode}): {stderr}");
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
            if (process.ExitCode != 0 && process.ExitCode != -1 && !cancellationToken.IsCancellationRequested) AnsiConsole.MarkupLine($"[yellow]Interactive exit code: {process.ExitCode}[/]");
        } catch (OperationCanceledException) { AnsiConsole.MarkupLine("[yellow]Interactive cancelled.[/]"); try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Err interactive: {ex.Message}[/]"); try { if (!process.HasExited) process.Kill(true); } catch { } }
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

            if (completedTask == cancellationTcs.Task) { // Cancelled
                try { if (!process.HasExited) { AnsiConsole.MarkupLine("\n[yellow]Sending termination...[/]"); process.Kill(true); await Task.Delay(1500); } } catch { }
                AnsiConsole.MarkupLine("[yellow]"+ new string('═', 60) +"[/]"); AnsiConsole.MarkupLine("[yellow]✓ Bot stopped by user (Ctrl+C)[/]");
                throw new OperationCanceledException(); // Rethrow for Program.cs to handle
            }
            // Exited normally
            await Task.Delay(500); AnsiConsole.MarkupLine("[yellow]"+ new string('═', 60) +"[/]");
            if (process.ExitCode == 0) AnsiConsole.MarkupLine($"[green]✓ Bot exited OK (Code: {process.ExitCode})[/]");
            else if (process.ExitCode == -1 || cancellationToken.IsCancellationRequested) AnsiConsole.MarkupLine($"[yellow]⚠ Bot terminated (Code: {process.ExitCode})[/]"); // Treat -1 as terminated
            else AnsiConsole.MarkupLine($"[red]✗ Bot exited ERR (Code: {process.ExitCode})[/]");
            AnsiConsole.MarkupLine("\n[dim]Press Enter to return...[/]"); Console.ReadLine();
        } catch (OperationCanceledException) { try { if (!process.HasExited) {process.Kill(true); await Task.Delay(1000);} } catch { } AnsiConsole.MarkupLine("\n[dim]Press Enter to return...[/]"); Console.ReadLine(); throw; } // Let Program.cs show the cancelled message
        catch (Exception ex) { AnsiConsole.MarkupLine("\n[yellow]"+ new string('═', 60) +"[/]"); AnsiConsole.MarkupLine($"[red]✗ Err run bot: {ex.Message.EscapeMarkup()}[/]"); try { if (!process.HasExited) process.Kill(true); } catch { } AnsiConsole.MarkupLine("\n[dim]Press Enter to return...[/]"); Console.ReadLine(); throw; }
    }

    private static ProcessStartInfo CreateStartInfo(string command, string args, TokenEntry? token) {
        // === FIX: Gunakan FindExecutable untuk mencari di PATH ===
        var startInfo = new ProcessStartInfo {
            FileName = FindExecutable(command), // Cari 'gh' atau command lain di PATH
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // === AKHIR FIX ===
        if (token != null) SetEnvironmentVariables(startInfo, token);
        return startInfo;
    }

     private static void SetEnvironmentVariables(ProcessStartInfo startInfo, TokenEntry token) {
        startInfo.EnvironmentVariables["GH_TOKEN"] = token.Token;
        if (!string.IsNullOrEmpty(token.Proxy)) {
            startInfo.EnvironmentVariables["https_proxy"] = token.Proxy; startInfo.EnvironmentVariables["http_proxy"] = token.Proxy;
            startInfo.EnvironmentVariables["HTTPS_PROXY"] = token.Proxy; startInfo.EnvironmentVariables["HTTP_PROXY"] = token.Proxy;
            startInfo.EnvironmentVariables["NO_PROXY"] = "localhost,127.0.0.1"; startInfo.EnvironmentVariables["no_proxy"] = "localhost,127.0.0.1";
        }
    }

    private static void SetFileNameAndArgs(ProcessStartInfo startInfo, string command, string args) {
         if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            startInfo.FileName = FindExecutable("cmd.exe"); // Cari cmd.exe di PATH
            string targetExe = FindExecutable(command); // Cari target command (misal python.exe) di PATH
            string quotedCommand = targetExe.Contains(' ') ? $"\"{targetExe}\"" : targetExe;
            startInfo.Arguments = $"/c {quotedCommand} {args}";
        } else {
            startInfo.FileName = "/bin/bash"; // Common path for bash
            string targetExe = FindExecutable(command); // Cari target command di PATH
            // Escape args for bash shell if necessary (simple escaping here)
            string escapedArgs = args.Replace("\"", "\\\"");
            string escapedCommand = targetExe.Contains(' ') ? $"\\\"{targetExe}\\\"" : targetExe; // Quote if path has spaces
            startInfo.Arguments = $"-c \"{escapedCommand} {escapedArgs}\"";
        }
    }

    private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(ProcessStartInfo startInfo, int timeoutMilliseconds = DEFAULT_TIMEOUT_MS) {
        using var process = new Process { StartInfo = startInfo };
        var stdoutBuilder = new StringBuilder(); var stderrBuilder = new StringBuilder();
        var tcs = new TaskCompletionSource<(string, string, int)>();
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (s, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) { stderrBuilder.AppendLine(e.Data); /* Optional: Log non-empty stderr lines */ if (!string.IsNullOrWhiteSpace(e.Data)) AnsiConsole.MarkupLine($"[grey]stderr: {e.Data.EscapeMarkup()}[/]"); } };
        process.Exited += (s, e) => {
            // Beri waktu sedikit agar output/error stream selesai dibaca
            Task.Delay(200).ContinueWith(_ => tcs.TrySetResult((stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim(), process.ExitCode)));
        };
        CancellationTokenSource? timeoutCts = null;
        try {
            if (!File.Exists(startInfo.FileName)) {
                 throw new FileNotFoundException($"Executable not found: {startInfo.FileName}. Ensure it's in your PATH.");
            }
            process.Start();
            process.BeginOutputReadLine(); process.BeginErrorReadLine();

            timeoutCts = new CancellationTokenSource(timeoutMilliseconds);
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));

            if (completedTask != tcs.Task) { // Timeout occurred
                 throw new TaskCanceledException($"Process timed out after {timeoutMilliseconds / 1000}s");
             }
             // Process finished before timeout
             return await tcs.Task; // Return result

        } catch (TaskCanceledException) { // Catch timeout specifically
            AnsiConsole.MarkupLine($"[red]Timeout ({timeoutMilliseconds / 1000}s): {startInfo.FileName} {startInfo.Arguments}[/]");
            try { process.Kill(true); } catch { /* Ignore kill errors */ }
            // Rethrow agar RunGhCommand bisa handle retry timeout
            throw;
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Failed run '{startInfo.FileName}': {ex.Message}[/]");
            try { if (!process.HasExited) process.Kill(true); } catch { /* Ignore kill errors */ }
             // Kembalikan error message di stderr
            return (stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim() + "\n" + ex.Message, process.HasExited ? process.ExitCode : -1);
        } finally {
            timeoutCts?.Dispose();
        }
    }

    // === PERBAIKAN: Fungsi FindExecutable (Lebih Robust) ===
    private static string FindExecutable(string command)
    {
        // 1. Jika path absolut, langsung return
        if (Path.IsPathFullyQualified(command) && File.Exists(command)) {
            return command;
        }

        // 2. Cek apakah command itu sendiri adalah path file (misal './script.sh')
        if (File.Exists(command)) {
             return Path.GetFullPath(command);
        }

        // 3. Cari di PATH Environment Variable
        var paths = Environment.GetEnvironmentVariable("PATH");
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (Environment.GetEnvironmentVariable("PATHEXT")?.Split(Path.PathSeparator) ?? new[] { ".COM", ".EXE", ".BAT", ".CMD" })
            : new[] { "" }; // Linux/macOS tidak perlu extension

        if (paths != null) {
            foreach (var path in paths.Split(Path.PathSeparator)) {
                foreach (var ext in extensions) {
                    // Pastikan ext dimulai dengan "." jika perlu (Windows)
                    string effectiveExt = (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !string.IsNullOrEmpty(ext) && !ext.StartsWith(".")) ? "." + ext : ext;
                    var fullPath = Path.Combine(path, command + effectiveExt);
                    try {
                        if (File.Exists(fullPath)) {
                            // AnsiConsole.MarkupLine($"[grey]FindExecutable: Found '{command}' at '{fullPath}'[/]"); // Debug log
                            return fullPath;
                        }
                    } catch (System.Security.SecurityException) {
                        // Abaikan path yang tidak bisa diakses
                    } catch (UnauthorizedAccessException) {
                        // Abaikan path yang tidak bisa diakses
                    }
                }
            }
        }

        // 4. Jika tidak ketemu, return nama command asli (biarkan OS yang handle/error)
        AnsiConsole.MarkupLine($"[yellow]Warn: Cannot find '{command}' in PATH. Using command name directly. Ensure '{command}' is accessible.[/]");
        return command;
    }
    // === AKHIR PERBAIKAN ===
}

