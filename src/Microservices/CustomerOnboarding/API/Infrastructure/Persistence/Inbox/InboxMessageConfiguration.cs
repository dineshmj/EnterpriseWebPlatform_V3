using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Inbox;

public sealed class InboxMessageConfiguration
    : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox_messages");

        builder.HasKey(x => x.Id)
            .HasName("pk_inbox_messages");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(x => x.MessageId)
            .HasColumnName("message_id")
            .IsRequired();

        builder.Property(x => x.Consumer)
            .HasColumnName("consumer")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.ReceivedAt)
            .HasColumnName("received_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.ProcessedAt)
            .HasColumnName("processed_at")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(x => new
        {
            x.MessageId,
            x.Consumer
        })
        .IsUnique()
        .HasDatabaseName("uq_inbox_messages_message_consumer");

        builder.HasIndex(x => x.MessageId)
            .HasDatabaseName("ix_inbox_messages_message_id");
    }
}
