using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;

using AwesomeAssertions;

using EnterpriseWebPlatform.Compliance.Api.Authorization;

namespace EnterpriseWebPlatform.Compliance.Api.Tests.Authorization;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ A compliance action needs ALL of: the compliance_officer role, every permission the
// │ endpoint names, the COMPLIANCE department and clearance level 3 or more. The case's
// │ risk may then ask for more (ClearanceOf feeds ComplianceCase.Approve).
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class ComplianceOfficerAuthorizationHandler_Edge_Requirements
{
    private const string Decide = "compliance.case.decide";

    private static ClaimsPrincipal Officer(
        string role = "compliance_officer", string? department = "Compliance", string? permission = Decide, params string[] clearances)
    {
        var claims = new List<Claim> { new("role", role) };
        if (permission is not null)
            claims.Add(new Claim("permission", permission));
        if (department is not null)
            claims.Add(new Claim("department", department));
        claims.AddRange((clearances.Length == 0 ? ["3"] : clearances).Select(c => new Claim("clearance_level", c)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "name", "role"));
    }

    private static async Task<bool> IsAllowed(ClaimsPrincipal user)
    {
        var requirement = new ComplianceOfficerRequirement(Decide);
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);
        await new ComplianceOfficerAuthorizationHandler().HandleAsync(context);
        return context.HasSucceeded;
    }

    [Test]
    [Category("Happy")]
    public async Task Given_AComplianceOfficerMeetingEveryRule_When_TheyAct_Then_ItIsAllowed()
    {
        // Given / When / Then
        (await IsAllowed(Officer())).Should().BeTrue();
    }

    [Test]
    public async Task Given_AnotherRole_When_TheyAct_Then_ItIsRefused()
    {
        // Given / When / Then
        (await IsAllowed(Officer(role: "kyc_officer"))).Should().BeFalse();
    }

    [Test]
    public async Task Given_NoPermission_When_TheyAct_Then_ItIsRefused()
    {
        // Given / When / Then
        (await IsAllowed(Officer(permission: null))).Should().BeFalse();
        (await IsAllowed(Officer(permission: "COMPLIANCE.CASE.DECIDE"))).Should().BeFalse();
    }

    [TestCase(null)]
    [TestCase("KYC")]
    public async Task Given_NotTheComplianceDepartment_When_TheyAct_Then_ItIsRefused(string? department)
    {
        // Given / When / Then
        (await IsAllowed(Officer(department: department))).Should().BeFalse();
    }

    [Test]
    [Category("Boundary")]
    public async Task Given_ClearanceTwo_When_TheyAct_Then_ItIsRefused()
    {
        // Given / When / Then
        (await IsAllowed(Officer(clearances: "2"))).Should().BeFalse();
    }

    [TestCase(new[] { "4" }, 4)]
    [TestCase(new[] { "3", "5" }, 5)]
    [TestCase(new[] { "five" }, 0)]
    [Category("Boundary")]
    public void Given_ClearanceClaims_When_TheClearanceIsRead_Then_TheHighestReadableOneCounts(string[] clearances, int expected)
    {
        // Given / When / Then
        ComplianceOfficerAuthorizationHandler.ClearanceOf(Officer(clearances: clearances)).Should().Be(expected);
    }
}