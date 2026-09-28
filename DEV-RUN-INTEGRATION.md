# Bold Upgrade Center dev-run Integration

This document explains how the `bold-upgrade-center` application is integrated with the local `dev-run` environment.

The finalized Upgrade Center source is hosted as a standalone repository and is exposed in dev-run through the `/upgrade-center` path.

## Repository

| Item | Value |
| --- | --- |
| Repository name | `bold-upgrade-center` |
| Repository URL | `https://gitea.syncfusion.com/bold-bi/bold-upgrade-center` |
| Branch | `main` |
| Local path | `D:\dev-run-new\bold-upgrade-center` |
| Project file | `D:\dev-run-new\bold-upgrade-center\Bold.UpgradeCenter.csproj` |
| dev-run service id | `bold-upgrade-center` |
| Local service URL | `http://localhost:6717/upgrade-center` |
| Proxy URL | `https://localhost:6843/platform/upgrade-center` |

## dev-run Files Modified

The dev-run integration requires changes in these files:

| File | Purpose |
| --- | --- |
| `dev-run\v2\config\source-workspace.source-dev.json` | Tells `devrun env fresh --sync-source` where to clone/pull the `bold-upgrade-center` repository from. |
| `dev-run\config\repo-manifest.json` | Keeps the older repository inventory aware of the new repository. |
| `dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json` | Registers `bold-upgrade-center` as a dev-run service, including project path, port, health check, dependencies, and public route. |
| `dev-run\v2\src\DevRun.Cli\RuntimeManager.cs` | Adds runtime environment values and proxy target values for the new service. |
| `dev-run\src\repo-local-proxy\Program.cs` | Routes `/upgrade-center` requests from the local proxy to the `bold-upgrade-center` service. |
| `bold-upgrade-center\Program.cs` | Application-side route/static-file/health-check setup for `/upgrade-center` and forwarded proxy prefix support. |

## Repository Pull Configuration

File:

```text
D:\dev-run-new\dev-run\v2\config\source-workspace.source-dev.json
```

Add the repository entry under the `repositories` array:

```json
{
  "name": "bold-upgrade-center",
  "path": "bold-upgrade-center",
  "remoteUrl": "https://gitea.syncfusion.com/bold-bi/bold-upgrade-center",
  "defaultBranch": "main",
  "featureBranch": "main",
  "branchSwitchingAllowed": false,
  "dirtyBlocksPublish": false
}
```

Purpose:

- Allows `devrun env fresh --sync-source` to clone or pull the repository.
- Keeps the repository under `D:\dev-run-new\bold-upgrade-center`.
- Uses `main` as the source branch.
- Keeps branch switching disabled so dev-run does not unexpectedly move a developer to a different branch.

Optional legacy inventory file:

```text
D:\dev-run-new\dev-run\config\repo-manifest.json
```

Add:

```json
{
  "name": "bold-upgrade-center",
  "path": "bold-upgrade-center",
  "cloneUrl": "https://gitea.syncfusion.com/bold-bi/bold-upgrade-center",
  "bootstrap": {
    "npmRoots": [],
    "requiresTools": []
  }
}
```

Purpose:

- Keeps the older repository manifest aligned with the v2 source workspace.
- The application does not require separate npm bootstrap steps for dev-run startup.

## Service Map Configuration

File:

```text
D:\dev-run-new\dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json
```

Add this service entry under the `services` array:

```json
{
  "id": "bold-upgrade-center",
  "displayName": "Bold Upgrade Center",
  "repository": "bold-upgrade-center",
  "projectPath": "Bold.UpgradeCenter.csproj",
  "runtimeMode": "publish",
  "configuration": "Debug",
  "runtimePath": "bi/upgrade-center",
  "assembly": "Bold.UpgradeCenter.dll",
  "dependencies": [
    "idp-web",
    "idp-api",
    "ums"
  ],
  "ports": {
    "http": 6717
  },
  "health": {
    "url": "http://localhost:6717/upgrade-center/health-check",
    "expectedStatus": 200,
    "timeoutSeconds": 30
  },
  "publicRoutes": [
    "https://localhost:6843/platform/upgrade-center"
  ],
  "logs": [
    ".runtime/proxy-traefik-https-source-dev/logs/bold-upgrade-center.log"
  ]
}
```

Purpose:

