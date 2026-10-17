namespace QuestionAppBuilder.Application.Responses;

public sealed class ResponseConflictException : Exception
{
    public ResponseConflictException() : base("This response changed after it was opened. Reload the latest saved revision before continuing.") { }
    public ResponseConflictException(Exception innerException) : base("This response changed after it was opened. Reload the latest saved revision before continuing.", innerException) { }
}
