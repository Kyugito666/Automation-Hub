using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public static class BillingManager
{
    private const double INCLUDED_CORE_HOURS = 120.0;
    private const double MACHINE_CORE_COUNT = 4.0;
    private const double SAFE_HOUR_BUFFER = 2.0;

    public static async Task<BillingInfo> GetBillingInfo(TokenEntry token)
    {
        if (string.IsNullOrEmpty(token.Username))
        {
            AnsiConsole.MarkupLine("[red]Username tidak diketahui untuk token, tidak bisa cek billing.[/]");
            return new BillingInfo { IsQuotaOk = false, Error = "Username unknown" };
        }

        try
        {
            using var client = TokenManager.CreateHttpClient(token);
            var url = $"/users/{token.Username}/settings/billing/usage";
            var response = await client.GetAsync("https://api.github.com" + url);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[yellow]WARNING: Gagal menghubungi API billing ({response.StatusCode}). Anggap kuota habis.[/]");
                AnsiConsole.MarkupLine($"[dim]{error.Split('\n').FirstOrDefault()}[/]");
                return new BillingInfo { IsQuotaOk = false, Error = response.StatusCode.ToString() };
            }

            var json = await response.Content.ReadAsStringAsync();
            var report = JsonSerializer.Deserialize<BillingReport>(json);

            if (report?.UsageItems == null)
            {
                AnsiConsole.MarkupLine("[yellow]WARNING: Format data billing tidak dikenal. Anggap kuota habis.[/]");
                return new BillingInfo { IsQuotaOk = false, Error = "Invalid JSON format" };
            }
            
            double totalCoreHoursUsed = 0.0;
            foreach (var item in report.UsageItems)
            {
                if (item.Product == "codespaces")
                {
                    if (item.Sku.Contains("compute 2-core"))
                        totalCoreHoursUsed += (item.Quantity * 2.0);
                    else if (item.Sku.Contains("compute 4-core"))
                        totalCoreHoursUsed += (item.Quantity * 4.0);
                    else if (item.Sku.Contains("compute 8-core"))
                        totalCoreHoursUsed += (item.Quantity * 8.0);
                    else if (item.Sku.Contains("compute 16-core"))
                        totalCoreHoursUsed += (item.Quantity * 16.0);
                    else if (item.Sku.Contains("compute 32-core"))
                        totalCoreHoursUsed += (item.Quantity * 32.0);
                }
            }

            var remainingCoreHours = INCLUDED_CORE_HOURS - totalCoreHoursUsed;
            var hoursRemaining = Math.Max(0.0, remainingCoreHours / MACHINE_CORE_COUNT);
            var isQuotaOk = hoursRemaining > SAFE_HOUR_BUFFER;

            return new BillingInfo
            {
                TotalCoreHoursUsed = totalCoreHoursUsed,
                IncludedCoreHours = INCLUDED_CORE_HOURS,
                HoursRemaining = hoursRemaining,
                IsQuotaOk = isQuotaOk
            };
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Exception saat cek billing: {ex.Message}[/]");
            return new BillingInfo { IsQuotaOk = false, Error = ex.Message };
        }
    }

    public static void DisplayBilling(BillingInfo billing, string username)
    {
        AnsiConsole.MarkupLine($"Billing [cyan]@{username}[/]: Used ~[yellow]{billing.TotalCoreHoursUsed:F1}[/] of [green]{billing.IncludedCoreHours:F1}[/] core-hours.");
        AnsiConsole.MarkupLine($"   Approx. [bold {(billing.IsQuotaOk ? "green" : "red")}]{billing.HoursRemaining:F1} hours remaining[/] (for {MACHINE_CORE_COUNT}-core machine).");
        
        if (!billing.IsQuotaOk)
        {
            AnsiConsole.MarkupLine($"   [red]WARNING: Kuota rendah (< {SAFE_HOUR_BUFFER}h) atau habis. Rotasi token diperlukan.[/]");
        }
    }
}

public class BillingInfo
{
    public double TotalCoreHoursUsed { get; set; }
    public double IncludedCoreHours { get; set; }
    public double HoursRemaining { get; set; }
    public bool IsQuotaOk { get; set; }
    public string? Error { get; set; }
}

public class BillingReport
{
    [JsonPropertyName("usageItems")]
    public List<UsageItem>? UsageItems { get; set; }
}

public class UsageItem
{
    [JsonPropertyName("product")]
    public string Product { get; set; } = "";
    [JsonPropertyName("sku")]
    public string Sku { get; set; } = "";
    [JsonPropertyName("quantity")]
    public double Quantity { get; set; }
}
