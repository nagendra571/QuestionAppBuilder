using FluentAssertions;
using QuestionAppBuilder.Application.Composition;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Tests.Evaluation;

public class CompositionBoundaryTests
{
    private static (ApplicationVersionGraph Graph, Element First, Element Second) Graph()
    {
        var first = new Element { Id = 1, Label = "Requested funding", Question = new Question { Id = 1, AnswerType = AnswerType.Number } };
        var second = new Element { Id = 2, Label = "Matching funding", Question = new Question { Id = 2, AnswerType = AnswerType.Number } };
        var category = new Category { Id = 1, Name = "Budget", Elements = [first, second] };
        var version = new ApplicationVersion { Categories = [category] };
        return (new ApplicationVersionGraph { Version = version, Categories = version.Categories }, first, second);
    }
    private static ValueSource Q(Element e) => new() { Kind = SourceKind.Question, Key = e.LogicalKey };
    private static ValueSource C(Guid id) => new() { Kind = SourceKind.Calculation, Key = id };

    [Fact]
    public void Live_validation_is_opt_in_and_legacy_json_defaults_to_off()
    {
        var legacy = CompositionDefinition.Read("""{"Rules":[{"Name":"Legacy","Kind":"Validation","Message":"Review this answer.","Checks":[{"Left":{"Kind":"Constant","Value":1},"Comparison":"GreaterThan","Right":{"Kind":"Constant","Value":2}}]}]}""");
        legacy.Rules.Should().ContainSingle().Which.ShowWhileAnswering.Should().BeFalse();

        legacy.Rules[0].ShowWhileAnswering = true;
        CompositionDefinition.Read(legacy.ToJson()).Rules.Single().ShowWhileAnswering.Should().BeTrue();
    }

    [Fact]
    public void Only_opted_in_validation_rules_are_live_but_every_failed_rule_remains_an_answer_error()
    {
        var (graph, first, second) = Graph();
        first.Question!.IsRequired = true;
        var total = new CalculatedField { Name = "Total", CategoryKey = graph.Categories[0].LogicalKey, Sources = [Q(first), Q(second)] };
        CompositionRule Rule(string name, string message, bool live) => new()
        {
            Name = name, CategoryKey = total.CategoryKey, TargetQuestionKey = first.LogicalKey,
            Kind = RulePurpose.Validation, Message = message, ShowWhileAnswering = live,
            Checks = [new ValueCheck { Left = C(total.Id), Comparison = Comparison.LessThanOrEqual, Right = new() { Value = 100 } }]
        };
        var liveRule = Rule("Live ceiling", "Live ceiling exceeded.", true);
        var submissionRule = Rule("Submission ceiling", "Submission ceiling exceeded.", false);
        graph.Version.CompositionJson = new CompositionDefinition { Calculations = [total], Rules = [liveRule, submissionRule] }.ToJson();
        var answers = new Dictionary<int, AnswerValue> { [1] = new(null, 60, null), [2] = new(null, 60, null) };

        var partial = new DefinitionEvaluator().Evaluate(graph, answers, requireComplete: false);

        partial.AnswerIssues.Where(issue => issue.Code == "combined-validation").Select(issue => issue.Message)
            .Should().BeEquivalentTo("Live ceiling exceeded.", "Submission ceiling exceeded.");
        partial.AnswerIssues.Should().NotContain(issue => issue.Code == "required");
        var liveError = partial.Composition!.LiveAnswerErrors.Should().ContainSingle().Which;
        liveError.RuleId.Should().Be(liveRule.Id);
        liveError.QuestionId.Should().Be(first.Question.Id);
        liveError.Message.Should().Be("Live ceiling exceeded.");

        var submission = new DefinitionEvaluator().Evaluate(graph, answers, requireComplete: true);
        submission.AnswerIssues.Where(issue => issue.Code == "combined-validation").Select(issue => issue.Message)
            .Should().BeEquivalentTo("Live ceiling exceeded.", "Submission ceiling exceeded.");
        submission.AnswerIssues.Should().NotContain(issue => issue.Code == "required", "both the live UI and submission have answers for every required question");
    }

