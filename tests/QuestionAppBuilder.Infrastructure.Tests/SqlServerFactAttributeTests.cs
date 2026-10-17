using FluentAssertions;
using QuestionAppBuilder.Infrastructure.Tests.SqlServer;

namespace QuestionAppBuilder.Infrastructure.Tests;

public class SqlServerFactAttributeTests
{
    [Fact]
    public void Missing_configuration_reports_skip_reason()
    {
        Environment.SetEnvironmentVariable("QAB_TEST_SQLSERVER", null);
        new SqlServerFactAttribute().Skip.Should().Contain("QAB_TEST_SQLSERVER");
    }
}
