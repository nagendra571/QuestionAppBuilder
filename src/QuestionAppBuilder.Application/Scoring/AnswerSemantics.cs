using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Application.Scoring;

public static class AnswerSemantics
{
    public static bool IsAnswered(AnswerType answerType, AnswerValue? answer) => answerType switch
    {
        AnswerType.Textbox or AnswerType.Textarea or AnswerType.UploadDocument or AnswerType.Date => !string.IsNullOrWhiteSpace(answer?.Text),
        AnswerType.Number => answer?.Number is not null,
        AnswerType.Dropdown or AnswerType.RadioGroup or AnswerType.Checkboxes => answer?.SelectedOptionIds is { Count: > 0 },
        _ => false
    };
}
