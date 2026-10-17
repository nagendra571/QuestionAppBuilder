namespace QuestionAppBuilder.Domain.Entities;

public class VisibilityPredicateOption
{
    public int VisibilityPredicateId { get; set; }
    public int QuestionOptionId { get; set; }
    public QuestionOption? QuestionOption { get; set; }
}
