using FluentAssertions;

namespace QuestionAppBuilder.Browser.Tests;

public sealed class SampleHostFixturePathTests
{
    [Fact]
    public void Resolves_the_host_from_the_same_isolated_artifact_tree_as_the_test_assembly()
    {
        WithTemporaryRoot(root =>
        {
            var repository = Path.Combine(root, "repo");
            var artifacts = Path.Combine(root, "release-artifacts");
            var testOutput = Path.Combine(artifacts, "bin", "QuestionAppBuilder.Browser.Tests", "debug");
            var host = Path.Combine(artifacts, "bin", "QuestionAppBuilder.SampleHost", "debug", "QuestionAppBuilder.SampleHost.dll");
            Directory.CreateDirectory(testOutput);
            Directory.CreateDirectory(Path.GetDirectoryName(host)!);
            File.WriteAllText(host, "isolated host");

            SampleHostFixture.ResolveHostAssembly(testOutput, repository).Should().Be(host);
        });
    }

    [Fact]
    public void Refuses_to_fall_back_to_a_stale_project_host_when_isolated_artifact_is_missing()
    {
        WithTemporaryRoot(root =>
        {
            var repository = Path.Combine(root, "repo");
            var testOutput = Path.Combine(root, "artifacts", "bin", "QuestionAppBuilder.Browser.Tests", "debug");
            var staleHost = Path.Combine(repository, "samples", "QuestionAppBuilder.SampleHost", "bin", "debug", "net8.0", "QuestionAppBuilder.SampleHost.dll");
            Directory.CreateDirectory(testOutput);
            Directory.CreateDirectory(Path.GetDirectoryName(staleHost)!);
            File.WriteAllText(staleHost, "stale host");

            var exception = Assert.Throws<FileNotFoundException>(() => SampleHostFixture.ResolveHostAssembly(testOutput, repository));
            exception.Message.Should().Contain("Refusing to fall back outside the current test build");
        });
    }

    [Fact]
    public void Uses_project_host_only_for_the_repositorys_normal_test_output()
    {
        WithTemporaryRoot(root =>
        {
            var repository = Path.Combine(root, "repo");
            var testOutput = Path.Combine(repository, "tests", "QuestionAppBuilder.Browser.Tests", "bin", "Debug", "net8.0");
            var host = Path.Combine(repository, "samples", "QuestionAppBuilder.SampleHost", "bin", "debug", "net8.0", "QuestionAppBuilder.SampleHost.dll");
            Directory.CreateDirectory(testOutput);
            Directory.CreateDirectory(Path.GetDirectoryName(host)!);
            File.WriteAllText(host, "project host");

            SampleHostFixture.ResolveHostAssembly(testOutput, repository).Should().Be(host);
        });
    }

    private static void WithTemporaryRoot(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "qab-fixture-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
