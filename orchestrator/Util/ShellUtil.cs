using Spectre.Console;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.IO;
using Orchestrator.Core; // Diperlukan untuk TokenEntry

namespace Orchestrator.Util
{
    internal static class ShellUtil
    {
        internal static ProcessStartInfo CreateStartInfo(string executable, string args, TokenEntry token, bool useProxy)
        {
            var startInfo = new ProcessStartInfo();
            
            string ghPath = FindExecutablePath(executable) ?? executable;
            string escapedExe = $"\"{ghPath}\"";
            string escapedArgs = args; 

            // Proxy Environment
            if (useProxy && TokenManager.IsProxyGloballyEnabled())
            {
                string? proxyUrl = token.Proxy; 
                if (string.IsNullOrEmpty(proxyUrl))
                {
                    AnsiConsole.MarkupLine("[yellow]WARN: Proxy enabled but token has no proxy URL.[/]");
                }
                else
                {
                    startInfo.EnvironmentVariables["HTTPS_PROXY"] = proxyUrl;
                    startInfo.EnvironmentVariables["HTTP_PROXY"] = proxyUrl;
                }
            }
            else
            {
                startInfo.EnvironmentVariables.Remove("HTTPS_PROXY");
                startInfo.EnvironmentVariables.Remove("HTTP_PROXY");
            }

            // GitHub Token Environment
            if (!string.IsNullOrEmpty(token.Token))
            {
                startInfo.EnvironmentVariables["GH_TOKEN"] = token.Token;
            }
            else
            {
                startInfo.EnvironmentVariables.Remove("GH_TOKEN");
            }
            
            // Konfigurasi standar
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            // Logika spesifik OS untuk eksekusi
            if (OperatingSystem.IsWindows())
            {
                startInfo.FileName = "cmd.exe";
                // Wrap SEMUA argumen dalam kutip ganda untuk /c
                // Ini memastikan "gh" (jika ada spasi di path) dieksekusi sebagai satu kesatuan
                // dan semua argumennya (escapedArgs) diteruskan apa adanya.
                startInfo.Arguments = $"/c \"\"{escapedExe}\" {escapedArgs}\"";
            }
            else
            {
                startInfo.FileName = escapedExe; // "gh"
                startInfo.Arguments = escapedArgs; // sisa argumen
            }
            
            return startInfo;
        }

        private static string? FindExecutablePath(string exeName)
        {
            // Tambahkan .exe di Windows jika belum ada
            if (OperatingSystem.IsWindows() && !exeName.EndsWith(".exe"))
            {
                exeName += ".exe";
            }

            // 1. Cek di direktori aplikasi (buat rilis portable)
            string appDir = AppContext.BaseDirectory;
            string appDirPath = Path.Combine(appDir, exeName);
            if (File.Exists(appDirPath))
            {
                return appDirPath;
            }

            // 2. Cek di PATH environment variable
            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (pathVar != null)
            {
                foreach (var path in pathVar.Split(Path.PathSeparator))
                {
                    try
                    {
                        string fullPath = Path.Combine(path, exeName);
                        if (File.Exists(fullPath))
                        {
                            return fullPath;
                        }
                    }
                    catch (Exception) { /* Abaikan path invalid */ }
                }
            }
            
            // 3. Cek di lokasi instalasi 'gh' standar Windows
            if (OperatingSystem.IsWindows())
            {
                string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string ghStdPath = Path.Combine(programFiles, "GitHub CLI", "gh.exe");
                if (File.Exists(ghStdPath))
                {
                    return ghStdPath;
                }
            }

            return null; // Tidak ditemukan, 'gh' mungkin global
        }


        internal static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
        {
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            using (var process = new Process { StartInfo = startInfo })
            {
                process.EnableRaisingEvents = true;
                
                var outputWaitHandle = new TaskCompletionSource<bool>();
                var errorWaitHandle = new TaskCompletionSource<bool>();

                process.OutputDataReceived += (s, e) => {
                    if (e.Data == null) {
                        outputWaitHandle.TrySetResult(true);
                    } else {
                        stdoutBuilder.AppendLine(e.Data);
                    }
                };
                process.ErrorDataReceived += (s, e) => {
                    if (e.Data == null) {
                        errorWaitHandle.TrySetResult(true);
                    } else {
                        stderrBuilder.AppendLine(e.Data);
                    }
                };

                try
                {
                    if (!process.Start())
                    {
                        throw new Exception($"Failed to start process: {startInfo.FileName}");
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // CancellationTokenRegistration
                    using (var reg = cancellationToken.Register(() => {
                        try { 
                            AnsiConsole.MarkupLine("[yellow]WARN: Process cancellation triggered. Killing process...[/]");
                            process.Kill(true); 
                        } 
                        catch (InvalidOperationException) { /* Proses sudah selesai */ }
                        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error killing process: {ex.Message.EscapeMarkup()}[/]"); }
                    }))
                    {
                        await process.WaitForExitAsync(cancellationToken);
                    }

                    // Pastikan semua output async selesai dibaca
                    await Task.WhenAll(outputWaitHandle.Task, errorWaitHandle.Task);

                    return (stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim(), process.ExitCode);
                }
                catch (OperationCanceledException)
                {
                    return (stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim(), -1);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]FATAL ShellUtil.RunProcessAsync: {ex.Message.EscapeMarkup()}[/]");
                    return (stdoutBuilder.ToString().Trim(), "FATAL EXCEPTION: " + ex.Message, -1);
                }
            }
        }

