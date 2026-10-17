namespace QuestionAppBuilder.Application.Evaluation;

public sealed record DefinitionIssue(string Code, int? ElementId, string Message, int? QuestionId = null);
