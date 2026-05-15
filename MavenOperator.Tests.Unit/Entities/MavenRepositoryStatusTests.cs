using Shouldly;
using MavenOperator.Entities.Status;
namespace MavenOperator.Tests.Unit.Entities;
public sealed class MavenRepositoryStatusTests
{
    [Fact]
    public void SetCondition_AddsNewCondition_WhenTypeNotPresent()
    {
        var status = new MavenRepositoryStatus();
        status.SetCondition("Available", isTrue: true, reason: "Ready", message: "All good");
        status.Conditions.Count.ShouldBe(1);
        var c = status.Conditions[0];
        c.Type.ShouldBe("Available");
        c.Status.ShouldBe("True");
        c.Reason.ShouldBe("Ready");
        c.Message.ShouldBe("All good");
    }
    [Fact]
    public void SetCondition_UpdatesExistingCondition_WhenTypeAlreadyPresent()
    {
        var status = new MavenRepositoryStatus();
        status.SetCondition("Available", isTrue: true, reason: "OK", message: "fine");
        status.SetCondition("Available", isTrue: false, reason: "Broken", message: "something broke");
        status.Conditions.Count.ShouldBe(1);
        status.Conditions[0].Status.ShouldBe("False");
        status.Conditions[0].Reason.ShouldBe("Broken");
    }
    [Fact]
    public void SetCondition_DoesNotDuplicateConditions_WhenCalledTwiceWithSameValues()
    {
        var status = new MavenRepositoryStatus();
        status.SetCondition("Available", isTrue: true, reason: "OK", message: "fine");
        status.SetCondition("Available", isTrue: true, reason: "OK", message: "fine");
        status.Conditions.Count.ShouldBe(1);
    }
    [Fact]
    public void SetCondition_SupportsMultipleDistinctTypes()
    {
        var status = new MavenRepositoryStatus();
        status.SetCondition("Available",   isTrue: true, reason: "DeploymentReady",   message: "ok");
        status.SetCondition("StorageBound", isTrue: true, reason: "PVCBound",          message: "pvc bound");
        status.SetCondition("AuthReady",   isTrue: true, reason: "HtpasswdGenerated", message: "2 users");
        status.Conditions.Count.ShouldBe(3);
        status.Conditions.Select(c => c.Type).ShouldBe(
            ["Available", "StorageBound", "AuthReady"], ignoreOrder: true);
    }
    [Fact]
    public void Phase_DefaultsToPending()
    {
        var status = new MavenRepositoryStatus();
        status.Phase.ShouldBe(RepositoryPhase.Pending);
    }
}
