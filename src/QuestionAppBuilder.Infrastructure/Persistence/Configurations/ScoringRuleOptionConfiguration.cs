using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class ScoringRuleOptionConfiguration : IEntityTypeConfiguration<ScoringRuleOption>
{
    public void Configure(EntityTypeBuilder<ScoringRuleOption> builder)
    {
        builder.ToTable("ScoringRuleOptions");
        builder.HasKey(o => o.Id);
        builder.HasIndex(o => new { o.ScoringRuleId, o.QuestionOptionId }).IsUnique();

        builder.HasOne(o => o.QuestionOption)
            .WithMany()
            .HasForeignKey(o => o.QuestionOptionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
