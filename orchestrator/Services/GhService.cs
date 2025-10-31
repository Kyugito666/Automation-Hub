using Spectre.Console;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Util;      
using Orchestrator.Core;      
using System; // <-- Ditambahkan

namespace Orchestrator.Services 
{
    public static class GhService
    {
        private const int DEFAULT_TIMEOUT_MS = 60000;
        private const int NETWORK_RETRY_DELAY_MS = 30000;
        private const int TIMEOUT_RETRY_DELAY_MS = 15000;

        private static bool _isAttemptingIpAuth = false;

        public static async Task<string> RunGhCommandNoProxyAsync(TokenEntry token, string args, int timeoutMilliseconds = DEFAULT_TIMEOUT_MS)
        {
            AnsiConsole.MarkupLine($"[dim]   (Running 'gh {args.Split(' ')[0]}...' [bold yellow]NO PROXY[/])[/]");
            return await RunGhCommand(token, args, timeoutMilliseconds, useProxy: false);
        }
        
        public static async Task<string> RunGhCommand(TokenEntry token, string args, int timeoutMilliseconds = DEFAULT_TIMEOUT_MS, bool useProxy = true)
        {
            var startInfo = ShellUtil.CreateStartInfo("gh", args, token, useProxy);
            Exception? lastException = null;
            var globalCancelToken = Program.GetMainCancellationToken(); 

            while (true) 
            {
                globalCancelToken.ThrowIfCancellationRequested(); 

                bool isSshCommand = args.Contains("codespace ssh");
                int effectiveTimeout = isSshCommand ? System.Threading.Timeout.Infinite : timeoutMilliseconds;

                using var commandTimeoutCts = new CancellationTokenSource();
                if (effectiveTimeout != System.Threading.Timeout.Infinite)
                {
                    commandTimeoutCts.CancelAfter(effectiveTimeout);
                }
                
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
                
                bool isBenignStopError = (lowerStderr.Contains("is not running") || lowerStderr.Contains("already stopped")) && args.Contains("codespace stop");
                if (isBenignStopError) {
                    AnsiConsole.MarkupLine("[dim]   (Interpreted benign 'stop' error as success)[/]");
                    return stdout; 
                }

                // === INI PERBAIKANNYA ===
                // 1. Deteksi error 'gh' internal timeout
                bool isCodespaceStartTimeout = lowerStderr.Contains("timed out while waiting for the codespace to start");

                bool isRateLimit = lowerStderr.Contains("api rate limit exceeded") || lowerStderr.Contains("403 forbidden");
                bool isAuthError = lowerStderr.Contains("bad credentials") || lowerStderr.Contains("401 unauthorized");
                bool isProxyAuthError = lowerStderr.Contains("407 proxy authentication required");
                
                // 2. Modifikasi 'isNetworkError' biar GAK salah klasifikasi
                bool isNetworkError = (lowerStderr.Contains("dial tcp") || lowerStderr.Contains("connection refused") ||
                                      lowerStderr.Contains("i/o timeout") || lowerStderr.Contains("error connecting") || // 'error connecting' bisa jadi 'gh timeout'
                                      lowerStderr.Contains("wsarecv") || lowerStderr.Contains("forcibly closed") ||
                                      lowerStderr.Contains("resolve host") || lowerStderr.Contains("tls handshake timeout") ||
                                      lowerStderr.Contains("unreachable network") || lowerStderr.Contains("unexpected eof") ||
                                      lowerStderr.Contains("connection reset") || lowerStderr.Contains("handshake failed"))
                                      && !isCodespaceStartTimeout; // <-- 3. Pengecualian
                // === AKHIR PERBAIKAN ===

                bool isNotFoundError = lowerStderr.Contains("404 not found");

                if (isProxyAuthError) { // Ini HANYA ke-trigger kalo proxy ON dan error 407
                    AnsiConsole.MarkupLine($"[yellow]Proxy Auth Error (407). Trying different account...[/]");
                    string? oldAccount = ExtractProxyAccount(token.Proxy);
                    
                    if (TokenManager.RotateProxyForToken(token)) {
                        string? newAccount = ExtractProxyAccount(token.Proxy);
                        
                        startInfo = ShellUtil.CreateStartInfo("gh", args, token, useProxy); 
                        
                        if (newAccount != null && newAccount != oldAccount) {
                            AnsiConsole.MarkupLine($"[green]Rotated to different account. Retrying...[/]");
                        } else {
                            AnsiConsole.MarkupLine($"[yellow]Rotated IP only (same account). Retrying...[/]");
                        }
                        
                        try { await Task.Delay(1000, globalCancelToken); } catch (OperationCanceledException) { throw; }
                        continue; 
                    }
                    
                    AnsiConsole.MarkupLine("[yellow]All proxy accounts exhausted. Attempting IP Auth...[/]");
                    if (!_isAttemptingIpAuth) {
                        _isAttemptingIpAuth = true;
                        bool ipAuthSuccess = await ProxyService.RunIpAuthorizationOnlyAsync(globalCancelToken);
                        _isAttemptingIpAuth = false;
                        if (ipAuthSuccess) { 
                            AnsiConsole.MarkupLine("[magenta]IP Auth successful. Testing & reloading...[/]"); 
                            await ProxyService.RunProxyTestAndSaveAsync(globalCancelToken);
                            AnsiConsole.MarkupLine("[cyan]Reloading all configs...[/]");
                            TokenManager.ReloadAllConfigs();
                            AnsiConsole.MarkupLine("[green]Retrying command...[/]"); 
                            continue; 
                        }
                        else { AnsiConsole.MarkupLine("[red]IP Auth failed. Treating as network error.[/]"); }
                    } else { AnsiConsole.MarkupLine("[yellow]IP Auth in progress. Treating as network error.[/]"); }
                }

                if (isNetworkError && !isNotFoundError) { 
                    // === INI PERBAIKANNYA ===
                    // 4. Cek apakah proxy global ON atau OFF pas nampilin error
                    string errorMsg = TokenManager.IsProxyGloballyEnabled() 
                        ? "[magenta]Network/Proxy error. Retrying in" 
                        : "[magenta]Network error (No Proxy). Retrying in";
                    
                    AnsiConsole.MarkupLine($"{errorMsg} {NETWORK_RETRY_DELAY_MS / 1000}s...[/]");
                    // === AKHIR PERBAIKAN ===
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

                // Jika errornya 'isCodespaceStartTimeout', dia akan lolos dari 'isNetworkError'
                // dan jatuh ke sini. Ini yang kita mau.
                // Loop 'WaitForSshReadyWithRetry' di 'CodeHealth.cs' akan nangkep exception ini
                // dan nge-retry loop-nya (ping terus menerus).
                AnsiConsole.MarkupLine($"[red]FATAL Unhandled gh command error (Exit {exitCode}). Command failed permanently.[/]");
                lastException = new Exception($"Unhandled GH Command Failed (Exit {exitCode}): {stderr.Split('\n').FirstOrDefault()?.Trim().EscapeMarkup()}");
                break; 

            } 

            throw lastException ?? new Exception("GH command failed unexpectedly after error handling.");
        }
        
        private static string? ExtractProxyAccount(string? proxyUrl) {
            if (string.IsNullOrEmpty(proxyUrl)) return null;
            try {
                if (Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo)) {
                    return uri.UserInfo;
                }
                var parts = proxyUrl.Split('@');
                if (parts.Length == 2) {
                    return parts[0];
                }
            } catch { }
            return null;
        }
    }
}
