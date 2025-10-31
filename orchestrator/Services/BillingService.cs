using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.Net;
using Orchestrator.Core; // <-- PERBAIKAN: Ditambahkan

namespace Orchestrator.Services 
{
    public static class BillingService
    {
        private const double INCLUDED_CORE_HOURS = 120.0;
        private const double MACHINE_CORE_COUNT = 4.0;
        private const double SAFE_HOUR_BUFFER = 2.0;
        private const int MAX_BILLING_RETRIES = 2;

        public const string PersistentProxyError = "PERSISTENT_PROXY_FAILURE";

        public static async Task<BillingInfo> GetBillingInfo(TokenEntry token)
        {
            if (string.IsNullOrEmpty(token.Username)) {
                AnsiConsole.MarkupLine("[red]Username unknown.[/]");
                return new BillingInfo { IsQuotaOk = false, Error = "Username unknown" };
            }
            for (int attempt = 1; attempt <= MAX_BILLING_RETRIES; attempt++) {
                using var client = TokenManager.CreateHttpClient(token);
                var url = $"/users/{token.Username}/settings/billing/usage";
                client.DefaultRequestHeaders.UserAgent.ParseAdd($"Orchestrator-BillingCheck/{token.Username}");
                try {
                    AnsiConsole.Markup($"[dim]   Attempt billing check ({attempt}/{MAX_BILLING_RETRIES})...[/]");
                    var response = await client.GetAsync("https://api.github.com" + url);
                    if (response.IsSuccessStatusCode) {
                        AnsiConsole.MarkupLine("[green]OK[/]");
                        var json = await response.Content.ReadAsStringAsync();
                        var report = JsonSerializer.Deserialize<BillingReport>(json);
                        if (report?.UsageItems == null) { AnsiConsole.MarkupLine("[yellow]WARN: Invalid format.[/]"); return new BillingInfo { IsQuotaOk = false, Error = "Invalid JSON format" }; }
                        double totalCoreHoursUsed = 0.0;
                        foreach (var item in report.UsageItems) { if (item.Product == "codespaces") {
                                if (item.Sku.Contains("2-core")) totalCoreHoursUsed += (item.Quantity * 2.0); else if (item.Sku.Contains("4-core")) totalCoreHoursUsed += (item.Quantity * 4.0);
                                else if (item.Sku.Contains("8-core")) totalCoreHoursUsed += (item.Quantity * 8.0); else if (item.Sku.Contains("16-core")) totalCoreHoursUsed += (item.Quantity * 16.0);
                                else if (item.Sku.Contains("32-core")) totalCoreHoursUsed += (item.Quantity * 32.0); } }
                        var remainingCoreHours = INCLUDED_CORE_HOURS - totalCoreHoursUsed;
                        var hoursRemaining = Math.Max(0.0, remainingCoreHours / MACHINE_CORE_COUNT);
                        var isQuotaOk = hoursRemaining > SAFE_HOUR_BUFFER;
                        return new BillingInfo { TotalCoreHoursUsed = totalCoreHoursUsed, IncludedCoreHours = INCLUDED_CORE_HOURS, HoursRemaining = hoursRemaining, IsQuotaOk = isQuotaOk };
                    } else if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired) {
                        AnsiConsole.MarkupLine($"[yellow]FAIL (407)[/]");
                        if (attempt < MAX_BILLING_RETRIES) { AnsiConsole.MarkupLine($"[yellow]   Rotating proxy...[/]"); if (TokenManager.RotateProxyForToken(token)) { await Task.Delay(2000); continue; } else { AnsiConsole.MarkupLine("[red]   Rotate failed.[/]"); return new BillingInfo { IsQuotaOk = false, Error = PersistentProxyError }; } }
                        else { AnsiConsole.MarkupLine($"[red]   Failed after retries (407).[/]"); return new BillingInfo { IsQuotaOk = false, Error = PersistentProxyError }; }
                    } else { var error = await response.Content.ReadAsStringAsync(); AnsiConsole.MarkupLine($"[red]FAIL ({response.StatusCode})[/]"); AnsiConsole.MarkupLine($"[yellow]   WARN: API fail.[/]"); AnsiConsole.MarkupLine($"[dim]   {error.Split('\n').FirstOrDefault()}[/]"); return new BillingInfo { IsQuotaOk = false, Error = response.StatusCode.ToString() }; }
                } catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException) {
                    AnsiConsole.MarkupLine($"[red]FAIL (Connection)[/]"); AnsiConsole.MarkupLine($"[yellow]   Exception: {ex.Message.Split('\n').FirstOrDefault()}[/]");
                    if (attempt < MAX_BILLING_RETRIES) { AnsiConsole.MarkupLine($"[yellow]   Rotating proxy...[/]"); if (TokenManager.RotateProxyForToken(token)) { await Task.Delay(3000); continue; } else { AnsiConsole.MarkupLine("[red]   Rotate failed.[/]"); return new BillingInfo { IsQuotaOk = false, Error = PersistentProxyError }; } }
                    else { AnsiConsole.MarkupLine($"[red]   Failed after retries (Connection).[/]"); return new BillingInfo { IsQuotaOk = false, Error = PersistentProxyError }; }
                } catch (Exception ex) { AnsiConsole.MarkupLine($"[red]FAIL (Unexpected)[/]"); AnsiConsole.MarkupLine($"[red]   Exception: {ex.Message}[/]"); return new BillingInfo { IsQuotaOk = false, Error = $"Unexpected: {ex.GetType().Name}" }; }
            } AnsiConsole.MarkupLine("[red]Billing Check: Failed (fallback).[/]"); return new BillingInfo { IsQuotaOk = false, Error = PersistentProxyError };
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
    public class BillingReport { [JsonPropertyName("usageItems")] public List<UsageItem>? UsageItems{get;set;} }
    public class UsageItem { [JsonPropertyName("product")] public string Product{get;set;} = ""; [JsonPropertyName("sku")] public string Sku{get;set;} = ""; [JsonPropertyName("quantity")] public double Quantity{get;set;} }
}
