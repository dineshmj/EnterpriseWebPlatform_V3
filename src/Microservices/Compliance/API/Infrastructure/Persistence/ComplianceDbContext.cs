using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;

using EnterpriseWebPlatform.Compliance.Api.Application.Abstractions;
using EnterpriseWebPlatform.Compliance.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Compliance.Api.Infrastructure.Messaging;

namespace EnterpriseWebPlatform.Compliance.Api.Infrastructure.Persistence;

public sealed class ComplianceDbContext(DbContextOptions<ComplianceDbContext> options, TimeProvider clock)
    : DbContext(options), IComplianceUnitOfWork, IInboxStore
{
    public DbSet<ComplianceCase> ComplianceCases => Set<ComplianceCase>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();

    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new EfUnitOfWorkTransaction(await Database.BeginTransactionAsync(cancellationToken));

    public Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken) =>
        InboxMessages.AsNoTracking().AnyAsync(x => x.MessageId == messageId && x.Consumer == consumer, cancellationToken);

    public void RecordProcessed(Guid messageId, string consumer) =>
        InboxMessages.Add(InboxMessage.Processed(messageId, consumer, clock.GetUtcNow()));

    public async Task SaveChangesAsync(WorkflowContext context, CancellationToken cancellationToken)
    {
        var changed = ChangeTracker.Entries<ComplianceCase>()
            .Select(e => e.Entity)
            .Where(a => a.DomainEvents.Count > 0)
            .ToList();

        // 1. The aggregates (a new case gets its database ID here).
        await SaveTranslatingErrorsAsync(cancellationToken);

        // 2. Their published events, one Outbox row at a time so the database-assigned
        //    sequence follows the order the events were raised (cause before effect).
        foreach (var aggregate in changed)
        {
            var events = aggregate.DomainEvents.ToList();
            aggregate.ClearDomainEvents();

            var (workflowId, correlationId) = await ResolveWorkflowAsync(aggregate, context, cancellationToken);
            // LAN IDs of everyone the events can name (this context's staff directory).
            var people = new[] { aggregate.InitiatedByUserId, aggregate.DecisionByUserId }.Where(x => x is not null).Distinct().ToList();
            var lanIds = await StaffMembers.AsNoTracking()
                .Where(x => people.Contains(x.UserId))
                .ToDictionaryAsync(x => x.UserId, x => x.LanId, cancellationToken);
            string? LanOf(string? userId) => userId is not null && lanIds.TryGetValue(userId, out var lan) ? lan : null;

            Guid? previousMessageId = null;
            foreach (var domainEvent in events)
            {
                var message = ComplianceIntegrationEventMapper.ToOutboxMessage(
                    aggregate, domainEvent, workflowId, correlationId, previousMessageId ?? context.CausationId, LanOf);
                if (message is null)
                    continue;   // internal-only event

                OutboxMessages.Add(message);
                await SaveTranslatingErrorsAsync(cancellationToken);
                previousMessageId = message.Id;
            }
        }
    }

    /// <summary>A decision continues the workflow the case was opened in.</summary>
    private async Task<(Guid? WorkflowId, Guid? CorrelationId)> ResolveWorkflowAsync(
        ComplianceCase aggregate, WorkflowContext context, CancellationToken cancellationToken)
    {
        if (context.WorkflowId is not null || context.CorrelationId is not null)
            return (context.WorkflowId, context.CorrelationId);

        var opened = await OutboxMessages.AsNoTracking()
            .Where(x => x.AggregateType == ComplianceIntegrationEventMapper.AggregateType &&
                        x.AggregateId == aggregate.Id.ToString() &&
                        x.EventType == ComplianceIntegrationEventMapper.ComplianceCaseCreated)
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
            throw new ConcurrencyConflictException("The compliance case was changed by another transaction.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new UniqueConstraintViolationException(ex.InnerException.Message, ex);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ComplianceCase>(e =>
        {
            e.ToTable("compliance_cases");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.DomainEvents);
            e.Ignore(x => x.IsTerminal);

            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.ApplicationRef).HasColumnName("application_ref").IsRequired();
            e.HasIndex(x => x.ApplicationRef).IsUnique().HasDatabaseName("uq_compliance_cases_application_ref");
            e.Property(x => x.ApplicationNumber).HasColumnName("application_number").HasMaxLength(30).IsRequired();
            e.Property(x => x.CustomerNumber).HasColumnName("customer_number").HasMaxLength(100).IsRequired();
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
            e.Property(x => x.KycCaseId).HasColumnName("kyc_case_id").IsRequired();
            e.Property(x => x.BranchCode).HasColumnName("branch_code").HasMaxLength(BranchCode.MaxLength)
                .HasConversion(v => v.Value, v => BranchCode.Create(v)).IsRequired();

            e.Property(x => x.InitiatedByUserId).HasColumnName("initiated_by_user_id").HasMaxLength(200);
            e.Property(x => x.KycIdentityDecidedByUserId).HasColumnName("kyc_identity_decided_by_user_id").HasMaxLength(200);
            e.Property(x => x.KycDocumentDecidedByUserId).HasColumnName("kyc_document_decided_by_user_id").HasMaxLength(200);

            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(30)
                .HasConversion(v => v.ToCode(), v => ComplianceCodes.ParseStatus(v)).IsRequired();

            e.Property(x => x.ScreeningOutcome).HasColumnName("screening_outcome").HasMaxLength(30)
                .HasConversion(v => v!.Value.ToCode(), v => ComplianceCodes.ParseScreeningOutcome(v));
            e.Property(x => x.ScreeningProvider).HasColumnName("screening_provider").HasMaxLength(100);
            e.Property(x => x.ScreeningReference).HasColumnName("screening_reference").HasMaxLength(100);
            e.Property(x => x.ScreenedAt).HasColumnName("screened_at");
            e.Property(x => x.ScreeningAttempts).HasColumnName("screening_attempts").IsRequired();
            e.Property(x => x.NextScreeningAt).HasColumnName("next_screening_at");
            e.Property(x => x.LastScreeningError).HasColumnName("last_screening_error");

            e.Property(x => x.RiskRating).HasColumnName("risk_rating").HasMaxLength(10)
                .HasConversion(v => v!.Value.ToCode(), v => ComplianceCodes.ParseRiskRating(v));
            e.Property(x => x.RequiredClearance).HasColumnName("required_clearance");

            e.Property(x => x.AssignedOfficerUserId).HasColumnName("assigned_officer_user_id").HasMaxLength(200);
            e.Property(x => x.HoldReason).HasColumnName("hold_reason").HasMaxLength(OfficerText.MaxLength);
            e.Property(x => x.DecisionByUserId).HasColumnName("decision_by_user_id").HasMaxLength(200);
            e.Property(x => x.DecisionAt).HasColumnName("decision_at");
            e.Property(x => x.DecisionRemarks).HasColumnName("decision_remarks").HasMaxLength(OfficerText.MaxLength);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
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

    /// <summary>W3C trace context of the request that raised the event.</summary>
    public string? TraceParent { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
}