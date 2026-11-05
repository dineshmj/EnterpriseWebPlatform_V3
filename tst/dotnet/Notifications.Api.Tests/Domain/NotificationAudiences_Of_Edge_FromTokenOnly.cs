using System.Security.Claims;

using AwesomeAssertions;

using EnterpriseWebPlatform.Notifications.Api.Domain;

namespace EnterpriseWebPlatform.Notifications.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ OWASP API1 (BOLA): what a person may read and which hub groups they join come from
// │ their token only - the person (sub), their work-queue roles in THEIR branch, the
// │ operations desk in every branch. No sub means nothing; no branch claim means no
// │ branch queues; a role that is not a work queue adds nothing.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class NotificationAudiences_Of_Edge_FromTokenOnly
{
    private static ClaimsPrincipal Person(string? sub, string? branch, params string[] roles)
    {
        var claims = new List<Claim>();
        if (sub is not null)
            claims.Add(new Claim("sub", sub));
        if (branch is not null)
            claims.Add(new Claim("branch", branch));
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    [Test]
    [Category("Happy")]
    public void Given_AKycOfficerOfABranch_When_AudiencesDerived_Then_ThemselvesAndTheirBranchsKycQueue()
    {
        // When
        var audiences = NotificationAudiences.Of(Person("8F1D7A52-0D0E-4C43-9A8E-0B6D2D0E1A03", "syd001", "kyc_officer"));

        // Then
        audiences.Should().BeEquivalentTo("user:8f1d7a52-0d0e-4c43-9a8e-0b6d2d0e1a03", "staff:kyc_officer:SYD001");
    }

    [Test]
    public void Given_NoSubject_When_AudiencesDerived_Then_None()
    {
        // When - roles and branch alone never open a queue
        var audiences = NotificationAudiences.Of(Person(null, "SYD001", "kyc_officer", "operations_administrator"));

        // Then
        audiences.Should().BeEmpty();
    }

    [Test]
    public void Given_AnOfficerWithoutABranchClaim_When_AudiencesDerived_Then_OnlyThemselves()
    {
        // When
        var audiences = NotificationAudiences.Of(Person("u-1", null, "kyc_officer", "payments_officer"));

        // Then
        audiences.Should().BeEquivalentTo("user:u-1");
    }

    [Test]
    public void Given_AnOperationsAdministrator_When_AudiencesDerived_Then_TheDeskInEveryBranchWithOrWithoutABranch()
    {
        // When
        var withoutBranch = NotificationAudiences.Of(Person("u-2", null, "operations_administrator"));
        var withBranch = NotificationAudiences.Of(Person("u-2", "MEL001", "operations_administrator"));

        // Then - the desk is not branch-bound; no MEL001 queue for a role that has none
        withoutBranch.Should().BeEquivalentTo("user:u-2", "staff:operations_administrator:*");
        withBranch.Should().BeEquivalentTo("user:u-2", "staff:operations_administrator:*");
    }

    [Test]
    public void Given_RolesThatAreNoWorkQueue_When_AudiencesDerived_Then_TheyAddNothing()
    {
        // When - a customer service role or an unknown role must not become a queue name
        var audiences = NotificationAudiences.Of(Person("u-3", "SYD001", "customer_service_officer", "*", "kyc_officer:MEL001"));

        // Then
        audiences.Should().BeEquivalentTo("user:u-3");
    }

    [Test]
    public void Given_ARoleRepeatedInTheToken_When_AudiencesDerived_Then_EachQueueOnce()
    {
        // When
        var audiences = NotificationAudiences.Of(Person("u-4", "SYD001", "payments_officer", "payments_officer", "operations_administrator", "operations_administrator"));

        // Then
        audiences.Should().OnlyHaveUniqueItems();
        audiences.Should().BeEquivalentTo("user:u-4", "staff:payments_officer:SYD001", "staff:operations_administrator:*");
    }
}