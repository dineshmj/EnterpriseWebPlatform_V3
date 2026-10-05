using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;

using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Accounts.Api.Infrastructure.Messaging;

namespace EnterpriseWebPlatform.Accounts.Api.Infrastructure.Persistence;

public sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options, TimeProvider clock)
    : DbContext(options), IAccountsUnitOfWork, IInboxStore
{
    public DbSet<AccountApplication> AccountApplications => Set<AccountApplication>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new EfUnitOfWorkTransaction(await Database.BeginTransactionAsync(cancellationToken));

    public Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken) =>
        InboxMessages.AsNoTracking().AnyAsync(x => x.MessageId == messageId && x.Consumer == consumer, cancellationToken);

    public void RecordProcessed(Guid messageId, string consumer) =>
        InboxMessages.Add(InboxMessage.Processed(messageId, consumer, clock.GetUtcNow()));

    public async Task SaveChangesAsync(WorkflowContext context, CancellationToken cancellationToken)
    {
        var changed = ChangeTracker.Entries<AccountApplication>()
            .Select(e => e.Entity)
            .Where(a => a.DomainEvents.Count > 0)
            .ToList();

        // 1. The aggregates (a new application or account gets its database ID here).
        await SaveTranslatingErrorsAsync(cancellationToken);

        // 2. Their published events, one Outbox row at a time so the database-assigned
        //    sequence follows the order the events were raised (cause before effect).
        foreach (var aggregate in changed)
        {
            var events = aggregate.DomainEvents.ToList();
            aggregate.ClearDomainEvents();

            var (workflowId, correlationId) = await ResolveWorkflowAsync(aggregate, context, cancellationToken);

            Guid? previousMessageId = null;
            foreach (var domainEvent in events)
            {
                var message = AccountsIntegrationEventMapper.ToOutboxMessage(
                    aggregate, domainEvent, workflowId, correlationId, previousMessageId ?? context.CausationId);
                if (message is null)
                    continue;   // internal-only event

                OutboxMessages.Add(message);
                await SaveTranslatingErrorsAsync(cancellationToken);
                previousMessageId = message.Id;
            }
        }
    }

    /// <summary>A decision or an opening continues the workflow the application was created in.</summary>
    private async Task<(Guid? WorkflowId, Guid? CorrelationId)> ResolveWorkflowAsync(
        AccountApplication aggregate, WorkflowContext context, CancellationToken cancellationToken)
    {
        if (context.WorkflowId is not null || context.CorrelationId is not null)
            return (context.WorkflowId, context.CorrelationId);

        var created = await OutboxMessages.AsNoTracking()
            .Where(x => x.AggregateType == AccountsIntegrationEventMapper.AggregateType &&
                        x.AggregateId == aggregate.Id.ToString() &&
                        x.EventType == AccountsIntegrationEventMapper.AccountApplicationCreated)
            .Select(x => new { x.WorkflowId, x.CorrelationId })
            .FirstOrDefaultAsync(cancellationToken);

        return (created?.WorkflowId, created?.CorrelationId);
    }

    private async Task SaveTranslatingErrorsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("The account application was changed by another transaction.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new UniqueConstraintViolationException(ex.InnerException.Message, ex);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountApplication>(e =>
        {
            e.ToTable("account_applications");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.DomainEvents);
            e.Ignore(x => x.IsDecided);

            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.ApplicationRef).HasColumnName("application_ref").IsRequired();
            e.HasIndex(x => x.ApplicationRef).IsUnique().HasDatabaseName("uq_account_applications_application_ref");
            e.Property(x => x.ApplicationNumber).HasColumnName("application_number").HasMaxLength(30).IsRequired();
            e.Property(x => x.CustomerNumber).HasColumnName("customer_number").HasMaxLength(100).IsRequired();
            e.ComplexProperty(x => x.HolderName, h =>
            {
                h.Property(x => x.FirstName).HasColumnName("holder_first_name").HasMaxLength(HolderName.MaxLength).IsRequired();
                h.Property(x => x.LastName).HasColumnName("holder_last_name").HasMaxLength(HolderName.MaxLength).IsRequired();
                h.Ignore(x => x.FullName);
            });
            e.Property(x => x.ComplianceCaseId).HasColumnName("compliance_case_id").IsRequired();
            e.Property(x => x.BranchCode).HasColumnName("branch_code").HasMaxLength(BranchCode.MaxLength)
                .HasConversion(v => v.Value, v => BranchCode.Create(v)).IsRequired();

            e.Property(x => x.InitiatedByUserId).HasColumnName("initiated_by_user_id").HasMaxLength(200);
            e.Property(x => x.ComplianceApprovedByUserId).HasColumnName("compliance_approved_by_user_id").HasMaxLength(200);

            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(30)
                .HasConversion(v => v.ToCode(), v => AccountsCodes.ParseApplicationStatus(v)).IsRequired();
            e.Property(x => x.Product).HasColumnName("product").HasMaxLength(30)
                .HasConversion(v => v!.Value.ToCode(), v => AccountsCodes.ParseProduct(v));

            e.Property(x => x.AssignedOfficerUserId).HasColumnName("assigned_officer_user_id").HasMaxLength(200);
            e.Property(x => x.HoldReason).HasColumnName("hold_reason").HasMaxLength(OfficerText.MaxLength);
            e.Property(x => x.DecisionByUserId).HasColumnName("decision_by_user_id").HasMaxLength(200);
            e.Property(x => x.DecisionAt).HasColumnName("decision_at");
            e.Property(x => x.DecisionRemarks).HasColumnName("decision_remarks").HasMaxLength(OfficerText.MaxLength);
            e.Property(x => x.DecisionId).HasColumnName("decision_id");

            e.Property(x => x.OpeningAttempts).HasColumnName("opening_attempts").IsRequired();
            e.Property(x => x.NextOpeningAt).HasColumnName("next_opening_at");
            e.Property(x => x.LastOpeningError).HasColumnName("last_opening_error");
            e.Property(x => x.AccountNumber).HasColumnName("account_number").HasMaxLength(30);
            e.Property(x => x.OpenedAt).HasColumnName("opened_at");
            e.Property(x => x.FailureReason).HasColumnName("failure_reason");

            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
            e.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken().IsRequired();
        });

        modelBuilder.Entity<Account>(e =>
        {
            e.ToTable("accounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.AccountNumber).HasColumnName("account_number").HasMaxLength(30).IsRequired();
            e.Property(x => x.Bsb).HasColumnName("bsb").HasMaxLength(7).IsRequired();
            e.HasIndex(x => new { x.Bsb, x.AccountNumber }).IsUnique().HasDatabaseName("uq_accounts_bsb_account_number");
            e.Property(x => x.CustomerNumber).HasColumnName("customer_number").HasMaxLength(100).IsRequired();
            e.ComplexProperty(x => x.HolderName, h =>
            {
                h.Property(x => x.FirstName).HasColumnName("holder_first_name").HasMaxLength(HolderName.MaxLength).IsRequired();
                h.Property(x => x.LastName).HasColumnName("holder_last_name").HasMaxLength(HolderName.MaxLength).IsRequired();
                h.Ignore(x => x.FullName);
            });
            e.Property(x => x.ApplicationRef).HasColumnName("application_ref").IsRequired();
            e.HasIndex(x => x.ApplicationRef).IsUnique().HasDatabaseName("uq_accounts_application_ref");
            e.Property(x => x.BranchCode).HasColumnName("branch_code").HasMaxLength(BranchCode.MaxLength)
                .HasConversion(v => v.Value, v => BranchCode.Create(v)).IsRequired();
            e.Property(x => x.Product).HasColumnName("product").HasMaxLength(30)
                .HasConversion(v => v.ToCode(), v => AccountsCodes.ParseProduct(v)).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20)
                .HasConversion(v => v.ToCode(), v => AccountsCodes.ParseAccountStatus(v)).IsRequired();
            e.Property(x => x.CoreBankingReference).HasColumnName("core_banking_reference").HasMaxLength(100).IsRequired();
            e.Property(x => x.OpenedAt).HasColumnName("opened_at").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
            e.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken().IsRequired();
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

    /// <summary>W3C trace context of the request that raised the event.</summary>
    public string? TraceParent { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
}