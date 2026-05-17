using MavenOperator.ImportJob.Models;
using MavenOperator.ImportJob.Services;
using Shouldly;

namespace MavenOperator.Tests.Unit.ImportJob.Services;

public sealed class FilterHelperTests
{
    private static ImportFilters NoFilters() => new();

    [Fact]
    public void MatchesGroupFilters_NoFilters_AlwaysTrue()
    {
        FilterHelper.MatchesGroupFilters("com/example/lib/1.0/lib.jar", NoFilters()).ShouldBeTrue();
    }

    [Fact]
    public void MatchesGroupFilters_IncludeGroup_MatchingGroup_ReturnsTrue()
    {
        var filters = new ImportFilters { IncludeGroups = ["com.example"] };
        FilterHelper.MatchesGroupFilters("com/example/lib/1.0/lib.jar", filters).ShouldBeTrue();
    }

    [Fact]
    public void MatchesGroupFilters_IncludeGroup_NonMatchingGroup_ReturnsFalse()
    {
        var filters = new ImportFilters { IncludeGroups = ["com.example"] };
        FilterHelper.MatchesGroupFilters("org/junit/junit/4.13/junit.jar", filters).ShouldBeFalse();
    }

    [Fact]
    public void MatchesGroupFilters_IncludeGlob_WildcardSuffix_MatchesSubgroups()
    {
        var filters = new ImportFilters { IncludeGroups = ["com.example.*"] };
        FilterHelper.MatchesGroupFilters("com/example/sub/lib/1.0/lib.jar", filters).ShouldBeTrue();
    }

    [Fact]
    public void MatchesGroupFilters_ExcludeGroup_ExcludesMatch()
    {
        var filters = new ImportFilters { ExcludeGroups = ["org.junit"] };
        FilterHelper.MatchesGroupFilters("org/junit/junit/4.13/junit.jar", filters).ShouldBeFalse();
    }

    [Fact]
    public void MatchesGroupFilters_ExcludeGroup_NonMatch_AllowsThrough()
    {
        var filters = new ImportFilters { ExcludeGroups = ["org.junit"] };
        FilterHelper.MatchesGroupFilters("com/example/lib/1.0/lib.jar", filters).ShouldBeTrue();
    }

    [Fact]
    public void MatchesGroupFilters_ShallowPath_AlwaysTrue()
    {
        // Files less than 3 segments deep can't have a meaningful group — pass through
        var filters = new ImportFilters { IncludeGroups = ["com.example"] };
        FilterHelper.MatchesGroupFilters("archetype-catalog.xml", filters).ShouldBeTrue();
    }
}

