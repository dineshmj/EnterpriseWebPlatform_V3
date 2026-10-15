using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;

using AwesomeAssertions;

using EnterpriseWebPlatform.Accounts.Api.Authorization;

namespace EnterpriseWebPlatform.Accounts.Api.Tests.Authorization;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ An account officer action needs ALL of: the account_officer role, every permission the
// │ endpoint names, the ACCOUNTS department and clearance level 3 or more. Missing any one
// │ of them refuses the request.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class AccountOfficerAuthorizationHandler_Edge_Requirements
{
    private static ClaimsPrincipal Officer(
        string role = "account_officer", string? department = "Accounts", string? clearance = "3", params string[] permissions)
    {
        var claims = new List<Claim> { new("role", role) };
        claims.AddRange((permissions.Length == 0 ? ["account.application.decide"] : permissions).Select(p => new Claim("permission", p)));
        if (department is not null)
            claims.Add(new Claim("department", department));
        if (clearance is not null)
            claims.Add(new Claim("clearance_level", clearance));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "name", "role"));
    }

    private static async Task<bool> IsAllowed(ClaimsPrincipal user)
    {
        var requirement = new AccountOfficerRequirement("account.application.decide");
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);
        await new AccountOfficerAuthorizationHandler().HandleAsync(context);
        return context.HasSucceeded;
    }

    [Test]
    [Category("Happy")]
    public async Task Given_AnAccountOfficerMeetingEveryRule_When_TheyAct_Then_ItIsAllowed()
    {
        // Given / When / Then
        (await IsAllowed(Officer())).Should().BeTrue();
    }

    [Test]
    public async Task Given_AnotherRole_When_TheyAct_Then_ItIsRefused()
    {
        // Given / When / Then
        (await IsAllowed(Officer(role: "customer_service_agent"))).Should().BeFalse();
    }

    [Test]
    public async Task Given_NoDepartment_When_TheyAct_Then_ItIsRefused()
    {
        // Given / When / Then
        (await IsAllowed(Officer(department: null))).Should().BeFalse();
    }

    [TestCase("2", false)]
    [TestCase("3", true)]
    [TestCase("not-a-number", false)]
    [TestCase(null, false)]
    [Category("Boundary")]
    public async Task Given_AClearanceLevel_When_TheyAct_Then_OnlyLevelThreeOrMoreIsAllowed(string? clearance, bool allowed)
    {
        // Given / When / Then
        (await IsAllowed(Officer(clearance: clearance))).Should().Be(allowed);
    }

    [Test]
    public async Task Given_AnotherPermissionOnly_When_TheyAct_Then_ItIsRefused()
    {
        // Given / When / Then
        (await IsAllowed(Officer(permissions: "account.application.view"))).Should().BeFalse();
    }

    [Test]
    public void Given_SeveralClearanceClaims_When_TheLevelIsRead_Then_TheHighestCounts()
    {
        // Given
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("clearance_level", "2"), new Claim("clearance_level", "4")], "test"));

        // When / Then
        AccountOfficerAuthorizationHandler.ClearanceOf(user).Should().Be(4);
    }
}