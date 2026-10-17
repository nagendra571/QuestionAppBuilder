namespace QuestionAppBuilder.Domain.Exceptions;

public sealed class InvalidAccessLinkException : Exception
{
    public InvalidAccessLinkException() : base("This access link is invalid, expired, or already used.") { }
}
