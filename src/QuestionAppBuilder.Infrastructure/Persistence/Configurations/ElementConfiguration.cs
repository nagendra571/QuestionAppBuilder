using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class ElementConfiguration : IEntityTypeConfiguration<Element>
{
    public void Configure(EntityTypeBuilder<Element> builder)
    {
        builder.ToTable("Elements");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Kind)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(e => e.Label)
            .IsRequired()
            .HasMaxLength(ValidationLimits.LabelMaxLength);
        builder.Property(e => e.HelpText)
            .HasMaxLength(ValidationLimits.HelpTextMaxLength);
        builder.Property(e => e.LogicalKey).IsRequired();
        builder.HasIndex(e => new { e.CategoryId, e.LogicalKey }).IsUnique();
        builder.Property(e => e.LogicalKey).HasDefaultValueSql("NEWID()");

        builder.HasOne<Category>()
            .WithMany(c => c.Elements)
            .HasForeignKey(e => e.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Question)
            .WithOne()
            .HasForeignKey<Question>(q => q.ElementId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<VisibilityCondition>().WithMany().HasForeignKey(e => e.VisibilityConditionId).OnDelete(DeleteBehavior.Restrict);
    }
}
