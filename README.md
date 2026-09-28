# Bold Upgrade Center

Bold Upgrade Center is an ASP.NET Core MVC application used to orchestrate Bold BI upgrades in Kubernetes environments. It integrates with the running Bold BI IDP, UMS, BI, database, and Kubernetes deployments so an administrator can review available versions, validate impact, back up affected schema objects, update deployment images, monitor progress, view history, and run rollback when required.

The application is designed to run beside an existing Bold BI installation, usually in the same Kubernetes namespace. It does not read Bold BI `app_data` files directly. Required configuration and cryptographic metadata are retrieved through internal IDP APIs.

## Project Overview

The Upgrade Center provides:

- Admin-only access through Bold BI UMS session validation.
- Runtime configuration discovery from the IDP Configuration API.
- Current Bold BI version discovery from the Bold BI Product Version API.
- Version catalog retrieval from the Bold BI Release Version Provider API.
- Standard release upgrade confirmation with database schema impact analysis.
- Custom patch upgrade by generating target image references from current Kubernetes deployment images.
- Pre-upgrade and post-upgrade Playwright validation.
- Master and tenant database discovery.
- Affected-table backup and restore for PostgreSQL, MySQL, Microsoft SQL Server, and Oracle.
- Kubernetes deployment image update with rollout health validation.
- Upgrade and rollback monitoring with operation-level progress.
- Persistent upgrade history and operational context in the Bold BI master database.

## High-Level Architecture

```text
Browser
  |
  v
Bold.UpgradeCenter MVC app
  |
  +-- UMS internal service
  |     - Session validation
  |     - Admin validation
  |     - User display name/email
  |
  +-- IDP API internal service
  |     - GET /configuration
  |     - GET /configuration/private-key
  |
  +-- Bold BI public/internal URL
  |     - GET /bi/api/product-version
  |
  +-- Release Version Provider API
  |     - GET /version/json
  |     - GET /version/json/{version}/database/{databaseType}
  |     - GET /version/json/{version}/k8s
  |
  +-- Master database
  |     - boldtc_tenantinfo
  |     - bolduc_upgradeoperationhistory
  |     - bolduc_upgradeoperationcontext
  |
  +-- Kubernetes API
        - List/get/patch/update deployments
        - Rollout health checks
```

## Main Workflow

### Standard Release Upgrade

1. Administrator opens `/upgrade-center`.
2. `RemoteUmsAuthenticationHandler` validates the Bold BI session through UMS.
3. `ProductVersionInstallationInfoProvider` gets the currently installed version from `ProductVersionProvider`.
4. `ProductVersionProvider` calls `{IdpBaseUrl}/bi/api/product-version` first and falls back to the IDP configuration version if needed.
5. `BoldBiReleaseVersionProvider` retrieves available versions from `UpgradeCenter:ReleaseVersionsApiUrl`.
6. Administrator selects a target version and opens confirmation.
7. `UpgradeImpactService` discovers master and tenant database types, calls `UpgradeDatabaseScriptImpactService`, and displays schema impact and warnings.
8. Administrator confirms the upgrade.
9. `UpgradeWorkflowService` creates an upgrade job and stores history.
10. `UpgradeJobRunner` executes:
    - Pre-upgrade Playwright validation.
    - Database discovery, script impact analysis, and affected-table backup.
    - Kubernetes image upgrade with rollout health validation.
    - Post-upgrade Playwright validation.
    - Automatic rollback when required.
11. Monitoring polls `/upgrade-center/monitor/{jobId}/status`.
12. History is written to `bolduc_upgradeoperationhistory`.
13. Operational job/rollback context is persisted to `bolduc_upgradeoperationcontext`.

### Custom Patch Upgrade

The custom patch flow is intended for support-provided image tags.

1. Administrator enters a custom patch version/image tag, for example `16.1.88`.
2. Optionally, administrator enters an image repository, for example:

   ```text
   us-central1-docker.pkg.dev/boldbi-dev-296107/boldbi
   ```

