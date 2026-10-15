namespace QuestionAppBuilder.Domain.Entities;

public class QuestionOption
{
    public int Id { get; set; }
    public int QuestionId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}
