using System.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using QuestionAppBuilder.SampleHost.DevAuth;
using QuestionAppBuilder.SampleHost.Integration;

namespace QuestionAppBuilder.Runtime.Tests;

public sealed class HostIntegrationTests
{
    [Fact]
    public void Production_host_rejects_dev_credentials_and_missing_identity_config()
    {
        var services = new ServiceCollection();
        var environment = new TestHostEnvironment { EnvironmentName = "Production" };
        var configuration = new ConfigurationBuilder().Build();
        var act = () => HostIdentityRegistration.AddHostIdentity(services, configuration, environment);

        act.Should().Throw<InvalidOperationException>().WithMessage("*OIDC Authority and Issuer*");
        services.BuildServiceProvider().GetService<DevUserStore>().Should().BeNull();
    }

    [Fact]
    public void Production_registration_does_not_register_dev_users()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), "qab-host-keys-" + Guid.NewGuid().ToString("N"));
        var values = ProductionConfiguration(keyPath);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        var environment = new TestHostEnvironment { EnvironmentName = "Production" };

        HostIdentityRegistration.AddHostIdentity(services, configuration, environment);

        using var provider = services.BuildServiceProvider();
        provider.GetService<DevUserStore>().Should().BeNull();
        provider.GetService<QuestionAppBuilder.Application.Access.IResponseEmailSender>().Should().BeOfType<SmtpResponseEmailSender>();
        if (Directory.Exists(keyPath)) Directory.Delete(keyPath, recursive: true);
    }

    [LocalDbFact]
    public async Task Production_startup_does_not_apply_migrations_implicitly()
    {
        var admin = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("QAB_TEST_SQLSERVER") ??
            "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true") { InitialCatalog = "master" };
        var databaseName = "QAB_NoMigration_" + Guid.NewGuid().ToString("N");
        var database = new SqlConnectionStringBuilder(admin.ConnectionString) { InitialCatalog = databaseName };
        var keyPath = Path.Combine(Path.GetTempPath(), "qab-production-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keyPath);
        var configuration = ProductionConfiguration(keyPath);
        configuration["QuestionAppBuilder:ConnectionString"] = database.ConnectionString;

        await using (var connection = new SqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DB_ID(@name)";
            command.Parameters.AddWithValue("@name", databaseName);
            (await command.ExecuteScalarAsync()).Should().Be(DBNull.Value);
        }

        await using (var factory = new ProductionHostFactory(configuration))
        {
            var client = factory.CreateClient();
            (await client.GetAsync("/Account/Login")).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
            await using var connection = new SqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DB_ID(@name)";
            command.Parameters.AddWithValue("@name", databaseName);
            (await command.ExecuteScalarAsync()).Should().Be(DBNull.Value);
        }

        if (Directory.Exists(keyPath)) Directory.Delete(keyPath, recursive: true);
    }

    private static Dictionary<string, string?> ProductionConfiguration(string keyPath) => new()
    {
        ["QuestionAppBuilder:Identity:Authority"] = "https://issuer.example.test",
        ["QuestionAppBuilder:Identity:Issuer"] = "https://issuer.example.test",
        ["QuestionAppBuilder:Identity:ClientId"] = "client-id",
        ["QuestionAppBuilder:Identity:ClientSecret"] = "test-only-secret",
        ["QuestionAppBuilder:DataProtection:KeyRingPath"] = keyPath,
        ["QuestionAppBuilder:Smtp:Host"] = "smtp.example.test",
        ["QuestionAppBuilder:Smtp:Port"] = "587",
        ["QuestionAppBuilder:Smtp:FromAddress"] = "questionnaires@example.test"
    };

    private sealed class ProductionHostFactory : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, string?> _configuration;
        public ProductionHostFactory(Dictionary<string, string?> configuration) => _configuration = configuration;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            foreach (var (key, value) in _configuration) builder.UseSetting(key, value);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_configuration));
        }
    }

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "QuestionAppBuilder.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class LocalDbFactAttribute : FactAttribute
    {
        public LocalDbFactAttribute()
        {
            if (!OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QAB_TEST_SQLSERVER")))
                Skip = "Set QAB_TEST_SQLSERVER to run this disposable production-host migration check.";
        }
    }
}
