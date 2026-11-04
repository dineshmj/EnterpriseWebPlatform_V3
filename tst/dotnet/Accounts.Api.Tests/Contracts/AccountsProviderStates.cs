using System.Text.Json;

using Moq;

using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.Events;
using EnterpriseWebPlatform.Accounts.Api.Infrastructure.Messaging;
using EnterpriseWebPlatform.Accounts.Api.Tests.Support;

namespace EnterpriseWebPlatform.Accounts.Api.Tests.Contracts;

// ┌─ CONTRACT ───────────────────────────────────────────────────────────────────────────
// │ The state names are the consumers' words (their Given(...)); an unknown one fails the
// │ verification instead of running against an empty API. Accounts, holds and replies are
// │ made through the aggregates' own methods and the REAL Outbox mapper.
// └──────────────────────────────────────────────────────────────────────────────────────
/// <summary>The Accounts API's ports as mocks, set per provider state; and its real funds replies per scenario.</summary>
internal sealed class AccountsProviderStates
{
    public Mock<IAccountRepository> Accounts { get; } = new();

    public Mock<IFundsHoldRepository> Holds { get; } = new();

    public Mock<IInboxStore> Inbox { get; } = new();

    public Mock<IAccountsUnitOfWork> UnitOfWork { get; } = new() { DefaultValue = DefaultValue.Mock };

    public Mock<IStaffDirectory> Staff { get; } = new();

    public void Set(string? state)
    {
        foreach (var mock in new Mock[] { Accounts, Holds, Inbox, UnitOfWork, Staff })
            mock.Reset();

        switch (state)
        {
            case null or "":
                break;   // a Kafka message: no state, the reply is built per scenario (Replies)

            case "an open account with enough money in the initiator's branch":
                // The Sydney account of CUST-100001 with 5,000 AUD - whatever BSB and number the command names.
                Accounts.Setup(a => a.GetForUpdateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => AccountsTestData.OpenAccount());
                break;

            case "funds are held for the payment":
                var account = AccountsTestData.OpenAccount();
                Holds.Setup(h => h.GetForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => AccountsTestData.Reserve(account, 140.25m));
                Accounts.Setup(a => a.GetForUpdateAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(account);
                break;

            case "no funds are held for the payment":
                break;   // every lookup finds nothing

            default:
                throw new InvalidOperationException($"Unknown provider state '{state}': add it to {nameof(AccountsProviderStates)}.");
        }
    }

    /// <summary>The reply the Outbox relay would publish for this scenario (the consumers' descriptions).</summary>
    public static IReadOnlyDictionary<string, Func<object>> Replies { get; } = new Dictionary<string, Func<object>>
    {
        ["a FundsReserved reply"] = () => Published(Held(), typeof(FundsReservedDomainEvent)),
        ["a FundsReservationFailed reply"] = () =>
            Published(AccountsTestData.Reserve(AccountsTestData.OpenAccount(balance: 100m), 140.25m), typeof(FundsReservationRefusedDomainEvent)),
        ["a FundsSettled reply"] = () => Published(Settled(), typeof(FundsSettledDomainEvent)),
        ["a FundsReleased reply"] = () => Published(
            FundsHold.ReleaseBeforeReservation(Guid.NewGuid(), "PAY-261015-TEST01", "062-000", "72947086", AccountsTestData.Customer, "AUD",
                AccountsTestData.Sophie, AccountsTestData.Now),
            typeof(FundsReleasedDomainEvent))
    };

    private static FundsHold Held() => AccountsTestData.Reserve(AccountsTestData.OpenAccount(), 140.25m);

    private static FundsHold Settled()
    {
        var account = AccountsTestData.OpenAccount();
        var hold = AccountsTestData.Reserve(account, 140.25m);
        hold.Settle(account, AccountsTestData.Now);
        return hold;
    }

    private static JsonElement Published(FundsHold hold, Type reply)
    {
        var domainEvent = hold.DomainEvents.Single(e => e.GetType() == reply);
        var command = Guid.NewGuid();   // the funds command this reply answers
        return JsonDocument.Parse(FundsIntegrationEventMapper.ToOutboxMessage(hold, domainEvent, Guid.NewGuid(), Guid.NewGuid(), command).Payload)
            .RootElement.Clone();
    }
}