3. If repository is empty, `CustomPatchValidationService` reads the current Kubernetes deployment image for each Bold BI deployment, keeps the repository and image name, and replaces only the tag.
4. If repository is provided, the service keeps the current image name and combines it with the provided repository and tag.
5. `ContainerImageRegistryValidator` validates generated image manifests.
6. The UI enables "Upgrade to this patch" only if every required generated image validates successfully.
7. The job is created as `UpgradeJobType.CustomVersionOrPatch`.
8. Database script lookup and table backup are skipped for custom patch image upgrades.
9. Kubernetes image update and validation use the generated `CustomPatchImageMapping` list.

## Folder Structure

```text
Bold.UpgradeCenter
├── Controllers
│   ├── UpgradeCenterController.cs
│   └── HomeController.cs
├── Models
│   ├── UpgradeModels.cs
│   └── ViewModels
├── Security
│   ├── RemoteUmsAuthenticationHandler.cs
│   ├── AdminContinuationStore.cs
│   └── UpgradeCenterPolicies.cs
├── Services
│   ├── UpgradeWorkflowService.cs
│   ├── UpgradeJobRunner.cs
│   ├── UpgradeRollbackJobRunner.cs
│   ├── UpgradeDatabaseDiscoveryService.cs
│   ├── UpgradeDatabaseScriptImpactService.cs
│   ├── UpgradeDatabaseBackupService.cs
│   ├── KubernetesUpgradeService.cs
│   ├── PlaywrightScriptRunner.cs
│   ├── ProductVersionProvider.cs
│   ├── IdpConfigurationProvider.cs
│   ├── IdpPrivateKeyProvider.cs
│   ├── DatabaseUpgradeHistoryStore.cs
│   └── DatabaseUpgradeOperationalStateStore.cs
├── Views
│   ├── UpgradeCenter
│   └── Shared
├── wwwroot
│   ├── css
│   ├── images
│   └── js
├── k8s
├── Dockerfile
├── appsettings.json
└── Bold.UpgradeCenter.csproj
```

### Key Components

| Component | Purpose |
| --- | --- |
| `UpgradeCenterController` | MVC routes for dashboard, confirmation, start, monitor, rollback, logout, and patch validation. |
| `RemoteUmsAuthenticationHandler` | Validates browser cookies through UMS and creates authenticated/admin claims. |
| `IdpConfigurationProvider` | Retrieves runtime configuration from the IDP Configuration API and caches last-known-good values. |
| `ProductVersionProvider` | Gets current Bold BI version from `{IdpBaseUrl}/bi/api/product-version`, with configuration fallback. |
| `BoldBiReleaseVersionProvider` | Retrieves and filters release versions from the Version Provider API. |
| `UpgradeImpactService` | Builds confirmation-page schema-impact summary. |
| `UpgradeDatabaseDiscoveryService` | Decrypts master DB connection, reads `boldtc_tenantinfo`, decrypts tenant DB connections, and deduplicates databases. |
| `UpgradeDatabaseScriptImpactService` | Retrieves version-specific database scripts and extracts affected tables. |
| `UpgradeDatabaseBackupService` | Validates databases, backs up affected tables, restores backups, and cleans backups. |
| `KubernetesUpgradeService` | Discovers deployments, maps images, patches deployments, and waits for rollout health. |
| `UpgradeJobRunner` | Executes the standard/custom upgrade workflow. |
| `UpgradeRollbackJobRunner` | Executes manual rollback. |
| `DatabaseUpgradeHistoryStore` | Persists customer-facing upgrade history in the master database. |
| `DatabaseUpgradeOperationalStateStore` | Persists job and rollback context in the master database. |

## Prerequisites

### Development Machine

- .NET SDK matching `net10.0`.
- Access to the Bold BI dev-run environment or running Bold BI services.
- No local Node.js/npm setup is required for the Upgrade Center web application. Real Playwright validation runs in the separate Kubernetes runner image when enabled; when disabled, validation uses product health checks.
- Access to the Bold BI master database.
- For Kubernetes upgrade testing:
  - Current Kubernetes context or in-cluster service account access.
  - RBAC permission to list/get/patch/update deployments.

### Runtime Dependencies

- `id-api` internal service.
- `id-ums` internal service.
- Bold BI BI product-version endpoint.
- Master database and tenant database access.
- Kubernetes API access from the Upgrade Center pod.
- Version Provider API access.
- Container registry access for custom patch image validation.

