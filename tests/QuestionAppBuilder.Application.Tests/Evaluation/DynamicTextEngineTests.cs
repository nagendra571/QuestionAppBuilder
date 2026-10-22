using FluentAssertions;
using QuestionAppBuilder.Application.Composition;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Tests.Evaluation;

public class DynamicTextEngineTests
{
    [Fact]
    public void Date_answers_require_iso_calendar_dates_and_are_answered_when_valid()
    {
        var element = Question(1, AnswerType.Date, required: true);
        var graph = Graph(element);
        var evaluator = new DefinitionEvaluator();

        var valid = evaluator.Evaluate(graph, Answers((1, new AnswerValue("2024-02-29", null, null))), true);
        var malformed = evaluator.Evaluate(graph, Answers((1, new AnswerValue("02/29/2024", null, null))), true);
        var wrongShape = evaluator.Evaluate(graph, Answers((1, new AnswerValue("2024-02-29", 1m, null))), false);

        valid.AnswerIssues.Should().BeEmpty();
        malformed.AnswerIssues.Select(x => x.Code).Should().Contain("invalid-answer");
        malformed.AnswerIssues.Should().NotContain(x => x.Code == "required");
        wrongShape.AnswerIssues.Select(x => x.Code).Should().Contain("invalid-answer");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_optional_date_is_missing_and_blank_required_date_only_reports_required(string blank)
    {
        var optional = Question(1, AnswerType.Date);
        var optionalGraph = Graph(optional);
        var variable = new DynamicVariable
        {
            Name = "Birth date", Operation = DynamicOperation.Value,
            Sources = [new() { Kind = DynamicSourceKind.Question, Key = optional.LogicalKey }]
        };
        optionalGraph.Version.CompositionJson = new CompositionDefinition
        {
            DynamicText = new DynamicTextDefinition
            {
                Variables = [variable], Templates = [new() { Slot = DynamicTextSlot.ConfirmationText, OwnerKey = Guid.Empty,
                    Parts = [new() { Kind = DynamicPartKind.Value, Source = new() { Kind = DynamicSourceKind.Variable, Key = variable.Id }, Fallback = "not supplied" }] }]
            }
        }.ToJson();
        var optionalResult = new DefinitionEvaluator().Evaluate(optionalGraph,
            Answers((1, new AnswerValue(blank, null, null))), false);

        optionalResult.AnswerIssues.Should().BeEmpty();
        optionalResult.Composition!.DynamicText!.Values.Single().Value.State.Should().Be(DynamicValueState.Missing);
        optionalResult.Composition.DynamicText.Texts.Single().Text.Should().Be("not supplied");

        var required = Question(2, AnswerType.Date, required: true);
        var requiredResult = new DefinitionEvaluator().Evaluate(Graph(required),
            Answers((2, new AnswerValue(blank, null, null))), true);
        requiredResult.AnswerIssues.Select(issue => issue.Code).Should().ContainSingle().Which.Should().Be("required");
    }

    [Fact]
    public void Dynamic_age_uses_one_evaluation_instant_and_renders_an_application_template()
    {
        var element = Question(1, AnswerType.Date);
        var graph = Graph(element);
        var age = new DynamicVariable
        {
            Id = Guid.NewGuid(), Name = "Age", Operation = DynamicOperation.Age,
            Sources = [new() { Kind = DynamicSourceKind.Question, Key = element.LogicalKey }, new() { Kind = DynamicSourceKind.Today }]
        };
        graph.Version.CompositionJson = new CompositionDefinition
        {
            DynamicText = new DynamicTextDefinition
            {
                TimeZoneId = "UTC", Variables = [age],
                Templates = [new DynamicTemplate
                {
                    Slot = DynamicTextSlot.ConfirmationText, OwnerKey = Guid.Empty,
                    Parts = [new() { Kind = DynamicPartKind.Literal, Text = "Age: " },
                        new() { Kind = DynamicPartKind.Value, Source = new() { Kind = DynamicSourceKind.Variable, Key = age.Id }, Fallback = "unknown" }]
                }]
            }
        }.ToJson();

        var composition = CompositionEngine.Evaluate(graph, Answers((1, new AnswerValue("2000-02-29", null, null))),
            new HashSet<int> { 1 }, new HashSet<int> { 1 });
        var result = DynamicTextEngine.Evaluate(graph, Answers((1, new AnswerValue("2000-02-29", null, null))),
            new HashSet<int> { 1 }, new HashSet<int> { 1 }, composition, new DateTimeOffset(2025, 2, 28, 23, 59, 0, TimeSpan.Zero));

        result.Values.Should().ContainSingle().Which.Value.Number.Should().Be(25);
        result.ReferenceDate.Should().Be(new DateOnly(2025, 2, 28));
        result.Texts.Should().ContainSingle().Which.Text.Should().Be("Age: 25");
        result.DefinitionErrors.Should().BeEmpty();
    }

    [Fact]
    public void Hidden_question_sources_are_not_read_and_use_the_template_fallback()
    {
        var element = Question(1, AnswerType.Textbox);
        var content = new Element { Id = 2, CategoryId = 1, Kind = ElementKind.Content, Label = "Summary", SortOrder = 2 };
        var variable = new DynamicVariable
        {
            Id = Guid.NewGuid(), Name = "Private value", Operation = DynamicOperation.Value,
            Sources = [new() { Kind = DynamicSourceKind.Question, Key = element.LogicalKey }]
        };
        var graph = Graph(element);
        graph.Categories[0].Elements.Add(content);
        var config = CompositionDefinition.Read(Definition(variable, new DynamicTemplate
        {
            Slot = DynamicTextSlot.ConfirmationText, OwnerKey = Guid.Empty,
            Parts = [new() { Kind = DynamicPartKind.Value, Source = new() { Kind = DynamicSourceKind.Variable, Key = variable.Id }, Fallback = "not available" }]
        }));
        config.DynamicText.Templates.Add(new DynamicTemplate
        {
            Slot = DynamicTextSlot.ElementLabel, OwnerKey = content.LogicalKey, HideWhenUnavailable = true,
            Parts = [new() { Kind = DynamicPartKind.Value, Source = new() { Kind = DynamicSourceKind.Variable, Key = variable.Id }, Fallback = "not available" }]
        });
        config.DynamicText.Templates.Add(new DynamicTemplate
        {
            Slot = DynamicTextSlot.ElementLabel, OwnerKey = element.LogicalKey,
            Parts = [new() { Kind = DynamicPartKind.Value, Source = new() { Kind = DynamicSourceKind.Variable, Key = variable.Id }, Fallback = "not available" }]
        });
        graph.Version.CompositionJson = config.ToJson();
        var answers = Answers((1, new AnswerValue("secret", null, null)));
        var composition = CompositionEngine.Evaluate(graph, answers, new HashSet<int>(), new HashSet<int> { 1 });

        var result = DynamicTextEngine.Evaluate(graph, answers, new HashSet<int>(), new HashSet<int> { 1 }, composition,
            new DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero));

        result.Values.Single().Value.State.Should().Be(DynamicValueState.NotApplicable);
        result.Texts.Single(x => x.Slot == DynamicTextSlot.ConfirmationText).Text.Should().Be("not available");
        result.Texts.Single(x => x.Slot == DynamicTextSlot.ElementLabel).IsHidden.Should().BeTrue();
        result.Texts.Should().NotContain(x => x.OwnerKey == element.LogicalKey);
    }

