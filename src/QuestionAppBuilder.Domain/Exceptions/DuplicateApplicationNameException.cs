namespace QuestionAppBuilder.Domain.Exceptions;

public class DuplicateApplicationNameException : Exception
{
    public DuplicateApplicationNameException(string name)
        : base($"An application named '{name}' already exists.")
    {
    }
}
