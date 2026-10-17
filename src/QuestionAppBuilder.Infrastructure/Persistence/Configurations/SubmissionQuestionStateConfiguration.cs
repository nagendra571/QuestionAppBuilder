using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public sealed class SubmissionQuestionStateConfiguration : IEntityTypeConfiguration<SubmissionQuestionState>
{
    public void Configure(EntityTypeBuilder<SubmissionQuestionState> builder)
    {
        builder.ToTable("SubmissionQuestionStates");
        builder.HasKey(x => new { x.SubmissionId, x.QuestionId });
        builder.Property(x => x.QuestionLogicalKey).IsRequired();
        builder.HasOne<Question>().WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Restrict);
    }
}
