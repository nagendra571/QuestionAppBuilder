namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QAB_TEST_SQLSERVER")))
            Skip = "Set QAB_TEST_SQLSERVER to run disposable SQL Server integration tests.";
    }
}
