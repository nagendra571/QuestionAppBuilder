using FluentAssertions;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Application.Tests.Scoring;

public class UploadDocumentTests
{
    [Fact]
    public void Upload_document_is_a_supported_answer_and_requires_a_stored_filename()
    {
        Enum.TryParse<AnswerType>("UploadDocument", out var type).Should().BeTrue();
        AnswerSemantics.IsAnswered(type, new AnswerValue("budget.pdf", null, null)).Should().BeTrue();
        AnswerSemantics.IsAnswered(type, new AnswerValue(null, null, null)).Should().BeFalse();
    }
}
