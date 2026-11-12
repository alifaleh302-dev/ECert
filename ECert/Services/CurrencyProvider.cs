using ECert.Data;

namespace ECert.Services;

/// <summary>
/// Per-request access to the institution's currency symbol, so views can render money as
/// <c>@Currency.Format(amount)</c> instead of hard-coding a currency. The symbol is read from the
/// key/value settings once per request and cached on this scoped instance. It is display-only: no
/// conversion between currencies is ever performed.
/// </summary>
public class CurrencyProvider
{
    private readonly ECertDbContext _db;
    private string? _symbol;

    public CurrencyProvider(ECertDbContext db) => _db = db;

    /// <summary>The configured display symbol (e.g. ر.ي), falling back to the built-in default.</summary>
    public string Symbol
    {
        get
        {
            if (_symbol is null)
            {
                var value = _db.SiteSettings
                    .Where(s => s.Key == SystemSettingsService.CurrencySymbolKey)
                    .Select(s => s.Value)
                    .FirstOrDefault();
                _symbol = string.IsNullOrWhiteSpace(value) ? SystemSettingsService.DefaultCurrencySymbol : value!.Trim();
            }
            return _symbol;
        }
    }

    /// <summary>The configured currency name (e.g. ريال يمني).</summary>
    public string Name
    {
        get
        {
            var value = _db.SiteSettings
                .Where(s => s.Key == SystemSettingsService.CurrencyNameKey)
                .Select(s => s.Value)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(value) ? SystemSettingsService.DefaultCurrencyName : value!.Trim();
        }
    }

    /// <summary>Formats an amount with the given numeric format, suffixed by the currency symbol.</summary>
    public string Format(decimal amount, string format = "N2") => $"{amount.ToString(format)} {Symbol}";
}
