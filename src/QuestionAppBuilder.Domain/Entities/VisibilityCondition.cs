using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Domain.Entities;

public class VisibilityCondition
{
    public int Id { get; set; }
    public int ApplicationVersionId { get; set; }
    public ConditionJunction Junction { get; set; } = ConditionJunction.All;
    public List<VisibilityPredicate> Predicates { get; set; } = new();
}
