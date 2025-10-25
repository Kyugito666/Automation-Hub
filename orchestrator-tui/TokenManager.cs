using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public static class TokenManager
{
    // ... (Konstanta path file tetap sama) ...
     private static readonly string ConfigRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config"));
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string TokensPath = Path.Combine(ConfigRoot, "github_tokens.txt");
    private static readonly string MasterProxyListPath = Path.Combine(ProjectRoot, "proxysync", "proxy.txt");
    private static readonly string SuccessProxyListPath = Path.Combine(ProjectRoot, "proxysync", "success_proxy.txt");
    private static readonly string StatePath = Path.Combine(ProjectRoot, ".token-state.json");
    private static readonly string TokenCachePath = Path.Combine(ProjectRoot, ".token-cache.json");

    private static List<TokenEntry> _tokens = new();
    private static List<string> _availableProxies = new();
    private static TokenState _state = new();
    private static Dictionary<string, string> _tokenCache = new();
    private static Dictionary<string, Queue<string>> _proxyPool = new();

    // Timeout default untuk HttpClient (detik)
    private const int DEFAULT_HTTP_TIMEOUT_SEC = 60; // Naikkan jadi 60 detik

    // ... (Initialize, ReloadAllConfigs, LoadTokenCache, SaveTokenCache, LoadTokens, LoadProxyList, AssignProxiesAndUsernames, InitializeProxyPool, RotateProxyForToken, LoadState, GetState, SaveState, GetCurrentToken, GetAllTokenEntries, GetUsernameCache, SwitchToNextToken, MaskToken, MaskProxy, ShowStatus - TETAP SAMA SEPERTI SEBELUMNYA) ...
     public static void Initialize() { LoadTokens(); LoadProxyList(); LoadState(); LoadTokenCache(); AssignProxiesAndUsernames(); InitializeProxyPool(); }
     public static void ReloadAllConfigs() { /* ... */ Initialize(); /* ... */ }
     private static void LoadTokenCache() { /* ... */ }
     public static void SaveTokenCache(Dictionary<string, string> cache) { /* ... */ }
     private static void LoadTokens() { /* ... */ }
     private static void LoadProxyList() { /* ... */ }
     private static void AssignProxiesAndUsernames() { /* ... */ }
     private static void InitializeProxyPool() { /* ... */ }
     public static bool RotateProxyForToken(TokenEntry currentTokenEntry) { /* ... */ }
     private static void LoadState() { /* ... */ }
     public static TokenState GetState() => _state;
     public static void SaveState(TokenState state) { /* ... */ }
     public static TokenEntry GetCurrentToken() { /* ... */ }
     public static List<TokenEntry> GetAllTokenEntries() => _tokens;
     public static Dictionary<string, string> GetUsernameCache() => _tokenCache;
     public static TokenEntry SwitchToNextToken() { /* ... */ }
     public static string MaskToken(string token) { /* ... */ }
     public static string MaskProxy(string proxy) { /* ... */ }
     public static void ShowStatus() { /* ... */ }


    // --- PERBAIKAN DI SINI: CreateHttpClient ---
    public static HttpClient CreateHttpClient(TokenEntry token)
    {
        var handler = new HttpClientHandler();

        if (!string.IsNullOrEmpty(token.Proxy))
        {
            try
            {
                var proxyUri = new Uri(token.Proxy); // Coba parse proxy string jadi Uri
                var webProxy = new WebProxy(proxyUri);

                // Cek apakah ada info user:pass di dalam Uri
                if (!string.IsNullOrEmpty(proxyUri.UserInfo))
                {
                    var credentials = proxyUri.UserInfo.Split(':', 2); // Split user:pass
                    if (credentials.Length == 2)
                    {
                        // Buat NetworkCredential eksplisit
                        webProxy.Credentials = new NetworkCredential(credentials[0], credentials[1]);
                        webProxy.UseDefaultCredentials = false; // Pastikan pakai credential ini
                        AnsiConsole.MarkupLine($"[grey dim]   Using credentials for proxy {proxyUri.Host}[/]");
                    }
                    else {
                         AnsiConsole.MarkupLine($"[yellow]   Warning: Could not parse user:pass from proxy URI {MaskProxy(token.Proxy)}[/]");
                    }
                } else {
                     AnsiConsole.MarkupLine($"[grey dim]   Proxy {proxyUri.Host} does not require credentials.[/]");
                     webProxy.UseDefaultCredentials = true; // Atau false jika tidak mau kirim default cred
                }

                handler.Proxy = webProxy;
                handler.UseProxy = true;
            }
            catch (UriFormatException ex) // Tangkap error jika format URI salah
            {
                AnsiConsole.MarkupLine($"[red]Invalid proxy format {MaskProxy(token.Proxy)}: {ex.Message}. Disabling proxy for this request.[/]");
                handler.Proxy = null;
                handler.UseProxy = false;
            }
            catch (Exception ex) // Tangkap error lain saat setup proxy
            {
                 AnsiConsole.MarkupLine($"[red]Error setting up proxy {MaskProxy(token.Proxy)}: {ex.Message}. Disabling proxy.[/]");
                 handler.Proxy = null;
                 handler.UseProxy = false;
            }
        } else {
             handler.Proxy = null;
             handler.UseProxy = false;
        }

        var client = new HttpClient(handler);
        // Set timeout global di HttpClient
        client.Timeout = TimeSpan.FromSeconds(DEFAULT_HTTP_TIMEOUT_SEC);

        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Token}");
        client.DefaultRequestHeaders.Add("User-Agent", "Automation-Hub-Orchestrator/3.2"); // Versi update
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

        return client;
    }
    // --- AKHIR PERBAIKAN ---
}

// --- Kelas Model (TokenEntry, TokenState) ---
public class TokenEntry { /* ... */ public string Token{get;set;}=string.Empty; public string? Proxy{get;set;} public string? Username{get;set;} public string Owner{get;set;}=string.Empty; public string Repo{get;set;}=string.Empty; }
public class TokenState { [JsonPropertyName("current_index")] public int CurrentIndex{get;set;}=0; [JsonPropertyName("active_codespace_name")] public string? ActiveCodespaceName{get;set;}=null; }
