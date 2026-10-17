using System.Data.Common;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Reporting;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Reporting;
using Xunit.Abstractions;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

[Trait("Category", "Scale")]
public sealed class ReportingScaleTests(ITestOutputHelper output)
{
    [SqlServerFact]
    public async Task Reporting_scale_benchmark_uses_ten_thousand_submissions_and_fifty_questions()
    {
        await using var sql = await SqlServerDatabase.CreateAsync();
        await using var setup = sql.CreateContext();
        await setup.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        var category = new Category { Name = "Scale test", SortOrder = 1 };
        for (var index = 1; index <= 50; index++)
        {
            category.Elements.Add(new Element
            {
                Kind = ElementKind.Question, Label = $"Question {index}", SortOrder = index,
                Question = new Question { AnswerType = AnswerType.Textbox, IsRequired = false }
            });
        }
        var version = new ApplicationVersion
        {
            VersionNumber = 1, Status = VersionStatus.Published, DisplayName = "Scale benchmark",
            CreatedUtc = now, PublishedUtc = now, Categories = { category }
        };
        var app = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "Reporting scale " + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "RFA scale " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
            CreatedUtc = now, UpdatedUtc = now, AcceptNewResponses = true, AcceptSubmissions = true, Versions = { version }
        };
        setup.Applications.Add(app);
        await setup.SaveChangesAsync();
        app.CurrentVersionId = version.Id;
        await setup.SaveChangesAsync();

        setup.Submissions.AddRange(Enumerable.Range(0, 10_000).Select(index => new Submission
        {
            ApplicationId = app.Id, ApplicationVersionId = version.Id, UserId = $"synthetic-{index:D5}",
            SubmittedUtc = now.AddSeconds(index), TotalScore = index % 101
        }));
        await setup.SaveChangesAsync();
        var queries = new ReportQueries(new ContextFactory(sql));
        var export = new ReportExport(new ContextFactory(sql));
        var filter = new ReportFilter(app.Id, version.Id);

        var before = GC.GetTotalAllocatedBytes(true);
        var overviewClock = Stopwatch.StartNew();
        var overview = await queries.GetOverviewAsync(filter);
        overviewClock.Stop();
        overview.SubmittedCount.Should().Be(10_000);

        var pageClock = Stopwatch.StartNew();
        var page = await queries.GetPageAsync(filter, pageSize: 100);
        pageClock.Stop();
        page.Items.Should().HaveCount(100);

        var detailClock = Stopwatch.StartNew();
        var detail = await queries.GetDetailAsync(page.Items[0].Id);
        detailClock.Stop();
        detail!.Questions.Should().HaveCount(50);

        var summaryClock = Stopwatch.StartNew();
        await export.WriteAsync(new ExportFilter(filter, IncludeAnswers: false), Stream.Null);
        summaryClock.Stop();

        var answerClock = Stopwatch.StartNew();
        await export.WriteAsync(new ExportFilter(filter, IncludeAnswers: true), Stream.Null);
        answerClock.Stop();
        var allocated = GC.GetTotalAllocatedBytes(true) - before;

        var plan = await CaptureActualPagePlanAsync(setup.Database.GetDbConnection(), app.Id);
        var planPath = Environment.GetEnvironmentVariable("QAB_REPORT_BENCHMARK_PLAN");
        if (string.IsNullOrWhiteSpace(planPath))
            planPath = Path.Combine(Path.GetTempPath(), "qab-report-scale-" + Guid.NewGuid().ToString("N") + ".sqlplan");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(planPath))!);
        await File.WriteAllTextAsync(planPath, plan);

        output.WriteLine($"Dataset: 10,000 submissions, 50 questions, SQL Server disposable catalog; query/export times are this-machine observations only.");
        output.WriteLine($"Overview: {overviewClock.Elapsed.TotalMilliseconds:F1} ms; page 100: {pageClock.Elapsed.TotalMilliseconds:F1} ms; detail: {detailClock.Elapsed.TotalMilliseconds:F1} ms; summary export: {summaryClock.Elapsed.TotalMilliseconds:F1} ms; answer export: {answerClock.Elapsed.TotalMilliseconds:F1} ms; total allocated bytes: {allocated:N0}.");
        output.WriteLine($"Actual SQL Server page plan: {planPath}");
    }

    private static async Task<string> CaptureActualPagePlanAsync(DbConnection connection, int applicationId)
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SET STATISTICS XML ON; SELECT TOP (101) [s].[Id], [s].[SubmittedUtc], [s].[TotalScore] FROM [dbo].[Submissions] AS [s] WHERE [s].[ApplicationId] = @applicationId ORDER BY [s].[SubmittedUtc] DESC, [s].[Id] DESC; SET STATISTICS XML OFF;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@applicationId";
        parameter.Value = applicationId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync();
        var plans = new List<string>();
        do
        {
            if (reader.FieldCount == 1 && reader.GetName(0).Contains("Microsoft SQL Server", StringComparison.OrdinalIgnoreCase))
                while (await reader.ReadAsync()) plans.Add(reader.GetString(0));
            else while (await reader.ReadAsync()) { }
        } while (await reader.NextResultAsync());
        plans.Should().NotBeEmpty("SET STATISTICS XML must return an actual execution plan for the paged submission query");
        return string.Join(Environment.NewLine, plans);
    }

    private sealed class ContextFactory(SqlServerDatabase database) : IDbContextFactory<QuestionAppBuilderDbContext>
    {
        public QuestionAppBuilderDbContext CreateDbContext() => database.CreateContext();
        public Task<QuestionAppBuilderDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(database.CreateContext());
    }
}