    [Fact]
    public void Required_errors_remain_submission_only_when_live_composition_errors_are_enabled()
    {
        var (graph, first, second) = Graph();
        first.Question!.IsRequired = true;
        second.Question!.IsRequired = true;
        var rule = new CompositionRule { Name = "Minimum", CategoryKey = graph.Categories[0].LogicalKey,
            TargetQuestionKey = first.LogicalKey, Kind = RulePurpose.Validation, Message = "Increase the request.", ShowWhileAnswering = true,
            Checks = [new ValueCheck { Left = Q(first), Comparison = Comparison.GreaterThanOrEqual, Right = new() { Value = 10 } }] };
        graph.Version.CompositionJson = new CompositionDefinition { Rules = [rule] }.ToJson();
        var answers = new Dictionary<int, AnswerValue> { [1] = new(null, 1, null) };

        var partial = new DefinitionEvaluator().Evaluate(graph, answers, requireComplete: false);
        partial.Composition!.LiveAnswerErrors.Should().ContainSingle().Which.Message.Should().Be("Increase the request.");
        partial.AnswerIssues.Should().NotContain(issue => issue.Code == "required");

        var submission = new DefinitionEvaluator().Evaluate(graph, answers, requireComplete: true);
        submission.AnswerIssues.Should().Contain(issue => issue.Code == "required" && issue.QuestionId == second.Question!.Id);
        submission.AnswerIssues.Should().Contain(issue => issue.Code == "combined-validation" && issue.Message == "Increase the request.");
    }

    [Theory]
    [InlineData("{\"Calculations\":[null]}")]
    [InlineData("{\"Groups\":null}")]
    [InlineData("{\"Rules\":[null]}")]
    [InlineData("not json")]
    public void Malformed_configuration_returns_definition_errors(string json)
    {
        var (graph, _, _) = Graph(); graph.Version.CompositionJson = json;
        CompositionEngine.Evaluate(graph, new Dictionary<int, AnswerValue>(), new HashSet<int>()).DefinitionErrors.Should().NotBeEmpty();
    }

    [Fact]
    public void Null_nested_group_rows_return_definition_errors()
    {
        var (graph, _, _) = Graph();
        graph.Version.CompositionJson = "{\"Groups\":[{\"Name\":\"Broken\",\"CategoryKey\":\"" + graph.Categories[0].LogicalKey + "\",\"Rows\":[null]}]}";

        CompositionEngine.Evaluate(graph, new Dictionary<int, AnswerValue>(), new HashSet<int>()).DefinitionErrors.Should().NotBeEmpty();
    }

    [Fact]
    public void Dependent_calculations_ignore_stale_hidden_answers_and_handle_zero_denominators()
    {
        var (graph, a, b) = Graph();
        var total = new CalculatedField { Name = "Total", CategoryKey = graph.Categories[0].LogicalKey, Sources = [Q(a), Q(b)] };
        var share = new CalculatedField { Name = "Funding share", CategoryKey = total.CategoryKey, Operation = CalculationOperation.Percentage, Sources = [Q(a), C(total.Id)] };
        graph.Version.CompositionJson = new CompositionDefinition { Calculations = [share, total] }.ToJson();
        var result = CompositionEngine.Evaluate(graph, new Dictionary<int, AnswerValue> { [1] = new(null, 25, null), [2] = new(null, 75, null) }, new HashSet<int> { 1 });
        result.DefinitionErrors.Should().BeEmpty();
        result.Values.Single(v => v.Id == total.Id).Value.Should().Be(25);
        result.Values.Single(v => v.Id == share.Id).Value.Should().Be(100);
        var empty = CompositionEngine.Evaluate(graph, new Dictionary<int, AnswerValue>(), new HashSet<int> { 1, 2 });
        empty.Values.Single(v => v.Id == share.Id).Value.Should().BeNull();
    }

