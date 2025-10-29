using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using System.Net; // <-- Pastikan using System.Net ada

namespace Orchestrator;

public static class BillingManager
{
    private const double INCLUDED_CORE_HOURS = 120.0;
    // === PERBAIKAN: Sesuaikan core count mesin (dari CodespaceManager.cs) ===
    // Mesin kita pakai standardLinux32gb yang sepertinya 4-core, tapi pastikan ini benar
    private const double MACHINE_CORE_COUNT = 4.0; // Sesuaikan jika mesin beda
    // === AKHIR PERBAIKAN ===
    private const double SAFE_HOUR_BUFFER = 2.0; // Buffer aman (misal 2 jam)
    private const int MAX_BILLING_RETRIES = 2; // Batas retry jika proxy error 407

    public static async Task<BillingInfo> GetBillingInfo(TokenEntry token)
    {
        if (string.IsNullOrEmpty(token.Username))
        {
            AnsiConsole.MarkupLine("[red]Username tidak diketahui untuk token, tidak bisa cek billing.[/]");
            return new BillingInfo { IsQuotaOk = false, Error = "Username unknown" };
        }

        // === PERBAIKAN: Tambah Retry Loop untuk handle error proxy ===
        for (int attempt = 1; attempt <= MAX_BILLING_RETRIES; attempt++)
        {
            // Buat HttpClient BARU di tiap attempt biar pake proxy terbaru dari TokenManager
            // (Penting jika proxy dirotasi di attempt sebelumnya)
            using var client = TokenManager.CreateHttpClient(token);
            var url = $"/users/{token.Username}/settings/billing/usage";
            // Tambahkan User-Agent spesifik (opsional tapi bagus untuk debugging)
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"Orchestrator-BillingCheck/{token.Username}");

            try
            {
                AnsiConsole.Markup($"[dim]   Attempting billing check ({attempt}/{MAX_BILLING_RETRIES})...[/]");
                var response = await client.GetAsync("https://api.github.com" + url);

                // 1. Handle Sukses
                if (response.IsSuccessStatusCode)
                {
                    AnsiConsole.MarkupLine("[green]OK[/]");
                    var json = await response.Content.ReadAsStringAsync();
                    var report = JsonSerializer.Deserialize<BillingReport>(json);

                    if (report?.UsageItems == null) {
                        AnsiConsole.MarkupLine("[yellow]WARNING: Format data billing tidak dikenal. Anggap kuota habis.[/]");
                        return new BillingInfo { IsQuotaOk = false, Error = "Invalid JSON format" };
                    }

                    double totalCoreHoursUsed = 0.0;
                    foreach (var item in report.UsageItems) {
                        if (item.Product == "codespaces") {
                            // Gunakan Contains biar lebih fleksibel
                            if (item.Sku.Contains("2-core")) totalCoreHoursUsed += (item.Quantity * 2.0);
                            else if (item.Sku.Contains("4-core")) totalCoreHoursUsed += (item.Quantity * 4.0);
                            else if (item.Sku.Contains("8-core")) totalCoreHoursUsed += (item.Quantity * 8.0);
                            else if (item.Sku.Contains("16-core")) totalCoreHoursUsed += (item.Quantity * 16.0);
                            else if (item.Sku.Contains("32-core")) totalCoreHoursUsed += (item.Quantity * 32.0);
                        }
                    }

                    var remainingCoreHours = INCLUDED_CORE_HOURS - totalCoreHoursUsed;
                    var hoursRemaining = Math.Max(0.0, remainingCoreHours / MACHINE_CORE_COUNT);
                    var isQuotaOk = hoursRemaining > SAFE_HOUR_BUFFER;

                    return new BillingInfo {
                        TotalCoreHoursUsed = totalCoreHoursUsed,
                        IncludedCoreHours = INCLUDED_CORE_HOURS,
                        HoursRemaining = hoursRemaining,
                        IsQuotaOk = isQuotaOk
                    };
                }
                // 2. Handle Error Proxy Auth (407) -> ROTASI & RETRY
                else if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                {
                    AnsiConsole.MarkupLine($"[yellow]FAIL (407 Proxy Auth)[/]");
                    if (attempt < MAX_BILLING_RETRIES) {
                        AnsiConsole.MarkupLine($"[yellow]   Rotating proxy... (Attempt {attempt}/{MAX_BILLING_RETRIES})[/]");
                        if (TokenManager.RotateProxyForToken(token)) {
                            await Task.Delay(2000); // Tunggu sebentar setelah rotasi
                            continue; // Coba lagi dengan proxy baru
                        } else {
                            AnsiConsole.MarkupLine("[red]   Gagal rotasi proxy (tidak ada proxy lagi?). Anggap kuota habis.[/]");
                            return new BillingInfo { IsQuotaOk = false, Error = "Proxy rotation failed (407)" }; // Keluar loop jika rotasi gagal
                        }
                    } else {
                        // Jika attempt terakhir gagal 407, anggap error
                        AnsiConsole.MarkupLine($"[red]   Gagal setelah {MAX_BILLING_RETRIES} attempts (407). Anggap kuota habis.[/]");
                        return new BillingInfo { IsQuotaOk = false, Error = "Proxy Auth failed after retries" };
                    }
                }
                // 3. Handle Error Lainnya (401, 403, 404, 5xx, dll) -> GAGAL LANGSUNG
                else
                {
                    var error = await response.Content.ReadAsStringAsync();
                    AnsiConsole.MarkupLine($"[red]FAIL ({response.StatusCode})[/]");
                    AnsiConsole.MarkupLine($"[yellow]   WARNING: Gagal menghubungi API billing ({response.StatusCode}). Anggap kuota habis.[/]");
                    AnsiConsole.MarkupLine($"[dim]   {error.Split('\n').FirstOrDefault()}[/]");
                    return new BillingInfo { IsQuotaOk = false, Error = response.StatusCode.ToString() };
                }
            }
            // 4. Handle Exception Koneksi (Timeout, DNS, etc) -> ROTASI & RETRY
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                AnsiConsole.MarkupLine($"[red]FAIL (Connection Error)[/]");
                AnsiConsole.MarkupLine($"[yellow]   Exception saat cek billing: {ex.Message.Split('\n').FirstOrDefault()}[/]");
                if (attempt < MAX_BILLING_RETRIES) {
                    AnsiConsole.MarkupLine($"[yellow]   Rotating proxy due to connection error... (Attempt {attempt}/{MAX_BILLING_RETRIES})[/]");
                    if (TokenManager.RotateProxyForToken(token)) {
                        await Task.Delay(3000); // Tunggu lebih lama untuk error koneksi
                        continue; // Coba lagi
                    } else {
                         AnsiConsole.MarkupLine("[red]   Gagal rotasi proxy. Anggap kuota habis.[/]");
                         return new BillingInfo { IsQuotaOk = false, Error = "Proxy rotation failed (Connection Error)" };
                    }
                } else {
                    // Jika attempt terakhir gagal koneksi, anggap error
                    AnsiConsole.MarkupLine($"[red]   Gagal koneksi setelah {MAX_BILLING_RETRIES} attempts. Anggap kuota habis.[/]");
                    return new BillingInfo { IsQuotaOk = false, Error = "Connection failed after retries" };
                }
            }
             // 5. Handle Exception Lainnya -> GAGAL LANGSUNG
            catch (Exception ex)
            {
                 AnsiConsole.MarkupLine($"[red]FAIL (Unexpected Error)[/]");
                 AnsiConsole.MarkupLine($"[red]   Unexpected exception saat cek billing: {ex.Message}[/]");
                 return new BillingInfo { IsQuotaOk = false, Error = $"Unexpected: {ex.GetType().Name}" };
            }
        } // Akhir retry loop
        // === AKHIR PERBAIKAN ===

        // Fallback jika loop selesai tanpa return (seharusnya tidak terjadi)
        AnsiConsole.MarkupLine("[red]Billing Check: Gagal setelah semua retry. Anggap kuota habis.[/]");
        return new BillingInfo { IsQuotaOk = false, Error = "Max retries reached" };
    }


    public static void DisplayBilling(BillingInfo billing, string username)
    {
        // Tampilkan error jika ada dan kuota dianggap habis
        // Kita modifikasi agar pesan error lebih jelas
        if (!string.IsNullOrEmpty(billing.Error) && !billing.IsQuotaOk)
        {
             // Ambil pesan error yang lebih deskriptif
             string reason = billing.Error.Contains("Proxy") ? "Proxy Error" :
                             billing.Error.Contains("Connection") ? "Connection Error" :
                             billing.Error.Contains("unknown") ? "Username Unknown" :
                             billing.Error.Contains("JSON") ? "Invalid Response" :
                             $"API Error ({billing.Error})";

             AnsiConsole.MarkupLine($"Billing @{username.EscapeMarkup()}: [red]CHECK FAILED ({reason})[/]");
             AnsiConsole.MarkupLine($"   [yellow]Assuming quota is insufficient due to error.[/]");
             AnsiConsole.MarkupLine($"   [red]WARNING: Kuota rendah (< {SAFE_HOUR_BUFFER}h) atau habis. Rotasi token diperlukan.[/]");
             return; // Jangan tampilkan info jam jika error
        }

        // Tampilan normal jika tidak ada error fatal
        AnsiConsole.MarkupLine($"Billing @{username.EscapeMarkup()}: Used ~[yellow]{billing.TotalCoreHoursUsed:F1}[/] of [green]{billing.IncludedCoreHours:F1}[/] core-hours.");
        AnsiConsole.MarkupLine($"   Approx. [bold {(billing.IsQuotaOk ? "green" : "red")}]{billing.HoursRemaining:F1} hours remaining[/] (for {MACHINE_CORE_COUNT}-core machine).");

        if (!billing.IsQuotaOk)
        {
            AnsiConsole.MarkupLine($"   [red]WARNING: Kuota rendah (< {SAFE_HOUR_BUFFER}h) atau habis. Rotasi token diperlukan.[/]");
        }
        // else // Optional: Pesan jika kuota OK
        // {
        //     AnsiConsole.MarkupLine($"   [green]Quota OK.[/]");
        // }
    }
}

// Struct BillingInfo, BillingReport, UsageItem tidak perlu diubah
public class BillingInfo
{
    public double TotalCoreHoursUsed { get; set; }
    public double IncludedCoreHours { get; set; } = 120.0;
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
