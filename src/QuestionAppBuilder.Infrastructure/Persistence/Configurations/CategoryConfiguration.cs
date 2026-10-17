using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Name)
            .IsRequired()
            .HasMaxLength(ValidationLimits.NameMaxLength);
        builder.Property(c => c.LogicalKey).IsRequired();
        builder.HasIndex(c => new { c.ApplicationVersionId, c.LogicalKey }).IsUnique();
        builder.Property(c => c.LogicalKey).HasDefaultValueSql("NEWID()");

        builder.HasOne<ApplicationVersion>()
            .WithMany(v => v.Categories)
            .HasForeignKey(c => c.ApplicationVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(c => c.VisibilityCondition).WithMany().HasForeignKey(c => c.VisibilityConditionId).OnDelete(DeleteBehavior.Restrict);
    }
}
