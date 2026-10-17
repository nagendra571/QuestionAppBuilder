# SQL Server integration tests

Set `QAB_TEST_SQLSERVER` to a SQL Server connection string for integration tests. Tests create a unique `QAB_Test_<guid>` catalog per test, apply EF migrations there, and drop only that catalog afterward. The configured catalog is ignored and never migrated or deleted. Missing configuration marks each SQL fact skipped; an unreachable configured server fails the test. LocalDB example for PowerShell: `$env:QAB_TEST_SQLSERVER = 'Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true'`.
