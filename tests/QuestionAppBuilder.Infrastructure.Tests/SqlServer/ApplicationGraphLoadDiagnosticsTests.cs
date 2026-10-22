using System.Data.Common;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Services;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Authoring;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Repositories;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public sealed class ApplicationGraphLoadDiagnosticsTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public ApplicationGraphLoadDiagnosticsTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [SqlServerFact]
    public async Task Draft_graph_reads_record_elapsed_time_and_sql_command_counts()
    {
        await using var sql = await SqlServerDatabase.CreateAsync();
        await using (var seed = sql.CreateContext())
        {
            await seed.Database.MigrateAsync();
            var questions = Enumerable.Range(1, 26).Select(index => CreateQuestion(index)).ToArray();
            var elements = questions.Select((question, index) => new Element
            {
                Kind = ElementKind.Question, Label = $"Question {index + 1}", SortOrder = index + 1,
                Question = question
            }).ToList();
            elements.AddRange(Enumerable.Range(27, 2).Select(index => new Element
            {
                Kind = ElementKind.Content, Label = $"Content {index - 26}", SortOrder = index
            }));
            var conditions = Enumerable.Range(0, 2).Select(conditionIndex => new VisibilityCondition
            {
                Junction = ConditionJunction.All,
                Predicates = Enumerable.Range(0, 2).Select(predicateIndex =>
                {
                    var question = questions[(conditionIndex * 4 + predicateIndex) % questions.Length];
                    return new VisibilityPredicate
                    {
                        SourceQuestion = question, Operator = ConditionOperator.HasAnyOption,
                        Options = question.Options.Take(1).Select(option => new VisibilityPredicateOption
                            { QuestionOption = option }).ToList()
                    };
                }).ToList()
            }).ToList();
            var category = new Category { Name = "Diagnostics", SortOrder = 1, Elements = elements };
            var version = new ApplicationVersion
            {
                VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = DateTime.UtcNow,
                DisplayName = "Graph load diagnostics", Categories = [category], VisibilityConditions = conditions
            };
            var application = new QuestionAppBuilder.Domain.Entities.Application
            {
                Name = "Graph diagnostics " + Guid.NewGuid().ToString("N"),
                ApplicationType = new ApplicationType { Name = "Graph diagnostics type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, Versions = [version]
            };
            seed.Applications.Add(application);
            await seed.SaveChangesAsync();
        }

        var metrics = new CommandMetrics();
        var factory = new InterceptedContextFactory(sql, metrics);
        var drafts = new DraftService(factory, new DefinitionEvaluator());
        _ = await drafts.GetAsync(1); // Warm EF model/query compilation before collecting baseline timings.
        metrics.Reset();
        var getElapsed = Stopwatch.StartNew();
        var snapshot = await drafts.GetAsync(1);
        getElapsed.Stop();
        WriteMetrics("DraftService.Get", getElapsed.Elapsed, metrics);
        var getMetrics = AssertSplitCommandShape(metrics);
        snapshot.Definition.Categories.Single().Elements.Should().HaveCount(28);
        snapshot.Definition.Categories.Single().Elements.Count(element => element.Question is not null).Should().Be(26);
        AssertGraphCompleteness(snapshot.Definition);

        _ = await drafts.ReviewAsync(1);
        metrics.Reset();
        var reviewElapsed = Stopwatch.StartNew();
        _ = await drafts.ReviewAsync(1);
        reviewElapsed.Stop();
        WriteMetrics("DraftService.Review", reviewElapsed.Elapsed, metrics);
        AssertSplitCommandShape(metrics);

        _ = await drafts.GetWorkspaceAsync(1);
        metrics.Reset();
        var workspaceElapsed = Stopwatch.StartNew();
        var workspace = await drafts.GetWorkspaceAsync(1);
        workspaceElapsed.Stop();
        WriteMetrics("DraftService.GetWorkspace", workspaceElapsed.Elapsed, metrics);
        var workspaceMetrics = AssertSplitCommandShape(metrics);
        workspaceMetrics.Count.Should().Be(getMetrics.Count, "the combined workspace should materialize the graph once");
        workspace.Draft.Definition.Categories.Single().Elements.Should().HaveCount(28);
        workspace.Review.Revision.Should().Equal(workspace.Draft.Revision);
        AssertGraphCompleteness(workspace.Draft.Definition);

        var categoryId = snapshot.Definition.Categories.Single().Id;
        var revision = snapshot.Revision;
        await using (var warmupContext = sql.CreateContext(metrics))
        {
            var warmup = new ApplicationDesignService(new ApplicationRepository(warmupContext));
            await warmup.AddQuestionAsync(categoryId, "Warmup", AnswerType.Textbox, ct: default, expectedRevision: revision);
        }
        metrics.Reset();
        var addElapsed = Stopwatch.StartNew();
        await using (var writeContext = sql.CreateContext(metrics))
        {
            var design = new ApplicationDesignService(new ApplicationRepository(writeContext));
            await design.AddQuestionAsync(categoryId, "Measured", AnswerType.Textbox, ct: default);
        }
        addElapsed.Stop();
        WriteMetrics("ApplicationDesign.AddQuestion", addElapsed.Elapsed, metrics);
        AssertSplitCommandShape(metrics);

        Func<Task> staleEdit = () => drafts.SaveAsync(new SaveDraftRequest(1, snapshot.Revision, snapshot.Definition));
        await staleEdit.Should().ThrowAsync<DraftConflictException>();

        await using var transactionContext = sql.CreateContext();
        await using var callerTransaction = await transactionContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        var callerGraph = await new ApplicationRepository(transactionContext).GetDraftGraphAsync(1);
        callerGraph.Should().NotBeNull();
        AssertGraphCompleteness(callerGraph!, expectedQuestionCount: 28);
        transactionContext.Database.CurrentTransaction.Should().BeSameAs(callerTransaction);
        await callerTransaction.RollbackAsync();
    }

    private void WriteMetrics(string operation, TimeSpan elapsed, CommandMetrics metrics)
    {
        var result = metrics.GetResult();
        var line = $"{operation}: elapsed={elapsed.TotalMilliseconds:F1}ms, commands={result.Count}, dbTime={result.DatabaseTime.TotalMilliseconds:F1}ms, sqlChars={result.SqlCharacters}";
        _output.WriteLine(line);
        Console.WriteLine(line);
    }

    private static (int Count, TimeSpan DatabaseTime, long SqlCharacters, string[] Commands) AssertSplitCommandShape(CommandMetrics metrics)
    {
        var result = metrics.GetResult();
        result.Count.Should().BeGreaterThan(1).And.BeLessThan(15);
        result.Commands.Should().OnlyContain(sql => !(sql.Contains("LEFT JOIN [Categories]", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("LEFT JOIN [VisibilityConditions]", StringComparison.OrdinalIgnoreCase)));
        result.Commands.Max(sql => sql.Length).Should().BeLessThan(5000);
        return result;
    }

    private static Question CreateQuestion(int questionNumber)
    {
        var options = Enumerable.Range(1, 2).Select(optionNumber => new QuestionOption
        {
            Label = $"Question {questionNumber} option {optionNumber}", Value = optionNumber.ToString(), SortOrder = optionNumber
        }).ToList();
        return new Question
        {
            AnswerType = AnswerType.Checkboxes, Options = options,
            Rules = Enumerable.Range(1, 2).Select(ruleNumber => new ScoringRule
            {
                Family = RuleFamily.Selection, MembershipOperator = MembershipOperator.AnyOf,
                Points = ruleNumber, SortOrder = ruleNumber,
                Options = options.Take(1).Select(option => new ScoringRuleOption { QuestionOption = option }).ToList()
            }).ToList()
        };
    }

    private static void AssertGraphCompleteness(QuestionAppBuilder.Domain.Graphs.ApplicationVersionGraph graph, int expectedQuestionCount = 26)
    {
        var questions = graph.Categories.SelectMany(category => category.Elements)
            .Where(element => element.Question is not null).Select(element => element.Question!).ToArray();
        questions.Should().HaveCount(expectedQuestionCount);
        questions.Sum(question => question.Options.Count).Should().Be(52);
        questions.Sum(question => question.Rules.Count).Should().Be(52);
        questions.Sum(question => question.Rules.Sum(rule => rule.Options.Count)).Should().Be(52);
        foreach (var question in questions)
        foreach (var rule in question.Rules)
        foreach (var option in rule.Options)
        {
            question.Options.Select(actual => actual.Id).Should().Contain(option.QuestionOptionId);
        }

        graph.VisibilityConditions.Should().HaveCount(2);
        graph.VisibilityConditions.Sum(condition => condition.Predicates.Count).Should().Be(4);
        graph.VisibilityConditions.Sum(condition => condition.Predicates.Sum(predicate => predicate.Options.Count)).Should().Be(4);
        foreach (var predicate in graph.VisibilityConditions.SelectMany(condition => condition.Predicates))
        foreach (var option in predicate.Options)
        {
            questions.SelectMany(question => question.Options).Select(actual => actual.Id).Should().Contain(option.QuestionOptionId);
        }
    }

    private sealed class InterceptedContextFactory(SqlServerDatabase database, CommandMetrics metrics)
        : IDbContextFactory<QuestionAppBuilderDbContext>
    {
        public QuestionAppBuilderDbContext CreateDbContext() => database.CreateContext(metrics);
        public Task<QuestionAppBuilderDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(database.CreateContext(metrics));
    }

    private sealed class CommandMetrics : DbCommandInterceptor
    {
        private readonly object _gate = new();
        private int _count;
        private TimeSpan _databaseTime;
        private long _sqlCharacters;
        private readonly List<string> _commands = [];

        public void Reset()
        {
            lock (_gate) { _count = 0; _databaseTime = TimeSpan.Zero; _sqlCharacters = 0; _commands.Clear(); }
        }

        public (int Count, TimeSpan DatabaseTime, long SqlCharacters, string[] Commands) GetResult()
        {
            lock (_gate) return (_count, _databaseTime, _sqlCharacters, _commands.ToArray());
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            Record(command, eventData.Duration);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            Record(command, eventData.Duration);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            object? result, CancellationToken cancellationToken = default)
        {
            Record(command, eventData.Duration);
            return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command, TimeSpan duration)
        {
            lock (_gate) { _count++; _databaseTime += duration; _sqlCharacters += command.CommandText.Length; _commands.Add(command.CommandText); }
        }
    }
}
