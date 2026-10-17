namespace QuestionAppBuilder.Domain.Entities;

public class Submission
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public int ApplicationVersionId { get; set; }
    public string? UserId { get; set; }
    public DateTime SubmittedUtc { get; set; }
    public int TotalScore { get; set; }
    public Guid? ResponseId { get; set; }
    public Guid? RequestId { get; set; }
    public string EngineVersion { get; set; } = "legacy-v1";
    public List<Answer> Answers { get; set; } = new();
    public List<SubmissionCategoryScore> CategoryScores { get; set; } = new();
    public List<SubmissionQuestionState> QuestionStates { get; set; } = new();
    public List<SubmissionRuleTrace> RuleTraces { get; set; } = new();
}
