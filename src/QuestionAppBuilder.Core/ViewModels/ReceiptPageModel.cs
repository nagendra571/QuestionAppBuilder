using QuestionAppBuilder.Application.Responses;

namespace QuestionAppBuilder.Core.ViewModels;

public sealed record ReceiptPageModel(SubmissionReceipt Receipt, string LayoutName)
{
    public int Category { get; init; }
    public bool IsTabbed { get; init; }
}
