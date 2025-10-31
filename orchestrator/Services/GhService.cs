using Spectre.Console;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Util;      // <-- PERBAIKAN: Ditambahkan
using Orchestrator.Core;      // <-- PERBAIKAN: Ditambahkan

namespace Orchestrator.Services 
{
    public static class GhService
    {
        private const int DEFAULT_TIMEOUT_MS = 120000;
        private const int NETWORK_RETRY_DELAY_MS = 30000;
        private const int TIMEOUT_RETRY_DELAY_MS = 15000;

        private static bool _isAttemptingIpAuth = false;

        public static async Task<string> RunGhCommand(TokenEntry token, string args, int timeoutMilliseconds = DEFAULT_TIMEOUT_MS)
        {
            var startInfo = ShellUtil.CreateStartInfo("gh", args, token);
            Exception? lastException = null;
            var globalCancelToken = Program.GetMainCancellationToken(); 

            while (true) 
            {
                globalCancelToken.ThrowIfCancellationRequested(); 
                using var commandTimeoutCts = new CancellationTokenSource(timeoutMilliseconds);
                string stdout = "", stderr = ""; int exitCode = -1;

                try {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(commandTimeoutCts.Token, globalCancelToken);
                    (stdout, stderr, exitCode) = await ShellUtil.RunProcessAsync(startInfo, linkedCts.Token);

                    if (exitCode == 0) return stdout; 

                    AnsiConsole.MarkupLine($"[yellow]WARN: gh command failed (Exit {exitCode}). Analyzing error...[/]");
                    AnsiConsole.MarkupLine($"[grey]   CMD: gh {args.EscapeMarkup()}[/]");
                    AnsiConsole.MarkupLine($"[grey]   ERR: {stderr.Split('\n').FirstOrDefault()?.Trim().EscapeMarkup() ?? "No stderr"}[/]");
                }
                catch (OperationCanceledException) when (commandTimeoutCts.IsCancellationRequested && !globalCancelToken.IsCancellationRequested) {
                    AnsiConsole.MarkupLine($"[yellow]Command timed out ({timeoutMilliseconds / 1000}s). Retrying in {TIMEOUT_RETRY_DELAY_MS / 1000}s...[/]");
                    AnsiConsole.MarkupLine($"[grey]   CMD: gh {args.EscapeMarkup()}[/]");
                    try { await Task.Delay(TIMEOUT_RETRY_DELAY_MS, globalCancelToken); } catch (OperationCanceledException) { throw; } 
                    continue; 
                }
                catch (OperationCanceledException) when (globalCancelToken.IsCancellationRequested) {
                    AnsiConsole.MarkupLine("[yellow]Command cancelled by user (Global Cancel).[/]");
                    throw; 
                }
                catch (Exception ex) {
                    AnsiConsole.MarkupLine($"[red]ShellHelper Exception during RunProcessAsync: {ex.Message.Split('\n').FirstOrDefault()?.Trim().EscapeMarkup()}[/]");
                    AnsiConsole.MarkupLine($"[yellow]Retrying in {NETWORK_RETRY_DELAY_MS / 1000}s...[/]");
                    try { await Task.Delay(NETWORK_RETRY_DELAY_MS, globalCancelToken); } catch (OperationCanceledException) { throw; } 
                    continue; 
                }

                string lowerStderr = stderr.ToLowerInvariant();
                bool isRateLimit = lowerStderr.Contains("api rate limit exceeded") || lowerStderr.Contains("403 forbidden");
                bool isAuthError = lowerStderr.Contains("bad credentials") || lowerStderr.Contains("401 unauthorized");
                bool isProxyAuthError = lowerStderr.Contains("407 proxy authentication required");
                bool isNetworkError = lowerStderr.Contains("dial tcp") || lowerStderr.Contains("connection refused") ||
                                      lowerStderr.Contains("i/o timeout") || lowerStderr.Contains("error connecting") ||
                                      lowerStderr.Contains("wsarecv") || lowerStderr.Contains("forcibly closed") ||
                                      lowerStderr.Contains("resolve host") || lowerStderr.Contains("tls handshake timeout") ||
                                      lowerStderr.Contains("unreachable network") || lowerStderr.Contains("unexpected eof") ||
                                      lowerStderr.Contains("connection reset") || lowerStderr.Contains("handshake failed");
                bool isNotFoundError = lowerStderr.Contains("404 not found");

                if (isProxyAuthError) {
                    AnsiConsole.MarkupLine($"[yellow]Proxy Auth Error (407). Rotating proxy...[/]");
                    if (TokenManager.RotateProxyForToken(token)) {
                        startInfo = ShellUtil.CreateStartInfo("gh", args, token); 
                        AnsiConsole.MarkupLine($"[cyan]Proxy rotated. Retrying command...[/]");
                        try { await Task.Delay(1000, globalCancelToken); } catch (OperationCanceledException) { throw; }
                        continue; 
                    }
                    AnsiConsole.MarkupLine("[yellow]Proxy rotation failed. Attempting IP Auth...[/]");
                    if (!_isAttemptingIpAuth) {
                        _isAttemptingIpAuth = true;
                        bool ipAuthSuccess = await ProxyService.RunIpAuthorizationOnlyAsync(globalCancelToken);
                        _isAttemptingIpAuth = false;
                        if (ipAuthSuccess) { AnsiConsole.MarkupLine("[magenta]IP Auth successful. Retrying command...[/]"); continue; }
                        else { AnsiConsole.MarkupLine("[red]IP Auth failed. Treating as network error.[/]"); }
                    } else { AnsiConsole.MarkupLine("[yellow]IP Auth in progress. Treating as network error.[/]"); }
                }

                if ((isNetworkError || isProxyAuthError) && !isNotFoundError) { 
                    AnsiConsole.MarkupLine($"[magenta]Network/Proxy error. Retrying in {NETWORK_RETRY_DELAY_MS / 1000}s...[/]");
                    AnsiConsole.MarkupLine($"[grey]   (Detail: {stderr.Split('\n').FirstOrDefault()?.Trim().EscapeMarkup()})[/]");
                    try { await Task.Delay(NETWORK_RETRY_DELAY_MS, globalCancelToken); } catch (OperationCanceledException) { throw; }
                    continue; 
                }

                if (isAuthError || isRateLimit || isNotFoundError) { 
                    string errorType = isAuthError ? "Auth (401)" : isRateLimit ? "Rate Limit/Forbidden (403)" : "Not Found (404)";
                    AnsiConsole.MarkupLine($"[red]FATAL GH Error: {errorType}. Command failed permanently.[/]");
                    lastException = new Exception($"GH Command Failed ({errorType}): {stderr.Split('\n').FirstOrDefault()?.Trim().EscapeMarkup()}");
                    break; 
                }

                AnsiConsole.MarkupLine($"[red]FATAL Unhandled gh command error (Exit {exitCode}). Command failed permanently.[/]");
                lastException = new Exception($"Unhandled GH Command Failed (Exit {exitCode}): {stderr.Split('\n').FirstOrDefault()?.Trim().EscapeMarkup()}");
                break; 

            } 

            throw lastException ?? new Exception("GH command failed unexpectedly after error handling.");
        }
    }
}
