# SQL Server verification ledger — 2026-10-15

Task 7 of `docs/superpowers/plans/2026-10-15-questionappbuilder-core-engine.md`. Proves on a real SQL Server the behaviors EF InMemory fakes: seed-on-migrate, unique-index enforcement, rowversion lost-update blocking, and enum-as-string storage.

The suite is env-gated by `QAB_TEST_SQLSERVER`. When unset, every smoke test early-returns (`// SQL smoke disabled: set QAB_TEST_SQLSERVER to run`) — the suite always passes and **never** runs against InMemory; that is the point.

- Environment: Windows; .NET SDK 10.0.113 building net8.0; EF Core 8.0.32 (`Microsoft.EntityFrameworkCore.SqlServer 8.0.32`, `Microsoft.Data.SqlClient 5.1.9`); global `dotnet-ef 9.0.8`; SQL Server LocalDB instance `MSSQLLocalDB`; `sqlcmd` (ODBC 170).
- Suite: `tests/QuestionAppBuilder.Infrastructure.Tests/SqlServer/SqlServerSmokeTests.cs`.
- Per-test isolation: xUnit creates a new test-class instance per fact; each fact creates its own database `QAB_Smoke_{Guid.NewGuid():N}` (built with `SqlConnectionStringBuilder` over the env connection string), runs `Database.MigrateAsync()` in its own body, and `DisposeAsync` calls `EnsureDeletedAsync()`. No test depends on another migrating first.

## Step 2 — suite without the variable (early returns)

Command (env var explicitly removed in the same scope):

```powershell
Remove-Item Env:\QAB_TEST_SQLSERVER -ErrorAction SilentlyContinue
dotnet test tests/QuestionAppBuilder.Infrastructure.Tests
```

Observed (exact):

```
Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8, Duration: 1 s - QuestionAppBuilder.Infrastructure.Tests.dll (net8.0)
```

8 = 2 `ModelRoundTripTests` + 2 `ServiceCollectionExtensionsTests` + 4 `SqlServerSmokeTests` that early-returned. PASS.

## Step 3 — suite against a real SQL Server (LocalDB)

Command (env var set in the same PowerShell scope as `dotnet test`):

```powershell
$env:QAB_TEST_SQLSERVER='Server=(localdb)\MSSQLLocalDB;Integrated Security=true'; dotnet test tests/QuestionAppBuilder.Infrastructure.Tests --logger "console;verbosity=detailed"
```

Observed (exact, per-test lines trimmed to the test name):

```
  Passed QuestionAppBuilder.Infrastructure.Tests.SqlServer.SqlServerSmokeTests.Duplicate_application_name_violates_unique_index [2 s]
  Passed QuestionAppBuilder.Infrastructure.Tests.SqlServer.SqlServerSmokeTests.RowVersion_blocks_lost_update [531 ms]
  Passed QuestionAppBuilder.Infrastructure.Tests.SqlServer.SqlServerSmokeTests.Migrations_apply_and_seed [382 ms]
  Passed QuestionAppBuilder.Infrastructure.Tests.SqlServer.SqlServerSmokeTests.Enums_persist_as_strings [393 ms]

Test Run Successful.
Total tests: 8
     Passed: 8
 Total time: 5.0067 Seconds
```

What each fact verified on real SQL Server:

1. `Migrations_apply_and_seed` — `Database.MigrateAsync()` created the schema and ran the seed; raw read `SELECT [Name] FROM [ApplicationTypes]` returned both `"Request For Application (RFA)"` and `"Request For Information (RFI)"`.
2. `Duplicate_application_name_violates_unique_index` — first insert saved; a second `Application` row with the same `Name` made `SaveChangesAsync` throw `DbUpdateException` — SQL Server enforced unique index `IX_Applications_Name`.
3. `RowVersion_blocks_lost_update` — two contexts loaded the same `Application`; both edited distinct new names; the first `SaveChangesAsync` succeeded; the second threw `DbUpdateConcurrencyException` — a real `rowversion` token mismatch, not an InMemory no-op.
4. `Enums_persist_as_strings` — an `Application` and a `Draft` `ApplicationVersion` were inserted through the context; the raw scalar `SELECT [Status] FROM [ApplicationVersions]` read back exactly `"Draft"` (not `0`).

