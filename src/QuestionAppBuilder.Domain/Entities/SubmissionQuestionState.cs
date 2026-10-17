namespace QuestionAppBuilder.Domain.Entities;

public class SubmissionQuestionState
{
    public int SubmissionId { get; set; }
    public int QuestionId { get; set; }
    public Guid QuestionLogicalKey { get; set; }
    public bool IsApplicable { get; set; }
    public int PointsEarned { get; set; }
}
