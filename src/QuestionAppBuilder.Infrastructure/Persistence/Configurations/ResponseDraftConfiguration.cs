using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public sealed class ResponseDraftConfiguration : IEntityTypeConfiguration<ResponseDraft>
{
    public void Configure(EntityTypeBuilder<ResponseDraft> builder)
    {
        builder.ToTable("ResponseDrafts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.OwnerKey).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.RevisionNonce).HasDefaultValueSql("NEWID()").IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.OwnerKey, x.Status, x.UpdatedUtc });
        builder.HasIndex(x => new { x.ApplicationId, x.ApplicationVersionId });
        builder.HasOne<ApplicationEntity>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationVersion>().WithMany().HasForeignKey(x => x.ApplicationVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Answers).WithOne().HasForeignKey(x => x.ResponseDraftId).OnDelete(DeleteBehavior.Cascade);
    }
}
