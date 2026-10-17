using QuestionAppBuilder.Domain.Exceptions;

namespace QuestionAppBuilder.Application.Authoring;

public sealed class DraftConflictException : DefinitionValidationException
{
    public DraftConflictException() : base("This draft changed after it was opened. Reload the current revision before saving or publishing.") { }
}
