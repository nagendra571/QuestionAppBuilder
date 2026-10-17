using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class ApplicationAuditEventConfiguration : IEntityTypeConfiguration<ApplicationAuditEvent>
{
    public void Configure(EntityTypeBuilder<ApplicationAuditEvent> builder)
    {
        builder.ToTable("ApplicationAuditEvents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ActorKey).HasMaxLength(256).IsRequired();
        builder.Property(x => x.EventType).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => new { x.ApplicationId, x.OccurredUtc });
    }
}
