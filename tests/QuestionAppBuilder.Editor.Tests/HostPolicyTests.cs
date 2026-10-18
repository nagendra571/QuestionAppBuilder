using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.Editor.DependencyInjection;

namespace QuestionAppBuilder.Editor.Tests;

public sealed class HostPolicyTests
{
    [Fact]
    public void Missing_designer_policy_fails_closed()
    {
        var services = new ServiceCollection();
        var act = () => services.AddQuestionAppBuilderEditor(options => options.DesignPolicy = "  ");
        act.Should().Throw<InvalidOperationException>().WithMessage("*DesignPolicy*");
    }

    [Fact]
    public async Task Conventional_route_cannot_bypass_designer_policy()
    {
        using var factory = new EditorWebApplicationFactory();
        var filler = await factory.CreateSignedInClientAsync("filler");

        (await filler.GetAsync("/ConfigureApp/Applications/Index")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await filler.GetAsync("/QuestionAppBuilderEditor/Applications/Index")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Custom_base_paths_and_layout_render_all_surfaces()
    {
        var configuration = new Dictionary<string, string?>
        {
            ["QuestionAppBuilderEditor:BasePath"] = "/admin/design",
            ["QuestionAppBuilderRuntime:BasePath"] = "/forms",
        };
        using var factory = new EditorWebApplicationFactory(configuration);
        factory.Services.GetRequiredService<QuestionAppBuilder.Editor.Options.QuestionAppBuilderEditorOptions>().BasePath.Should().Be("/admin/design");
        var designer = await factory.CreateSignedInClientAsync("designer");

        var editor = await designer.GetAsync("/admin/design/Applications");
        editor.StatusCode.Should().Be(HttpStatusCode.OK, await editor.Content.ReadAsStringAsync());
        var html = await editor.Content.ReadAsStringAsync();
        html.Should().Contain("href=\"/forms/Mine\"").And.Contain("Platform surfaces");
        var filler = await factory.CreateSignedInClientAsync("filler");
        var mine = await filler.GetAsync("/forms/Mine");
        mine.StatusCode.Should().Be(HttpStatusCode.OK, await mine.Content.ReadAsStringAsync());
    }
}
