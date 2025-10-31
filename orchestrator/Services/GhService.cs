using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.Net;
using Orchestrator.Core;
using System.Collections.Generic; // <-- Tambahkan
using System; // <-- Tambahkan
using System.Linq; // <-- Tambahkan
using System.Threading.Tasks; // <-- Tambahkan

namespace Orchestrator.Services 
{
    public static class BillingService
    {
        private const double INCLUDED_CORE_HOURS = 120.0;
        private const double MACHINE_CORE_COUNT = 4.0; // Sesuai Automation-Hub (bukan 8.0 seperti Nexus)
        private const double SAFE_HOUR_BUFFER = 2.0;
        private const int MAX_BILLING_RETRIES = 2;

        public const string PersistentProxyError = "PERSISTENT_PROXY_FAILURE";

        public static async Task<BillingInfo> GetBillingInfo(TokenEntry token)
        {
            if (string.IsNullOrEmpty(token.Username)) {
                AnsiConsole.MarkupLine("[red]Username unknown.[/]");
                return new BillingInfo { IsQuotaOk = false, Error = "Username unknown" };
            }
            
            var attemptedAccounts = new HashSet<string?>();
            string? currentAccount = ExtractProxyAccount(token.Proxy);
            if (currentAccount != null) attemptedAccounts.Add(currentAccount);
            
            for (int attempt = 1; attempt <= MAX_BILLING_RETRIES; attempt++) {
                using var client = TokenManager.CreateHttpClient(token);
                
                // === PERBAIKAN: Ganti endpoint /shared-storage -> /usage ===
                var url = $"/users/{token.Username}/settings/billing/usage";
                // === AKHIR PERBAIKAN ===

                client.DefaultRequestHeaders.UserAgent.ParseAdd($"Orchestrator-BillingCheck/{token.Username}");
                try {
                    AnsiConsole.Markup($"[dim]   Attempt billing check ({attempt}/{MAX_BILLING_RETRIES})...[/]");
                    var response = await client.GetAsync("https://api.github.com" + url);
                    
                    if (response.IsSuccessStatusCode) {
                        AnsiConsole.MarkupLine("[green]OK[/]");
                        var json = await response.Content.ReadAsStringAsync();
                        
                        // === PERBAIKAN: Ganti logic kalkulasi dan model deserializer ===
                        var billingReport = JsonSerializer.Deserialize<BillingReport>(json);
                        if (billingReport?.UsageItems == null) { 
                            AnsiConsole.MarkupLine("[yellow]WARN: Invalid format (UsageItems null).[/]"); 
                            return new BillingInfo { IsQuotaOk = false, Error = "Invalid JSON format" }; 
                        }
                        
                        double totalCoreHoursUsed = 0.0;
                        foreach (var item in billingReport.UsageItems)
                        {
                            if (item.Product?.Equals("codespaces", StringComparison.OrdinalIgnoreCase) == true)
                            {
                                if (item.Sku?.Contains("compute 2-core") == true) {
                                    totalCoreHoursUsed += item.Quantity * 2.0;
                                } else if (item.Sku?.Contains("compute 4-core") == true) {
                                    totalCoreHoursUsed += item.Quantity * 4.0;
                                }
                                else if (item.Sku?.Contains("compute 8-core") == true) {
                                    totalCoreHoursUsed += item.Quantity * 8.0;
                                }
                                else if (item.Sku?.Contains("compute 16-core") == true) {
                                    totalCoreHoursUsed += item.Quantity * 16.0;
                                }
                                else if (item.Sku?.Contains("compute 32-core") == true) {
                                    totalCoreHoursUsed += item.Quantity * 32.0;
                                }
                            }
                        }
                        
                        var remainingCoreHours = INCLUDED_CORE_HOURS - totalCoreHoursUsed;
                        
                        // Gunakan MACHINE_CORE_COUNT (4.0) dari file ini
                        var hoursRemaining = Math.Max(0.0, remainingCoreHours / MACHINE_CORE_COUNT);
                        var isQuotaOk = hoursRemaining > SAFE_HOUR_BUFFER;
                        
                        return new BillingInfo { 
                            TotalCoreHoursUsed = totalCoreHoursUsed, 
                            IncludedCoreHours = INCLUDED_CORE_HOURS, 
                            HoursRemaining = hoursRemaining, 
                            IsQuotaOk = isQuotaOk 
                        };
                        // === AKHIR PERBAIKAN ===
                    } 
                    else if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired) {
                        AnsiConsole.MarkupLine($"[yellow]FAIL (407 Proxy Auth)[/]");
                        
                        if (attempt < MAX_BILLING_RETRIES) { 
                            string? oldAccount = ExtractProxyAccount(token.Proxy);
                            AnsiConsole.MarkupLine($"[yellow]   Rotating to different proxy account...[/]"); 
                            
                            if (TokenManager.RotateProxyForToken(token)) { 
                                string? newAccount = ExtractProxyAccount(token.Proxy);
                                
                                if (newAccount != null && newAccount != oldAccount && !attemptedAccounts.Contains(newAccount)) {
                                    attemptedAccounts.Add(newAccount);
                                    AnsiConsole.MarkupLine($"[green]   → Using different account (attempt {attempt + 1})[/]");
                                    await Task.Delay(2000); 
                                    continue; 
                                } else if (newAccount != null && attemptedAccounts.Contains(newAccount)) {
                                    AnsiConsole.MarkupLine($"[yellow]   → Already tried this account[/]");
                                } else {
                                    AnsiConsole.MarkupLine($"[yellow]   → Same account, different IP only[/]");
                                    await Task.Delay(2000); 
                                    continue;
                                }
                            } else { 
                                AnsiConsole.MarkupLine("[red]   Rotate failed.[/]"); 
                            }
                        }
                        
                        AnsiConsole.MarkupLine($"[red]   Failed after retries (407). Likely bandwidth limit.[/]"); 
                        return new BillingInfo { IsQuotaOk = false, Error = PersistentProxyError }; 
                    } 
                    // === PERBAIKAN: Tangani error 410 (Gone) secara eksplisit jika muncul lagi ===
                    else if (response.StatusCode == HttpStatusCode.Gone) {
                        AnsiConsole.MarkupLine($"[red]FAIL (410 Gone)[/]"); 
                        AnsiConsole.MarkupLine($"[red]   FATAL: Endpoint billing '{url}' sudah mati.[/]"); 
                        return new BillingInfo { IsQuotaOk = false, Error = "410 Gone" }; 
                    }
                    // === AKHIR PERBAIKAN ===
                    else { 
                        var error = await response.Content.ReadAsStringAsync(); 
                        AnsiConsole.MarkupLine($"[red]FAIL ({response.StatusCode})[/]"); 
                        AnsiConsole.MarkupLine($"[yellow]   WARN: API fail.[/]"); 
                        AnsiConsole.MarkupLine($"[dim]   {error.Split('\n').FirstOrDefault()}[/]"); 
                        return new BillingInfo { IsQuotaOk = false, Error = response.StatusCode.ToString() }; 
                    }
                } 
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException) {
                    AnsiConsole.MarkupLine($"[red]FAIL (Connection)[/]"); 
                    AnsiConsole.MarkupLine($"[yellow]   Exception: {ex.Message.Split('\n').FirstOrDefault()}[/]");
                    
                    if (attempt < MAX_BILLING_RETRIES) { 
                        AnsiConsole.MarkupLine($"[yellow]   Rotating proxy...[/]"); 
                        if (TokenManager.RotateProxyForToken(token)) { 
                            await Task.Delay(3000); 
                            continue; 
                        } else { 
                            AnsiConsole.MarkupLine("[red]   Rotate failed.[/]"); 
                            return new BillingInfo { IsQuotaOk = false, Error = PersistentProxyError }; 
                        }
                    }
                    else { 
                        AnsiConsole.MarkupLine($"[red]   Failed after retries (Connection).[/]"); 
                        return new BillingInfo { IsQuotaOk = false, Error = PersistentProxyError }; 
                    }
                } 
                catch (Exception ex) { 
                    AnsiConsole.MarkupLine($"[red]FAIL (Unexpected)[/]"); 
                    AnsiConsole.MarkupLine($"[red]   Exception: {ex.Message}[/]"); 
                    return new BillingInfo { IsQuotaOk = false, Error = $"Unexpected: {ex.GetType().Name}" }; 
                }
            } 
            
