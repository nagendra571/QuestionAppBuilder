namespace QuestionAppBuilder.Domain.Entities;

public class Application
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ApplicationTypeId { get; set; }
    public int? CurrentVersionId { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public ApplicationType ApplicationType { get; set; } = null!;
    public ApplicationVersion? CurrentVersion { get; set; }
    public List<ApplicationVersion> Versions { get; set; } = new();
}
