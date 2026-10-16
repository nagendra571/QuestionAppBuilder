using System.ComponentModel.DataAnnotations;

namespace QuestionAppBuilder.Editor.ViewModels;

public sealed class OptionFormViewModel
{
    [Required(ErrorMessage = "Option label is required.")]
    [MaxLength(200, ErrorMessage = "Option label must be at most 200 characters.")]
    public string Label { get; set; } = string.Empty;

    [Required(ErrorMessage = "Option value is required.")]
    [MaxLength(100, ErrorMessage = "Option value must be at most 100 characters.")]
    public string Value { get; set; } = string.Empty;
}
