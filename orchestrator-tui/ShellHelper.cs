using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;
using System.Net;

namespace Orchestrator;

public static class ShellHelper
{
    private const int DEFAULT_TIMEOUT_MS = 120000; // 2 minutes default
    private const int SHORT_TIMEOUT_MS = 30000; // 30 seconds for quick checks
    private const int LONG_TIMEOUT_MS = 600000; // 10 minutes for create
    private const int NETWORK_RETRY_DELAY_MS = 30000; // Delay 30s for network errors
    private const int TIMEOUT_RETRY_DELAY_MS = 15000; // Delay 15s for command timeouts

    private static bool _isAttemptingIpAuth = false;

     public static async Task<string> RunGhCommand(TokenEntry token, string args, int timeoutMilliseconds = DEFAULT_TIMEOUT_MS)
    {
        var startInfo = CreateStartInfo("gh", args, token);
        Exception? lastException = null; // Store the final exception if retry loop exits

        // Infinite Retry Loop
        while (true)
        {
            // Use a CancellationTokenSource for command-specific timeout
            var commandTimeoutCts = new CancellationTokenSource(timeoutMilliseconds);
            string stdout = "", stderr = "";
            int exitCode = -1;

            try
            {
                // Run the process with the command timeout token
                (stdout, stderr, exitCode) = await RunProcessAsync(startInfo, commandTimeoutCts.Token);

                // --- SUCCESS ---
                if (exitCode == 0)
                {
                    // AnsiConsole.MarkupLine($"[grey]DEBUG: Command OK (Exit 0): {args.Split(' ').FirstOrDefault()}[/]");
                    return stdout; // Return successful output
                }

                // --- FAILURE (Exit Code != 0) ---
                // Log the failure clearly before analyzing
                AnsiConsole.MarkupLine($"[yellow]WARN: gh command failed (Exit {exitCode}). Analyzing error...[/]");
                AnsiConsole.MarkupLine($"[grey]   CMD: gh {args}[/]");
                AnsiConsole.MarkupLine($"[grey]   ERR: {stderr.Split('\n').FirstOrDefault()?.Trim()}[/]");

            }
            // --- CATCH COMMAND TIMEOUT ---
            catch (TaskCanceledException) // Specifically catches the commandTimeoutCts cancellation
            {
                AnsiConsole.MarkupLine($"[yellow]Command timed out ({timeoutMilliseconds / 1000}s). Retrying in {TIMEOUT_RETRY_DELAY_MS / 1000}s...[/]");
                AnsiConsole.MarkupLine($"[grey]   CMD: gh {args}[/]");
                try { await Task.Delay(TIMEOUT_RETRY_DELAY_MS, _mainCts.Token); } catch (OperationCanceledException) { throw; } // Allow main cancellation during delay
                continue; // Retry the same command
            }
            // --- CATCH USER CANCELLATION (Ctrl+C) ---
            catch (OperationCanceledException) // Catches cancellation from _mainCts (passed down or triggered by handler)
            {
                AnsiConsole.MarkupLine("[yellow]Command cancelled by user (OperationCanceled).[/]");
                throw; // Re-throw to stop the main loop or calling function
            }
            // --- CATCH UNEXPECTED ERRORS DURING PROCESS RUN ---
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]ShellHelper Exception during RunProcessAsync: {ex.Message.Split('\n').FirstOrDefault()?.Trim()}[/]");
                AnsiConsole.MarkupLine($"[yellow]Retrying in {NETWORK_RETRY_DELAY_MS / 1000}s...[/]");
                 try { await Task.Delay(NETWORK_RETRY_DELAY_MS, _mainCts.Token); } catch (OperationCanceledException) { throw; } // Allow cancel
                continue; // Retry
            }

