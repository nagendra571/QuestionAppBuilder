namespace QuestionAppBuilder.Application.Reporting;

public sealed record SubmissionCursor(DateTime SubmittedUtc, int Id);

public sealed record SubmissionListItem(
    int Id,
    string ApplicationName,
    int ApplicationVersionId,
    int VersionNumber,
    string DisplayName,
    DateTime SubmittedUtc,
    string? OwnerReference,
    int TotalScore);

public sealed record SubmissionPage(
    IReadOnlyList<SubmissionListItem> Items,
    SubmissionCursor? NextCursor,
    int PageSize);
