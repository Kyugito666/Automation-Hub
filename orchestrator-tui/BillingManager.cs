using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace OrchestratorV2;

public class BillingInfo
{
    public double TotalCoreHoursUsed { get; set; }
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

public static class BillingManager
{
    private const double INCLUDED_CORE_HOURS = 120.0;
    private const double MACHINE_CORE_COUNT = 4.0;
    private const double SAFE_HOUR_BUFFER = 2.0;

    public static async Task<BillingInfo> GetBillingInfo(TokenEntry token)
    {
        if (string.IsNullOrEmpty(token.Username))
        {
            return new BillingInfo { IsQuotaOk = false, Error = "Username unknown" };
        }

        try
        {
            using var client = TokenManager.CreateHttpClient(token);
            var url = $"https://api.github.com/users/{token.Username}/settings/billing/usage";
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                return new BillingInfo { IsQuotaOk = false, Error = response.StatusCode.ToString() };
            }

            var json = await response.Content.ReadAsStringAsync();
            var report = JsonSerializer.Deserialize<BillingReport>(json);

            if (report?.UsageItems == null)
            {
                return new BillingInfo { IsQuotaOk = false, Error = "Invalid JSON format" };
            }

            double totalCoreHoursUsed = 0.0;
            foreach (var item in report.UsageItems)
            {
                if (item.Product == "codespaces")
                {
                    if (item.Sku.Contains("compute 2-core"))
                        totalCoreHoursUsed += item.Quantity * 2.0;
                    else if (item.Sku.Contains("compute 4-core"))
                        totalCoreHoursUsed += item.Quantity * 4.0;
                    else if (item.Sku.Contains("compute 8-core"))
                        totalCoreHoursUsed += item.Quantity * 8.0;
                    else if (item.Sku.Contains("compute 16-core"))
                        totalCoreHoursUsed += item.Quantity * 16.0;
                    else if (item.Sku.Contains("compute 32-core"))
                        totalCoreHoursUsed += item.Quantity * 32.0;
                }
            }

            var remainingCoreHours = INCLUDED_CORE_HOURS - totalCoreHoursUsed;
            var hoursRemaining = Math.Max(0.0, remainingCoreHours / MACHINE_CORE_COUNT);
            var isQuotaOk = hoursRemaining > SAFE_HOUR_BUFFER;

            return new BillingInfo
            {
                TotalCoreHoursUsed = totalCoreHoursUsed,
                HoursRemaining = hoursRemaining,
                IsQuotaOk = isQuotaOk
            };
        }
        catch (Exception ex)
        {
            return new BillingInfo { IsQuotaOk = false, Error = ex.Message };
        }
    }

    public static void DisplayBilling(BillingInfo billing, string username)
    {
        AnsiConsole.MarkupLine($"Billing @{username}: Used ~[yellow]{billing.TotalCoreHoursUsed:F1}[/] of [green]{INCLUDED_CORE_HOURS:F1}[/] core-hours.");
        AnsiConsole.MarkupLine($"   Approx. [bold {(billing.IsQuotaOk ? "green" : "red")}]{billing.HoursRemaining:F1} hours remaining[/] (for {MACHINE_CORE_COUNT}-core machine).");

        if (!billing.IsQuotaOk)
        {
            AnsiConsole.MarkupLine($"   [red]WARNING: Low quota (< {SAFE_HOUR_BUFFER}h) or exhausted. Token rotation needed.[/]");
        }
    }
}
