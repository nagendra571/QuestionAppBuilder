using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class ScoringRuleConfiguration : IEntityTypeConfiguration<ScoringRule>
{
    public void Configure(EntityTypeBuilder<ScoringRule> builder)
    {
        builder.ToTable("ScoringRules");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Family)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(r => r.MembershipOperator)
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(r => r.MinValue).HasPrecision(18, 4);
        builder.Property(r => r.MaxValue).HasPrecision(18, 4);
        builder.Property(r => r.LogicalKey).IsRequired();
        builder.HasIndex(r => new { r.QuestionId, r.LogicalKey }).IsUnique();
        builder.Property(r => r.LogicalKey).HasDefaultValueSql("NEWID()");
        builder.Property(r => r.MinInclusive).HasDefaultValue(true);
        builder.Property(r => r.MaxInclusive).HasDefaultValue(true);

        builder.HasMany(r => r.Options)
            .WithOne()
            .HasForeignKey(o => o.ScoringRuleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
