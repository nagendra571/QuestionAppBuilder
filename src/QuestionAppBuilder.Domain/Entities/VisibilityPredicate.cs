using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Domain.Entities;

public class VisibilityPredicate
{
    public int Id { get; set; }
    public int VisibilityConditionId { get; set; }
    public int SourceQuestionId { get; set; }
    public Question? SourceQuestion { get; set; }
    public ConditionOperator Operator { get; set; }
    public decimal? NumberValue { get; set; }
    public List<VisibilityPredicateOption> Options { get; set; } = new();
}
