# Bold Upgrade Center Tech Stack

## Application Runtime

- Platform: ASP.NET Core MVC
- Language: C#
- Runtime target: .NET containerized web application
- UI rendering: Razor views with static CSS and JavaScript
- Base path: `/upgrade-center`
- Container role: web UI, orchestration API, background job coordinator, monitoring endpoint, report viewer

The web container does not run Playwright directly. Real Playwright validation runs in a separate Kubernetes runner image.

## Core Frameworks and Libraries

- ASP.NET Core MVC for routing, controllers, views, model binding, authentication middleware, and dependency injection.
- Microsoft.Extensions configuration, options, logging, and hosted application patterns.
- System.Text.Json for API payload parsing and persisted operation context serialization.
- ADO.NET provider APIs for database discovery, backup, restore, persistence, and history.

## Database Providers

The Upgrade Center supports database backup, restore, and state persistence through provider-specific clients:

- Microsoft SQL Server: `Microsoft.Data.SqlClient`
- MySQL: `MySqlConnector`
- PostgreSQL: `Npgsql`
- Oracle: `Oracle.ManagedDataAccess.Core`

Supported operational database work includes:

- Master database discovery.
- Active tenant database discovery.
- Table existence checks.
- Affected table backup.
- Created-table rollback cleanup where metadata is available.
- Restore of backed-up tables.
- Upgrade history, operation context, rollback context, and log persistence.

Sensitive connection details must be resolved at runtime and must not be written to logs, history, URLs, or report output.

## Kubernetes Integration

The application talks to the Kubernetes API using the in-cluster ServiceAccount token and namespace context.

Kubernetes resources used:

- Deployments
- Jobs
- Pods
- Pod logs
- Secrets
- PersistentVolumeClaims when Playwright shared state is enabled

Kubernetes permissions must remain namespace-scoped and limited to the resources needed for:

- Discovering deployments.
- Patching deployment images.
- Monitoring rollouts.
- Creating and monitoring Playwright validation jobs.
- Creating temporary Secrets.
- Creating shared state PVCs when enabled.
- Cleaning temporary resources.

## Release and Version Metadata

Bold BI release metadata is retrieved from the configured release API:

- `UpgradeCenter:ReleaseVersionsApiUrl`

The version provider is responsible for:

- Fetching available versions.
- Reading release date and release notes metadata.
- Filtering versions newer than the installed version.
- Supplying database script metadata URLs.
- Supplying Kubernetes image metadata for selected releases.

Version comparison must use version-aware ordering.

## Authentication and Authorization

Authentication is integrated with Bold ID/UMS.

Key responsibilities:

- Validate the user session through UMS.
- Validate administrator authorization before privileged actions.
- Redirect unauthenticated users to the configured login path.
- Allow limited pod-lifecycle admin continuation during temporary UMS unavailability after a successful admin validation.

Privileged operations include:

- Starting upgrades.
- Validating custom patch images.
- Cancelling upgrades.
- Starting manual rollback.
- Viewing operation reports and details.

## Playwright Validation Stack

When enabled, Playwright validation runs in a separate Kubernetes Job.

Runner requirements:

- Node.js runtime
- npm/npx
- Playwright
- Chromium and required browser dependencies
- Bold BI automation test source
- Runtime entrypoint that writes only supported configuration files

Runtime values are passed through Kubernetes Secrets. The Upgrade Center configures the runner through:

- `UpgradeCenter:Playwright:RunnerImage`
- `UpgradeCenter:Playwright:JobTimeoutSeconds`
- `UpgradeCenter:Playwright:WorkerCount`
- `UpgradeCenter:Playwright:JobCpuRequest`
- `UpgradeCenter:Playwright:JobCpuLimit`
- `UpgradeCenter:Playwright:JobMemoryRequest`
- `UpgradeCenter:Playwright:JobMemoryLimit`
- `UpgradeCenter:Playwright:PassRateThreshold`
- `UpgradeCenter:Playwright:ReportUploadBaseUrl`
- `UpgradeCenter:Playwright:ResultUploadBaseUrl`
- Shared state PVC settings when required

The runner uploads:

- Structured result summary.
- Self-contained HTML report.

The Upgrade Center uses structured results for stage status and the HTML report for detailed customer review.

## Product Health Checks

When Playwright Kubernetes Jobs are disabled, validation uses product-level health checks instead of local sample scripts.

Health checks are also used during rollback health verification after Kubernetes rollout succeeds.

Bold BI health targets include:

- Identity Provider Web
- Identity Provider API
- User Management Web
- Dashboard Server Web
- Dashboard Server API
- Dashboard Server Jobs
- Dashboard Designer Service
- ETL Service

## Storage and Retention

Persistent operational data:

- Upgrade history.
- Job state.
- Rollback context.
- Backup mappings.
- Kubernetes image mappings.
- Operation logs.

Local or mounted report storage:

- Playwright HTML reports under `App_Data/PlaywrightReports`.
- Reports are separated by upgrade job ID and validation stage.
- Report retention keeps the latest configured number of jobs.

Data protection keys:

- Stored under `App_Data/DataProtectionKeys`.
- In Kubernetes, report storage may be mounted separately so data protection key directory permissions are not affected by report PVCs.

## Deployment Artifacts

Main artifacts:

- Application Dockerfile.
- Kubernetes Deployment, Service, ServiceAccount, Role, RoleBinding, ConfigMap, Secret template, PVC, and IngressRoute manifests.
- Playwright runner Docker image built separately from the automation test project.

The Upgrade Center web image should not include Node.js, Chromium, Playwright browser dependencies, or automation test source.

## Observability

Logging and monitoring surfaces:

- ASP.NET Core console/debug logs.
- Upgrade stage progress.
- Stage-specific details.
- Complete operation logs.
- Playwright structured result summaries.
- HTML report links.
- Upgrade history.

Logs must be sanitized before display or persistence.

## Product-Specific Notes

This stack applies to Bold Upgrade Center. Bold Reports Upgrade Center uses the same application stack but different release API, Kubernetes deployment list, health-check targets, Playwright runner image, worker/resource defaults, report paths, and product branding.
