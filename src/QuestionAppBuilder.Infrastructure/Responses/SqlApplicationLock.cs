using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Infrastructure.Responses;

internal static class SqlApplicationLock
{
    public static async Task AcquireAsync(QuestionAppBuilderDbContext db, int applicationId, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer()) throw new InvalidOperationException("Application coordination requires SQL Server.");
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000, @DbPrincipal = 'public'; SELECT @result;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.DbType = DbType.String;
        parameter.Value = $"QuestionAppBuilder:Application:{applicationId}";
        command.Parameters.Add(parameter);
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        if (result < 0) throw new TimeoutException($"Could not acquire the application coordination lock (SQL Server result {result}).");
    }
}