            AnsiConsole.MarkupLine("[red]Billing Check: Failed (fallback).[/]"); 
            return new BillingInfo { IsQuotaOk = false, Error = PersistentProxyError };
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

         public static void DisplayBilling(BillingInfo billing, string username) {
             if (!string.IsNullOrEmpty(billing.Error) && !billing.IsQuotaOk) {
                  string reason = billing.Error == PersistentProxyError ? "[magenta]Persistent Proxy/Network Error[/]" : billing.Error.Contains("unknown") ? "Username Unknown" : billing.Error.Contains("JSON") ? "Invalid Response" : $"API Error ({billing.Error})";
                  AnsiConsole.MarkupLine($"Billing @{username.EscapeMarkup()}: [red]CHECK FAILED ({reason})[/]");
                  if (billing.Error == PersistentProxyError) AnsiConsole.MarkupLine($"   [yellow]Attempting automatic IP Authorization...[/]"); else AnsiConsole.MarkupLine($"   [yellow]Assuming quota is insufficient.[/]");
                  AnsiConsole.MarkupLine($"   [red]WARNING: Action required (IP Auth or Token Rotation).[/]"); return;
             }
             AnsiConsole.MarkupLine($"Billing @{username.EscapeMarkup()}: Used ~[yellow]{billing.TotalCoreHoursUsed:F1}[/] of [green]{billing.IncludedCoreHours:F1}[/] core-hours.");
             AnsiConsole.MarkupLine($"   Approx. [bold {(billing.IsQuotaOk ? "green" : "red")}]{billing.HoursRemaining:F1} hours remaining[/] (for {MACHINE_CORE_COUNT}-core).");
             if (!billing.IsQuotaOk) AnsiConsole.MarkupLine($"   [red]WARNING: Kuota rendah (< {SAFE_HOUR_BUFFER}h) atau habis.[/]");
         }
    }
    
    public class BillingInfo { public double TotalCoreHoursUsed{get;set;} public double IncludedCoreHours{get;set;}=120.0; public double HoursRemaining{get;set;} public bool IsQuotaOk{get;set;} public string? Error{get;set;} }
    
    // === PERBAIKAN: Ganti Model SharedStorageReport -> BillingReport + UsageItem ===
    public class UsageItem
    {
        [JsonPropertyName("product")]
        public string Product { get; set; } = "";
        [JsonPropertyName("sku")]
        public string Sku { get; set; } = "";
        [JsonPropertyName("quantity")]
        public double Quantity { get; set; }
    }

    public class BillingReport
    {
        [JsonPropertyName("usageItems")]
        public List<UsageItem> UsageItems { get; set; } = new();
    }
    // === AKHIR PERBAIKAN ===
}
