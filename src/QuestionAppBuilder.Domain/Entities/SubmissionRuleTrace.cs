namespace QuestionAppBuilder.Domain.Entities;

public class SubmissionRuleTrace
{
    public int Id { get; set; }
    public int SubmissionId { get; set; }
    public int QuestionId { get; set; }
    public int RuleId { get; set; }
    public bool Matches { get; set; }
    public bool Contributes { get; set; }
    public int Points { get; set; }
}
