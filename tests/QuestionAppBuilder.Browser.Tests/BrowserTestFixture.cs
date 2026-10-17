using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using QuestionAppBuilder.Application.Access;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Core.DependencyInjection;
using QuestionAppBuilder.Core.Routing;
using QuestionAppBuilder.Runtime.Tests;

namespace QuestionAppBuilder.Browser.Tests;

internal sealed class BrowserTestFixture : IAsyncDisposable
{
    private readonly WebApplication _app;
    public Uri BaseAddress { get; }
    public TestHostFixture.FakeRuntime Runtime { get; }

    private BrowserTestFixture(WebApplication app, Uri baseAddress, TestHostFixture.FakeRuntime runtime)
    { _app = app; BaseAddress = baseAddress; Runtime = runtime; }

    public static async Task<BrowserTestFixture> StartAsync()
    {
        var runtime = new TestHostFixture.FakeRuntime();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseStaticWebAssets();
        builder.WebHost.UseUrls("https://127.0.0.1:0");
        builder.Services.AddQuestionAppBuilderRuntime(options =>
        { options.FillPolicy = "Fill"; options.BasePath = "/App"; options.LayoutName = null!; options.PublicBaseUri = new Uri("https://forms.example.test"); });
        builder.Services.AddSingleton<IDefinitionEvaluator>(new DefinitionEvaluator());
        builder.Services.AddSingleton<IResponseService>(runtime);
        builder.Services.AddSingleton<ISubmissionCoordinator>(runtime);
        builder.Services.AddSingleton<IResponseAccessService>(runtime);
        var app = builder.Build();
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "CoreAssets")),
            RequestPath = "/_content/QuestionAppBuilder.Core"
        });
        app.UseRouting(); app.UseAuthentication(); app.UseRateLimiter(); app.UseAuthorization();
        app.MapQuestionAppBuilderRuntime();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new BrowserTestFixture(app, new Uri(address), runtime);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
