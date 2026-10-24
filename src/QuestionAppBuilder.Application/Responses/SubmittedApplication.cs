using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Application.Responses;

/// <summary>Display-only projection of the submitted definition and saved answers.</summary>
public sealed record SubmittedApplication(string Title, IReadOnlyList<SubmittedCategory> Categories);

public sealed record SubmittedCategory(int Id, string Name, IReadOnlyList<SubmittedElement> Elements,
    IReadOnlyList<SubmittedGroup> Groups, IReadOnlyList<SubmittedCalculatedValue> CalculatedValues,
    int? PointsEarned, IReadOnlyList<SubmittedAward> Awards);

public sealed record SubmittedElement(int Id, Guid LogicalKey, ElementKind Kind, string Label, string? HelpText,
    int FollowUpDepth, string? FollowUpContext, SubmittedQuestion? Question);

public sealed record SubmittedQuestion(int Id, AnswerType AnswerType, string? AnswerText, int? PointsEarned);

public sealed record SubmittedAward(string Name, int Points);

public sealed record SubmittedCalculatedValue(string Name, decimal? Value, bool Percentage);

public sealed record SubmittedGroup(string Name, bool TableLayout, IReadOnlyList<string> Columns, IReadOnlyList<SubmittedGroupRow> Rows);

public sealed record SubmittedGroupRow(SubmittedElement Element, IReadOnlyList<string> Cells);
