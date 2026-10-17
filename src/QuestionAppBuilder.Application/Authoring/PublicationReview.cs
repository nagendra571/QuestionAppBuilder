namespace QuestionAppBuilder.Application.Authoring;

public sealed record PublicationReview(byte[] Revision, IReadOnlyList<PublicationIssue> Errors, IReadOnlyList<PublicationIssue> Warnings);
