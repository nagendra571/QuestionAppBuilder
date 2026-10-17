using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Reporting;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Reporting;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public sealed class ExportQueryTests
{
    [SqlServerFact]
    public async Task Csv_formula_prefixes_are_neutralized()
    {
        var dangerous = new[] { "=SUM(1,1)", "+1+1", "-1+1", "@SUM(1,1)", "\t=1+1", "\r=1+1", "\n=1+1", " \t=1+1", "  =1+1" };
        await using var fixture = await Fixture.CreateAsync(dangerous);
        using var stream = new MemoryStream();

        await fixture.Export().WriteAsync(new ExportFilter(new ReportFilter(fixture.ApplicationId,
            fixture.VersionId), IncludeAnswers: true), stream);

        var rows = ParseCsv(Encoding.UTF8.GetString(stream.ToArray()));
        rows.Skip(1).Select(row => row[11]).Should().BeEquivalentTo(dangerous.Select(value => "'" + value));
    }

    [SqlServerFact]
    public async Task Quotes_commas_newlines_round_trip()
    {
        const string value = "A response, with \"quoted text\"\nand another line";
        await using var fixture = await Fixture.CreateAsync(new[] { value });
        using var stream = new MemoryStream();

        await fixture.Export().WriteAsync(new ExportFilter(new ReportFilter(fixture.ApplicationId,
            fixture.VersionId), IncludeAnswers: true), stream);

        var rows = ParseCsv(Encoding.UTF8.GetString(stream.ToArray()));
        rows[1][11].Should().Be(value);
    }

    [SqlServerFact]
    public async Task Export_distinguishes_not_applicable_from_unanswered_and_hides_hidden_values()
    {
        await using var fixture = await Fixture.CreateAsync(new[] { "hidden answer", string.Empty }, notApplicableIndex: 0, unansweredIndex: 1);
        using var stream = new MemoryStream();

        await fixture.Export().WriteAsync(new ExportFilter(new ReportFilter(fixture.ApplicationId,
            fixture.VersionId), IncludeAnswers: true), stream);

        var csv = Encoding.UTF8.GetString(stream.ToArray());
        var rows = ParseCsv(csv);
        rows[1][9].Should().Be("not-applicable");
        rows[1][10].Should().Be("not-applicable");
        rows[1][11].Should().BeEmpty();
        rows[2][9].Should().Be("applicable");
        rows[2][10].Should().Be("unanswered");
        rows[2][11].Should().BeEmpty();
        csv.Should().NotContain("hidden answer");
    }

    [SqlServerFact]
    public async Task Cross_version_answer_export_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync(new[] { "answer" });
        using var stream = new MemoryStream();

        var act = () => fixture.Export().WriteAsync(new ExportFilter(new ReportFilter(fixture.ApplicationId), IncludeAnswers: true), stream);

        await act.Should().ThrowAsync<ArgumentException>();
        stream.Length.Should().Be(0);
    }

    [SqlServerFact]
    public async Task Cancellation_stops_stream_without_loading_the_full_result()
    {
        await using var fixture = await Fixture.CreateAsync(Enumerable.Range(0, 300).Select(i => $"answer-{i}").ToArray());
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancelOnWriteStream(cancellation);

        var act = () => fixture.Export().WriteAsync(new ExportFilter(new ReportFilter(fixture.ApplicationId),
            IncludeAnswers: false), stream, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var rows = ParseCsv(Encoding.UTF8.GetString(stream.ToArray()));
        rows.Count.Should().BeGreaterThan(1).And.BeLessThan(301);
    }

    [SqlServerFact]
    public async Task Export_totals_match_filtered_screen()
    {
        await using var fixture = await Fixture.CreateAsync(new[] { "one", "two", "three" });
        using var stream = new MemoryStream();
        var filter = new ReportFilter(fixture.ApplicationId);

        await fixture.Export().WriteAsync(new ExportFilter(filter, IncludeAnswers: false), stream);
        var overview = await fixture.Queries().GetOverviewAsync(filter);
        var rows = ParseCsv(Encoding.UTF8.GetString(stream.ToArray()));

        (rows.Count - 1).Should().Be(overview.SubmittedCount);
        rows[0].Should().Contain("Version ID").And.Contain("Submitted UTC");
    }

    private static List<string[]> ParseCsv(string csv)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < csv.Length; index++)
        {
            var current = csv[index];
            if (quoted)
            {
                if (current == '"' && index + 1 < csv.Length && csv[index + 1] == '"') { cell.Append('"'); index++; }
                else if (current == '"') quoted = false;
                else cell.Append(current);
                continue;
            }
            if (current == '"' && cell.Length == 0) { quoted = true; continue; }
            if (current == ',') { row.Add(cell.ToString()); cell.Clear(); continue; }
            if (current == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n')
            { row.Add(cell.ToString()); cell.Clear(); rows.Add(row.ToArray()); row.Clear(); index++; continue; }
            cell.Append(current);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    private sealed class CancelOnWriteStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var write = base.WriteAsync(buffer, offset, count, cancellationToken);
            cancellation.Cancel();
            return write;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var write = base.WriteAsync(buffer, cancellationToken);
            cancellation.Cancel();
            return write;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqlServerDatabase _database;
        private Fixture(SqlServerDatabase database) => _database = database;
        public int ApplicationId { get; private set; }
        public int VersionId { get; private set; }
        public ReportExport Export() => new(new Factory(_database));
        public ReportQueries Queries() => new(new Factory(_database));

        public static async Task<Fixture> CreateAsync(string[] values, int? notApplicableIndex = null, int? unansweredIndex = null)
        {
            var fixture = new Fixture(await SqlServerDatabase.CreateAsync());
            await using var context = fixture._database.CreateContext();
            await context.Database.MigrateAsync();
            var now = new DateTime(2026, 1, 12, 12, 0, 0, DateTimeKind.Utc);
            var category = new Category { Name = "Answers", SortOrder = 1 };
            var element = new Element { Kind = ElementKind.Question, Label = "Applicant's explanation", SortOrder = 1,
                Question = new Question { AnswerType = AnswerType.Textarea } };
            category.Elements.Add(element);
            var version = new ApplicationVersion { VersionNumber = 1, Status = VersionStatus.Published, CreatedUtc = now,
                PublishedUtc = now, DisplayName = "First", Categories = { category } };
            var app = new QuestionAppBuilder.Domain.Entities.Application { Name = "Export test", ApplicationType = new ApplicationType
                { Name = $"RFA-{Guid.NewGuid():N}", SortOrder = 1 }, AcceptNewResponses = true, AcceptSubmissions = true,
                CreatedUtc = now, UpdatedUtc = now, Versions = { version } };
            context.Applications.Add(app);
            await context.SaveChangesAsync();
            app.CurrentVersionId = version.Id;
            fixture.ApplicationId = app.Id;
            fixture.VersionId = version.Id;

            context.Submissions.AddRange(values.Select((value, index) =>
            {
                var submission = new Submission
                {
                    ApplicationId = app.Id, ApplicationVersionId = version.Id, UserId = $"owner-{index}",
                    SubmittedUtc = now.AddMinutes(index), TotalScore = index + 1,
                    QuestionStates = { new SubmissionQuestionState { QuestionId = element.Question.Id,
                        QuestionLogicalKey = element.LogicalKey, IsApplicable = index != notApplicableIndex, PointsEarned = index + 1 } }
                };
                if (index != unansweredIndex)
                    submission.Answers.Add(new Answer { QuestionId = element.Question.Id, TextValue = value, PointsEarned = index + 1 });
                return submission;
            }));
            await context.SaveChangesAsync();
            return fixture;
        }

        public ValueTask DisposeAsync() => _database.DisposeAsync();
    }

    private sealed class Factory(SqlServerDatabase database) : IDbContextFactory<QuestionAppBuilderDbContext>
    {
        public QuestionAppBuilderDbContext CreateDbContext() => database.CreateContext();
        public Task<QuestionAppBuilderDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(database.CreateContext());
    }
}
