using System.ComponentModel.DataAnnotations;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Editor.ViewModels;

public sealed class ApplicationCreateViewModel
{
    [Required(ErrorMessage = "Name is required.")]
    [MaxLength(200, ErrorMessage = "Name must be at most 200 characters.")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Application type is required.")]
    public int ApplicationTypeId { get; set; }

    public IReadOnlyList<ApplicationType> Types { get; set; } = Array.Empty<ApplicationType>();
}
