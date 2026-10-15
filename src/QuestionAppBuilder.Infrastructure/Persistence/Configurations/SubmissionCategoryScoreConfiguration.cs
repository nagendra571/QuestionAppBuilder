using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Infrastructure.Persistence.Configurations;

public class SubmissionCategoryScoreConfiguration : IEntityTypeConfiguration<SubmissionCategoryScore>
{
    public void Configure(EntityTypeBuilder<SubmissionCategoryScore> builder)
    {
        builder.ToTable("SubmissionCategoryScores");
        builder.HasKey(s => s.Id);
        builder.HasIndex(s => new { s.SubmissionId, s.CategoryId }).IsUnique();

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(s => s.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
