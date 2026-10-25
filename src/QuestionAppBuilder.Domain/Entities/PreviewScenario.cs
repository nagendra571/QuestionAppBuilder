namespace QuestionAppBuilder.Domain.Entities;

/// <summary>Shared authoring test data, independent of any published definition or response.</summary>
public class PreviewScenario
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int ApplicationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
