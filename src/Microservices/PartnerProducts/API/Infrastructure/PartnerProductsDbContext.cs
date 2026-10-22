using System.Security.Claims;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.PartnerProducts.Api.Domain;
using EnterpriseWebPlatform.PartnerProducts.Api.Infrastructure.Messaging;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Infrastructure;

/// <summary>EwpPartnerProductsDb: EWP's side of the Partner Bank's Digital Gold (see PartnerProductsDb/EwpPartnerProductsDb.sql).</summary>
public sealed class PartnerProductsDbContext(
    DbContextOptions<PartnerProductsDbContext> options,
    IHttpContextAccessor httpContext) : DbContext(options)
{
    public DbSet<GoldAccountApplication> Applications => Set<GoldAccountApplication>();

    public DbSet<GoldPurchase> Purchases => Set<GoldPurchase>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    // ┌─ :: RESILIENCE :: Transactional Outbox (write side) ───────────────────────────────────────────
    // │ Why : An event must exist exactly when its change does: the relay publishes only what committed.
    // │ How : The changed aggregates are saved first, then their raised events as Outbox rows - one
    // │       row per save, so the database-assigned sequence keeps the order they were raised in - all
    // │       inside ONE transaction (the caller's, if it has one). A failed save leaves no event.
    // │ Ref : Features 1.3.1 | Transactional Outbox pattern | PartnerProductsOutboxPublisher
    // └────────────────────────────────────────────────────────────────────────────────────────────────
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var raising = ChangeTracker.Entries()
            .Select(e => e.Entity)
            .OfType<IRaisesDigitalGoldEvents>()
            .Where(a => a.Events.Count > 0)
            .ToList();
        if (raising.Count == 0)
            return await base.SaveChangesAsync(cancellationToken);

        var actor = CurrentActor();
        var transaction = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var changed = await base.SaveChangesAsync(cancellationToken);
            foreach (var aggregate in raising)
            {
                foreach (var raised in aggregate.Events)
                {
                    OutboxMessages.Add(aggregate switch
                    {
                        GoldAccountApplication application => DigitalGoldIntegrationEventMapper.ToOutboxMessage(application, raised, actor),
                        GoldPurchase purchase => DigitalGoldIntegrationEventMapper.ToOutboxMessage(purchase, raised, actor),
                        _ => throw new InvalidOperationException($"No published contract for {aggregate.GetType().Name}.")
                    });
                    changed += await base.SaveChangesAsync(cancellationToken);
                }
            }
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            raising.ForEach(a => a.ClearEvents());
            return changed;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Who acted comes from the validated access token of THIS request - the staff member
    // │ (sub, lan_id) or, for the workflow's internal calls, the worker's machine client
    // │ (client_id) - never from a request body.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private EventActor CurrentActor()
    {
        var user = httpContext.HttpContext?.User;
        return new EventActor(
            user?.FindFirstValue("sub") ?? user?.FindFirstValue("client_id"),
            user?.FindFirstValue("lan_id"));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GoldAccountApplication>(e =>
        {
            e.ToTable("gold_account_applications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.Number).HasColumnName("application_number").HasMaxLength(30);
            e.Property(x => x.CustomerNumber).HasColumnName("customer_number").HasMaxLength(20);
            e.Property(x => x.CustomerName).HasColumnName("customer_name").HasMaxLength(140);
            e.Property(x => x.DateOfBirth).HasColumnName("date_of_birth");
            e.Property(x => x.Mobile).HasColumnName("mobile").HasMaxLength(12);
            e.Property(x => x.BranchCode).HasColumnName("branch_code").HasMaxLength(20);
            e.Property(x => x.FormNumber).HasColumnName("form_number").HasMaxLength(32);
            e.Property(x => x.FormSignedOn).HasColumnName("form_signed_on");
            e.Property(x => x.FormDocumentId).HasColumnName("form_document_id");
            e.Property(x => x.FormScanSha256).HasColumnName("form_scan_sha256").HasMaxLength(64);
            e.Property(x => x.RecordedBySubject).HasColumnName("recorded_by_subject").HasMaxLength(200);
            e.Property(x => x.RecordedByLanId).HasColumnName("recorded_by_lan_id").HasMaxLength(50);
            e.Property(x => x.RecordedAt).HasColumnName("recorded_at");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
            e.Property(x => x.PartnerAccountNumber).HasColumnName("partner_account_number").HasMaxLength(20);
            e.Property(x => x.StatusReason).HasColumnName("status_reason").HasMaxLength(500);
            e.Property(x => x.InstructionId).HasColumnName("instruction_id");
            e.Property(x => x.SentAt).HasColumnName("sent_at");
            e.Property(x => x.SentByLanId).HasColumnName("sent_by_lan_id").HasMaxLength(50);
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
            // │ Optimistic concurrency: every change raises the version and is saved only if
            // │ nobody changed the row meanwhile - two people sending the same Form VII at once
            // │ get one send and one 409, never two.
            // └──────────────────────────────────────────────────────────────────────────────────────
            e.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
            e.Ignore(x => x.IsOpen);
            e.Ignore(x => x.IsActive);
            e.Ignore(x => x.Events);
        });

        modelBuilder.Entity<GoldPurchase>(e =>
        {
            e.ToTable("gold_purchases");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.Number).HasColumnName("purchase_number").HasMaxLength(30);
            e.Property(x => x.ApplicationId).HasColumnName("application_id");
            e.Property(x => x.CustomerNumber).HasColumnName("customer_number").HasMaxLength(20);
            e.Property(x => x.PartnerAccountNumber).HasColumnName("partner_account_number").HasMaxLength(20);
            e.Property(x => x.AmountAud).HasColumnName("amount_aud").HasPrecision(18, 2);
            e.Property(x => x.DebtorBsb).HasColumnName("debtor_bsb").HasMaxLength(7);
            e.Property(x => x.DebtorAccountNumber).HasColumnName("debtor_account_number").HasMaxLength(9);
            e.Property(x => x.BranchCode).HasColumnName("branch_code").HasMaxLength(20);
            e.Property(x => x.SlipNumber).HasColumnName("slip_number").HasMaxLength(32);
            e.Property(x => x.SlipSignedOn).HasColumnName("slip_signed_on");
            e.Property(x => x.SlipMobile).HasColumnName("slip_mobile").HasMaxLength(12);
            e.Property(x => x.SlipDocumentId).HasColumnName("slip_document_id");
            e.Property(x => x.SlipScanSha256).HasColumnName("slip_scan_sha256").HasMaxLength(64);
            e.Property(x => x.RecordedBySubject).HasColumnName("recorded_by_subject").HasMaxLength(200);
            e.Property(x => x.RecordedByLanId).HasColumnName("recorded_by_lan_id").HasMaxLength(50);
            e.Property(x => x.RecordedAt).HasColumnName("recorded_at");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
            e.Property(x => x.InstructionId).HasColumnName("instruction_id");
            e.Property(x => x.SentAt).HasColumnName("sent_at");
            e.Property(x => x.SentByLanId).HasColumnName("sent_by_lan_id").HasMaxLength(50);
            e.Property(x => x.PartnerOrderNumber).HasColumnName("partner_order_number").HasMaxLength(30);
            e.Property(x => x.PricePerGram).HasColumnName("price_per_gram").HasPrecision(18, 2);
            e.Property(x => x.Grams).HasColumnName("grams").HasPrecision(18, 4);
            e.Property(x => x.QuoteExpiresAt).HasColumnName("quote_expires_at");
            e.Property(x => x.PaymentRef).HasColumnName("payment_ref");
            e.Property(x => x.PaymentNumber).HasColumnName("payment_number").HasMaxLength(40);
            e.Property(x => x.PaymentStartedByLanId).HasColumnName("payment_started_by_lan_id").HasMaxLength(50);
            e.Property(x => x.PaymentStartedAt).HasColumnName("payment_started_at");
            e.Property(x => x.NetworkReference).HasColumnName("network_reference").HasMaxLength(64);
            e.Property(x => x.PaidAt).HasColumnName("paid_at");
            e.Property(x => x.AllocatedAt).HasColumnName("allocated_at");
            e.Property(x => x.StatusReason).HasColumnName("status_reason").HasMaxLength(500);
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            // Optimistic concurrency, as for applications: the counter and the workflow never overwrite each other.
            e.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
            e.Ignore(x => x.Events);
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
    }
}