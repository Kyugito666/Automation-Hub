using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;

namespace Orchestrator;

public static class ShellHelper
{
    private const int DEFAULT_TIMEOUT_MS = 60000;
    private const int MAX_RETRY_ON_PROXY_ERROR = 2;

    public static async Task<string> RunGhCommand(TokenEntry token, string args, int timeoutMilliseconds = DEFAULT_TIMEOUT_MS)
    {
        var startInfo = CreateStartInfo("gh", args, token);
        
        int retryCount = 0;
        Exception? lastException = null;

        while (retryCount <= MAX_RETRY_ON_PROXY_ERROR)
        {
            try
            {
                var (stdout, stderr, exitCode) = await RunProcessAsync(startInfo, timeoutMilliseconds);

                if (exitCode != 0)
                {
                    // Parse error type
                    bool isRateLimit = stderr.Contains("API rate limit exceeded") || stderr.Contains("403");
                    bool isAuthError = stderr.Contains("Bad credentials") || stderr.Contains("401");
                    bool isProxyError = stderr.Contains("407") || stderr.Contains("Proxy Authentication Required");
                    bool isNetworkError = stderr.Contains("dial tcp") || stderr.Contains("connection refused") || stderr.Contains("i/o timeout");
                    
                    // Handle proxy errors dengan rotasi
                    if (isProxyError && retryCount < MAX_RETRY_ON_PROXY_ERROR)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Proxy error detected (407). Attempting proxy rotation... (Retry {retryCount + 1}/{MAX_RETRY_ON_PROXY_ERROR})[/]");
                        
                        if (TokenManager.RotateProxyForToken(token))
                        {
                            // Update startInfo dengan proxy baru
                            startInfo = CreateStartInfo("gh", args, token);
                            retryCount++;
                            await Task.Delay(3000); // Wait before retry
                            continue;
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[red]No more proxies available for rotation.[/]");
                        }
                    }
                    
                    // Handle network errors dengan retry
                    if (isNetworkError && retryCount < MAX_RETRY_ON_PROXY_ERROR)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Network error detected. Retrying... ({retryCount + 1}/{MAX_RETRY_ON_PROXY_ERROR})[/]");
                        retryCount++;
                        await Task.Delay(5000);
                        continue;
                    }
                    
                    // Handle rate limit / auth errors (trigger token rotation di caller)
                    if (isRateLimit || isAuthError)
                    {
                        string errorType = isRateLimit ? "Rate Limit/403" : "Auth/401";
                        AnsiConsole.MarkupLine($"[red]Error ({errorType}) detected. Token rotation may be needed.[/]");
                        throw new Exception($"GH Command Failed ({errorType}): {stderr.Split('\n').FirstOrDefault()}");
                    }
                    
                    // Generic error
                    throw new Exception($"gh command failed (Exit Code: {exitCode}): {stderr.Split('\n').FirstOrDefault()}");
                }

                return stdout;
            }
            catch (TaskCanceledException)
            {
                if (retryCount < MAX_RETRY_ON_PROXY_ERROR)
                {
                    AnsiConsole.MarkupLine($"[yellow]Command timeout. Retrying... ({retryCount + 1}/{MAX_RETRY_ON_PROXY_ERROR})[/]");
                    retryCount++;
                    await Task.Delay(5000);
                    continue;
                }
                throw new Exception($"Command timed out after {timeoutMilliseconds}ms");
            }
            catch (Exception ex) when (retryCount < MAX_RETRY_ON_PROXY_ERROR)
            {
                lastException = ex;
                AnsiConsole.MarkupLine($"[yellow]Command failed: {ex.Message}. Retrying... ({retryCount + 1}/{MAX_RETRY_ON_PROXY_ERROR})[/]");
                retryCount++;
                await Task.Delay(3000);
                continue;
            }
            catch (Exception ex)
            {
                // Final throw after all retries
                throw;
            }
        }

        // Jika keluar loop tanpa return, throw last exception
        throw lastException ?? new Exception("Command failed after retries");
    }

    public static async Task RunCommandAsync(string command, string args, string? workingDir = null, TokenEntry? token = null)
    {
        var startInfo = CreateStartInfo(command, args, token);
        if (workingDir != null)
        {
            startInfo.WorkingDirectory = workingDir;
        }

        var (stdout, stderr, exitCode) = await RunProcessAsync(startInfo);

        if (exitCode != 0)
        {
            throw new Exception($"Command failed (Exit Code: {exitCode}): {stderr}");
        }
    }

    public static async Task RunInteractive(string command, string args, string? workingDir = null, TokenEntry? token = null, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false
        };

        if (workingDir != null)
        {
            startInfo.WorkingDirectory = workingDir;
        }

        if (token != null)
        {
            startInfo.EnvironmentVariables["GH_TOKEN"] = token.Token;
            if (!string.IsNullOrEmpty(token.Proxy))
            {
                startInfo.EnvironmentVariables["https_proxy"] = token.Proxy;
                startInfo.EnvironmentVariables["http_proxy"] = token.Proxy;
                startInfo.EnvironmentVariables["HTTPS_PROXY"] = token.Proxy;
                startInfo.EnvironmentVariables["HTTP_PROXY"] = token.Proxy;
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/c \"{command} {args}\"";
        }
        else
        {
            startInfo.FileName = command;
            startInfo.Arguments = args;
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            AnsiConsole.MarkupLine($"[dim]Starting interactive: {startInfo.FileName} {startInfo.Arguments}[/]");
            process.Start();
            
            cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
                catch { }
            });

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0 && process.ExitCode != -1)
            {
                AnsiConsole.MarkupLine($"[yellow]Interactive process exited with code: {process.ExitCode}[/]");
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Interactive process cancelled.[/]");
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error running interactive process: {ex.Message}[/]");
            try { if (!process.HasExited) process.Kill(true); } catch { }
        }
    }

    private static ProcessStartInfo CreateStartInfo(string command, string args, TokenEntry? token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FindExecutable(command),
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (token != null)
        {
            startInfo.EnvironmentVariables["GH_TOKEN"] = token.Token;
            if (!string.IsNullOrEmpty(token.Proxy))
            {
                startInfo.EnvironmentVariables["https_proxy"] = token.Proxy;
                startInfo.EnvironmentVariables["http_proxy"] = token.Proxy;
                startInfo.EnvironmentVariables["HTTPS_PROXY"] = token.Proxy;
                startInfo.EnvironmentVariables["HTTP_PROXY"] = token.Proxy;
            }
        }

        return startInfo;
    }

    private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(ProcessStartInfo startInfo, int timeoutMilliseconds = 120000)
    {
        using var process = new Process { StartInfo = startInfo };

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var tcs = new TaskCompletionSource<(string, string, int)>();

        process.EnableRaisingEvents = true;

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                stdoutBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                stderrBuilder.AppendLine(e.Data);
                
                // Only log non-trivial errors to console
                if (!string.IsNullOrWhiteSpace(e.Data) && 
                    !e.Data.Contains("Flag shorthand") && // Suppress deprecation warnings
                    !e.Data.StartsWith("✓")) // Suppress success messages from gh CLI
                {
                    AnsiConsole.MarkupLine($"[yellow]ERR: {e.Data.EscapeMarkup()}[/]");
                }
            }
        };
        
        process.Exited += (sender, e) =>
        {
            Task.Delay(100).ContinueWith(_ => 
                tcs.TrySetResult((stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim(), process.ExitCode))
            );
        };

        try
        {
            if (!File.Exists(startInfo.FileName))
            {
                throw new FileNotFoundException($"Executable not found: {startInfo.FileName}");
            }

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(timeoutMilliseconds);
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));

            if (completedTask == tcs.Task)
            {
                timeoutCts.Cancel();
                return await tcs.Task;
            }
            else
            {
                throw new TaskCanceledException($"Process timed out after {timeoutMilliseconds / 1000}s");
            }
        }
        catch (TaskCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Timeout after {timeoutMilliseconds / 1000}s: {startInfo.FileName} {startInfo.Arguments}[/]");
            try { process.Kill(true); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to run process '{startInfo.FileName}': {ex.Message}[/]");
            try { if (!process.HasExited) process.Kill(true); } catch { }
            return (stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim() + "\n" + ex.Message, -1);
        }
    }

    private static string FindExecutable(string command)
    {
        if (Path.IsPathFullyQualified(command) && File.Exists(command))
        {
            return command;
        }

        var paths = Environment.GetEnvironmentVariable("PATH");
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new[] { "", ".exe", ".cmd", ".bat" } : new[] { "" };

        foreach (var path in paths?.Split(Path.PathSeparator) ?? Array.Empty<string>())
        {
            foreach (var ext in extensions)
            {
                var fullPath = Path.Combine(path, command + ext);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }
        
        return command;
    }
}
