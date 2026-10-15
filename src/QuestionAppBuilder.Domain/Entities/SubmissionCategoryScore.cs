namespace QuestionAppBuilder.Domain.Entities;

public class SubmissionCategoryScore
{
    public int Id { get; set; }
    public int SubmissionId { get; set; }
    public int CategoryId { get; set; }
    public int Score { get; set; }
}
