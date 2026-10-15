namespace QuestionAppBuilder.Domain.Entities;

public class Answer
{
    public int Id { get; set; }
    public int SubmissionId { get; set; }
    public int QuestionId { get; set; }
    public string? TextValue { get; set; }
    public decimal? NumberValue { get; set; }
    public int PointsEarned { get; set; }
    public List<AnswerSelectedOption> SelectedOptions { get; set; } = new();
}