            // --- Analyze stderr if exitCode != 0 ---
            string lowerStderr = stderr.ToLowerInvariant();
            bool isRateLimit = lowerStderr.Contains("api rate limit exceeded") || lowerStderr.Contains("403 forbidden"); // More specific 403
            bool isAuthError = lowerStderr.Contains("bad credentials") || lowerStderr.Contains("401 unauthorized"); // More specific 401
            bool isProxyAuthError = lowerStderr.Contains("407 proxy authentication required"); // More specific 407
            // === PERBAIKAN: Tambahkan "unexpected eof" ===
            bool isNetworkError = lowerStderr.Contains("dial tcp") || lowerStderr.Contains("connection refused") ||
                                  lowerStderr.Contains("i/o timeout") || lowerStderr.Contains("error connecting") ||
                                  lowerStderr.Contains("wsarecv") || lowerStderr.Contains("forcibly closed") ||
                                  lowerStderr.Contains("resolve host") || lowerStderr.Contains("tls handshake timeout") ||
                                  lowerStderr.Contains("unreachable network") || lowerStderr.Contains("unexpected eof"); // <-- DITAMBAHKAN DI SINI
            // === AKHIR PERBAIKAN ===
            bool isNotFoundError = lowerStderr.Contains("404 not found"); // More specific 404

            // --- Error Handling Logic ---

            // 1. Proxy Auth Error (407): Rotate -> IP Auth -> Network Error Fallback
            if (isProxyAuthError) {
                AnsiConsole.MarkupLine($"[yellow]Proxy Auth Error (407) detected. Attempting to rotate proxy...[/]");
                if (TokenManager.RotateProxyForToken(token)) {
                    startInfo = CreateStartInfo("gh", args, token); // Update ProcessStartInfo with the new proxy
                    AnsiConsole.MarkupLine($"[cyan]Proxy rotated. Retrying command immediately...[/]");
                    await Task.Delay(1000); // Short delay after rotation
                    continue; // Retry command with new proxy
                }

                // Rotation failed (no more proxies?), try IP Auth
                AnsiConsole.MarkupLine("[yellow]Proxy rotation failed or no alternative proxies. Attempting automatic IP Authorization...[/]");
                if (!_isAttemptingIpAuth) { // Prevent recursive IP Auth attempts
                    _isAttemptingIpAuth = true;
                    bool ipAuthSuccess = await ProxyManager.RunIpAuthorizationOnlyAsync(_mainCts.Token); // Pass main cancel token
                    _isAttemptingIpAuth = false;

                    if (ipAuthSuccess) {
                        AnsiConsole.MarkupLine("[magenta]IP Auth successful. Retrying command...[/]");
                        continue; // Retry the original command
                    } else {
                         AnsiConsole.MarkupLine("[red]Automatic IP Auth failed. Treating as persistent network error.[/]");
                         // Let it fall through to the Network Error handling below
                    }
                } else {
                     AnsiConsole.MarkupLine("[yellow]IP Auth already in progress, treating as network error.[/]");
                     // Fall through to network error
                }
                // If IP Auth fails or was already running, treat as a Network Error for infinite retry
                 AnsiConsole.MarkupLine($"[magenta]Persistent Proxy/Network issue. Retrying command in {NETWORK_RETRY_DELAY_MS / 1000}s...[/]");
                 try { await Task.Delay(NETWORK_RETRY_DELAY_MS, _mainCts.Token); } catch (OperationCanceledException) { throw; }
                 continue;
            }

            // 2. General Network Error (Includes EOF, connection refused, etc., or failed 407): Infinite Retry
            if (isNetworkError) {
                AnsiConsole.MarkupLine($"[magenta]Network error detected. Retrying command in {NETWORK_RETRY_DELAY_MS / 1000}s...[/]");
                AnsiConsole.MarkupLine($"[grey]   (Detail: {stderr.Split('\n').FirstOrDefault()?.Trim()})[/]");
                try { await Task.Delay(NETWORK_RETRY_DELAY_MS, _mainCts.Token); } catch (OperationCanceledException) { throw; } // Allow cancel during delay
                continue; // Retry the same command
            }

            // 3. Fatal Errors (Auth, Rate Limit, Not Found): Give up immediately
            if (isAuthError || isRateLimit || isNotFoundError) {
                string errorType = isAuthError ? "Authentication (401)" : isRateLimit ? "Rate Limit/Forbidden (403)" : "Not Found (404)";
                AnsiConsole.MarkupLine($"[red]FATAL GH Error: {errorType}. Command failed permanently.[/]");
                lastException = new Exception($"GH Command Failed ({errorType}): {stderr.Split('\n').FirstOrDefault()?.Trim()}");
                break; // Exit the while(true) loop
            }