## Configuration

The primary configuration section is `UpgradeCenter` in `appsettings.json`.

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

### Required Service URLs

| Setting | Environment variable | Purpose |
| --- | --- | --- |
| `UpgradeCenter:Services:IdpApi` | `UpgradeCenter__Services__IdpApi` | Internal IDP API base URL. Example: `http://id-api-service:6001/api`. |
| `UpgradeCenter:Services:Ums` | `UpgradeCenter__Services__Ums` | Internal UMS base URL. Example: `http://id-ums-service:6002/ums`. |
| `UpgradeCenter:ReleaseVersionsApiUrl` | `UpgradeCenter__ReleaseVersionsApiUrl` | Version Provider API URL. |

Fallback environment variable names also supported:

- `UPGRADE_CENTER_IDP_API_INTERNAL_URL`
- `IDP_API_INTERNAL_URL`
- `ID_API_SERVICE_URL`
- `UPGRADE_CENTER_UMS_INTERNAL_URL`
- `ID_UMS_SERVICE_URL`
- `UPGRADE_CENTER_IDP_PUBLIC_URL`
- `BOLD_UPGRADE_CENTER_IDP_URL`

### IDP Configuration API Values

`IdpConfigurationProvider` calls:

```text
GET {IdpApi}/configuration
```

The response is expected to provide:

- `InternalAppUrls.Idp`
- `InternalAppUrls.Bi`
- `BoldProducts` / Bold BI version fallback
- `SqlConfiguration.ConnectionString`
- `SqlConfiguration.ServerType`
- `MachineKey.DecryptionKey`
- `InternalAppClientId`
- `InternalAppClientSecret`

The Upgrade Center uses these values for:

- Browser login/return URL.
- Back to Bold BI link.
- Product version endpoint resolution.
- Master database connection decryption.
- Future service-to-service automation scenarios.

### IDP Private Key API

`IdpPrivateKeyProvider` calls:

```text
GET {IdpApi}/configuration/private-key
```

It expects a response containing:

```json
{
  "data": {
    "encryptedPrivateKey": "..."
  }
}
```

This is required to decrypt the master database connection string and tenant database connection strings. Private-key data, machine keys, and decrypted connection strings must never be logged.

### Authentication Settings

| Setting | Default | Purpose |
| --- | --- | --- |
| `UpgradeCenter:Authentication:LoginPath` | `/accounts/login` | IDP login path used for challenge redirects. |
| `UpgradeCenter:Authentication:SessionValidationPath` | `/upgrade-center/auth/session` | UMS session/admin validation endpoint. |
| `UpgradeCenter:Authentication:AdminValidationPath` | `/upgrade-center/authorization/admin` | Backward-compatible admin validation fallback. |
| `UpgradeCenter:Authentication:IdpPublicUrlOverride` | empty | Optional fallback public IDP URL if IDP config cannot provide it. |

### Playwright Settings

Configured through `UpgradeCenter:Playwright`.

When `UseKubernetesJob` is `true`, pre-upgrade and post-upgrade validation run through the external Playwright runner Kubernetes Job.

When `UseKubernetesJob` is `false`, the Upgrade Center does not execute bundled sample scripts. It validates product availability through `ProductHealthCheckService`.

### Database Backup Settings

Configured through `UpgradeCenter:DatabaseBackup`.

| Setting | Default | Purpose |
| --- | --- | --- |
| `BatchSize` | `1000` | Number of rows copied per batch. |
| `DropAndRecreate` | `true` | Recreate target backup objects. |
| `DisableForeignKeysDuringLoad` | `true` | Temporarily disable FK checks where supported during restore/load. |

### Kubernetes Settings

Configured through `UpgradeCenter:Kubernetes`.

