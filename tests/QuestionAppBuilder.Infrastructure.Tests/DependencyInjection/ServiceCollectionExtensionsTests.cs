using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.DependencyInjection;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Infrastructure.Tests.DependencyInjection;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void Missing_connection_string_throws()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddQuestionAppBuilder(_ => { });

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task End_to_end_create_publish_submit()
    {
        var services = new ServiceCollection();
        services.AddQuestionAppBuilder(o => o.ConnectionString = "Server=unused");

        var dbContextOptions = services.Single(descriptor => descriptor.ServiceType == typeof(DbContextOptions<QuestionAppBuilderDbContext>));
        services.Remove(dbContextOptions);
        var databaseName = $"di-e2e-{Guid.NewGuid():N}";
        services.AddDbContext<QuestionAppBuilderDbContext>(options => options.UseInMemoryDatabase(databaseName));

        using var provider = services.BuildServiceProvider();

        int applicationTypeId;
        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<QuestionAppBuilderDbContext>();
            var applicationType = new ApplicationType { Name = "RFA", SortOrder = 1 };
            context.ApplicationTypes.Add(applicationType);
            await context.SaveChangesAsync();
            applicationTypeId = applicationType.Id;
        }

        int applicationId, publishedVersionId, draftVersionId, questionId, optionId;
        using (var scope = provider.CreateScope())
        {
            var design = scope.ServiceProvider.GetRequiredService<IApplicationDesignService>();

            var application = await design.CreateApplicationAsync("End to end", applicationTypeId);
            applicationId = application.Id;

            var category = await design.AddCategoryAsync(applicationId, "Eligibility");
            var question = await design.AddQuestionAsync(category.Id, "Are you registered?", AnswerType.Dropdown);
            questionId = question.Id;

            var option = await design.AddOptionAsync(questionId, "Yes", "yes");
            optionId = option.Id;
            await design.AddOptionAsync(questionId, "No", "no");
            await design.AddRuleAsync(questionId, RuleFamily.Selection, 5, MembershipOperator.AnyOf, new[] { optionId });

            publishedVersionId = (await design.GetDraftGraphAsync(applicationId)).Version.Id;
            await design.PublishAsync(applicationId);

            await design.AddCategoryAsync(applicationId, "Next round");
            draftVersionId = (await design.GetDraftGraphAsync(applicationId)).Version.Id;
            draftVersionId.Should().NotBe(publishedVersionId);
        }

        int firstSubmissionId;
        using (var scope = provider.CreateScope())
        {
            var submissions = scope.ServiceProvider.GetRequiredService<ISubmissionService>();

            var submission = await submissions.SubmitAsync(new SubmitRequest(
                applicationId,
                publishedVersionId,
                new Dictionary<int, AnswerValue>
                {
                    [questionId] = new(null, null, new[] { optionId })
                }));

            submission.TotalScore.Should().Be(5);
            submission.ApplicationVersionId.Should().Be(publishedVersionId);
            firstSubmissionId = submission.Id;
        }

        using (var scope = provider.CreateScope())
        {
            var submissions = scope.ServiceProvider.GetRequiredService<ISubmissionService>();

            var second = await submissions.SubmitAsync(new SubmitRequest(
                applicationId,
                publishedVersionId,
                new Dictionary<int, AnswerValue>
                {
                    [questionId] = new(null, null, new[] { optionId })
                }));

            second.Id.Should().NotBe(firstSubmissionId);
            second.TotalScore.Should().Be(5);
            second.ApplicationVersionId.Should().Be(publishedVersionId);
        }

        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<QuestionAppBuilderDbContext>();

            var persisted = await context.Submissions.OrderBy(s => s.Id).ToListAsync();

            persisted.Should().HaveCount(2);
            persisted.Should().OnlyContain(s => s.ApplicationId == applicationId);
            persisted.Should().OnlyContain(s => s.ApplicationVersionId == publishedVersionId);
            persisted.Should().OnlyContain(s => s.TotalScore == 5);
            persisted.Should().NotContain(s => s.ApplicationVersionId == draftVersionId);
        }
    }
}
