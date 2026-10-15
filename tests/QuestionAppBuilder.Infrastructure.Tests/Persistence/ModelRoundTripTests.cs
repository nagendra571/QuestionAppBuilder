using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Infrastructure.Tests.Persistence;

public class ModelRoundTripTests
{
    [Fact]
    public void Model_enforces_length_limits()
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>()
            .UseSqlServer("Server=(localdb)\\QuestionAppBuilderModelTests;Database=QuestionAppBuilderModelTests")
            .Options;

        using var context = new QuestionAppBuilderDbContext(options);

        var model = context.Model;

        model.FindEntityType(typeof(Application))!.FindProperty(nameof(Application.Name))!
            .GetMaxLength().Should().Be(200);
        model.FindEntityType(typeof(ApplicationType))!.FindProperty(nameof(ApplicationType.Name))!
            .GetMaxLength().Should().Be(100);
        model.FindEntityType(typeof(Element))!.FindProperty(nameof(Element.Label))!
            .GetMaxLength().Should().Be(500);
        model.FindEntityType(typeof(Element))!.FindProperty(nameof(Element.HelpText))!
            .GetMaxLength().Should().Be(2000);
        model.FindEntityType(typeof(QuestionOption))!.FindProperty(nameof(QuestionOption.Label))!
            .GetMaxLength().Should().Be(200);
        model.FindEntityType(typeof(QuestionOption))!.FindProperty(nameof(QuestionOption.Value))!
            .GetMaxLength().Should().Be(100);
    }

    [Fact]
    public void Graph_round_trips()
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>()
            .UseInMemoryDatabase($"graph-round-trips-{Guid.NewGuid():N}")
            .Options;

        var now = DateTime.UtcNow;
        int versionId;

        using (var context = new QuestionAppBuilderDbContext(options))
        {
            var application = new Application
            {
                Name = "Annual Grant Survey",
                ApplicationTypeId = 1,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            context.Applications.Add(application);
            context.SaveChanges();

            var version = new ApplicationVersion
            {
                ApplicationId = application.Id,
                VersionNumber = 1,
                Status = VersionStatus.Draft,
                CreatedUtc = now
            };

            var category = new Category
            {
                Name = "Eligibility",
                SortOrder = 1,
                IsIndependent = true
            };

            category.Elements.Add(new Element
            {
                Kind = ElementKind.Question,
                Label = "Are you a registered vendor?",
                HelpText = "Select the option that applies.",
                SortOrder = 1,
                Question = new Question
                {
                    AnswerType = AnswerType.Dropdown,
                    IsRequired = true,
                    Options =
                    {
                        new QuestionOption { Label = "Yes", Value = "yes", SortOrder = 1 },
                        new QuestionOption { Label = "No", Value = "no", SortOrder = 2 }
                    },
                    Rules =
                    {
                        new ScoringRule { Family = RuleFamily.Presence, Points = 1 },
                        new ScoringRule
                        {
                            Family = RuleFamily.NumericRange,
                            Points = 2,
                            MinValue = 10.5m,
                            MaxValue = 25.25m
                        }
                    }
                }
            });

            category.Elements.Add(new Element
            {
                Kind = ElementKind.Content,
                Label = "Please answer honestly.",
                SortOrder = 2
            });

            version.Categories.Add(category);
            context.ApplicationVersions.Add(version);
            context.SaveChanges();

            versionId = version.Id;
        }

        using (var context = new QuestionAppBuilderDbContext(options))
        {
            var reloaded = context.ApplicationVersions
                .Include(v => v.Categories)
                    .ThenInclude(c => c.Elements)
                        .ThenInclude(e => e.Question)
                            .ThenInclude(q => q!.Options)
                .Include(v => v.Categories)
                    .ThenInclude(c => c.Elements)
                        .ThenInclude(e => e.Question)
                            .ThenInclude(q => q!.Rules)
                .Single(v => v.Id == versionId);

            reloaded.Categories.Should().HaveCount(1);

            var reloadedCategory = reloaded.Categories.Single();
            reloadedCategory.IsIndependent.Should().BeTrue();
            reloadedCategory.Elements.Should().HaveCount(2);

            var questionElement = reloadedCategory.Elements.Single(e => e.Kind == ElementKind.Question);
            var contentElement = reloadedCategory.Elements.Single(e => e.Kind == ElementKind.Content);
            contentElement.Question.Should().BeNull();

            var reloadedQuestion = questionElement.Question;
            reloadedQuestion.Should().NotBeNull();
            reloadedQuestion!.Options.Should().HaveCount(2);
            reloadedQuestion.Options.Select(o => o.Value).Should().BeEquivalentTo(new[] { "yes", "no" });
            reloadedQuestion.Rules.Should().HaveCount(2);

            var numericRule = reloadedQuestion.Rules.Single(r => r.Family == RuleFamily.NumericRange);
            numericRule.MinValue.Should().Be(10.5m);
            numericRule.MaxValue.Should().Be(25.25m);
        }
    }
}