- Registers the service with the final service id `bold-upgrade-center`.
- Publishes the project from the `bold-upgrade-center` repository folder.
- Runs the published assembly `Bold.UpgradeCenter.dll`.
- Uses port `6717`.
- Exposes the app through `/upgrade-center`.
- Makes the app available through the local proxy at `/platform/upgrade-center`.
- Declares the required identity dependencies:
  - `idp-web`
  - `idp-api`
  - `ums`

Do not reintroduce earlier temporary service names.

The previous initial application route `/upgrader` should not be used going forward.

## Runtime Environment Wiring

File:

```text
D:\dev-run-new\dev-run\v2\src\DevRun.Cli\RuntimeManager.cs
```

Add the new service id to the default environment flow:

```csharp
if (service.Id is "bold-upgrade-center")
{
    AddLocalServiceUrlEnvironment(loaded, environment);
}
```

Purpose:

- Provides the Upgrade Center with the same local service discovery values used by the rest of the dev-run services.
- This includes internal URLs for IDP, UMS, BI API, BI web, jobs, datasource/designer, ETL, AI, and Reports services where available.

Add the proxy target environment variable:

```csharp
environment["REPO_PROXY_BOLDBI_UPGRADE_CENTER"] =
    GetOptionalServiceUrl(loaded, "bold-upgrade-center", 6717);
```

Purpose:

- Gives the local proxy the backend target for `/upgrade-center`.

Add the local service URL:

```csharp
environment["BOLDBI_UPGRADE_CENTER_SERVICE_URL"] =
    GetOptionalServiceUrl(loaded, "bold-upgrade-center", 6717) + "/upgrade-center";
```

Purpose:

- Makes the Upgrade Center URL available to other dev-run services if they need to link to it.

## Local Proxy Route

File:

```text
D:\dev-run-new\dev-run\src\repo-local-proxy\Program.cs
```

Add the Upgrade Center target to the diagnostic route list:

```csharp
routes = new
{
    options.IdpWeb,
    options.Ums,
    options.IdpApi,
    options.BiWeb,
    options.BiApi,
    options.BiJobs,
    options.BiDesigner,
    options.Etl,
    options.Ai,
    options.BoldBiUpgradeCenter
}
```

Add the `/upgrade-center` routing rule:

```csharp
if (path.Equals("/upgrade-center", StringComparison.OrdinalIgnoreCase) ||
    path.StartsWith("/upgrade-center/", StringComparison.OrdinalIgnoreCase))
{
    return (options.BoldBiUpgradeCenter, path);
}
```

Add the proxy option:

```csharp
public string BoldBiUpgradeCenter { get; init; } = string.Empty;
```

Load the proxy option from the environment:

```csharp
BoldBiUpgradeCenter = GetSetting("REPO_PROXY_BOLDBI_UPGRADE_CENTER", "http://localhost:6717")
```

Add the service to absolute URL rewrite source bases:

```csharp
var sourceBases = new[]
{
    HostedBase,
    IdpWeb,
    Ums,
    IdpApi,
    BiWeb,
    BiApi,
    BiJobs,
    BiDesigner,
    Etl,
    Ai,
    BoldBiUpgradeCenter
};
```

Purpose:

- Sends `/upgrade-center` and `/upgrade-center/*` requests to the Upgrade Center service.
- Allows redirects and absolute links generated by the service to be rewritten correctly through the local proxy.
- Keeps the public dev-run URL stable:

```text
https://localhost:6843/platform/upgrade-center
```

## Application Route and Path Handling

File:

```text
D:\dev-run-new\bold-upgrade-center\Program.cs
```

The application itself must support the `/upgrade-center` path.

Static files are served under `/upgrade-center`:

```csharp
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/upgrade-center"
});
```

Health checks are exposed at both root and prefixed paths:

```csharp
app.MapHealthChecks("/health-check");
app.MapHealthChecks("/upgrade-center/health-check");
```

The MVC route is registered under `/upgrade-center`:

```csharp
app.MapControllerRoute(
    name: "upgrade-center",
    pattern: "upgrade-center/{action=Index}/{jobId?}",
    defaults: new { controller = "UpgradeCenter" });
```

The root path redirects to `/upgrade-center`:

```csharp
app.MapGet("/", () => Results.Redirect("/upgrade-center"));
```

The app also honors `X-Forwarded-Prefix` from the dev proxy:

```csharp
app.Use(ApplyForwardedPrefix);
```

Purpose:

- Allows the app to work directly at `http://localhost:6717/upgrade-center`.
- Allows the app to work behind the dev-run proxy at `https://localhost:6843/platform/upgrade-center`.
- Ensures static assets such as CSS, JavaScript, and images load correctly under the `/upgrade-center` path.

