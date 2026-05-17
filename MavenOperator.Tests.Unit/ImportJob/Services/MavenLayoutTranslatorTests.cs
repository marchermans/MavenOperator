using MavenOperator.ImportJob.Services;
using Shouldly;

namespace MavenOperator.Tests.Unit.ImportJob.Services;

public sealed class MavenLayoutTranslatorTests
{
    [Theory]
    [InlineData("releases/com/example/my-lib/1.0/my-lib-1.0.jar",   "releases", "com/example/my-lib/1.0/my-lib-1.0.jar")]
    [InlineData("releases/org/junit/junit/4.13/junit-4.13.jar",      "releases", "org/junit/junit/4.13/junit-4.13.jar")]
    [InlineData("snapshots/com/acme/app/1.0-SNAPSHOT/app-1.0.jar",   "snapshots", "com/acme/app/1.0-SNAPSHOT/app-1.0.jar")]
    public void Translate_StripsRepositoryPrefix(string path, string repo, string expected)
    {
        var result = MavenLayoutTranslator.Translate(path, repo);
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("releases/.index/catalog.idx",    "releases")]
    [InlineData("releases/.cache/data",           "releases")]
    [InlineData("releases/.reposilite/meta.json", "releases")]
    [InlineData("releases/.trash/old.jar",        "releases")]
    public void Translate_ReturnsNull_ForInternalDirectories(string path, string repo)
    {
        var result = MavenLayoutTranslator.Translate(path, repo);
        result.ShouldBeNull();
    }

    [Fact]
    public void Translate_NormalisesBackslashes()
    {
        var path   = @"releases\com\example\lib\1.0\lib-1.0.jar";
        var result = MavenLayoutTranslator.Translate(path, "releases");
        result.ShouldBe("com/example/lib/1.0/lib-1.0.jar");
    }

    [Fact]
    public void Translate_TrimsLeadingSlash()
    {
        var result = MavenLayoutTranslator.Translate("/releases/com/example/lib/1.0/lib.jar", "releases");
        result.ShouldBe("com/example/lib/1.0/lib.jar");
    }

    [Fact]
    public void NormalizeMavenPath_StripsInternalDirs()
    {
        var result = MavenLayoutTranslator.NormalizeMavenPath("com/example/.cache/data");
        result.ShouldBeNull();
    }

    [Fact]
    public void NormalizeMavenPath_PassesThroughCleanPath()
    {
        var result = MavenLayoutTranslator.NormalizeMavenPath("com/example/lib/1.0/lib.jar");
        result.ShouldBe("com/example/lib/1.0/lib.jar");
    }

    [Fact]
    public void NormalizeMavenPath_NormalisesBackslashes()
    {
        var result = MavenLayoutTranslator.NormalizeMavenPath(@"com\example\lib\1.0\lib.jar");
        result.ShouldBe("com/example/lib/1.0/lib.jar");
    }
}

