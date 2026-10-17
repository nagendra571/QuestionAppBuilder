namespace QuestionAppBuilder.Domain.Entities;

public class ResponseAccessToken
{
    public Guid Id { get; set; }
    public byte[] TokenDigest { get; set; } = Array.Empty<byte>();
    public int ApplicationId { get; set; }
    public int ApplicationVersionId { get; set; }
    public Guid? ResponseId { get; set; }
    public string OwnerKey { get; set; } = string.Empty;
    public string VerifiedEmail { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? ConsumedUtc { get; set; }
}
