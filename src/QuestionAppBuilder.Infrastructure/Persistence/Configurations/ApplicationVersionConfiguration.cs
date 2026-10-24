using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class ApplicationVersionConfiguration : IEntityTypeConfiguration<ApplicationVersion>
{
    public void Configure(EntityTypeBuilder<ApplicationVersion> builder)
    {
        builder.ToTable("ApplicationVersions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(v => v.NavigationMode).HasConversion<string>().HasMaxLength(32).HasDefaultValue(NavigationMode.Sequential).IsRequired();
        builder.Property(v => v.RespondentAccessMode).HasConversion<string>().HasMaxLength(32).HasDefaultValue(RespondentAccessMode.HostSignIn).IsRequired();
        builder.Property(v => v.ResultVisibility).HasConversion<string>().HasMaxLength(32).HasDefaultValue(ResultVisibility.Total).IsRequired();
        builder.Property(v => v.SubmittedApplicationVisibility).HasConversion<string>().HasMaxLength(32)
            .HasDefaultValue(SubmittedApplicationVisibility.WithoutScores).IsRequired();
        builder.Property(v => v.SubmitLabel).HasMaxLength(200).IsRequired();
        builder.Property(v => v.ConfirmationText).HasMaxLength(4000).IsRequired();
        builder.Property(v => v.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(v => v.RevisionNonce).HasDefaultValueSql("NEWID()").IsRequired();
        builder.Property(v => v.RowVersion).IsRowVersion();
        builder.HasIndex(v => v.ApplicationId).IsUnique().HasFilter("[Status] = 'Draft'");
        builder.HasIndex(v => new { v.ApplicationId, v.VersionNumber }).IsUnique();

        builder.HasOne<ApplicationEntity>()
            .WithMany(a => a.Versions)
            .HasForeignKey(v => v.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
