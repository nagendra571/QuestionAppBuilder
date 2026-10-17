using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Domain.Entities;

public class ScoringRule
{
    public int Id { get; set; }
    public int QuestionId { get; set; }
    public RuleFamily Family { get; set; }
    public int Points { get; set; }
    public int SortOrder { get; set; }
    public Guid LogicalKey { get; set; } = Guid.NewGuid();
    public bool MinInclusive { get; set; } = true;
    public bool MaxInclusive { get; set; } = true;
    public MembershipOperator? MembershipOperator { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public List<ScoringRuleOption> Options { get; set; } = new();
}
