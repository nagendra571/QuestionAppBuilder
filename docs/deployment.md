# Deployment and operations

QuestionAppBuilder is deployed inside its consuming ASP.NET Core host. The host owns its SQL Server catalog and identity provider. The first release targets `net8.0`/EF Core 8, and uses SQL Server transactions, `sp_getapplock`, rowversions, and EF migrations for durable response, access, and report behavior.

## Configuration and secrets

Outside Development, provide HTTPS OIDC authority and expected issuer, client ID and secret, an absolute persistent Data Protection key-ring path, a trusted HTTPS `PublicBaseUri`, and SMTP host/port/sender. SMTP may use implicit TLS or STARTTLS; if credentials are used, supply both username and password from the deployment secret provider. Do not put credentials in source control, appsettings checked into a repository, build logs, or release artifacts. The host fails startup for missing production identity, key persistence, or email configuration. Keep one trusted issuer and stable subject mapping for signed-in respondent ownership.

Use an encrypted, access-controlled, durable key ring shared by every replica. Back it up with the database and restrict read access to the application identity. Rotating or losing these keys can invalidate sign-in and response cookies. Terminate TLS only at a trusted ingress and configure forwarded headers for known proxies; do not trust arbitrary forwarded host/scheme headers. Apply the design, fill, and report policies from the consuming host's authorization model. An empty design policy is deliberately rejected.

## Database release

Back up the production database and verify the restore procedure before a schema release. Generate and review the idempotent migration script from the version being released, apply it through the controlled database release identity, and verify schema state before shifting traffic. The application runtime account should have only the permissions required by the deployment's migration strategy; separate schema-change privileges from steady-state access when the organization permits it. The sample host migrates automatically in Development only. Production startup never creates, resets, or migrates a database.

Migrations are additive and preserve existing response/submission data. Validate upgrades from the previous migration on a disposable SQL Server catalog with the release test script. Never point acceptance tests at a production catalog: SQL integration tests create and drop uniquely named `QAB_Test_<guid>` databases under the supplied server connection.

## Availability, retention, and recovery

Availability changes, response drafts, one-time access grants, submissions, frozen answer states, and score traces live in SQL Server. Protect backups and logs as respondent data. Define retention and deletion procedures with the host organization; this platform does not make a retention period decision. A response has at most one submission, and a repeated submit request returns its existing receipt. SQL Server is the authority for transactions, concurrency, foreign keys, and per-application locks; in-memory test providers do not prove those guarantees.

Back up the database and Data Protection key ring as a coordinated recovery set. Restore both into a restricted environment first, verify migration state and application startup, then validate that an existing email-link/session workflow behaves as expected. Monitor database capacity, transaction failures, SMTP delivery, authentication, and authorization failures through host-owned telemetry. Do not log raw access tokens, SMTP secrets, or full respondent answers unnecessarily.

## Release and rollback

Run `scripts/Test-Release.ps1 -SqlConnection <disposable-test-server-connection>` from a Windows environment with the .NET SDK, LocalDB or a dedicated disposable SQL Server, and installed Playwright Chromium. This runs the full solution against uniquely named SQL catalogs and verifies an installed-package consumer. It does not deploy or publish packages. For a failed release, stop traffic to the new application version and restore the prior application artifact only after confirming it remains compatible with the migrated schema; do not drop or reset the database. Use a forward repair migration if a schema rollback would lose data.
