using MavenOperator.VirtualProxy.Services;
using Shouldly;

namespace MavenOperator.Tests.Unit.VirtualProxy.Services;

/// <summary>
/// Unit tests for <see cref="VirtualProxyMetrics"/> — specifically the asset-type classifier
/// and verifying that the metrics object can be instantiated and called without exceptions.
/// Prometheus counters are global singletons so we only validate classification logic here.
/// </summary>
public sealed class VirtualProxyMetricsTests
{
    [Theory]
    [InlineData("com/example/foo/1.0/foo-1.0.jar",            "jar")]
    [InlineData("com/example/foo/1.0/foo-1.0.war",            "jar")]
    [InlineData("com/example/foo/1.0/foo-1.0.aar",            "jar")]
    [InlineData("com/example/foo/1.0/foo-1.0.pom",            "pom")]
    [InlineData("com/example/foo/maven-metadata.xml",          "metadata")]
    [InlineData("com/example/foo/maven-metadata.xml.sha1",     "metadata")]
    [InlineData("com/example/foo/maven-metadata.xml.md5",      "metadata")]
    [InlineData("com/example/foo/1.0/foo-1.0.jar.sha1",        "checksum")]
    [InlineData("com/example/foo/1.0/foo-1.0.jar.md5",         "checksum")]
    [InlineData("com/example/foo/1.0/foo-1.0.jar.sha256",      "checksum")]
    [InlineData("com/example/foo/1.0/foo-1.0-sources.jar",     "jar")]
    [InlineData("com/example/foo/1.0/something.txt",           "other")]
    [InlineData("",                                             "other")]
    public void ClassifyAssetType_ReturnsExpectedBucket(string path, string expected)
    {
        VirtualProxyMetrics.ClassifyAssetType(path).ShouldBe(expected);
    }

    [Fact]
    public void RecordRequest_DoesNotThrow()
    {
        var metrics = new VirtualProxyMetrics();
        Should.NotThrow(() =>
            metrics.RecordRequest("my-virtual", "com/example/foo/1.0/foo-1.0.jar", "jar", 200));
    }

    [Fact]
    public void RecordMemberRequest_DoesNotThrow()
    {
        var metrics = new VirtualProxyMetrics();
        Should.NotThrow(() =>
            metrics.RecordMemberRequest("my-virtual", "hosted-releases", true, 0.042));
    }

    [Fact]
    public void RecordMetadataMerge_DoesNotThrow()
    {
        var metrics = new VirtualProxyMetrics();
        Should.NotThrow(() =>
            metrics.RecordMetadataMerge("my-virtual", 3, 0.015));
    }
}

