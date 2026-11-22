using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ECert.Controllers;

/// <summary>
/// Entry point for the unified settings area (إعدادات النظام). The sidebar links here; the action
/// sends the user to the first tab they are allowed to open, so one navigation item serves every
/// role without exposing a tab the role cannot use.
/// </summary>
[Authorize]
public class SystemSettingsController : Controller
{
    public IActionResult Index()
    {
        var first = SystemSettingsSections.VisibleFor(User).FirstOrDefault();
        return first is null ? Forbid() : Redirect(first.Url);
    }
}