    [Fact]
    public void Missing_sources_are_skipped_by_join_and_answer_payload_overflow_becomes_invalid()
    {
        var element = Question(1, AnswerType.Textbox);
        var join = new DynamicVariable
        {
            Id = Guid.NewGuid(), Name = "Greeting", Operation = DynamicOperation.JoinText, SkipEmpty = true,
            Sources = [new() { Kind = DynamicSourceKind.Question, Key = element.LogicalKey }, new() { Kind = DynamicSourceKind.Text, Text = "Welcome" }]
        };
        var overflow = new DynamicVariable
        {
            Id = Guid.NewGuid(), Name = "Overflow", Operation = DynamicOperation.Add,
            Sources = [new() { Kind = DynamicSourceKind.Number, Number = decimal.MaxValue }, new() { Kind = DynamicSourceKind.Number, Number = 1m }]
        };
        var emptyJoin = new DynamicVariable
        {
            Id = Guid.NewGuid(), Name = "Empty join", Operation = DynamicOperation.JoinText, SkipEmpty = true,
            Sources = [new() { Kind = DynamicSourceKind.Question, Key = element.LogicalKey }]
        };
        var graph = Graph(element);
        graph.Version.CompositionJson = new CompositionDefinition
        {
            DynamicText = new DynamicTextDefinition { Variables = [join, overflow, emptyJoin] }
        }.ToJson();
        var answers = new Dictionary<int, AnswerValue>();
        var composition = CompositionEngine.Evaluate(graph, answers, new HashSet<int> { 1 }, new HashSet<int> { 1 });

        var result = DynamicTextEngine.Evaluate(graph, answers, new HashSet<int> { 1 }, new HashSet<int> { 1 }, composition,
            new DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero));

