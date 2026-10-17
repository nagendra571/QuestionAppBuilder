namespace QuestionAppBuilder.Domain.Entities;

public class ScoringRuleOption
{
    public int Id { get; set; }
    public int ScoringRuleId { get; set; }
    public int QuestionOptionId { get; set; }
    public QuestionOption? QuestionOption { get; set; }
}
