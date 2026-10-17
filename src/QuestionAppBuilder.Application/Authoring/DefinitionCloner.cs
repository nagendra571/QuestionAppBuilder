using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Authoring;

public sealed class DefinitionCloneMap
{
    public Dictionary<int, Category> Categories { get; } = new();
    public Dictionary<int, Element> Elements { get; } = new();
    public Dictionary<int, Question> Questions { get; } = new();
    public Dictionary<int, QuestionOption> Options { get; } = new();
    public Dictionary<int, ScoringRule> Rules { get; } = new();
    public Dictionary<int, VisibilityCondition> Conditions { get; } = new();
    public List<(ScoringRule Rule, int OptionId)> RuleOptionLinks { get; } = new();
}

public static class DefinitionCloner
{
    public static (ApplicationVersionGraph Graph, DefinitionCloneMap Map) Clone(ApplicationVersionGraph source, int versionNumber)
    {
        var version = new ApplicationVersion
        {
            ApplicationId = source.Version.ApplicationId, VersionNumber = versionNumber, Status = VersionStatus.Draft,
            CreatedUtc = DateTime.UtcNow, NavigationMode = source.Version.NavigationMode,
            RespondentAccessMode = source.Version.RespondentAccessMode, ResultVisibility = source.Version.ResultVisibility,
            SubmitLabel = source.Version.SubmitLabel, ConfirmationText = source.Version.ConfirmationText,
            DisplayName = source.Version.DisplayName
        };
        var map = new DefinitionCloneMap();
        foreach (var sourceCategory in source.Categories)
        {
            var category = new Category { Name = sourceCategory.Name, SortOrder = sourceCategory.SortOrder, LogicalKey = sourceCategory.LogicalKey };
            map.Categories[sourceCategory.Id] = category;
            version.Categories.Add(category);
            foreach (var sourceElement in sourceCategory.Elements)
            {
                var element = new Element { Kind = sourceElement.Kind, Label = sourceElement.Label, HelpText = sourceElement.HelpText,
                    SortOrder = sourceElement.SortOrder, LogicalKey = sourceElement.LogicalKey };
                map.Elements[sourceElement.Id] = element;
                category.Elements.Add(element);
                if (sourceElement.Question is not { } sourceQuestion) continue;
                var question = new Question { AnswerType = sourceQuestion.AnswerType, IsRequired = sourceQuestion.IsRequired,
                    ScoringMode = sourceQuestion.ScoringMode, MaxLength = sourceQuestion.MaxLength, MinValue = sourceQuestion.MinValue,
                    MaxValue = sourceQuestion.MaxValue, MinSelections = sourceQuestion.MinSelections, MaxSelections = sourceQuestion.MaxSelections };
                map.Questions[sourceQuestion.Id] = question;
                element.Question = question;
                foreach (var sourceOption in sourceQuestion.Options)
                {
                    var option = new QuestionOption { Label = sourceOption.Label, Value = sourceOption.Value,
                        SortOrder = sourceOption.SortOrder, LogicalKey = sourceOption.LogicalKey };
                    map.Options[sourceOption.Id] = option;
                    question.Options.Add(option);
                }
                foreach (var sourceRule in sourceQuestion.Rules)
                {
                    var rule = new ScoringRule { Family = sourceRule.Family, Points = sourceRule.Points, SortOrder = sourceRule.SortOrder,
                        LogicalKey = sourceRule.LogicalKey, MinInclusive = sourceRule.MinInclusive, MaxInclusive = sourceRule.MaxInclusive,
                        MembershipOperator = sourceRule.MembershipOperator, MinValue = sourceRule.MinValue, MaxValue = sourceRule.MaxValue };
                    map.Rules[sourceRule.Id] = rule;
                    question.Rules.Add(rule);
                    foreach (var sourceRuleOption in sourceRule.Options) map.RuleOptionLinks.Add((rule, sourceRuleOption.QuestionOptionId));
                }
            }
        }
        foreach (var sourceCondition in source.VisibilityConditions)
        {
            var condition = new VisibilityCondition { Junction = sourceCondition.Junction };
            map.Conditions[sourceCondition.Id] = condition;
            foreach (var sourcePredicate in sourceCondition.Predicates)
            {
                var predicate = new VisibilityPredicate { Operator = sourcePredicate.Operator, NumberValue = sourcePredicate.NumberValue,
                    SourceQuestion = map.Questions[sourcePredicate.SourceQuestionId] };
                condition.Predicates.Add(predicate);
                foreach (var option in sourcePredicate.Options)
                    predicate.Options.Add(new VisibilityPredicateOption { QuestionOption = map.Options[option.QuestionOptionId] });
            }
            version.VisibilityConditions.Add(condition);
        }
        foreach (var sourceCategory in source.Categories)
        {
            if (sourceCategory.VisibilityConditionId is int categoryConditionId)
                map.Categories[sourceCategory.Id].VisibilityCondition = map.Conditions[categoryConditionId];
            foreach (var sourceElement in sourceCategory.Elements)
                if (sourceElement.VisibilityConditionId is int elementConditionId)
                    map.Elements[sourceElement.Id].VisibilityCondition = map.Conditions[elementConditionId];
        }
        return (new ApplicationVersionGraph { Version = version, Categories = version.Categories, VisibilityConditions = version.VisibilityConditions }, map);
    }

    public static void LinkRuleOptions(DefinitionCloneMap map)
    {
        foreach (var (rule, sourceOptionId) in map.RuleOptionLinks)
            rule.Options.Add(new ScoringRuleOption { QuestionOption = map.Options[sourceOptionId] });
    }
}
