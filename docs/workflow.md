# Bold Upgrade Center Workflow

## 1. Entry and Access Validation

1. A super administrator connects through the approved corporate VPN/private network or identity-aware administrative access path and opens `/upgrade-center`.
2. The Upgrade Center validates the user session through UMS.
3. The Upgrade Center confirms that the authenticated user is a super administrator.
4. If validation succeeds, the dashboard is shown.
5. If validation fails, the user is redirected to the configured login or access-denied flow.

If UMS becomes temporarily unavailable after successful validation, the application may allow read-only monitoring of an already-running operation for the validated pod lifecycle. It must not start, cancel, retry, roll back, restore, delete, or otherwise mutate state until fresh super-administrator authorization succeeds.

Session cookies use `Secure`, `HttpOnly`, and an approved `SameSite` policy. Login-return and Back-to-site destinations are allowlisted to prevent open redirects. Every state-changing browser request requires anti-forgery validation and server-side super-administrator authorization; hiding a button is never treated as authorization.

The MVC application uses an unprivileged ServiceAccount. It cannot patch deployments, create Playwright resources or Secrets, read pod logs, or restore databases. Mutations are executed by the internal-only `UpgradeExecutor` after it validates a signed, approved, single-use operation plan.

## 2. Dashboard Load

When the dashboard opens, the application:

1. Resolves the installed Bold BI version.
2. Retrieves available release metadata from the configured version provider.
3. Filters releases newer than the installed version.
4. Loads recent upgrade history.
5. Resolves whether a valid active rollback point exists.
6. Displays available releases, custom patch input, and history.

If the installed version is already current, the Available releases section shows a no-newer-version message.

## 3. Standard Release Confirmation

When the administrator clicks Upgrade for a release:

1. The application opens the confirmation flow.
2. Master and active tenant metadata databases are discovered. Customer dashboard/business data stores are excluded.
3. Duplicate database connections are removed.
4. Database types are identified.
5. Database scripts are retrieved for the selected upgrade range.
6. Affected tables are extracted.
7. Existing affected tables are identified per database.
8. Created tables are tracked where metadata is available.
9. The confirmation dialog shows current version, target version, affected table summary, and warnings.

No upgrade job starts until the administrator clicks Start Upgrade.

## 4. Upgrade Job Start

After confirmation:

1. The POST request passes anti-forgery validation.
2. UMS/Bold ID performs mandatory MFA-backed step-up authentication. Failure or unavailability denies the operation.
3. The session and super-administrator permission are freshly revalidated.
4. For production, a different super administrator approves the request. Self-approval is rejected. Approval expires after 15 minutes.
5. The confirmed source/target versions, environment, namespace, database scope, script digests, image digests, requester, approver, nonce, and expiry are serialized into a canonical immutable operation plan.
6. The operation plan is signed using the approved asymmetric workload key. Any change after approval invalidates the signature and approval.
7. The service acquires the namespace-scoped distributed operation lock and enforces the unique active-operation constraint.
8. A client/request idempotency key and the single-use operation nonce are recorded so retries or replays cannot start another job.
9. A new upgrade job ID is created.
10. Job state and the first durable checkpoint are stored.
11. History, approval, and append-only audit records are created with the requester and approver identities.
12. The internal `UpgradeExecutor` validates the signature, issuer, audience, expiry, nonce, environment, namespace, approval, and resource allowlist before the background job starts.
13. The UI navigates to the monitoring page and begins rate-limited polling.

Only one upgrade, rollback, or mutating recovery operation can run in a namespace at a time. The lock remains effective across replicas and restarts and is released only after terminal-state reconciliation.

## 5. Schema Backup Stage

The Schema Backup stage runs before pre-upgrade validation so validation-created temporary data is not included in customer rollback backups.

The stage:

1. Discovers master and active tenant metadata databases and rejects databases outside the approved metadata scope.
2. Validates reachable database connections.
3. Analyzes database scripts for the selected upgrade range.
4. Builds the cumulative affected table set.
5. Checks whether each affected table exists in each database.
6. Backs up only matching existing tables.
7. Records warnings for missing tables.
8. Stores backup mappings and rollback context.
9. Encrypts backup artifacts using the approved storage/KMS mechanism and records integrity metadata.
10. Verifies the backup can be read and matches its integrity metadata before proceeding.
11. Writes an audit event containing the operation, database identifier, affected-table count, and outcome without connection strings or credentials.

