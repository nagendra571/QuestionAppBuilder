using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Infrastructure.Persistence;
namespace QuestionAppBuilder.Browser.Tests;

internal sealed class SampleHostFixture : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _databaseName;
    private readonly string _adminConnection;
    private readonly string _databaseConnection;
    private readonly string _certificatePath;
    private readonly string _hostAssemblyPath;
    private readonly ConcurrentQueue<string> _output = new();
    public Uri BaseAddress { get; }

    private SampleHostFixture(Process process, string databaseName, string adminConnection, string databaseConnection, string certificatePath, string hostAssemblyPath, Uri baseAddress)
    {
        _process = process;
        _databaseName = databaseName;
        _adminConnection = adminConnection;
        _databaseConnection = databaseConnection;
        _certificatePath = certificatePath;
        _hostAssemblyPath = hostAssemblyPath;
        BaseAddress = baseAddress;
        process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) _output.Enqueue(eventArgs.Data); };
        process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) _output.Enqueue(eventArgs.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public static async Task<SampleHostFixture> StartAsync()
    {
        var repositoryRoot = FindRepositoryRoot();
        var hostAssembly = ResolveHostAssembly(AppContext.BaseDirectory, repositoryRoot);
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
        var address = new Uri($"https://localhost:{port}");
        var certificatePath = Path.Combine(Path.GetTempPath(), $"qab-browser-{Guid.NewGuid():N}.pfx");
        const string certificatePassword = "QabTestOnly-Temporary-Certificate!";
        using (var rsa = RSA.Create(2048))
        {
            var certificateRequest = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
            subjectAlternativeNames.AddDnsName("localhost");
            subjectAlternativeNames.AddIpAddress(System.Net.IPAddress.Loopback);
            certificateRequest.CertificateExtensions.Add(subjectAlternativeNames.Build());
            using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx, certificatePassword));
        }
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
        start.Environment["ASPNETCORE_Kestrel__Certificates__Default__Path"] = certificatePath;
        start.Environment["ASPNETCORE_Kestrel__Certificates__Default__Password"] = certificatePassword;
        start.Environment["QuestionAppBuilder__ConnectionString"] = connectionBuilder.ConnectionString;

        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the SampleHost test process.");
        var fixture = new SampleHostFixture(process, databaseName, admin.ConnectionString, connectionBuilder.ConnectionString, certificatePath, hostAssembly, address);
        try
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow.AddSeconds(60);
            var latestStatus = "no response";
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                    throw new InvalidOperationException($"SampleHost exited before becoming ready. {fixture.GetDiagnosticLogs()}");
                try
                {
                    using var response = await client.GetAsync(new Uri(address, "/Account/Login"));
                    latestStatus = $"HTTP {(int)response.StatusCode} {response.StatusCode}";
                    if (response.StatusCode == HttpStatusCode.OK)
                        return fixture;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(250);
            }
            throw new TimeoutException($"SampleHost did not become ready within 60 seconds (last response: {latestStatus}).");
        }
        catch (Exception exception)
        {
            await fixture.DisposeAsync();
            if (exception is TimeoutException)
            {
                var output = fixture.GetDiagnosticLogs();
                throw new TimeoutException($"{exception.Message}{Environment.NewLine}{output[^Math.Min(output.Length, 12000)..]}", exception);
            }
            throw;
        }
    }

    public async Task<int> SeedHostSignInApplicationAsync()
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options;
        await using var db = new QuestionAppBuilderDbContext(options);
        var now = DateTime.UtcNow;
        var version = new ApplicationVersion
        {
            VersionNumber = 1, Status = VersionStatus.Published, CreatedUtc = now, PublishedUtc = now,
            DisplayName = "Owner isolation test", ResultVisibility = ResultVisibility.ConfirmationOnly,
            Categories =
            {
                new Category
                {
                    Name = "Applicant", SortOrder = 1,
                    Elements =
                    {
                        new Element
                        {
                            Kind = ElementKind.Question, Label = "Organization name", SortOrder = 1,
                            Question = new Question { AnswerType = AnswerType.Textbox, IsRequired = false }
                        }
                    }
                }
            }
        };
        var application = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "Owner isolation " + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "Owner isolation type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
            CreatedUtc = now, UpdatedUtc = now, AcceptNewResponses = true, AcceptSubmissions = true,
            Versions = { version }
        };
        db.Applications.Add(application);
        await db.SaveChangesAsync();
        application.CurrentVersionId = version.Id;
        await db.SaveChangesAsync();
        return application.Id;
    }

    public async Task<int> SeedDraftPreviewApplicationAsync()
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options;
        await using var db = new QuestionAppBuilderDbContext(options);
        var now = DateTime.UtcNow;
        var version = new ApplicationVersion
        {
            VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = now, DisplayName = "Preview test draft",
            NavigationMode = NavigationMode.Sequential, ResultVisibility = ResultVisibility.Total,
            Categories =
            {
                new Category
                {
                    Name = "First category", SortOrder = 1,
                    Elements =
                    {
                        new Element
                        {
                            Kind = ElementKind.Question, Label = "Requested amount", SortOrder = 1,
                            Question = new Question { AnswerType = AnswerType.Number, IsRequired = true }
                        }
                    }
                },
                new Category
                {
                    Name = "Second category", SortOrder = 2,
                    Elements =
                    {
                        new Element
                        {
                            Kind = ElementKind.Question, Label = "Reason for applying", SortOrder = 1,
                            Question = new Question { AnswerType = AnswerType.Textbox, IsRequired = true }
                        }
                    }
                }
            }
        };
        var application = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "Preview test " + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "Preview type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
            CreatedUtc = now, UpdatedUtc = now, AcceptNewResponses = true, AcceptSubmissions = true,
            Versions = { version }
        };
        db.Applications.Add(application);
        await db.SaveChangesAsync();
        return application.Id;
    }

    public async Task<int> SeedResponsiveAuthoringApplicationAsync()
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options;
        await using var db = new QuestionAppBuilderDbContext(options);
        var now = DateTime.UtcNow;
        var selected = new QuestionOption { Label = "An unusually long option label for narrow responsive layouts", Value = "responsive-selected", SortOrder = 1 };
        var other = new QuestionOption { Label = "Another long option label that should wrap safely", Value = "responsive-other", SortOrder = 2 };
        var version = new ApplicationVersion
        {
            VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = now,
            DisplayName = "ApplicationTitle_" + new string('A', 100),
            NavigationMode = NavigationMode.Sequential, ResultVisibility = ResultVisibility.Total,
            Categories =
            {
                new Category
                {
                    Name = "Category_" + new string('C', 84), SortOrder = 1,
                    Elements =
                    {
                        new Element { Kind = ElementKind.Content, Label = "Content_" + new string('H', 126), HelpText = "Responsive content block", SortOrder = 1 },
                        new Element
                        {
                            Kind = ElementKind.Question, Label = "Question_" + new string('Q', 142), SortOrder = 2,
                            Question = new Question
                            {
                                AnswerType = AnswerType.Checkboxes, IsRequired = true, ScoringMode = ScoringMode.Additive,
                                Options = { selected, other },
                                Rules =
                                {
                                    new ScoringRule
                                    {
                                        Family = RuleFamily.Selection, Points = 7, SortOrder = 1,
                                        MembershipOperator = MembershipOperator.AnyOf,
                                        Options = { new ScoringRuleOption { QuestionOption = selected } }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
        var application = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "ApplicationTitle_" + new string('A', 100) + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "Responsive type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
            CreatedUtc = now, UpdatedUtc = now, AcceptNewResponses = true, AcceptSubmissions = true,
            Versions = { version }
        };
        db.Applications.Add(application);
        await db.SaveChangesAsync();
        return application.Id;
    }

    public async Task<int> CountResponsesAsync()
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options;
        await using var db = new QuestionAppBuilderDbContext(options);
        return await db.ResponseDrafts.CountAsync();
    }

    public async Task<int> CountSubmissionsAsync()
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options;
        await using var db = new QuestionAppBuilderDbContext(options);
        return await db.Submissions.CountAsync();
    }

    public async Task<(bool AcceptNewResponses, bool AcceptSubmissions, int AuditCount, string? ActorKey)> ReadAvailabilityAsync(int applicationId)
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options;
        await using var db = new QuestionAppBuilderDbContext(options);
        var application = await db.Applications.SingleAsync(item => item.Id == applicationId);
        var audit = await db.ApplicationAuditEvents.Where(item => item.ApplicationId == applicationId && item.EventType == "AvailabilityChanged")
            .OrderByDescending(item => item.OccurredUtc).FirstOrDefaultAsync();
        return (application.AcceptNewResponses, application.AcceptSubmissions,
            await db.ApplicationAuditEvents.CountAsync(item => item.ApplicationId == applicationId && item.EventType == "AvailabilityChanged"), audit?.ActorKey);
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
        File.Delete(_certificatePath);
    }

    public string GetDiagnosticLogs()
    {
        var lines = _output.ToArray();
        return $"SampleHost assembly: {_hostAssemblyPath}{Environment.NewLine}{string.Join(Environment.NewLine, lines.TakeLast(160))}";
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
        var configuredRoot = Environment.GetEnvironmentVariable("QAB_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var fullPath = Path.GetFullPath(configuredRoot);
            if (File.Exists(Path.Combine(fullPath, "QuestionAppBuilder.sln"))) return fullPath;
        }
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QuestionAppBuilder.sln"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not find the QuestionAppBuilder solution root.");
    }

    internal static string ResolveHostAssembly(string testOutputDirectory, string repositoryRoot)
    {
        var configuration = testOutputDirectory.Contains("\\release\\", StringComparison.OrdinalIgnoreCase) ? "release" : "debug";
        var artifactsBin = Path.GetFullPath(Path.Combine(testOutputDirectory, "..", ".."));
        var artifactsHost = Path.Combine(artifactsBin, "QuestionAppBuilder.SampleHost", configuration, "QuestionAppBuilder.SampleHost.dll");
        if (File.Exists(artifactsHost)) return artifactsHost;

        var normalProjectOutput = Path.GetFullPath(Path.Combine(repositoryRoot, "tests", "QuestionAppBuilder.Browser.Tests", "bin"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedTestOutput = Path.GetFullPath(testOutputDirectory);
        if (normalizedTestOutput.StartsWith(normalProjectOutput, StringComparison.OrdinalIgnoreCase))
        {
            var projectHost = Path.Combine(repositoryRoot, "samples", "QuestionAppBuilder.SampleHost", "bin", configuration, "net8.0", "QuestionAppBuilder.SampleHost.dll");
            if (File.Exists(projectHost)) return projectHost;
        }

        throw new FileNotFoundException(
            $"Could not find the SampleHost test assembly next to isolated artifacts at '{artifactsHost}'. Refusing to fall back outside the current test build. Test base: '{testOutputDirectory}'.",
            artifactsHost);
    }

}