Cleanup evidence (`DisposeAsync` ran `Database.EnsureDeleted()` for every context):

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "SET NOCOUNT ON; SELECT name FROM sys.databases WHERE name LIKE 'QAB_Smoke%'; IF @@ROWCOUNT = 0 PRINT 'ZERO_SMOKE_DATABASES_LEFT'"
```

Observed: no name rows, then `ZERO_SMOKE_DATABASES_LEFT` — no `QAB_Smoke_*` databases remained.

Negative control — proves the variable is actually consumed and there is no silent InMemory fallback:

```powershell
$env:QAB_TEST_SQLSERVER='Server=(localdb)\QAB_NoSuchInstance;Integrated Security=true'; dotnet test tests/QuestionAppBuilder.Infrastructure.Tests --filter "FullyQualifiedName~SqlServerSmokeTests"
```

Observed:

```
Failed!  - Failed:     4, Passed:     0, Skipped:     0, Total:     4, Duration: 1 m - QuestionAppBuilder.Infrastructure.Tests.dll (net8.0)
```

with `Microsoft.Data.SqlClient.SqlException: A network-related or instance-specific error occurred while establishing a connection to SQL Server. ... The specified LocalDB instance does not exist.` — with the variable set, the tests attempt real SQL and fail loudly.

## Step 4 — migration script inspection

Command:

```powershell
dotnet ef migrations script --project src/QuestionAppBuilder.Infrastructure --startup-project src/QuestionAppBuilder.Infrastructure > qab-migration-script.sql
```

Observed: exit code 0; output 245 lines (lines 1–2 are MSBuild status `Build started...` / `Build succeeded.`; SQL body follows); no warnings.

Script observations:

- **13 user tables** plus `__EFMigrationsHistory` (14 `CREATE TABLE` statements): ApplicationTypes, Applications, ApplicationVersions, Categories, Elements, Questions, QuestionOptions, ScoringRules, ScoringRuleOptions, Submissions, Answers, AnswerSelectedOptions, SubmissionCategoryScores.
- **Unique indexes (6)**: `IX_Applications_Name`; `IX_ApplicationVersions_ApplicationId_VersionNumber`; `IX_AnswerSelectedOptions_AnswerId_QuestionOptionId`; `IX_ScoringRuleOptions_ScoringRuleId_QuestionOptionId`; `IX_SubmissionCategoryScores_SubmissionId_CategoryId`; `IX_Questions_ElementId` (one-to-one convention).
- **Rowversion**: `[RowVersion] rowversion NOT NULL,` inside `CREATE TABLE [Applications]`.
- **Seed rows (2)**: `INSERT INTO [ApplicationTypes] ([Id], [Name], [SortOrder]) VALUES (1, N'Request For Application (RFA)', 1), (2, N'Request For Information (RFI)', 2);` wrapped in `IDENTITY_INSERT` on/off.
- **Enums as strings**: `[Status] nvarchar(32) NOT NULL`, `[Kind] nvarchar(32) NOT NULL`, `[AnswerType] nvarchar(32) NOT NULL`, `[Family] nvarchar(32) NOT NULL`, `[MembershipOperator] nvarchar(32) NULL`.
- **Table rebuild check: none.** 0 occurrences of `DROP COLUMN`, `ALTER COLUMN`, or `sp_rename` — an initial create performs only `CREATE TABLE`/`CREATE INDEX`/FK additions; the 4 deferred `ALTER TABLE ... ADD CONSTRAINT` statements are plain foreign-key adds for tables that reference later-created tables, not rebuilds.

## Step 6 — final full run

Commands and observed results (env var unset):

```
dotnet build
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

```
dotnet test
Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8, Duration: 1 s - QuestionAppBuilder.Infrastructure.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31, Duration: 2 s - QuestionAppBuilder.Application.Tests.dll (net8.0)
```

39/39 passing; with the variable unset the smoke suite early-returned, exactly as designed.

## Notes

- The three behaviors covered here (unique-index rejection, rowversion concurrency, enum-to-string storage) are SQL Server–only and are faked by the InMemory provider; this suite is their only real verification and therefore self-skips outside CI/dev machines with the env var set.
- Nothing was skipped or left unrunnable: Steps 2, 3, 4, and 6 all executed for real on this machine; every observed result above is copied from actual terminal output.
