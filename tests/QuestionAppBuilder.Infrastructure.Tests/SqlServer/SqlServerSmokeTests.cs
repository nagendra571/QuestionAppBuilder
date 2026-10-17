using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Tests.SqlServer;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public class SqlServerSmokeTests
{
    [SqlServerFact]
    public async Task Migrations_apply_and_seed()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var names = await context.Database.SqlQuery<string>($"SELECT [Name] FROM [ApplicationTypes]").ToListAsync();
        names.Should().Contain("Request For Application (RFA)");
        names.Should().Contain("Request For Information (RFI)");
    }

    [SqlServerFact]
    public async Task Duplicate_application_name_violates_unique_index()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var now = DateTime.UtcNow;
        context.Applications.Add(NewApplication("Smoke Duplicate", now));
        await context.SaveChangesAsync();
        context.Applications.Add(NewApplication("Smoke Duplicate", now));
        Func<Task> act = () => context.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [SqlServerFact]
    public async Task RowVersion_blocks_lost_update()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            await setup.Database.MigrateAsync();
            var now = DateTime.UtcNow;
            var application = NewApplication("Smoke RowVersion", now);
            setup.Applications.Add(application);
            await setup.SaveChangesAsync();
            await using var first = database.CreateContext();
            await using var second = database.CreateContext();
            var firstCopy = await first.Applications.SingleAsync(a => a.Id == application.Id);
            var secondCopy = await second.Applications.SingleAsync(a => a.Id == application.Id);
            firstCopy.Name = "Smoke RowVersion (first writer)";
            secondCopy.Name = "Smoke RowVersion (second writer)";
            await first.SaveChangesAsync();
            Func<Task> act = () => second.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        }
    }

    [SqlServerFact]
    public async Task Enums_persist_as_strings()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var now = DateTime.UtcNow;
        var application = NewApplication("Smoke Enum", now);
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        context.ApplicationVersions.Add(new ApplicationVersion
        {
            ApplicationId = application.Id, VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = now
        });
        await context.SaveChangesAsync();
        var statuses = await context.Database.SqlQuery<string>($"SELECT [Status] FROM [ApplicationVersions]").ToListAsync();
        statuses.Should().ContainSingle().Which.Should().Be("Draft");
    }

    private static ApplicationEntity NewApplication(string name, DateTime now) => new()
    {
        Name = name, ApplicationTypeId = 1, CreatedUtc = now, UpdatedUtc = now
    };
}
