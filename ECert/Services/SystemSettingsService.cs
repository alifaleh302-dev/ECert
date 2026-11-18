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
    public const string RefundAutoApprovalKey = "RefundAutoApprovalEnabled";
    public const string HandoverAutoApprovalKey = "HandoverAutoApprovalEnabled";
    public const string MaxOpenHandoversPerEmployeeKey = "MaxOpenHandoversPerEmployee";

    public const string DefaultCurrencyName = "ريال يمني";
    public const string DefaultCurrencySymbol = "ر.ي";
    public const int DefaultMaxOpenHandoversPerEmployee = 1;

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

    /// <summary>A boolean setting, defaulting when it is unset or not "true"/"false".</summary>
    public async Task<bool> GetBoolAsync(string key, bool defaultValue = false)
    {
        var value = await GetStringAsync(key);
        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    /// <summary>An integer setting, defaulting when it is unset or not a whole number.</summary>
    public async Task<int> GetIntAsync(string key, int defaultValue)
    {
        var value = await GetStringAsync(key);
        return int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
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

    /// <summary>
    /// When on, every refund applies immediately and none waits for approval — the ceiling is ignored.
    /// Off by default, so the ceiling governs unless an administrator opts out of the gate.
    /// </summary>
    public Task<bool> RefundAutoApprovalEnabledAsync() => GetBoolAsync(RefundAutoApprovalKey);

    /// <summary>
    /// When on, a handover request settles immediately (custody debited, fund credited) instead of
    /// waiting for an approver. Off by default.
    /// </summary>
    public Task<bool> HandoverAutoApprovalEnabledAsync() => GetBoolAsync(HandoverAutoApprovalKey);

    /// <summary>
    /// The most handover requests an employee may keep open at once. At least one, so an employee can
    /// always request a handover; the default is one.
    /// </summary>
    public async Task<int> GetMaxOpenHandoversPerEmployeeAsync()
    {
        var value = await GetIntAsync(MaxOpenHandoversPerEmployeeKey, DefaultMaxOpenHandoversPerEmployee);
        return value < 1 ? DefaultMaxOpenHandoversPerEmployee : value;
    }
}
