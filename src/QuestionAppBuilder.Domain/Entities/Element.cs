using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Domain.Entities;

public class Element
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public ElementKind Kind { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? HelpText { get; set; }
    public int SortOrder { get; set; }
    public Guid LogicalKey { get; set; } = Guid.NewGuid();
    public int? VisibilityConditionId { get; set; }
    public Question? Question { get; set; }
}
