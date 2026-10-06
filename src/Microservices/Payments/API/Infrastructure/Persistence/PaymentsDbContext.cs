using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;

using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Infrastructure.Messaging;

namespace EnterpriseWebPlatform.Payments.Api.Infrastructure.Persistence;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options, TimeProvider clock)
    : DbContext(options), IPaymentsUnitOfWork, IInboxStore, ISagaTrace
{
    /// <summary>
    /// Shadow property (column trace_parent): the W3C traceparent of the request that
    /// started the payment, so background steps continue that trace. Not part of the domain.
    /// </summary>
    private const string TraceParent = "TraceParent";

    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentSaga> PaymentSagas => Set<PaymentSaga>();
    public DbSet<SagaHistoryEntry> SagaHistory => Set<SagaHistoryEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();

    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new EfUnitOfWorkTransaction(await Database.BeginTransactionAsync(cancellationToken));

    public void DiscardChanges() => ChangeTracker.Clear();

    public Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken) =>
        InboxMessages.AsNoTracking().AnyAsync(x => x.MessageId == messageId && x.Consumer == consumer, cancellationToken);

    public void RecordProcessed(Guid messageId, string consumer) =>
        InboxMessages.Add(InboxMessage.Processed(messageId, consumer, clock.GetUtcNow()));

    public IDisposable? Continue(PaymentSaga saga) =>
        MessagingTelemetry.StartContinuation("payment saga step", Entry(saga).Property<string?>(TraceParent).CurrentValue);

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        var sagas = ChangeTracker.Entries<PaymentSaga>().Select(e => e.Entity).ToList();
        var payments = ChangeTracker.Entries<Payment>().Select(e => e.Entity).ToList();

        // A new saga remembers the trace of the request that started it.
        foreach (var added in ChangeTracker.Entries<PaymentSaga>().Where(e => e.State == EntityState.Added))
            added.Property<string?>(TraceParent).CurrentValue = MessagingTelemetry.CurrentTraceParent();

        // 1. The aggregates and the saga's new history lines.
        await SaveTranslatingErrorsAsync(cancellationToken);

        // 2. The saga's commands, then the payment's published facts: one Outbox row at a
        //    time, so the database-assigned sequence keeps the order they were raised in.
        foreach (var saga in sagas.Where(s => s.DomainEvents.Count > 0 || payments.Any(p => p.Id == s.PaymentId && p.DomainEvents.Count > 0)))
        {
            var payment = payments.SingleOrDefault(p => p.Id == saga.PaymentId)
                ?? await Payments.SingleAsync(p => p.Id == saga.PaymentId, cancellationToken);
            var initiatorLanId = await StaffMembers.AsNoTracking()
                .Where(x => x.UserId == saga.InitiatedByUserId).Select(x => x.LanId).FirstOrDefaultAsync(cancellationToken);

            var commands = saga.DomainEvents.ToList();
            saga.ClearDomainEvents();
            foreach (var command in commands)
            {
                OutboxMessages.Add(PaymentsMessageMapper.ToOutboxMessage(saga, payment, command, initiatorLanId));
                await SaveTranslatingErrorsAsync(cancellationToken);
            }

            var facts = payment.DomainEvents.ToList();
            payment.ClearDomainEvents();
            foreach (var fact in facts)
            {
                OutboxMessages.Add(PaymentsMessageMapper.ToOutboxMessage(saga, payment, fact, initiatorLanId));
                await SaveTranslatingErrorsAsync(cancellationToken);
            }
        }

        if (payments.Any(p => p.DomainEvents.Count > 0))
            throw new InvalidOperationException("A payment raised events without its saga: every change to a payment goes through its saga.");
    }

    private async Task SaveTranslatingErrorsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("The payment was changed by another transaction.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new UniqueConstraintViolationException(ex.InnerException.Message, ex);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.DomainEvents);
            e.Ignore(x => x.HasEnded);

            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.PaymentRef).HasColumnName("payment_ref").IsRequired();
            e.HasIndex(x => x.PaymentRef).IsUnique().HasDatabaseName("uq_payments_payment_ref");
            e.Property(x => x.PaymentNumber).HasColumnName("payment_number").HasMaxLength(30).IsRequired();
            e.HasIndex(x => x.PaymentNumber).IsUnique().HasDatabaseName("uq_payments_payment_number");
            e.Property(x => x.CustomerNumber).HasColumnName("customer_number").HasMaxLength(100).IsRequired();
            e.ComplexProperty(x => x.From, a =>
            {
                a.Property(x => x.Bsb).HasColumnName("from_bsb").HasMaxLength(7).IsRequired();
                a.Property(x => x.AccountNumber).HasColumnName("from_account_number").HasMaxLength(9).IsRequired();
            });
            e.Property(x => x.PayeeName).HasColumnName("payee_name").HasMaxLength(PaymentRules.PayeeNameMaxLength).IsRequired();
            e.ComplexProperty(x => x.To, a =>
            {
                a.Property(x => x.Bsb).HasColumnName("to_bsb").HasMaxLength(7).IsRequired();
                a.Property(x => x.AccountNumber).HasColumnName("to_account_number").HasMaxLength(9).IsRequired();
            });
            e.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 2).IsRequired();
            e.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            e.Property(x => x.Reference).HasColumnName("reference").HasMaxLength(PaymentRules.ReferenceMaxLength);
            e.Property(x => x.BranchCode).HasColumnName("branch_code").HasMaxLength(BranchCode.MaxLength)
                .HasConversion(v => v.Value, v => BranchCode.Create(v)).IsRequired();
            e.Property(x => x.InitiatedByUserId).HasColumnName("initiated_by_user_id").HasMaxLength(200).IsRequired();
            e.Property(x => x.ApprovalRequired).HasColumnName("approval_required").IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(30)
                .HasConversion(v => v.ToCode(), v => PaymentsCodes.ParsePaymentStatus(v)).IsRequired();
            e.Property(x => x.CompensationOutcome).HasColumnName("compensation_outcome").HasMaxLength(30)
                .HasConversion(v => v!.Value.ToCode(), v => PaymentsCodes.ParsePaymentStatus(v));
            e.Property(x => x.OutcomeCode).HasColumnName("outcome_code").HasMaxLength(40);
            e.Property(x => x.OutcomeReason).HasColumnName("outcome_reason").HasMaxLength(1000);
            e.Property(x => x.NetworkReference).HasColumnName("network_reference").HasMaxLength(100);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
            e.Property(x => x.EndedAt).HasColumnName("ended_at");
            e.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken().IsRequired();
        });

        modelBuilder.Entity<PaymentSaga>(e =>
        {
            e.ToTable("payment_sagas");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.DomainEvents);
            e.Property<string?>(TraceParent).HasColumnName("trace_parent").HasMaxLength(55);

            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.PaymentId).HasColumnName("payment_id").IsRequired();
            e.HasIndex(x => x.PaymentId).IsUnique().HasDatabaseName("uq_payment_sagas_payment_id");
            e.Property(x => x.PaymentRef).HasColumnName("payment_ref").IsRequired();
            e.HasIndex(x => x.PaymentRef).IsUnique().HasDatabaseName("uq_payment_sagas_payment_ref");
            e.Property(x => x.Step).HasColumnName("step").HasMaxLength(30)
                .HasConversion(v => v.ToCode(), v => PaymentsCodes.ParseSagaStep(v)).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(30)
                .HasConversion(v => v.ToCode(), v => PaymentsCodes.ParseSagaStatus(v)).IsRequired();
            e.Property(x => x.Attempts).HasColumnName("attempts").IsRequired();
            e.Property(x => x.NextCheckAt).HasColumnName("next_check_at");
            e.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(1000);
            e.Property(x => x.CurrentCommandId).HasColumnName("current_command_id");
            e.Property(x => x.LastMessageId).HasColumnName("last_message_id");
            e.Property(x => x.WorkflowId).HasColumnName("workflow_id").IsRequired();
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id").IsRequired();
            e.Property(x => x.InitiatedByUserId).HasColumnName("initiated_by_user_id").HasMaxLength(200).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
            e.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken().IsRequired();

            e.HasMany(x => x.History).WithOne().HasForeignKey(x => x.SagaId);
            e.Navigation(x => x.History).HasField("_history").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<SagaHistoryEntry>(e =>
        {
            e.ToTable("payment_saga_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.SagaId).HasColumnName("saga_id").IsRequired();
            e.Property(x => x.At).HasColumnName("at").IsRequired();
            e.Property(x => x.Step).HasColumnName("step").HasMaxLength(30).IsRequired();
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(40).IsRequired();
            e.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(2000).IsRequired();
            e.Property(x => x.MessageId).HasColumnName("message_id");
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.AggregateType).HasColumnName("aggregate_type").HasMaxLength(100).IsRequired();
            e.Property(x => x.AggregateId).HasColumnName("aggregate_id").HasMaxLength(100).IsRequired();
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(200).IsRequired();
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at").IsRequired();
            e.Property(x => x.WorkflowId).HasColumnName("workflow_id");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.CausationId).HasColumnName("causation_id");
            e.Property(x => x.InitiatedByUserId).HasColumnName("initiated_by_user_id").HasMaxLength(200);
            e.Property(x => x.ActedByUserId).HasColumnName("acted_by_user_id").HasMaxLength(200);
            e.Property(x => x.TraceParent).HasColumnName("trace_parent").HasMaxLength(55);
            e.Property(x => x.PublishedAt).HasColumnName("published_at");
            e.Property(x => x.AttemptCount).HasColumnName("attempt_count").IsRequired();
            e.Property(x => x.LastAttemptAt).HasColumnName("last_attempt_at");
            e.Property(x => x.LastError).HasColumnName("last_error");
        });

        modelBuilder.Entity<StaffMember>(e =>
        {
            e.ToTable("staff_members");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(200);
            e.Property(x => x.LanId).HasColumnName("lan_id").HasMaxLength(20).IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired();
        });

        modelBuilder.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.MessageId).HasColumnName("message_id").IsRequired();
            e.Property(x => x.Consumer).HasColumnName("consumer").HasMaxLength(200).IsRequired();
            e.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
        });
    }

    private sealed class EfUnitOfWorkTransaction(IDbContextTransaction transaction) : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}

public sealed class InboxMessage
{
    private InboxMessage() { }

    public Guid Id { get; private set; }
    public Guid MessageId { get; private set; }
    public string Consumer { get; private set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    public static InboxMessage Processed(Guid messageId, string consumer, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), MessageId = messageId, Consumer = consumer, ReceivedAt = now, ProcessedAt = now
    };
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string AggregateType { get; set; } = string.Empty;
    public string AggregateId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? WorkflowId { get; set; }
    public Guid? CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public string? InitiatedByUserId { get; set; }
    public string? ActedByUserId { get; set; }

    /// <summary>W3C trace context of the request (or continued trace) that raised the message.</summary>
    public string? TraceParent { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
}