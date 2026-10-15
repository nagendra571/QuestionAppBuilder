using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Domain.Entities;

public class ApplicationVersion
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public int VersionNumber { get; set; }
    public VersionStatus Status { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? PublishedUtc { get; set; }
    public List<Category> Categories { get; set; } = new();
}