Outcomes:

- Success: continue to Pre-Upgrade Validation.
- No matching existing tables: skip physical backup, record warning, continue when safe.
- Failure: stop before image changes and mark the job failed.

## 6. Pre-Upgrade Validation Stage

If Playwright Kubernetes Jobs are enabled:

1. Runtime configuration is resolved.
2. A short-lived, job-scoped temporary Secret and upload token are created.
3. A hardened Playwright Job is created with mode `pre`, a dedicated ServiceAccount, non-root security context, resource limits, active deadline, retry limit, and TTL cleanup.
4. Shared state PVC is mounted when enabled.
5. The runner executes the pre-upgrade tests.
6. The runner uploads schema-validated structured results and the HTML report using the job/stage/attempt-scoped one-time token.
7. The application validates content type, size, job state, idempotency key, and server-generated storage path.
8. Temporary sensitive resources are cleaned up on success, failure, cancellation, timeout, and recovery; cleanup failures generate alerts.

If Playwright Kubernetes Jobs are disabled:

1. Product health checks run through `ProductHealthCheckService`.
2. Results are mapped to the pre-upgrade validation stage.

Outcomes:

- Success: continue to Kubernetes Upgrade.
- Failure: stop before image changes, clean new backup artifacts where safe, and mark the job failed.

## 7. Kubernetes Upgrade Stage

The stage:

1. Resolves the Kubernetes namespace.
2. Retrieves authenticated target image metadata for the selected release or custom patch.
3. Verifies release metadata, database-script digests, image signatures/provenance, and immutable image digests.
4. Verifies the release manifest's detached signature using an independently protected release-signing trust root; Release API or registry TLS alone is not sufficient.
5. The internal executor lists Bold BI deployments using its short-lived, audience-bound namespace ServiceAccount token.
6. It selects the correct container image in each approved deployment and rejects unexpected deployments or containers.
7. It records previous and target immutable image digests.
8. It creates or updates rollback context and a durable checkpoint before patching images.
9. It patches only the approved image fields in rollout groups; arbitrary manifests and commands are not accepted.
10. It writes a durable checkpoint after each deployment group.
11. It compares the running digest with the approved digest and stops on drift.
12. It waits for each rollout to become healthy within bounded timeouts.

Rollout groups:

1. `id-web-deployment`, `id-api-deployment`, `id-ums-deployment`
2. `bi-web-deployment`, `bi-api-deployment`, `bi-jobs-deployment`
3. `bi-dataservice-deployment`, `bold-ai-deployment`, `bold-etl-deployment`

Outcomes:

- Success: continue to Post-Upgrade Validation.
- Failure with rollback context: start automatic rollback.
- Failure without rollback context: mark upgrade failed.

## 8. Post-Upgrade Validation Stage

If Playwright Kubernetes Jobs are enabled:

1. A Playwright Job is created with mode `post`.
2. Shared state generated during pre-upgrade validation is available when shared state volume is enabled.
3. The runner executes post-upgrade tests.
4. Structured results and HTML report are uploaded.

If Playwright Kubernetes Jobs are disabled:

1. Product health checks run through `ProductHealthCheckService`.

Outcomes:

- Success: mark upgrade succeeded and update history.
- Failure: start automatic rollback using the recorded rollback point.

## 9. Automatic Rollback

Automatic rollback runs when an upgrade cannot be safely completed after image changes.

The workflow:

1. Restore backed-up database tables.
2. Drop tracked created tables where applicable.
3. Revert Kubernetes deployment images to previous references.
4. Wait for reverted deployment rollouts.
5. Run product health checks.
6. Mark rollback succeeded or failed.
7. Update the parent upgrade job and history.

If rollback fails, recovery context is preserved.

## 10. Manual Rollback

Manual rollback is available only for the latest valid active rollback point.

The workflow:

1. Super administrator opens rollback confirmation.
2. Confirmation page shows rollback version transition and warning.
3. Super administrator confirms through an anti-forgery-protected POST request and completes mandatory MFA-backed step-up authentication.
4. In production, a different super administrator approves the rollback. Self-approval and expired approval are rejected.
5. The application revalidates the rollback point, backup ownership and integrity, target database identity, previous image digests, operation scope, and anti-rollback authorization.
6. It creates and signs a single-use rollback plan bound to the requester, approver, environment, namespace, backup IDs, previous image digests, nonce, and 15-minute expiry.
7. The namespace operation lock and idempotency record are acquired.
8. The internal executor validates the plan and begins rollback using its isolated database and Kubernetes identities.
9. Monitoring shows rollback-specific stages only.
10. The rollback point is marked consumed after success.
11. The main Rollback page no longer shows consumed or deleted rollback points as available.

