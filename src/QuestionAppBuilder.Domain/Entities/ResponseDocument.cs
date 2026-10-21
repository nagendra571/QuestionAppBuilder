namespace QuestionAppBuilder.Domain.Entities;

public sealed class ResponseDocument
{
    public Guid ResponseDraftId { get; set; }
    public int QuestionId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public byte[] Content { get; set; } = [];
}