    [Fact]
    public void Hidden_category_calculations_still_feed_visible_calculations_but_are_not_rendered()
    {
        var (graph, first, second) = Graph();
        var laterCategory = new Category { Id = 2, Name = "Later", SortOrder = 1 };
        laterCategory.Elements.Add(second);
        graph.Categories[0].Elements.Remove(second);
        graph.Categories[0].SortOrder = 0;
        var graphWithLaterCategory = new ApplicationVersionGraph { Version = graph.Version, Categories = [graph.Categories[0], laterCategory] };
        graph.Version.Categories = graphWithLaterCategory.Categories.ToList();
        var hidden = new CalculatedField { Name = "Hidden total", CategoryKey = laterCategory.LogicalKey, Sources = [Q(second)] };
        var shown = new CalculatedField { Name = "Shown total", CategoryKey = graph.Categories[0].LogicalKey, Sources = [C(hidden.Id), Q(first)] };
        graph.Version.CompositionJson = new CompositionDefinition { Calculations = [shown, hidden] }.ToJson();

        var result = CompositionEngine.Evaluate(graphWithLaterCategory,
            new Dictionary<int, AnswerValue> { [1] = new(null, 5, null), [2] = new(null, 7, null) },
            new HashSet<int> { 1, 2 }, new HashSet<int> { graphWithLaterCategory.Categories[0].Id });

        result.DefinitionErrors.Should().BeEmpty();
        result.Values.Should().ContainSingle(v => v.Id == shown.Id && v.Value == 12);
    }

    [Fact]
    public void Validation_rule_cannot_depend_on_later_category_answers_directly_or_through_calculations()
    {
        var (graph, first, second) = Graph();
        var laterCategory = new Category { Id = 2, Name = "Later review", SortOrder = 1, Elements = [second] };
        graph.Categories[0].SortOrder = 0;
        graph.Categories[0].Elements.Remove(second);
        var option = new QuestionOption { Id = 30, Label = "Eligible" };
        var choice = new Element { Id = 3, Label = "Eligibility", Question = new Question { Id = 3, AnswerType = AnswerType.RadioGroup, Options = [option] } };
        laterCategory.Elements.Add(choice);
        var graphWithLaterCategory = new ApplicationVersionGraph { Version = graph.Version, Categories = [graph.Categories[0], laterCategory] };
        graph.Version.Categories = graphWithLaterCategory.Categories.ToList();
        var laterTotal = new CalculatedField { Name = "Later total", CategoryKey = laterCategory.LogicalKey, Sources = [Q(second)] };
        var forwarded = new CalculatedField { Name = "Forwarded total", CategoryKey = graph.Categories[0].LogicalKey, Sources = [C(laterTotal.Id)] };
        var directRule = new CompositionRule { Name = "Early direct check", CategoryKey = graph.Categories[0].LogicalKey,
            Kind = RulePurpose.Validation, Message = "Check funding",
            Conditions = [new() { Left = new() { Kind = SourceKind.OptionSelected, Key = option.LogicalKey }, Comparison = Comparison.Equal, Right = new() { Value = 1 } }],
            Checks = [new() { Left = Q(second), Comparison = Comparison.GreaterThan, Right = new() }] };
        var transitiveRule = new CompositionRule { Name = "Early calculated check", CategoryKey = graph.Categories[0].LogicalKey,
            Kind = RulePurpose.Validation, Message = "Check total", Conditions = [new() { Left = C(forwarded.Id), Comparison = Comparison.GreaterThanOrEqual, Right = new() }],
            Checks = [new() { Left = Q(first), Comparison = Comparison.GreaterThanOrEqual, Right = new() }] };
        graph.Version.CompositionJson = new CompositionDefinition { Calculations = [forwarded, laterTotal], Rules = [directRule, transitiveRule] }.ToJson();

        var errors = CompositionEngine.Evaluate(graphWithLaterCategory, new Dictionary<int, AnswerValue>(), new HashSet<int>()).DefinitionErrors;

        errors.Should().Contain(m => m.Contains("Early direct check") && m.Contains("Later review"));
        errors.Should().Contain(m => m.Contains("Early calculated check") && m.Contains("Later review"));
    }

    [Fact]
    public void Scoring_rules_may_depend_on_later_category_answers()
    {
        var (graph, _, later) = Graph();
        var laterCategory = new Category { Id = 2, Name = "Later", SortOrder = 1, Elements = [later] };
        graph.Categories[0].SortOrder = 0;
        graph.Categories[0].Elements.Remove(later);
        var graphWithLaterCategory = new ApplicationVersionGraph { Version = graph.Version, Categories = [graph.Categories[0], laterCategory] };
        graph.Version.Categories = graphWithLaterCategory.Categories.ToList();
        var scoringRule = new CompositionRule { Name = "Later score", CategoryKey = graph.Categories[0].LogicalKey,
            Kind = RulePurpose.Score, Points = 2, Checks = [new() { Left = Q(later), Comparison = Comparison.GreaterThan, Right = new() }] };
        graph.Version.CompositionJson = new CompositionDefinition { Rules = [scoringRule] }.ToJson();

        CompositionEngine.Evaluate(graphWithLaterCategory, new Dictionary<int, AnswerValue>(), new HashSet<int>()).DefinitionErrors.Should().BeEmpty();
    }

