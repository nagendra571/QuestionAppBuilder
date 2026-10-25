namespace QuestionAppBuilder.Domain.Exceptions;

public sealed class PreviewScenarioConflictException : Exception
{
    public PreviewScenarioConflictException()
        : base("This preview scenario changed. Reload the scenarios before saving or deleting it.") { }
}
