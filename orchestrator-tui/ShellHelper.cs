using System.Diagnostics;
using System.Text;
using Spectre.Console;

namespace OrchestratorV2;

public static class ShellHelper
{
    public static async Task<string> RunGhCommand(TokenEntry token, string args, int timeoutMs = 60000)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.EnvironmentVariables["GH_TOKEN"] = token.Token;
        if (!string.IsNullOrEmpty(token.Proxy))
        {
            startInfo.EnvironmentVariables["https_proxy"] = token.Proxy;
            startInfo.EnvironmentVariables["http_proxy"] = token.Proxy;
            startInfo.EnvironmentVariables["HTTPS_PROXY"] = token.Proxy;
            startInfo.EnvironmentVariables["HTTP_PROXY"] = token.Proxy;
        }

        using var process = new Process { StartInfo = startInfo };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null) stdoutBuilder.AppendLine(e.Data);
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null) stderrBuilder.AppendLine(e.Data);
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(timeoutMs);
            await process.WaitForExitAsync(cts.Token);

            var stdout = stdoutBuilder.ToString().Trim();
            var stderr = stderrBuilder.ToString().Trim();

            if (process.ExitCode != 0)
            {
                bool isRateLimit = stderr.Contains("API rate limit exceeded") || stderr.Contains("403");
                bool isAuthError = stderr.Contains("Bad credentials") || stderr.Contains("401");

                if (isRateLimit || isAuthError)
                {
                    AnsiConsole.MarkupLine($"[red]Error ({(isRateLimit ? "Rate Limit" : "Auth")}) detected. Token rotation needed.[/]");
                    TokenManager.SwitchToNextToken();
                    throw new Exception($"GH Command Failed ({(isRateLimit ? "403" : "401")}): Triggering token rotation.");
                }

                throw new Exception($"gh command failed (Exit Code: {process.ExitCode}): {stderr}");
            }

            return stdout;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw new Exception($"Command timed out after {timeoutMs / 1000}s");
        }
    }

    public static async Task RunCommand(string command, string args, string? workingDir = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (workingDir != null)
            startInfo.WorkingDirectory = workingDir;

        using var process = new Process { StartInfo = startInfo };

        process.Start();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new Exception($"Command failed (Exit Code: {process.ExitCode}): {stderr}");
        }
    }
}
