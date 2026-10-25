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
using QuestionAppBuilder.Domain.Composition;
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

    public async Task<string?> ReadSavedAnswerAsync(Guid responseId, int questionId)
    {
        await using var db = new QuestionAppBuilderDbContext(new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options);
        return await db.ResponseAnswers.Where(a => a.ResponseDraftId == responseId && a.QuestionId == questionId).Select(a => a.TextValue).SingleOrDefaultAsync();
    }

    public async Task<int> SeedDocumentApplicationAsync()
    {
        var appId = await SeedDraftPreviewApplicationAsync();
        await using var db = new QuestionAppBuilderDbContext(new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options);
        var element = await db.Elements.Include(e => e.Question).SingleAsync(e => e.Label == "Requested amount" && e.Question != null);
        element.Label = "Supporting document";
        element.Question!.AnswerType = AnswerType.UploadDocument;
        element.Question.RequiredMessage = "Please upload your supporting document.";
        await db.SaveChangesAsync();
        return appId;
    }

    public async Task<int> SeedFollowUpApplicationAsync(AnswerType answerType)
    {
        var appId = await SeedDraftPreviewApplicationAsync();
        await using var db = new QuestionAppBuilderDbContext(new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options);
        var element = await db.Elements.Include(e => e.Question).SingleAsync(e => e.Label == "Requested amount" && e.Question != null);
        element.Label = "Organization type";
        element.Question!.AnswerType = answerType;
        element.Question.Options.Add(new QuestionOption { Label = "Nonprofit", Value = "nonprofit", SortOrder = 1 });
        await db.SaveChangesAsync();
        return appId;
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

    public async Task<int> SeedContentVisibilityPreviewApplicationAsync()
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options;
        await using var db = new QuestionAppBuilderDbContext(options);
        var now = DateTime.UtcNow;
        var source = new Question { AnswerType = AnswerType.Textbox, IsRequired = true };
        var sourceElement = new Element { Kind = ElementKind.Question, Label = "Visibility trigger", SortOrder = 1, Question = source };
        var contentElement = new Element { Kind = ElementKind.Content, Label = "Conditional preview instructions", SortOrder = 2 };
        var dynamicContentElement = new Element { Kind = ElementKind.Content, Label = "Dynamic preview instructions", SortOrder = 3 };
        var firstCategory = new Category { Name = "First category", SortOrder = 1, Elements = { sourceElement, contentElement, dynamicContentElement } };
        var condition = new VisibilityCondition { Junction = ConditionJunction.All,
            Predicates = { new VisibilityPredicate { SourceQuestion = source, Operator = ConditionOperator.Answered } } };
        contentElement.VisibilityCondition = condition;
        var version = new ApplicationVersion
        {
            VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = now, DisplayName = "Content visibility preview",
            NavigationMode = NavigationMode.Sequential,
            CompositionJson = new CompositionDefinition
            {
                DynamicText = new DynamicTextDefinition
                {
                    Templates = [new DynamicTemplate
                    {
                        Slot = DynamicTextSlot.ElementLabel, OwnerKey = dynamicContentElement.LogicalKey, HideWhenUnavailable = true,
                        Parts = [new DynamicTextPart { Kind = DynamicPartKind.Value,
                            Source = new DynamicSource { Kind = DynamicSourceKind.Question, Key = sourceElement.LogicalKey }, Fallback = "" }]
                    }]
                }
            }.ToJson(),
            Categories = { firstCategory,
                new Category { Name = "Second category", SortOrder = 2, Elements = { new Element { Kind = ElementKind.Question,
                    Label = "Finish preview", SortOrder = 1, Question = new Question { AnswerType = AnswerType.Textbox, IsRequired = true } } } } },
            VisibilityConditions = { condition }
        };
        var application = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "Content visibility preview " + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "Content visibility type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
            CreatedUtc = now, UpdatedUtc = now, AcceptNewResponses = true, AcceptSubmissions = true,
            Versions = { version }
        };
        db.Applications.Add(application);
        await db.SaveChangesAsync();
        return application.Id;
    }

    public async Task<(int ApplicationId, Guid RuleId, Guid CombinedScoreRuleId)> SeedCombinedValidationPreviewApplicationAsync()
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options;
        await using var db = new QuestionAppBuilderDbContext(options);
        var amount = new Question { AnswerType = AnswerType.Number, IsRequired = false };
        var amountElement = new Element { Kind = ElementKind.Question, Label = "Requested amount", SortOrder = 1, Question = amount };
        var category = new Category { Name = "Funding", SortOrder = 1, Elements = { amountElement } };
        var rule = new CompositionRule
        {
            Name = "Minimum amount", CategoryKey = category.LogicalKey, TargetQuestionKey = amountElement.LogicalKey,
            Kind = RulePurpose.Validation, Message = "Increase the requested amount.", ShowWhileAnswering = false,
            Checks =
            [
                new ValueCheck { Left = new ValueSource { Kind = SourceKind.Question, Key = amountElement.LogicalKey },
                    Comparison = Comparison.GreaterThanOrEqual, Right = new ValueSource { Kind = SourceKind.Constant, Value = 0 } },
                new ValueCheck { Left = new ValueSource { Kind = SourceKind.Question, Key = amountElement.LogicalKey },
                    Comparison = Comparison.GreaterThanOrEqual, Right = new ValueSource { Kind = SourceKind.Constant, Value = 100 } },
                new ValueCheck { Left = new ValueSource { Kind = SourceKind.Question, Key = amountElement.LogicalKey },
                    Comparison = Comparison.LessThanOrEqual, Right = new ValueSource { Kind = SourceKind.Constant, Value = 1000 } }
            ]
        };
        var scoreRule = new CompositionRule
        {
            Name = "First funding award", CategoryKey = category.LogicalKey, TargetQuestionKey = amountElement.LogicalKey,
            Kind = RulePurpose.Score, Points = 5, BandGroup = "Funding cap",
            Checks = [new ValueCheck { Left = new ValueSource { Kind = SourceKind.Question, Key = amountElement.LogicalKey },
                Comparison = Comparison.GreaterThanOrEqual, Right = new ValueSource { Kind = SourceKind.Constant, Value = 10 } }]
        };
        var suppressedRule = new CompositionRule
        {
            Name = "Second funding award", CategoryKey = category.LogicalKey, TargetQuestionKey = amountElement.LogicalKey,
            Kind = RulePurpose.Score, Points = 3, BandGroup = " funding CAP ",
            Checks = [new ValueCheck { Left = new ValueSource { Kind = SourceKind.Question, Key = amountElement.LogicalKey },
                Comparison = Comparison.GreaterThanOrEqual, Right = new ValueSource { Kind = SourceKind.Constant, Value = 10 } }]
        };
        var composition = new CompositionDefinition { Rules = [rule, scoreRule, suppressedRule] };
        var version = new ApplicationVersion
        {
            VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = DateTime.UtcNow,
            DisplayName = "Combined validation preview", NavigationMode = NavigationMode.Sequential,
            CompositionJson = composition.ToJson(), Categories = { category }
        };
        var application = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "Combined validation preview " + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "Combined validation type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, AcceptNewResponses = true,
            AcceptSubmissions = true, Versions = { version }
        };
        db.Applications.Add(application);
        await db.SaveChangesAsync();
        return (application.Id, rule.Id, scoreRule.Id);
    }

    public async Task<int> SeedAllHiddenPreviewApplicationAsync()
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options;
        await using var db = new QuestionAppBuilderDbContext(options);
        var source = new Question { AnswerType = AnswerType.Textbox, IsRequired = true };
        var questionElement = new Element { Kind = ElementKind.Question, Label = "Hidden question", SortOrder = 1, Question = source };
        var contentElement = new Element { Kind = ElementKind.Content, Label = "Hidden content", SortOrder = 2 };
        var category = new Category { Name = "Hidden section", SortOrder = 1, Elements = { questionElement, contentElement } };
        var condition = new VisibilityCondition { Junction = ConditionJunction.All,
            Predicates = { new VisibilityPredicate { SourceQuestion = source, Operator = ConditionOperator.Answered } } };
        category.VisibilityCondition = condition;
        var version = new ApplicationVersion
        {
            VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = DateTime.UtcNow,
            DisplayName = "All hidden preview", NavigationMode = NavigationMode.Sequential,
            Categories = { category }, VisibilityConditions = { condition }
        };
        var application = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "All hidden preview " + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "All hidden preview type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            AcceptNewResponses = true, AcceptSubmissions = true, Versions = { version }
        };
        db.Applications.Add(application);
        await db.SaveChangesAsync();
        return application.Id;
    }

    public async Task<int> SeedDependencyInspectorApplicationAsync()
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options;
        await using var db = new QuestionAppBuilderDbContext(options);
        var now = DateTime.UtcNow;
        var question = new Question { AnswerType = AnswerType.Number, IsRequired = true };
        var source = new Element { Kind = ElementKind.Question, Label = "Annual income", SortOrder = 1, Question = question };
        var note = new Element { Kind = ElementKind.Content, Label = "Income note", SortOrder = 2 };
        var category = new Category { Name = "Applicant", SortOrder = 1, Elements = { source, note } };
        var condition = new VisibilityCondition { Junction = ConditionJunction.All,
            Predicates = { new VisibilityPredicate { SourceQuestion = question, Operator = ConditionOperator.Answered } } };
        note.VisibilityCondition = condition;
        var calculationId = Guid.NewGuid(); var variableId = Guid.NewGuid();
        var composition = new CompositionDefinition
        {
            Groups = [new QuestionGroup { Name = "Applicant data", CategoryKey = category.LogicalKey, Rows = [new GroupRow { QuestionKey = source.LogicalKey }] }],
            Calculations = [new CalculatedField { Id = calculationId, Name = "Income subtotal", CategoryKey = category.LogicalKey,
                Sources = [new ValueSource { Kind = SourceKind.Question, Key = source.LogicalKey }] }],
            Rules = [new CompositionRule { Name = "Income review", CategoryKey = category.LogicalKey, Kind = RulePurpose.Validation,
                Checks = [new ValueCheck { Left = new ValueSource { Kind = SourceKind.Calculation, Key = calculationId }, Comparison = Comparison.GreaterThan,
                    Right = new ValueSource { Kind = SourceKind.Constant, Value = 0 } }] }],
            DynamicText = new DynamicTextDefinition
            {
                Variables = [new DynamicVariable { Id = variableId, Name = "Income display", Sources = [new DynamicSource { Kind = DynamicSourceKind.Calculation, Key = calculationId }] }],
                Templates = [new DynamicTemplate { Slot = DynamicTextSlot.ElementLabel, OwnerKey = note.LogicalKey,
                    Parts = [new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Variable, Key = variableId } }] }]
            }
        };
        var version = new ApplicationVersion { VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = now,
            DisplayName = "Dependency inspector test", NavigationMode = NavigationMode.Sequential,
            CompositionJson = composition.ToJson(), Categories = { category }, VisibilityConditions = { condition } };
        var application = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "Dependency inspector " + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "Dependency type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
            CreatedUtc = now, UpdatedUtc = now, AcceptNewResponses = true, AcceptSubmissions = true, Versions = { version }
        };
        db.Applications.Add(application);
        await db.SaveChangesAsync();
        return application.Id;
    }

    public async Task<int> SeedCompositionApplicationAsync()
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options;
        await using var db = new QuestionAppBuilderDbContext(options);
        var now = DateTime.UtcNow;
        var version = new ApplicationVersion
        {
            VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = now, DisplayName = "Composition pilot",
            NavigationMode = NavigationMode.Sequential, ResultVisibility = ResultVisibility.Total,
            Categories =
            {
                new Category
                {
                    Name = "Budget", SortOrder = 1,
                    Elements =
                    {
                        new Element { Kind = ElementKind.Question, Label = "Requested amount", SortOrder = 1,
                            Question = new Question { AnswerType = AnswerType.Number, IsRequired = true } },
                        new Element { Kind = ElementKind.Question, Label = "Matching amount", SortOrder = 2,
                            Question = new Question { AnswerType = AnswerType.Number, IsRequired = true } }
                    }
                }
            }
        };
        var application = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "Composition browser " + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "Composition type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
            CreatedUtc = now, UpdatedUtc = now, AcceptNewResponses = true, AcceptSubmissions = true,
            Versions = { version }
        };
        db.Applications.Add(application);
        await db.SaveChangesAsync();
        return application.Id;
    }

    public async Task<int> SeedDynamicTextApplicationAsync()
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options;
        await using var db = new QuestionAppBuilderDbContext(options);
        var now = DateTime.UtcNow;
        var content = new Element { Kind = ElementKind.Content, Label = "Applicant details", SortOrder = 1 };
        var first = new Element { Kind = ElementKind.Question, Label = "First name", SortOrder = 2, Question = new Question { AnswerType = AnswerType.Textbox, IsRequired = true } };
        var middle = new Element { Kind = ElementKind.Question, Label = "Middle name", SortOrder = 3, Question = new Question { AnswerType = AnswerType.Textbox, IsRequired = false } };
        var last = new Element { Kind = ElementKind.Question, Label = "Last name", SortOrder = 4, Question = new Question { AnswerType = AnswerType.Textbox, IsRequired = true } };
        var birthDate = new Element { Kind = ElementKind.Question, Label = "Date of birth", SortOrder = 5, Question = new Question { AnswerType = AnswerType.Date, IsRequired = true } };
        var category = new Category { Name = "Applicant", SortOrder = 1, Elements = { content, first, middle, last, birthDate } };
        var formattedBirthDate = new DynamicVariable
        {
            Name = "Formatted birth date", Operation = DynamicOperation.FormatDate, DateFormat = "MMM d, yyyy",
            Sources = { new DynamicSource { Kind = DynamicSourceKind.Question, Key = birthDate.LogicalKey } }
        };
        var age = new DynamicVariable
        {
            Name = "Age", Operation = DynamicOperation.Age,
            Sources = { new DynamicSource { Kind = DynamicSourceKind.Question, Key = birthDate.LogicalKey }, new DynamicSource { Kind = DynamicSourceKind.Today } }
        };
        var composition = new CompositionDefinition();
        composition.DynamicText.Variables.AddRange([formattedBirthDate, age]);
        composition.DynamicText.Templates.AddRange([
            new DynamicTemplate { Slot = DynamicTextSlot.ElementLabel, OwnerKey = birthDate.LogicalKey, Parts = { new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = "Date of birth for " }, new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Question, Key = first.LogicalKey }, Fallback = "you" } } },
            new DynamicTemplate { Slot = DynamicTextSlot.ElementHelpText, OwnerKey = last.LogicalKey, Parts = { new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = "Birth date (ISO): " }, new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Question, Key = birthDate.LogicalKey }, Fallback = "not provided" } } },
            new DynamicTemplate { Slot = DynamicTextSlot.ElementLabel, OwnerKey = content.LogicalKey, HideWhenUnavailable = true, Parts = { new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = "Applicant details for " }, new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Question, Key = first.LogicalKey } } } },
            new DynamicTemplate { Slot = DynamicTextSlot.ReviewSummary, HideWhenUnavailable = false, Parts = {
                new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = "Applicant: " },
                new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Question, Key = first.LogicalKey } },
                new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = " " },
                new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Question, Key = middle.LogicalKey } },
                new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = " " },
                new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Question, Key = last.LogicalKey } },
                new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = "; DOB: " },
                new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Variable, Key = formattedBirthDate.Id } },
                new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = "; age: " },
                new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Variable, Key = age.Id } }
            } },
            new DynamicTemplate { Slot = DynamicTextSlot.ConfirmationText, Parts = {
                new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = "Thank you, " },
                new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Question, Key = first.LogicalKey } },
                new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = " " },
                new DynamicTextPart { Kind = DynamicPartKind.Value, Source = new DynamicSource { Kind = DynamicSourceKind.Question, Key = last.LogicalKey } }
            } }
        ]);
        var version = new ApplicationVersion
        {
            VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = now, DisplayName = "Dynamic text pilot",
            ConfirmationText = "Thank you for your application.", CompositionJson = composition.ToJson(), Categories = { category }
        };
        var application = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "Dynamic text browser " + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "Dynamic text type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
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

    public async Task<int> SeedDeletionPreviewApplicationAsync()
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options;
        await using var db = new QuestionAppBuilderDbContext(options);
        var source = new Element { Kind = ElementKind.Question, Label = "Deletion source", SortOrder = 1,
            Question = new Question { AnswerType = AnswerType.Number, IsRequired = true } };
        var dependent = new Element { Kind = ElementKind.Question, Label = "Review question", SortOrder = 1,
            Question = new Question { AnswerType = AnswerType.Number, IsRequired = true } };
        var sourceCategory = new Category { Name = "Source section", SortOrder = 1, Elements = { source } };
        var reviewCategory = new Category { Name = "Review section", SortOrder = 2, Elements = { dependent } };
        var composition = new CompositionDefinition
        {
            Calculations = [new CalculatedField { Name = "Source total", CategoryKey = reviewCategory.LogicalKey,
                Sources = [new ValueSource { Kind = SourceKind.Question, Key = source.LogicalKey }] }]
        };
        var version = new ApplicationVersion
        {
            VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = DateTime.UtcNow,
            DisplayName = "Deletion preview fixture", NavigationMode = NavigationMode.Sequential,
            ResultVisibility = ResultVisibility.Total, CompositionJson = composition.ToJson(),
            Categories = { sourceCategory, reviewCategory }
        };
        var application = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "Deletion preview " + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "Deletion preview type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, AcceptNewResponses = true,
            AcceptSubmissions = true, Versions = { version }
        };
        db.Applications.Add(application);
        await db.SaveChangesAsync();
        return application.Id;
    }

    public async Task<int> SeedPerformanceDraftApplicationAsync()
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options;
        await using var db = new QuestionAppBuilderDbContext(options);
        var now = DateTime.UtcNow;
        var firstQuestion = new Question { AnswerType = AnswerType.Number, IsRequired = true };
        var categories = new List<Category>();
        var conditions = new List<VisibilityCondition>();
        var questionsAdded = 0;
        var optionCounter = 0;

        for (var categoryIndex = 0; categoryIndex < 6; categoryIndex++)
        {
            var category = new Category { Name = $"Performance category {categoryIndex + 1}", SortOrder = categoryIndex + 1 };
            var questionCount = categoryIndex < 4 ? 4 : 5;
            for (var questionIndex = 0; questionIndex < questionCount; questionIndex++)
            {
                var isFirst = categoryIndex == 0 && questionIndex == 0;
                var isChoice = questionIndex == 1;
                var answerType = isChoice ? AnswerType.Checkboxes : questionIndex == 3 ? AnswerType.Textbox : AnswerType.Number;
                var question = isFirst ? firstQuestion : new Question { AnswerType = answerType, IsRequired = questionIndex == 0 };
                VisibilityCondition? visibilityCondition = null;
                if (categoryIndex > 0 && questionIndex == 0)
                {
                    visibilityCondition = new VisibilityCondition
                    {
                        Predicates =
                        {
                            new VisibilityPredicate { SourceQuestion = firstQuestion, Operator = ConditionOperator.GreaterThanOrEqual, NumberValue = 0 }
                        }
                    };
                    conditions.Add(visibilityCondition);
                }
                var element = new Element
                {
                    Kind = ElementKind.Question,
                    Label = $"Question {questionsAdded + 1}",
                    SortOrder = questionIndex + 1,
                    Question = question,
                    VisibilityCondition = visibilityCondition
                };

                if (isChoice)
                {
                    var option = new QuestionOption { Label = $"Option {++optionCounter}", Value = $"option-{optionCounter}", SortOrder = 1 };
                    question.Options.Add(option);
                    question.Rules.Add(new ScoringRule
                    {
                        Family = RuleFamily.Selection,
                        MembershipOperator = MembershipOperator.AnyOf,
                        Points = 1,
                        SortOrder = 1,
                        Options = { new ScoringRuleOption { QuestionOption = option } }
                    });
                }
                else if (answerType == AnswerType.Number)
                {
                    question.Rules.Add(new ScoringRule
                    {
                        Family = RuleFamily.NumericRange, Points = 1, MinValue = 0, MaxValue = 100,
                        MinInclusive = true, MaxInclusive = true, SortOrder = 1
                    });
                }

                category.Elements.Add(element);
                questionsAdded++;
            }
            categories.Add(category);
        }

        var version = new ApplicationVersion
        {
            VersionNumber = 1, Status = VersionStatus.Draft, CreatedUtc = now,
            DisplayName = "Authoring performance sample", NavigationMode = NavigationMode.Sequential,
            ResultVisibility = ResultVisibility.Total, Categories = categories, VisibilityConditions = conditions
        };
        var application = new QuestionAppBuilder.Domain.Entities.Application
        {
            Name = "Authoring performance " + Guid.NewGuid().ToString("N"),
            ApplicationType = new ApplicationType { Name = "Performance type " + Guid.NewGuid().ToString("N"), SortOrder = 1 },
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

    public async Task<string> GetDraftDisplayNameAsync(int applicationId)
    {
        var options = new DbContextOptionsBuilder<QuestionAppBuilderDbContext>().UseSqlServer(_databaseConnection).Options;
        await using var db = new QuestionAppBuilderDbContext(options);
        return await db.ApplicationVersions.Where(version => version.ApplicationId == applicationId && version.Status == VersionStatus.Draft)
            .Select(version => version.DisplayName).SingleAsync();
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
