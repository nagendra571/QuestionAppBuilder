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
    public NavigationMode NavigationMode { get; set; } = NavigationMode.Sequential;
    public RespondentAccessMode RespondentAccessMode { get; set; } = RespondentAccessMode.HostSignIn;
    public ResultVisibility ResultVisibility { get; set; } = ResultVisibility.Total;
    public string SubmitLabel { get; set; } = "Submit application";
    public string ConfirmationText { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    public Guid RevisionNonce { get; set; } = Guid.NewGuid();
    public List<Category> Categories { get; set; } = new();
}