## Port and Route Mapping

| Layer | URL |
| --- | --- |
| Direct service | `http://localhost:6717/upgrade-center` |
| Direct health check | `http://localhost:6717/upgrade-center/health-check` |
| Proxy route | `https://localhost:6843/platform/upgrade-center` |
| Proxy base | `https://localhost:6843/platform/` |

The proxy strips/maps the public `/platform` prefix and forwards the remaining `/upgrade-center` path to the Upgrade Center service.

## Service Dependencies

The dev-run service map declares these dependencies:

```json
"dependencies": [
  "idp-web",
  "idp-api",
  "ums"
]
```

Purpose:

- `idp-web`: authentication/login and public platform route integration.
- `idp-api`: configuration API used by Upgrade Center.
- `ums`: session validation, admin validation, and authenticated user details.

Other service URLs are still exposed through `AddLocalServiceUrlEnvironment` so the app can interact with BI services when the relevant workflow requires them.

## Important Environment Values

The following values are provided by dev-run through `RuntimeManager.cs`.

Proxy-facing value:

```text
REPO_PROXY_BOLDBI_UPGRADE_CENTER=http://localhost:6717
```

Service-facing value:

```text
BOLDBI_UPGRADE_CENTER_SERVICE_URL=http://localhost:6717/upgrade-center
```

Common local service URL values also provided:

```text
ID_WEB_SERVICE_URL
ID_API_SERVICE_URL
ID_UMS_SERVICE_URL
BI_WEB_SERVICE_URL
BI_API_SERVICE_URL
BI_JOBS_SERVICE_URL
BI_DESIGNER_SERVICE_URL
BI_DESIGNER_HELPER_SERVICE_URL
BOLD_ETL_SERVICE_URL
BOLD_AI_SERVICE_URL
REPORTS_WEB_SERVICE_URL
REPORTS_API_SERVICE_URL
REPORTS_JOBS_SERVICE_URL
REPORTS_DESIGNER_SERVICE_URL
REPORTS_VIEWER_SERVICE_URL
```

Application configuration is defined in:

```text
D:\dev-run-new\bold-upgrade-center\appsettings.json
```

Important sections:

```json
{
  "UpgradeCenter": {
    "ProductName": "Bold Upgrade Center",
    "ReleaseVersionsApiUrl": "https://releases.boldbi.com/version/json",
    "Services": {
      "IdpApi": "",
      "Ums": ""
    },
    "Authentication": {
      "LoginPath": "/accounts/login",
      "SessionValidationPath": "/upgrade-center/auth/session",
      "AdminValidationPath": "/upgrade-center/authorization/admin",
      "IdpPublicUrlOverride": ""
    }
  }
}
```

Purpose:

- Empty `Services` values allow the app to resolve service URLs from the dev-run environment.
- Authentication paths point to the UMS/IDP Upgrade Center validation endpoints.

## Fresh dev-run Integration Steps

Use these steps when integrating the application into a clean dev-run checkout.

1. Add the repository entry to:

```text
dev-run\v2\config\source-workspace.source-dev.json
```

2. Optionally add the repository entry to:

```text
dev-run\config\repo-manifest.json
```

3. Add the `bold-upgrade-center` service entry to:

```text
dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json
```

4. Add runtime environment wiring in:

```text
dev-run\v2\src\DevRun.Cli\RuntimeManager.cs
```

5. Add the `/upgrade-center` proxy route in:

```text
dev-run\src\repo-local-proxy\Program.cs
```

6. Pull/sync source repositories:

```powershell
.\dev-run\v2\scripts\devrun.ps1 env fresh --sync-source --profile source-dev --interactive-setup --service-map dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json --timeout-seconds 900
```

7. Publish the Upgrade Center service:

```powershell
.\dev-run\v2\scripts\devrun.ps1 publish bold-upgrade-center --service-map dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json
```

8. Start the required identity chain and the Upgrade Center service.

Start all services:

```powershell
.\dev-run\v2\scripts\devrun.ps1 start id-chain --group all --service-map dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json
```

Start only the Upgrade Center service:

```powershell
.\dev-run\v2\scripts\devrun.ps1 start bold-upgrade-center --service-map dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json
```

9. Open:

```text
https://localhost:6843/platform/upgrade-center
```

## Updating After New Changes Are Pushed to `main`

To pull the latest source and refresh the dev-run workspace:

```powershell
.\dev-run\v2\scripts\devrun.ps1 env fresh --sync-source --profile source-dev --interactive-setup --service-map dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json --timeout-seconds 900
```

