using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public sealed class SubmissionRuleTraceConfiguration : IEntityTypeConfiguration<SubmissionRuleTrace>
{
    public void Configure(EntityTypeBuilder<SubmissionRuleTrace> builder)
    {
        builder.ToTable("SubmissionRuleTraces");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.SubmissionId, x.QuestionId, x.RuleId }).IsUnique();
        builder.HasOne<Question>().WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ScoringRule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Restrict);
    }
}