## 11. Cancellation

Cancellation is available only while an upgrade is active.

Behavior:

- Before image changes: stop the current operation and mark the job cancelled.
- After image changes: request cancellation and run rollback when rollback context exists.
- During rollback: cancellation is blocked because rollback is restoring the system.

Repeated cancellation requests must not create duplicate rollback jobs.

Cancellation uses an anti-forgery-protected POST request, mandatory MFA-backed step-up authentication, fresh authorization, the current operation ID, and an idempotency key. Cancellation after mutation requires a second production approver because it may initiate rollback. The service records who requested and approved cancellation and the stage at which it was accepted or rejected.

## 12. Monitoring and Logs

The monitoring page polls:

- Job status.
- Stage status.
- Stage details.
- Complete operation logs.
- Playwright report availability.

Stage Details contain stage-specific progress. Complete Operation Logs contain job-level live logs. Sensitive values are sanitized.

Monitoring and log endpoints require current authorization, enforce operation validation, apply polling and response-size limits, and use correlation IDs. Audit events and complete logs are exported to approved centralized storage with retention and integrity protection. Alerts are raised for authorization failures, integrity failures, rollback/restore failures, abnormal uploads, secret-cleanup failures, and inconsistent recovery state.

Production detections block or alert on repeated upgrade/cancel/rollback cycles, rollback outside an approved change window, unexpected requester/approver patterns, image digest drift, release-signature failure, stale temporary Secrets, anomalous report uploads, and recovery-state inconsistency. Requester and approver receive an out-of-band notification when a privileged operation is requested, approved, rejected, started, cancelled, rolled back, restored, or completed.

## 13. Playwright Report Flow

1. The runner executes validation.
2. It writes a structured result file.
3. It uploads result summary JSON to the Upgrade Center.
4. It uploads the self-contained HTML report to the Upgrade Center.
5. The upload endpoint validates the one-time token, job ID, stage, attempt, idempotency key, schema/content type, and compressed/expanded size.
6. The server generates the storage name and path; runner-provided filenames are not used.
7. The report is stored outside the web root by job ID and stage.
8. The UI shows View HTML Report only when the stored report exists and the current user remains a super administrator.
9. HTML is downloaded as an attachment or rendered in an isolated sandbox with a restrictive Content Security Policy; it cannot read Upgrade Center cookies or call Upgrade Center APIs.
10. Report view and deletion events are audited.
11. Old report directories beyond the configured retention limit are removed, except evidence belonging to active or unresolved operations.

Database backup artifacts are not reports and are never exposed through the UI, report endpoint, history endpoint, monitoring endpoint, or any download API. Only the internal executor's restore identity can retrieve an artifact by its operation-bound identifier.

## 14. History Flow

History records are created when jobs start and updated when they reach terminal state.

History includes:

- Completed upgrades.
- Failed upgrades.
- Cancelled upgrades.
- Manual rollbacks.
- Rolled-back upgrades.

The Open link is available when job detail context is available.

## 15. Recovery After Upgrade Center Restart

On restart, persisted operational state is used to recover or reconcile safe stages.

Safe recovery examples:

- Recover Kubernetes rollout status after image patching.
- Continue or finalize post-upgrade validation when enough context exists.
- Preserve rollback context for manual recovery.

Unsafe stages fail safely with a clear message instead of silently continuing.

Recovery first acquires the namespace-scoped distributed operation lock and verifies the persisted operation owner, sequence number, stage, attempt, terminal markers, backup identifiers, and previous/target image digests. Non-idempotent backup, restore, deployment patch, rollback, and cleanup steps are never replayed without their durable completion markers. Inconsistent state blocks further mutation, preserves artifacts, creates an audit event and alert, and requires a newly authorized operator decision.

## 16. Input and Command Safety

