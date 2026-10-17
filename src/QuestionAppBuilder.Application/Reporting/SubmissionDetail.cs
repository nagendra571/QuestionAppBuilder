using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Application.Reporting;

public sealed record SubmissionCategoryScore(string Name, int SortOrder, int Score);
public sealed record SubmissionQuestionDetail(
    int QuestionId,
    Guid LogicalKey,
    string Label,
    string AnswerType,
    bool? IsApplicable,
    bool HasAnswer,
    string? TextValue,
    decimal? NumberValue,
    IReadOnlyList<string> SelectedOptions,
    int PointsEarned);
public sealed record SubmissionRuleDetail(
    int QuestionId,
    string QuestionLabel,
    int RuleId,
    int SortOrder,
    bool Matches,
    bool Contributes,
    int Points);

public sealed record SubmissionDetail(
    int Id,
    int ApplicationId,
    string ApplicationName,
    int ApplicationVersionId,
    int VersionNumber,
    string DisplayName,
    DateTime SubmittedUtc,
    string? OwnerReference,
    int TotalScore,
    ResultVisibility ResultVisibility,
    IReadOnlyList<SubmissionCategoryScore> Categories,
    IReadOnlyList<SubmissionQuestionDetail> Questions,
    IReadOnlyList<SubmissionRuleDetail> RuleTraces);
