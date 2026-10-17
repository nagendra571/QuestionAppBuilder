using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public sealed class ResponseAccessTokenConfiguration : IEntityTypeConfiguration<ResponseAccessToken>
{
    public void Configure(EntityTypeBuilder<ResponseAccessToken> builder)
    {
        builder.ToTable("ResponseAccessTokens");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TokenDigest).HasColumnType("binary(32)").IsRequired();
        builder.HasIndex(x => x.TokenDigest).IsUnique();
        builder.Property(x => x.OwnerKey).HasMaxLength(256).IsRequired();
        builder.Property(x => x.VerifiedEmail).HasMaxLength(320).IsRequired();
        builder.HasIndex(x => new { x.ApplicationId, x.VerifiedEmail, x.CreatedUtc });
        builder.HasOne<ApplicationEntity>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationVersion>().WithMany().HasForeignKey(x => x.ApplicationVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ResponseDraft>().WithMany().HasForeignKey(x => x.ResponseId).OnDelete(DeleteBehavior.Restrict);
    }
}
