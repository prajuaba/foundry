using Foundry.Rules;
using Xunit;

namespace Foundry.Rules.Tests;

/// <summary>
/// Role gating in <see cref="WorkflowEngine.ValidatePermission"/>.
/// </summary>
/// <remarks>
/// This is an authorisation boundary: it decides whether a caller may move an entity through a
/// workflow. A false negative is an annoyance; a false positive lets an unauthorised actor approve
/// something. It is tested from both directions for that reason.
/// </remarks>
public class WorkflowPermissionTests
{
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static WorkflowEngine Engine() => new(new EmptyServiceProvider());

    private static void Validate(
        List<string> transitionRoles,
        List<string> stateRoles,
        IEnumerable<string> userRoles)
        => Engine().ValidatePermission("submit", "Draft", transitionRoles, stateRoles, userRoles);

    [Fact]
    public void NoRolesConfigured_AllowsAnyone()
    {
        // An unconfigured workflow is open by design; asserted so it cannot change unnoticed.
        Validate([], [], []);
    }

    [Fact]
    public void MatchingTransitionRole_IsAllowed()
    {
        Validate(["Approver"], [], ["Approver"]);
    }

    [Fact]
    public void MatchingRole_IsCaseInsensitive()
    {
        Validate(["Approver"], [], ["approver"]);
    }

    [Fact]
    public void OneMatchingRoleOutOfSeveral_IsAllowed()
    {
        Validate(["Approver", "Admin"], [], ["Reader", "Admin"]);
    }

    [Fact]
    public void MissingTransitionRole_IsDenied()
    {
        var error = Assert.Throws<WorkflowException>(() => Validate(["Approver"], [], ["Reader"]));
        Assert.Contains("Approver", error.Message);
    }

    [Fact]
    public void MissingStateRole_IsDenied()
    {
        var error = Assert.Throws<WorkflowException>(() => Validate([], ["Owner"], ["Reader"]));
        Assert.Contains("Owner", error.Message);
    }

    [Fact]
    public void NoRolesAtAll_IsDeniedWhenRolesAreRequired()
    {
        // The common real case: an unauthenticated caller reaching a gated transition.
        Assert.Throws<WorkflowException>(() => Validate(["Approver"], [], []));
    }

    [Fact]
    public void StateGate_IsCheckedEvenWhenTheTransitionGatePasses()
    {
        // Both gates must hold. If the state gate were skipped once the transition gate passed,
        // a caller with the transition role could act on a state they have no access to.
        Assert.Throws<WorkflowException>(() => Validate(["Approver"], ["Owner"], ["Approver"]));
    }

    [Fact]
    public void BothGatesSatisfied_IsAllowed()
    {
        Validate(["Approver"], ["Owner"], ["Approver", "Owner"]);
    }
}
