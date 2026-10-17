using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public class ResponseSchemaTests
{
    [SqlServerFact]
    public async Task One_submission_per_response_rejects_a_second_insert()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var (application, version) = await SeedPublishedAsync(db);
        var response = new ResponseDraft { Id = Guid.NewGuid(), ApplicationId = application.Id, ApplicationVersionId = version.Id,
            OwnerKey = "owner-1", Status = ResponseStatus.InProgress, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
        db.ResponseDrafts.Add(response);
        await db.SaveChangesAsync();
        db.Submissions.Add(NewSubmission(application.Id, version.Id, response.Id));
        await db.SaveChangesAsync();
        db.Submissions.Add(NewSubmission(application.Id, version.Id, response.Id));

        Func<Task> duplicate = () => db.SaveChangesAsync();

        await duplicate.Should().ThrowAsync<DbUpdateException>();
    }

    [SqlServerFact]
    public async Task Referenced_published_version_cannot_be_deleted()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var (application, version) = await SeedPublishedAsync(db);
        db.ResponseDrafts.Add(new ResponseDraft { Id = Guid.NewGuid(), ApplicationId = application.Id, ApplicationVersionId = version.Id,
            OwnerKey = "owner-1", Status = ResponseStatus.InProgress, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.ApplicationVersions.Remove(await db.ApplicationVersions.SingleAsync(v => v.Id == version.Id));

        Func<Task> remove = () => db.SaveChangesAsync();

        await remove.Should().ThrowAsync<DbUpdateException>();
    }

    [SqlServerFact]
    public async Task Stored_snapshot_keeps_hidden_state_without_answer_value()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var (application, version) = await SeedPublishedAsync(db);
        var question = version.Categories.Single().Elements.Single().Question!;
        var submission = NewSubmission(application.Id, version.Id, null);
        submission.QuestionStates.Add(new SubmissionQuestionState { QuestionId = question.Id, IsApplicable = false, PointsEarned = 0 });
        db.Submissions.Add(submission);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var saved = await db.Submissions.Include(x => x.QuestionStates).Include(x => x.Answers).SingleAsync();

        saved.QuestionStates.Should().ContainSingle().Which.IsApplicable.Should().BeFalse();
        saved.Answers.Should().BeEmpty();
    }

    [SqlServerFact]
    public async Task Response_rowversion_rejects_lost_update()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using (var seed = database.CreateContext())
        {
            await seed.Database.MigrateAsync();
            var (application, version) = await SeedPublishedAsync(seed);
            seed.ResponseDrafts.Add(new ResponseDraft { Id = Guid.NewGuid(), ApplicationId = application.Id, ApplicationVersionId = version.Id,
                OwnerKey = "owner-1", Status = ResponseStatus.InProgress, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }
        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
        var first = await firstContext.ResponseDrafts.SingleAsync();
        var second = await secondContext.ResponseDrafts.SingleAsync();
        first.UpdatedUtc = DateTime.UtcNow.AddMinutes(1);
        await firstContext.SaveChangesAsync();
        second.UpdatedUtc = DateTime.UtcNow.AddMinutes(2);

        Func<Task> stale = () => secondContext.SaveChangesAsync();

        await stale.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    private static Submission NewSubmission(int applicationId, int versionId, Guid? responseId) => new()
    {
        ApplicationId = applicationId, ApplicationVersionId = versionId, ResponseId = responseId,
        SubmittedUtc = DateTime.UtcNow, TotalScore = 0
    };

    private static async Task<(QuestionAppBuilder.Domain.Entities.Application Application, ApplicationVersion Version)> SeedPublishedAsync(QuestionAppBuilder.Infrastructure.Persistence.QuestionAppBuilderDbContext db)
    {
        var app = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = $"Responses {Guid.NewGuid():N}", ApplicationTypeId = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            AcceptNewResponses = true, AcceptSubmissions = true,
            Versions = { new ApplicationVersion { VersionNumber = 1, Status = VersionStatus.Published, CreatedUtc = DateTime.UtcNow, PublishedUtc = DateTime.UtcNow, DisplayName = "Responses" } }
        };
        app.Versions.Single().Categories.Add(new Category
        {
            Name = "Basics", SortOrder = 1,
            Elements = { new Element { Kind = ElementKind.Question, Label = "Name", SortOrder = 1, Question = new Question { AnswerType = AnswerType.Textbox, IsRequired = true } } }
        });
        db.Applications.Add(app);
        await db.SaveChangesAsync();
        app.CurrentVersionId = app.Versions.Single().Id;
        await db.SaveChangesAsync();
        return (app, app.Versions.Single());
    }
}
