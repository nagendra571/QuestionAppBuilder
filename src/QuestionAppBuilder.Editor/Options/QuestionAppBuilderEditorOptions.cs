namespace QuestionAppBuilder.Editor.Options;

public sealed class QuestionAppBuilderEditorOptions
{
    public string BasePath { get; set; } = "/ConfigureApp";

    public string? DesignerRole { get; set; }

    public string LayoutName { get; set; } = "_Layout";
}
