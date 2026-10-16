using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Graphs;
using QuestionAppBuilder.Domain.Interfaces;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;

namespace QuestionAppBuilder.Application.Services;

public sealed class ApplicationDesignService : IApplicationDesignService
{
    private readonly IApplicationRepository _repository;

    public ApplicationDesignService(IApplicationRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<IReadOnlyList<ApplicationType>> ListApplicationTypesAsync(CancellationToken ct = default)
        => await _repository.ListApplicationTypesAsync(ct);

    public async Task<ApplicationEntity> CreateApplicationAsync(string name, int applicationTypeId, CancellationToken ct = default)
    {
        EnsureMaxLength(name, ValidationLimits.NameMaxLength, "Application name");

        if (await _repository.NameExistsAsync(name, ct))
        {
            throw new DuplicateApplicationNameException(name);
        }

        var now = DateTime.UtcNow;
        var application = new ApplicationEntity
        {
            Name = name,
            ApplicationTypeId = applicationTypeId,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        application.Versions.Add(new ApplicationVersion
        {
            VersionNumber = 1,
            Status = VersionStatus.Draft,
            CreatedUtc = now
        });

        _repository.Add(application);
        await _repository.SaveChangesAsync(ct);

        return application;
    }

    public async Task<IReadOnlyList<ApplicationEntity>> ListApplicationsAsync(CancellationToken ct = default)
        => await _repository.ListAsync(ct);

    public async Task<ApplicationVersionGraph> GetDraftGraphAsync(int applicationId, CancellationToken ct = default)
    {
        var graph = await _repository.GetDraftGraphAsync(applicationId, ct);
        if (graph is null)
        {
            throw new NotFoundException($"No draft version found for application {applicationId}.");
        }

        return graph;
    }

    public async Task<ApplicationVersionGraph?> TryGetDraftGraphAsync(int applicationId, CancellationToken ct = default)
        => await _repository.GetDraftGraphAsync(applicationId, ct);

    public async Task<ApplicationVersionGraph> GetLatestPublishedGraphAsync(int applicationId, CancellationToken ct = default)
    {
        var application = await GetApplicationAsync(applicationId, ct);
        if (application.CurrentVersionId is not int currentVersionId)
        {
            throw new NotFoundException($"Application {applicationId} has no published version.");
        }

        return await _repository.GetVersionGraphAsync(currentVersionId, ct);
    }

    public async Task<ApplicationVersionGraph> OpenDraftAsync(int applicationId, CancellationToken ct = default)
    {
        var draft = await TryGetDraftGraphAsync(applicationId, ct);
        if (draft is not null)
        {
            return draft;
        }

        var application = await GetApplicationAsync(applicationId, ct);
        var published = await GetPublishedGraphAsync(application, ct);
        var (cloneGraph, cloneMap) = CloneGraph(published, published.Version.VersionNumber + 1);
        _repository.AddGraph(cloneGraph);
        LinkClonedRuleOptions(cloneMap);
        await _repository.SaveChangesAsync(ct);

        return cloneGraph;
    }

    public async Task<Category> AddCategoryAsync(int applicationId, string name, bool isIndependent = false, CancellationToken ct = default)
    {
        EnsureMaxLength(name, ValidationLimits.NameMaxLength, "Category name");

        var application = await GetApplicationAsync(applicationId, ct);
        var graph = await _repository.GetDraftGraphAsync(applicationId, ct);

        if (graph is null)
        {
            var published = await GetPublishedGraphAsync(application, ct);
            var (cloneGraph, cloneMap) = CloneGraph(published, published.Version.VersionNumber + 1);
            _repository.AddGraph(cloneGraph);
            LinkClonedRuleOptions(cloneMap);
            graph = cloneGraph;
        }

        var category = new Category
        {
            ApplicationVersionId = graph.Version.Id,
            Name = name,
            SortOrder = NextSortOrder(graph.Categories.Select(c => c.SortOrder)),
            IsIndependent = isIndependent
        };

        graph.Version.Categories.Add(category);
        await _repository.SaveChangesAsync(ct);

        return category;
    }

    public async Task UpdateCategoryAsync(int categoryId, string name, bool isIndependent, CancellationToken ct = default)
    {
        EnsureMaxLength(name, ValidationLimits.NameMaxLength, "Category name");

        var editable = await OpenForEditAsync(ChildEntityKind.Category, categoryId, g => FindCategory(g, categoryId), ct);

        editable.Target.Name = name;
        editable.Target.IsIndependent = isIndependent;
        await _repository.SaveChangesAsync(ct);
    }

    public async Task RemoveCategoryAsync(int categoryId, CancellationToken ct = default)
    {
        var editable = await OpenForEditAsync(ChildEntityKind.Category, categoryId, g => FindCategory(g, categoryId), ct);

        editable.Graph.Version.Categories.Remove(editable.Target);
        await _repository.SaveChangesAsync(ct);
    }

    public async Task<Element> AddContentElementAsync(int categoryId, string label, string? helpText = null, CancellationToken ct = default)
    {
        EnsureMaxLength(label, ValidationLimits.LabelMaxLength, "Element label");
        EnsureOptionalMaxLength(helpText, ValidationLimits.HelpTextMaxLength, "Help text");

        var editable = await OpenForEditAsync(ChildEntityKind.Category, categoryId, g => FindCategory(g, categoryId), ct);

        var element = new Element
        {
            CategoryId = editable.Target.Id,
            Kind = ElementKind.Content,
            Label = label,
            HelpText = helpText,
            SortOrder = NextSortOrder(editable.Target.Elements.Select(e => e.SortOrder))
        };

        editable.Target.Elements.Add(element);
        await _repository.SaveChangesAsync(ct);

        return element;
    }

    public async Task<Question> AddQuestionAsync(int categoryId, string label, AnswerType answerType, bool isRequired = true, string? helpText = null, CancellationToken ct = default)
    {
        EnsureMaxLength(label, ValidationLimits.LabelMaxLength, "Element label");
        EnsureOptionalMaxLength(helpText, ValidationLimits.HelpTextMaxLength, "Help text");

        var editable = await OpenForEditAsync(ChildEntityKind.Category, categoryId, g => FindCategory(g, categoryId), ct);

        var question = new Question
        {
            AnswerType = answerType,
            IsRequired = isRequired
        };

        var element = new Element
        {
            CategoryId = editable.Target.Id,
            Kind = ElementKind.Question,
            Label = label,
            HelpText = helpText,
            SortOrder = NextSortOrder(editable.Target.Elements.Select(e => e.SortOrder)),
            Question = question
        };

        editable.Target.Elements.Add(element);
        await _repository.SaveChangesAsync(ct);

        return question;
    }

    public async Task<QuestionOption> AddOptionAsync(int questionId, string label, string value, CancellationToken ct = default)
    {
        EnsureMaxLength(label, ValidationLimits.OptionLabelMaxLength, "Option label");
        EnsureMaxLength(value, ValidationLimits.OptionValueMaxLength, "Option value");

        var editable = await OpenForEditAsync(ChildEntityKind.Question, questionId, g => FindQuestion(g, questionId), ct);

        var option = new QuestionOption
        {
            QuestionId = editable.Target.Id,
            Label = label,
            Value = value,
            SortOrder = NextSortOrder(editable.Target.Options.Select(o => o.SortOrder))
        };

        editable.Target.Options.Add(option);
        await _repository.SaveChangesAsync(ct);

        return option;
    }

    public async Task<ScoringRule> AddRuleAsync(int questionId, RuleFamily family, int points, MembershipOperator? membershipOperator = null, IReadOnlyCollection<int>? optionIds = null, decimal? minValue = null, decimal? maxValue = null, CancellationToken ct = default)
    {
        var editable = await OpenForEditAsync(ChildEntityKind.Question, questionId, g => FindQuestion(g, questionId), ct);

        var sourceQuestion = editable.Source is null
            ? editable.Target
            : FindQuestion(editable.Source, questionId) ?? throw new NotFoundException($"Question {questionId} was not found.");

        var rule = new ScoringRule
        {
            QuestionId = editable.Target.Id,
            Family = family,
            Points = points,
            MembershipOperator = membershipOperator,
            MinValue = minValue,
            MaxValue = maxValue
        };

        switch (family)
        {
            case RuleFamily.Selection:
                if (membershipOperator is null)
                {
                    throw new DefinitionValidationException("A Selection rule needs a membership operator.");
                }

                if (optionIds is null || optionIds.Count == 0)
                {
                    throw new DefinitionValidationException("A Selection rule needs at least one option id.");
                }

                if (optionIds.Distinct().Count() != optionIds.Count)
                {
                    throw new DefinitionValidationException("A Selection rule cannot reference the same option more than once.");
                }

                var sourceOptionIds = sourceQuestion.Options.Select(o => o.Id).ToHashSet();
                foreach (var optionId in optionIds)
                {
                    if (!sourceOptionIds.Contains(optionId))
                    {
                        throw new DefinitionValidationException($"Option {optionId} does not belong to question {questionId}.");
                    }

                    var targetOptionId = editable.CloneMap is null
                        ? optionId
                        : editable.CloneMap.Options[optionId].Id;
                    rule.Options.Add(new ScoringRuleOption { QuestionOptionId = targetOptionId });
                }

                break;
            case RuleFamily.NumericRange:
                if (minValue is null && maxValue is null)
                {
                    throw new DefinitionValidationException("A NumericRange rule needs a minimum and/or a maximum value.");
                }

                if (minValue is not null && maxValue is not null && minValue > maxValue)
                {
                    throw new DefinitionValidationException("A NumericRange rule needs MinValue to be less than or equal to MaxValue.");
                }

                break;
        }

        editable.Target.Rules.Add(rule);
        await _repository.SaveChangesAsync(ct);

        return rule;
    }

    public async Task RemoveElementAsync(int elementId, CancellationToken ct = default)
    {
        var editable = await OpenForEditAsync(ChildEntityKind.Element, elementId, g => FindElement(g, elementId), ct);

        var category = editable.Graph.Categories.Single(c => c.Elements.Contains(editable.Target));
        category.Elements.Remove(editable.Target);
        await _repository.SaveChangesAsync(ct);
    }

    public async Task RemoveOptionAsync(int optionId, CancellationToken ct = default)
    {
        var editable = await OpenForEditAsync(ChildEntityKind.Option, optionId, g => FindOption(g, optionId), ct);

        if (IsOptionReferencedByAnyRule(editable.Graph, editable.Target))
        {
            throw new DefinitionValidationException($"Option {optionId} is used by a scoring rule; remove or change the rule first.");
        }

        var question = FindQuestionContainingOption(editable.Graph, editable.Target)
            ?? throw new NotFoundException($"Option {optionId} was not found.");
        question.Options.Remove(editable.Target);
        await _repository.SaveChangesAsync(ct);
    }

    public async Task RemoveRuleAsync(int ruleId, CancellationToken ct = default)
    {
        var editable = await OpenForEditAsync(ChildEntityKind.Rule, ruleId, g => FindRule(g, ruleId), ct);

        var question = FindQuestionContainingRule(editable.Graph, editable.Target)
            ?? throw new NotFoundException($"Rule {ruleId} was not found.");
        question.Rules.Remove(editable.Target);
        await _repository.SaveChangesAsync(ct);
    }

    public async Task PublishAsync(int applicationId, CancellationToken ct = default)
    {
        var application = await GetApplicationAsync(applicationId, ct);
        var graph = await _repository.GetDraftGraphAsync(applicationId, ct);
        if (graph is null)
        {
            throw new DefinitionValidationException("No draft to publish.");
        }

        var scorableQuestions = graph.Categories.SelectMany(c => c.Elements).Count(e => e.Question is not null);
        if (graph.Categories.Count == 0 || scorableQuestions == 0)
        {
            throw new DefinitionValidationException("An application needs at least one category and one scorable question before it can be published.");
        }

        graph.Version.Status = VersionStatus.Published;
        graph.Version.PublishedUtc = DateTime.UtcNow;
        application.CurrentVersionId = graph.Version.Id;
        await _repository.SaveChangesAsync(ct);
    }

    public async Task<ApplicationEntity> GetApplicationAsync(int applicationId, CancellationToken ct = default)
    {
        var application = await _repository.GetAsync(applicationId, ct);
        if (application is null)
        {
            throw new NotFoundException($"Application {applicationId} was not found.");
        }

        return application;
    }

    private async Task<ApplicationVersionGraph> GetPublishedGraphAsync(ApplicationEntity application, CancellationToken ct)
    {
        if (application.CurrentVersionId is not int currentVersionId)
        {
            throw new NotFoundException($"Application {application.Id} has no published version to clone.");
        }

        return await _repository.GetVersionGraphAsync(currentVersionId, ct);
    }

    private async Task<EditableTarget<T>> OpenForEditAsync<T>(
        ChildEntityKind kind,
        int childId,
        Func<ApplicationVersionGraph, T?> locate,
        CancellationToken ct)
        where T : class
    {
        var graph = await _repository.GetTrackedGraphByChildIdAsync(kind, childId, ct);
        var target = graph is null ? null : locate(graph);
        if (graph is null || target is null)
        {
            throw new NotFoundException($"{kind} {childId} was not found.");
        }

        if (graph.Version.Status == VersionStatus.Draft)
        {
            return new EditableTarget<T> { Graph = graph, Target = target };
        }

        var application = await GetApplicationAsync(graph.Version.ApplicationId, ct);
        if (graph.Version.Id != application.CurrentVersionId)
        {
            throw new NotFoundException($"{kind} {childId} belongs to version {graph.Version.Id}, which is no longer the current published version.");
        }

        if (await _repository.GetDraftGraphAsync(application.Id, ct) is not null)
        {
            throw new DefinitionValidationException($"Application {application.Id} already has a draft; it must be published before editing the published version.");
        }

        var nextVersionNumber = await GetNextVersionNumberAsync(application, graph.Version.VersionNumber, ct);
        var (cloneGraph, cloneMap) = CloneGraph(graph, nextVersionNumber);
        _repository.AddGraph(cloneGraph);
        LinkClonedRuleOptions(cloneMap);

        return new EditableTarget<T>
        {
            Graph = cloneGraph,
            Target = FindCounterpart<T>(cloneMap, kind, childId),
            Source = graph,
            CloneMap = cloneMap
        };
    }

    private async Task<int> GetNextVersionNumberAsync(ApplicationEntity application, int sourceVersionNumber, CancellationToken ct)
    {
        var maxExisting = sourceVersionNumber;
        if (application.CurrentVersionId is int currentVersionId)
        {
            var current = await _repository.GetVersionGraphAsync(currentVersionId, ct);
            maxExisting = Math.Max(maxExisting, current.Version.VersionNumber);
        }

        return maxExisting + 1;
    }

    private static (ApplicationVersionGraph Graph, CloneMap Map) CloneGraph(ApplicationVersionGraph source, int versionNumber)
    {
        var version = new ApplicationVersion
        {
            ApplicationId = source.Version.ApplicationId,
            VersionNumber = versionNumber,
            Status = VersionStatus.Draft,
            CreatedUtc = DateTime.UtcNow,
            PublishedUtc = null
        };

        var map = new CloneMap();

        foreach (var sourceCategory in source.Categories)
        {
            var category = new Category
            {
                Name = sourceCategory.Name,
                SortOrder = sourceCategory.SortOrder,
                IsIndependent = sourceCategory.IsIndependent
            };

            map.Categories[sourceCategory.Id] = category;
            version.Categories.Add(category);

            foreach (var sourceElement in sourceCategory.Elements)
            {
                var element = new Element
                {
                    Kind = sourceElement.Kind,
                    Label = sourceElement.Label,
                    HelpText = sourceElement.HelpText,
                    SortOrder = sourceElement.SortOrder
                };

                map.Elements[sourceElement.Id] = element;
                category.Elements.Add(element);

                if (sourceElement.Question is null)
                {
                    continue;
                }

                var question = new Question
                {
                    AnswerType = sourceElement.Question.AnswerType,
                    IsRequired = sourceElement.Question.IsRequired
                };

                map.Questions[sourceElement.Question.Id] = question;
                element.Question = question;

                foreach (var sourceOption in sourceElement.Question.Options)
                {
                    var option = new QuestionOption
                    {
                        Label = sourceOption.Label,
                        Value = sourceOption.Value,
                        SortOrder = sourceOption.SortOrder
                    };

                    map.Options[sourceOption.Id] = option;
                    question.Options.Add(option);
                }

                foreach (var sourceRule in sourceElement.Question.Rules)
                {
                    var rule = new ScoringRule
                    {
                        Family = sourceRule.Family,
                        Points = sourceRule.Points,
                        MembershipOperator = sourceRule.MembershipOperator,
                        MinValue = sourceRule.MinValue,
                        MaxValue = sourceRule.MaxValue
                    };

                    map.Rules[sourceRule.Id] = rule;
                    question.Rules.Add(rule);

                    foreach (var sourceRuleOption in sourceRule.Options)
                    {
                        map.RuleOptionLinks.Add((rule, sourceRuleOption.QuestionOptionId));
                    }
                }
            }
        }

        return (new ApplicationVersionGraph { Version = version, Categories = version.Categories }, map);
    }

    private static void LinkClonedRuleOptions(CloneMap map)
    {
        foreach (var (rule, sourceOptionId) in map.RuleOptionLinks)
        {
            rule.Options.Add(new ScoringRuleOption { QuestionOptionId = map.Options[sourceOptionId].Id });
        }
    }

    private static T FindCounterpart<T>(CloneMap map, ChildEntityKind kind, int childId)
        where T : class
        => kind switch
        {
            ChildEntityKind.Category => (T)(object)map.Categories[childId],
            ChildEntityKind.Element => (T)(object)map.Elements[childId],
            ChildEntityKind.Question => (T)(object)map.Questions[childId],
            ChildEntityKind.Option => (T)(object)map.Options[childId],
            ChildEntityKind.Rule => (T)(object)map.Rules[childId],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static Category? FindCategory(ApplicationVersionGraph graph, int categoryId)
        => graph.Categories.FirstOrDefault(c => c.Id == categoryId);

    private static Element? FindElement(ApplicationVersionGraph graph, int elementId)
        => graph.Categories.SelectMany(c => c.Elements).FirstOrDefault(e => e.Id == elementId);

    private static Question? FindQuestion(ApplicationVersionGraph graph, int questionId)
    {
        foreach (var question in graph.Categories.SelectMany(c => c.Elements).Select(e => e.Question))
        {
            if (question is not null && question.Id == questionId)
            {
                return question;
            }
        }

        return null;
    }

    private static QuestionOption? FindOption(ApplicationVersionGraph graph, int optionId)
    {
        foreach (var question in graph.Categories.SelectMany(c => c.Elements).Select(e => e.Question))
        {
            var option = question?.Options.FirstOrDefault(o => o.Id == optionId);
            if (option is not null)
            {
                return option;
            }
        }

        return null;
    }

    private static ScoringRule? FindRule(ApplicationVersionGraph graph, int ruleId)
    {
        foreach (var question in graph.Categories.SelectMany(c => c.Elements).Select(e => e.Question))
        {
            var rule = question?.Rules.FirstOrDefault(r => r.Id == ruleId);
            if (rule is not null)
            {
                return rule;
            }
        }

        return null;
    }

    private static Question? FindQuestionContainingOption(ApplicationVersionGraph graph, QuestionOption option)
    {
        foreach (var question in graph.Categories.SelectMany(c => c.Elements).Select(e => e.Question))
        {
            if (question is not null && question.Options.Contains(option))
            {
                return question;
            }
        }

        return null;
    }

    private static bool IsOptionReferencedByAnyRule(ApplicationVersionGraph graph, QuestionOption option)
    {
        foreach (var question in graph.Categories.SelectMany(c => c.Elements).Select(e => e.Question))
        {
            if (question is null)
            {
                continue;
            }

            foreach (var rule in question.Rules)
            {
                if (rule.Options.Any(o => o.QuestionOptionId == option.Id))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Question? FindQuestionContainingRule(ApplicationVersionGraph graph, ScoringRule rule)
    {
        foreach (var question in graph.Categories.SelectMany(c => c.Elements).Select(e => e.Question))
        {
            if (question is not null && question.Rules.Contains(rule))
            {
                return question;
            }
        }

        return null;
    }

    private static int NextSortOrder(IEnumerable<int> sortOrders) => sortOrders.DefaultIfEmpty(0).Max() + 1;

    private static void EnsureMaxLength(string value, int maxLength, string fieldName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);

        if (value.Length > maxLength)
        {
            throw new DefinitionValidationException($"{fieldName} must be at most {maxLength} characters.");
        }
    }

    private static void EnsureOptionalMaxLength(string? value, int maxLength, string fieldName)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length > maxLength)
        {
            throw new DefinitionValidationException($"{fieldName} must be at most {maxLength} characters.");
        }
    }

    private sealed class EditableTarget<T>
        where T : class
    {
        public required ApplicationVersionGraph Graph { get; init; }
        public required T Target { get; init; }
        public ApplicationVersionGraph? Source { get; init; }
        public CloneMap? CloneMap { get; init; }
    }

    private sealed class CloneMap
    {
        public Dictionary<int, Category> Categories { get; } = new();
        public Dictionary<int, Element> Elements { get; } = new();
        public Dictionary<int, Question> Questions { get; } = new();
        public Dictionary<int, QuestionOption> Options { get; } = new();
        public Dictionary<int, ScoringRule> Rules { get; } = new();
        public List<(ScoringRule Rule, int OptionId)> RuleOptionLinks { get; } = new();
    }
}
