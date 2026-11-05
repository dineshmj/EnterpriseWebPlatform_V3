using System.Text.Json;

using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Support;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Privacy;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.PersonalDataErasure;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Repositories;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Privacy;

namespace EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Privacy;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The erasure through the REAL handler and unit of work: an approval by a second person
// │ deletes the customer's key and clears the blind indexes in the same transaction as the
// │ Outbox event - after which nothing reads, finds or rewrites them, while the customer
// │ number and the ciphertext remain. A refused step changes nothing, and the published
// │ facts name both people and a reason code, never personal data.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class PersonalDataErasure_Approve_Edge_KeyDestroyed
{
    // Subject IDs are GUIDs in the IDP's tokens: the envelope names the requester by one.
    private static readonly string Daniel = Guid.Parse("8f1d7a52-0d0e-4c43-9a8e-0b6d2d0e1a01").ToString();
    private static readonly string Platform = Guid.Parse("8f1d7a52-0d0e-4c43-9a8e-0b6d2d0e1a02").ToString();

    // The laptop's policy: no retention period, a week to decide.
    private static readonly PersonalDataErasurePolicy Policy = new(TimeSpan.Zero, TimeSpan.FromDays(7));

    private CustomerTestDatabase _database = null!;
    private string _avaNumber = null!;
    private long _avaId;

    [SetUp]
    public async Task SetUp()
    {
        _database = new CustomerTestDatabase();

        var ava = OnboardingTestData.Prospect();
        await using var db = _database.Context();
        await CustomerTestDatabase.Repository(db, _database.Cipher).AddAsync(ava, CancellationToken.None);
        await db.SaveChangesAsync();
        _avaId = ava.Id;
        _avaNumber = ava.CustomerNumber.Value;

        _database.Execute($"INSERT INTO staff_members (user_id, lan_id, updated_at) VALUES ('{Daniel}', 'daniel.ops', '2026-11-04'), ('{Platform}', 'michael.pfadmin', '2026-11-04')");
    }

    [TearDown]
    public void TearDown() => _database.Dispose();

    private static PersonalDataErasureCommandHandler Handler(CustomerDbContext db, CustomerTestDatabase database) =>
        new(CustomerTestDatabase.Repository(db, database.Cipher), new PersonalDataErasureRequestRepository(db), db, Policy, TimeProvider.System);

    private async Task<long> RequestedByDanielAsync()
    {
        await using var db = _database.Context();
        var result = await Handler(db, _database).RequestAsync(
            new RequestPersonalDataErasureCommand(_avaNumber, PersonalDataErasureReason.CustomerRequest, Daniel), CancellationToken.None);
        return result.ErasureRequestId;
    }

    private async Task<PersonalDataErasureResult> DecideAsync(long requestId, string decider, bool approve = true, string? remarks = null)
    {
        await using var db = _database.Context();
        return await Handler(db, _database).DecideAsync(
            new DecidePersonalDataErasureCommand(requestId, approve, remarks, decider), CancellationToken.None);
    }

    private JsonElement OutboxPayload(string eventType)
    {
        using var db = _database.Context();
        var message = db.OutboxMessages.AsNoTracking().Single(x => x.EventType == eventType);
        return message.Payload.RootElement.Clone();
    }

    [Test]
    [Category("Happy")]
    public async Task Given_ARequestByOneOperator_When_AnotherApprovesIt_Then_TheKeyIsGoneAndTheCustomerReadsAsErased()
    {
        // Given
        var requestId = await RequestedByDanielAsync();

        // When
        var result = await DecideAsync(requestId, Platform);

        // Then
        result.Status.Should().Be("ERASED");
        _database.Raw("SELECT customer_number FROM personal_data_keys").Should().BeNull();
        _database.Raw("SELECT email_index FROM customers").Should().BeNull();
        _database.Raw("SELECT last_name_index FROM customers").Should().BeNull();
        _database.Raw("SELECT personal_data_erased_at FROM customers").Should().NotBeNull();
        _database.Raw("SELECT email FROM customers").Should().StartWith("v1.");   // the ciphertext stays: unreadable now

        await using var db = _database.Context();
        var shown = (await new CustomerPersonalData(db, _database.Cipher).ReadAsync([_avaId], CancellationToken.None))[_avaId];
        shown.Should().Be(CustomerPersonalDetails.ErasedDetails);
    }

    [Test]
    public async Task Given_AnErasedCustomer_When_SearchedByTheirExactEmailOrSurname_Then_NothingIsFound()
    {
        // Given
        await DecideAsync(await RequestedByDanielAsync(), Platform);
        await using var db = _database.Context();
        var personalData = new CustomerPersonalData(db, _database.Cipher);

        // When
        var byEmail = personalData.WhereEmailOrSurnameIs(db.Customers, "ava.nguyen@example.com").Count();
        var bySurname = personalData.WhereEmailOrSurnameIs(db.Customers, "Nguyen").Count();

        // Then - the blind index alone would still confirm a guessed value
        byEmail.Should().Be(0);
        bySurname.Should().Be(0);
    }

    [Test]
    public async Task Given_AnErasedCustomer_When_LoadedToBeChanged_Then_ItIsRefused()
    {
        // Given
        await DecideAsync(await RequestedByDanielAsync(), Platform);
        await using var db = _database.Context();

        // When
        var load = () => CustomerTestDatabase.Repository(db, _database.Cipher).GetByIdAsync(_avaId, CancellationToken.None);

        // Then
        await load.Should().ThrowAsync<DomainRuleViolationException>().WithMessage("*erased*");
    }

    [Test]
    public async Task Given_ARequestByOneOperator_When_TheSameOperatorApprovesIt_Then_ItIsRefusedAndTheKeyStays()
    {
        // Given
        var requestId = await RequestedByDanielAsync();

        // When
        var approve = () => DecideAsync(requestId, Daniel);

        // Then
        await approve.Should().ThrowAsync<SeparationOfDutiesViolationException>();
        _database.Raw("SELECT customer_number FROM personal_data_keys").Should().Be(_avaNumber);
        _database.Raw("SELECT status FROM personal_data_erasure_requests").Should().Be("PENDING");
    }

    [Test]
    public async Task Given_ARequestWaiting_When_TheSameCustomerIsRequestedAgain_Then_ItIsRefused()
    {
        // Given
        await RequestedByDanielAsync();

        // When
        var again = () => RequestedByDanielAsync();

        // Then - one open request per customer
        await again.Should().ThrowAsync<DomainConflictException>().WithMessage("*already waiting*");
    }

    [Test]
    public async Task Given_AnErasure_When_ItsEventsArePublished_Then_TheyNameBothPeopleAndAReasonCodeOnly()
    {
        // Given
        var requestId = await RequestedByDanielAsync();

        // When
        await DecideAsync(requestId, Platform);

        // Then
        var requested = OutboxPayload("PersonalDataErasureRequested");
        var erased = OutboxPayload("CustomerPersonalDataErased");

        requested.GetProperty("InitiatedByUserId").GetString().Should().Be(Daniel);
        requested.GetProperty("InitiatedByLanId").GetString().Should().Be("daniel.ops");
        requested.GetProperty("Payload").GetProperty("Status").GetString().Should().Be("PENDING");

        erased.GetProperty("InitiatedByUserId").GetString().Should().Be(Daniel);   // the requester stays the initiator
        erased.GetProperty("WorkflowId").GetString().Should().Be(requested.GetProperty("WorkflowId").GetString());
        erased.GetProperty("CausationId").GetString().Should().Be(requested.GetProperty("MessageId").GetString());
        var payload = erased.GetProperty("Payload");
        payload.GetProperty("CustomerNumber").GetString().Should().Be(_avaNumber);
        payload.GetProperty("ReasonCode").GetString().Should().Be("CUSTOMER_REQUEST");
        payload.GetProperty("Status").GetString().Should().Be("ERASED");
        payload.GetProperty("DecisionByUserId").GetString().Should().Be(Platform);
        payload.GetProperty("DecisionByLanId").GetString().Should().Be("michael.pfadmin");

        // The whole phone number (spaced and stored forms): a fragment such as "412" also occurs in random message IDs.
        string.Concat(requested.GetRawText(), erased.GetRawText())
            .Should().NotContainAny("Ava", "Nguyen", "ava.nguyen", "412 345 678", "412345678");
    }

    [Test]
    public async Task Given_ARequestRejectedWithReasons_When_Published_Then_TheRemarksStayInCustomerOnboarding()
    {
        // Given
        var requestId = await RequestedByDanielAsync();

        // When
        var result = await DecideAsync(requestId, Platform, approve: false, remarks: "Ava Nguyen has an open complaint.");

        // Then
        result.Status.Should().Be("REJECTED");
        _database.Raw("SELECT customer_number FROM personal_data_keys").Should().Be(_avaNumber);
        OutboxPayload("PersonalDataErasureRejected").GetRawText().Should().NotContain("complaint");
        _database.Raw("SELECT decision_remarks FROM personal_data_erasure_requests").Should().Be("Ava Nguyen has an open complaint.");
    }

    [Test]
    public async Task Given_RequestsByTwoOperators_When_Listed_Then_EachSeesLanIdsAndMayDecideOnlyTheOthers()
    {
        // Given
        await RequestedByDanielAsync();
        await using var db = _database.Context();
        var queries = new GetPersonalDataErasureRequestsQueryHandler(db, new StaffDirectory(db, TimeProvider.System), TimeProvider.System);

        // When
        var forDaniel = await queries.HandleAsync(Daniel, CancellationToken.None);
        var forPlatform = await queries.HandleAsync(Platform, CancellationToken.None);

        // Then
        forDaniel.Should().ContainSingle().Which.Should().Match<PersonalDataErasureRequestDto>(r =>
            r.RequestedBy == "daniel.ops" && r.Status == "PENDING" && r.ReasonCode == "CUSTOMER_REQUEST" && !r.MayDecide);
        forPlatform.Single().MayDecide.Should().BeTrue();
    }
}