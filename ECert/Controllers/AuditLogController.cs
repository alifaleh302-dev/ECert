using System.Data;
using System.Data.Common;
using ECert.Data;
using ECert.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class AuditLogController : Controller
{
    private readonly ECertDbContext _db;
    private readonly ILogger<AuditLogController> _logger;
    public AuditLogController(ECertDbContext db, ILogger<AuditLogController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private bool HasPermission(string perm) => User.HasClaim(c => c.Type == "Permission" && c.Value == perm);

    // Bound from the query string only: a plain `action` parameter would otherwise
    // bind the MVC route value ("Index"), so the filter silently became Action='Index'.
    public async Task<IActionResult> Index(string? entityType, [FromQuery(Name = "action")] string? action)
    {
        var roleName = User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.Role)?.Value;
        if (!HasPermission("view-audit-log") && roleName != "SuperAdmin") return Forbid();

        int totalInDb = 0;
        List<AuditLog> logs = new();
        string? errorMessage = null;

        try
        {
            totalInDb = await _db.AuditLogs.IgnoreQueryFilters().CountAsync();

            // 1) نحاول القراءة عبر EF Core.
            IQueryable<AuditLog> query = _db.AuditLogs.IgnoreQueryFilters().AsQueryable();
            if (!string.IsNullOrEmpty(entityType))
                query = query.Where(a => a.EntityType == entityType);
            if (!string.IsNullOrEmpty(action))
                query = query.Where(a => a.Action == action);

            logs = await query.OrderByDescending(a => a.Timestamp).Take(200).ToListAsync();

            // 2) لو عاد EF بصفر رغم وجود صفوف في الجدول (يحدث بصمت مع هذا الجدول)،
            //    نقرأ مباشرة عبر ADO.NET لضمان ظهور السجلات دائماً.
            if (logs.Count == 0 && totalInDb > 0)
            {
                _logger.LogWarning("AuditLog: EF returned 0 rows while table has {Total}; switching to direct ADO.NET read", totalInDb);
                var (adoLogs, adoErr) = await FetchViaAdoAsync(entityType, action);
                if (adoLogs.Count > 0)
                {
                    logs = adoLogs;
                }
                else if (!string.IsNullOrEmpty(adoErr))
                {
                    errorMessage = adoErr;
                }
                else if (string.IsNullOrEmpty(entityType) && string.IsNullOrEmpty(action))
                {
                    // لا فلتر ومع ذلك صفر => مشكلة حقيقية في القراءة
                    errorMessage = "تعذّرت قراءة السجلات عبر كلا المسارين (EF وADO.NET) رغم وجود صفوف في الجدول.";
                }
                // مع فلتر: صفر نتائج طبيعي (لا سجلات مطابقة) - لا نعرض خطأً
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AuditLog page threw for user={User} role={Role}", User.Identity?.Name, roleName);
            if (logs.Count == 0)
            {
                var (adoLogs, adoErr) = await FetchViaAdoAsync(entityType, action);
                if (adoLogs.Count > 0)
                {
                    logs = adoLogs;
                }
                else
                {
                    errorMessage = $"{ex.GetType().Name}: {ex.Message}";
                    if (!string.IsNullOrEmpty(adoErr))
                        errorMessage += $" | ADO: {adoErr}";
                }
            }
            else
            {
                errorMessage = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        // قيم مميزة لقوائم التصفية (قراءة عبر ADO.NET احتياطاً إن أخفقت EF)
        var distinctActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinctEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (actions, entities) = await FetchDistinctAsync();
            foreach (var a in actions) if (!string.IsNullOrEmpty(a)) distinctActions.Add(a);
            foreach (var e in entities) if (!string.IsNullOrEmpty(e)) distinctEntities.Add(e);
        }
        catch { /* القوائم تبقى فارغة ولا تعطّل الصفحة */ }

        ViewBag.Logs = logs;
        ViewBag.TotalCount = totalInDb;
        ViewBag.EntityType = entityType;
        ViewBag.Action = action;
        ViewBag.Error = errorMessage;
        ViewBag.ActorRole = roleName;
        ViewBag.AvailableActions = distinctActions.OrderBy(x => x).ToList();
        ViewBag.AvailableEntities = distinctEntities.OrderBy(x => x).ToList();
        return View();
    }

    /// <summary>قراءة مباشرة عبر ADO.NET — لا تعتمد على تخطيط كيان EF لهذا الجدول.</summary>
    private async Task<(List<AuditLog>, string?)> FetchViaAdoAsync(string? entityType, string? action)
    {
        var result = new List<AuditLog>();
        try
        {
            var conn = _db.Database.GetDbConnection();
            var shouldClose = conn.State != ConnectionState.Open;
            if (shouldClose) await conn.OpenAsync();
            try
            {
                var sql = "SELECT `AuditLogId`, `UserName`, `Action`, `EntityType`, `EntityId`, `OldValues`, `NewValues`, `Timestamp`, `IpAddress` FROM `AuditLogs`";
                var conditions = new List<string>();
                if (!string.IsNullOrEmpty(entityType)) conditions.Add("`EntityType` = @et");
                if (!string.IsNullOrEmpty(action)) conditions.Add("`Action` = @a");
                if (conditions.Count > 0) sql += " WHERE " + string.Join(" AND ", conditions);
                sql += " ORDER BY `Timestamp` DESC LIMIT 200";

                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                if (!string.IsNullOrEmpty(entityType)) cmd.Parameters.Add(CreateParam(cmd, "@et", entityType));
                if (!string.IsNullOrEmpty(action)) cmd.Parameters.Add(CreateParam(cmd, "@a", action));

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    result.Add(new AuditLog
                    {
                        AuditLogId = reader.GetInt32(0),
                        UserName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        Action = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        EntityType = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                        EntityId = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
                        OldValues = reader.IsDBNull(5) ? null : reader.GetString(5),
                        NewValues = reader.IsDBNull(6) ? null : reader.GetString(6),
                        Timestamp = reader.GetDateTime(7),
                        IpAddress = reader.IsDBNull(8) ? null : reader.GetString(8)
                    });
                }
                return (result, null);
            }
            finally
            {
                if (shouldClose) conn.Close();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AuditLog ADO.NET fallback failed");
            return (result, ex.Message);
        }
    }

    private async Task<(List<string> actions, List<string> entities)> FetchDistinctAsync()
    {
        var actions = new List<string>();
        var entities = new List<string>();
        var conn = _db.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT `Action` FROM `AuditLogs` WHERE `Action` IS NOT NULL AND `Action` <> ''";
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) actions.Add(r.GetString(0));
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT `EntityType` FROM `AuditLogs` WHERE `EntityType` IS NOT NULL AND `EntityType` <> ''";
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) entities.Add(r.GetString(0));
            }
            return (actions, entities);
        }
        finally
        {
            if (shouldClose) conn.Close();
        }
    }

    private static DbParameter CreateParam(DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        return p;
    }
}
