namespace QuestionAppBuilder.Domain.Entities;

public class ResponseAnswer
{
    public Guid Id { get; set; }
    public Guid ResponseDraftId { get; set; }
    public int QuestionId { get; set; }
    public string? TextValue { get; set; }
    public decimal? NumberValue { get; set; }
    public List<ResponseSelectedOption> SelectedOptions { get; set; } = new();
}