| Setting | Default | Purpose |
| --- | --- | --- |
| `Enabled` | `true` | Enables Kubernetes image update. |
| `Namespace` | empty | Optional namespace override. |
| `ApiServer` | empty | Optional Kubernetes API override. |
| `ServiceAccountTokenPath` | `/var/run/secrets/kubernetes.io/serviceaccount/token` | In-cluster token path. |
| `NamespacePath` | `/var/run/secrets/kubernetes.io/serviceaccount/namespace` | In-cluster namespace file. |
| `CertificateAuthorityPath` | `/var/run/secrets/kubernetes.io/serviceaccount/ca.crt` | In-cluster CA file. |
| `SkipTlsVerify` | `false` | Kubernetes API TLS behavior. |
| `RolloutTimeoutSeconds` | `300` | Per-deployment rollout timeout. |
| `RolloutPollSeconds` | `5` | Rollout polling interval. |
| `ExpectedDeployments` | built-in list | Bold BI deployment names. |
| `OptionalDeployments` | built-in list | Optional deployments. |
| `AllowedImageRepositories` | empty | Optional custom patch image repository allow-list. |

Default rollout groups:

```text
Group 1: id-web-deployment, id-api-deployment, id-ums-deployment
Group 2: bi-web-deployment, bi-api-deployment, bi-jobs-deployment
Group 3: bi-dataservice-deployment, bold-ai-deployment, bold-etl-deployment
```

Deployments inside each group are patched and monitored in parallel. The next group starts only after the current group succeeds.

## Build and Run

### Restore and Build

```powershell
cd D:\dev-run-new\Bold.UpgradeCenter
dotnet restore
dotnet build
```

### Run Locally

```powershell
cd D:\dev-run-new\Bold.UpgradeCenter
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:6717"
$env:UpgradeCenter__Services__IdpApi = "http://localhost:6702/api"
$env:UpgradeCenter__Services__Ums = "http://localhost:6701/ums"
dotnet run
```

Open:

```text
http://localhost:6717/upgrade-center
```

When running through dev-run proxy, open:

```text
https://localhost:6843/platform/upgrade-center
```

The application supports forwarded prefixes through `X-Forwarded-Prefix`.

## Dev-Run Integration

The dev-run service map currently contains service ID:

```text
bold-upgrade-center
```

Relevant service map path:

```text
dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json
```

The service map entry uses:

```text
repository: Bold.UpgradeCenter
projectPath: Bold.UpgradeCenter.csproj
runtimeMode: publish
http port: 6717
health: http://localhost:6717/upgrade-center/health-check
public route: https://localhost:6843/platform/upgrade-center
```

Typical commands:

```powershell
.\dev-run\v2\scripts\devrun.ps1 publish bold-upgrade-center --service-map dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json
.\dev-run\v2\scripts\devrun.ps1 restart bold-upgrade-center --service-map dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json
```

To start all configured services:

```powershell
.\dev-run\v2\scripts\devrun.ps1 start all --service-map dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json
```

To stop all configured services:

```powershell
.\dev-run\v2\scripts\devrun.ps1 stop all --service-map dev-run\v2\config\service-map.proxy-traefik-https.source-dev.json
```

Check logs:

```text
.runtime/proxy-traefik-https-source-dev/logs/bold-upgrade-center.log
```

## Docker

The Dockerfile uses:

- .NET SDK `10.0-noble` for build.
- ASP.NET Core runtime `10.0-noble`.
- Release publish.
- Non-root user `appuser`.
- Port `8080`.

Build:

```powershell
cd D:\dev-run-new
docker build -t sivakumar2809/bold-upgrade-center:v2.0.0 .\Bold.UpgradeCenter
```

Push:

```powershell
docker push sivakumar2809/bold-upgrade-center:v2.0.0
```

## Kubernetes Deployment

Manifest:

```text
k8s/bold-upgrade-center.yaml
```

It creates:

- `ServiceAccount`
- namespace-scoped `Role`
- `RoleBinding`
- `ConfigMap`
- `Deployment`
- `Service`

Apply:

```powershell
kubectl apply -f .\Bold.UpgradeCenter\k8s\bold-upgrade-center.yaml
```

Check rollout:

```powershell
kubectl rollout status deployment/bold-upgrade-center
kubectl get pods -l app.kubernetes.io/name=bold-upgrade-center
kubectl logs deployment/bold-upgrade-center
```

Health:

```powershell
kubectl port-forward service/bold-upgrade-center 8080:80
curl http://localhost:8080/upgrade-center/health-check
```

