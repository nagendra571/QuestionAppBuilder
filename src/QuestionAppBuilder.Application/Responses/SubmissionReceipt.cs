using QuestionAppBuilder.Application.Scoring;

namespace QuestionAppBuilder.Application.Responses;

public sealed record SubmissionReceipt(int SubmissionId, Guid ResponseId, DateTime SubmittedUtc, string ConfirmationText,
    int? Total, IReadOnlyList<CategoryScore>? Categories)
{
    public string? ReviewSummary { get; init; }
    public SubmittedApplication? SubmittedApplication { get; init; }
}
