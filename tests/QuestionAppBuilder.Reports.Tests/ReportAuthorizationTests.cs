using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Reports.DependencyInjection;
using QuestionAppBuilder.SampleHost.DevAuth;

namespace QuestionAppBuilder.Reports.Tests;

public sealed class ReportAuthorizationTests
{
    [Fact]
    public async Task Filler_and_anonymous_cannot_access_any_report_route()
    {
        using var factory = new ReportsHostFactory();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

        var anonymousResponse = await anonymous.GetAsync("/Reports");
        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Found);
        anonymousResponse.Headers.Location!.AbsolutePath.Should().Be("/Account/Login");

        using var filler = await CreateSignedInClientAsync(factory, "filler");
        var index = await filler.GetAsync("/Reports");
        var submissions = await filler.GetAsync("/Reports/Submissions");
        var details = await filler.GetAsync("/Reports/Detail/1");
        var conventional = await filler.GetAsync("/QuestionAppBuilderReports/Reports/Index");

        new[] { index, submissions, details, conventional }.Select(response => response.StatusCode)
            .Should().OnlyContain(status => status == HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Analyst_can_open_reports_but_designer_cannot()
    {
        using var factory = new ReportsHostFactory();
        using var analyst = await CreateSignedInClientAsync(factory, "analyst");
        using var designer = await CreateSignedInClientAsync(factory, "designer");

        (await analyst.GetAsync("/Reports")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await designer.GetAsync("/Reports")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Custom_reports_base_path_serves_the_host_layout()
    {
        using var factory = new ReportsHostFactory(new Dictionary<string, string?>
        {
            ["QuestionAppBuilderReports:BasePath"] = "/Insights"
        });
        using var analyst = await CreateSignedInClientAsync(factory, "analyst");

        var response = await analyst.GetAsync("/Insights/");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Platform surfaces").And.Contain("Reports").And.Contain("/Insights/")
            .And.Contain("Download summary CSV").And.Contain("/Insights/Export");
    }

    [Fact]
    public void Blank_report_policy_fails_closed_during_registration()
    {
        var services = new ServiceCollection();

        var act = () => services.AddQuestionAppBuilderReports(options => options.ReportPolicy = " ");

        act.Should().Throw<InvalidOperationException>().WithMessage("A host report authorization policy is required.");
    }

    [Fact]
    public async Task Unauthorized_export_writes_no_body()
    {
        using var factory = new ReportsHostFactory();
        using var filler = await CreateSignedInClientAsync(factory, "filler");

        var response = await filler.GetAsync("/Reports/Export?applicationId=1&versionId=1&includeAnswers=true");
        var body = await response.Content.ReadAsByteArrayAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        body.Should().BeEmpty();
    }

    [Fact]
    public async Task Authorized_export_streams_summary_and_validates_answer_scope_first()
    {
        using var factory = new ReportsHostFactory();
        using var analyst = await CreateSignedInClientAsync(factory, "analyst");

        var invalidAnswers = await analyst.GetAsync("/Reports/Export?includeAnswers=true");
        invalidAnswers.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await invalidAnswers.Content.ReadAsStringAsync()).Should().Contain("require both applicationId and versionId");

        using var response = await analyst.GetAsync("/Reports/Export?includeAnswers=false");
        var csv = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        csv.Should().StartWith("Submission ID,Application ID,Application,Version ID,Version,Submitted UTC,Owner Reference,Total Score");
    }

    private static async Task<HttpClient> CreateSignedInClientAsync(ReportsHostFactory factory, string username)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        var html = await client.GetStringAsync("/Account/Login");
        var token = Regex.Match(html, "<input[^>]*name=\\\"__RequestVerificationToken\\\"[^>]*value=\\\"([^\\\"]+)\\\"")
            .Groups[1].Value;
        token.Should().NotBeNullOrWhiteSpace();
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = username, ["Password"] = DevUserStore.SharedPassword, ["__RequestVerificationToken"] = token
        }));
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        return client;
    }

    private sealed class ReportsHostFactory : WebApplicationFactory<Program>
    {
        private readonly IReadOnlyDictionary<string, string?> _settings;
        public ReportsHostFactory(IReadOnlyDictionary<string, string?>? settings = null) => _settings = settings ?? new Dictionary<string, string?>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            foreach (var (key, value) in _settings) builder.UseSetting(key, value);
            builder.ConfigureServices(services =>
            {
                var descriptor = services.Single(service => service.ServiceType == typeof(DbContextOptions<QuestionAppBuilderDbContext>));
                services.Remove(descriptor);
                services.AddDbContext<QuestionAppBuilderDbContext>(options => options.UseInMemoryDatabase($"reports-{Guid.NewGuid():N}"));
            });
        }
    }
}
