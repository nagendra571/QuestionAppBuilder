using FluentAssertions;
using QuestionAppBuilder.Infrastructure.Tests.SqlServer;

namespace QuestionAppBuilder.Infrastructure.Tests;

public class SqlServerFactAttributeTests
{
    [Fact]
    public void Missing_configuration_reports_skip_reason()
    {
        var configured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QAB_TEST_SQLSERVER"));
        if (configured)
            new SqlServerFactAttribute().Skip.Should().BeNull();
        else
            new SqlServerFactAttribute().Skip.Should().Contain("QAB_TEST_SQLSERVER");
    }
}
