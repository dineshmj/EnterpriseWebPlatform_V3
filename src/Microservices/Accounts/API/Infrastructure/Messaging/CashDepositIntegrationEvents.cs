using System.Text.Json;

using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.Common;
using EnterpriseWebPlatform.Accounts.Api.Domain.Events;
using EnterpriseWebPlatform.Accounts.Api.Infrastructure.Persistence;
using EnterpriseWebPlatform.Common.Observability;

namespace EnterpriseWebPlatform.Accounts.Api.Infrastructure.Messaging;

/// <summary>
/// Cash paid in at the counter (topic accounts.account.deposited, key = DepositRef). No name
/// or address: the customer is named by number only.
/// </summary>
public sealed record CashDepositedPayload(
    Guid DepositRef,
    string DepositNumber,
    string CustomerNumber,
    string BranchCode,
    string Bsb,
    string AccountNumber,
    decimal Amount,
    string Currency,
    string Method,
    string Status,
    string ReceivedByUserId,
    string? ReceivedByLanId,
    DateTimeOffset RecordedAt);

/// <summary>Translates CashDeposit domain events into published integration events (Outbox rows).</summary>
internal static class CashDepositIntegrationEventMapper
{
    public const string AggregateType = "CashDeposit";
    public const string CashDeposited = "CashDeposited";

    private const string Source = "accounts";
    private const int SchemaVersion = 1;

    public static OutboxMessage ToOutboxMessage(
        CashDeposit deposit, IDomainEvent domainEvent, WorkflowContext context, string? receivedByLanId)
    {
        if (domainEvent is not CashDepositedDomainEvent deposited)
            throw new InvalidOperationException($"No integration event mapping exists for domain event {domainEvent.GetType().Name}.");

        var messageId = Guid.NewGuid();
        var payload = new CashDepositedPayload(
            deposit.DepositRef,
            deposit.DepositNumber,
            deposit.CustomerNumber,
            deposit.BranchCode.Value,
            deposit.Bsb,
            deposit.AccountNumber,
            deposit.Amount,
            deposit.Currency,
            Method: "CASH",
            Status: "RECORDED",
            deposit.ReceivedByUserId,
            receivedByLanId,
            deposit.RecordedAt);

        return new OutboxMessage
        {
            Id = messageId,
            AggregateType = AggregateType,
            AggregateId = deposit.DepositRef.ToString(),
            EventType = CashDeposited,
            Payload = JsonSerializer.Serialize(new AccountsIntegrationEventEnvelope<CashDepositedPayload>(
                messageId, CashDeposited, SchemaVersion, Source, deposited.OccurredAt, context.WorkflowId, context.CorrelationId,
                context.CausationId, deposit.ReceivedByUserId, payload, receivedByLanId)),
            OccurredAt = deposited.OccurredAt,
            WorkflowId = context.WorkflowId,
            CorrelationId = context.CorrelationId,
            CausationId = context.CausationId,
            InitiatedByUserId = deposit.ReceivedByUserId,
            ActedByUserId = deposit.ReceivedByUserId,
            TraceParent = MessagingTelemetry.CurrentTraceParent()
        };
    }
}