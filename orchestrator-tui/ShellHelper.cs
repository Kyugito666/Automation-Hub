using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;
using System.Net;

namespace Orchestrator;

public static class ShellHelper
{
    private const int DEFAULT_TIMEOUT_MS = 120000;
    private const int MAX_RETRY_ON_PROXY_ERROR = 2;
    private const int MAX_RETRY_ON_NETWORK_ERROR = 2;
    private const int MAX_RETRY_ON_TIMEOUT = 1;
    private static bool _isAttemptingIpAuth = false;

     public static async Task<string> RunGhCommand(TokenEntry token, string args, int timeoutMilliseconds = DEFAULT_TIMEOUT_MS)
{
    var startInfo = CreateStartInfo("gh", args, token);
    int proxyRetryCount = 0; int networkRetryCount = 0; int timeoutRetryCount = 0;
    Exception? lastException = null;

    while (true) {
        try {
            var cts = new CancellationTokenSource(timeoutMilliseconds);
            var (stdout, stderr, exitCode) = await RunProcessAsync(startInfo, cts.Token);
            
            if (exitCode != 0) {
                stderr = stderr.ToLowerInvariant();
                bool isRateLimit = stderr.Contains("api rate limit exceeded") || stderr.Contains("403");
                bool isAuthError = stderr.Contains("bad credentials") || stderr.Contains("401");
                bool isProxyAuthError = stderr.Contains("407") || stderr.Contains("proxy authentication required");
                bool isNetworkError = stderr.Contains("dial tcp") || stderr.Contains("connection refused") || stderr.Contains("i/o timeout") ||
                                      stderr.Contains("error connecting") || stderr.Contains("wsarecv") || stderr.Contains("forcibly closed") ||
                                      stderr.Contains("resolve host") || stderr.Contains("tls handshake timeout");
                bool isNotFoundError = stderr.Contains("404") || stderr.Contains("could not find");

                if (isProxyAuthError && proxyRetryCount < MAX_RETRY_ON_PROXY_ERROR) {
                    proxyRetryCount++; AnsiConsole.MarkupLine($"[yellow]Proxy Auth (407). Rotating... ({proxyRetryCount}/{MAX_RETRY_ON_PROXY_ERROR})[/]");
                    if (TokenManager.RotateProxyForToken(token)) { startInfo = CreateStartInfo("gh", args, token); await Task.Delay(2000); continue; }
                    else { AnsiConsole.MarkupLine("[red]No more proxies.[/]"); }
                }
                if (isNetworkError && networkRetryCount < MAX_RETRY_ON_NETWORK_ERROR) {
                    networkRetryCount++; AnsiConsole.MarkupLine($"[yellow]Network error. Retrying... ({networkRetryCount}/{MAX_RETRY_ON_NETWORK_ERROR})[/]");
                    await Task.Delay(4000); continue;
                }
                if ((isNetworkError || isProxyAuthError) && !_isAttemptingIpAuth) {
                    AnsiConsole.MarkupLine("[magenta]Persistent error. Attempting auto IP Auth...[/]");
                    _isAttemptingIpAuth = true;
                    bool ipAuthSuccess = await ProxyManager.RunIpAuthorizationOnlyAsync();
                    _isAttemptingIpAuth = false;
                    if (ipAuthSuccess) {
                        AnsiConsole.MarkupLine("[magenta]IP Auth OK. Retrying command...[/]");
                        proxyRetryCount = 0; networkRetryCount = 0; timeoutRetryCount = 0;
                        startInfo = CreateStartInfo("gh", args, token); await Task.Delay(2000); continue;
                    } else { AnsiConsole.MarkupLine("[red]Auto IP Auth failed. Failing.[/]"); }
                }
                if (isRateLimit || isAuthError) { string errorType = isRateLimit ? "RateLimit/403" : "Auth/401"; AnsiConsole.MarkupLine($"[red]GH Error ({errorType}).[/]"); lastException = new Exception($"GH Fail ({errorType}): {stderr.Split('\n').FirstOrDefault()?.Trim()}"); break; }
                if (isNotFoundError) { AnsiConsole.MarkupLine($"[red]GH Error (NotFound/404).[/]"); lastException = new Exception($"GH Not Found (404): {stderr.Split('\n').FirstOrDefault()?.Trim()}"); break; }
                lastException = new Exception($"gh command failed (Exit {exitCode}): {stderr.Split('\n').FirstOrDefault()?.Trim()}"); break;
            }
            return stdout;
        }
        catch (OperationCanceledException ex) {
            AnsiConsole.MarkupLine("[yellow]Command cancelled by user.[/]");
            throw;
        }
        catch (TaskCanceledException ex) {
            if (timeoutRetryCount < MAX_RETRY_ON_TIMEOUT) { timeoutRetryCount++; AnsiConsole.MarkupLine($"[yellow]Timeout. Retrying... ({timeoutRetryCount}/{MAX_RETRY_ON_TIMEOUT})[/]"); await Task.Delay(5000); continue; }
            lastException = new Exception($"Command timed out after retries.", ex); break;
        }
        catch (Exception ex) {
            lastException = ex;
            if (networkRetryCount == 0 && proxyRetryCount == 0 && timeoutRetryCount == 0) {
                networkRetryCount++; AnsiConsole.MarkupLine($"[yellow]Unexpected fail: {ex.Message.Split('\n').FirstOrDefault()?.Trim()}. Retrying once...[/]"); await Task.Delay(3000); continue;
            }
            break;
        }
    }
    throw lastException ?? new Exception("GH command failed.");
}