### IngressRoute

Template:

```text
k8s/ingressroute-upgrade-center.yaml
```

The intended public path is:

```text
/upgrade-center
```

When integrating with the existing Bold BI Traefik IngressRoute, add the `PathPrefix(`/upgrade-center`)` route before catch-all routes.

## Bold BI IDP and UMS Dependencies

The Upgrade Center depends on IDP/UMS source/application changes that expose internal endpoints:

### IDP API

Required:

```text
GET {IdpApi}/configuration
GET {IdpApi}/configuration/private-key
```

The private-key endpoint must:

- Be intended for internal service-to-service use.
- Return only encrypted private-key content required for decryption.
- Not expose file paths.
- Not log private-key values.

### UMS

Required:

```text
GET {Ums}/upgrade-center/auth/session
```

Expected response model:

```json
{
  "isAuthenticated": true,
  "isAdmin": true,
  "userId": "...",
  "userName": "admin",
  "email": "admin@boldbi.com",
  "displayName": "admin"
}
```

Fallback endpoint:

```text
GET {Ums}/upgrade-center/authorization/admin
```

The Upgrade Center forwards the browser cookie header to UMS for validation.

## Authentication Flow

1. Browser requests `/upgrade-center`.
2. ASP.NET authorization policy `UpgradeCenterPolicies.AdminOnly` is applied.
3. `RemoteUmsAuthenticationHandler` reads browser cookies.
4. It calls UMS session validation with the cookie header.
5. If UMS returns authenticated admin details, the handler creates claims:
   - `ClaimTypes.NameIdentifier`
   - `ClaimTypes.Name`
   - `upgrade_center:is_admin=true`
   - optional email/role claims
6. If the user is not authenticated, the handler challenges and redirects to IDP login.
7. If authenticated but not admin, the user is redirected to `/upgrade-center/unauthorized`.
8. If UMS is temporarily unavailable and the same pod previously validated the same admin cookie fingerprint, pod-lifetime admin continuation can allow monitoring/rollback access.

The continuation cache is in memory only. It is cleared when the Upgrade Center pod restarts.

## Database Discovery and Backup

Database discovery starts from IDP configuration:

1. `IdpConfigurationProvider` reads encrypted master DB connection string, server type, and machine key.
2. `IdpPrivateKeyProvider` gets encrypted private-key content.
3. `BoldConnectionStringDecryptor` decrypts the master DB connection string.
4. `UpgradeDatabaseDiscoveryService` connects to the master DB.
5. It looks for `boldtc_tenantinfo`.
6. It reads tenant `connectionstring` and `databasetype`.
7. Tenant connection strings are decrypted.
8. Duplicate database connections are deduplicated.

Supported database types:

- PostgreSQL
- MySQL
- Microsoft SQL Server
- Oracle

`MSSQLCE` is explicitly unsupported.

## Schema Impact Analysis

`UpgradeDatabaseScriptImpactService` uses the Version Provider API.

For standard upgrades, it:

1. Normalizes current and target versions.
2. Retrieves the available version list.
3. Determines all versions where:

   ```text
   currentVersion < version <= targetVersion
   ```

4. Calls database-specific script endpoints only for discovered database types:

   ```text
   {ReleaseVersionsApiUrl}/{version}/database/postgresql
   {ReleaseVersionsApiUrl}/{version}/database/mysql
   {ReleaseVersionsApiUrl}/{version}/database/mssql
   {ReleaseVersionsApiUrl}/{version}/database/oracle
   ```

5. Parses SQL scripts with regex-based table extraction.
6. Builds a cumulative, deduplicated affected-table list.
7. Fails safely if a required script cannot be retrieved or a script reports changes but affected tables cannot be identified.

Currently detected operations include:

- `ALTER TABLE`
- `UPDATE`
- `INSERT INTO`
- `DELETE FROM`
- `MERGE INTO`
- `CREATE INDEX`
- `DROP TABLE`
- `CREATE TABLE`
- `TRUNCATE`
- `REINDEX TABLE`

Dynamic SQL or unsupported script patterns may be treated as inconclusive and should stop the upgrade.

## Kubernetes Upgrade

`KubernetesUpgradeService`:

