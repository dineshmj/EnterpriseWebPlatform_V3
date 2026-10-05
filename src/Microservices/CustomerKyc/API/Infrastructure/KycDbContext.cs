using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;

using EnterpriseWebPlatform.CustomerKyc.Api.Application.Abstractions;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure.Messaging;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

public sealed class KycDbContext(DbContextOptions<KycDbContext> options, TimeProvider clock)
    : DbContext(options), IKycUnitOfWork, IInboxStore
{
    public DbSet<KycCase> KycCases => Set<KycCase>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    public Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken) =>
        InboxMessages.AsNoTracking().AnyAsync(x => x.MessageId == messageId && x.Consumer == consumer, cancellationToken);

    public void RecordProcessed(Guid messageId, string consumer) =>
        InboxMessages.Add(InboxMessage.Processed(messageId, consumer, clock.GetUtcNow()));

    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new EfUnitOfWorkTransaction(await Database.BeginTransactionAsync(cancellationToken));

    public async Task SaveChangesAsync(WorkflowContext context, CancellationToken cancellationToken)
    {
        var changedAggregates = ChangeTracker.Entries<KycCase>()
            .Select(entry => entry.Entity)
            .Where(aggregate => aggregate.DomainEvents.Count > 0)
            .ToList();

        // 1. The aggregates themselves (a new case gets its database ID here).
        await SaveTranslatingErrorsAsync(cancellationToken);

        // 2. Their domain events, as Outbox rows. Each row is saved on its own:
        //    outbox_messages.sequence is assigned in INSERT order and EF Core may
        //    reorder rows inserted by one SaveChanges, but the relay publishes each
        //    aggregate's events strictly in sequence order (cause before effect).
        foreach (var aggregate in changedAggregates)
        {
            var domainEvents = aggregate.DomainEvents.ToList();
            aggregate.ClearDomainEvents();

            var (workflowId, correlationId) = await ResolveWorkflowAsync(aggregate, context, cancellationToken);

            Guid? previousMessageId = null;
            foreach (var domainEvent in domainEvents)
            {
                // The first event is caused by the command / triggering message; each
                // later event raised by the same change is caused by the one before it.
                var message = await KycIntegrationEventMapper.ToOutboxMessageAsync(
                    this,
                    aggregate,
                    domainEvent,
                    workflowId,
                    correlationId,
                    causationId: previousMessageId ?? context.CausationId,
                    previousMessageId,
                    cancellationToken);

                if (message is null)
                    continue;   // internal-only event (not published)

                OutboxMessages.Add(message);
                await SaveTranslatingErrorsAsync(cancellationToken);
                previousMessageId = message.Id;
            }
        }
    }

    private async Task<(Guid? WorkflowId, Guid? CorrelationId)> ResolveWorkflowAsync(
        KycCase aggregate,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        if (context.WorkflowId is not null || context.CorrelationId is not null)
            return (context.WorkflowId, context.CorrelationId);

        // A decision continues the workflow the case was opened in.
        var opened = await OutboxMessages
            .AsNoTracking()
            .Where(x => x.AggregateType == KycIntegrationEventMapper.AggregateType &&
                        x.AggregateId == aggregate.Id.ToString() &&
                        x.EventType == KycIntegrationEventMapper.KycCaseCreated)
            .OrderBy(x => x.OccurredAt)
            .Select(x => new { x.WorkflowId, x.CorrelationId })
            .FirstOrDefaultAsync(cancellationToken);

        return (opened?.WorkflowId, opened?.CorrelationId);
    }

    private async Task SaveTranslatingErrorsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("The KYC case was changed by another transaction.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new UniqueConstraintViolationException(ex.InnerException.Message, ex);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KycCase>(e =>
        {
            e.ToTable("kyc_cases");
            e.HasKey(x => x.Id).HasName("pk_kyc_cases");
            e.Ignore(x => x.DomainEvents);
            e.Ignore(x => x.IsTerminal);

            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.ApplicationRef).HasColumnName("application_ref").HasColumnType("uuid").IsRequired();
            e.Property(x => x.ApplicationNumber).HasColumnName("application_number").HasMaxLength(30).IsRequired();
            e.HasIndex(x => x.ApplicationRef).IsUnique().HasDatabaseName("uq_kyc_cases_application_ref");

            e.Property(x => x.BranchCode)
                .HasColumnName("branch_code")
                .HasMaxLength(BranchCode.MaxLength)
                .HasConversion(v => v.Value, v => BranchCode.Create(v))
                .IsRequired();

            e.Property(x => x.AssignedOfficerUserId).HasColumnName("assigned_officer_user_id").HasMaxLength(200);
            e.Property(x => x.CustomerNumber).HasColumnName("customer_number").HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.CustomerNumber).HasDatabaseName("ix_kyc_cases_customer_number");

            e.Property(x => x.Status)
                .HasColumnName("status")
                .HasMaxLength(50)
                .HasConversion(v => v.ToCode(), v => KycCodes.ParseCaseStatus(v))
                .IsRequired();

            e.Property(x => x.InitiatedByUserId).HasColumnName("initiated_by_user_id").HasMaxLength(200);

            e.ComplexProperty(x => x.Applicant, a =>
            {
                a.Property(x => x.FirstName).HasColumnName("applicant_first_name").HasMaxLength(Applicant.MaxNameLength).IsRequired();
                a.Property(x => x.LastName).HasColumnName("applicant_last_name").HasMaxLength(Applicant.MaxNameLength).IsRequired();
                a.Property(x => x.AddressLine1).HasColumnName("applicant_address_line1").HasMaxLength(200);
                a.Property(x => x.AddressLine2).HasColumnName("applicant_address_line2").HasMaxLength(200);
                a.Property(x => x.City).HasColumnName("applicant_city").HasMaxLength(100);
                a.Property(x => x.State).HasColumnName("applicant_state").HasMaxLength(100);
                a.Property(x => x.PostalCode).HasColumnName("applicant_postal_code").HasMaxLength(20);
                a.Property(x => x.CountryCode).HasColumnName("applicant_country_code").HasMaxLength(2);
                a.Ignore(x => x.FullName);
            });

            e.Property(x => x.IdentityProofDocumentId).HasColumnName("identity_proof_document_id").HasColumnType("uuid").IsRequired();
            e.Property(x => x.TaxProofDocumentId).HasColumnName("tax_proof_document_id").HasColumnType("uuid").IsRequired();

            e.ComplexProperty(x => x.IdentityVerification, s => MapStage(s, "identity_verification"));
            e.ComplexProperty(x => x.DocumentVerification, s => MapStage(s, "document_verification"));

            e.Property(x => x.DecisionByUserId).HasColumnName("decision_by_user_id").HasMaxLength(200);
            e.Property(x => x.DecisionAt).HasColumnName("decision_at").HasColumnType("timestamp with time zone");
            e.Property(x => x.DecisionRemarks).HasColumnName("decision_remarks").HasMaxLength(DecisionRemarks.MaxLength);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired();
            e.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken().IsRequired();
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id).HasName("pk_kyc_outbox_messages");
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.AggregateType).HasColumnName("aggregate_type").HasMaxLength(100).IsRequired();
            e.Property(x => x.AggregateId).HasColumnName("aggregate_id").HasMaxLength(100).IsRequired();
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(200).IsRequired();
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone").IsRequired();
            e.Property(x => x.WorkflowId).HasColumnName("workflow_id").HasColumnType("uuid");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasColumnType("uuid");
            e.Property(x => x.CausationId).HasColumnName("causation_id").HasColumnType("uuid");
            e.Property(x => x.InitiatedByUserId).HasColumnName("initiated_by_user_id").HasMaxLength(200);
            e.Property(x => x.ActedByUserId).HasColumnName("acted_by_user_id").HasMaxLength(200);
            e.Property(x => x.TraceParent).HasColumnName("trace_parent").HasMaxLength(55);
            e.Property(x => x.PublishedAt).HasColumnName("published_at").HasColumnType("timestamp with time zone");
            e.Property(x => x.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0).IsRequired();
            e.Property(x => x.LastAttemptAt).HasColumnName("last_attempt_at").HasColumnType("timestamp with time zone");
            e.Property(x => x.LastError).HasColumnName("last_error");
            e.HasIndex(x => x.OccurredAt).HasDatabaseName("ix_kyc_outbox_unpublished").HasFilter("published_at IS NULL");
            e.HasIndex(x => x.WorkflowId).HasDatabaseName("ix_kyc_outbox_workflow_id");
            e.HasIndex(x => x.CorrelationId).HasDatabaseName("ix_kyc_outbox_correlation_id");
            e.HasIndex(x => x.CausationId).HasDatabaseName("ix_kyc_outbox_causation_id");
        });

        modelBuilder.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_messages");
            e.HasKey(x => x.Id).HasName("pk_kyc_inbox_messages");
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.MessageId).HasColumnName("message_id").IsRequired();
            e.Property(x => x.Consumer).HasColumnName("consumer").HasMaxLength(200).IsRequired();
            e.Property(x => x.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamp with time zone").IsRequired();
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at").HasColumnType("timestamp with time zone");
            e.HasIndex(x => new { x.MessageId, x.Consumer }).IsUnique().HasDatabaseName("uq_kyc_inbox_messages_message_consumer");
        });
    }

    private static void MapStage(ComplexPropertyBuilder<VerificationStage> stage, string prefix)
    {
        stage.Property(x => x.Status)
            .HasColumnName($"{prefix}_status")
            .HasMaxLength(50)
            .HasConversion(v => v.ToCode(), v => KycCodes.ParseVerificationStatus(v))
            .IsRequired();
        stage.Property(x => x.DecidedByUserId).HasColumnName($"{prefix}_by_user_id").HasMaxLength(200);
        stage.Property(x => x.DecidedAt).HasColumnName($"{prefix}_at").HasColumnType("timestamp with time zone");
        stage.Property(x => x.Remarks).HasColumnName($"{prefix}_remarks").HasMaxLength(DecisionRemarks.MaxLength);
    }

    private sealed class EfUnitOfWorkTransaction(IDbContextTransaction transaction) : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}