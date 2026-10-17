using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public class DefinitionSchemaTests
{
    [SqlServerFact]
    public async Task Definition_settings_round_trip()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var app = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = $"Definition {Guid.NewGuid():N}", ApplicationTypeId = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
        };
        app.Versions.Add(new ApplicationVersion
        {
            VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = DateTime.UtcNow,
            NavigationMode = NavigationMode.Free, RespondentAccessMode = RespondentAccessMode.EmailLink,
            ResultVisibility = ResultVisibility.CategoryBreakdown
        });
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var loaded = await context.ApplicationVersions.SingleAsync(v => v.ApplicationId == app.Id);
        loaded.NavigationMode.Should().Be(NavigationMode.Free);
        loaded.RespondentAccessMode.Should().Be(RespondentAccessMode.EmailLink);
        loaded.ResultVisibility.Should().Be(ResultVisibility.CategoryBreakdown);
    }

    [SqlServerFact]
    public async Task Exactly_one_draft_rejects_a_second_draft()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var app = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = $"Draft {Guid.NewGuid():N}", ApplicationTypeId = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            Versions = { new ApplicationVersion { VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = DateTime.UtcNow } }
        };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        context.ApplicationVersions.Add(new ApplicationVersion { ApplicationId = app.Id, VersionNumber = 2, Status = VersionStatus.Draft, CreatedUtc = DateTime.UtcNow });
        Func<Task> duplicate = () => context.SaveChangesAsync();
        await duplicate.Should().ThrowAsync<DbUpdateException>();
    }

    [SqlServerFact]
    public async Task Condition_foreign_keys_reject_missing_sources()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var app = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = $"Condition {Guid.NewGuid():N}", ApplicationTypeId = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            Versions = { new ApplicationVersion { VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = DateTime.UtcNow } }
        };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var version = await context.ApplicationVersions.SingleAsync(v => v.ApplicationId == app.Id);
        context.VisibilityConditions.Add(new VisibilityCondition
        {
            ApplicationVersionId = version.Id,
            Predicates = { new VisibilityPredicate { SourceQuestionId = int.MaxValue, Operator = ConditionOperator.Answered } }
        });
        Func<Task> invalid = () => context.SaveChangesAsync();
        await invalid.Should().ThrowAsync<DbUpdateException>();
    }

    [SqlServerFact]
    public async Task Migration_preserves_existing_versions()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync("20261015183053_InitialCreate");
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO Applications (Name, ApplicationTypeId, CreatedUtc, UpdatedUtc) VALUES ('Legacy', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO ApplicationVersions (ApplicationId, VersionNumber, Status, CreatedUtc) VALUES (1, 1, 'Draft', SYSUTCDATETIME());
            INSERT INTO Categories (ApplicationVersionId, Name, SortOrder, IsIndependent) VALUES (1, 'Legacy category', 1, 1);
            INSERT INTO Elements (CategoryId, Kind, Label, SortOrder) VALUES (1, 'Question', 'Legacy number', 1);
            INSERT INTO Questions (ElementId, AnswerType, IsRequired) VALUES (1, 'Number', 1);
            INSERT INTO ScoringRules (QuestionId, Family, Points, MembershipOperator, MinValue, MaxValue) VALUES (1, 'NumericRange', 2, NULL, 10, 20);
            """);
        await context.Database.MigrateAsync();
        var loaded = await context.ApplicationVersions
            .Include(v => v.Categories).ThenInclude(c => c.Elements).ThenInclude(e => e.Question).ThenInclude(q => q!.Rules)
            .SingleAsync(v => v.ApplicationId == 1);
        loaded.NavigationMode.Should().Be(NavigationMode.Sequential);
        loaded.ResultVisibility.Should().Be(ResultVisibility.Total);
        loaded.DisplayName.Should().Be("Legacy");
        var question = loaded.Categories.Single().Elements.Single().Question!;
        question.ScoringMode.Should().Be(ScoringMode.Additive);
        question.Rules.Single().MinInclusive.Should().BeTrue();
        question.Rules.Single().MaxInclusive.Should().BeTrue();
        loaded.Categories.Single().LogicalKey.Should().NotBeEmpty();
    }
}
