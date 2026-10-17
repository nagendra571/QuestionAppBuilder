using QuestionAppBuilder.Application.Responses;

namespace QuestionAppBuilder.Core.ViewModels;

public sealed record ReceiptPageModel(SubmissionReceipt Receipt, string LayoutName);