    [Fact]
    public void Score_schema_version_tracks_configured_calculations_in_hidden_categories()
    {
        var (graph, _, later) = Graph();
        var laterCategory = new Category { Id = 2, Name = "Later", SortOrder = 1, Elements = [later] };
        graph.Categories[0].SortOrder = 0;
        graph.Categories[0].Elements.Remove(later);
        var graphWithLaterCategory = new ApplicationVersionGraph { Version = graph.Version, Categories = [graph.Categories[0], laterCategory] };
        graph.Version.Categories = graphWithLaterCategory.Categories.ToList();
        graph.Version.CompositionJson = new CompositionDefinition
        {
            Calculations = [new CalculatedField { Name = "Later total", CategoryKey = laterCategory.LogicalKey, Sources = [Q(later)] }]
        }.ToJson();

        var score = new ScoringEngine().Score(graphWithLaterCategory, new Dictionary<int, AnswerValue>(), new HashSet<int> { 1 });

        score.EngineVersion.Should().Be("2.0");
    }

    [Fact]
    public void Numeric_overflow_is_reported_as_a_calculation_definition_error()
    {
        var (graph, first, second) = Graph();
        var calculation = new CalculatedField { Name = "Too large", CategoryKey = graph.Categories[0].LogicalKey, Sources = [Q(first), Q(second)] };
        graph.Version.CompositionJson = new CompositionDefinition { Calculations = [calculation] }.ToJson();

        var result = CompositionEngine.Evaluate(graph,
            new Dictionary<int, AnswerValue> { [1] = new(null, decimal.MaxValue, null), [2] = new(null, 1, null) },
            new HashSet<int> { 1, 2 });

        result.DefinitionErrors.Should().Contain(m => m.Contains("supported numeric range"));
    }

    [Fact]
    public void Circular_and_deleted_sources_cannot_be_published()
    {
        var (graph, a, _) = Graph();
        var one = new CalculatedField { Name = "One", CategoryKey = graph.Categories[0].LogicalKey };
        var two = new CalculatedField { Name = "Two", CategoryKey = one.CategoryKey, Sources = [C(one.Id)] };
        one.Sources = [C(two.Id)];
        graph.Version.CompositionJson = new CompositionDefinition { Calculations = [one, two] }.ToJson();
        CompositionEngine.Evaluate(graph, new Dictionary<int, AnswerValue>(), new HashSet<int>()).DefinitionErrors.Should().Contain(m => m.Contains("circular"));
        one.Sources = [Q(a)]; two.Sources = [new ValueSource { Kind = SourceKind.Question, Key = Guid.NewGuid() }];
        graph.Version.CompositionJson = new CompositionDefinition { Calculations = [one, two] }.ToJson();
        CompositionEngine.Evaluate(graph, new Dictionary<int, AnswerValue>(), new HashSet<int>()).DefinitionErrors.Should().NotBeEmpty();
    }

    [Fact]
    public void Staffing_rules_are_conditional_and_use_unrounded_percentages()
    {
        var (graph, a, b) = Graph(); a.Label = "Qualified staff"; b.Label = "Total staff";
        var option = new QuestionOption { Id = 20, Label = "Large organization" };
        var type = new Element { Id = 3, Question = new Question { Id = 3, AnswerType = AnswerType.RadioGroup, Options = [option] } };
        graph.Categories[0].Elements.Add(type);
        var share = new CalculatedField { Name = "Qualified percentage", CategoryKey = graph.Categories[0].LogicalKey, Operation = CalculationOperation.Percentage, Sources = [Q(a), Q(b)] };
        var check = new ValueCheck { Left = C(share.Id), Comparison = Comparison.GreaterThanOrEqual, Right = new() { Value = 66.67m } };
        var rule = new CompositionRule { Name = "Staff qualification", Kind = RulePurpose.Score, CategoryKey = share.CategoryKey, Points = 5, Checks = [check] };
        var validation = new CompositionRule { Name = "Large organization minimum", CategoryKey = share.CategoryKey, Message = "At least four staff are required.",
            Conditions = [new() { Left = new() { Kind = SourceKind.OptionSelected, Key = option.LogicalKey }, Comparison = Comparison.Equal, Right = new() { Value = 1 } }],
            Checks = [new() { Left = Q(b), Comparison = Comparison.GreaterThanOrEqual, Right = new() { Value = 4 } }] };
        graph.Version.CompositionJson = new CompositionDefinition { Calculations = [share], Rules = [rule, validation] }.ToJson();
        var answers = new Dictionary<int, AnswerValue> { [1] = new(null, 2, null), [2] = new(null, 3, null) };
        var result = new DefinitionEvaluator().Evaluate(graph, answers, true);
        result.Score.Total.Should().Be(0); result.AnswerIssues.Should().BeEmpty();
        answers[3] = new(null, null, [20]);
        new DefinitionEvaluator().Evaluate(graph, answers, true).AnswerIssues.Should().Contain(i => i.Message.Contains("four staff"));
    }

