using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class ApplicationConfiguration : IEntityTypeConfiguration<Application>
{
    public void Configure(EntityTypeBuilder<Application> builder)
    {
        builder.ToTable("Applications");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Name)
            .IsRequired()
            .HasMaxLength(ValidationLimits.NameMaxLength);
        builder.HasIndex(a => a.Name).IsUnique();
        builder.Property(a => a.RowVersion).IsRowVersion();

        builder.HasOne(a => a.ApplicationType)
            .WithMany()
            .HasForeignKey(a => a.ApplicationTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.CurrentVersion)
            .WithMany()
            .HasForeignKey(a => a.CurrentVersionId);
    }
}
