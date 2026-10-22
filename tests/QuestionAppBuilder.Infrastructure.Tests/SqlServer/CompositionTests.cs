using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Composition;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Reporting;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Infrastructure.Authoring;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Reporting;
using QuestionAppBuilder.Infrastructure.Repositories;
using QuestionAppBuilder.Infrastructure.Responses;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public class CompositionTests
{
    [SqlServerFact]
    public async Task Budget_composition_survives_draft_publish_submission_report_and_csv_export()
    {
        await using var sql = await SqlServerDatabase.CreateAsync();
        await using var context = sql.CreateContext();
        await context.Database.MigrateAsync();

        var requested = new Element
        {
            Kind = ElementKind.Question, Label = "Requested funds", SortOrder = 1,
            Question = new Question
            {
                AnswerType = AnswerType.Number, IsRequired = true, WholeNumbersOnly = true, MinValue = 0,
                MaxValue = 1000,
                Rules = [new ScoringRule
                {
                    Family = RuleFamily.NumericRange, Points = 0, MinValue = 0, MaxValue = 1000,
                    AwardMethod = AwardMethod.PointsPerUnit, PointsPerUnit = 2, MaximumPoints = 500
                }]
            }
        };
        var match = new Element
        {
            Kind = ElementKind.Question, Label = "Matching funds", SortOrder = 2,
            Question = new Question { AnswerType = AnswerType.Number, IsRequired = true }
        };
        var category = new Category { Name = "Budget", SortOrder = 1, Elements = [requested, match] };
        var version = new ApplicationVersion
        {
            VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = DateTime.UtcNow,
            DisplayName = "Budget pilot", ResultVisibility = ResultVisibility.Total,
            Categories = [category]
        };
        var application = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "Budget composition " + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "Composition type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            AcceptNewResponses = true, AcceptSubmissions = true, Versions = [version]
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync();

        var factory = new TestContextFactory(sql);
        var evaluator = new DefinitionEvaluator();
        var drafts = new DraftService(factory, evaluator, actor: new TestActorContext());
        var initial = await drafts.GetAsync(application.Id);
        var requestedKey = requested.LogicalKey;
        var matchKey = match.LogicalKey;
        var totalId = Guid.NewGuid();
        var definition = new CompositionDefinition
        {
            Groups =
            [
                new QuestionGroup
                {
                    Name = "Funding sources", CategoryKey = category.LogicalKey,
                    Columns = ["Source", "Amount"],
                    Rows =
                    [
                        new GroupRow { QuestionKey = requestedKey, Cells = ["Requested", "Requested funds"] },
                        new GroupRow { QuestionKey = matchKey, Cells = ["Match", "Matching funds"] }
                    ]
                }
            ],
            Calculations =
            [
                new CalculatedField
                {
                    Id = totalId, Name = "Total budget", CategoryKey = category.LogicalKey,
                    Operation = CalculationOperation.Sum,
                    Sources =
                    [
                        new ValueSource { Kind = SourceKind.Question, Key = requestedKey },
                        new ValueSource { Kind = SourceKind.Question, Key = matchKey }
                    ]
                }
            ],
            Rules =
            [
                new CompositionRule
                {
                    Name = "Budget ceiling", CategoryKey = category.LogicalKey,
                    Kind = RulePurpose.Validation, Message = "The total budget must not exceed $1,000.",
                    Checks =
                    [
                        new ValueCheck
                        {
                            Left = new ValueSource { Kind = SourceKind.Calculation, Key = totalId },
                            Comparison = Comparison.LessThanOrEqual,
                            Right = new ValueSource { Kind = SourceKind.Constant, Value = 1000 }
                        }
                    ]
                }
            ]
        };
        initial.Definition.Version.CompositionJson = definition.ToJson();
        var saved = await drafts.SaveAsync(new SaveDraftRequest(application.Id, initial.Revision, initial.Definition));

        var reloaded = await new DraftService(factory, evaluator).GetAsync(application.Id);
        CompositionDefinition.Read(reloaded.Definition.Version.CompositionJson).Calculations
            .Should().ContainSingle().Which.Name.Should().Be("Total budget");
        reloaded.Definition.Categories.Single().Elements.Single(e => e.LogicalKey == requestedKey).Question!.Rules
            .Should().ContainSingle().Which.Should().Match<ScoringRule>(rule =>
                rule.AwardMethod == AwardMethod.PointsPerUnit && rule.PointsPerUnit == 2 && rule.MaximumPoints == 500);
        var review = await drafts.ReviewAsync(application.Id);
        review.Errors.Should().BeEmpty();
        review.Warnings.Should().BeEmpty();

        await drafts.PublishAsync(new PublishRequest(application.Id, saved.Revision, new HashSet<string>()));
        await using (var publishedContext = sql.CreateContext())
        {
            (await publishedContext.Applications.SingleAsync(a => a.Id == application.Id)).CurrentVersionId.Should().Be(version.Id);
            (await publishedContext.ApplicationVersions.Where(v => v.Id == version.Id).Select(v => v.Status).SingleAsync())
                .Should().Be(VersionStatus.Published);
        }

        var identity = new TestRespondentContext(new RespondentIdentity("budget-owner", null, null));
        var responses = new ResponseService(factory, identity);
        var started = await responses.StartAsync(application.Id);
        var draft = await responses.SaveAsync(new SaveResponseRequest(started.Id, started.Revision,
            new Dictionary<int, AnswerValue>
            {
                [requested.Question!.Id] = new AnswerValue(null, 400, null),
                [match.Question!.Id] = new AnswerValue(null, 250, null)
            }));
        var coordinator = new SubmissionCoordinator(factory, identity, evaluator);
        var receipt = await coordinator.SubmitAsync(new SubmitResponseRequest(draft.Id, draft.Revision, Guid.NewGuid()));

        await using (var submittedContext = sql.CreateContext())
        {
            var snapshotJson = await submittedContext.Submissions.Where(s => s.Id == receipt.SubmissionId)
                .Select(s => s.CompositionSnapshotJson).SingleAsync();
            snapshotJson.Should().NotBeNullOrWhiteSpace();
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<CompositionResult>(snapshotJson!);
            snapshot!.Values.Should().ContainSingle().Which.Value.Should().Be(650);
            snapshot.Values.Single().Name.Should().Be("Total budget");
        }

        receipt.Total.Should().Be(500);

        var reports = new ReportQueries(factory);
        var report = await reports.GetDetailAsync(receipt.SubmissionId);
        report!.CompositionSnapshotJson.Should().Contain("Total budget").And.Contain("650");
        report.Questions.Single(q => q.Label == "Requested funds").PointsEarned.Should().Be(500);
        report.RuleTraces.Should().ContainSingle().Which.Should().Match<SubmissionRuleDetail>(trace =>
            trace.Matches && trace.Contributes && trace.Points == 500);
        using var csv = new MemoryStream();
        await new ReportExport(factory).WriteAsync(new ExportFilter(
            new ReportFilter(application.Id, version.Id), IncludeAnswers: true), csv);
        var csvText = Encoding.UTF8.GetString(csv.ToArray());
        csvText.Should().Contain("Total budget").And.Contain("calculated").And.Contain("650");
    }

    [SqlServerFact]
    public async Task Category_copy_remaps_owned_composition_and_deletion_prunes_owned_items_only()
    {
        await using var sql = await SqlServerDatabase.CreateAsync();
        await using var context = sql.CreateContext();
        await context.Database.MigrateAsync();
        var sourceQuestion = new Element { Kind = ElementKind.Question, Label = "Requested", SortOrder = 1,
            Question = new Question
            {
                AnswerType = AnswerType.Number, WholeNumbersOnly = true, MinValue = 0, MaxValue = 100,
                Rules = [new ScoringRule
                {
                    Family = RuleFamily.NumericRange, PointsPerUnit = 3, MaximumPoints = 120,
                    AwardMethod = AwardMethod.PointsPerUnit, MinValue = 0, MaxValue = 100
                }]
            } };
        var otherQuestion = new Element { Kind = ElementKind.Question, Label = "Match", SortOrder = 1,
            Question = new Question { AnswerType = AnswerType.Number } };
        var sourceCategory = new Category { Name = "Source budget", SortOrder = 1, Elements = [sourceQuestion] };
        var otherCategory = new Category { Name = "Other budget", SortOrder = 2, Elements = [otherQuestion] };
        var version = new ApplicationVersion
        {
            VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = DateTime.UtcNow,
            DisplayName = "Category composition test", Categories = [sourceCategory, otherCategory]
        };
        var application = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "Composition copy " + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "Composition copy type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, Versions = [version]
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync();

        var ownedCalculationId = Guid.NewGuid();
        var dependencyCalculationId = Guid.NewGuid();
        version.CompositionJson = new CompositionDefinition
        {
            Groups = [new QuestionGroup { Name = "Source layout", CategoryKey = sourceCategory.LogicalKey,
                Columns = ["Line"], Rows = [new GroupRow { QuestionKey = sourceQuestion.LogicalKey, Cells = ["Requested amount"] }] }],
            Calculations =
            [
                new CalculatedField { Id = ownedCalculationId, Name = "Source total", CategoryKey = sourceCategory.LogicalKey,
                    Sources = [new ValueSource { Kind = SourceKind.Question, Key = sourceQuestion.LogicalKey }] },
                new CalculatedField { Id = dependencyCalculationId, Name = "Cross category dependency", CategoryKey = otherCategory.LogicalKey,
                    Sources = [new ValueSource { Kind = SourceKind.Question, Key = sourceQuestion.LogicalKey }] }
            ],
            Rules = [new CompositionRule { Name = "Source rule", CategoryKey = sourceCategory.LogicalKey,
                Kind = RulePurpose.Validation, Message = "Source rule failed.", ShowWhileAnswering = true, Checks = [new ValueCheck
                {
                    Left = new ValueSource { Kind = SourceKind.Calculation, Key = ownedCalculationId },
                    Comparison = Comparison.GreaterThanOrEqual, Right = new ValueSource { Kind = SourceKind.Constant, Value = 1 }
                }] }]
        }.ToJson();
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var design = new ApplicationDesignService(new ApplicationRepository(context));
        var duplicate = await design.DuplicateCategoryAsync(sourceCategory.Id);
        var copiedQuestion = duplicate.Elements.Single().LogicalKey;
        duplicate.Elements.Single().Question!.Should().Match<Question>(question => question.WholeNumbersOnly
            && question.MinValue == 0 && question.MaxValue == 100 && question.Rules.Count == 1
            && question.Rules[0].AwardMethod == AwardMethod.PointsPerUnit
            && question.Rules[0].PointsPerUnit == 3 && question.Rules[0].MaximumPoints == 120);
        var copiedDefinition = CompositionDefinition.Read((await design.GetDraftGraphAsync(application.Id)).Version.CompositionJson);
        var copiedGroup = copiedDefinition.Groups.Single(group => group.CategoryKey == duplicate.LogicalKey);
        copiedGroup.Rows.Should().ContainSingle().Which.QuestionKey.Should().Be(copiedQuestion);
        var copiedCalculation = copiedDefinition.Calculations.Single(calculation => calculation.CategoryKey == duplicate.LogicalKey);
        copiedCalculation.Id.Should().NotBe(ownedCalculationId);
        copiedCalculation.Sources.Single().Key.Should().Be(copiedQuestion);
        var copiedRule = copiedDefinition.Rules.Single(rule => rule.CategoryKey == duplicate.LogicalKey);
        copiedRule.Checks.Single().Left.Key.Should().Be(copiedCalculation.Id);
        copiedRule.ShowWhileAnswering.Should().BeTrue("category duplication must preserve opt-in live validation");

        context.ChangeTracker.Clear();
        await design.RemoveCategoryAsync(sourceCategory.Id);
        var afterDelete = CompositionDefinition.Read((await design.GetDraftGraphAsync(application.Id)).Version.CompositionJson);
        afterDelete.Groups.Select(group => group.CategoryKey).Should().ContainSingle().Which.Should().Be(duplicate.LogicalKey);
        afterDelete.Calculations.Select(calculation => calculation.Id).Should().Contain(dependencyCalculationId)
            .And.Contain(copiedCalculation.Id).And.NotContain(ownedCalculationId);
        afterDelete.Rules.Should().ContainSingle().Which.CategoryKey.Should().Be(duplicate.LogicalKey);
        var review = await new DraftService(new TestContextFactory(sql), new DefinitionEvaluator()).ReviewAsync(application.Id);
        review.Errors.Select(error => error.Message).Should().Contain(message => message.Contains("calculation", StringComparison.OrdinalIgnoreCase));
    }

    [SqlServerFact]
    public async Task Duplicating_category_with_dangling_group_row_returns_a_definition_error()
    {
        await using var sql = await SqlServerDatabase.CreateAsync();
        await using var context = sql.CreateContext();
        await context.Database.MigrateAsync();
        var question = new Element { Kind = ElementKind.Question, Label = "Amount", SortOrder = 1,
            Question = new Question { AnswerType = AnswerType.Number } };
        var category = new Category { Name = "Budget", SortOrder = 1, Elements = [question] };
        var version = new ApplicationVersion { VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = DateTime.UtcNow,
            DisplayName = "Dangling composition", Categories = [category] };
        var app = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "Dangling composition " + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "Dangling type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, Versions = [version]
        };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        version.CompositionJson = new CompositionDefinition
        {
            Groups = [new QuestionGroup { Name = "Broken group", CategoryKey = category.LogicalKey,
                Columns = ["Description"], Rows = [new GroupRow { QuestionKey = Guid.NewGuid(), Cells = ["Missing"] }] }]
        }.ToJson();
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var design = new ApplicationDesignService(new ApplicationRepository(context));

        Func<Task> duplicate = () => design.DuplicateCategoryAsync(category.Id);

        await duplicate.Should().ThrowAsync<DefinitionValidationException>()
            .WithMessage("*question group*missing or out-of-category question*");
    }

    private sealed class TestContextFactory(SqlServerDatabase database) : IDbContextFactory<QuestionAppBuilderDbContext>
    {
        public QuestionAppBuilderDbContext CreateDbContext() => database.CreateContext();
        public Task<QuestionAppBuilderDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(database.CreateContext());
    }

    private sealed class TestActorContext : ITrustedActorContext
    {
        public string GetRequiredActorKey() => "composition-test-designer";
    }

    private sealed class TestRespondentContext(RespondentIdentity identity) : IRespondentContext
    {
        public RespondentIdentity GetRequiredIdentity() => identity;
    }
}