        // === FUNGSI STREAMING BARU ===
        internal static async Task<(string stdout, string stderr, int exitCode)> RunProcessAndStreamOutputAsync(
            ProcessStartInfo startInfo, 
            CancellationToken cancellationToken,
            Func<string, bool> onStdOutLine)
        {
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            using (var process = new Process { StartInfo = startInfo })
            {
                process.EnableRaisingEvents = true;
                
                var outputWaitHandle = new TaskCompletionSource<bool>();
                var errorWaitHandle = new TaskCompletionSource<bool>();

                bool stopStreaming = false;

                process.OutputDataReceived += (s, e) => {
                    if (e.Data == null) {
                        outputWaitHandle.TrySetResult(true);
                    } else if (!stopStreaming) {
                        stdoutBuilder.AppendLine(e.Data);
                        try {
                            if (onStdOutLine(e.Data))
                            {
                                stopStreaming = true;
                            }
                        } catch (Exception ex) {
                            AnsiConsole.MarkupLine($"[red]Error in StdOut callback: {ex.Message.EscapeMarkup()}[/]");
                        }
                    }
                };
                process.ErrorDataReceived += (s, e) => {
                    if (e.Data == null) {
                        errorWaitHandle.TrySetResult(true);
                    } else if (!stopStreaming) {
                        stderrBuilder.AppendLine(e.Data);
                        try {
                            // === PERBAIKAN: Ganti style [REMOTE_ERR] ===
                            AnsiConsole.MarkupLine($"[red]REMOTE_ERR:[/] {e.Data.EscapeMarkup()}");
                            // === AKHIR PERBAIKAN ===
                        } catch (Exception ex) {
                             AnsiConsole.MarkupLine($"[red]Error in StdErr callback: {ex.Message.EscapeMarkup()}[/]");
                        }
                    }
                };

                try
                {
                    if (!process.Start())
                    {
                        throw new Exception($"Failed to start process: {startInfo.FileName}");
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    using (var reg = cancellationToken.Register(() => {
                        try { 
                            AnsiConsole.MarkupLine("[yellow]WARN: Stream cancellation triggered. Killing process...[/]");
                            process.Kill(true); 
                            stopStreaming = true;
                        } 
                        catch (InvalidOperationException) { /* Proses sudah selesai */ }
                        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error killing stream process: {ex.Message.EscapeMarkup()}[/]"); }
                    }))
                    {
                        await process.WaitForExitAsync(cancellationToken);
                    }

                    await Task.WhenAll(outputWaitHandle.Task, errorWaitHandle.Task);

                    return (stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim(), process.ExitCode);
                }
                catch (OperationCanceledException)
                {
                    return (stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim(), -1);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]FATAL ShellUtil.RunProcessAndStreamOutputAsync: {ex.Message.EscapeMarkup()}[/]");
                    return (stdoutBuilder.ToString().Trim(), "FATAL EXCEPTION: " + ex.Message, -1);
                }
            }
        }
        // === AKHIR FUNGSI STREAMING BARU ===


        internal static async Task RunProcessWithFileStdinAsync(ProcessStartInfo startInfo, string localFilePath, CancellationToken cancellationToken)
        {
            var stderrBuilder = new StringBuilder();

            using (var process = new Process { StartInfo = startInfo })
            {
                process.EnableRaisingEvents = true;
                process.StartInfo.RedirectStandardInput = true; // Kita butuh Stdin
                
                var errorWaitHandle = new TaskCompletionSource<bool>();

                process.ErrorDataReceived += (s, e) => {
                    if (e.Data == null) {
                        errorWaitHandle.TrySetResult(true);
                    } else {
                        stderrBuilder.AppendLine(e.Data);
                    }
                };

                try
                {
                    if (!process.Start())
                    {
                        throw new Exception($"Failed to start process: {startInfo.FileName}");
                    }

                    process.BeginErrorReadLine();

                    // CancellationTokenRegistration
                    using (var reg = cancellationToken.Register(() => {
                        try { 
                            AnsiConsole.MarkupLine("[yellow]WARN: Stdin process cancellation. Killing...[/]");
                            process.Kill(true); 
                        } 
                        catch (InvalidOperationException) { /* Proses sudah selesai */ }
                        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error killing stdin process: {ex.Message.EscapeMarkup()}[/]"); }
                    }))
                    {
                        // --- Logika Stdin ---
                        await using (FileStream fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                        {
                            await fs.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
                            process.StandardInput.Close(); // TUTUP stdin untuk sinyal EOF
                        }
                        // --- Akhir Logika Stdin ---
                        
                        await process.WaitForExitAsync(cancellationToken);
                    }
                    
                    await errorWaitHandle.Task; // Tunggu stderr selesai

                    if (process.ExitCode != 0)
                    {
                        throw new Exception($"Command '{startInfo.Arguments}' failed (Exit Code: {process.ExitCode}): {stderrBuilder.ToString().Trim()}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw; // Biar ditangani di luar sebagai "batal"
                }
                catch (Exception ex)
                {
                    throw new Exception($"Error in RunProcessWithFileStdinAsync: {ex.Message}", ex);
                }
            }
        }
    }
}