1. Version values, repository names, tags, digests, namespaces, deployments, containers, job IDs, database providers, database/schema/table identifiers, and paths are validated against allowlists and discovered metadata.
2. Database values use parameterized commands; identifiers use provider-specific quoting after validation.
3. The application does not concatenate user-controlled input into shell commands, Kubernetes command arguments, SQL, filesystem paths, URLs, or redirect destinations.
4. Custom patch input is resolved to an approved registry/repository and immutable digest before confirmation. Changing the resolved target invalidates confirmation.
5. Error responses return correlation IDs and safe messages without commands, connection strings, tokens, stack traces, or internal paths.

## 17. Retention and Cleanup

1. Backup, report, operation-log, audit-log, history, Job, Secret, and PVC retention periods are defined through approved configuration.
2. Active rollback points and unresolved security/operational evidence are protected from automated deletion.
3. Expired artifacts are deleted by an authenticated cleanup operation that records counts, identifiers, outcome, and failures.
4. Temporary Secrets and upload tokens expire independently of normal cleanup and cannot be reused.
5. Cleanup is bounded, idempotent, rate-limited, and cannot delete resources lacking the expected ownership labels and operation ID.

## 18. Dependency Failure and Availability

1. Release API, registry, UMS/IDP, database, Kubernetes API, health-check, and storage calls use bounded timeouts and cancellation.
2. Read-only calls may use limited retries with exponential backoff and jitter. Database mutation, image patch, restore, and rollback steps are not blindly retried.
3. Failure before mutation stops safely. Failure after mutation uses durable checkpoints to reconcile or roll back.
4. Playwright Jobs enforce worker, CPU, memory, ephemeral-storage, log-size, retry, active-deadline, and TTL limits.
5. The system limits request bodies, report sizes, status polling, log responses, retained history, and concurrent requests to prevent resource exhaustion.

## 19. Production Approval and Break-Glass Workflow

1. The requester creates a production operation plan but cannot execute it without a second super administrator.
2. The approver reviews the exact environment, namespace, source/target versions, database scope, script digests, image digests, expected downtime, and rollback point.
3. The approval is cryptographically bound to the plan digest, expires after 15 minutes, and is invalidated by any change.
4. The executor accepts the plan only once and rejects replayed, expired, self-approved, differently scoped, or incorrectly signed plans.
5. Emergency break-glass access uses a separate controlled identity and requires an incident/change reference. It cannot silently bypass audit logging, immutable digest enforcement, backup integrity, or executor allowlists.
6. Break-glass use immediately notifies security and product operations and requires a documented post-event review.

## 20. Security Verification and Release Gate

The secured workflow has no reduced-control production mode. Mandatory step-up authentication, two-person approval, identity/RBAC isolation, signed single-use plans, signed release metadata, immutable image/script digests, encrypted and integrity-checked backups, protected Data Protection keys, private ingress, default-deny NetworkPolicies, centralized immutable audit logs and alerts, secure report uploads, credential expiry/cleanup, abuse-rate controls, and restart/replay protection are prerequisites for execution. Failure of any runtime prerequisite denies or freezes the operation, and failure of any deployment validation blocks release.

Required verification includes positive and negative authorization tests, self-approval denial, stale-session denial, signature/replay/expiry tests, network allowed/denied tests, RBAC `can-i` denial tests, image-drift tests, backup-tamper restore denial, report path-traversal and active-content tests, temporary-secret expiry alerts, anomalous rollback simulations, and repeated-restart recovery tests. Failed or missing evidence keeps the corresponding release gate closed.

## 21. Residual Risk Outcome

This workflow implements the conformant target architecture assessed in `architecture-design.md`. Its residual design risk is **23/100 (LOW)**, calculated after the mandatory controls that define the workflow. The remaining risk is bounded because:

- The administrative interface is private and anonymous/public requests cannot reach it.
- A compromised browser session alone cannot authorize mutation because MFA-backed step-up and a distinct approver are required.
- Compromise of the MVC process does not provide Kubernetes or database mutation credentials.
- The executor cannot expand beyond a signed one-time plan, one namespace, approved deployments, and approved metadata-database targets.
- Backup content cannot be browsed or downloaded through the application.
- Release API or registry compromise alone cannot bypass independent signature, digest, provenance, and admission checks.
- Repeated or inconsistent operations are blocked by the operation lock, nonce, idempotency controls, anomaly rules, and fail-closed recovery.

There is no accepted fallback that omits these controls. A deployment that does not meet them is rejected as non-conformant rather than treated as an approved higher-risk variant.