To publish only the Upgrade Center after pulling:

```powershell
.\dev-run\v2\scripts\devrun.ps1 publish bold-upgrade-center --service-map dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json
```

To restart only the Upgrade Center:

```powershell
.\dev-run\v2\scripts\devrun.ps1 stop bold-upgrade-center --service-map dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json
.\dev-run\v2\scripts\devrun.ps1 start bold-upgrade-center --service-map dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json
```

## Validation

Validate repository sync:

```powershell
Test-Path .\bold-upgrade-center\Bold.UpgradeCenter.csproj
```

Validate build:

```powershell
dotnet build .\bold-upgrade-center\Bold.UpgradeCenter.csproj
```

Validate dev-run service publish:

```powershell
.\dev-run\v2\scripts\devrun.ps1 publish bold-upgrade-center --service-map dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json
```

Validate direct health:

```powershell
Invoke-WebRequest http://localhost:6717/upgrade-center/health-check
```

Validate proxy access:

```text
https://localhost:6843/platform/upgrade-center
```

Validate logs:

```text
D:\dev-run-new\dev-run\.runtime\proxy-traefik-https-source-dev\logs\bold-upgrade-center.log
D:\dev-run-new\dev-run\.runtime\proxy-traefik-https-source-dev\logs\bold-upgrade-center.stderr.log
```

## Common Issues

### Repository is not pulled

Symptom:

```text
The repository folder bold-upgrade-center is missing.
```

Check:

```text
dev-run\v2\config\source-workspace.source-dev.json
```

Confirm this entry exists:

```json
"name": "bold-upgrade-center"
```

Then rerun:

```powershell
.\dev-run\v2\scripts\devrun.ps1 env fresh --sync-source --profile source-dev --interactive-setup --service-map dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json --timeout-seconds 900
```

### Service is not found by dev-run

Symptom:

```text
Service 'bold-upgrade-center' was not found.
```

Check:

```text
dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json
```

Confirm this service id exists:

```json
"id": "bold-upgrade-center"
```

### `/upgrade-center` returns the IDP page instead of Upgrade Center

Cause:

- The local proxy route was not added.

Check:

```text
dev-run\src\repo-local-proxy\Program.cs
```

Confirm this route exists before the generic `/bi` and default IDP routes:

```csharp
if (path.Equals("/upgrade-center", StringComparison.OrdinalIgnoreCase) ||
    path.StartsWith("/upgrade-center/", StringComparison.OrdinalIgnoreCase))
{
    return (options.BoldBiUpgradeCenter, path);
}
```

### CSS or images do not load

Cause:

- Static files are not served under `/upgrade-center`, or path base/prefix handling is missing.

Check:

```text
bold-upgrade-center\Program.cs
```

Confirm:

```csharp
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/upgrade-center"
});
```

Also confirm forwarded prefix handling is enabled:

```csharp
app.Use(ApplyForwardedPrefix);
```

### Health check fails

Check the direct health endpoint first:

```text
http://localhost:6717/upgrade-center/health-check
```

Then check the logs:

```text
dev-run\.runtime\proxy-traefik-https-source-dev\logs\bold-upgrade-center.log
dev-run\.runtime\proxy-traefik-https-source-dev\logs\bold-upgrade-center.stderr.log
```

### Port 6717 is already in use

Stop the service:

```powershell
.\dev-run\v2\scripts\devrun.ps1 stop bold-upgrade-center --service-map dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json
```

If another process owns the port, stop that process before starting dev-run again.

### Authentication redirects loop

Check:

- `idp-web` is running.
- `ums` is running.
- `idp-api` is running.
- The proxy public URL is `https://localhost:6843/platform`.
- UMS has the Upgrade Center auth/session endpoints available:

```text
/upgrade-center/auth/session
/upgrade-center/authorization/admin
```

### Package restore fails

Run restore manually:

```powershell
dotnet restore .\bold-upgrade-center\Bold.UpgradeCenter.csproj
```

If NuGet access is blocked, ensure network access to required package feeds is available.

## Summary

The dev-run integration has three responsibilities:

1. Pull the source from:

```text
https://gitea.syncfusion.com/bold-bi/bold-upgrade-center
```

2. Publish and run it as:

```text
bold-upgrade-center
```

3. Expose it at:

```text
https://localhost:6843/platform/upgrade-center
```

The old initial application route `/upgrader` and earlier temporary service ids are obsolete and should not be reintroduced.