1. Resolves namespace from config, `POD_NAMESPACE`, or service account namespace file.
2. Lists deployments in the current namespace.
3. Validates expected Bold BI deployments.
4. Retrieves target images from:

   ```text
   {ReleaseVersionsApiUrl}/{selectedVersion}/k8s
   ```

5. Records previous deployment image for rollback.
6. Persists rollback context before patching images.
7. Patches deployment images through Kubernetes API.
8. Waits for rollout health.

Rollout health checks include:

- `observedGeneration`
- `updatedReplicas`
- `readyReplicas`
- `availableReplicas`
- `Progressing` condition
- `Available` condition
- timeout/progress-deadline handling

## Rollback

Rollback uses the latest valid rollback point only.

Rollback restores:

- Database backups created before upgrade.
- Previous Kubernetes deployment images recorded before patching.

Manual rollback flow:

1. User clicks rollback for the most recent rollbackable upgrade.
2. `UpgradeWorkflowService.StartRollbackAsync` validates that the rollback point is available.
3. `UpgradeRollbackJobRunner` restores database tables.
4. It reverts Kubernetes image tags and waits for rollout health.
5. It verifies health from rollout results.
6. It marks the rollback point consumed and cleans backup databases where safe.

Automatic rollback is triggered when Kubernetes image upgrade fails after rollback context exists, or when post-upgrade validation fails.

## Persistence

Upgrade Center creates and uses two tables in the Bold BI master database:

```text
bolduc_upgradeoperationhistory
bolduc_upgradeoperationcontext
```

These tables are created automatically if missing.

Do not store or expose:

- Plain connection strings.
- Passwords.
- Private keys.
- Machine keys.
- Authentication cookies.
- Tokens.

## Debugging

### Recommended Local Debugging Mode: Use Real IDP/UMS

This is the safest and most accurate mode. Keep IDP, UMS, BI, and database services running through dev-run, then run only `Bold.UpgradeCenter` from VS Code.

1. Start required Bold BI services.
2. Stop only the dev-run `bold-upgrade-center` service if it is already using port `6717`.
3. Open VS Code at:

   ```text
   D:\dev-run-new\Bold.UpgradeCenter
   ```

4. Use a C# launch configuration like:

   ```json
   {
     "name": "Debug Bold Upgrade Center",
     "type": "coreclr",
     "request": "launch",
     "program": "${workspaceFolder}/bin/Debug/net10.0/Bold.UpgradeCenter.dll",
     "args": [],
     "cwd": "${workspaceFolder}",
     "stopAtEntry": false,
     "preLaunchTask": "build",
     "env": {
       "ASPNETCORE_ENVIRONMENT": "Development",
       "ASPNETCORE_URLS": "http://localhost:6717",
       "UpgradeCenter__Services__IdpApi": "http://localhost:6702/api",
       "UpgradeCenter__Services__Ums": "http://localhost:6701/ums"
     }
   }
   ```

5. Browse:

   ```text
   http://localhost:6717/upgrade-center
   ```

6. If you need the Bold BI public login cookies and path behavior, browse through the proxy:

   ```text
   https://localhost:6843/platform/upgrade-center
   ```

7. Put breakpoints in:

   - `RemoteUmsAuthenticationHandler.HandleAuthenticateAsync`
   - `UpgradeCenterController.Index`
   - `UpgradeCenterController.Confirm`
   - `UpgradeCenterController.StartUpgrade`
   - `UpgradeWorkflowService.StartUpgradeAsync`
   - `UpgradeJobRunner.RunAsync`
   - `UpgradeDatabaseDiscoveryService.DiscoverDatabasesAsync`
   - `UpgradeDatabaseScriptImpactService.AnalyzeAsync`
   - `UpgradeDatabaseBackupService.BackupAffectedTablesAsync`
   - `KubernetesUpgradeService.UpgradeAsync`
   - `UpgradeRollbackJobRunner.RunAsync`

### Required Local URLs

Typical dev-run URLs:

```text
IDP public URL: https://localhost:6843/platform
IDP API internal URL: http://localhost:6702/api
UMS internal URL: http://localhost:6701/ums
Upgrade Center direct URL: http://localhost:6717/upgrade-center
Upgrade Center proxy URL: https://localhost:6843/platform/upgrade-center
```

