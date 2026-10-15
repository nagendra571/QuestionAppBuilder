namespace QuestionAppBuilder.Domain.Entities;

public class Submission
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public int ApplicationVersionId { get; set; }
    public string? UserId { get; set; }
    public DateTime SubmittedUtc { get; set; }
    public int TotalScore { get; set; }
    public List<Answer> Answers { get; set; } = new();
    public List<SubmissionCategoryScore> CategoryScores { get; set; } = new();
}