        result.Values.Single(x => x.Id == join.Id).Value.Should().Be(new DynamicValue(DynamicValueKind.Text, DynamicValueState.Present, Text: "Welcome"));
        result.Values.Single(x => x.Id == overflow.Id).Value.State.Should().Be(DynamicValueState.Invalid);
        result.Values.Single(x => x.Id == emptyJoin.Id).Value.State.Should().Be(DynamicValueState.Missing);
    }

    [Fact]
    public void Variable_dependencies_evaluate_as_a_diamond_in_dependency_order()
    {
        var baseValue = new DynamicVariable
        {
            Id = Guid.NewGuid(), Name = "Base", Operation = DynamicOperation.Value,
            Sources = [new() { Kind = DynamicSourceKind.Text, Text = "hello" }]
        };
        var left = new DynamicVariable
        {
            Id = Guid.NewGuid(), Name = "Left", Operation = DynamicOperation.Value,
            Sources = [new() { Kind = DynamicSourceKind.Variable, Key = baseValue.Id }]
        };
        var right = new DynamicVariable
        {
            Id = Guid.NewGuid(), Name = "Right", Operation = DynamicOperation.JoinText,
            Sources = [new() { Kind = DynamicSourceKind.Variable, Key = baseValue.Id }, new() { Kind = DynamicSourceKind.Text, Text = "world" }]
        };
        var top = new DynamicVariable
        {
            Id = Guid.NewGuid(), Name = "Top", Operation = DynamicOperation.JoinText,
            Sources = [new() { Kind = DynamicSourceKind.Variable, Key = left.Id }, new() { Kind = DynamicSourceKind.Variable, Key = right.Id }]
        };
        var graph = Graph(Question(1, AnswerType.Textbox));
        graph.Version.CompositionJson = new CompositionDefinition
        {
            DynamicText = new DynamicTextDefinition { Variables = [top, right, left, baseValue] }
        }.ToJson();
        var composition = CompositionEngine.Evaluate(graph, new Dictionary<int, AnswerValue>(), new HashSet<int> { 1 }, new HashSet<int> { 1 });

        var result = DynamicTextEngine.Evaluate(graph, new Dictionary<int, AnswerValue>(), new HashSet<int> { 1 }, new HashSet<int> { 1 }, composition,
            new DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero));

        result.DefinitionErrors.Should().BeEmpty();
        result.Values.Single(x => x.Id == top.Id).Value.Text.Should().Be("hello hello world");
    }

    [Fact]
    public void Invalid_shared_variable_dependency_graph_is_bounded()
    {
        var variables = new List<DynamicVariable>();
        var first = Guid.NewGuid();
        variables.Add(new DynamicVariable { Id = first, Name = "Cycle", Operation = DynamicOperation.Value,
            Sources = [new() { Kind = DynamicSourceKind.Variable, Key = first }] });
        var prior = first;
        for (var index = 0; index < 40; index++)
        {
            var id = Guid.NewGuid();
            variables.Add(new DynamicVariable { Id = id, Name = $"Layer {index}", Operation = DynamicOperation.JoinText,
                Sources = [new() { Kind = DynamicSourceKind.Variable, Key = prior }, new() { Kind = DynamicSourceKind.Variable, Key = prior }] });
            prior = id;
        }
        var graph = Graph(Question(1, AnswerType.Textbox));
        graph.Version.CompositionJson = new CompositionDefinition
        {
            DynamicText = new DynamicTextDefinition { Variables = variables }
        }.ToJson();
        var composition = CompositionEngine.Evaluate(graph, new Dictionary<int, AnswerValue>(), new HashSet<int> { 1 }, new HashSet<int> { 1 });

        var result = DynamicTextEngine.Evaluate(graph, new Dictionary<int, AnswerValue>(), new HashSet<int> { 1 }, new HashSet<int> { 1 }, composition,
            new DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero));

        result.DefinitionErrors.Should().Contain(error => error.Contains("circular", StringComparison.OrdinalIgnoreCase)
            || error.Contains("operation or source type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Empty_dynamic_question_label_falls_back_to_the_static_label()
    {
        var element = Question(1, AnswerType.Textbox);
        var variable = new DynamicVariable { Name = "Empty", Operation = DynamicOperation.Value,
            Sources = [new() { Kind = DynamicSourceKind.Text, Text = "" }] };
        var graph = Graph(element);
        graph.Version.CompositionJson = new CompositionDefinition
        {
            DynamicText = new DynamicTextDefinition
            {
                Variables = [variable], Templates = [new() { Slot = DynamicTextSlot.ElementLabel, OwnerKey = element.LogicalKey,
                    Parts = [new() { Kind = DynamicPartKind.Value, Source = new() { Kind = DynamicSourceKind.Variable, Key = variable.Id } }] }]
            }
        }.ToJson();
        var composition = CompositionEngine.Evaluate(graph, new Dictionary<int, AnswerValue>(), new HashSet<int> { 1 }, new HashSet<int> { 1 });

        var result = DynamicTextEngine.Evaluate(graph, new Dictionary<int, AnswerValue>(), new HashSet<int> { 1 }, new HashSet<int> { 1 }, composition,
            new DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero));

        result.Texts.Should().BeEmpty();
    }

    [Fact]
    public void Checkbox_source_below_minimum_is_invalid_but_empty_selection_is_missing()
    {
        var question = new Question { Id = 1, ElementId = 1, AnswerType = AnswerType.Checkboxes, MinSelections = 2,
            Options = [new() { Id = 11, QuestionId = 1, Label = "One", SortOrder = 1 }, new() { Id = 12, QuestionId = 1, Label = "Two", SortOrder = 2 }] };
        var element = new Element { Id = 1, CategoryId = 1, Kind = ElementKind.Question, Label = "Choices", Question = question };
        var variable = new DynamicVariable { Name = "Choices", Operation = DynamicOperation.Value,
            Sources = [new() { Kind = DynamicSourceKind.Question, Key = element.LogicalKey }] };
        var graph = Graph(element);
        graph.Version.CompositionJson = new CompositionDefinition
        {
            DynamicText = new DynamicTextDefinition
            {
                Variables = [variable], Templates = [new() { Slot = DynamicTextSlot.ConfirmationText, OwnerKey = Guid.Empty,
                    Parts = [new() { Kind = DynamicPartKind.Value, Source = new() { Kind = DynamicSourceKind.Variable, Key = variable.Id }, Fallback = "not supplied" }] }]
            }
        }.ToJson();

        DynamicTextResult Evaluate(IReadOnlyCollection<int>? selected)
        {
            var answers = new Dictionary<int, AnswerValue> { [1] = new AnswerValue(null, null, selected) };
            var visibleQuestions = new HashSet<int> { 1 };
            var visibleCategories = new HashSet<int> { 1 };
            var composition = CompositionEngine.Evaluate(graph, answers, visibleQuestions, visibleCategories);
            return DynamicTextEngine.Evaluate(graph, answers, visibleQuestions, visibleCategories, composition,
                new DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero));
        }

        var belowMinimum = Evaluate([11]);
        var empty = Evaluate([]);
        var absent = Evaluate(null);

        belowMinimum.Values.Single().Value.State.Should().Be(DynamicValueState.Invalid);
        belowMinimum.Texts.Single().Text.Should().Be("not supplied");
        empty.Values.Single().Value.State.Should().Be(DynamicValueState.Missing);
        absent.Values.Single().Value.State.Should().Be(DynamicValueState.Missing);
    }

    [Fact]
    public void Invalid_dynamic_definition_is_reported_without_throwing_or_publishing_a_value()
    {
        var element = Question(1, AnswerType.Number);
        var graph = Graph(element);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        graph.Version.CompositionJson = new CompositionDefinition
        {
            DynamicText = new DynamicTextDefinition
            {
                Variables =
                [
                    new() { Id = first, Name = "A", Operation = DynamicOperation.Value, Sources = [new() { Kind = DynamicSourceKind.Variable, Key = second }] },
                    new() { Id = second, Name = "B", Operation = DynamicOperation.Value, Sources = [new() { Kind = DynamicSourceKind.Variable, Key = first }] }
                ]
            }
        }.ToJson();

        var result = new DefinitionEvaluator().Evaluate(graph, new Dictionary<int, AnswerValue>(), false);

        result.DefinitionIssues.Select(x => x.Code).Should().Contain("invalid-dynamic-text");
        result.Composition.Should().NotBeNull();
        result.Composition!.DynamicText.Should().NotBeNull();
        result.Composition.DynamicText!.DefinitionErrors.Should().NotBeEmpty();
        result.Composition.DynamicText.Values.Should().BeEmpty();
    }

    [Fact]
    public void Existing_composition_json_defaults_dynamic_text_and_malformed_calculations_do_not_throw()
    {
        var legacy = CompositionDefinition.Read("{\"Groups\":[],\"Calculations\":[],\"Rules\":[]}");
        legacy.DynamicText.TimeZoneId.Should().Be("UTC");
        legacy.DynamicText.Variables.Should().BeEmpty();

        var graph = Graph(Question(1, AnswerType.Textbox));
        graph.Version.CompositionJson = "{\"Groups\":[],\"Calculations\":null,\"Rules\":[],\"DynamicText\":{\"TimeZoneId\":\"UTC\",\"Variables\":[],\"Templates\":[]}}";

        var result = new DefinitionEvaluator().Evaluate(graph, new Dictionary<int, AnswerValue>(), false);

        result.DefinitionIssues.Select(x => x.Code).Should().Contain("invalid-dynamic-text");
    }

    [Fact]
    public void Evaluator_uses_one_injected_evaluation_instant_for_dynamic_values()
    {
        var graph = Graph(Question(1, AnswerType.Date));
        var expected = new DateTimeOffset(2026, 10, 22, 9, 30, 0, TimeSpan.Zero);
        var evaluator = new DefinitionEvaluator(timeProvider: new FixedTimeProvider(expected));

        var result = evaluator.Evaluate(graph, new Dictionary<int, AnswerValue>(), false);

        result.Composition!.DynamicText!.EvaluatedAtUtc.Should().Be(expected);
        result.Composition.DynamicText.ReferenceDate.Should().Be(new DateOnly(2026, 10, 22));
    }

    private static string Definition(DynamicVariable variable, DynamicTemplate template) => new CompositionDefinition
    {
        DynamicText = new DynamicTextDefinition { Variables = [variable], Templates = [template] }
    }.ToJson();

    private static ApplicationVersionGraph Graph(params Element[] elements) => new()
    {
        Version = new ApplicationVersion { Id = 1, ApplicationId = 1, VersionNumber = 1 },
        Categories = [new Category { Id = 1, ApplicationVersionId = 1, SortOrder = 1, Name = "Category", Elements = elements.ToList() }]
    };

    private static Element Question(int id, AnswerType type, bool required = false) => new()
    {
        Id = id, CategoryId = 1, Kind = ElementKind.Question, Label = $"Question {id}", SortOrder = id,
        Question = new Question { Id = id, ElementId = id, AnswerType = type, IsRequired = required }
    };

    private static Dictionary<int, AnswerValue> Answers(params (int Id, AnswerValue Value)[] values) => values.ToDictionary(x => x.Id, x => x.Value);

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
