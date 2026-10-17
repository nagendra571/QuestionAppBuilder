namespace QuestionAppBuilder.Domain.Exceptions;

public sealed class DuplicateApplicationTypeNameException : Exception
{
    public DuplicateApplicationTypeNameException(string name) : base($"An application type named '{name}' already exists.") { }
}
