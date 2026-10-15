namespace QuestionAppBuilder.Domain.Exceptions;

public class DefinitionValidationException : Exception
{
    public DefinitionValidationException(string message)
        : base(message)
    {
    }
}
