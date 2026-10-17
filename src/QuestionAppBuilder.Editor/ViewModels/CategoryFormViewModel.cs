using System.ComponentModel.DataAnnotations;

namespace QuestionAppBuilder.Editor.ViewModels;

public sealed class CategoryFormViewModel
{
    [Required(ErrorMessage = "Category name is required.")]
    [MaxLength(200, ErrorMessage = "Category name must be at most 200 characters.")]
    public string Name { get; set; } = string.Empty;

}
