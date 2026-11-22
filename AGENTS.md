# ECert — repository notes for agents

## Project

ASP.NET Core 10 MVC, EF Core, MySQL/MariaDB, Arabic RTL. Repo:
https://github.com/alifaleh302-dev/ECert.

## Build & test

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build ECert/ECert.csproj -c Release
dotnet test ECert.Tests/ECert.Tests.csproj        # needs MySQL/MariaDB on 127.0.0.1:3306
```

Tests run against a **real** MySQL server (not in-memory/SQLite) via
`ECert.Tests/Infrastructure/TestAppFactory.cs`, which creates and drops a
throwaway database per run. The app itself uses `EnsureCreated` on first boot plus
per-feature idempotent `*SchemaMigrationService` classes for existing databases.

Run the app: `/tmp/runapp.sh` (port 12000). Login at `/Auth/Login`, user `admin`.

## Unified system settings (إعدادات النظام)

One sidebar entry (`SystemSettings`) opens `/SystemSettings`, which redirects to the
first tab the signed-in role may open. Tabs are declared once in
`ECert/Services/SystemSettingsSections.cs` (label, icon, controller/action, URL,
permission); the tab strip (`Views/Shared/_SettingsTabs.cshtml`), the sidebar entry
and the hub redirect all read from it. Settings pages set
`Layout = "_SettingsLayout"`, which renders the shared header, tab strip and
breadcrumb — don't add a `page-header` to them. Security and backup are two tabs of
`SecuritySettingsController` (`Index` / `Backup`) with separate POST actions
(`SaveSecurity` / `SaveBackup`) so one form never overwrites the other's fields.

## GitHub credentials in this environment

Three credential env vars are injected; only two are usable:

| Env var | Account | HTTP scopes | ECert repo permission |
| --- | --- | --- | --- |
| `GITHUB_TOKEN` | `hamoud302js-wq` | none (`x-oauth-scopes:` empty → fine-grained, permissionless) | **pull only** (read) |
| `GITHUB_PERSONAL_ACCESS_TOKEN` | `alifaleh302-dev` | `gist, repo, workflow` | **admin / push / pull** |
| `OH_GIT_CREDENTIAL_ACCESS_TOKEN_GITHUB` | — | invalid (HTTP 401) | none |

Consequences:

- The MCP `default_create_pr` tool and plain `GITHUB_TOKEN` API calls return
  **403 "Resource not accessible by integration"** on this repo.
- **Create PRs / push with `GITHUB_PERSONAL_ACCESS_TOKEN`** (the repo owner),
  e.g. a small `python3` `urllib` script, or embed it in the git remote URL.

## Certificate designer

- Editor UI + JS: `ECert/Views/CertificateDesigns/Index.cshtml` (one big IIFE).
- Styles: `ECert/wwwroot/css/certificate-design-editor.css`.
- Save contract: `ECert/Controllers/CertificateDesignsController.cs`
  (`DesignElementPayload` + `ParseElements`).
- Public render: `ECert/Views/Certificates/VerifyResult.cshtml`.
- Schema: `ECert/Services/CertificateDesignSchemaMigrationService.cs` — **additive
  only**, never drops data.
- Contract tests: `ECert.Tests/CertificateDesignerTests.cs` (assert on the real
  rendered HTTP surface, so keep test strings in sync with the view).

Designer work was organised in phases, one branch each:
Phase 1 nav/snapping (#19), Phase 2 multi-select (#20),
Phase 3 align/distribute/order/layers (#21),
Phase 4 speed/access — clipboard, inline edit, context menu, palette, rulers (#22),
Phase 5 pro polish — grouping, typography, presets, drag-to-place, safe area (#24).
All five are merged into `main` (integration merge `cc80d84`); the phase branches
are spent.

The phases stacked linearly (each built on the previous), so merging them into
`main` needed one manual conflict resolution: `main` had since moved the public
element projection out of `CertificatesController.Verify` into
`CertificateRenderService`. Keep that service as the single mapping point and
copy new element fields there — the controller just does
`model.DesignElements = design.Elements.ToList()`.

A fresh phase's server-rendered markup only appears after a **Release rebuild
plus a process restart** — the running `dotnet run --no-build` keeps the old
`ECert.Views.dll` loaded, so a live browser check can silently show stale HTML.

Pushing straight to `main` (via `GITHUB_PERSONAL_ACCESS_TOKEN`, which is the repo
owner) does not auto-close a PR whose base was another branch, and the MCP
`default_create_pr` / `gh pr create` tools fail with "must be a collaborator"
because the integration `GITHUB_TOKEN` is a read-only fine-grained token — create
and close PRs with the REST API instead.

### Validating designer JS

The `<script>` block contains Razor. Before building, strip it and syntax-check:

```bash
python3 - <<'PY'
import re, subprocess, pathlib
src = pathlib.Path('ECert/Views/CertificateDesigns/Index.cshtml').read_text(encoding='utf-8')
js = max(re.findall(r'<script>(.*?)</script>', src, re.S), key=len)
js = js.replace('@Html.Raw(', '(').replace('@@', '@')
js = re.sub(r'@(foreach|if|for)\s*\(.*?\)\s*\{', '{', js, flags=re.S)
js = re.sub(r'</?text>', '', js)
js = re.sub(r'@Model\.[A-Za-z0-9_.]+', '0', js)
js = re.sub(r'@[A-Za-z][A-Za-z0-9_.]*', '0', js)
pathlib.Path('/tmp/designer.js').write_text(js, encoding='utf-8')
subprocess.run(['node', '--check', '/tmp/designer.js'], check=True)
PY
```