    public static async Task RunCommandAsync(string command, string args, string? workingDir = null, TokenEntry? token = null)
    {
        var startInfo = CreateStartInfo(command, args, token);
        if (workingDir != null) startInfo.WorkingDirectory = workingDir;
        var (_, stderr, exitCode) = await RunProcessAsync(startInfo);
        if (exitCode != 0) throw new Exception($"Command '{command}' failed (Exit Code: {exitCode}): {stderr}");
    }

     public static async Task RunInteractive(string command, string args, string? workingDir = null, TokenEntry? token = null, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = false, RedirectStandardOutput = false, RedirectStandardError = false, RedirectStandardInput = false };
        if (workingDir != null) startInfo.WorkingDirectory = workingDir;
        if (token != null) SetEnvironmentVariables(startInfo, token, command);
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
        if (token != null) SetEnvironmentVariables(startInfo, token, command);
        SetFileNameAndArgs(startInfo, command, args);
        using var process = new Process { StartInfo = startInfo };
        try {
            AnsiConsole.MarkupLine($"[bold green]▶ Starting bot FULL INTERACTIVE[/]"); AnsiConsole.MarkupLine($"[dim]Cmd: {command} {args}[/]"); AnsiConsole.MarkupLine($"[dim]Dir: {workingDir ?? "current"}[/]"); AnsiConsole.MarkupLine("[yellow]"+ new string('═', 60) +"[/]");
            process.Start();
            var processExitedTcs = new TaskCompletionSource<bool>(); process.EnableRaisingEvents = true; process.Exited += (s, e) => processExitedTcs.TrySetResult(true);
            var cancellationTcs = new TaskCompletionSource<bool>(); using var reg = cancellationToken.Register(() => cancellationTcs.TrySetResult(true));
            var completedTask = await Task.WhenAny(processExitedTcs.Task, cancellationTcs.Task);
            if (completedTask == cancellationTcs.Task) {
                try { if (!process.HasExited) { AnsiConsole.MarkupLine("\n[yellow]Terminating...[/]"); process.Kill(true); await Task.Delay(1500); } } catch { }
                AnsiConsole.MarkupLine("[yellow]"+ new string('═', 60) +"[/]"); AnsiConsole.MarkupLine("[yellow]✓ Stopped by user.[/]"); throw new OperationCanceledException();
            }
            await Task.Delay(500); AnsiConsole.MarkupLine("[yellow]"+ new string('═', 60) +"[/]");
            if (process.ExitCode == 0) AnsiConsole.MarkupLine($"[green]✓ Exited OK (Code: {process.ExitCode})[/]");
            else if (process.ExitCode == -1 || cancellationToken.IsCancellationRequested) AnsiConsole.MarkupLine($"[yellow]⚠ Terminated (Code: {process.ExitCode})[/]");
            else AnsiConsole.MarkupLine($"[red]✗ Exited ERR (Code: {process.ExitCode})[/]");
            AnsiConsole.MarkupLine("\n[dim]Press Enter...[/]"); Console.ReadLine();
        } catch (OperationCanceledException) { try { if (!process.HasExited) {process.Kill(true); await Task.Delay(1000);} } catch { } AnsiConsole.MarkupLine("\n[dim]Press Enter...[/]"); Console.ReadLine(); throw; }
        catch (Exception ex) { AnsiConsole.MarkupLine("\n[yellow]"+ new string('═', 60) +"[/]"); AnsiConsole.MarkupLine($"[red]✗ Err run bot: {ex.Message.EscapeMarkup()}[/]"); try { if (!process.HasExited) process.Kill(true); } catch { } AnsiConsole.MarkupLine("\n[dim]Press Enter...[/]"); Console.ReadLine(); throw; }
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
        if (isGhCommand) { startInfo.EnvironmentVariables["GH_TOKEN"] = token.Token; }
        if (!string.IsNullOrEmpty(token.Proxy)) {
            startInfo.EnvironmentVariables["https_proxy"] = token.Proxy; startInfo.EnvironmentVariables["http_proxy"] = token.Proxy;
            startInfo.EnvironmentVariables["HTTPS_PROXY"] = token.Proxy; startInfo.EnvironmentVariables["HTTP_PROXY"] = token.Proxy;
            startInfo.EnvironmentVariables["NO_PROXY"] = "localhost,127.0.0.1"; startInfo.EnvironmentVariables["no_proxy"] = "localhost,127.0.0.1";
        } else {
             startInfo.EnvironmentVariables.Remove("https_proxy"); startInfo.EnvironmentVariables.Remove("http_proxy");
             startInfo.EnvironmentVariables.Remove("HTTPS_PROXY"); startInfo.EnvironmentVariables.Remove("HTTP_PROXY");
        }
    }

     private static void SetFileNameAndArgs(ProcessStartInfo startInfo, string command, string args) {
         if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/c \"{command} {args}\"";
        } else {
            startInfo.FileName = "/bin/bash";
            string escapedArgs = args.Replace("\"", "\\\"");
            startInfo.Arguments = $"-c \"{command} {escapedArgs}\"";
        }
    }

    private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
{
    using var process = new Process { StartInfo = startInfo };
    var stdoutBuilder = new StringBuilder();
    var stderrBuilder = new StringBuilder();
    var tcs = new TaskCompletionSource<(string, string, int)>();

    process.EnableRaisingEvents = true;
    process.OutputDataReceived += (s, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
    process.ErrorDataReceived += (s, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };
    process.Exited += (s, e) => { Task.Delay(200).ContinueWith(_ => tcs.TrySetResult((stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim(), process.ExitCode))); };

    try {
        if (!process.Start()) { return ("", $"Failed to start: {startInfo.FileName}", -1); }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var reg = cancellationToken.Register(() => {
            try {
                if (!process.HasExited) {
                    process.Kill(true);
                    tcs.TrySetCanceled();
                }
            } catch { }
        });

        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cancellationToken));
        
        if (cancellationToken.IsCancellationRequested) {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw new OperationCanceledException();
        }

        return await tcs.Task;
    }
    catch (OperationCanceledException) {
        try { if (!process.HasExited) process.Kill(true); } catch { }
        throw;
    }
    catch (Exception ex) {
        try { if (!process.HasExited) process.Kill(true); } catch { }
        return (stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim() + "\n" + ex.Message, process.HasExited ? process.ExitCode : -1);
    }
}

} // End class ShellHelper
