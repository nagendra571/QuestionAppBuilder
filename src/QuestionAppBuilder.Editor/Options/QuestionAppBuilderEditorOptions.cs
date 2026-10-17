namespace QuestionAppBuilder.Editor.Options;

public sealed class QuestionAppBuilderEditorOptions
{
    public string BasePath { get; set; } = "/ConfigureApp";

    public string DesignPolicy { get; set; } = "QuestionAppBuilder.Design";

    public string LayoutName { get; set; } = "_Layout";
}
