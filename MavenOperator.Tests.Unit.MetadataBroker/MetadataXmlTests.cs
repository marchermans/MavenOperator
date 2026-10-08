using System.Text;
using System.Xml.Linq;
using MavenOperator.MetadataBroker.Services;
using Shouldly;

namespace MavenOperator.Tests.Unit.MetadataBroker;

public class MetadataXmlTests
{
    private static readonly DateTimeOffset T1 = new(2026, 10, 8, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 10, 8, 10, 45, 0, TimeSpan.Zero);

    [Fact]
    public void RenderListingProducesMavenShapeWithDotGroupAndSortedVersions()
    {
        var (body, lastUpdated) = MetadataXml.RenderListing("com.acme", "lib", new[]
        {
            ("2.1", T2),
            ("1.0", T1),
            ("1.3-SNAPSHOT", T1),
        });

        var doc = XDocument.Load(new MemoryStream(body));
        doc.Root!.Attribute("modelVersion")!.Value.ShouldBe("1.1.0");
        doc.Descendants().First(e => e.Name.LocalName == "groupId").Value.ShouldBe("com.acme");
        doc.Descendants().First(e => e.Name.LocalName == "artifactId").Value.ShouldBe("lib");

        // Sorted ascending: 1.0 < 1.3-SNAPSHOT? No — labelled sorts BEFORE bare at equal tuple,
        // but 1.3 > 1.0 numerically, so order is 1.0, 1.3-SNAPSHOT, 2.1.
        doc.Descendants().Where(e => e.Name.LocalName == "version")
            .Select(e => e.Value)
            .ShouldBe(["1.0", "1.3-SNAPSHOT", "2.1"]);

        doc.Descendants().First(e => e.Name.LocalName == "latest").Value.ShouldBe("2.1");
        doc.Descendants().First(e => e.Name.LocalName == "release").Value.ShouldBe("2.1");

        lastUpdated.UtcDateTime.ShouldBe(T2.UtcDateTime);
    }

    [Fact]
    public void RenderListingOmitsReleaseWhenOnlyLabelledVersionsExist()
    {
        var (body, _) = MetadataXml.RenderListing("com.acme", "lib", new[]
        {
            ("1.0-SNAPSHOT", T1),
        });

        XDocument.Load(new MemoryStream(body))
            .Descendants().Any(e => e.Name.LocalName == "release")
            .ShouldBeFalse();
    }

    [Fact]
    public void RenderListingIsDeterministicForSameInputs()
    {
        var input = new[] { ("1.0", T1), ("2.0", T2) };
        var a = MetadataXml.RenderListing("g", "a", input);
        var b = MetadataXml.RenderListing("g", "a", input);
        a.Body.ShouldBe(b.Body, "same inputs must produce identical bytes (stable ETag)");
    }

    [Fact]
    public void RenderListingChangesBytesWhenMtimeChanges()
    {
        var a = MetadataXml.RenderListing("g", "a", new[] { ("1.0", T1) });
        var b = MetadataXml.RenderListing("g", "a", new[] { ("1.0", T2) });
        a.Body.SequenceEqual(b.Body).ShouldBeFalse(); // lastUpdated must change
    }

    [Fact]
    public void RenderSnapshotPicksHighestBuildNumberPerExtension()
    {
        var files = new[]
        {
            new SnapshotFileEntry("jar", "lib-1.0-20261008.111111-5", T1),
            new SnapshotFileEntry("jar", "lib-1.0-20261008.123456-7", T2),
            new SnapshotFileEntry("pom", "lib-1.0-20261008.123456-7", T2),
        };

        var (body, lastUpdated) = MetadataXml.RenderSnapshot("com.acme", "lib", "1.0-SNAPSHOT", files);
        var doc = XDocument.Load(new MemoryStream(body));

        var snapshot = doc.Root!.Element("snapshot")!;
        snapshot.Element("localCopy")!.Value.ShouldBe("false");

        // Per-extension entries live under <snapshotVersions> as <snapshotVersion> elements.
        var sv = snapshot.Descendants().Where(e => e.Name.LocalName == "snapshotVersion").ToList();
        sv.Count.ShouldBe(2);

        var jar = sv.First(e => e.Element("extension")!.Value == "jar");
        jar.Element("value")!.Value.ShouldBe("lib-1.0-20261008.123456-7"); // build 7 wins over 5
        jar.Element("updated")!.Value.ShouldBe(T2.UtcDateTime.ToString("yyyyMMddHHmmss"));

        var pom = sv.First(e => e.Element("extension")!.Value == "pom");
        pom.Element("value")!.Value.ShouldBe("lib-1.0-20261008.123456-7");

        lastUpdated.UtcDateTime.ShouldBe(T2.UtcDateTime); // max file mtime
    }

    [Fact]
    public void RenderSnapshotListsExtensionsSorted()
    {
        var files = new[]
        {
            new SnapshotFileEntry("war", "w-1.0-20261008.000001-1", T1),
            new SnapshotFileEntry("jar", "j-1.0-20261008.000001-1", T1),
        };

        var (body, _) = MetadataXml.RenderSnapshot("g", "a", "1.0-SNAPSHOT", files);
        XDocument.Load(new MemoryStream(body))
            .Root!.Element("snapshot")!
            .Descendants()
            .Where(e => e.Name.LocalName == "snapshotVersion")
            .Select(e => e.Element("extension")!.Value)
            .ShouldBe(["jar", "war"]);
    }
}
