using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public class SqlServerSmokeTests : IAsyncLifetime
{
    private readonly List<QuestionAppBuilderDbContext> _contexts = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.Database.EnsureDeletedAsync();
            await context.DisposeAsync();
        }
    }

    [Fact]
    public async Task Migrations_apply_and_seed()
    {
        var cs = Environment.GetEnvironmentVariable("QAB_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(cs)) return; // SQL smoke disabled: set QAB_TEST_SQLSERVER to run

        var context = CreateContext(SmokeConnectionString(cs));
        await context.Database.MigrateAsync();

        var names = await context.Database.SqlQuery<string>($"SELECT [Name] FROM [ApplicationTypes]").ToListAsync();

        names.Should().Contain("Request For Application (RFA)");
        names.Should().Contain("Request For Information (RFI)");
    }

    [Fact]
    public async Task Duplicate_application_name_violates_unique_index()
    {
        var cs = Environment.GetEnvironmentVariable("QAB_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(cs)) return; // SQL smoke disabled: set QAB_TEST_SQLSERVER to run

        var context = CreateContext(SmokeConnectionString(cs));
        await context.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        context.Applications.Add(new ApplicationEntity
        {
            Name = "Smoke Duplicate",
            ApplicationTypeId = 1,
            CreatedUtc = now,
            UpdatedUtc = now
        });
        await context.SaveChangesAsync();

        context.Applications.Add(new ApplicationEntity
        {
            Name = "Smoke Duplicate",
            ApplicationTypeId = 1,
            CreatedUtc = now,
            UpdatedUtc = now
        });

        Func<Task> act = () => context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task RowVersion_blocks_lost_update()
    {
        var cs = Environment.GetEnvironmentVariable("QAB_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(cs)) return; // SQL smoke disabled: set QAB_TEST_SQLSERVER to run

        var connectionString = SmokeConnectionString(cs);
        var setup = CreateContext(connectionString);
        await setup.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        var application = new ApplicationEntity
        {
            Name = "Smoke RowVersion",
            ApplicationTypeId = 1,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        setup.Applications.Add(application);
        await setup.SaveChangesAsync();

        var first = CreateContext(connectionString);
        var second = CreateContext(connectionString);
        var firstCopy = await first.Applications.SingleAsync(a => a.Id == application.Id);
        var secondCopy = await second.Applications.SingleAsync(a => a.Id == application.Id);

        firstCopy.Name = "Smoke RowVersion (first writer)";
        secondCopy.Name = "Smoke RowVersion (second writer)";

        await first.SaveChangesAsync();

        Func<Task> act = () => second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task Enums_persist_as_strings()
    {
        var cs = Environment.GetEnvironmentVariable("QAB_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(cs)) return; // SQL smoke disabled: set QAB_TEST_SQLSERVER to run

        var context = CreateContext(SmokeConnectionString(cs));
        await context.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        var application = new ApplicationEntity
        {
            Name = "Smoke Enum",
            ApplicationTypeId = 1,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        context.Applications.Add(application);
        await context.SaveChangesAsync();

        context.ApplicationVersions.Add(new ApplicationVersion
        {
            ApplicationId = application.Id,
            VersionNumber = 1,
            Status = VersionStatus.Draft,
            CreatedUtc = now
        });
        await context.SaveChangesAsync();

        var statuses = await context.Database.SqlQuery<string>($"SELECT [Status] FROM [ApplicationVersions]").ToListAsync();

        statuses.Should().ContainSingle().Which.Should().Be("Draft");
    }

    private static string SmokeConnectionString(string baseConnectionString) =>
        new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = $"QAB_Smoke_{Guid.NewGuid():N}"
        }.ConnectionString;

    private QuestionAppBuilderDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        var context = new QuestionAppBuilderDbContext(options);
        _contexts.Add(context);
        return context;
    }
}
