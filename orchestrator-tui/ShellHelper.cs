using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;

namespace Orchestrator;

public static class ShellHelper
{
    public static async Task<string> RunGhCommand(TokenEntry token, string args, int timeoutMilliseconds = 60000)
    {
        var startInfo = CreateStartInfo("gh", args, token);
        
        var (stdout, stderr, exitCode) = await RunProcessAsync(startInfo, timeoutMilliseconds);

        if (exitCode != 0)
        {
            bool isRateLimit = stderr.Contains("API rate limit exceeded") || stderr.Contains("403");
            bool isAuthError = stderr.Contains("Bad credentials") || stderr.Contains("401");
            
            if (isRateLimit || isAuthError)
            {
                AnsiConsole.MarkupLine($"[red]Error ({(isRateLimit ? "Rate Limit" : "Auth")}) detected. Attempting token rotation...[/]");
                TokenManager.SwitchToNextToken();
                throw new Exception($"GH Command Failed ({(isRateLimit ? "Rate Limit/403" : "Auth/401")}): Triggering token rotation.");
            }
            
            throw new Exception($"gh command failed (Exit Code: {exitCode}): {stderr}");
        }

        return stdout;
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
                if (!string.IsNullOrWhiteSpace(e.Data))
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
