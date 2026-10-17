using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Playwright;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class AuthoringJourneyTests
{
    [Fact]
    public async Task Designer_can_create_and_edit_a_draft_and_keep_local_input_after_conflict()
    {
        await using var host = await SampleHostFixture.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync(new Uri(host.BaseAddress, "/Account/Login").ToString());
        await page.GetByLabel("Username").FillAsync("designer");
        await page.GetByLabel("Password").FillAsync("DevPassword!1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();

        await page.GetByRole(AriaRole.Heading, new() { Name = "Applications" }).WaitForAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "New application" }).ClickAsync();
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync("Browser-authored " + Guid.NewGuid().ToString("N"));
        var type = await page.Locator("select[name='ApplicationTypeId'] option").First.GetAttributeAsync("value");
        type.Should().NotBeNullOrWhiteSpace("database migrations seed an application classification");
        await page.Locator("select[name='ApplicationTypeId']").SelectOptionAsync(type!);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create application" }).ClickAsync();

        await page.GetByRole(AriaRole.Link, new() { Name = "Open authoring workspace" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Questionnaire settings" }).WaitForAsync();
        var cssStatus = await page.EvaluateAsync<int>("async () => (await fetch('/_content/QuestionAppBuilder.Editor/css/editor.css')).status");
        var jsStatus = await page.EvaluateAsync<int>("async () => (await fetch('/_content/QuestionAppBuilder.Editor/js/editor.js')).status");
        cssStatus.Should().Be((int)HttpStatusCode.OK);
        jsStatus.Should().Be((int)HttpStatusCode.OK);

        await page.Locator("[name='categoryName']").FillAsync("Applicant details");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add category" }).ClickAsync();
        (await page.Locator("[name='Categories[0].Name']").InputValueAsync()).Should().Be("Applicant details");
        await page.Locator("[name='elementLabel']").FillAsync("Application method");
        await page.Locator("[name='elementType']").SelectOptionAsync("RadioGroup");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add element" }).ClickAsync();
        (await page.Locator("[name='Elements[0].Label']").InputValueAsync()).Should().Be("Application method");
        await page.Locator("[name='optionLabel']").FillAsync("Online");
        await page.Locator("[name='optionValue']").FillAsync("online");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add option" }).ClickAsync();
        (await page.Locator("[name='Options[0].Label']").InputValueAsync()).Should().Be("Online");
        var duplicateButton = page.GetByRole(AriaRole.Button, new() { Name = "Duplicate element" });
        var duplicateResponseTask = page.WaitForResponseAsync(response => response.Url.Contains("/Structure/DuplicateElement", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 10000 });
        var duplicateWorkspaceTask = page.WaitForResponseAsync(response => response.Request.Method == "GET" && response.Url.Contains("/Drafts/", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 10000 });
        await duplicateButton.ClickAsync();
        (await duplicateResponseTask).Status.Should().Be((int)HttpStatusCode.OK);
        (await duplicateWorkspaceTask).Status.Should().Be((int)HttpStatusCode.OK);
        await page.Locator("[name='Elements[1].Label']").WaitForAsync();
        (await page.Locator("[name='Elements[1].Label']").InputValueAsync()).Should().Be("Application method copy");
        var moveResponseTask = page.WaitForResponseAsync(response => response.Url.Contains("/Structure/MoveElement", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 10000 });
        var movedWorkspaceTask = page.WaitForResponseAsync(response => response.Request.Method == "GET" && response.Url.Contains("/Drafts/", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 10000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Move Application method copy up" }).ClickAsync();
        (await moveResponseTask).Status.Should().Be((int)HttpStatusCode.OK);
        (await movedWorkspaceTask).Status.Should().Be((int)HttpStatusCode.OK);
        await page.Locator("[name='Elements[0].Label']").WaitForAsync();
        (await page.Locator("[name='Elements[0].Label']").InputValueAsync()).Should().Be("Application method copy");

        var displayName = page.Locator("[name='Version.DisplayName']");
        var savedRequest = page.WaitForResponseAsync(response =>
            response.Url.Contains("/Drafts/", StringComparison.Ordinal) && response.Url.EndsWith("/Save", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 10000 });
        await displayName.FillAsync("Saved from a real browser");
        var saveResponse = await savedRequest;
        if (saveResponse.Status != (int)HttpStatusCode.OK)
            throw new InvalidOperationException($"Draft autosave returned {saveResponse.Status}: {await saveResponse.TextAsync()}");
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "All changes saved" }).WaitForAsync(new() { Timeout = 10000 });
        (await displayName.InputValueAsync()).Should().Be("Saved from a real browser");

        await page.RouteAsync("**/ConfigureApp/Drafts/*/Save", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = (int)HttpStatusCode.Conflict,
            ContentType = "application/json",
            Body = "{\"message\":\"The draft changed elsewhere. Your edits are still here.\"}"
        }));
        await page.Locator("#draft-workspace").EvaluateAsync("form => form.dataset.saveDelay = '10'");
        var conflictRequest = page.WaitForResponseAsync(response => response.Url.EndsWith("/Save", StringComparison.Ordinal),
            new PageWaitForResponseOptions { Timeout = 5000 });
        await displayName.FillAsync("Retain this value after a conflict");
        var conflictResponse = await conflictRequest;
        conflictResponse.Status.Should().Be((int)HttpStatusCode.Conflict);
        await page.WaitForTimeoutAsync(100);
        (await page.GetByRole(AriaRole.Status).InnerTextAsync()).Should().Contain("The draft changed elsewhere");
        (await displayName.InputValueAsync()).Should().Be("Retain this value after a conflict");
    }

    private sealed class SampleHostFixture : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly string _databaseName;
        private readonly string _adminConnection;
        private readonly Task<string> _output;
        public Uri BaseAddress { get; }

        private SampleHostFixture(Process process, string databaseName, string adminConnection, Uri baseAddress)
        {
            _process = process;
            _databaseName = databaseName;
            _adminConnection = adminConnection;
            BaseAddress = baseAddress;
            _output = ReadOutputAsync(process);
        }

        public static async Task<SampleHostFixture> StartAsync()
        {
            var configured = Environment.GetEnvironmentVariable("QAB_TEST_SQLSERVER");
            var admin = new SqlConnectionStringBuilder(string.IsNullOrWhiteSpace(configured)
                ? "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true"
                : configured);
            admin.InitialCatalog = "master";
            admin.ConnectTimeout = 5;
            var databaseName = $"QAB_Authoring_{Guid.NewGuid():N}";
            await using (var connection = new SqlConnection(admin.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{databaseName}]";
                await command.ExecuteNonQueryAsync();
            }

            var connectionBuilder = new SqlConnectionStringBuilder(admin.ConnectionString)
            {
                InitialCatalog = databaseName,
                ConnectTimeout = 5
            };
            var port = ReservePort();
            var address = new Uri($"http://127.0.0.1:{port}");
            var repositoryRoot = FindRepositoryRoot();
            var artifactBin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "bin"));
            var hostAssembly = Path.Combine(artifactBin, "QuestionAppBuilder.SampleHost", "debug", "QuestionAppBuilder.SampleHost.dll");
            if (!File.Exists(hostAssembly))
                throw new FileNotFoundException($"The Browser.Tests project reference did not build the SampleHost test assembly at '{hostAssembly}' (test base '{AppContext.BaseDirectory}').", hostAssembly);
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(hostAssembly);
            start.ArgumentList.Add("--urls");
            start.ArgumentList.Add(address.ToString());
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["ASPNETCORE_URLS"] = address.ToString();
            start.Environment["QuestionAppBuilder__ConnectionString"] = connectionBuilder.ConnectionString;
            start.Environment["QuestionAppBuilderEditor__DesignerRole"] = "Designer";

            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the SampleHost test process.");
            var fixture = new SampleHostFixture(process, databaseName, admin.ConnectionString, address);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline)
                {
                    if (process.HasExited)
                        throw new InvalidOperationException($"SampleHost exited before becoming ready. {await fixture._output}");
                    try
                    {
                        using var response = await client.GetAsync(new Uri(address, "/Account/Login"));
                        if (response.StatusCode == HttpStatusCode.OK)
                            return fixture;
                    }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) { }
                    await Task.Delay(250);
                }
                throw new TimeoutException("SampleHost did not become ready within 60 seconds.");
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            _process.Dispose();
            await using var connection = new SqlConnection(_adminConnection);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"IF DB_ID(@databaseName) IS NOT NULL BEGIN ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]; END";
            command.Parameters.AddWithValue("@databaseName", _databaseName);
            await command.ExecuteNonQueryAsync();
        }

        private static int ReservePort()
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "QuestionAppBuilder.sln"))) return directory.FullName;
            }
            throw new DirectoryNotFoundException("Could not find the QuestionAppBuilder solution root.");
        }

        private static async Task<string> ReadOutputAsync(Process process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdout, stderr);
            return stdout.Result + Environment.NewLine + stderr.Result;
        }
    }
}
