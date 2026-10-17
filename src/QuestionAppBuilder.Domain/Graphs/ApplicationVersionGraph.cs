using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Domain.Graphs;

public class ApplicationVersionGraph
{
    public required ApplicationVersion Version { get; init; }
    public required IReadOnlyList<Category> Categories { get; init; }
    public IReadOnlyList<VisibilityCondition> VisibilityConditions { get; set; } = Array.Empty<VisibilityCondition>();
}
