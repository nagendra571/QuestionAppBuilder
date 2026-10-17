namespace QuestionAppBuilder.Reports.Options;

public sealed class QuestionAppBuilderReportsOptions
{
    public string BasePath { get; set; } = "/Reports";
    public string ReportPolicy { get; set; } = "QuestionAppBuilder.Report";
    public string LayoutName { get; set; } = "_Layout";
    public string DisplayTimeZone { get; set; } = "UTC";
}