            // 4. Other Unhandled Errors: Consider fatal after one immediate retry attempt
            AnsiConsole.MarkupLine($"[red]Unhandled gh command error (Exit {exitCode}). Attempting one immediate retry...[/]");
            lastException = new Exception($"Unhandled GH Command Failed (Exit {exitCode}): {stderr.Split('\n').FirstOrDefault()?.Trim()}");
            await Task.Delay(2000); // Short delay before one final try
            // If it fails again, the loop will catch it, not recognize the error, and break with this lastException.
            // If it succeeds on the retry, the loop will return the result.
            // This acts as a single retry for truly weird errors.
            continue;

        } // End while(true)

        // If the loop was exited via 'break', throw the stored fatal exception
        throw lastException ?? new Exception("GH command failed after exhausting retry/error handling logic.");
    } // End RunGhCommand

    // --- RunCommandAsync, RunInteractive, RunInteractiveWithFullInput ---
    // --- CreateStartInfo, SetEnvironmentVariables, SetFileNameAndArgs ---
    // --- RunProcessAsync ---
    // (These helper functions remain unchanged from the previous correct version)
    // (Salin dari versi sebelumnya ke sini)

    public static async Task RunCommandAsync(string command, string args, string? workingDir = null, TokenEntry? token = null)
    {
        // Pass CancellationToken.None as this is meant for non-cancellable background tasks mostly
        var startInfo = CreateStartInfo(command, args, token);
        if (workingDir != null) startInfo.WorkingDirectory = workingDir;
        var cts = new CancellationTokenSource(DEFAULT_TIMEOUT_MS); // Use default timeout
        var (_, stderr, exitCode) = await RunProcessAsync(startInfo, cts.Token);
        if (exitCode != 0) throw new Exception($"Command '{command} {args}' failed (Exit Code: {exitCode}): {stderr}");
    }

     public static async Task RunInteractive(string command, string args, string? workingDir = null, TokenEntry? token = null, CancellationToken cancellationToken = default)
    {
        // Used for non-full interactive, like proxysync menu
        var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = false, RedirectStandardOutput = false, RedirectStandardError = false, RedirectStandardInput = false };
        if (workingDir != null) startInfo.WorkingDirectory = workingDir;
        if (token != null) SetEnvironmentVariables(startInfo, token, command);
        SetFileNameAndArgs(startInfo, command, args);
        using var process = new Process { StartInfo = startInfo };
        try {
            AnsiConsole.MarkupLine($"[dim]Starting interactive: {startInfo.FileName} {startInfo.Arguments}[/]");
            process.Start();
            // Register cancellation
            using var reg = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            await process.WaitForExitAsync(cancellationToken); // Wait for exit or cancellation
            // Log exit code if not cancelled and not 0 or -1 (typical killed process code)
            if (!cancellationToken.IsCancellationRequested && process.ExitCode != 0 && process.ExitCode != -1)
            {
                AnsiConsole.MarkupLine($"[yellow]Interactive process exited with code: {process.ExitCode}[/]");
            }
        } catch (OperationCanceledException) {
            AnsiConsole.MarkupLine("[yellow]Interactive operation cancelled.[/]");
            try { if (!process.HasExited) process.Kill(true); } catch { } // Ensure process is killed on cancel
            // Do not re-throw, allow calling menu to continue normally after cancel
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"[red]Error running interactive process: {ex.Message}[/]");
            try { if (!process.HasExited) process.Kill(true); } catch { }
            // Optionally re-throw if this should halt the TUI
            // throw;
        }
    }


    public static async Task RunInteractiveWithFullInput(string command, string args, string? workingDir = null, TokenEntry? token = null, CancellationToken cancellationToken = default)
    {
        // Used for Attach and Remote Shell where TUI needs to handle Ctrl+C
        var startInfo = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = false, RedirectStandardOutput = false, RedirectStandardError = false, RedirectStandardInput = false };
        if (workingDir != null) startInfo.WorkingDirectory = workingDir;
        if (token != null) SetEnvironmentVariables(startInfo, token, command);
        SetFileNameAndArgs(startInfo, command, args);
        using var process = new Process { StartInfo = startInfo };
        try {
            AnsiConsole.MarkupLine($"[bold green]▶ Starting Full Interactive Session[/]");
            AnsiConsole.MarkupLine($"[dim]Cmd: {command} {args}[/]");
            AnsiConsole.MarkupLine($"[dim]Dir: {workingDir ?? "current"}[/]");
            AnsiConsole.MarkupLine("[yellow]"+ new string('═', 60) +"[/]"); // Separator

            process.Start();

            // TaskCompletionSource for process exit event
            var processExitedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.EnableRaisingEvents = true;
            process.Exited += (s, e) => processExitedTcs.TrySetResult(true);

            // TaskCompletionSource for external cancellation token
            var cancellationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = cancellationToken.Register(() => cancellationTcs.TrySetResult(true));

            // Wait for either the process to exit OR the cancellation token to be triggered
            var completedTask = await Task.WhenAny(processExitedTcs.Task, cancellationTcs.Task);

            AnsiConsole.MarkupLine("\n[yellow]"+ new string('═', 60) +"[/]"); // Separator

            // --- Handle Cancellation FIRST ---
            if (completedTask == cancellationTcs.Task || cancellationToken.IsCancellationRequested) {
                AnsiConsole.MarkupLine("[yellow]Cancellation requested. Terminating process...[/]");
                try {
                    if (!process.HasExited) {
                        process.Kill(true); // Force kill
                        // Give a moment for kill to propagate
                        await Task.WhenAny(processExitedTcs.Task, Task.Delay(1500));
                    }
                } catch (InvalidOperationException) { /* Process already exited */ }
                catch (Exception killEx) { AnsiConsole.MarkupLine($"[red]Error terminating process: {killEx.Message}[/]"); }

                // IMPORTANT: Throw OperationCanceledException so the CALLER knows it was cancelled
                // This allows Program.cs (_interactiveCts handling) to work correctly
                throw new OperationCanceledException();
            }

            // --- Handle Normal Process Exit ---
            // If we get here, processExitedTcs completed first
            await Task.Delay(100); // Small delay to ensure exit code is available
            int exitCode = process.ExitCode; // Get exit code

            if (exitCode == 0) {
                AnsiConsole.MarkupLine($"[green]✓ Process exited normally (Code: {exitCode})[/]");
            } else {
                // Treat -1 as potentially terminated/killed externally, not necessarily an error
                AnsiConsole.MarkupLine($"[yellow]Process exited with non-zero code: {exitCode}[/]");
            }

        } catch (OperationCanceledException) {
             // Catch the exception we threw above after handling cancellation
             AnsiConsole.MarkupLine("[yellow]Interactive session cancelled.[/]");
             // Do NOT re-throw here, let the caller (Program.cs menu) handle the flow after cancel
        } catch (Exception ex) {
            AnsiConsole.MarkupLine("\n[yellow]"+ new string('═', 60) +"[/]");
            AnsiConsole.MarkupLine($"[red]✗ Error running full interactive process: {ex.Message.EscapeMarkup()}[/]");
            try { if (!process.HasExited) process.Kill(true); } catch { } // Ensure cleanup on error
            // Optionally pause or re-throw depending on desired TUI behavior on error
            // Pause("Press Enter...", CancellationToken.None);
             throw; // Re-throw to indicate failure to the caller
        }
    } // End RunInteractiveWithFullInput


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
        if (isGhCommand) {
            // AnsiConsole.MarkupLine($"[grey]DEBUG: Setting GH_TOKEN: {TokenManager.MaskToken(token.Token)}[/]");
            startInfo.EnvironmentVariables["GH_TOKEN"] = token.Token;
        }
        if (!string.IsNullOrEmpty(token.Proxy)) {
            // AnsiConsole.MarkupLine($"[grey]DEBUG: Setting Proxy: {TokenManager.MaskProxy(token.Proxy)}[/]");
            startInfo.EnvironmentVariables["https_proxy"] = token.Proxy; startInfo.EnvironmentVariables["http_proxy"] = token.Proxy;
            startInfo.EnvironmentVariables["HTTPS_PROXY"] = token.Proxy; startInfo.EnvironmentVariables["HTTP_PROXY"] = token.Proxy;
            // Set NO_PROXY for common local/internal addresses
            string noProxy = "localhost,127.0.0.1,.github.com,github.com,api.github.com"; // Added github domains
            startInfo.EnvironmentVariables["NO_PROXY"] = noProxy; startInfo.EnvironmentVariables["no_proxy"] = noProxy;
        } else {
            // AnsiConsole.MarkupLine($"[grey]DEBUG: Unsetting Proxy variables[/]");
             startInfo.EnvironmentVariables.Remove("https_proxy"); startInfo.EnvironmentVariables.Remove("http_proxy");
             startInfo.EnvironmentVariables.Remove("HTTPS_PROXY"); startInfo.EnvironmentVariables.Remove("HTTP_PROXY");
             startInfo.EnvironmentVariables.Remove("NO_PROXY"); startInfo.EnvironmentVariables.Remove("no_proxy");
        }
    }

     private static void SetFileNameAndArgs(ProcessStartInfo startInfo, string command, string args) {
         if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            startInfo.FileName = "cmd.exe";
            // Ensure proper quoting for cmd.exe /c
            startInfo.Arguments = $"/c \"\"{command}\" {args}\""; // Encapsulate command and args
        } else {
            startInfo.FileName = "/bin/bash";
            // Escape double quotes within the arguments string for bash -c "..."
            string escapedArgs = args.Replace("\"", "\\\"");
            startInfo.Arguments = $"-c \"{command} {escapedArgs}\"";
        }
    }

    private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        // Use TaskCompletionSource with RunContinuationsAsynchronously for better performance
        var tcs = new TaskCompletionSource<(string, string, int)>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.EnableRaisingEvents = true;

        // Use lambda expressions for handlers
        process.OutputDataReceived += (s, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };
        process.Exited += (s, e) => {
             // Short delay might still be needed for streams to flush, but TrySetResult is safer
             // Task.Delay(100).ContinueWith(_ => tcs.TrySetResult(...)); // Less ideal
             tcs.TrySetResult((stdoutBuilder.ToString().TrimEnd(), stderrBuilder.ToString().TrimEnd(), process.ExitCode));
        };

        CancellationTokenRegistration cancellationRegistration = default;
        try {
            if (!process.Start()) {
                 // Throw exception if process fails to start
                 throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
            }

            // Begin reading output streams
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Register cancellation callback
            cancellationRegistration = cancellationToken.Register(() => {
                // Use TrySetCanceled for proper cancellation propagation
                if (tcs.TrySetCanceled(cancellationToken)) {
                    try {
                        if (!process.HasExited) {
                             AnsiConsole.MarkupLine($"[grey]DEBUG: Cancellation triggered, attempting to kill process {process.Id}...[/]");
                             process.Kill(true); // Attempt to kill the entire process tree
                        }
                    } catch (InvalidOperationException) { /* Process already exited */ }
                    catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Error during process kill on cancellation: {ex.Message}[/]"); }
                }
            });

            // Asynchronously wait for the task completion source (either exit or cancellation)
            return await tcs.Task;
        }
        catch (TaskCanceledException) {
             // This is expected if cancellationToken is triggered
             // AnsiConsole.MarkupLine($"[grey]DEBUG: RunProcessAsync TaskCanceledException caught.[/]");
             throw; // Re-throw TaskCanceledException
        }
        catch (OperationCanceledException) {
            // This might happen if the token passed in was already cancelled
             // AnsiConsole.MarkupLine($"[grey]DEBUG: RunProcessAsync OperationCanceledException caught.[/]");
            throw; // Re-throw OperationCanceledException
        }
        catch (Exception ex) {
            // Catch other exceptions (e.g., process start failure)
            AnsiConsole.MarkupLine($"[red]Error in RunProcessAsync: {ex.Message}[/]");
            // Try to ensure process is killed if it somehow started
            try { if (process != null && !process.HasExited) process.Kill(true); } catch { }
            // Return failure state, including the exception message in stderr
            return (stdoutBuilder.ToString().TrimEnd(), (stderrBuilder.ToString().TrimEnd() + "\n" + ex.Message).Trim(), process?.ExitCode ?? -1);
        } finally {
            // IMPORTANT: Dispose of the cancellation registration
            await cancellationRegistration.DisposeAsync();
        }
    } // End RunProcessAsync

    // Reference _mainCts from Program.cs for global cancellation signal
    private static CancellationTokenSource _programExitCts => Program._mainCts;

} // End Class
