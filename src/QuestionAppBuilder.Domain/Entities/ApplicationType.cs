namespace QuestionAppBuilder.Domain.Entities;

public class ApplicationType
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}
