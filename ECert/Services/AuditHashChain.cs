using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ECert.Models;

namespace ECert.Services;

/// <summary>
/// Builds the tamper-evident hash chain over the audit tables. Each row stores the hash of the row
/// before it plus a hash of its own content, so changing or removing any row breaks every hash
/// after it. The exact serialisation lives here — shared by the runtime writer, the integrity
/// verifier and the one-time backfill — so all three always agree on a row's hash.
/// </summary>
public static class AuditHashChain
{
    /// <summary>
    /// Timestamps are normalised to whole seconds before they are hashed and stored. The database
    /// keeps sub-second precision, and formatting a value that has already been rounded by the
    /// storage engine would make the in-memory hash disagree with the re-read hash.
    /// </summary>
    public static DateTime Normalize(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);

    private static string Format(DateTime value) =>
        Normalize(value).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Format(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    public static string Payload(AuditLog log) => string.Join('|',
        log.UserName, log.Action, log.EntityType,
        log.EntityId?.ToString(CultureInfo.InvariantCulture) ?? "",
        log.OldValues ?? "", log.NewValues ?? "",
        Format(log.Timestamp), log.IpAddress ?? "");

    public static string Payload(FinancialAuditEntry entry) => string.Join('|',
        entry.UserName, entry.ActionType, Format(entry.Amount),
        entry.ReferenceType ?? "",
        entry.ReferenceId?.ToString(CultureInfo.InvariantCulture) ?? "",
        entry.ReferenceNumber ?? "", entry.Details ?? "",
        Format(entry.Timestamp));

    /// <summary>SHA-256 (lower-case hex) over the previous hash and this row's payload.</summary>
    public static string Compute(string? previousHash, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes((previousHash ?? string.Empty) + "|" + payload);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string Compute(AuditLog log, string? previousHash) => Compute(previousHash, Payload(log));

    public static string Compute(FinancialAuditEntry entry, string? previousHash) => Compute(previousHash, Payload(entry));
}
