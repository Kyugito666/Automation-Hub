using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;

namespace Orchestrator;

public static class ShellHelper
{
    /// <summary>
    /// Menjalankan perintah 'gh' (GitHub CLI) dengan token dan proxy yang sesuai.
    /// Ini adalah wrapper utama untuk semua interaksi Codespace.
    /// </summary>
    /// <param name="token">TokenEntry yang berisi PAT dan Proxy</param>
    /// <param name="args">Argument untuk 'gh' (misal: "codespace list --json name")</param>
    /// <param name="timeoutMilliseconds">Opsional timeout</param>
    /// <returns>Hasil stdout jika sukses</returns>
    /// <exception cref="Exception">Jika command gagal</exception>
    public static async Task<string> RunGhCommand(TokenEntry token, string args, int timeoutMilliseconds = 60000)
    {
        var startInfo = CreateStartInfo("gh", args, token);
        
        var (stdout, stderr, exitCode) = await RunProcessAsync(startInfo, timeoutMilliseconds);

        if (exitCode != 0)
        {
            if (stderr.Contains("Bad credentials") || stderr.Contains("401"))
            {
                TokenManager.SwitchToNextToken();
            }
            throw new Exception($"gh command failed (Exit Code: {exitCode}): {stderr}");
        }

        return stdout;
    }

    /// <summary>
    /// Menjalankan perintah shell umum (seperti python, git) dengan proxy opsional.
    /// </summary>
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

    /// <summary>
    /// Helper untuk membuat ProcessStartInfo dengan environment variables (token+proxy).
    /// </summary>
    private static ProcessStartInfo CreateStartInfo(string command, string args, TokenEntry? token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Inject environment variables
        if (token != null)
        {
            startInfo.EnvironmentVariables["GH_TOKEN"] = token.Token;
            if (!string.IsNullOrEmpty(token.Proxy))
            {
                // Set proxy untuk 'gh' dan tools lain (seperti git)
                startInfo.EnvironmentVariables["https_proxy"] = token.Proxy;
                startInfo.EnvironmentVariables["http_proxy"] = token.Proxy;
            }
        }

        return startInfo;
    }

    /// <summary>
    /// Inti dari eksekusi proses, membaca stdout/stderr secara async.
    /// </summary>
    private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(ProcessStartInfo startInfo, int timeoutMilliseconds = 120000)
    {
        using var process = new Process { StartInfo = startInfo };

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, e) => {
            if (e.Data != null) {
                stdoutBuilder.AppendLine(e.Data);
                AnsiConsole.MarkupLine($"[grey]OUT: {e.Data.EscapeMarkup()}[/]");
            }
        };
        process.ErrorDataReceived += (sender, e) => {
            if (e.Data != null) {
                stderrBuilder.AppendLine(e.Data);
                AnsiConsole.MarkupLine($"[yellow]ERR: {e.Data.EscapeMarkup()}[/]");
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(new CancellationTokenSource(timeoutMilliseconds).Token);
            
            return (stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim(), process.ExitCode);
        }
        catch (TaskCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch {}
            throw new Exception($"Process timed out after {timeoutMilliseconds / 1000}s: {startInfo.FileName} {startInfo.Arguments}");
        }
        catch (Exception ex)
        {
            return (stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim() + "\n" + ex.Message, -1);
        }
    }
}
