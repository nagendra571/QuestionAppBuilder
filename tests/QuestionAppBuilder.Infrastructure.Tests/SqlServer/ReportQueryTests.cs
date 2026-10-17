using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Reporting;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Reporting;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public sealed class ReportQueryTests
{
    [SqlServerFact]
    public async Task Submitted_only_overview_excludes_drafts()
    {
        await using var fixture = await Fixture.CreateAsync();

        var overview = await fixture.Queries().GetOverviewAsync(new(fixture.ApplicationId));

        overview.SubmittedCount.Should().Be(3);
        overview.InProgressCount.Should().Be(1);
    }

    [SqlServerFact]
    public async Task Score_distribution_separates_versions()
    {
        await using var fixture = await Fixture.CreateAsync();

        var overview = await fixture.Queries().GetOverviewAsync(new(fixture.ApplicationId));

        overview.Versions.Should().HaveCount(2);
        overview.Versions.Single(version => version.VersionNumber == 1).SubmissionCount.Should().Be(2);
        overview.Versions.Single(version => version.VersionNumber == 2).SubmissionCount.Should().Be(1);
        overview.Versions.Single(version => version.VersionNumber == 1).AverageTotalScore.Should().Be(15m);
        overview.Versions.Single(version => version.VersionNumber == 2).AverageTotalScore.Should().Be(90m);
    }

    [SqlServerFact]
    public async Task Equal_timestamp_cursor_has_no_gaps()
    {
        await using var fixture = await Fixture.CreateAsync();
        var query = fixture.Queries();
        var filter = new ReportFilter(fixture.ApplicationId);
        var ids = new List<int>();
        SubmissionCursor? cursor = null;
        do
        {
            var page = await query.GetPageAsync(filter, cursor, pageSize: 1);
            ids.AddRange(page.Items.Select(item => item.Id));
            cursor = page.NextCursor;
            if (page.Items.Count == 0) break;
        } while (cursor is not null);

        ids.Should().OnlyHaveUniqueItems();
        ids.Should().HaveCount(3);
    }

    [SqlServerFact]
    public async Task Hidden_and_unanswered_have_distinct_detail_states()
    {
        await using var fixture = await Fixture.CreateAsync();

        var detail = await fixture.Queries().GetDetailAsync(fixture.FirstSubmissionId);

        detail.Should().NotBeNull();
        detail!.Questions.Should().ContainSingle(question => question.Label == "Question 1" && question.IsApplicable == true && !question.HasAnswer);
        detail.Questions.Should().ContainSingle(question => question.Label == "Question 2" && question.IsApplicable == false && !question.HasAnswer);
    }

    [SqlServerFact]
    public async Task DST_date_filter_uses_half_open_UTC_range()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddSubmissionsAsync(new DateTime(2026, 3, 8, 4, 59, 59, DateTimeKind.Utc),
            new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 9, 3, 59, 59, DateTimeKind.Utc),
            new DateTime(2026, 3, 9, 4, 0, 0, DateTimeKind.Utc));

        var overview = await fixture.Queries().GetOverviewAsync(new ReportFilter(fixture.ApplicationId,
            FromUtc: new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc),
            ToUtcExclusive: new DateTime(2026, 3, 9, 4, 0, 0, DateTimeKind.Utc)));

        overview.SubmittedCount.Should().Be(2);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqlServerDatabase _sql;
        private Fixture(SqlServerDatabase sql) => _sql = sql;

        public int ApplicationId { get; private set; }
        public int FirstSubmissionId { get; private set; }

        public ReportQueries Queries() => new(new Factory(_sql));

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(await SqlServerDatabase.CreateAsync());
            await using var context = fixture._sql.CreateContext();
            await context.Database.MigrateAsync();
            var now = new DateTime(2026, 1, 12, 12, 0, 0, DateTimeKind.Utc);
            var type = new ApplicationType { Name = $"RFA-{Guid.NewGuid():N}", SortOrder = 1 };
            var versionOne = MakeVersion(1, now, "First release");
            var versionTwo = MakeVersion(2, now, "Second release");
            var app = new QuestionAppBuilder.Domain.Entities.Application
            {
                Name = "Report test application",
                ApplicationType = type,
                AcceptNewResponses = true,
                AcceptSubmissions = true,
                CreatedUtc = now,
                UpdatedUtc = now,
                Versions = { versionOne, versionTwo }
            };
            context.Applications.Add(app);
            await context.SaveChangesAsync();
            app.CurrentVersionId = versionTwo.Id;
            await context.SaveChangesAsync();
            fixture.ApplicationId = app.Id;

            var q1 = versionOne.Categories[0].Elements[0];
            var q2 = versionOne.Categories[0].Elements[1];
            var q3 = versionTwo.Categories[0].Elements[0];
            var first = MakeSubmission(app.Id, versionOne, now, 10, (q1, true), (q2, false));
            var second = MakeSubmission(app.Id, versionOne, now, 20, (q1, true), (q2, true));
            var third = MakeSubmission(app.Id, versionTwo, now, 90, (q3, true));
            context.Submissions.AddRange(first, second, third);
            context.ResponseDrafts.Add(new ResponseDraft
            {
                Id = Guid.NewGuid(), ApplicationId = app.Id, ApplicationVersionId = versionTwo.Id,
                OwnerKey = "test-owner", Status = ResponseStatus.InProgress, CreatedUtc = now, UpdatedUtc = now
            });
            await context.SaveChangesAsync();
            fixture.FirstSubmissionId = first.Id;
            return fixture;
        }

        public async Task AddSubmissionsAsync(params DateTime[] times)
        {
            await using var context = _sql.CreateContext();
            var version = await context.ApplicationVersions.AsNoTracking()
                .Where(version => version.ApplicationId == ApplicationId && version.VersionNumber == 1)
                .SingleAsync();
            context.Submissions.AddRange(times.Select(time => new Submission
            {
                ApplicationId = ApplicationId,
                ApplicationVersionId = version.Id,
                UserId = "test-owner",
                SubmittedUtc = time,
                TotalScore = 10
            }));
            await context.SaveChangesAsync();
        }

        private static ApplicationVersion MakeVersion(int number, DateTime now, string name)
        {
            var category = new Category { Name = "Applicant", SortOrder = 1 };
            category.Elements.Add(new Element
            {
                Kind = ElementKind.Question, Label = "Question 1", SortOrder = 1,
                Question = new Question { AnswerType = AnswerType.Textbox, IsRequired = false }
            });
            if (number == 1)
                category.Elements.Add(new Element
                {
                    Kind = ElementKind.Question, Label = "Question 2", SortOrder = 2,
                    Question = new Question { AnswerType = AnswerType.Textbox, IsRequired = false }
                });
            return new ApplicationVersion
            {
                VersionNumber = number, Status = VersionStatus.Published, CreatedUtc = now, PublishedUtc = now,
                DisplayName = name, Categories = { category }
            };
        }

        private static Submission MakeSubmission(int applicationId, ApplicationVersion version, DateTime now, int score,
            params (Element Element, bool Applicable)[] states)
        {
            var submission = new Submission
            {
                ApplicationId = applicationId, ApplicationVersionId = version.Id, UserId = "test-owner",
                SubmittedUtc = now, TotalScore = score
            };
            foreach (var (element, applicable) in states)
            {
                var question = element.Question!;
                submission.QuestionStates.Add(new SubmissionQuestionState
                {
                    QuestionId = question.Id, QuestionLogicalKey = element.LogicalKey, IsApplicable = applicable,
                    PointsEarned = applicable ? score : 0
                });
            }
            submission.CategoryScores.Add(new QuestionAppBuilder.Domain.Entities.SubmissionCategoryScore
            {
                CategoryId = version.Categories[0].Id, Score = score
            });
            return submission;
        }

        public ValueTask DisposeAsync() => _sql.DisposeAsync();
    }

    private sealed class Factory(SqlServerDatabase database) : IDbContextFactory<QuestionAppBuilderDbContext>
    {
        public QuestionAppBuilderDbContext CreateDbContext() => database.CreateContext();
        public Task<QuestionAppBuilderDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(database.CreateContext());
    }
}
