using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.PartnerProducts.Api.Domain;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Infrastructure;

/// <summary>EwpPartnerProductsDb: EWP's side of the Partner Bank's Digital Gold (see PartnerProductsDb/EwpPartnerProductsDb.sql).</summary>
public sealed class PartnerProductsDbContext(DbContextOptions<PartnerProductsDbContext> options) : DbContext(options)
{
    public DbSet<GoldAccountApplication> Applications => Set<GoldAccountApplication>();

    public DbSet<GoldPurchase> Purchases => Set<GoldPurchase>();

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
            e.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
            e.Ignore(x => x.IsOpen);
            e.Ignore(x => x.IsActive);
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
            e.Property(x => x.PartnerOrderNumber).HasColumnName("partner_order_number").HasMaxLength(30);
            e.Property(x => x.StatusReason).HasColumnName("status_reason").HasMaxLength(500);
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        });
    }
}