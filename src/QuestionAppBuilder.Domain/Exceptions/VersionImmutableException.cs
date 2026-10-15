namespace QuestionAppBuilder.Domain.Exceptions;

public class VersionImmutableException : Exception
{
    public VersionImmutableException(string message)
        : base(message)
    {
    }
}