    [Fact]
    public void Version_clone_preserves_generic_configuration_and_whole_number_constraint()
    {
        var (graph, a, _) = Graph();
        a.Question!.WholeNumbersOnly = true;
        a.Question.Rules.Add(new ScoringRule
        {
            Id = 77, Family = RuleFamily.NumericRange, AwardMethod = AwardMethod.PointsPerUnit,
            PointsPerUnit = 4, MaximumPoints = 120, MinValue = 0, MaxValue = 30
        });
        graph.Version.CompositionJson = new CompositionDefinition { Groups = [new() { Name = "Budget rows", CategoryKey = graph.Categories[0].LogicalKey, Rows = [new() { QuestionKey = a.LogicalKey }] }] }.ToJson();
        var clone = DefinitionCloner.Clone(graph, 2).Graph;
        clone.Version.CompositionJson.Should().Be(graph.Version.CompositionJson);
        clone.Categories[0].Elements[0].LogicalKey.Should().Be(a.LogicalKey);
        clone.Categories[0].Elements[0].Question!.WholeNumbersOnly.Should().BeTrue();
        clone.Categories[0].Elements[0].Question!.Rules.Should().ContainSingle().Which.Should().Match<ScoringRule>(rule =>
            rule.AwardMethod == AwardMethod.PointsPerUnit && rule.PointsPerUnit == 4 && rule.MaximumPoints == 120);
        var result = new DefinitionEvaluator().Evaluate(graph, new Dictionary<int, AnswerValue> { [1] = new(null, 1.5m, null) }, true);
        result.AnswerIssues.Should().Contain(i => i.Code == "invalid-answer");
    }

    [Fact]
    public void Combined_per_unit_and_composition_score_overflow_is_reported_and_composition_awards_are_omitted()
    {
        var (graph, questionElement, _) = Graph();
        var question = questionElement.Question!;
        question.AnswerType = AnswerType.Number;
        question.WholeNumbersOnly = true;
        question.MinValue = 0;
        question.MaxValue = 1_000_000;
        question.Rules.AddRange(Enumerable.Range(1, 2147).Select(id => new ScoringRule
        {
            Id = id, Family = RuleFamily.NumericRange, AwardMethod = AwardMethod.PointsPerUnit,
            PointsPerUnit = 1, MaximumPoints = 1_000_000, MinValue = 0
        }));
        graph.Version.CompositionJson = new CompositionDefinition
        {
            Rules = [new CompositionRule
            {
                Name = "Configured award", CategoryKey = graph.Categories[0].LogicalKey,
                Kind = RulePurpose.Score, Points = 1_000_000,
                Checks = [new ValueCheck
                {
                    Left = new ValueSource { Kind = SourceKind.Constant, Value = 1 },
                    Comparison = Comparison.GreaterThan,
                    Right = new ValueSource { Kind = SourceKind.Constant, Value = 0 }
                }]
            }]
        }.ToJson();

        var result = new DefinitionEvaluator().Evaluate(graph, new Dictionary<int, AnswerValue>(), requireComplete: false);

        result.DefinitionIssues.Select(issue => issue.Message).Should().Contain(message =>
            message.Contains("Combined scoring exceeds", StringComparison.Ordinal));
        result.Composition!.Awards.Should().BeEmpty();
        result.Score.Total.Should().Be(0);
    }
}
