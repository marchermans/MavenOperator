using MavenOperator.MetadataBroker.Services;
using Shouldly;

namespace MavenOperator.Tests.Unit.MetadataBroker;

public class VersionOrderTests
{
    [Fact]
    public void SortsByNumericTupleAscending()
        => VersionOrder.Compare("1.0", "2.0").ShouldBeLessThan(0);

    [Fact]
    public void TwoPartAndThreePartWithZeroPatchAreEqualTuples()
        => VersionOrder.Compare("2.0", "2.0.0").ShouldBe(0);

    [Fact]
    public void MinorComesBeforeMajor_TieBreaksToHigherComponent()
    {
        VersionOrder.Compare("1.9", "2.0").ShouldBeLessThan(0);
        VersionOrder.Compare("1.0.5", "1.0.6").ShouldBeLessThan(0);
    }

    [Fact]
    public void LabelledVersionSortsBeforeBareReleaseWithSameTuple()
        => VersionOrder.Compare("1.3-SNAPSHOT", "1.3").ShouldBeLessThan(0);

    [Fact]
    public void LabelsTieBreakLexicographically()
        => VersionOrder.Compare("4.1-RC1", "4.1-alpha").ShouldBeLessThan(0); // 'R' < 'a'

    [Fact]
    public void DateVersionsSortNumericallyNotLexically()
    {
        // Lexical order would put 20251231 after 20260101? No — but 9 vs 10-digit shapes
        // and leading zeros break lexical compare; numeric compare handles them.
        VersionOrder.Compare("20251231", "20260101").ShouldBeLessThan(0);
    }

    [Fact]
    public void SortAscendingReturnsNewSortedList()
    {
        var sorted = VersionOrder.SortAscending(new[] { "2.1", "1.0-SNAPSHOT", "1.0", "1.3" });
        sorted.ShouldBe(["1.0-SNAPSHOT", "1.0", "1.3", "2.1"]);
    }

    [Fact]
    public void UnparseableNumericPartsFallBackToZero()
        // Maven allows oddities like "beta-1"; non-numeric base components parse as 0.
        => VersionOrder.Compare("beta-1", "0.0").ShouldBeLessThan(0); // labelled < bare at (0,0,0)

    [Fact]
    public void ParseExposesLabelAndComponents()
    {
        var key = VersionOrder.Parse("2.5-SNAPSHOT");
        key.Major.ShouldBe(2);
        key.Minor.ShouldBe(5);
        key.Patch.ShouldBe(0);
        key.HasLabel.ShouldBeTrue();
        key.Label.ShouldBe("SNAPSHOT");

        VersionOrder.Parse("1.2").HasLabel.ShouldBeFalse();
    }
}
