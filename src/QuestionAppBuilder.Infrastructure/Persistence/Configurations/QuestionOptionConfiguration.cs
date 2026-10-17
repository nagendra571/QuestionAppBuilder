using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class QuestionOptionConfiguration : IEntityTypeConfiguration<QuestionOption>
{
    public void Configure(EntityTypeBuilder<QuestionOption> builder)
    {
        builder.ToTable("QuestionOptions");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Label)
            .IsRequired()
            .HasMaxLength(ValidationLimits.OptionLabelMaxLength);
        builder.Property(o => o.Value)
            .IsRequired()
            .HasMaxLength(ValidationLimits.OptionValueMaxLength);
        builder.Property(o => o.LogicalKey).IsRequired();
        builder.HasIndex(o => new { o.QuestionId, o.LogicalKey }).IsUnique();
        builder.Property(o => o.LogicalKey).HasDefaultValueSql("NEWID()");
    }
}
