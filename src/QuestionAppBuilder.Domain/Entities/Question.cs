using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Domain.Entities;

public class Question
{
    public int Id { get; set; }
    public int ElementId { get; set; }
    public AnswerType AnswerType { get; set; }
    public bool IsRequired { get; set; }
    public string? RequiredMessage { get; set; }

    public string GetRequiredMessage() => string.IsNullOrWhiteSpace(RequiredMessage)
        ? "A response is required." : RequiredMessage.Trim();
    public ScoringMode ScoringMode { get; set; } = ScoringMode.Additive;
    public bool WholeNumbersOnly { get; set; }
    public int? MaxLength { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public int? MinSelections { get; set; }
    public int? MaxSelections { get; set; }
    public List<QuestionOption> Options { get; set; } = new();
    public List<ScoringRule> Rules { get; set; } = new();
}
