using System.ComponentModel.DataAnnotations;

namespace QuestionAppBuilder.Editor.ViewModels;

public sealed class ContentElementFormViewModel
{
    [Required(ErrorMessage = "Element label is required.")]
    [MaxLength(500, ErrorMessage = "Element label must be at most 500 characters.")]
    public string Label { get; set; } = string.Empty;

    [MaxLength(2000, ErrorMessage = "Help text must be at most 2000 characters.")]
    public string? HelpText { get; set; }
}
