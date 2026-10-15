namespace QuestionAppBuilder.Domain.Exceptions;

public class AnswerValidationException : Exception
{
    public AnswerValidationException(string message, IReadOnlyList<int> questionIds)
        : base(message)
    {
        QuestionIds = questionIds;
    }

    public IReadOnlyList<int> QuestionIds { get; }
}
