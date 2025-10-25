using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;

namespace Orchestrator;

public static class ShellHelper
{
    // ... (RunGhCommand, RunCommandAsync, CreateStartInfo, RunProcessAsync DARI PART 2 TETAP SAMA) ...

    /// <summary>
    /// Menjalankan perintah shell secara interaktif di konsol saat ini.
    /// Berguna untuk tools seperti ProxySync yang punya UI sendiri.
    /// </summary>
    public static async Task RunInteractive(string command, string args, string? workingDir = null, TokenEntry? token = null, CancellationToken cancellationToken = default)
    {
        var startInfo = CreateStartInfo(command, args, token);
        if (workingDir != null)
        {
            startInfo.WorkingDirectory = workingDir;
        }

        // Override redirect settings for interactive mode
        startInfo.RedirectStandardOutput = false;
        startInfo.RedirectStandardError = false;
        startInfo.RedirectStandardInput = false; // Input akan langsung dari console
        startInfo.UseShellExecute = false; // Harus false untuk environment vars
        startInfo.CreateNoWindow = false; // Tampilkan window proses

        // Di Windows, kita mungkin perlu wrapper 'cmd /c' jika command butuh shell
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && (command == "python" || command == "pip" || command == "npm" || command == "node"))
        {
            startInfo.Arguments = $"/c \"{command} {args}\"";
            startInfo.FileName = "cmd.exe";
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            AnsiConsole.MarkupLine($"[dim]Starting interactive: {startInfo.FileName} {startInfo.Arguments}[/]");
            process.Start();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[yellow]Interactive process exited with code: {process.ExitCode}[/]");
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Interactive process cancelled.[/]");
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw; // Re-throw cancellation
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error running interactive process: {ex.Message}[/]");
            try { if (!process.HasExited) process.Kill(true); } catch { }
            // Tidak re-throw error biasa, hanya log
        }
    }
    
    // ==========================================================
    // BAGIAN KODE DI BAWAH INI SAMA DENGAN PART 2
    // ==========================================================
    
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
            // Cek spesifik error rate limit atau auth
            bool isRateLimit = stderr.Contains("API rate limit exceeded") || stderr.Contains("403");
            bool isAuthError = stderr.Contains("Bad credentials") || stderr.Contains("401");
            
            // Handle rotasi otomatis jika rate limit atau auth error
            if (isRateLimit || isAuthError)
            {
                 AnsiConsole.MarkupLine($"[red]Error ({ (isRateLimit ? "Rate Limit" : "Auth") }) detected. Attempting token rotation...[/]");
                 TokenManager.SwitchToNextToken(); 
                 // Lemparkan exception agar loop utama mencoba lagi dengan token baru
                 throw new Exception($"GH Command Failed ({(isRateLimit ? "Rate Limit/403" : "Auth/401")}): Triggering token rotation.");
            }
            
            // Jika error lain, lemparkan seperti biasa
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
            // Coba resolve command path
            FileName = FindExecutable(command), 
            Arguments = args,
            RedirectStandardOutput = true, // Default true, override di RunInteractive
            RedirectStandardError = true,  // Default true, override di RunInteractive
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
                // Set proxy untuk 'gh' dan tools lain (seperti git, pip, curl)
                startInfo.EnvironmentVariables["https_proxy"] = token.Proxy;
                startInfo.EnvironmentVariables["http_proxy"] = token.Proxy;
                startInfo.EnvironmentVariables["HTTPS_PROXY"] = token.Proxy; // Beberapa tools pakai upper case
                startInfo.EnvironmentVariables["HTTP_PROXY"] = token.Proxy;
            }
        }

        return startInfo;
    }

    /// <summary>
    /// Inti dari eksekusi proses (non-interaktif), membaca stdout/stderr secara async.
    /// </summary>
    private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(ProcessStartInfo startInfo, int timeoutMilliseconds = 120000)
    {
        using var process = new Process { StartInfo = startInfo };

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var tcs = new TaskCompletionSource<(string, string, int)>(); // Untuk handle exit

        process.EnableRaisingEvents = true;

        process.OutputDataReceived += (sender, e) => {
            if (e.Data != null) {
                stdoutBuilder.AppendLine(e.Data);
                // Jangan log output di sini lagi, terlalu verbose
                // AnsiConsole.MarkupLine($"[grey]OUT: {e.Data.EscapeMarkup()}[/]");
            }
        };
        process.ErrorDataReceived += (sender, e) => {
            if (e.Data != null) {
                stderrBuilder.AppendLine(e.Data);
                 // Hanya log error jika penting
                 if (!string.IsNullOrWhiteSpace(e.Data)) {
                    AnsiConsole.MarkupLine($"[yellow]ERR: {e.Data.EscapeMarkup()}[/]");
                 }
            }
        };
        
        process.Exited += (sender, e) => {
            // Beri waktu sedikit agar buffer output/error selesai dibaca
            Task.Delay(100).ContinueWith(_ => 
                tcs.TrySetResult((stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim(), process.ExitCode))
            );
        };

        try
        {
            if (!File.Exists(startInfo.FileName)) {
                 throw new FileNotFoundException($"Executable not found: {startInfo.FileName}");
            }

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Setup timeout
            using var timeoutCts = new CancellationTokenSource(timeoutMilliseconds);
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));

            if (completedTask == tcs.Task)
            {
                // Process finished
                timeoutCts.Cancel(); // Batalkan timer
                return await tcs.Task; // Return hasil
            }
            else
            {
                // Timeout tercapai
                throw new TaskCanceledException($"Process timed out after {timeoutMilliseconds / 1000}s");
            }
        }
        catch (TaskCanceledException) // Khusus timeout
        {
            AnsiConsole.MarkupLine($"[red]Timeout after {timeoutMilliseconds / 1000}s: {startInfo.FileName} {startInfo.Arguments}[/]");
            try { process.Kill(true); } catch { } // Force kill
            // Lemparkan lagi agar bisa ditangkap di loop utama
            throw; 
        }
        catch (Exception ex)
        {
             AnsiConsole.MarkupLine($"[red]Failed to run process '{startInfo.FileName}': {ex.Message}[/]");
             // Pastikan proses mati jika masih jalan
             try { if (!process.HasExited) process.Kill(true); } catch { }
             // Kembalikan error agar loop utama tahu ada masalah
             return (stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim() + "\n" + ex.Message, -1);
        }
    }

     /// <summary>
     /// Mencari path executable (cross-platform).
     /// </summary>
     private static string FindExecutable(string command)
     {
         // Jika path absolut, langsung kembalikan
         if (Path.IsPathFullyQualified(command) && File.Exists(command))
         {
             return command;
         }

         // Cek di PATH environment variable
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
         
         // Jika tidak ketemu, kembalikan command asli (mungkin alias atau built-in shell)
         return command; 
     }
}
