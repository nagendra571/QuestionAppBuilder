using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Domain.Entities;

public class ScoringRule
{
    public int Id { get; set; }
    public int QuestionId { get; set; }
    public RuleFamily Family { get; set; }
    public int Points { get; set; }
    public MembershipOperator? MembershipOperator { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public List<ScoringRuleOption> Options { get; set; } = new();
}
