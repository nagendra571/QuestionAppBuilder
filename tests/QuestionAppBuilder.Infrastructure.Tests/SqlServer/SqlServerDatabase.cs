using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public sealed class SqlServerDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _databaseName;
    private bool _disposed;

    private SqlServerDatabase(string adminConnectionString, string databaseName)
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
    }

    public static async Task<SqlServerDatabase> CreateAsync(CancellationToken cancellationToken = default)
    {
        var configured = Environment.GetEnvironmentVariable("QAB_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("QAB_TEST_SQLSERVER is required for SQL Server tests.");

        var builder = new SqlConnectionStringBuilder(configured);
        builder.InitialCatalog = "master";
        var databaseName = $"QAB_Test_{Guid.NewGuid():N}";
        var fixture = new SqlServerDatabase(builder.ConnectionString, databaseName);
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{databaseName}]";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return fixture;
    }

    public QuestionAppBuilderDbContext CreateContext()
    {
        var builder = new SqlConnectionStringBuilder(_adminConnectionString) { InitialCatalog = _databaseName };
        return new QuestionAppBuilderDbContext(new DbContextOptionsBuilder<QuestionAppBuilderDbContext>()
            .UseSqlServer(builder.ConnectionString).Options);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await using var connection = new SqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID(@databaseName) IS NOT NULL BEGIN ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]; END";
        command.Parameters.AddWithValue("@databaseName", _databaseName);
        await command.ExecuteNonQueryAsync();
    }
}
