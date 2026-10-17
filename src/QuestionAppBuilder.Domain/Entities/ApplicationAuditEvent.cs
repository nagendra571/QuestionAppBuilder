namespace QuestionAppBuilder.Domain.Entities;

public class ApplicationAuditEvent
{
    public long Id { get; set; }
    public int ApplicationId { get; set; }
    public int? ApplicationVersionId { get; set; }
    public string ActorKey { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime OccurredUtc { get; set; }
}
