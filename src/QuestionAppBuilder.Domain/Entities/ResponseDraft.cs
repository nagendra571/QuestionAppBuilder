using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Domain.Entities;

public class ResponseDraft
{
    public Guid Id { get; set; }
    public int ApplicationId { get; set; }
    public int ApplicationVersionId { get; set; }
    public string OwnerKey { get; set; } = string.Empty;
    public ResponseStatus Status { get; set; } = ResponseStatus.InProgress;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    public Guid RevisionNonce { get; set; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public List<ResponseAnswer> Answers { get; set; } = new();
}
