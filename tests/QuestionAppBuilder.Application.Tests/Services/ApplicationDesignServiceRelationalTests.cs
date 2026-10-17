using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Repositories;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;

namespace QuestionAppBuilder.Application.Tests.Services;

public class ApplicationDesignServiceRelationalTests
{
    [Fact]
    public async Task OpenDraft_clone_relinks_rule_options_to_cloned_options()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var seed = new RelationalTestDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
        }

        int applicationId;
        int publishedOptionId;

        await using (var context = new RelationalTestDbContext(options))
        {
            var service = new ApplicationDesignService(new ApplicationRepository(context));
            var typeId = await context.ApplicationTypes.Select(t => t.Id).FirstAsync();
            var application = await service.CreateApplicationAsync("Clone links", typeId);
            var category = await service.AddCategoryAsync(application.Id, "Scoring");
            var question = await service.AddQuestionAsync(category.Id, "Scored?", AnswerType.Dropdown);
            var option = await service.AddOptionAsync(question.Id, "Yes", "yes");
            await service.AddRuleAsync(question.Id, RuleFamily.Selection, 5, MembershipOperator.AnyOf, new[] { option.Id });
            await service.PublishAsync(application.Id);

            applicationId = application.Id;
            publishedOptionId = option.Id;

            var draft = await service.OpenDraftAsync(applicationId);
            draft.Version.VersionNumber.Should().Be(2);
        }

        await using var verify = new RelationalTestDbContext(options);
        var draftVersion = await verify.ApplicationVersions
            .SingleAsync(version => version.ApplicationId == applicationId && version.Status == VersionStatus.Draft);

        var stored = await new ApplicationRepository(verify).GetVersionGraphAsync(draftVersion.Id);
        var storedQuestion = stored.Categories.Single().Elements.Single().Question!;
        var clonedOption = storedQuestion.Options.Single();
        var clonedRule = storedQuestion.Rules.Single();

        clonedOption.Id.Should().NotBe(publishedOptionId);
        clonedRule.Options.Single().QuestionOptionId.Should().Be(clonedOption.Id);
    }

    private sealed class RelationalTestDbContext : QuestionAppBuilderDbContext
    {
        public RelationalTestDbContext(DbContextOptions<QuestionAppBuilderDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<ApplicationEntity>()
                .Property(a => a.RowVersion)
                .HasColumnType("BLOB")
                .HasDefaultValueSql("(randomblob(8))");
        }
    }
}
