using FluentAssertions;

namespace QuestionAppBuilder.Application.Tests;

public class SolutionTests
{
    [Fact]
    public void Solution_builds_and_tests_run()
    {
        (1 + 1).Should().Be(2);
    }
}
