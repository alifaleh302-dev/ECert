using ECert.Data;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Reads the global system settings that the financial layer needs at runtime — the institution's
/// currency (name and display symbol) and the direct-refund ceiling. Values live in the key/value
/// <c>SiteSetting</c> table, so no schema change is needed to add a setting; every reader goes
/// through this service so a setting is never duplicated as a hard-coded string in a view.
/// </summary>
public class SystemSettingsService
{
    private readonly ECertDbContext _db;
    public SystemSettingsService(ECertDbContext db) => _db = db;

    public const string CurrencyNameKey = "CurrencyName";
    public const string CurrencySymbolKey = "CurrencySymbol";
    public const string RefundApprovalThresholdKey = "RefundApprovalThreshold";

    public const string DefaultCurrencyName = "ريال يمني";
    public const string DefaultCurrencySymbol = "ر.ي";

    /// <summary>A setting's raw value, or <paramref name="defaultValue"/> when it is missing or blank.</summary>
    public async Task<string> GetStringAsync(string key, string defaultValue = "")
    {
        var value = await _db.SiteSettings.Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value!;
    }

    /// <summary>A decimal setting, or null when it is unset or not a number.</summary>
    public async Task<decimal?> GetDecimalAsync(string key)
    {
        var value = await GetStringAsync(key);
        return decimal.TryParse(value, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    public Task<string> GetCurrencyNameAsync() => GetStringAsync(CurrencyNameKey, DefaultCurrencyName);

    public Task<string> GetCurrencySymbolAsync() => GetStringAsync(CurrencySymbolKey, DefaultCurrencySymbol);

    /// <summary>
    /// The largest refund that may be applied directly (without a SuperAdmin approval). A null result
    /// means the ceiling is not configured, so every refund applies immediately.
    /// </summary>
    public Task<decimal?> GetRefundApprovalThresholdAsync() => GetDecimalAsync(RefundApprovalThresholdKey);

    /// <summary>True when a refund of <paramref name="amount"/> must wait for SuperAdmin approval.</summary>
    public async Task<bool> RefundRequiresApprovalAsync(decimal amount)
    {
        var threshold = await GetRefundApprovalThresholdAsync();
        return threshold.HasValue && amount > threshold.Value;
    }
}