### Debugging Authentication

The controller is protected by:

```csharp
[Authorize(Policy = UpgradeCenterPolicies.AdminOnly)]
```

The middleware pipeline includes:

```csharp
app.UseAuthentication();
app.UseAuthorization();
```

The real authentication handler is:

```csharp
RemoteUmsAuthenticationHandler
```

Do not remove these for normal development. They are part of the real integration.

### Temporary Standalone UI Debugging Bypass

Use this only when you need to inspect MVC/UI rendering without running IDP/UMS. Do not commit this change.

Temporary approach:

1. Comment out or remove the controller-level authorization attribute:

   ```csharp
   // [Authorize(Policy = UpgradeCenterPolicies.AdminOnly)]
   ```

2. In `UpgradeCenterController.Index`, replace the real user call with a temporary user:

   ```csharp
   var user = new UpgradeCenterUser
   {
       IsAuthenticated = true,
       IsAuthorized = true,
       DisplayName = "Local Debug Admin",
       Initials = "L"
   };
   ```

3. Avoid starting real upgrade/rollback operations in this mode, because database discovery, IDP private-key retrieval, UMS validation, and Kubernetes operations still require real dependencies.

4. Restore the original code immediately after UI debugging:

   - Re-enable `[Authorize(Policy = UpgradeCenterPolicies.AdminOnly)]`.
   - Restore `_authService.GetCurrentUserAsync(User)`.
   - Verify `app.UseAuthentication()` and `app.UseAuthorization()` remain enabled.

Preferred alternative: keep the real auth path and use dev-run IDP/UMS. That catches integration issues earlier.

### Debugging Common Flows

#### Login/authentication

Breakpoints:

- `RemoteUmsAuthenticationHandler.HandleAuthenticateAsync`
- `ValidateSessionAsync`
- `HandleChallengeAsync`
- `ClaimsUpgradeCenterAuthorizationService.GetCurrentUserAsync`

Check:

- Browser cookies are present.
- `UpgradeCenter__Services__Ums` points to UMS base URL.
- UMS returns authenticated/admin response.
- `upgrade_center:is_admin` claim is created.

#### Configuration

Breakpoints:

- `IdpConfigurationProvider.GetConfigurationAsync`
- `IdpConfigurationProvider.LoadConfigurationAsync`
- `IdpPrivateKeyProvider.GetEncryptedPrivateKeyAsync`

Check:

- `UpgradeCenter__Services__IdpApi` is configured.
- `/configuration` returns `InternalAppUrls`, `SqlConfiguration`, `MachineKey`.
- `/configuration/private-key` returns `encryptedPrivateKey`.

#### Upgrade confirmation

Breakpoints:

- `UpgradeCenterController.Confirm`
- `UpgradeImpactService.GetImpactAsync`
- `UpgradeDatabaseDiscoveryService.DiscoverDatabasesAsync`
- `UpgradeDatabaseScriptImpactService.AnalyzeAsync`

Check:

- Current version is valid.
- Target version is found in release API.
- Required database types are discovered.
- Script endpoints return expected JSON.

#### Upgrade execution

Breakpoints:

- `UpgradeCenterController.StartUpgrade`
- `UpgradeWorkflowService.StartUpgradeAsync`
- `UpgradeJobRunner.RunAsync`
- `PlaywrightScriptRunner.ExecutePreUpgradeAsync`
- `UpgradeDatabaseBackupService.BackupAffectedTablesAsync`
- `KubernetesUpgradeService.UpgradeAsync`
- `PlaywrightScriptRunner.ExecutePostUpgradeAsync`

#### Rollback

Breakpoints:

- `UpgradeCenterController.Rollback`
- `UpgradeWorkflowService.StartRollbackAsync`
- `UpgradeRollbackJobRunner.RunAsync`
- `UpgradeDatabaseBackupService.RestoreAffectedTablesAsync`
- `KubernetesUpgradeService.RollbackAsync`

## Troubleshooting

### Access denied

Likely causes:

