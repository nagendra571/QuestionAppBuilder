using System.ComponentModel.DataAnnotations;
using System.Text;
using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Editor.ViewModels;

public sealed class QuestionFormViewModel
{
    [Required(ErrorMessage = "Question label is required.")]
    [MaxLength(500, ErrorMessage = "Question label must be at most 500 characters.")]
    public string Label { get; set; } = string.Empty;

    public AnswerType AnswerType { get; set; }

    public bool IsRequired { get; set; } = true;

    [MaxLength(2000, ErrorMessage = "Help text must be at most 2000 characters.")]
    public string? HelpText { get; set; }

    public IReadOnlyList<AnswerType> AnswerTypes { get; } = Enum.GetValues<AnswerType>();

    public static string SplitPascalCase(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character))
            {
                builder.Append(' ');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
