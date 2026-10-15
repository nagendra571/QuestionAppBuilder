namespace QuestionAppBuilder.Domain.Entities;

public class Category
{
    public int Id { get; set; }
    public int ApplicationVersionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsIndependent { get; set; }
    public List<Element> Elements { get; set; } = new();
}
