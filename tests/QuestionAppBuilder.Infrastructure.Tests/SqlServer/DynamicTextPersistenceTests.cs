using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Composition;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Reporting;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Reporting;
using QuestionAppBuilder.Infrastructure.Responses;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public class DynamicTextPersistenceTests
{
    [SqlServerFact]
    public async Task Date_answer_and_frozen_dynamic_text_survive_submission_receipt_reports_and_csv()
    {
        await using var sql = await SqlServerDatabase.CreateAsync();
        await using var context = sql.CreateContext();
        await context.Database.MigrateAsync();

        var element = new Element
        {
            Kind = ElementKind.Question, Label = "Date of birth", SortOrder = 1,
            Question = new Question { AnswerType = AnswerType.Date, IsRequired = true }
        };
        var optionalElement = new Element
        {
            Kind = ElementKind.Question, Label = "Optional date", SortOrder = 2,
            Question = new Question { AnswerType = AnswerType.Date, IsRequired = false }
        };
        var category = new Category { Name = "Applicant", SortOrder = 1, Elements = [element, optionalElement] };
        var version = new ApplicationVersion
        {
            VersionNumber = 1, Status = VersionStatus.Published, CreatedUtc = DateTime.UtcNow,
            DisplayName = "Dynamic date", ConfirmationText = "Static confirmation",
            ResultVisibility = ResultVisibility.ConfirmationOnly, Categories = [category]
        };
        var application = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "Dynamic date " + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "Dynamic date type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, AcceptNewResponses = true,
            AcceptSubmissions = true, Versions = [version]
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        application.CurrentVersionId = version.Id;

        var ageId = Guid.NewGuid();
        var optionalDateId = Guid.NewGuid();
        version.CompositionJson = new CompositionDefinition
        {
            DynamicText = new DynamicTextDefinition
            {
                TimeZoneId = "UTC",
                Variables = [new DynamicVariable
                {
                    Id = ageId, Name = "Age", Operation = DynamicOperation.Age,
                    Sources = [new DynamicSource { Kind = DynamicSourceKind.Question, Key = element.LogicalKey }, new DynamicSource { Kind = DynamicSourceKind.Today }]
                }, new DynamicVariable
                {
                    Id = optionalDateId, Name = "Optional date value", Operation = DynamicOperation.Value,
                    Sources = [new DynamicSource { Kind = DynamicSourceKind.Question, Key = optionalElement.LogicalKey }]
                }],
                Templates =
                [
                    new DynamicTemplate
                    {
                        Slot = DynamicTextSlot.ElementLabel, OwnerKey = element.LogicalKey,
                        Parts = [new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = "Applicant age: " },
                            new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Variable, Key = ageId }, Fallback = "unavailable" }]
                    },
                    new DynamicTemplate
                    {
                        Slot = DynamicTextSlot.ElementLabel, OwnerKey = optionalElement.LogicalKey,
                        Parts = [new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = "Hidden optional label: " },
                            new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Variable, Key = optionalDateId }, Fallback = "private fallback" }]
                    },
                    new DynamicTemplate
                    {
                        Slot = DynamicTextSlot.ReviewSummary, OwnerKey = Guid.Empty,
                        Parts = [new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = "Age at review: " },
                            new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Variable, Key = ageId }, Fallback = "unavailable" }]
                    },
                    new DynamicTemplate
                    {
                        Slot = DynamicTextSlot.ConfirmationText, OwnerKey = Guid.Empty,
                        Parts = [new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = "Submitted. Age: " },
                            new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Variable, Key = ageId }, Fallback = "unavailable" }]
                    }
                ]
            }
        }.ToJson();
        await context.SaveChangesAsync();

        var factory = new TestContextFactory(sql);
        var identity = new TestRespondentContext(new RespondentIdentity("dynamic-date-owner", null, null));
        var responseService = new ResponseService(factory, identity);
        var started = await responseService.StartAsync(application.Id);
        Func<Task> invalidDate = () => responseService.SaveAsync(new SaveResponseRequest(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [element.Question!.Id] = new AnswerValue("02/29/2000", null, null) }));
        await invalidDate.Should().ThrowAsync<AnswerValidationException>();
        var saved = await responseService.SaveAsync(new SaveResponseRequest(started.Id, started.Revision,
            new Dictionary<int, AnswerValue>
            {
                [element.Question!.Id] = new AnswerValue("2000-02-29", null, null),
                [optionalElement.Question!.Id] = new AnswerValue("2020-01-01", null, null)
            }));
        saved = await responseService.SaveAsync(new SaveResponseRequest(started.Id, saved.Revision,
            new Dictionary<int, AnswerValue>
            {
                [element.Question.Id] = new AnswerValue("2000-02-29", null, null),
                [optionalElement.Question.Id] = new AnswerValue("", null, null)
            }));
        var resumed = await new ResponseService(factory, identity).GetAsync(started.Id);
        resumed.Answers[element.Question.Id].Text.Should().Be("2000-02-29");
        resumed.Answers[element.Question.Id].Number.Should().BeNull();
        resumed.Answers[element.Question.Id].SelectedOptionIds.Should().BeNull();
        resumed.Answers.Should().NotContainKey(optionalElement.Question.Id);

        var coordinator = new SubmissionCoordinator(factory, identity, new DefinitionEvaluator());
        var receipt = await coordinator.SubmitAsync(new SubmitResponseRequest(started.Id, saved.Revision, Guid.NewGuid()));
        receipt.ConfirmationText.Should().StartWith("Submitted. Age: ");
        receipt.ReviewSummary.Should().StartWith("Age at review: ");
        receipt.Total.Should().BeNull();
        typeof(SubmissionReceipt).GetProperty("CompositionSnapshotJson").Should().BeNull();
        receipt.SubmittedUtc.Kind.Should().Be(DateTimeKind.Utc);
        string snapshotJson;
        await using (var snapshotContext = sql.CreateContext())
            snapshotJson = await snapshotContext.Submissions.Where(s => s.Id == receipt.SubmissionId)
                .Select(s => s.CompositionSnapshotJson!).SingleAsync();

        var snapshot = System.Text.Json.JsonSerializer.Deserialize<CompositionResult>(snapshotJson);
        snapshot!.DynamicText.Should().NotBeNull();
        snapshot.DynamicText!.EvaluatedAtUtc.UtcDateTime.Should().Be(receipt.SubmittedUtc);
        snapshot.DynamicText.ReferenceDate.Should().Be(DateOnly.FromDateTime(receipt.SubmittedUtc));
        snapshot.DynamicText.Values.Should().ContainSingle(value => value.Id == ageId)
            .Which.Value.State.Should().Be(DynamicValueState.Present);
        var anniversary = new DateOnly(snapshot.DynamicText.ReferenceDate.Year, 2,
            DateTime.IsLeapYear(snapshot.DynamicText.ReferenceDate.Year) ? 29 : 28);
        var expectedAge = snapshot.DynamicText.ReferenceDate.Year - 2000
            - (snapshot.DynamicText.ReferenceDate < anniversary ? 1 : 0);
        snapshot.DynamicText.Values.Single(value => value.Id == ageId).Value.Number.Should().Be(expectedAge);
        snapshot.DynamicText.Texts.Should().Contain(text => text.Slot == DynamicTextSlot.ReviewSummary && text.Text.StartsWith("Age at review: "));
        snapshot.DynamicText.Values.Should().ContainSingle(value => value.Id == optionalDateId)
            .Which.Value.State.Should().Be(DynamicValueState.Missing);

        snapshot = snapshot with
        {
            DynamicText = snapshot.DynamicText with
            {
                Texts = snapshot.DynamicText.Texts.Select(text =>
                    (text.Slot == DynamicTextSlot.ConfirmationText && text.OwnerKey == Guid.Empty
                        || text.Slot == DynamicTextSlot.ElementLabel && text.OwnerKey == optionalElement.LogicalKey)
                        ? text with { IsHidden = true } : text).ToArray()
            }
        };
        snapshotJson = System.Text.Json.JsonSerializer.Serialize(snapshot);
        await using (var snapshotContext = sql.CreateContext())
        {
            var stored = await snapshotContext.Submissions.SingleAsync(s => s.Id == receipt.SubmissionId);
            stored.CompositionSnapshotJson = snapshotJson;
            await snapshotContext.SaveChangesAsync();
        }

        var idempotentReceipt = await coordinator.GetReceiptAsync(started.Id);
        idempotentReceipt.ConfirmationText.Should().BeEmpty();
        idempotentReceipt.ReviewSummary.Should().Be(receipt.ReviewSummary);
        idempotentReceipt.SubmittedUtc.Should().Be(receipt.SubmittedUtc);

        var report = await new ReportQueries(factory).GetDetailAsync(receipt.SubmissionId);
        report!.Questions.Should().HaveCount(2);
        report.Questions.Should().Contain(question => question.Label.StartsWith("Applicant age: "));
        report.Questions.Should().Contain(question => question.Label.Length == 0);
        report.CompositionSnapshotJson.Should().Be(snapshotJson);
        using var csv = new MemoryStream();
        await new ReportExport(factory).WriteAsync(new ExportFilter(new ReportFilter(application.Id, version.Id), IncludeAnswers: true), csv);
        var csvText = Encoding.UTF8.GetString(csv.ToArray());
        csvText.Should().Contain("Applicant age:").And.Contain("ReviewSummary").And.Contain("dynamic");
        csvText.Should().NotContain("Hidden optional label:");
    }

    [SqlServerFact]
    public async Task Legacy_submission_without_dynamic_snapshot_uses_static_confirmation_text()
    {
        await using var sql = await SqlServerDatabase.CreateAsync();
        await using var context = sql.CreateContext();
        await context.Database.MigrateAsync();
        var element = new Element { Kind = ElementKind.Question, Label = "Name", SortOrder = 1,
            Question = new Question { AnswerType = AnswerType.Textbox, IsRequired = true } };
        var version = new ApplicationVersion { VersionNumber = 1, Status = VersionStatus.Published,
            CreatedUtc = DateTime.UtcNow, DisplayName = "Legacy", ConfirmationText = "Static thanks", Categories =
            [new Category { Name = "Applicant", SortOrder = 1, Elements = [element] }] };
        var application = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "Legacy response " + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "Legacy type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, AcceptNewResponses = true,
            AcceptSubmissions = true, Versions = [version]
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        application.CurrentVersionId = version.Id;
        await context.SaveChangesAsync();

        var factory = new TestContextFactory(sql);
        var identity = new TestRespondentContext(new RespondentIdentity("legacy-owner", null, null));
        var responses = new ResponseService(factory, identity);
        var started = await responses.StartAsync(application.Id);
        var saved = await responses.SaveAsync(new SaveResponseRequest(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [element.Question!.Id] = new AnswerValue("Alex", null, null) }));
        var receipt = await new SubmissionCoordinator(factory, identity, new DefinitionEvaluator())
            .SubmitAsync(new SubmitResponseRequest(started.Id, saved.Revision, Guid.NewGuid()));

        await using (var legacyContext = sql.CreateContext())
        {
            var stored = await legacyContext.Submissions.SingleAsync(s => s.Id == receipt.SubmissionId);
            stored.CompositionSnapshotJson = "{\"Values\":[],\"Awards\":[],\"DefinitionErrors\":[],\"AnswerErrors\":[]}";
            await legacyContext.SaveChangesAsync();
        }
        var legacyReceipt = await new SubmissionCoordinator(factory, identity, new DefinitionEvaluator()).GetReceiptAsync(started.Id);
        legacyReceipt.ConfirmationText.Should().Be("Static thanks");
        legacyReceipt.ReviewSummary.Should().BeNull();
    }

    private sealed class TestContextFactory(SqlServerDatabase database) : IDbContextFactory<QuestionAppBuilderDbContext>
    {
        public QuestionAppBuilderDbContext CreateDbContext() => database.CreateContext();
        public Task<QuestionAppBuilderDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(database.CreateContext());
    }

    private sealed class TestRespondentContext(RespondentIdentity identity) : IRespondentContext
    {
        public RespondentIdentity GetRequiredIdentity() => identity;
    }
}
