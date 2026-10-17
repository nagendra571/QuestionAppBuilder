using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Editor.ViewModels;

public sealed class ApplicationTypesViewModel
{
    public IReadOnlyList<ApplicationType> Types { get; set; } = Array.Empty<ApplicationType>();
    public string NewName { get; set; } = string.Empty;
}