- UMS session validation says the user is not admin.
- Browser cookie is missing or invalid.
- UMS URL is wrong.

Check:

```text
UpgradeCenter__Services__Ums
```

Break in:

```text
RemoteUmsAuthenticationHandler.HandleAuthenticateAsync
```

### Redirect loops or wrong return URL

Check:

- `InternalAppUrls.Idp` from IDP Configuration API.
- Reverse proxy `X-Forwarded-Prefix`.
- IngressRoute path prefix.
- `UpgradeCenter:Authentication:LoginPath`.

### Static CSS not loading

The layout references:

```html
~/upgrade-center/css/upgrade-center.css
```

The app serves static files with:

```csharp
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/upgrade-center"
});
```

Check that the browser can load:

```text
/upgrade-center/css/upgrade-center.css
```

When using a proxy prefix, ensure forwarded prefix headers are passed correctly.

### Product version is stale briefly after upgrade or rollback

Current version comes from:

```text
{IdpBaseUrl}/bi/api/product-version
```

During Kubernetes rollout or rollback, the service may temporarily route to older/newer BI pods until endpoints settle. If the Product Version API is unavailable, the provider falls back to cached IDP configuration.

### Database backup fails: private key unavailable

Check:

```text
GET {IdpApi}/configuration/private-key
```

The response must include encrypted private-key content. The Upgrade Center does not read `app_data` directly.

### `boldtc_tenantinfo` not found

The service searches for `boldtc_tenantinfo` case-insensitively where possible. If not found, it continues with master database only and logs a warning.

### Schema impact analysis fails

Common causes:

- Target version is not in the release list.
- Database script endpoint is unavailable.
- Script reports changes but affected tables cannot be identified.
- Unsupported database type.

Check endpoints:

```text
{ReleaseVersionsApiUrl}/{version}/database/mysql
{ReleaseVersionsApiUrl}/{version}/database/mssql
{ReleaseVersionsApiUrl}/{version}/database/postgresql
{ReleaseVersionsApiUrl}/{version}/database/oracle
```

### Kubernetes namespace validation fails

Check:

```powershell
kubectl auth can-i list deployments
kubectl auth can-i get deployments
kubectl auth can-i patch deployments
kubectl get deploy
```

The Upgrade Center pod must run in the Bold BI namespace or have `UpgradeCenter:Kubernetes:Namespace` configured.

### Rollout timeout

Default timeout:

```text
UpgradeCenter:Kubernetes:RolloutTimeoutSeconds = 300
```

Check deployment events:

```powershell
kubectl describe deployment <deployment-name>
kubectl get pods
kubectl get rs
```

### Custom patch validation fails

Common causes:

- Invalid image tag format.
- Kubernetes deployments cannot be discovered.
- Current deployment image is missing or not tag-based.
- Registry denies manifest access.
- Image tag does not exist.
- Repository violates configured `AllowedImageRepositories`.

### Playwright fails

Check:

- `UpgradeCenter:Playwright:UseKubernetesJob` is enabled when real QA validation is expected.
- `UpgradeCenter:Playwright:RunnerImage` points to the correct Playwright runner image.
- The Upgrade Center service account has permission to create/read/delete Jobs, Pods, Pod logs, Secrets, and PVCs where configured.
- Required Playwright runtime secrets are present.
- If `UseKubernetesJob` is disabled, check the product health-check endpoints configured through `ProductHealthCheckService`.

## Security Notes

- Do not log full connection strings.
- Do not log database passwords.
- Do not log private-key data.
- Do not log `MachineKey` values.
- Do not persist browser cookies or access tokens.
- Rollback context persists database backup mappings and Kubernetes image snapshots, but sensitive values are sanitized where persisted.
- Authentication bypass code must never be committed or deployed.

## Maintenance Notes

- Keep the Upgrade Center route stable at `/upgrade-center`.
- Keep IDP/UMS internal APIs backward compatible.
- Keep Kubernetes RBAC namespace-scoped.
- Keep Playwright sample scripts replaceable by real QA scripts.
- Keep custom patch validation conservative: if a required image cannot be validated, do not enable upgrade.
- If new database script syntax is introduced, review `UpgradeDatabaseScriptImpactService` affected-table extraction.

