using System.Text.Json;

using Moq;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.Common;
using EnterpriseWebPlatform.Payments.Api.Domain.Events;
using EnterpriseWebPlatform.Payments.Api.Infrastructure.Messaging;
using EnterpriseWebPlatform.Payments.Api.Tests.Support;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Contracts;

// ┌─ CONTRACT ───────────────────────────────────────────────────────────────────────────
// │ The state names are the consumers' words (their Given(...)); an unknown one fails the
// │ verification instead of running against an empty API. Every payment and message is
// │ made through the aggregates' own methods and the REAL Outbox mapper - what Kafka
// │ would carry, not a copy of it.
// └──────────────────────────────────────────────────────────────────────────────────────
/// <summary>The Payments API's ports as mocks, set per provider state; and its real Outbox messages per scenario.</summary>
internal sealed class PaymentsProviderStates
{
    public Mock<IPaymentRepository> Payments { get; } = new();

    public Mock<IPaymentSagaRepository> Sagas { get; } = new();

    public Mock<IInboxStore> Inbox { get; } = new();

    public Mock<IPaymentsUnitOfWork> UnitOfWork { get; } = new() { DefaultValue = DefaultValue.Mock };

    public Mock<IStaffDirectory> Staff { get; } = new();

    public void Set(string? state)
    {
        foreach (var mock in new Mock[] { Payments, Sagas, Inbox, UnitOfWork, Staff })
            mock.Reset();

        switch (state)
        {
            case null or "":
                break;   // a Kafka message: no state, the message is built per scenario (Messages)

            case "a payment saga waits for Accounts' reply":
                Sagas.Setup(s => s.GetForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => PaymentsTestData.Started().Saga);
                Payments.Setup(p => p.GetAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => PaymentsTestData.NewPayment());
                break;

            case "no payment saga exists for the payment":
            case "no payment was started with the key":
                break;   // every lookup finds nothing

            case "a completed payment exists for the key":
                Payments.Setup(p => p.GetByRefAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => Completed().Payment);
                break;

            default:
                throw new InvalidOperationException($"Unknown provider state '{state}': add it to {nameof(PaymentsProviderStates)}.");
        }
    }

    /// <summary>The message the Outbox relay would publish for this scenario (the consumers' descriptions).</summary>
    public static IReadOnlyDictionary<string, Func<object>> Messages { get; } = new Dictionary<string, Func<object>>
    {
        ["a ReserveFunds command"] = () => Command(PaymentsTestData.Started(), PaymentSaga.ReserveFundsCommand),
        ["a SettleFunds command"] = () => Command(SentToNetwork(), PaymentSaga.SettleFundsCommand),
        ["a ReleaseFunds command"] = () => Command(RefusedByNetwork(), PaymentSaga.ReleaseFundsCommand),
        ["a PaymentCompleted event"] = () => Fact(Completed(), typeof(PaymentCompletedDomainEvent)),
        ["a PaymentFailed event"] = () => Fact(Released(RefusedByNetwork()), typeof(PaymentFailedDomainEvent))
    };

    /// <summary>The network accepted the payment: the saga asks Accounts to settle.</summary>
    private static (Payment Payment, PaymentSaga Saga) SentToNetwork()
    {
        var (payment, saga) = PaymentsTestData.FundsReserved();
        saga.OnNetworkAccepted(payment, "NPP-261015-000042", PaymentsTestData.Policy, PaymentsTestData.Now);
        return (payment, saga);
    }

    /// <summary>The network refused it: the saga asks Accounts to release the funds.</summary>
    private static (Payment Payment, PaymentSaga Saga) RefusedByNetwork()
    {
        var (payment, saga) = PaymentsTestData.FundsReserved();
        saga.OnNetworkRefused(payment, "Account closed", PaymentsTestData.Policy, PaymentsTestData.Now);
        return (payment, saga);
    }

    private static (Payment Payment, PaymentSaga Saga) Completed()
    {
        var (payment, saga) = SentToNetwork();
        saga.OnFundsSettled(payment, Guid.NewGuid(), saga.CurrentCommandId!.Value, PaymentsTestData.Now);
        return (payment, saga);
    }

    private static (Payment Payment, PaymentSaga Saga) Released((Payment Payment, PaymentSaga Saga) compensating)
    {
        var (payment, saga) = compensating;
        saga.OnFundsReleased(payment, Guid.NewGuid(), saga.CurrentCommandId!.Value, nothingWasHeld: false, PaymentsTestData.Now);
        return (payment, saga);
    }

    private static JsonElement Command((Payment Payment, PaymentSaga Saga) at, string command) =>
        Published(at, at.Saga.FundsCommands().Last(c => c.CommandType == command));

    private static JsonElement Fact((Payment Payment, PaymentSaga Saga) at, Type fact) =>
        Published(at, at.Payment.DomainEvents.Single(e => e.GetType() == fact));

    private static JsonElement Published((Payment Payment, PaymentSaga Saga) at, IDomainEvent domainEvent) =>
        JsonDocument.Parse(PaymentsMessageMapper.ToOutboxMessage(at.Saga, at.Payment, domainEvent, userId => userId is null ? null : "SOPHIE01").Payload)
            .RootElement.Clone();
}