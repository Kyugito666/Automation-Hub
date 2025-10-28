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
        var startInfo = CreateStartInfo("gh", args, token);

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
                    bool isRateLimit = stderr.Contains("API rate limit exceeded") || stderr.Contains("403");
                    bool isAuthError = stderr.Contains("Bad credentials") || stderr.Contains("401");
                    bool isProxyError = stderr.Contains("407") || stderr.Contains("Proxy Authentication Required");
                    
                    // === PERBAIKAN: Tambahkan deteksi error "wsarecv" / "forcibly closed" ===
                    bool isNetworkError = stderr.Contains("dial tcp") || 
                                          stderr.Contains("connection refused") || 
                                          stderr.Contains("i/o timeout") || 
                                          stderr.Contains("error connecting to http") ||
                                          stderr.Contains("wsarecv") || // <-- TAMBAH INI
                                          stderr.Contains("forcibly closed"); // <-- TAMBAH INI
                    // === AKHIR PERBAIKAN ===
                                          
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
                        AnsiConsole.MarkupLine($"[yellow]Network error ({stderr.Split('\n').FirstOrDefault()?.Trim()}). Retrying... ({networkRetryCount}/{MAX_RETRY_ON_NETWORK_ERROR})[/]");
                        await Task.Delay(5000); continue;
                    }

                    if (isRateLimit || isAuthError) {
                        string errorType = isRateLimit ? "Rate Limit/403" : "Auth/401";
                        AnsiConsole.MarkupLine($"[red]Error ({errorType}). Token rotation needed?[/]");
                        lastException = new Exception($"GH Fail ({errorType}): {stderr.Split('\n').FirstOrDefault()?.Trim()}");
                        break;
                    }

                     if (isNotFoundError) {
                        lastException = new Exception($"GH Not Found (404): {stderr.Split('\n').FirstOrDefault()?.Trim()}");
                        break;
                    }

                    lastException = new Exception($"gh command failed (Exit Code: {exitCode}): {stderr.Split('\n').FirstOrDefault()?.Trim()}");
                    break;
                }
                return stdout;
            }
            catch (TaskCanceledException ex)
            {
                if (timeoutRetryCount < MAX_RETRY_ON_TIMEOUT) {
                    timeoutRetryCount++;
                    AnsiConsole.MarkupLine($"[yellow]Command timeout ({timeoutMilliseconds / 1000}s). Retrying... ({timeoutRetryCount}/{MAX_RETRY_ON_TIMEOUT})[/]");
                    await Task.Delay(5000); continue;
                }
                lastException = new Exception($"Command timed out after {timeoutMilliseconds}ms and {MAX_RETRY_ON_TIMEOUT} retry.", ex);
                break;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (networkRetryCount == 0 && proxyRetryCount == 0 && timeoutRetryCount == 0) {
                    networkRetryCount++;
                     AnsiConsole.MarkupLine($"[yellow]Unexpected command fail: {ex.Message.Split('\n').FirstOrDefault()?.Trim()}. Retrying once...[/]");
                     await Task.Delay(3000); continue;
                }
                break;
            }
        }

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

            if (completedTask == cancellationTcs.Task) {
                try { if (!process.HasExited) { AnsiConsole.MarkupLine("\n[yellow]Sending termination...[/]"); process.Kill(true); await Task.Delay(1500); } } catch { }
                AnsiConsole.MarkupLine("[yellow]"+ new string('═', 60) +"[/]"); AnsiConsole.MarkupLine("[yellow]✓ Bot stopped by user (Ctrl+C)[/]");
                throw new OperationCanceledException();
            }
            await Task.Delay(500); AnsiConsole.MarkupLine("[yellow]"+ new string('═', 60) +"[/]");
            if (process.ExitCode == 0) AnsiConsole.MarkupLine($"[green]✓ Bot exited OK (Code: {process.ExitCode})[/]");
            else if (process.ExitCode == -1 || cancellationToken.IsCancellationRequested) AnsiConsole.MarkupLine($"[yellow]⚠ Bot terminated (Code: {process.ExitCode})[/]");
            else AnsiConsole.MarkupLine($"[red]✗ Bot exited ERR (Code: {process.ExitCode})[/]");
            AnsiConsole.MarkupLine("\n[dim]Press Enter to return...[/]"); Console.ReadLine();
        } catch (OperationCanceledException) { try { if (!process.HasExited) {process.Kill(true); await Task.Delay(1000);} } catch { } AnsiConsole.MarkupLine("\n[dim]Press Enter to return...[/]"); Console.ReadLine(); throw; }
        catch (Exception ex) { AnsiConsole.MarkupLine("\n[yellow]"+ new string('═', 60) +"[/]"); AnsiConsole.MarkupLine($"[red]✗ Err run bot: {ex.Message.EscapeMarkup()}[/]"); try { if (!process.HasExited) process.Kill(true); } catch { } AnsiConsole.MarkupLine("\n[dim]Press Enter to return...[/]"); Console.ReadLine(); throw; }
    }

    private static ProcessStartInfo CreateStartInfo(string command, string args, TokenEntry? token) {
        var startInfo = new ProcessStartInfo {
            FileName = "gh",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
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
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/c {command} {args}";
        } else {
            startInfo.FileName = "/bin/bash";
            startInfo.Arguments = $"-c \"{command} {args}\"";
        }
    }

    private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(ProcessStartInfo startInfo, int timeoutMilliseconds = DEFAULT_TIMEOUT_MS) {
        using var process = new Process { StartInfo = startInfo };
        var stdoutBuilder = new StringBuilder(); var stderrBuilder = new StringBuilder();
        var tcs = new TaskCompletionSource<(string, string, int)>();
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (s, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) { stderrBuilder.AppendLine(e.Data); if (!string.IsNullOrWhiteSpace(e.Data)) AnsiConsole.MarkupLine($"[grey]stderr: {e.Data.EscapeMarkup()}[/]"); } };
        process.Exited += (s, e) => {
            Task.Delay(200).ContinueWith(_ => tcs.TrySetResult((stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim(), process.ExitCode)));
        };
        CancellationTokenSource? timeoutCts = null;
        try {
            process.Start();
            process.BeginOutputReadLine(); process.BeginErrorReadLine();

            timeoutCts = new CancellationTokenSource(timeoutMilliseconds);
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));

            if (completedTask != tcs.Task) {
                 throw new TaskCanceledException($"Process timed out after {timeoutMilliseconds / 1000}s");
             }
             return await tcs.Task;

        } catch (TaskCanceledException) {
            AnsiConsole.MarkupLine($"[red]Timeout ({timeoutMilliseconds / 1000}s): {startInfo.FileName} {startInfo.Arguments}[/]");
            try { process.Kill(true); } catch { }
            throw;
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Failed run '{startInfo.FileName}': {ex.Message}[/]");
            try { if (!process.HasExited) process.Kill(true); } catch { }
            return (stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim() + "\n" + ex.Message, process.HasExited ? process.ExitCode : -1);
        } finally {
            timeoutCts?.Dispose();
        }
    }
}
